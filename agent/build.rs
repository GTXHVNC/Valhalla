use std::{env, fs, path::PathBuf};

const MAGIC: &[u8] = b"VLHCFG\x00\x01";
const SLOT_TAG: &[u8] = b"VALHALLA-CFG-SLOT-V1\x00";
const CONFIG_REGION_SIZE: usize = 4096;

fn main() {
    assert!(MAGIC.len() + SLOT_TAG.len() < CONFIG_REGION_SIZE);

    let mut region = vec![0u8; CONFIG_REGION_SIZE];
    region[..MAGIC.len()].copy_from_slice(MAGIC);
    let tag_start = MAGIC.len();
    region[tag_start..tag_start + SLOT_TAG.len()].copy_from_slice(SLOT_TAG);

    let out_dir = PathBuf::from(env::var_os("OUT_DIR").expect("OUT_DIR must be set by Cargo"));
    fs::write(out_dir.join("valhalla_config_region.bin"), region)
        .expect("failed to write embedded configuration region");

    println!("cargo:rerun-if-changed=build.rs");
}
