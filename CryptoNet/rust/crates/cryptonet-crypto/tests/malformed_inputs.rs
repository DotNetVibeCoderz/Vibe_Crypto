//! Every public function must reject malformed lengths with an error (or `false`), never panic.
use cryptonet_crypto::{ed25519, secp256k1, sr25519};

const LENGTHS: [usize; 9] = [0, 1, 31, 32, 33, 63, 64, 65, 100];

#[test]
fn secp256k1_rejects_bad_lengths() {
    for &a in &LENGTHS {
        for &b in &LENGTHS {
            let x = vec![7u8; a];
            let y = vec![9u8; b];
            let _ = secp256k1::convert_public_key(&x, true);
            let _ = secp256k1::derive_public_key(&x, true);
            let _ = secp256k1::sign_recoverable(&x, &y);
            let _ = secp256k1::verify(&x, &y, &y);
            let _ = secp256k1::recover_public_key(&x, &y, 0, true);
            let _ = secp256k1::xonly_public_key(&x);
            let _ = secp256k1::schnorr_verify(&x, &y, &y);
            let _ = secp256k1::schnorr_sign(&x, &y, &[1u8; 32]);
            let _ = secp256k1::taproot_tweak_public_key(&x, &y);
            let _ = secp256k1::taproot_tweak_secret_key(&x, &y);
        }
    }
}

#[test]
fn ed25519_and_sr25519_reject_bad_lengths() {
    for &a in &LENGTHS {
        for &b in &LENGTHS {
            let x = vec![7u8; a];
            let y = vec![9u8; b];
            let _ = ed25519::derive_public_key(&x);
            let _ = ed25519::sign(&x, &y);
            let _ = ed25519::verify(&x, &y, &y);
            let _ = sr25519::keypair_from_seed(&x);
            let _ = sr25519::public_key(&x);
            let _ = sr25519::sign(&x, &y);
            let _ = sr25519::verify(&x, &y, &y);
        }
    }
}
