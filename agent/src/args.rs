use std::{path::PathBuf, time::Duration};

use crate::{stub, transport::Endpoint};

pub struct Args {
    pub endpoint: Endpoint,
    pub agent_token: String,
    pub arti_state_dir: PathBuf,
    pub arti_cache_dir: PathBuf,
    pub connect_timeout: Duration,
    pub handshake_timeout: Duration,
    pub heartbeat: Duration,
    pub retry_base: Duration,
    pub retry_max: Duration,
}

pub fn get() -> Args {
    let (endpoint, _endpoint_display, install_dir, folder_name, agent_token) = match stub::load_config() {
        Ok(value) => value,
        Err(_) => std::process::exit(2),
    };

    let (arti_state_dir, arti_cache_dir) = resolve_data_dirs(&install_dir, &folder_name);
    Args {
        endpoint,
        agent_token,
        arti_state_dir,
        arti_cache_dir,
        connect_timeout: Duration::from_secs(15),
        handshake_timeout: Duration::from_secs(10),
        heartbeat: Duration::from_secs(30),
        retry_base: Duration::from_secs(1),
        retry_max: Duration::from_secs(60),
    }
}

fn resolve_data_dirs(install_dir: &stub::InstallDir, folder_name: &str) -> (PathBuf, PathBuf) {
    let base = install_dir.resolve().unwrap_or_else(|| PathBuf::from("."));
    let root = base.join(folder_name);
    (root.join("arti-state"), root.join("arti-cache"))
}


#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn endpoint_is_loaded_from_embedded_configuration() {
        let (endpoint, display) = crate::transport::Endpoint::parse("ws://127.0.0.1:4793/")
            .map(|e| (e, "ws://127.0.0.1:4793/".to_owned()))
            .expect("test endpoint should parse");
        assert!(matches!(endpoint, Endpoint::Local { .. }));
        assert_eq!(display, "ws://127.0.0.1:4793/");
    }
}
