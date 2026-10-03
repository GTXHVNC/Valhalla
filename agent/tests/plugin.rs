#[test]
fn abi_contract() {
    let h = std::fs::read_to_string("plugin/valhalla_plugin.h").unwrap();
    for s in ["PluginOnLoad", "PluginOnEvent", "PluginOnUnload", "valhalla_emit"] {
        assert!(h.contains(s));
    }
    for s in ["VALHALLA_EVENT_FILE_SEND_BEGIN", "VALHALLA_EVENT_FILE_SEND_CHUNK", "VALHALLA_EVENT_FILE_SEND_END"] {
        assert!(h.contains(s));
    }
}

#[test]
fn plugin_protocol_contract() {
    let text = std::fs::read_to_string("src/text.rs").unwrap();
    for s in ["PLUGIN_MSG:", "PLUGIN_BEGIN:", "PLUGIN_CHUNK:", "PLUGIN_END:", "PLUGIN_RESUME:"] {
        assert!(text.contains(s), "missing {s}");
    }
    let net = std::fs::read_to_string("src/net.rs").unwrap();
    for s in ["plugins.begin_transfer", "plugins.append_transfer", "plugins.finish_transfer", "plugins.resume_transfer"] {
        assert!(net.contains(s), "missing {s}");
    }
    assert!(text.contains("PLUGIN_OUT:"));
    assert!(net.contains("text::PLUGOUT"));
}
