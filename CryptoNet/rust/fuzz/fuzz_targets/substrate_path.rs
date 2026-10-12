#![no_main]
use cryptonet_crypto::sr25519;
use libfuzzer_sys::fuzz_target;

fuzz_target!(|data: &[u8]| {
    if let Ok(path) = std::str::from_utf8(data) {
        let _ = sr25519::parse_path(path);
        let _ = sr25519::derive(&[7u8; 64], path);
    }
});
