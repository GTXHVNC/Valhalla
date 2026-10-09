use std::{io, net::SocketAddr, path::PathBuf, sync::Arc, time::{Duration, Instant}};

use arti_client::{config::TorClientConfigBuilder, DataStream, TorClient};
use base64::{engine::general_purpose::STANDARD, Engine as _};
use ed25519_dalek::SigningKey;
use futures_util::{SinkExt, StreamExt};
use tokio::{net::TcpStream, time::{sleep, timeout}};
use tokio_tungstenite::{client_async_with_config, tungstenite::{http::Request, Message}, WebSocketStream};
use tor_rtcompat::PreferredRuntime;
use zeroize::{Zeroize, Zeroizing};

use crate::{auth, dbg_log, text};

/// How long to pause after a bootstrap timeout before returning the error.
///
/// When `tokio::time::timeout` cancels the bootstrap future, Arti's background
/// tasks (directory updater, status reporter) are still running on the Tokio
/// runtime.  Those tasks hold file locks on the Arti state/cache directories.
/// If we retry immediately, `create_unbootstrapped_async` will detect
/// `LocalResourceAlreadyInUse` and open the SQLite directory store in
/// **read-only** mode, which prevents any directory documents from being
/// written and guarantees that every subsequent bootstrap attempt also times
/// out.  Sleeping briefly after a cancelled bootstrap gives the background
/// tasks enough time to observe that the `Arc<DirMgr>` has been dropped
/// (`Weak::upgrade` → None) and exit, releasing both the SQLite lock file and
/// the state lock file before we create a fresh client.
const POST_CANCEL_GRACE: Duration = Duration::from_secs(3);

#[derive(Debug, Clone)]
pub enum Endpoint {
    Onion { host: String, port: u16, path: String },
    Local { host: String, port: u16, path: String },
    Direct { host: String, port: u16, path: String },
}

impl Endpoint {
    pub fn parse(value: &str) -> Result<Self, String> {
        let (scheme, rest) = value.split_once("://").ok_or("endpoint must use ws://")?;
        if !scheme.eq_ignore_ascii_case("ws") {
            return Err("only ws:// endpoints are supported".into());
        }
        let (authority, raw_path) = rest.split_once('/').unwrap_or((rest, ""));
        let path = format!("/{}", raw_path.trim_start_matches('/'));
        let path = if path == "/" { "/".into() } else { path };
        let (host, port) = if let Some(rest) = authority.strip_prefix('[') {
            let (host, suffix) = rest.split_once(']').ok_or("invalid bracketed host")?;
            let suffix = suffix.strip_prefix(':').ok_or("IPv6 endpoints require a port")?;
            (host.to_owned(), parse_port(suffix)?)
        } else {
            let (host, port) = authority.rsplit_once(':').ok_or("endpoint must include a port")?;
            (host.to_owned(), parse_port(port)?)
        };
        let host = host.to_ascii_lowercase();
        if host.is_empty() {
            return Err("endpoint host is empty".into());
        }
        if host.ends_with(".onion") {
            if !valid_onion_host(&host) {
                return Err("endpoint host is not a valid v3 onion hostname".into());
            }
            Ok(Self::Onion { host, port, path })
        } else if is_loopback_host(&host) {
            Ok(Self::Local { host, port, path })
        } else {
            Err("non-onion endpoints are restricted to loopback for deterministic testing".into())
        }
    }

    pub fn target(&self) -> (&str, u16, &str) {
        match self {
            Self::Onion { host, port, path } | Self::Local { host, port, path } | Self::Direct { host, port, path } => (host, *port, path),
        }
    }

    pub fn is_onion(&self) -> bool {
        matches!(self, Self::Onion { .. })
    }

    #[cfg(test)]
    pub fn is_direct(&self) -> bool {
        matches!(self, Self::Direct { .. })
    }

    pub fn path(&self) -> &str {
        match self {
            Self::Onion { path, .. } | Self::Local { path, .. } | Self::Direct { path, .. } => path,
        }
    }

    pub fn direct(addr: SocketAddr, path: &str) -> Result<Self, String> {
        if addr.port() == 0 {
            return Err("direct endpoint port must not be zero".into());
        }
        if addr.ip().is_unspecified() || addr.ip().is_multicast() {
            return Err("direct endpoint address is not usable".into());
        }
        let host = addr.ip().to_string();
        if host.is_empty() {
            return Err("direct endpoint host is empty".into());
        }
        Ok(Self::Direct { host, port: addr.port(), path: normalize_path(path) })
    }
}

impl Drop for Endpoint {
    fn drop(&mut self) {
        if let Self::Direct { host, path, .. } = self {
            host.zeroize();
            path.zeroize();
        }
    }
}

fn format_authority(host: &str, port: u16) -> String {
    if host.contains(':') {
        format!("[{host}]:{port}")
    } else {
        format!("{host}:{port}")
    }
}

fn normalize_path(raw: &str) -> String {
    let path = format!("/{}", raw.trim_start_matches('/'));
    if path == "/" { "/".to_owned() } else { path }
}

fn parse_port(value: &str) -> Result<u16, String> {
    let port = value.parse::<u16>().map_err(|_| "invalid endpoint port".to_owned())?;
    if port == 0 { Err("endpoint port must not be zero".into()) } else { Ok(port) }
}

fn valid_onion_host(host: &str) -> bool {
    let label = host.strip_suffix(".onion").unwrap_or_default();
    label.len() == 56 && label.bytes().all(|b| b.is_ascii_lowercase() || matches!(b, b'2'..=b'7'))
}

fn is_loopback_host(host: &str) -> bool {
    matches!(host, "127.0.0.1" | "localhost" | "::1")
}

#[derive(Debug, Clone, Copy)]
pub struct ClientIdentity<'a> {
    pub fingerprint: &'a str,
}

pub enum Session {
    Local(WebSocketStream<TcpStream>),
    Tor(WebSocketStream<DataStream>),
}

impl Session {
    pub async fn send_text(&mut self, text: &str) -> io::Result<()> {
        match self {
            Self::Local(stream) => stream.send(Message::Text(text.to_owned().into())).await.map_err(ws_io),
            Self::Tor(stream) => stream.send(Message::Text(text.to_owned().into())).await.map_err(ws_io),
        }
    }

    pub async fn send_ping(&mut self) -> io::Result<()> {
        match self {
            Self::Local(stream) => stream.send(Message::Ping(Vec::new().into())).await.map_err(ws_io),
            Self::Tor(stream) => stream.send(Message::Ping(Vec::new().into())).await.map_err(ws_io),
        }
    }

    pub async fn close(&mut self) -> io::Result<()> {
        match self {
            Self::Local(stream) => stream.close(None).await.map_err(ws_io),
            Self::Tor(stream) => stream.close(None).await.map_err(ws_io),
        }
    }

    pub async fn next_text(&mut self) -> io::Result<Option<String>> {
        loop {
            let message = match self {
                Self::Local(stream) => stream.next().await,
                Self::Tor(stream) => stream.next().await,
            };
            match message {
                Some(Ok(Message::Text(text))) => return Ok(Some(text.to_string())),
                Some(Ok(Message::Binary(_))) => return Err(io::Error::new(io::ErrorKind::InvalidData, "binary WebSocket frame is not supported")),
                Some(Ok(Message::Ping(payload))) => {
                    match self {
                        Self::Local(stream) => stream.send(Message::Pong(payload)).await,
                        Self::Tor(stream) => stream.send(Message::Pong(payload)).await,
                    }.map_err(ws_io)?;
                }
                Some(Ok(Message::Pong(_))) | Some(Ok(Message::Frame(_))) => {}
                Some(Ok(Message::Close(_))) | None => return Ok(None),
                Some(Err(error)) => return Err(ws_io(error)),
            }
        }
    }
}

fn ws_io(error: tokio_tungstenite::tungstenite::Error) -> io::Error {
    io::Error::new(io::ErrorKind::Other, error.to_string())
}

pub fn ws_config() -> tokio_tungstenite::tungstenite::protocol::WebSocketConfig {
    tokio_tungstenite::tungstenite::protocol::WebSocketConfig::default()
        .read_buffer_size(8 * 1024)
        .write_buffer_size(4 * 1024)
        .max_write_buffer_size(64 * 1024)
        .max_message_size(Some(valhalla_protocol::MAX_WS_MESSAGE_BYTES))
        .max_frame_size(Some(valhalla_protocol::MAX_WS_MESSAGE_BYTES))
}

pub struct Connector {
    endpoint: Endpoint,
    state_dir: PathBuf,
    cache_dir: PathBuf,
    agent_token: Zeroizing<String>,
    tor: Option<Arc<TorClient<PreferredRuntime>>>,
}

impl Connector {
    pub fn new(endpoint: Endpoint, state_dir: PathBuf, cache_dir: PathBuf, agent_token: String) -> Self {
        Self { endpoint, state_dir, cache_dir, agent_token: Zeroizing::new(agent_token), tor: None }
    }

    pub fn endpoint(&self) -> &Endpoint {
        &self.endpoint
    }

    pub fn set_endpoint(&mut self, endpoint: Endpoint) {
        self.endpoint = endpoint;
    }

    /// Initialise the Arti/Tor client, performing full bootstrap.
    ///
    /// The `bootstrap_timeout` governs the entire Arti bootstrap phase, which
    /// includes downloading directory documents on first run.  This is
    /// intentionally separate from the per-connection `handshake_timeout` used
    /// by individual WebSocket operations: first-run bootstrap legitimately
    /// takes far longer than a single network handshake and must not be
    /// constrained by the same short timeout.
    ///
    /// Once bootstrap succeeds the client is cached; subsequent calls return
    /// the cached client without re-bootstrapping.
    ///
    /// # Implementation notes
    ///
    /// This function deliberately **does not** call `TorClient::create_bootstrapped`
    /// behind a single `tokio::time::timeout`.  That pattern has two problems:
    ///
    /// 1. When the outer timeout fires and drops the future, Arti's internal
    ///    background tasks (directory updater, status reporter) are still alive
    ///    on the Tokio runtime.  They hold file locks on the state/cache
    ///    directories via `LockFileGuard`.  A fresh `create_unbootstrapped_async`
    ///    call on the next retry then finds `LocalResourceAlreadyInUse` and
    ///    silently downgrades the SQLite directory store to read-only mode.
    ///    In read-only mode no directory documents can be written, so every
    ///    subsequent bootstrap attempt also times out — forming an infinite loop.
    ///
    /// 2. The real error (network blockage, clock skew, directory failure) is
    ///    discarded and replaced with a generic opaque timeout message,
    ///    making it impossible to distinguish a timed-out bootstrap from one
    ///    that failed immediately with a concrete error.
    ///
    /// The fix is to:
    ///   a) Create the client with `create_unbootstrapped_async` first (which
    ///      itself handles `LocalResourceAlreadyInUse` with a short retry).
    ///   b) Subscribe to bootstrap events so we can log meaningful progress.
    ///   c) Drive `bootstrap()` and the event stream concurrently inside the
    ///      deadline, logging progress at every status change.
    ///   d) After a timeout cancellation, sleep `POST_CANCEL_GRACE` before
    ///      returning so Arti's background tasks have time to see the dropped
    ///      `Arc` and release their file locks before the next retry.
    async fn ensure_tor(&mut self, bootstrap_timeout: Duration) -> io::Result<Arc<TorClient<PreferredRuntime>>> {
        if let Some(tor) = &self.tor {
            dbg_log!("[Arti] Using cached Tor client");
            return Ok(Arc::clone(tor));
        }

        dbg_log!("[Setup] Creating Arti state/cache directories");
        std::fs::create_dir_all(&self.state_dir)
            .map_err(|e| io::Error::new(e.kind(), format!("failed to create Arti state dir: {e}")))?;
        std::fs::create_dir_all(&self.cache_dir)
            .map_err(|e| io::Error::new(e.kind(), format!("failed to create Arti cache dir: {e}")))?;
        dbg_log!("[Setup] Arti directories ready");

        dbg_log!("[Arti] Building Arti client configuration");
        let config = TorClientConfigBuilder::from_directories(&self.state_dir, &self.cache_dir)
            .build()
            .map_err(|e| io::Error::new(io::ErrorKind::InvalidData, format!("invalid Arti configuration: {e}")))?;

        // Phase 1: create an unbootstrapped client.
        //
        // `create_unbootstrapped_async` acquires the state/cache directory
        // locks and initialises in-memory data structures.  It already retries
        // internally for up to 500 ms if another instance holds the lock
        // (`LocalResourceAlreadyInUse`), which covers the window immediately
        // after a previous bootstrap attempt was cancelled.  We still add
        // POST_CANCEL_GRACE on the timeout path (below) to ensure the window
        // is covered even when that built-in retry is exhausted.
        dbg_log!("[Arti] Creating unbootstrapped Tor client");
        let tor = TorClient::with_runtime(
                tor_rtcompat::PreferredRuntime::current()
                    .map_err(|e| io::Error::new(io::ErrorKind::Other, format!("could not obtain Tokio runtime handle: {e}")))?,
            )
            .config(config)
            .create_unbootstrapped_async()
            .await
            .map_err(|e| io::Error::new(io::ErrorKind::Other, format!("Arti client creation failed: {e}")))?;

        // Phase 2: subscribe to bootstrap events before calling bootstrap().
        //
        // The subscription must be created before bootstrap() is called so that
        // no status updates are missed between the two calls.
        let mut events = tor.bootstrap_events();

        dbg_log!("[Arti] Starting Tor bootstrap (timeout: {}s)", bootstrap_timeout.as_secs());
        let start = Instant::now();

        // Retain a second Arc handle before the async block below takes
        // ownership of `tor`.  When the timeout fires and drops the async
        // block, this handle keeps the TorClient alive for the grace-period
        // sleep so that we can drop it intentionally after the sleep, not
        // before.  In the success branch we use this handle to cache the
        // client in self.tor.
        let tor_handle = Arc::clone(&tor);

        // Drive bootstrap() and status events concurrently.  We select! so
        // that we can log meaningful progress at every status change while
        // still driving the bootstrap future forward.
        //
        // `tor` is moved into this async block.  `tor_handle` is retained
        // outside so the success/failure match arms can use the client.
        let result = timeout(bootstrap_timeout, async move {
            // Pin the bootstrap future so we can poll it repeatedly in select!.
            tokio::pin! {
                let boot = tor.bootstrap();
            }
            let mut last_pct = -1i32;
            loop {
                tokio::select! {
                    // Bootstrap completed (or failed with a hard error).
                    result = &mut boot => {
                        return result.map_err(|e| {
                            io::Error::new(io::ErrorKind::Other, format!("Arti bootstrap failed: {e}"))
                        });
                    }
                    // A new status event arrived — log it and keep going.
                    Some(status) = events.next() => {
                        let pct = (status.as_frac() * 100.0) as i32;
                        // Only log when the percentage changes to avoid spam.
                        if pct != last_pct {
                            last_pct = pct;
                            let elapsed = start.elapsed().as_secs();
                            if let Some(blockage) = status.blocked() {
                                dbg_log!("[Arti] Bootstrap {}% (+{}s) — BLOCKED: {}",
                                    pct, elapsed, blockage.message());
                            } else {
                                dbg_log!("[Arti] Bootstrap {}% (+{}s)", pct, elapsed);
                            }
                        }
                    }
                }
            }
        }).await;

        match result {
            // Bootstrap completed successfully.
            Ok(Ok(())) => {
                dbg_log!("[Arti] Tor bootstrap completed successfully (+{}s)",
                    start.elapsed().as_secs());
                // tor_handle is the surviving Arc (tor was moved into the async
                // block above which has now been consumed by the timeout call).
                self.tor = Some(Arc::clone(&tor_handle));
                Ok(tor_handle)
            }
            // Bootstrap returned a hard error before the deadline.
            Ok(Err(e)) => {
                dbg_log!("[Arti] Bootstrap failed after {}s: {}",
                    start.elapsed().as_secs(), e);
                // Drop the client explicitly, then sleep so Arti's background
                // tasks can observe the dropped Arc and release directory locks
                // before the next retry.
                drop(tor_handle);
                sleep(POST_CANCEL_GRACE).await;
                Err(e)
            }
            // The deadline fired — bootstrap was cancelled.
            Err(_elapsed) => {
                dbg_log!("[Arti] Bootstrap timed out after {}s (deadline={}s) — sleeping {}s to let background tasks release directory locks",
                    start.elapsed().as_secs(),
                    bootstrap_timeout.as_secs(),
                    POST_CANCEL_GRACE.as_secs());
                // The async block (which owned `tor`) was dropped when timeout
                // fired, but tor_handle is still live here.  Dropping it now
                // signals ManagerDropped to Arti's background tasks.  The sleep
                // that follows gives them time to exit and release their file
                // locks before the outer retry loop calls ensure_tor again.
                drop(tor_handle);
                sleep(POST_CANCEL_GRACE).await;
                Err(io::Error::new(
                    io::ErrorKind::TimedOut,
                    format!("Arti bootstrap timed out after {}s", bootstrap_timeout.as_secs()),
                ))
            }
        }
    }

    pub async fn connect_authenticated(
        &mut self,
        fingerprint: &str,
        signing_key: &SigningKey,
        connect_timeout: Duration,
        handshake_timeout: Duration,
        bootstrap_timeout: Duration,
    ) -> io::Result<Session> {
        dbg_log!("[Network] Connecting (connect={}s, handshake={}s, bootstrap={}s)",
            connect_timeout.as_secs(), handshake_timeout.as_secs(), bootstrap_timeout.as_secs());
        let mut session = self.connect_ws(connect_timeout, handshake_timeout, bootstrap_timeout).await?;
        dbg_log!("[Network] WebSocket connection established; authenticating");
        authenticate(&mut session, ClientIdentity { fingerprint }, signing_key, handshake_timeout).await?;
        dbg_log!("[Network] Authentication successful");
        Ok(session)
    }

    pub async fn connect_ws(
        &mut self,
        connect_timeout: Duration,
        handshake_timeout: Duration,
        bootstrap_timeout: Duration,
    ) -> io::Result<Session> {
        let (host, port, path) = self.endpoint.target();
        let authority = Zeroizing::new(format_authority(host, port));
        let uri = Zeroizing::new(format!("ws://{}{}", authority.as_str(), path));
        let request = Request::builder()
            .uri(uri.as_str())
            .header("Host", authority.as_str())
            .header("X-VALHALLA-AGENT-TOKEN", self.agent_token.as_str())
            .body(())
            .map_err(|e| io::Error::new(io::ErrorKind::InvalidInput, e.to_string()))?;

        let endpoint = self.endpoint.clone();
        // Endpoint implements Drop so its owned String fields cannot be moved out. Borrowing the
        // cloned endpoint also keeps the endpoint data independent from the mutable Tor cache.
        match &endpoint {
            Endpoint::Local { host, port, .. } => {
                dbg_log!("[Network] Connecting to local endpoint {}:{}", host, port);
                let stream = timeout(connect_timeout, TcpStream::connect((host.as_str(), *port)))
                    .await
                    .map_err(|_| io::Error::new(io::ErrorKind::TimedOut, "WebSocket connect timeout"))??;
                let (ws, _) = timeout(handshake_timeout, client_async_with_config(request, stream, Some(ws_config())))
                    .await
                    .map_err(|_| io::Error::new(io::ErrorKind::TimedOut, "WebSocket handshake timeout"))?
                    .map_err(ws_io)?;
                Ok(Session::Local(ws))
            }
            Endpoint::Onion { host, port, .. } => {
                // ensure_tor uses the bootstrap timeout, not the handshake timeout.
                // This is intentional: first-run Arti bootstrap requires downloading
                // directory documents and can take minutes.  The handshake timeout
                // only applies to the subsequent per-connection WebSocket operations.
                dbg_log!("[Arti] Ensuring Tor client is ready for onion connection");
                let tor = self.ensure_tor(bootstrap_timeout).await?;
                dbg_log!("[Network] Connecting to onion service {}:{}", &host[..8], port);
                let stream = timeout(connect_timeout, tor.connect((host.as_str(), *port)))
                    .await
                    .map_err(|_| io::Error::new(io::ErrorKind::TimedOut, "Tor onion connection timeout"))?
                    .map_err(|e| io::Error::new(io::ErrorKind::Other, format!("Tor onion connection failed: {e}")))?;
                let (ws, _) = timeout(handshake_timeout, client_async_with_config(request, stream, Some(ws_config())))
                    .await
                    .map_err(|_| io::Error::new(io::ErrorKind::TimedOut, "onion WebSocket handshake timeout"))?
                    .map_err(ws_io)?;
                Ok(Session::Tor(ws))
            }
            Endpoint::Direct { host, port, .. } => {
                dbg_log!("[Network] Connecting to direct endpoint {}:{}", host, port);
                let stream = timeout(connect_timeout, TcpStream::connect((host.as_str(), *port)))
                    .await
                    .map_err(|_| io::Error::new(io::ErrorKind::TimedOut, "direct WebSocket connect timeout"))?
                    .map_err(|e| io::Error::new(e.kind(), format!("direct endpoint connection failed: {e}")))?;
                let (ws, _) = timeout(handshake_timeout, client_async_with_config(request, stream, Some(ws_config())))
                    .await
                    .map_err(|_| io::Error::new(io::ErrorKind::TimedOut, "direct WebSocket handshake timeout"))?
                    .map_err(ws_io)?;
                Ok(Session::Local(ws))
            }
        }
    }
}

async fn authenticate(
    session: &mut Session,
    identity: ClientIdentity<'_>,
    signing_key: &SigningKey,
    handshake_timeout: Duration,
) -> io::Result<()> {
    let hello = format!("{}{}", text::HELLO, identity.fingerprint);
    timeout(handshake_timeout, session.send_text(&hello))
        .await
        .map_err(|_| io::Error::new(io::ErrorKind::TimedOut, "authentication send timeout"))??;

    let challenge = timeout(handshake_timeout, session.next_text())
        .await
        .map_err(|_| io::Error::new(io::ErrorKind::TimedOut, "authentication challenge timeout"))??
        .ok_or_else(|| io::Error::new(io::ErrorKind::UnexpectedEof, "server closed before authentication challenge"))?;
    let nonce_b64 = challenge.strip_prefix("AUTH:CHALLENGE:")
        .ok_or_else(|| io::Error::new(io::ErrorKind::PermissionDenied, "unexpected authentication challenge"))?;
    let nonce = STANDARD.decode(nonce_b64)
        .map_err(|_| io::Error::new(io::ErrorKind::InvalidData, "invalid authentication challenge"))?;
    if nonce.len() != 32 {
        return Err(io::Error::new(io::ErrorKind::InvalidData, "invalid authentication nonce length"));
    }
    let public_key = auth::public_key_hex(signing_key);
    let signature = auth::sign_challenge(&identity, &nonce, signing_key);
    let response = format!("AUTH:RESPONSE:{public_key}:{signature}");
    timeout(handshake_timeout, session.send_text(&response))
        .await
        .map_err(|_| io::Error::new(io::ErrorKind::TimedOut, "authentication response timeout"))??;

    let result = timeout(handshake_timeout, session.next_text())
        .await
        .map_err(|_| io::Error::new(io::ErrorKind::TimedOut, "authentication result timeout"))??
        .ok_or_else(|| io::Error::new(io::ErrorKind::UnexpectedEof, "server closed before authentication result"))?;
    if result == "AUTH:OK" {
        Ok(())
    } else {
        Err(io::Error::new(io::ErrorKind::PermissionDenied, "authentication rejected"))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn accepts_v3_onion_and_loopback_only() {
        let onion = format!("ws://{}.onion:443/ws", "a".repeat(56));
        assert!(Endpoint::parse(&onion).is_ok());
        assert!(Endpoint::parse("ws://127.0.0.1:4793/").is_ok());
        assert!(Endpoint::parse("ws://10.0.0.1:4793/").is_err());
    }

    #[test]
    fn normalizes_onion_hostname_case() {
        let upper = format!("ws://{}.ONION:443/ws", "A".repeat(56));
        let endpoint = Endpoint::parse(&upper).expect("uppercase onion endpoint should normalize");
        assert_eq!(endpoint.target().0, format!("{}.onion", "a".repeat(56)));
    }

    #[test]
    fn rejects_non_ws() {
        assert!(Endpoint::parse("wss://127.0.0.1:4793/").is_err());
    }
}

#[cfg(test)]
mod direct_tests {
    use super::*;

    #[test]
    fn direct_endpoint_accepts_socket_address_and_preserves_path() {
        let endpoint = Endpoint::direct("192.0.2.10:4794".parse().unwrap(), "/valhalla").unwrap();
        assert!(endpoint.is_direct());
        assert_eq!(endpoint.target(), ("192.0.2.10", 4794, "/valhalla"));
    }

    #[test]
    fn direct_endpoint_accepts_ipv6_socket_address() {
        let endpoint = Endpoint::direct("[2001:db8::10]:4794".parse().unwrap(), "/valhalla").unwrap();
        assert!(endpoint.is_direct());
        assert_eq!(endpoint.path(), "/valhalla");
    }

    #[test]
    fn direct_endpoint_rejects_unspecified_address() {
        assert!(Endpoint::direct("0.0.0.0:4794".parse().unwrap(), "/valhalla").is_err());
    }

    #[test]
    fn websocket_authority_brackets_ipv6() {
        assert_eq!(format_authority("2001:db8::10", 4794), "[2001:db8::10]:4794");
        assert_eq!(format_authority("203.0.113.10", 4794), "203.0.113.10:4794");
    }
}
