use std::{env, path::PathBuf, process, time::Duration};

use crate::{telemetry, text, transport::Endpoint};

pub struct Args {
    pub endpoint: Endpoint,
    pub endpoint_display: String,
    pub auth_key_file: PathBuf,
    pub arti_state_dir: PathBuf,
    pub arti_cache_dir: PathBuf,
    pub connect_timeout: Duration,
    pub handshake_timeout: Duration,
    pub heartbeat: Duration,
    pub retry_base: Duration,
    pub retry_max: Duration,
}

pub fn get() -> Args {
    let (endpoint, endpoint_display) = match crate::stub::load_endpoint() {
        Ok(value) => value,
        Err(error) => {
            eprintln!("endpoint stub error: {error}");
            process::exit(2);
        }
    };
    get_from(env::args().skip(1), endpoint, endpoint_display)
}

fn get_from<I: Iterator<Item = String>>(
    mut it: I,
    endpoint: Endpoint,
    endpoint_display: String,
) -> Args {
    let mut auth_key_file = env::var_os("VALHALLA_AUTH_KEY_FILE")
        .map(PathBuf::from)
        .unwrap_or_else(crate::auth::default_key_path);
    let mut arti_state_dir = env::var_os("VALHALLA_ARTI_STATE_DIR")
        .map(PathBuf::from)
        .unwrap_or_else(default_state_dir);
    let mut arti_cache_dir = env::var_os("VALHALLA_ARTI_CACHE_DIR")
        .map(PathBuf::from)
        .unwrap_or_else(default_cache_dir);
    let mut connect_timeout = Duration::from_secs(15);
    let mut handshake_timeout = Duration::from_secs(10);
    let mut heartbeat = Duration::from_secs(30);
    let mut retry_base = Duration::from_secs(1);
    let mut retry_max = Duration::from_secs(60);

    while let Some(arg) = it.next() {
        if arg.starts_with("--valhalla-update-") {
            continue;
        }
        match arg.as_str() {
            "--auth-key-file" => auth_key_file = PathBuf::from(value(&mut it)),
            "--arti-state-dir" => arti_state_dir = PathBuf::from(value(&mut it)),
            "--arti-cache-dir" => arti_cache_dir = PathBuf::from(value(&mut it)),
            "--connect-timeout" => connect_timeout = parse_duration_secs(&value(&mut it), "connect timeout"),
            "--handshake-timeout" => handshake_timeout = parse_duration_secs(&value(&mut it), "handshake timeout"),
            "--heartbeat" => heartbeat = parse_duration_secs(&value(&mut it), "heartbeat"),
            "--retry-base" => retry_base = parse_duration_secs(&value(&mut it), "retry base"),
            "--retry-max" => retry_max = parse_duration_secs(&value(&mut it), "retry max"),
            "--print-fingerprint" => {
                println!("{}", telemetry::fingerprint());
                process::exit(0);
            }
            text::HELP | text::SHORT_HELP => {
                println!("{}", text::USAGE);
                process::exit(0);
            }
            // Endpoint configuration is deliberately not accepted on the command line.
            // The runtime source of truth is always ./stub/stub.bin.
            "--endpoint" | "--ip" | "--port" => fail(),
            _ => fail(),
        }
    }

    if connect_timeout.is_zero()
        || handshake_timeout.is_zero()
        || heartbeat.is_zero()
        || retry_base.is_zero()
        || retry_max.is_zero()
        || retry_max < retry_base
    {
        fail();
    }

    Args {
        endpoint,
        endpoint_display,
        auth_key_file,
        arti_state_dir,
        arti_cache_dir,
        connect_timeout,
        handshake_timeout,
        heartbeat,
        retry_base,
        retry_max,
    }
}

fn default_state_dir() -> PathBuf {
    env::var_os("LOCALAPPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("."))
        .join("Valhalla")
        .join("arti-state")
}

fn default_cache_dir() -> PathBuf {
    env::var_os("LOCALAPPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("."))
        .join("Valhalla")
        .join("arti-cache")
}

fn parse_duration_secs(value: &str, label: &str) -> Duration {
    match value.parse::<u64>() {
        Ok(v) if v > 0 && v <= 86_400 => Duration::from_secs(v),
        _ => {
            eprintln!("invalid {label}");
            fail();
        }
    }
}

fn value<I: Iterator<Item = String>>(it: &mut I) -> String {
    match it.next() {
        Some(v) => v,
        None => fail(),
    }
}

fn fail() -> ! {
    eprintln!("{}", text::FAILED);
    process::exit(2)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn endpoint_is_supplied_outside_process_arguments() {
        let (endpoint, display) = crate::transport::Endpoint::parse("ws://127.0.0.1:4793/")
            .map(|e| (e, "ws://127.0.0.1:4793/".to_owned()))
            .expect("test endpoint should parse");
        let args = get_from(
            ["--valhalla-update-final-port=49152".to_owned(),
             "--valhalla-update-final-token=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa".to_owned()]
                .into_iter(),
            endpoint,
            display,
        );
        assert!(matches!(args.endpoint, Endpoint::Local { .. }));
        assert_eq!(args.endpoint_display, "ws://127.0.0.1:4793/");
    }
}
