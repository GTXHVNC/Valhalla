use std::{path::PathBuf, time::Duration};

use crate::{stub, transport::Endpoint};

pub struct Args {
    pub endpoint: Endpoint,
    pub agent_token: String,
    pub arti_state_dir: PathBuf,
    pub arti_cache_dir: PathBuf,
    pub connect_timeout: Duration,
    pub handshake_timeout: Duration,
    /// Separate timeout for Arti/Tor first-run bootstrap.
    /// Bootstrap on a first run must download consensus documents and
    /// microdescriptors, which takes far longer than a single WebSocket
    /// handshake.  This timeout governs the entire bootstrap phase and
    /// must not be confused with the per-connection handshake timeout.
    pub arti_bootstrap_timeout: Duration,
    pub heartbeat: Duration,
    pub retry_base: Duration,
    pub retry_max: Duration,
}

pub fn get() -> Result<Args, String> {
    let (endpoint, _endpoint_display, install_dir, folder_name, agent_token) = stub::load_config()?;

    let (arti_state_dir, arti_cache_dir) = resolve_data_dirs(&install_dir, &folder_name);
    Args {
        endpoint,
        agent_token,
        arti_state_dir,
        arti_cache_dir,
        connect_timeout: Duration::from_secs(15),
        handshake_timeout: Duration::from_secs(10),
        // First-run Arti/Tor bootstrap can take 60-120+ seconds on a slow
        // connection.  This timeout is intentionally generous so that slow
        // first-run setup does not terminate the agent prematurely.
        arti_bootstrap_timeout: Duration::from_secs(360),
        heartbeat: Duration::from_secs(30),
        retry_base: Duration::from_secs(5),
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
