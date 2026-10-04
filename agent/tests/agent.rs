#[test]
fn command_contract() {
    let source = std::fs::read_to_string("src/text.rs").unwrap();
    for command in [
        "SLEEP", "HIBERNATE", "RESTART", "SHUTDOWN", "RECONNECT", "CLOSE", "EXECUTE", "UPDATE",
    ] {
        assert!(source.contains(command));
    }
    for token in [
        "UPDATE_BEGIN:", "UPDATE_CHUNK:", "UPDATE_END:", "HELLO:FINGERPRINT:", "DATA:",
        "PLUGIN:", "PLUGIN_EVENT:", "PLUGIN_OUT:", "DIRECT_CONNECT:", "DIRECT_DISCONNECT", "PLUGIN_RESUME:",
    ] {
        assert!(source.contains(token), "missing protocol token: {token}");
    }
}

#[test]
fn transport_contract() {
    let source = std::fs::read_to_string("src/transport.rs").unwrap();
    assert!(source.contains("TorClient::create_bootstrapped"));
    assert!(source.contains("tor.connect"));
    assert!(source.contains("client_async_with_config"));
    assert!(source.contains("MAX_WS_MESSAGE_BYTES"));
    assert!(source.contains("match &endpoint"));
    assert!(!source.contains("match endpoint {"));

    let args = std::fs::read_to_string("src/args.rs").unwrap();
    assert!(!args.contains("VALHALLA_ENDPOINT"));
    assert!(args.contains("stub::load_config"));
    assert!(args.contains("VALHALLA_AUTH_KEY_FILE"));
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
    for token in ["--valhalla-update-successor", "PROBE_WAIT", "UPDATE_FINAL_READY:", "request_helper_self_delete"] {
        assert!(update.contains(token), "missing update token: {token}");
    }
}

#[test]
fn runtime_stays_multithreaded() {
    let main = std::fs::read_to_string("src/main.rs").unwrap();
    assert!(main.contains("new_multi_thread"));
    assert!(main.contains("worker_threads(2)"));
}
