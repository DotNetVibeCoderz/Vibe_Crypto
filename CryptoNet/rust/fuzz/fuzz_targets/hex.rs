#![no_main]
use cryptonet_codec::hex;
use libfuzzer_sys::fuzz_target;

fuzz_target!(|data: &[u8]| {
    assert_eq!(hex::decode(&hex::encode(data)).unwrap(), data);
    if let Ok(s) = std::str::from_utf8(data) {
        let _ = hex::decode(s);
    }
});
