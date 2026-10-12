//! X25519 (RFC 7748) Diffie-Hellman, used by WalletConnect v2 key agreement.

use curve25519_dalek::montgomery::MontgomeryPoint;

/// `X25519(scalar, u)`; the scalar is clamped as RFC 7748 requires.
pub fn x25519(scalar: &[u8], u: &[u8]) -> Result<[u8; 32], &'static str> {
    let k: [u8; 32] = scalar.try_into().map_err(|_| "Scalar must be 32 bytes")?;
    let u: [u8; 32] = u.try_into().map_err(|_| "Point must be 32 bytes")?;
    Ok(MontgomeryPoint(u).mul_clamped(k).to_bytes())
}

/// Public key: `X25519(scalar, 9)`.
pub fn public_key(scalar: &[u8]) -> Result<[u8; 32], &'static str> {
    let mut base = [0u8; 32];
    base[0] = 9;
    x25519(scalar, &base)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn h(s: &str) -> Vec<u8> {
        hex::decode(s).unwrap()
    }

    #[test]
    fn rfc7748_vectors() {
        let r = x25519(
            &h("a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4"),
            &h("e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c"),
        )
        .unwrap();
        assert_eq!(
            hex::encode(r),
            "c3da55379de9c6908e94ea4df28d084f32eccf03491c71f754b4075577a28552"
        );
        let alice = h("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        let bob = h("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");
        assert_eq!(
            hex::encode(public_key(&alice).unwrap()),
            "8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a"
        );
        let shared = x25519(&alice, &public_key(&bob).unwrap()).unwrap();
        assert_eq!(
            hex::encode(shared),
            "4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742"
        );
        assert_eq!(shared, x25519(&bob, &public_key(&alice).unwrap()).unwrap());
    }
}
