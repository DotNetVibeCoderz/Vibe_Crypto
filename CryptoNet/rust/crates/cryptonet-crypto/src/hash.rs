use blake2::digest::{Update, VariableOutput};
use blake2::Blake2bVar;
use ripemd::Ripemd160;
use sha2::{Digest, Sha256, Sha512};
use sha3::Keccak256;

#[inline]
pub fn sha256(data: &[u8]) -> [u8; 32] {
    Sha256::digest(data).into()
}

#[inline]
pub fn double_sha256(data: &[u8]) -> [u8; 32] {
    sha256(&sha256(data))
}

/// BIP-340 tagged hash: `SHA256(SHA256(tag) || SHA256(tag) || msg)`.
pub fn tagged_hash(tag: &str, msg: &[u8]) -> [u8; 32] {
    let tag_hash = sha256(tag.as_bytes());
    let mut h = Sha256::new();
    Digest::update(&mut h, tag_hash);
    Digest::update(&mut h, tag_hash);
    Digest::update(&mut h, msg);
    h.finalize().into()
}

#[inline]
pub fn sha512(data: &[u8]) -> [u8; 64] {
    Sha512::digest(data).into()
}

#[inline]
pub fn keccak256(data: &[u8]) -> [u8; 32] {
    Keccak256::digest(data).into()
}

#[inline]
pub fn ripemd160(data: &[u8]) -> [u8; 20] {
    Ripemd160::digest(data).into()
}

#[inline]
pub fn hash160(data: &[u8]) -> [u8; 20] {
    ripemd160(&sha256(data))
}

/// BLAKE2b with a variable digest length (1..=64 bytes), unkeyed.
pub fn blake2b(data: &[u8], out: &mut [u8]) -> Result<(), &'static str> {
    let mut hasher = Blake2bVar::new(out.len()).map_err(|_| "Invalid BLAKE2b output length")?;
    hasher.update(data);
    hasher
        .finalize_variable(out)
        .map_err(|_| "Invalid BLAKE2b output length")
}

pub fn blake2b_256(data: &[u8]) -> [u8; 32] {
    let mut out = [0u8; 32];
    blake2b(data, &mut out).expect("32 is a valid BLAKE2b length");
    out
}

pub fn blake2b_512(data: &[u8]) -> [u8; 64] {
    let mut out = [0u8; 64];
    blake2b(data, &mut out).expect("64 is a valid BLAKE2b length");
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    fn h(b: &[u8]) -> String {
        hex::encode(b)
    }

    #[test]
    fn sha256_vectors() {
        assert_eq!(
            h(&sha256(b"")),
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        );
        assert_eq!(
            h(&sha256(b"abc")),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
    }

    #[test]
    fn keccak256_vectors() {
        assert_eq!(
            h(&keccak256(b"")),
            "c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"
        );
    }

    #[test]
    fn ripemd160_vectors() {
        assert_eq!(
            h(&ripemd160(b"")),
            "9c1185a5c5e9fc54612808977ee8f548b2258d31"
        );
        assert_eq!(
            h(&ripemd160(b"abc")),
            "8eb208f7e05d987a9b044a8e98c6b087f15a0bfc"
        );
    }

    #[test]
    fn blake2b_vectors() {
        assert_eq!(
            h(&blake2b_256(b"")),
            "0e5751c026e543b2e8ab2eb06099daa1d1e5df47778f7787faab45cdf12fe3a8"
        );
        assert_eq!(
            h(&blake2b_512(b"")),
            "786a02f742015903c6c6fd852552d272912f4740e15847618a86e217f71f5419d25e1031afee585313896444934eb04b903a685b1448b755d56f701afe9be2ce"
        );
        assert!(blake2b(b"", &mut [0u8; 65]).is_err());
    }
}
