use std::{fs, io, path::PathBuf};

use crate::transport::Endpoint;

const STUB_RELATIVE_PATH: &str = "stub/stub.bin";

pub(crate) fn path() -> PathBuf {
    std::env::current_dir()
        .unwrap_or_else(|_| PathBuf::from("."))
        .join(STUB_RELATIVE_PATH)
}

pub(crate) fn load_endpoint() -> Result<(Endpoint, String), String> {
    let path = path();
    let bytes = fs::read(&path)
        .map_err(|error| format!("unable to read {}: {error}", path.display()))?;
    parse_endpoint_bytes(&bytes, &path)
}

fn parse_endpoint_bytes(bytes: &[u8], path: &std::path::Path) -> Result<(Endpoint, String), String> {
    if bytes.is_empty() {
        return Err(format!("{} is empty", path.display()));
    }

    let text = String::from_utf8(bytes.to_vec())
        .map_err(|_| format!("{} is not valid UTF-8", path.display()))?;
    let endpoint_text = text
        .trim_start_matches('\u{feff}')
        .lines()
        .next()
        .unwrap_or("")
        .trim();
    if endpoint_text.is_empty() {
        return Err(format!("{} does not contain an endpoint", path.display()));
    }
    if text.lines().skip(1).any(|line| !line.trim().is_empty()) {
        return Err(format!("{} contains unexpected data after the endpoint", path.display()));
    }

    let endpoint = Endpoint::parse(endpoint_text)
        .map_err(|error| format!("invalid endpoint in {}: {error}", path.display()))?;
    Ok((endpoint, endpoint_text.to_owned()))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn stub_path_is_relative_to_current_directory() {
        assert!(path().ends_with(PathBuf::from("stub").join("stub.bin")));
    }

    #[test]
    fn valid_stub_parses_endpoint() {
        let path = PathBuf::from("stub/stub.bin");
        let (_, display) = parse_endpoint_bytes(b"ws://127.0.0.1:4793/valhalla\n", &path).unwrap();
        assert_eq!(display, "ws://127.0.0.1:4793/valhalla");
    }

    #[test]
    fn malformed_stub_is_rejected() {
        let path = PathBuf::from("stub/stub.bin");
        assert!(parse_endpoint_bytes(b"not-an-endpoint\n", &path).is_err());
        assert!(parse_endpoint_bytes(b"ws://127.0.0.1:4793/valhalla\nextra\n", &path).is_err());
        assert!(parse_endpoint_bytes(&[0xff, 0xfe], &path).is_err());
    }
}
