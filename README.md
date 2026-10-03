# Valhalla

A remote access framework built in Rust (server + agent) and C# (operator panel), designed around Tor onion services as the primary transport layer. Agents connect outbound through the Tor network to a hidden-service relay, which routes operator commands from the panel back to any connected agent — no open ports, no exposed IP, no static infrastructure for an agent to fingerprint or block.

---

## Architecture

Valhalla is structured as three components that communicate over authenticated WebSocket connections.

**Relay server** (`server/`) — A Rust binary (`valhalla-relay`) that acts as the hub. It hosts a Tor v3 onion service using the [Arti](https://gitlab.torproject.org/tpo/core/arti) client library, accepts inbound agent connections, maintains an agent registry keyed by fingerprint, and exposes a TLS-secured panel gateway for operator sessions. Metrics are exposed over a local Unix socket consumed by the telemetry sidecar.

**Agent** (`agent/`) — A Rust binary that runs on target Windows machines. On startup it derives a unique, stable fingerprint from the host hardware identity using HKDF-SHA256, signs a challenge with its Ed25519 key, and establishes an outbound WebSocket session through Tor to the relay's onion address. The connection endpoint is embedded in a compiled stub (`stub/stub.bin`) rather than passed on the command line. The agent handles commands dispatched from the relay, manages the plugin lifecycle, and performs in-place self-updates without dropping its session.

**Panel** (`panel/`) — A Windows Forms application built on DevExpress that connects operators to the relay's panel gateway over mutual TLS. It presents a live grid of connected agents with their telemetry (hostname, user, OS, hardware, privilege level, AV, ping), provides per-agent and broadcast command dispatch, a file manager, a plugin manager, live charts, Telegram notifications, and an agent builder that packages a configured agent binary for deployment.

---

## Transport and Authentication

Agents connect over Tor by default. The relay also supports a direct WebSocket mode (port 4794) that agents can switch to dynamically on operator instruction (`CMD:DIRECT_CONNECT`), with automatic fallback to the onion endpoint if the direct path fails.

Authentication is mutual and cryptographic. Each agent holds an Ed25519 signing key. The server maintains an `authorized_keys` file mapping agent fingerprints to their public keys. On connection the server issues a nonce challenge; the agent signs it; the server verifies the signature against the known public key for that fingerprint. Sessions that fail authentication within the configured timeout are dropped. Rate limiting, per-connection semaphores, and configurable inflight caps prevent abuse and resource exhaustion.

The panel gateway uses a separate HMAC-SHA256 TOTP-style mechanism over TLS, with an independent secret file and connection limit.

---

## Protocol

The shared protocol crate (`server/protocol/`) defines the command vocabulary, maximum frame sizes, telemetry field limits, and control request/response structures used by both the relay and the panel.

Agent commands follow a structured prefix scheme:

| Prefix | Purpose |
|---|---|
| `REQ:DATA` | Request fresh telemetry from an agent |
| `CMD:RECONNECT / CLOSE / SLEEP / HIBERNATE / RESTART / SHUTDOWN` | Session and power control |
| `CMD:DIRECT_CONNECT / DIRECT_DISCONNECT` | Transport switching |
| `CMD:PLUGIN_*` | Chunked plugin delivery and lifecycle |
| `CMD:UPDATE_*` | Chunked in-place agent update |
| `CMD:EXECUTE:` | Arbitrary payload execution |

Commands are validated before dispatch; the relay refuses anything not matching the supported vocabulary.

---

## Plugin System

Agents support runtime-loadable plugins as native DLLs (Windows). Plugins expose three C-ABI entry points defined in `agent/plugin/valhalla_plugin.h`: `PluginOnLoad`, `PluginOnEvent`, and `PluginOnUnload`. The agent maps the plugin into executable memory, resolves its exports, invokes the load callback with the host context, and routes events bidirectionally between the plugin and the server. Built-in event types cover chunked file transfer; plugins may define their own event names for application-specific messaging.

The agent loads plugins from memory without touching disk beyond the initial delivery stage.

---

## Agent Updates

The update system (`agent/src/update.rs`) handles in-place binary replacement while the agent remains registered with the relay. The sequence uses a probe process, a SHA-256 integrity check on the replacement binary, a local TCP handoff handshake to transfer session continuity, and a final-ready acknowledgement before the predecessor exits. The replacement binary inherits the session endpoint and authentication key paths without re-authentication.

---

## Telemetry

On each connection the agent collects and transmits: hardware fingerprint, machine nickname, username, privilege level, OS version, CPU, GPU, RAM, antivirus presence, uptime, AFK state, and round-trip ping (for direct connections). The relay's panel hub caches the latest telemetry record per fingerprint and pushes updates to all active panel sessions over a broadcast channel.

A Python telemetry sidecar (`server/telemetry_service.py`) consumes the relay's local Unix socket and can relay commands via the control socket, providing a lightweight scriptable interface to the control plane without a full panel session.

---

## Key Generation

The `auth-keygen` tool (`server/tools/auth-keygen/`) generates Ed25519 keypairs for agent provisioning. The private seed goes into the agent's key file; the public key hex goes into the server's `authorized_keys` file alongside the fingerprint. Keys can be generated fresh or derived from a provided seed hex.

---

## Build and Deployment

**Agent** builds for `x86_64-pc-windows-msvc` with Rust 1.92.0. Release profile uses `opt-level = 3`, LTO, single codegen unit, `panic = abort`, and symbol stripping for minimal binary size.

**Relay** builds for Linux on the same toolchain. The provided systemd unit (`server/deploy/systemd/valhalla-relay.service`) runs the relay under a dedicated `valhalla` user with `NoNewPrivileges`, `ProtectSystem=strict`, private tmp, and a 20,000 file descriptor limit.

**Panel** targets .NET Framework and is built as a Windows Forms application. The `panel/build/prepare_dependencies.py` script handles DevExpress and other native dependency preparation; `panel/build/dependency_manifest.json` pins versions.

CI runs on GitHub Actions: agent tests and release build on `windows-2022`, relay build and tests on `ubuntu-latest`, panel build on `windows-2022`.

---

## Configuration Reference

The relay accepts configuration entirely through command-line flags. Key parameters:

| Flag | Default | Description |
|---|---|---|
| `--authorized-keys` | `/etc/valhalla/authorized_keys` | Agent public key file |
| `--state-dir` / `--cache-dir` | `/var/lib/valhalla/arti-*` | Arti Tor client state |
| `--onion-port` | `443` | Port advertised on the onion service |
| `--max-connections` | `4096` | Concurrent agent connection cap |
| `--max-auth-inflight` | `64` | Concurrent handshakes in progress |
| `--handshake-timeout` | `8s` | Time allowed for WebSocket upgrade |
| `--auth-timeout` | `8s` | Time allowed for challenge/response |
| `--idle-timeout` | `90s` | Inactivity before session drop |
| `--panel-listen` | — | Address for the TLS panel gateway |
| `--no-onion` | — | Disable Tor; use direct/local only |

---

## License

Educational Cybersecurity License (ECL) 1.0.

---

*Some doors don't open from the outside.*
