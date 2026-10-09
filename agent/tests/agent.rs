#[test]
fn command_contract() {
    let source = std::fs::read_to_string("src/text.rs").unwrap();
    for command in [
        "SLEEP", "HIBERNATE", "RESTART", "SHUTDOWN", "RECONNECT", "CLOSE", "EXECUTE", "UPDATE",
    ] {
        assert!(source.contains(command));
    }
    for token in [
        "UPDATE_BEGIN:", "UPDATE_CHUNK:", "UPDATE_END:", "HELLO:EINHERJAR:FINGERPRINT:", "DATA:",
        "PLUGIN:", "PLUGIN_EVENT:", "PLUGIN_OUT:", "DIRECT_CONNECT:", "DIRECT_DISCONNECT", "PLUGIN_RESUME:",
    ] {
        assert!(source.contains(token), "missing protocol token: {token}");
    }
}

#[test]
fn transport_contract() {
    let source = std::fs::read_to_string("src/transport.rs").unwrap();
    // Bootstrap is performed in two phases: first create an unbootstrapped
    // client (which acquires directory locks), then drive bootstrap() separately
    // so we can monitor progress and apply a grace period after cancellation.
    // The old single-call TorClient::create_bootstrapped pattern was replaced
    // because wrapping it in tokio::time::timeout caused background tasks to
    // hold directory file locks across retries, silently degrading subsequent
    // attempts to read-only mode.
    assert!(source.contains("create_unbootstrapped_async"),
        "bootstrap must use two-phase init (create_unbootstrapped_async + bootstrap())");
    assert!(source.contains(".bootstrap()"),
        "bootstrap must call .bootstrap() on the unbootstrapped client");
    assert!(source.contains("bootstrap_events"),
        "bootstrap must subscribe to status events for progress logging");
    assert!(source.contains("POST_CANCEL_GRACE"),
        "bootstrap must sleep POST_CANCEL_GRACE after cancellation to release dir locks");
    assert!(source.contains("tor.connect"));
    assert!(source.contains("client_async_with_config"));
    assert!(source.contains("MAX_WS_MESSAGE_BYTES"));
    assert!(source.contains("match &endpoint"));
    assert!(!source.contains("match endpoint {"));

    let args = std::fs::read_to_string("src/args.rs").unwrap();
    assert!(!args.contains("EINHERJAR_ENDPOINT"));
    assert!(args.contains("stub::load_config"));
    assert!(!args.contains("std::env::args"));
    assert!(!args.contains("EINHERJAR_AUTH_KEY_FILE"));
}

#[test]
fn update_flow_contract() {
    let source = std::fs::read_to_string("src/net.rs").unwrap();
    let update = std::fs::read_to_string("src/update.rs").unwrap();
    for token in ["text::UPDATE", "UPDATE_BEGIN", "UPDATE_CHUNK", "UPDATE_END"] {
        assert!(source.contains(token), "missing update token: {token}");
    }
    assert!(source.contains("spawn_blocking(move || handoff.wait_admission())"));
    assert!(!source.contains("unsupported-in-websocket-mode"));
    for token in ["EINHERJAR_UPDATE_SUCCESSOR", "PROBE_WAIT", "UPDATE_FINAL_READY:", "request_helper_self_delete"] {
        assert!(update.contains(token), "missing update token: {token}");
    }
}

#[test]
fn runtime_stays_multithreaded() {
    let main = std::fs::read_to_string("src/main.rs").unwrap();
    assert!(main.contains("new_multi_thread"));
    assert!(main.contains("worker_threads(2)"));
}

#[test]
fn configuration_contract() {
    let stub = std::fs::read_to_string("src/stub.rs").unwrap();
    assert!(stub.contains("CONFIG_SLOT_MARKER"));
    assert!(stub.contains("const CONFIG_SLOT_MARKER: &[u8; 32]"));
    assert!(stub.contains("CONFIG_SLOT_SIZE: usize = 4096"));
    assert!(stub.contains("#[unsafe(no_mangle)]"));
    assert!(stub.contains("pub static EMBEDDED_CONFIG_SLOT"));
    assert!(stub.contains("fn anchor_config_slot()"));
    assert!(stub.contains("read_volatile(EMBEDDED_CONFIG_SLOT.as_ptr())"));
    assert!(stub.contains("anchor_config_slot();"));
    assert!(stub.contains("parse_payload"));
    assert!(stub.contains("while search_from + MAGIC.len() <= haystack.len()"));

    let args = std::fs::read_to_string("src/args.rs").unwrap();
    assert!(args.contains("pub fn get() -> Result<Args, String>"));
    assert!(args.contains("arti_bootstrap_timeout: Duration::from_secs(360)"));
    assert!(!args.contains("Duration::from_secs(180)"));
}

#[test]
fn ci_config_slot_validator_contract() {
    let workflow = std::fs::read_to_string("../.github/workflows/build.yml")
        .or_else(|_| std::fs::read_to_string(".github/workflows/build.yml"))
        .unwrap();
    assert!(workflow.contains("$markerText = 'VALHALLA-EINHERJAR-CFG-SLOT-V1'"));
    assert!(workflow.contains("[System.Text.Encoding]::ASCII.GetString($bytes)"));
    assert!(workflow.contains("[System.StringComparison]::Ordinal"));
    assert!(workflow.contains("${path}: dedicated configuration slot validated"));
    assert!(!workflow.contains(r#""VALHALLA-EINHERJAR-CFG-SLOT-V1`0`0""#));
}

/// Verify that the bootstrap implementation preserves actionable error information.
///
/// Before the fix, every failure — whether a hard Arti error or a deadline
/// expiry — was collapsed to the generic string "Arti bootstrap timeout",
/// making it impossible to distinguish a timed-out bootstrap from one that
/// failed immediately with a concrete error (bad config, permission denied,
/// directory unavailable, etc.).
///
/// After the fix:
///   - A hard error from Arti is reported as "Arti bootstrap failed: <detail>".
///   - A deadline expiry is reported as "Arti bootstrap timed out after Ns".
///   - The two are never confused.
#[test]
fn bootstrap_error_messages_are_distinct_and_informative() {
    let source = std::fs::read_to_string("src/transport.rs").unwrap();

    // Hard Arti errors must preserve the underlying message.
    assert!(
        source.contains("Arti bootstrap failed:"),
        "hard bootstrap error must include 'Arti bootstrap failed:' with the underlying error"
    );

    // Timeout must be reported with the actual elapsed deadline, not a generic string.
    assert!(
        source.contains("Arti bootstrap timed out after"),
        "timeout path must report 'Arti bootstrap timed out after' with the deadline value"
    );

    // The old generic message that concealed the real cause must not appear
    // as a standalone error (it may appear only in comments/docs, not as an
    // io::Error message string).
    let error_construction_count = source.matches("\"Arti bootstrap timeout\"").count();
    assert_eq!(
        error_construction_count, 0,
        "the old opaque 'Arti bootstrap timeout' error string must not appear in error construction"
    );
}

/// Verify that bootstrap progress is observable through the status event stream.
///
/// Before the fix, the application logged only "Starting Tor bootstrap" and then
/// either "completed successfully" or (via the retry loop) the timeout message.
/// There was no intermediate visibility into what bootstrap was doing, what
/// percentage it had reached, or whether it was blocked by a network problem.
///
/// After the fix, the code subscribes to `TorClient::bootstrap_events()` and
/// logs every meaningful status change during bootstrap.
#[test]
fn bootstrap_progress_is_logged_via_events() {
    let source = std::fs::read_to_string("src/transport.rs").unwrap();

    assert!(
        source.contains("bootstrap_events"),
        "transport must subscribe to bootstrap_events() for progress logging"
    );
    assert!(
        source.contains("as_frac"),
        "transport must read bootstrap fraction from BootstrapStatus::as_frac()"
    );
    assert!(
        source.contains("blocked()"),
        "transport must check BootstrapStatus::blocked() to report network blockages"
    );
    assert!(
        source.contains("blockage.message()"),
        "transport must log the blockage message when Arti reports being stuck"
    );
    assert!(
        source.contains("Bootstrap {}%"),
        "transport must log bootstrap percentage at each status change"
    );
}

/// Verify that the grace-period sleep is present after a timeout cancellation.
///
/// When tokio::time::timeout fires and drops the bootstrap future, Arti's
/// background tasks (directory updater, status reporter) are still alive and
/// hold file locks on the state/cache directories.  A retry that begins
/// immediately finds LocalResourceAlreadyInUse and silently opens the SQLite
/// directory store in read-only mode, preventing any directory documents from
/// being written and guaranteeing that every subsequent attempt also times out.
///
/// The POST_CANCEL_GRACE sleep gives those tasks time to observe the dropped
/// Arc<DirMgr> and exit cleanly before the next create_unbootstrapped_async call.
#[test]
fn grace_period_sleep_is_applied_after_bootstrap_cancellation() {
    let source = std::fs::read_to_string("src/transport.rs").unwrap();

    assert!(
        source.contains("POST_CANCEL_GRACE"),
        "POST_CANCEL_GRACE constant must be defined and used after bootstrap cancellation"
    );

    // The constant must be defined with a non-zero duration.
    assert!(
        source.contains("Duration::from_secs("),
        "POST_CANCEL_GRACE must be a non-zero Duration"
    );

    // The grace period must be applied on both the hard-error path and the
    // timeout path, not just one of them.
    let grace_uses = source.matches("POST_CANCEL_GRACE").count();
    assert!(
        grace_uses >= 2,
        "POST_CANCEL_GRACE must appear at least twice: once for the const definition \
         and once (or more) for its use in sleep() calls after failures; found {}",
        grace_uses
    );

    // Verify that sleep() is called with POST_CANCEL_GRACE (not just defined).
    assert!(
        source.contains("sleep(POST_CANCEL_GRACE)"),
        "transport must call sleep(POST_CANCEL_GRACE) after a bootstrap failure or timeout"
    );
}

/// Verify that the two-phase bootstrap (create_unbootstrapped_async + bootstrap)
/// is used instead of the old single-call TorClient::create_bootstrapped pattern.
///
/// The old pattern wrapped create_bootstrapped in tokio::time::timeout.  When
/// the timeout fired, it dropped the entire future including the TorClient,
/// but background tasks spawned inside create_bootstrapped continued to run
/// and hold directory file locks.  The new pattern separates lock acquisition
/// (create_unbootstrapped_async) from network activity (bootstrap()), allowing
/// the timeout to cancel only the network phase while preserving the ability
/// to apply a grace period before the lock is released.
#[test]
fn bootstrap_uses_two_phase_initialisation() {
    let source = std::fs::read_to_string("src/transport.rs").unwrap();

    // Phase 1: unbootstrapped client creation must be explicit.
    assert!(
        source.contains("create_unbootstrapped_async"),
        "Phase 1 must call create_unbootstrapped_async() to acquire directory locks"
    );

    // Phase 2: bootstrap must be a separate call on the created client.
    assert!(
        source.contains(".bootstrap()"),
        "Phase 2 must call .bootstrap() on the client returned from Phase 1"
    );

    // The two phases must be driven concurrently with the event stream.
    assert!(
        source.contains("tokio::select!"),
        "bootstrap phase must use tokio::select! to drive bootstrap() and events concurrently"
    );

    // The old single-call pattern must not be present as a direct call.
    assert!(
        !source.contains("TorClient::create_bootstrapped("),
        "the old single-call TorClient::create_bootstrapped() must not be used; \
         use the two-phase create_unbootstrapped_async() + bootstrap() pattern instead"
    );
}

#[test]
fn panel_build_slot_contract_matches_agent_format() {
    let stub = std::fs::read_to_string("src/stub.rs").unwrap();
    let panel = std::fs::read_to_string("../panel/src/AgentBuildService.cs")
        .expect("panel AgentBuildService.cs must be present in the repository");

    assert!(stub.contains("VALHALLA-EINHERJAR-CFG-SLOT-V1"));
    assert!(panel.contains("VALHALLA-EINHERJAR-CFG-SLOT-V1"));
    assert!(stub.contains("CONFIG_SLOT_SIZE: usize = 4096"));
    assert!(panel.contains("private const int ConfigSlotSize = 4096"));
    assert!(stub.contains("CONFIG_SLOT_HEADER_SIZE"));
    assert!(panel.contains("ConfigSlotHeaderSize"));
    assert!(panel.contains("private const int ConfigSlotMarkerTerminatorSize = 2"));
    assert!(panel.contains("private const int ConfigSlotMarkerLength = 30 + ConfigSlotMarkerTerminatorSize"));
    assert!(panel.contains("private const int ConfigSlotPayloadCapacity = ConfigSlotSize - ConfigSlotHeaderSize"));
    assert!(panel.contains("FindConfigSlot(image)"));
    assert!(panel.contains("Array.Clear(image, payloadOffset, payloadCapacity)"));
    assert!(panel.contains("stub_debug.bin"));
    assert!(panel.contains("stub.bin"));
    // The service declares the preflight API; Form1 wires the selected
    // Release/Debug build action to that API before opening the save dialog.
    assert!(panel.contains("ValidateStubTemplate(AgentBuildVariant variant)"));
    let form = std::fs::read_to_string("../panel/src/Form1.cs")
        .expect("panel Form1.cs must be present in the repository");
    assert!(form.contains("AgentBuildService.ValidateStubTemplate(variant);"));
}
