//! Bech32 / Bech32m (BIP-173 / BIP-350) and SegWit address helpers.

use bech32::{self, u5, FromBase32, ToBase32, Variant};

pub type Error = &'static str;

/// Encodes raw bytes (8-bit, converted to 5-bit groups), e.g. Cosmos addresses.
pub fn encode(hrp: &str, data: &[u8], bech32m: bool) -> Result<String, Error> {
    let v = if bech32m {
        Variant::Bech32m
    } else {
        Variant::Bech32
    };
    bech32::encode(hrp, data.to_base32(), v).map_err(|_| "Failed to encode bech32")
}

/// Decodes to `(hrp, bytes, is_bech32m)`.
pub fn decode(s: &str) -> Result<(String, Vec<u8>, bool), Error> {
    let (hrp, data, variant) = bech32::decode(s).map_err(|_| "Invalid bech32 string")?;
    let bytes = Vec::<u8>::from_base32(&data).map_err(|_| "Invalid bech32 padding")?;
    Ok((hrp, bytes, matches!(variant, Variant::Bech32m)))
}

/// Encodes a SegWit address. Version 0 uses Bech32, versions 1..=16 use Bech32m.
pub fn segwit_encode(hrp: &str, version: u8, program: &[u8]) -> Result<String, Error> {
    validate_program(version, program)?;
    let mut data = Vec::with_capacity(1 + (program.len() * 8).div_ceil(5));
    data.push(u5::try_from_u8(version).map_err(|_| "Invalid witness version")?);
    data.extend(program.to_base32());
    let variant = if version == 0 {
        Variant::Bech32
    } else {
        Variant::Bech32m
    };
    bech32::encode(hrp, data, variant).map_err(|_| "Failed to encode segwit address")
}

/// Decodes a SegWit address to `(hrp, version, program)`, enforcing BIP-350 variant rules.
pub fn segwit_decode(s: &str) -> Result<(String, u8, Vec<u8>), Error> {
    if s.len() > 90 {
        return Err("SegWit address exceeds 90 characters");
    }
    let (hrp, data, variant) = bech32::decode(s).map_err(|_| "Invalid bech32 string")?;
    let (first, rest) = data.split_first().ok_or("Empty witness data")?;
    let version = first.to_u8();
    let program = Vec::<u8>::from_base32(rest).map_err(|_| "Invalid witness program padding")?;
    let expected = if version == 0 {
        Variant::Bech32
    } else {
        Variant::Bech32m
    };
    if variant != expected {
        return Err("Wrong bech32 variant for witness version");
    }
    validate_program(version, &program)?;
    Ok((hrp, version, program))
}

fn validate_program(version: u8, program: &[u8]) -> Result<(), Error> {
    if version > 16 {
        return Err("Witness version must be 0..=16");
    }
    if program.len() < 2 || program.len() > 40 {
        return Err("Witness program must be 2..=40 bytes");
    }
    if version == 0 && program.len() != 20 && program.len() != 32 {
        return Err("Witness v0 program must be 20 or 32 bytes");
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn bip173_p2wpkh() {
        let program = hex::decode("751e76e8199196d454941c45d1b3a323f1433bd6").unwrap();
        let addr = segwit_encode("bc", 0, &program).unwrap();
        assert_eq!(addr, "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4");
        assert_eq!(
            segwit_decode(&addr).unwrap(),
            ("bc".to_string(), 0, program)
        );
    }

    #[test]
    fn bip350_p2tr() {
        let program =
            hex::decode("a60869f0dbcf1dc659c9cecbaf8050135ea9e8cdc487053f1dc6880949dc684c")
                .unwrap();
        let addr = segwit_encode("bc", 1, &program).unwrap();
        assert_eq!(
            addr,
            "bc1p5cyxnuxmeuwuvkwfem96lqzszd02n6xdcjrs20cac6yqjjwudpxqkedrcr"
        );
        assert_eq!(segwit_decode(&addr).unwrap().1, 1);
    }

    #[test]
    fn rejects_bad_checksum_and_variant() {
        assert!(segwit_decode("bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t5").is_err());
        // v1 program encoded with plain Bech32 must be rejected (BIP-350)
        let program =
            hex::decode("a60869f0dbcf1dc659c9cecbaf8050135ea9e8cdc487053f1dc6880949dc684c")
                .unwrap();
        let mut data = vec![u5::try_from_u8(1).unwrap()];
        data.extend(program.to_base32());
        let wrong = bech32::encode("bc", data, Variant::Bech32).unwrap();
        assert!(segwit_decode(&wrong).is_err());
    }
}
