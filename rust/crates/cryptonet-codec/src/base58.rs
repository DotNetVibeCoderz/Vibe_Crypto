//! Base58 and Base58Check (Bitcoin alphabet).

pub type Error = &'static str;

pub fn encode(data: &[u8]) -> String {
    bs58::encode(data).into_string()
}

pub fn decode(s: &str) -> Result<Vec<u8>, Error> {
    bs58::decode(s)
        .into_vec()
        .map_err(|_| "Invalid Base58 string")
}

pub fn encode_check(data: &[u8]) -> String {
    bs58::encode(data).with_check().into_string()
}

pub fn decode_check(s: &str) -> Result<Vec<u8>, Error> {
    bs58::decode(s)
        .with_check(None)
        .into_vec()
        .map_err(|_| "Invalid Base58Check string or checksum mismatch")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn vectors() {
        assert_eq!(encode(b"Hello World!"), "2NEpo7TZRRrLZSi2U");
        assert_eq!(encode(&[0, 0, 1]), "112");
        assert_eq!(decode("112").unwrap(), vec![0, 0, 1]);
        assert!(decode("0OIl").is_err());
    }

    #[test]
    fn check_roundtrip() {
        let payload = hex::decode("00751e76e8199196d454941c45d1b3a323f1433bd6").unwrap();
        let s = encode_check(&payload);
        assert_eq!(s, "1BgGZ9tcN4rm9KBzDn7KprQz87SZ26SAMH");
        assert_eq!(decode_check(&s).unwrap(), payload);
        assert!(decode_check("1BgGZ9tcN4rm9KBzDn7KprQz87SZ26SAMJ").is_err());
    }
}
