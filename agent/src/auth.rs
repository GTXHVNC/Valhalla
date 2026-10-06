use base64::{engine::general_purpose::STANDARD, Engine as _};
use ed25519_dalek::{Signer, SigningKey};

use crate::{telemetry, transport::ClientIdentity};

pub fn signing_key() -> SigningKey {
    telemetry::signing_key()
}

pub fn sign_challenge(identity: &ClientIdentity<'_>, nonce: &[u8], key: &SigningKey) -> String {
    STANDARD.encode(key.sign(&valhalla_protocol::auth_message(identity.fingerprint, nonce)).to_bytes())
}

pub fn public_key_hex(key: &SigningKey) -> String {
    key.verifying_key().to_bytes().iter().map(|b| format!("{b:02x}")).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn signing_key_matches_identity_derivation() {
        let key = signing_key();
        let public = public_key_hex(&key);
        assert_eq!(public.len(), 64);
        assert_eq!(telemetry::fingerprint(), {
            use sha2::{Digest, Sha256};
            Sha256::digest(key.verifying_key().to_bytes())
                .iter()
                .map(|byte| format!("{byte:02x}"))
                .collect::<String>()
        });
    }
}
