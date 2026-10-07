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
    assert!(source.contains("TorClient::create_bootstrapped"));
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
