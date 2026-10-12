#![no_main]
use cryptonet_codec::base58;
use libfuzzer_sys::fuzz_target;

fuzz_target!(|data: &[u8]| {
    // Arbitrary bytes round-trip through encode/decode.
    assert_eq!(base58::decode(&base58::encode(data)).unwrap(), data);
    assert_eq!(
        base58::decode_check(&base58::encode_check(data)).unwrap(),
        data
    );
    // Arbitrary strings must decode or fail cleanly, and a decoded value re-encodes to the same string.
    if let Ok(s) = std::str::from_utf8(data) {
        if let Ok(bytes) = base58::decode(s) {
            assert_eq!(base58::encode(&bytes), s);
        }
        let _ = base58::decode_check(s);
    }
});
