#![no_main]
use cryptonet_codec::bech32_codec;
use libfuzzer_sys::fuzz_target;

fuzz_target!(|data: &[u8]| {
    if let Ok(s) = std::str::from_utf8(data) {
        if let Ok((hrp, payload, bech32m)) = bech32_codec::decode(s) {
            let again =
                bech32_codec::encode(&hrp, &payload, bech32m).expect("re-encode decoded bech32");
            assert_eq!(again.to_lowercase(), s.to_lowercase());
        }
        if let Ok((hrp, version, program)) = bech32_codec::segwit_decode(s) {
            let again = bech32_codec::segwit_encode(&hrp, version, &program)
                .expect("re-encode decoded segwit");
            assert_eq!(again.to_lowercase(), s.to_lowercase());
        }
    }
    // Program bytes of any length either encode as SegWit or are rejected.
    if let Some((&version, program)) = data.split_first() {
        if let Ok(address) = bech32_codec::segwit_encode("bc", version % 17, program) {
            let (_, v, p) = bech32_codec::segwit_decode(&address).expect("decode encoded segwit");
            assert_eq!((v, p.as_slice()), (version % 17, program));
        }
    }
});
