#![no_main]
use cryptonet_crypto::{ed25519, secp256k1, sr25519};
use libfuzzer_sys::fuzz_target;

// Verification and key parsing must never panic on attacker-controlled public keys, signatures or digests.
fuzz_target!(|data: &[u8]| {
    let split = |n: usize| data.get(..n.min(data.len())).unwrap_or_default();
    let key33 = split(33);
    let rest = data.get(33..).unwrap_or_default();
    let digest = rest.get(..32.min(rest.len())).unwrap_or_default();
    let sig = rest.get(32..).unwrap_or_default();

    let _ = secp256k1::convert_public_key(key33, false);
    let _ = secp256k1::convert_public_key(data, true);
    let _ = secp256k1::verify(key33, digest, sig);
    if let (Some(&rid), true) = (data.first(), sig.len() >= 64) {
        let _ = secp256k1::recover_public_key(digest, &sig[..64], rid % 4, true);
    }
    let _ = secp256k1::schnorr_verify(split(32), digest, sig);
    let _ = secp256k1::taproot_tweak_public_key(split(32), digest);
    let _ = ed25519::verify(split(32), digest, sig);
    let _ = sr25519::verify(split(32), digest, sig);
});
