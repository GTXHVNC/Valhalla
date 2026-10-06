use std::{
    fs::{self, File, OpenOptions},
    io::{self, BufRead, BufReader, Read, Write},
    net::{TcpListener, TcpStream},
    path::{Path, PathBuf},
    process::{Child, Command},
    time::{Duration, Instant},
};

#[cfg(windows)]
use std::os::windows::process::CommandExt;

use sha2::{Digest, Sha256};

use crate::{auth, telemetry, transport};

const MAX_UPDATE_BYTES: usize = 64 * 1024 * 1024;
const SUCCESSOR_ENV: &str = "EINHERJAR_UPDATE_SUCCESSOR";
const PROBE_ENV: &str = "EINHERJAR_UPDATE_PROBE";
const SOURCE_ENV: &str = "EINHERJAR_UPDATE_SOURCE";
const TARGET_ENV: &str = "EINHERJAR_UPDATE_TARGET";
const HASH_ENV: &str = "EINHERJAR_UPDATE_HASH";
const PARENT_ENV: &str = "EINHERJAR_UPDATE_PARENT";
const HANDOFF_PORT_ENV: &str = "EINHERJAR_UPDATE_HANDOFF_PORT";
const HANDOFF_TOKEN_ENV: &str = "EINHERJAR_UPDATE_HANDOFF_TOKEN";
const FINGERPRINT_ENV: &str = "EINHERJAR_UPDATE_FINGERPRINT";
const FINAL_PORT_ENV: &str = "EINHERJAR_UPDATE_FINAL_PORT";
const FINAL_TOKEN_ENV: &str = "EINHERJAR_UPDATE_FINAL_TOKEN";
const FINAL_HASH_ENV: &str = "EINHERJAR_UPDATE_FINAL_HASH";
const FINAL_FINGERPRINT_ENV: &str = "EINHERJAR_UPDATE_FINAL_FINGERPRINT";

const PROBE_WAIT: Duration = Duration::from_secs(45);
const CHILD_WAIT: Duration = Duration::from_secs(45);
#[cfg(windows)]
const PARENT_WAIT: Duration = Duration::from_secs(120);
const PROBE_CONNECT_TIMEOUT: Duration = Duration::from_secs(8);
const PROBE_READ_TIMEOUT: Duration = Duration::from_secs(15);
const HANDOFF_CONNECT_TIMEOUT: Duration = Duration::from_secs(3);
const HANDOFF_READ_TIMEOUT: Duration = Duration::from_secs(5);
const FINAL_READY_WAIT: Duration = Duration::from_secs(30);
const REPLACEMENT_RETRIES: usize = 40;
const REPLACEMENT_RETRY_DELAY: Duration = Duration::from_millis(250);

const PROBE_HELLO_PREFIX: &str = "HELLO:UPDATE-PROBE:";
const PROBE_ACK_PREFIX: &str = "ACK:UPDATE-PROBE:";
const PROBE_READY_PREFIX: &str = "UPDATE_PROBE_READY:";
const PROBE_READY_ACK_PREFIX: &str = "ACK:UPDATE_PROBE_READY:";
const PROBE_FAILED_PREFIX: &str = "UPDATE_PROBE_FAILED:";
const FINAL_READY_PREFIX: &str = "UPDATE_FINAL_READY:";
const FINAL_READY_ACK_PREFIX: &str = "ACK:UPDATE_FINAL_READY:";

pub(crate) fn max_update_bytes() -> usize {
    MAX_UPDATE_BYTES
}

pub(crate) fn sha256_hex(bytes: &[u8]) -> String {
    Sha256::digest(bytes)
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect()
}

pub(crate) fn validate_hash(expected: &str, actual: &str) -> bool {
    let expected = expected.trim();
    expected.len() == 64
        && expected.bytes().all(|byte| byte.is_ascii_hexdigit())
        && expected.eq_ignore_ascii_case(actual)
}

fn valid_fingerprint(value: &str) -> bool {
    value.len() == 64 && value.bytes().all(|byte| byte.is_ascii_hexdigit())
}

pub(crate) fn decode_base64(input: &str) -> Option<Vec<u8>> {
    let compact: Vec<u8> = input
        .bytes()
        .filter(|byte| !matches!(byte, b'\r' | b'\n' | b'\t' | b' '))
        .collect();

    if compact.is_empty()
        || compact.len() > MAX_UPDATE_BYTES.saturating_mul(4) / 3 + 4
        || compact.len() % 4 == 1
    {
        return None;
    }

    fn value(byte: u8) -> Option<u8> {
        match byte {
            b'A'..=b'Z' => Some(byte - b'A'),
            b'a'..=b'z' => Some(byte - b'a' + 26),
            b'0'..=b'9' => Some(byte - b'0' + 52),
            b'+' => Some(62),
            b'/' => Some(63),
            _ => None,
        }
    }

    let has_full_padding_boundary = compact.len() % 4 == 0;
    let mut output = Vec::with_capacity(compact.len() * 3 / 4);
    let mut index = 0;

    while index < compact.len() {
        let remaining = compact.len() - index;
        if remaining >= 4 {
            let a = value(compact[index])? as u32;
            let b = value(compact[index + 1])? as u32;
            let pad_second = compact[index + 2] == b'=';
            let pad_third = compact[index + 3] == b'=';
            let c = if pad_second {
                0
            } else {
                value(compact[index + 2])? as u32
            };
            let d = if pad_third {
                0
            } else {
                value(compact[index + 3])? as u32
            };

            if (pad_second && !pad_third)
                || (pad_second || pad_third) && index + 4 != compact.len()
            {
                return None;
            }
            if pad_second && (b & 0x0f) != 0 {
                return None;
            }
            if pad_third && !pad_second && (c & 0x03) != 0 {
                return None;
            }

            output.push(((a << 2) | (b >> 4)) as u8);
            if !pad_second {
                output.push(((b << 4) | (c >> 2)) as u8);
            }
            if !pad_third {
                output.push(((c << 6) | d) as u8);
            }
            index += 4;
        } else {
            if has_full_padding_boundary {
                return None;
            }

            let a = value(compact[index])? as u32;
            let b = value(compact[index + 1])? as u32;
            output.push(((a << 2) | (b >> 4)) as u8);

            if remaining == 3 {
                let c = value(compact[index + 2])? as u32;
                if (c & 0x03) != 0 {
                    return None;
                }
                output.push(((b << 4) | (c >> 2)) as u8);
            } else if (b & 0x0f) != 0 {
                return None;
            }
            index = compact.len();
        }
    }

    if output.is_empty() || output.len() > MAX_UPDATE_BYTES {
        None
    } else {
        Some(output)
    }
}

pub(crate) fn stage_bytes(bytes: &[u8], expected_hash: &str) -> io::Result<PathBuf> {
    if bytes.is_empty() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "update payload is empty",
        ));
    }
    if bytes.len() > MAX_UPDATE_BYTES {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "update payload is too large",
        ));
    }

    let actual_hash = sha256_hex(bytes);
    if !validate_hash(expected_hash, &actual_hash) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "update hash mismatch",
        ));
    }

    let staged_path = tempfile_path();
    let mut handle = OpenOptions::new()
        .create_new(true)
        .write(true)
        .open(&staged_path)?;

    if let Err(error) = handle.write_all(bytes).and_then(|_| handle.sync_all()) {
        let _ = fs::remove_file(&staged_path);
        return Err(error);
    }
    drop(handle);

    let staged_hash = sha256_file(&staged_path)?;
    if !validate_hash(expected_hash, &staged_hash) {
        let _ = fs::remove_file(&staged_path);
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "staged update failed integrity verification",
        ));
    }

    Ok(staged_path)
}

fn tempfile_path() -> PathBuf {
    let timestamp = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|duration| duration.as_nanos())
        .unwrap_or_default();
    std::env::temp_dir().join(format!(
        "einherjar-update-{}-{timestamp:x}.exe",
        std::process::id()
    ))
}

pub(crate) fn sha256_file(path: &Path) -> io::Result<String> {
    let mut file = File::open(path)?;
    let mut hash = Sha256::new();
    // Heap-backed buffer: the Windows executable's main thread has a finite stack.
    let mut buffer = vec![0u8; 64 * 1024];

    loop {
        let read = file.read(&mut buffer)?;
        if read == 0 {
            break;
        }
        hash.update(&buffer[..read]);
    }

    Ok(hash
        .finalize()
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect())
}

pub(crate) fn begin_update_file(expected_hash: &str, size: u64) -> io::Result<PathBuf> {
    if !validate_hash(expected_hash, expected_hash) {
        return Err(io::Error::new(io::ErrorKind::InvalidInput, "invalid update hash"));
    }
    if size == 0 || size > MAX_UPDATE_BYTES as u64 {
        return Err(io::Error::new(io::ErrorKind::InvalidInput, "invalid update size"));
    }
    let path = tempfile_path();
    OpenOptions::new().create_new(true).write(true).open(&path)?.sync_all()?;
    Ok(path)
}

pub(crate) fn append_update_file(path: &Path, offset: u64, bytes: &[u8]) -> io::Result<u64> {
    if bytes.is_empty() || bytes.len() > 128 * 1024 {
        return Err(io::Error::new(io::ErrorKind::InvalidInput, "invalid update chunk"));
    }
    let mut file = OpenOptions::new().write(true).open(path)?;
    use std::io::Seek;
    file.seek(io::SeekFrom::Start(offset))?;
    file.write_all(bytes)?;
    Ok(offset + bytes.len() as u64)
}

pub(crate) fn finalize_update_file(path: &Path, expected_hash: &str, expected_size: u64) -> io::Result<PathBuf> {
    let metadata = fs::metadata(path)?;
    if metadata.len() != expected_size || expected_size == 0 || expected_size > MAX_UPDATE_BYTES as u64 {
        let _ = fs::remove_file(path);
        return Err(io::Error::new(io::ErrorKind::InvalidData, "update size mismatch"));
    }
    let actual_hash = sha256_file(path)?;
    if !validate_hash(expected_hash, &actual_hash) {
        let _ = fs::remove_file(path);
        return Err(io::Error::new(io::ErrorKind::InvalidData, "update hash mismatch"));
    }
    #[cfg(windows)]
    {
        let mut file = File::open(path)?;
        let mut magic = [0u8; 2];
        file.read_exact(&mut magic)?;
        if &magic != b"MZ" {
            let _ = fs::remove_file(path);
            return Err(io::Error::new(io::ErrorKind::InvalidData, "update is not a Windows executable"));
        }
    }
    let file = OpenOptions::new().write(true).open(path)?;
    file.sync_all()?;
    Ok(path.to_owned())
}

#[derive(Debug)]
struct SuccessorArgs {
    source: PathBuf,
    target: PathBuf,
    hash: String,
    parent_pid: u32,
    fingerprint: String,
    handoff_port: u16,
    handoff_token: String,
}

fn parse_u16(value: &str, label: &str) -> Result<u16, String> {
    let parsed = value
        .parse::<u16>()
        .map_err(|_| format!("invalid {label}"))?;
    if parsed == 0 {
        return Err(format!("invalid {label}"));
    }
    Ok(parsed)
}

fn env_value(name: &str) -> Option<String> {
    std::env::var(name).ok().filter(|value| !value.trim().is_empty())
}

fn successor_args() -> Result<Option<SuccessorArgs>, String> {
    if !std::env::var(SUCCESSOR_ENV).is_ok_and(|value| value == "1") {
        return Ok(None);
    }

    let hash = env_value(HASH_ENV).ok_or("missing update hash")?;
    let fingerprint = env_value(FINGERPRINT_ENV).ok_or("missing update fingerprint")?;
    let handoff_token = env_value(HANDOFF_TOKEN_ENV).ok_or("missing update handoff token")?;
    let parent_pid = env_value(PARENT_ENV)
        .ok_or("missing update parent pid")?
        .parse::<u32>()
        .map_err(|_| "invalid update parent pid".to_owned())?;
    let handoff_port = env_value(HANDOFF_PORT_ENV)
        .ok_or("missing update handoff port")?
        .parse::<u16>()
        .map_err(|_| "invalid update handoff port".to_owned())?;
    if handoff_port == 0 {
        return Err("invalid update handoff port".into());
    }

    if !validate_hash(&hash, &hash) {
        return Err("invalid update hash".into());
    }
    if !valid_fingerprint(&fingerprint) {
        return Err("invalid update fingerprint".into());
    }
    if handoff_token.len() != 64 || !handoff_token.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Err("invalid update handoff token".into());
    }

    Ok(Some(SuccessorArgs {
        source: PathBuf::from(env_value(SOURCE_ENV).ok_or("missing update source")?),
        target: PathBuf::from(env_value(TARGET_ENV).ok_or("missing update target")?),
        hash,
        parent_pid,
        fingerprint,
        handoff_port,
        handoff_token,
    }))
}

pub(crate) struct UpdateHandoff {
    listener: TcpListener,
    token: String,
    hash: String,
    fingerprint: String,
}

impl UpdateHandoff {
    fn new(hash: String, fingerprint: String) -> io::Result<Self> {
        let listener = TcpListener::bind(("127.0.0.1", 0))?;
        listener.set_nonblocking(true)?;
        Ok(Self {
            listener,
            token: random_token()?,
            hash,
            fingerprint,
        })
    }

    fn port(&self) -> io::Result<u16> {
        match self.listener.local_addr()? {
            std::net::SocketAddr::V4(address) => Ok(address.port()),
            _ => Err(io::Error::new(
                io::ErrorKind::AddrNotAvailable,
                "IPv4 handoff listener required",
            )),
        }
    }

    pub(crate) fn wait_admission(self) -> io::Result<()> {
        let deadline = Instant::now() + PROBE_WAIT;

        while Instant::now() < deadline {
            match self.listener.accept() {
                Ok((mut stream, _)) => {
                    stream.set_read_timeout(Some(PROBE_READ_TIMEOUT))?;
                    let cloned = stream.try_clone()?;
                    let mut reader = BufReader::new(cloned);
                    let mut line = String::new();
                    if reader.read_line(&mut line)? == 0 {
                        continue;
                    }

                    let message = line.trim_end_matches(&['\r', '\n'][..]);
                    if let Some(rest) = message.strip_prefix(PROBE_READY_PREFIX) {
                        let parts: Vec<&str> = rest.split(':').collect();
                        if parts.len() == 3
                            && parts[0] == self.token
                            && validate_hash(&self.hash, parts[1])
                            && parts[2].eq_ignore_ascii_case(&self.fingerprint)
                        {
                            send_line(
                                &mut stream,
                                &format!("{PROBE_READY_ACK_PREFIX}{}", self.token),
                            )?;
                            return Ok(());
                        }
                    } else if let Some(rest) = message.strip_prefix(PROBE_FAILED_PREFIX) {
                        let parts: Vec<&str> = rest.splitn(2, ':').collect();
                        if parts.len() == 2 && parts[0] == self.token {
                            return Err(io::Error::new(
                                io::ErrorKind::PermissionDenied,
                                parts[1].to_owned(),
                            ));
                        }
                    }
                }
                Err(error) if error.kind() == io::ErrorKind::WouldBlock => {
                    std::thread::sleep(Duration::from_millis(50));
                }
                Err(error) => return Err(error),
            }
        }

        Err(io::Error::new(
            io::ErrorKind::TimedOut,
            "updated agent admission timed out",
        ))
    }
}

fn random_token() -> io::Result<String> {
    let mut bytes = [0u8; 32];
    getrandom::getrandom(&mut bytes)
        .map_err(|error| io::Error::new(io::ErrorKind::Other, error.to_string()))?;
    Ok(bytes
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect())
}

#[cfg(windows)]
fn wait_for_process_exit(pid: u32) -> io::Result<()> {
    type Handle = *mut core::ffi::c_void;
    const SYNCHRONIZE: u32 = 0x0010_0000;
    const WAIT_OBJECT_0: u32 = 0x0000_0000;
    const WAIT_TIMEOUT: u32 = 0x0000_0102;

    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn OpenProcess(desired_access: u32, inherit_handle: i32, process_id: u32) -> Handle;
        fn WaitForSingleObject(handle: Handle, milliseconds: u32) -> u32;
        fn CloseHandle(handle: Handle) -> i32;
    }

    let handle = unsafe { OpenProcess(SYNCHRONIZE, 0, pid) };
    if handle.is_null() {
        let error = io::Error::last_os_error();
        // Invalid PID means the process has already exited. Other failures must be surfaced.
        return if error.raw_os_error() == Some(87) {
            Ok(())
        } else {
            Err(error)
        };
    }

    let result = unsafe { WaitForSingleObject(handle, PARENT_WAIT.as_millis() as u32) };
    unsafe {
        CloseHandle(handle);
    }

    match result {
        WAIT_OBJECT_0 => Ok(()),
        WAIT_TIMEOUT => Err(io::Error::new(
            io::ErrorKind::TimedOut,
            "update parent did not exit before timeout",
        )),
        _ => Err(io::Error::last_os_error()),
    }
}

#[cfg(not(windows))]
fn wait_for_process_exit(_pid: u32) -> io::Result<()> {
    Ok(())
}

#[cfg(windows)]
fn wide(value: &std::ffi::OsStr) -> Vec<u16> {
    use std::os::windows::ffi::OsStrExt;
    value.encode_wide().chain(std::iter::once(0)).collect()
}

#[cfg(windows)]
fn replace_file(source: &Path, target: &Path) -> io::Result<()> {
    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn MoveFileExW(existing_name: *const u16, new_name: *const u16, flags: u32) -> i32;
    }

    const MOVEFILE_REPLACE_EXISTING: u32 = 0x0000_0001;
    const MOVEFILE_WRITE_THROUGH: u32 = 0x0000_0008;

    let source_wide = wide(source.as_os_str());
    let target_wide = wide(target.as_os_str());
    let ok = unsafe {
        MoveFileExW(
            source_wide.as_ptr(),
            target_wide.as_ptr(),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
        )
    };

    if ok == 0 {
        Err(io::Error::last_os_error())
    } else {
        Ok(())
    }
}

#[cfg(not(windows))]
fn replace_file(source: &Path, target: &Path) -> io::Result<()> {
    fs::rename(source, target)
}

#[cfg(windows)]
fn launch_updated(target: &Path) -> io::Result<Child> {
    use std::os::windows::process::CommandExt;

    Command::new(target)
        .creation_flags(0x0800_0000)
        .spawn()
}

#[cfg(not(windows))]
fn launch_updated(target: &Path) -> io::Result<Child> {
    Command::new(target).spawn()
}

fn rollback_path(target: &Path) -> PathBuf {
    target.with_extension("exe.einherjar-backup")
}

fn helper_path() -> PathBuf {
    let timestamp = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|duration| duration.as_nanos())
        .unwrap_or_default();
    std::env::temp_dir().join(format!(
        "einherjar-update-helper-{}-{timestamp:x}.exe",
        std::process::id()
    ))
}

fn send_line(stream: &mut TcpStream, line: &str) -> io::Result<()> {
    stream.write_all(line.as_bytes())?;
    stream.write_all(b"\n")?;
    stream.flush()
}

fn notify_handoff_failure(port: u16, token: &str, reason: &str) {
    let address = match format!("127.0.0.1:{port}").parse() {
        Ok(address) => address,
        Err(_) => return,
    };

    if let Ok(mut stream) = TcpStream::connect_timeout(&address, HANDOFF_CONNECT_TIMEOUT) {
        let sanitized = reason.replace(&['\r', '\n', ':'][..], " ");
        let _ = send_line(
            &mut stream,
            &format!("{PROBE_FAILED_PREFIX}{token}:{sanitized}"),
        );
    }
}


pub(crate) struct FinalReadyArgs {
    pub(crate) port: u16,
    pub(crate) token: String,
    pub(crate) hash: String,
    pub(crate) fingerprint: String,
}

pub(crate) fn final_ready_args() -> Result<Option<FinalReadyArgs>, String> {
    let Some(port) = env_value(FINAL_PORT_ENV) else { return Ok(None); };
    let token = env_value(FINAL_TOKEN_ENV).ok_or("missing update final token")?;
    let hash = env_value(FINAL_HASH_ENV).ok_or("missing update final hash")?;
    let fingerprint = env_value(FINAL_FINGERPRINT_ENV).ok_or("missing update final fingerprint")?;
    let port = parse_u16(&port, "update final port")?;
    if token.len() != 64 || !token.bytes().all(|b| b.is_ascii_hexdigit()) {
        return Err("invalid update final token".into());
    }
    if !validate_hash(&hash, &hash) {
        return Err("invalid update final hash".into());
    }
    if !valid_fingerprint(&fingerprint) {
        return Err("invalid update final fingerprint".into());
    }
    Ok(Some(FinalReadyArgs { port, token, hash, fingerprint }))
}
fn probe_entry() -> Result<(), String> {
    let source = std::env::current_exe().map_err(|error| error.to_string())?;
    let handoff_port = env_value(HANDOFF_PORT_ENV)
        .ok_or("missing probe handoff port")?
        .parse::<u16>()
        .map_err(|_| "invalid probe handoff port".to_owned())?;
    let handoff_token = env_value(HANDOFF_TOKEN_ENV).ok_or("missing probe handoff token")?;
    let expected_hash = env_value(HASH_ENV).ok_or("missing probe update hash")?;
    let expected_fingerprint = env_value(FINGERPRINT_ENV).ok_or("missing probe fingerprint")?;

    if handoff_port == 0 {
        return Err("invalid probe handoff port".into());
    }
    if handoff_token.len() != 64 || !handoff_token.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Err("invalid probe handoff token".into());
    }
    if !validate_hash(&expected_hash, &expected_hash) {
        return Err("invalid probe update hash".into());
    }
    if !valid_fingerprint(&expected_fingerprint) {
        return Err("invalid probe fingerprint".into());
    }

    run_probe(
        &source,
        handoff_port,
        &handoff_token,
        &expected_hash,
        &expected_fingerprint,
    ).map_err(|error| error.to_string())
}

pub(crate) fn notify_final_ready(final_args: &FinalReadyArgs) -> io::Result<()> {
    let address = format!("127.0.0.1:{}", final_args.port)
        .parse()
        .map_err(|_| io::Error::new(io::ErrorKind::InvalidInput, "invalid final update address"))?;
    let actual_hash = std::env::current_exe().and_then(|path| sha256_file(&path))?;
    if !validate_hash(&final_args.hash, &actual_hash) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "final executable hash mismatch",
        ));
    }
    let fingerprint = telemetry::fingerprint();
    if !fingerprint.eq_ignore_ascii_case(&final_args.fingerprint) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "final executable fingerprint mismatch",
        ));
    }

    let message = format!(
        "{FINAL_READY_PREFIX}{}:{}:{}",
        final_args.token, actual_hash, fingerprint
    );
    let expected = format!("{FINAL_READY_ACK_PREFIX}{}", final_args.token);
    let mut last_error = None;

    for attempt in 0..12 {
        match TcpStream::connect_timeout(&address, HANDOFF_CONNECT_TIMEOUT) {
            Ok(mut stream) => {
                if let Err(error) = stream.set_read_timeout(Some(HANDOFF_READ_TIMEOUT)) {
                    last_error = Some(error);
                } else if let Err(error) = send_line(&mut stream, &message) {
                    last_error = Some(error);
                } else {
                    let cloned = match stream.try_clone() {
                        Ok(cloned) => cloned,
                        Err(error) => {
                            last_error = Some(error);
                            continue;
                        }
                    };
                    let mut reader = BufReader::new(cloned);
                    let mut response = String::new();
                    match reader.read_line(&mut response) {
                        Ok(_) if response.trim_end_matches(&['\r', '\n'][..]) == expected => {
                            return Ok(());
                        }
                        Ok(_) => {
                            last_error = Some(io::Error::new(
                                io::ErrorKind::PermissionDenied,
                                "update final readiness was not acknowledged",
                            ));
                        }
                        Err(error) => last_error = Some(error),
                    }
                }
            }
            Err(error) => last_error = Some(error),
        }

        if attempt + 1 < 12 {
            std::thread::sleep(Duration::from_millis(250));
        }
    }

    Err(last_error.unwrap_or_else(|| {
        io::Error::new(
            io::ErrorKind::TimedOut,
            "update final readiness notification failed",
        )
    }))
}

fn spawn_probe(successor: &SuccessorArgs) -> io::Result<Child> {
    #[cfg(windows)]
    use std::os::windows::process::CommandExt;

    let mut command = Command::new(&successor.source);
    command
        .env_remove(SUCCESSOR_ENV)
        .env_remove(FINAL_PORT_ENV)
        .env_remove(FINAL_TOKEN_ENV)
        .env_remove(FINAL_HASH_ENV)
        .env_remove(FINAL_FINGERPRINT_ENV)
        .env(PROBE_ENV, "1")
        .env(HANDOFF_PORT_ENV, successor.handoff_port.to_string())
        .env(HANDOFF_TOKEN_ENV, &successor.handoff_token)
        .env(HASH_ENV, &successor.hash)
        .env(FINGERPRINT_ENV, &successor.fingerprint);

    #[cfg(windows)]
    command.creation_flags(0x0800_0000);

    command.spawn()
}


fn restart_original_after_failure(
    target: &Path,
    source: &Path,
    target_tmp: &Path,
    backup: &Path,
    original_error: io::Error,
) -> io::Result<()> {
    let mut restore_error = None;
    if !target.exists() && backup.exists() {
        for attempt in 0..REPLACEMENT_RETRIES {
            match replace_file(backup, target) {
                Ok(()) => {
                    restore_error = None;
                    break;
                }
                Err(error) => {
                    restore_error = Some(error);
                    if attempt + 1 < REPLACEMENT_RETRIES {
                        std::thread::sleep(REPLACEMENT_RETRY_DELAY);
                    }
                }
            }
        }
    }

    let restart_error = launch_updated(target).map(|_| ()).err();

    let _ = fs::remove_file(source);
    let _ = fs::remove_file(target_tmp);
    if backup.exists() && target.exists() {
        let _ = fs::remove_file(backup);
    }

    match (restore_error, restart_error) {
        (Some(restore), Some(restart)) => Err(io::Error::new(
            io::ErrorKind::Other,
            format!(
                "update failed: {original_error}; original target restore failed: {restore}; original-agent restart failed: {restart}"
            ),
        )),
        (Some(restore), None) => Err(io::Error::new(
            io::ErrorKind::Other,
            format!("update failed: {original_error}; original target restore failed: {restore}"),
        )),
        (None, Some(restart)) => Err(io::Error::new(
            io::ErrorKind::Other,
            format!("update failed: {original_error}; original-agent restart failed: {restart}"),
        )),
        (None, None) => Err(original_error),
    }
}

fn replace_and_launch(successor: &SuccessorArgs) -> io::Result<()> {
    let parent = match successor.target.parent() {
        Some(parent) => parent,
        None => {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "update target has no parent",
            ));
        }
    };

    let target_tmp = parent.join(format!(
        ".einherjar-update-{}.new.exe",
        std::process::id()
    ));
    let backup = rollback_path(&successor.target);

    let staged_hash = match sha256_file(&successor.source) {
        Ok(hash) => hash,
        Err(error) => {
            return restart_original_after_failure(
                &successor.target,
                &successor.source,
                &target_tmp,
                &backup,
                io::Error::new(
                    error.kind(),
                    format!("successor source verification failed: {error}"),
                ),
            );
        }
    };
    if !validate_hash(&successor.hash, &staged_hash) {
        return restart_original_after_failure(
            &successor.target,
            &successor.source,
            &target_tmp,
            &backup,
            io::Error::new(
                io::ErrorKind::InvalidData,
                "successor source failed integrity verification",
            ),
        );
    }

    if let Err(error) = fs::create_dir_all(parent) {
        return restart_original_after_failure(
            &successor.target,
            &successor.source,
            &target_tmp,
            &backup,
            error,
        );
    }

    let _ = fs::remove_file(&target_tmp);
    if backup.exists() {
        let _ = fs::remove_file(&backup);
    }

    if let Err(error) = fs::copy(&successor.source, &target_tmp) {
        return restart_original_after_failure(
            &successor.target,
            &successor.source,
            &target_tmp,
            &backup,
            error,
        );
    }

    let target_tmp_hash = match sha256_file(&target_tmp) {
        Ok(hash) => hash,
        Err(error) => {
            return restart_original_after_failure(
                &successor.target,
                &successor.source,
                &target_tmp,
                &backup,
                error,
            );
        }
    };
    if !validate_hash(&successor.hash, &target_tmp_hash) {
        return restart_original_after_failure(
            &successor.target,
            &successor.source,
            &target_tmp,
            &backup,
            io::Error::new(
                io::ErrorKind::InvalidData,
                "replacement copy failed integrity verification",
            ),
        );
    }

    if successor.target.exists() {
        if let Err(error) = fs::copy(&successor.target, &backup) {
            return restart_original_after_failure(
                &successor.target,
                &successor.source,
                &target_tmp,
                &backup,
                error,
            );
        }
    }

    let mut replacement_error = None;
    for attempt in 0..REPLACEMENT_RETRIES {
        match replace_file(&target_tmp, &successor.target) {
            Ok(()) => {
                replacement_error = None;
                break;
            }
            Err(error) => {
                replacement_error = Some(error);
                if attempt + 1 < REPLACEMENT_RETRIES {
                    std::thread::sleep(REPLACEMENT_RETRY_DELAY);
                }
            }
        }
    }

    if let Some(error) = replacement_error {
        return restart_original_after_failure(
            &successor.target,
            &successor.source,
            &target_tmp,
            &backup,
            error,
        );
    }

    // The old agent is already gone, so the helper is now solely responsible for
    // proving that the installed image actually started in normal mode and reached
    // the server. The final agent gets a fresh local listener and a second, stronger
    // readiness token; failure causes rollback and restoration of the old image.
    let final_listener = match TcpListener::bind(("127.0.0.1", 0)) {
        Ok(listener) => listener,
        Err(error) => {
            return restart_after_final_failure(successor, &target_tmp, &backup, error);
        }
    };
    if let Err(error) = final_listener.set_nonblocking(true) {
        return restart_after_final_failure(successor, &target_tmp, &backup, error);
    }
    let final_port = match final_listener.local_addr() {
        Ok(std::net::SocketAddr::V4(address)) => address.port(),
        Ok(_) => {
            return restart_after_final_failure(
                successor,
                &target_tmp,
                &backup,
                io::Error::new(io::ErrorKind::AddrNotAvailable, "IPv4 final listener required"),
            );
        }
        Err(error) => {
            return restart_after_final_failure(successor, &target_tmp, &backup, error);
        }
    };

    let mut final_command = Command::new(&successor.target);
    final_command
        .env_remove(SUCCESSOR_ENV)
        .env_remove(PROBE_ENV)
        .env_remove(SOURCE_ENV)
        .env_remove(TARGET_ENV)
        .env_remove(HASH_ENV)
        .env_remove(PARENT_ENV)
        .env_remove(HANDOFF_PORT_ENV)
        .env_remove(HANDOFF_TOKEN_ENV)
        .env_remove(FINGERPRINT_ENV)
        .env(FINAL_PORT_ENV, final_port.to_string())
        .env(FINAL_TOKEN_ENV, &successor.handoff_token)
        .env(FINAL_HASH_ENV, &successor.hash)
        .env(FINAL_FINGERPRINT_ENV, &successor.fingerprint);
    #[cfg(windows)]
    final_command.creation_flags(0x0800_0000);

    let mut final_child = match final_command.spawn() {
        Ok(child) => child,
        Err(error) => {
            return restart_after_final_failure(successor, &target_tmp, &backup, error);
        }
    };

    let final_deadline = Instant::now() + FINAL_READY_WAIT;
    let mut final_confirmed = false;
    let mut final_error = None;

    while Instant::now() < final_deadline {
        match final_child.try_wait() {
            Ok(Some(status)) => {
                final_error = Some(io::Error::new(
                    io::ErrorKind::Other,
                    format!("updated agent exited before final readiness: {status}"),
                ));
                break;
            }
            Ok(None) => {}
            Err(error) => {
                final_error = Some(error);
                break;
            }
        }

        match final_listener.accept() {
            Ok((mut stream, _)) => {
                if let Err(error) = stream.set_read_timeout(Some(HANDOFF_READ_TIMEOUT)) {
                    final_error = Some(error);
                    break;
                }
                let cloned = match stream.try_clone() {
                    Ok(cloned) => cloned,
                    Err(error) => {
                        final_error = Some(error);
                        break;
                    }
                };
                let mut reader = BufReader::new(cloned);
                let mut line = String::new();
                match reader.read_line(&mut line) {
                    Ok(bytes) if bytes > 0 => {
                        let message = line.trim_end_matches(&['\r', '\n'][..]);
                        if let Some(rest) = message.strip_prefix(FINAL_READY_PREFIX) {
                            let parts: Vec<&str> = rest.split(':').collect();
                            if parts.len() == 3
                                && parts[0] == successor.handoff_token
                                && validate_hash(&successor.hash, parts[1])
                                && parts[2].eq_ignore_ascii_case(&successor.fingerprint)
                            {
                                if let Err(error) = send_line(
                                    &mut stream,
                                    &format!("{FINAL_READY_ACK_PREFIX}{}", successor.handoff_token),
                                ) {
                                    final_error = Some(error);
                                    break;
                                }
                                final_confirmed = true;
                                break;
                            }
                        }
                    }
                    Ok(_) => {}
                    Err(error) => {
                        final_error = Some(error);
                        break;
                    }
                }
            }
            Err(error) if error.kind() == io::ErrorKind::WouldBlock => {
                std::thread::sleep(Duration::from_millis(50));
            }
            Err(error) => {
                final_error = Some(error);
                break;
            }
        }
    }

    if final_confirmed {
        let _ = fs::remove_file(&backup);
        let _ = fs::remove_file(&successor.source);
        request_helper_self_delete();
        Ok(())
    } else {
        let error = final_error.unwrap_or_else(|| {
            io::Error::new(io::ErrorKind::TimedOut, "updated agent final readiness timed out")
        });
        let _ = final_child.kill();
        let _ = final_child.wait();
        restart_after_final_failure(successor, &target_tmp, &backup, error)
    }
}

fn restart_after_final_failure(
    successor: &SuccessorArgs,
    target_tmp: &Path,
    backup: &Path,
    original_error: io::Error,
) -> io::Result<()> {
    let _ = fs::remove_file(target_tmp);
    let _ = fs::remove_file(&successor.source);

    let mut restored = false;
    if backup.exists() {
        for attempt in 0..REPLACEMENT_RETRIES {
            match replace_file(backup, &successor.target) {
                Ok(()) => {
                    restored = true;
                    break;
                }
                Err(_) if attempt + 1 < REPLACEMENT_RETRIES => {
                    std::thread::sleep(REPLACEMENT_RETRY_DELAY);
                }
                Err(_) => break,
            }
        }
    }

    let restart_error = if restored {
        launch_updated(&successor.target)
            .map(|_| ())
            .err()
    } else {
        None
    };

    request_helper_self_delete();

    match (restored, restart_error) {
        (true, None) => Err(original_error),
        (true, Some(restart)) => Err(io::Error::new(
            io::ErrorKind::Other,
            format!("{original_error}; original-agent restart failed: {restart}"),
        )),
        (false, _) => Err(io::Error::new(
            io::ErrorKind::Other,
            format!("{original_error}; original target restore failed"),
        )),
    }
}

#[cfg(windows)]
fn request_helper_self_delete() {
    let Ok(exe) = std::env::current_exe() else { return; };
    use std::os::windows::process::CommandExt;
    let command = format!(
        "ping 127.0.0.1 -n 2 > nul & del /f /q \"{}\"",
        exe.display()
    );
    let _ = Command::new("cmd.exe")
        .args(["/D", "/C", &command])
        .creation_flags(0x0800_0000)
        .spawn();
}

#[cfg(not(windows))]
fn request_helper_self_delete() {}

fn run_successor(successor: SuccessorArgs) -> io::Result<()> {
    let mut probe_child = match spawn_probe(&successor) {
        Ok(child) => child,
        Err(error) => {
            notify_handoff_failure(
                successor.handoff_port,
                &successor.handoff_token,
                &format!("could not start candidate: {error}"),
            );
            let _ = fs::remove_file(&successor.source);
            return Err(error);
        }
    };

    let probe_deadline = Instant::now() + CHILD_WAIT;
    loop {
        match probe_child.try_wait()? {
            Some(status) if status.success() => break,
            Some(status) => {
                let error = io::Error::new(
                    io::ErrorKind::PermissionDenied,
                    format!("updated agent probe failed: {status}"),
                );
                notify_handoff_failure(
                    successor.handoff_port,
                    &successor.handoff_token,
                    &error.to_string(),
                );
                let _ = fs::remove_file(&successor.source);
                return Err(error);
            }
            None if Instant::now() >= probe_deadline => {
                let _ = probe_child.kill();
                let _ = probe_child.wait();
                notify_handoff_failure(
                    successor.handoff_port,
                    &successor.handoff_token,
                    "updated agent probe timed out",
                );
                let _ = fs::remove_file(&successor.source);
                return Err(io::Error::new(
                    io::ErrorKind::TimedOut,
                    "updated agent probe timed out",
                ));
            }
            None => std::thread::sleep(Duration::from_millis(100)),
        }
    }

    match sha256_file(&successor.source) {
        Ok(staged_hash) if validate_hash(&successor.hash, &staged_hash) => {}
        Ok(_) => {
            let error = io::Error::new(
                io::ErrorKind::InvalidData,
                "staged update changed before parent shutdown",
            );
            notify_handoff_failure(
                successor.handoff_port,
                &successor.handoff_token,
                &error.to_string(),
            );
            let _ = fs::remove_file(&successor.source);
            return Err(error);
        }
        Err(error) => {
            notify_handoff_failure(
                successor.handoff_port,
                &successor.handoff_token,
                &format!("could not verify staged update before parent shutdown: {error}"),
            );
            let _ = fs::remove_file(&successor.source);
            return Err(error);
        }
    }

    if let Err(error) = wait_for_process_exit(successor.parent_pid) {
        let _ = fs::remove_file(&successor.source);
        return Err(error);
    }

    replace_and_launch(&successor)
}

pub(crate) fn spawn_successor(
    staged: PathBuf,
    target: PathBuf,
    hash: String,
    parent_pid: u32,
    fingerprint: &str,
) -> io::Result<UpdateHandoff> {
    #[cfg(windows)]
    use std::os::windows::process::CommandExt;

    let helper = helper_path();
    if let Err(error) = fs::copy(std::env::current_exe()?, &helper) {
        let _ = fs::remove_file(&helper);
        return Err(error);
    }

    let handoff = match UpdateHandoff::new(hash.clone(), fingerprint.to_owned()) {
        Ok(handoff) => handoff,
        Err(error) => {
            let _ = fs::remove_file(&helper);
            return Err(error);
        }
    };
    let handoff_port = handoff.port()?;

    let mut command = Command::new(&helper);
    command
        .env(SUCCESSOR_ENV, "1")
        .env(SOURCE_ENV, &staged)
        .env(TARGET_ENV, target)
        .env(HASH_ENV, &hash)
        .env(PARENT_ENV, parent_pid.to_string())
        .env(FINGERPRINT_ENV, &fingerprint)
        .env(HANDOFF_PORT_ENV, handoff_port.to_string())
        .env(HANDOFF_TOKEN_ENV, &handoff.token);

    #[cfg(windows)]
    command.creation_flags(0x0800_0000);

    if let Err(error) = command.spawn() {
        let _ = fs::remove_file(&helper);
        let _ = fs::remove_file(&staged);
        return Err(error);
    }

    Ok(handoff)
}

pub(crate) fn maybe_run_probe() -> bool {
    if std::env::var(PROBE_ENV).ok().as_deref() == Some("1") {
        match probe_entry() {
            Ok(()) => std::process::exit(0),
            Err(_) => std::process::exit(1),
        }
    }
    false
}

pub(crate) fn maybe_run_successor() -> bool {
    match successor_args() {
        Ok(None) => false,
        Ok(Some(successor)) => {
            if run_successor(successor).is_err() {
                std::process::exit(1);
            }
            std::process::exit(0);
        }
        Err(_) => std::process::exit(2),
    }
}

fn run_probe(
    source: &Path,
    handoff_port: u16,
    handoff_token: &str,
    expected_hash: &str,
    expected_fingerprint: &str,
) -> io::Result<()>
{
    let actual_hash = sha256_file(source)?;
    if !validate_hash(expected_hash, &actual_hash) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "probe executable hash mismatch",
        ));
    }

    let actual_fingerprint = telemetry::fingerprint();
    if !actual_fingerprint.eq_ignore_ascii_case(expected_fingerprint) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "probe fingerprint mismatch",
        ));
    }

    let (endpoint, endpoint_display, _install_dir, _folder_name, agent_token) = crate::stub::load_config()
        .map_err(|error| io::Error::new(io::ErrorKind::InvalidInput, error))?;
    let signing_key = auth::signing_key();
    let base = std::env::var_os("LOCALAPPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("."));
    let state_dir = base.join("Einherjar").join("arti-state");
    let cache_dir = base.join("Einherjar").join("arti-cache");
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .map_err(|e| io::Error::new(io::ErrorKind::Other, format!("probe runtime: {e}")))?;
    runtime.block_on(async {
        let mut connector = transport::Connector::new(endpoint, state_dir, cache_dir, agent_token);
        let mut session = connector
            .connect_authenticated(
                &actual_fingerprint,
                &signing_key,
                PROBE_CONNECT_TIMEOUT,
                PROBE_READ_TIMEOUT,
            )
            .await?;
        let telemetry_data = telemetry::record(&endpoint_display, None, &actual_fingerprint);
        session
            .send_text(&format!("{}{}", crate::text::DATA, telemetry_data))
            .await?;
        let response = session.next_text().await?.ok_or_else(|| {
            io::Error::new(
                io::ErrorKind::UnexpectedEof,
                "server closed probe connection before acknowledgement",
            )
        })?;
        if response != "ACK:DATA" {
            return Err(io::Error::new(
                io::ErrorKind::PermissionDenied,
                "server rejected probe telemetry",
            ));
        }
        Ok::<(), io::Error>(())
    })?;

    let address = format!("127.0.0.1:{handoff_port}")
        .parse()
        .map_err(|_| io::Error::new(io::ErrorKind::InvalidInput, "invalid handoff address"))?;
    let mut handoff = TcpStream::connect_timeout(&address, HANDOFF_CONNECT_TIMEOUT)?;
    handoff.set_read_timeout(Some(HANDOFF_READ_TIMEOUT))?;
    send_line(
        &mut handoff,
        &format!("{PROBE_READY_PREFIX}{handoff_token}:{actual_hash}:{actual_fingerprint}"),
    )?;

    let cloned = handoff.try_clone()?;
    let mut response_reader = BufReader::new(cloned);
    let mut response = String::new();
    response_reader.read_line(&mut response)?;
    if response.trim_end_matches(&['\r', '\n'][..])
        != format!("{PROBE_READY_ACK_PREFIX}{handoff_token}")
    {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "old agent did not acknowledge probe readiness",
        ));
    }

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sha256_and_hash_validation() {
        let value = sha256_hex(b"einherjar-update");
        assert_eq!(value.len(), 64);
        assert!(validate_hash(&value, &value.to_uppercase()));
        assert!(!validate_hash(&"0".repeat(64), &value));
        assert!(!validate_hash("no", &value));
    }

    #[test]
    fn base64_strict_decode() {
        assert_eq!(decode_base64("Wg=="), Some(vec![b'Z']));
        assert_eq!(decode_base64("Wg"), Some(vec![b'Z']));
        assert_eq!(decode_base64("Wg="), None);
        assert_eq!(decode_base64("Zh=="), None);
    }

    #[test]
    fn heap_hash_buffer() {
        let source = include_str!("update.rs");
        let start = source.find("fn sha256_file(").expect("sha256_file missing");
        let end = source[start..]
            .find("\n#[derive(Debug)")
            .map(|offset| start + offset)
            .unwrap_or(source.len());
        let hash_fn = &source[start..end];
        assert!(hash_fn.contains("let mut buffer = vec![0u8; 64 * 1024]"));
        assert!(!hash_fn.contains("let mut buf = [0u8; 1024 * 1024]"));
    }

    #[test]
    fn final_ready_protocol_uses_environment_contract() {
        assert!(FINAL_PORT_ENV.starts_with("EINHERJAR_UPDATE_"));
        assert!(FINAL_TOKEN_ENV.starts_with("EINHERJAR_UPDATE_"));
        assert!(FINAL_HASH_ENV.starts_with("EINHERJAR_UPDATE_"));
        assert!(FINAL_FINGERPRINT_ENV.starts_with("EINHERJAR_UPDATE_"));
    }

}
