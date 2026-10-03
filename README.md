# Valhalla

Only the chosen arrive here. Not by chance — by selection.

Valhalla is a remote access framework built in Rust (server + agent) and C# (operator panel). Agents traverse the Tor network to reach the relay, which stands as the hall they all report back to. The operator sees every soul that checks in — hostname, user, hardware, privileges, heartbeat — and can reach any of them with a single command. No open ports. No exposed infrastructure. Nothing to find from the outside.

---

## Architecture

Three components. Each with a role.

**The Relay** (`server/`) — The hall itself. A Rust binary (`valhalla-relay`) that hosts a Tor v3 onion service through the [Arti](https://gitlab.torproject.org/tpo/core/arti) library, accepts inbound agents, maintains a registry of every connected session keyed by fingerprint, and holds a TLS-secured gateway open for the operator panel. Metrics flow out over a local Unix socket to a telemetry sidecar.

**The Agent** (`agent/`) — The one who makes the journey. A Rust binary that runs on target Windows machines. On arrival it derives a stable identity from the host hardware using HKDF-SHA256, signs a challenge with its Ed25519 key, and establishes an outbound session through Tor to the relay's onion address. Its destination is baked into a compiled stub — not passed on the command line. It receives commands, manages plugins, and replaces itself in-place when called to without ever dropping its session.

**The Panel** (`panel/`) — Where the operator sits. A Windows Forms application built on DevExpress that authenticates to the relay's panel gateway over mutual TLS. It presents a live grid of every connected agent with full telemetry, handles per-agent and broadcast command dispatch, exposes a file manager, a plugin manager, live charts, Telegram notifications, and an agent builder for packaging new deployments.

---

## Transport and Authentication

Agents travel through Tor by default. The relay also supports a direct WebSocket mode on port 4794 — agents can switch transports mid-session on operator instruction (`CMD:DIRECT_CONNECT`) and fall back automatically to the onion path if the direct route closes.

Every agent carries an Ed25519 signing key. The relay holds an `authorized_keys` file mapping each agent's fingerprint to its public key. On connection the relay issues a nonce; the agent signs it; the signature is verified. Sessions that cannot prove their identity within the configured window are cut. Rate limiting, connection semaphores, and inflight caps hold the line against abuse.

The panel gateway runs its own HMAC-SHA256 challenge over TLS with a separate secret file and connection ceiling.

---

## Protocol

The shared protocol crate (`server/protocol/`) defines the full command vocabulary, frame size limits, telemetry field constraints, and control request/response structures that the relay and panel share.

Agent commands follow a structured prefix scheme:

| Prefix | Purpose |
|---|---|
| `REQ:DATA` | Pull fresh telemetry from an agent |
| `CMD:RECONNECT / CLOSE / SLEEP / HIBERNATE / RESTART / SHUTDOWN` | Session and power control |
| `CMD:DIRECT_CONNECT / DIRECT_DISCONNECT` | Transport switching |
| `CMD:PLUGIN_*` | Chunked plugin delivery and lifecycle |
| `CMD:UPDATE_*` | Chunked in-place agent replacement |
| `CMD:EXECUTE:` | Arbitrary payload execution |

The relay validates every command before routing. Anything outside the known vocabulary is refused at the gate.

---

## Plugin System

Agents carry the ability to receive and run native DLL plugins at runtime. Each plugin exposes three C-ABI entry points defined in `agent/plugin/valhalla_plugin.h`: `PluginOnLoad`, `PluginOnEvent`, and `PluginOnUnload`. The agent maps the plugin into executable memory, resolves its exports, fires the load callback, and routes events in both directions between plugin and server. Built-in events cover chunked file transfer; plugins can define any additional event names they need.

Plugins are loaded entirely from memory. Nothing touches disk beyond the initial delivery.

---

## Agent Updates

When a new agent binary is sent down, the update system (`agent/src/update.rs`) handles the full handoff without losing the session. A probe process validates the replacement, SHA-256 integrity is checked, a local TCP handshake transfers continuity, and the predecessor exits only after the successor confirms it is ready. The new binary inherits the endpoint and key paths without re-authenticating.

The hall never empties during a transfer.

---

## Telemetry

Each agent announces itself on arrival: hardware fingerprint, machine name, username, privilege level, OS, CPU, GPU, RAM, antivirus, uptime, AFK state, and ping. The relay's panel hub holds the latest record for every fingerprint and pushes live updates to all active operator sessions over a broadcast channel.

A Python sidecar (`server/telemetry_service.py`) consumes the relay's local Unix socket and exposes a scriptable interface to the control plane for operators who prefer to work without the full panel.

---

## Key Generation

The `auth-keygen` tool (`server/tools/auth-keygen/`) forges Ed25519 keypairs for agent provisioning. The private seed stays on the agent; the public key goes into the relay's `authorized_keys` file beside the fingerprint. Keys can be generated fresh or derived from a provided seed.

---

## Build and Deployment

**Agent** targets `x86_64-pc-windows-msvc` with Rust 1.92.0. Release builds use `opt-level = 3`, LTO, single codegen unit, `panic = abort`, and full symbol stripping.

**Relay** builds on Linux with the same toolchain. The systemd unit at `server/deploy/systemd/valhalla-relay.service` runs the relay under a dedicated `valhalla` user with `NoNewPrivileges`, `ProtectSystem=strict`, private tmp, and a 20,000 file descriptor limit.

**Panel** targets .NET Framework as a Windows Forms application. `panel/build/prepare_dependencies.py` handles DevExpress and native dependency preparation; `panel/build/dependency_manifest.json` pins versions.

CI covers all three on GitHub Actions: agent on `windows-2022`, relay on `ubuntu-latest`, panel on `windows-2022`.

---

## Configuration Reference

The relay is configured entirely through flags at launch:

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

*The fallen don't wander — they report.*
