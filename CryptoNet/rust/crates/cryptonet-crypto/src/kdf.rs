//! Key derivation: HMAC-SHA512, PBKDF2 (BIP-39), scrypt.

use hmac::{Hmac, Mac};
use pbkdf2::pbkdf2;
use sha2::{Sha256, Sha512};

type HmacSha512 = Hmac<Sha512>;

pub type Error = &'static str;

pub fn hmac_sha512(key: &[u8], data: &[u8]) -> [u8; 64] {
    let mut mac = HmacSha512::new_from_slice(key).expect("HMAC accepts keys of any size");
    mac.update(data);
    mac.finalize().into_bytes().into()
}

/// BIP-39 seed: PBKDF2-HMAC-SHA512(mnemonic, "mnemonic" || passphrase, 2048, 64).
/// Inputs must already be NFKD-normalized by the caller.
pub fn mnemonic_to_seed(mnemonic: &[u8], passphrase: &[u8]) -> [u8; 64] {
    let mut salt = Vec::with_capacity(8 + passphrase.len());
    salt.extend_from_slice(b"mnemonic");
    salt.extend_from_slice(passphrase);
    let mut seed = [0u8; 64];
    pbkdf2::<HmacSha512>(mnemonic, &salt, 2048, &mut seed).expect("HMAC accepts keys of any size");
    seed
}

pub fn pbkdf2_sha256(
    password: &[u8],
    salt: &[u8],
    iterations: u32,
    out: &mut [u8],
) -> Result<(), Error> {
    if iterations == 0 {
        return Err("Iterations must be > 0");
    }
    pbkdf2::<Hmac<Sha256>>(password, salt, iterations, out).map_err(|_| "PBKDF2 failed")
}

pub fn pbkdf2_sha512(
    password: &[u8],
    salt: &[u8],
    iterations: u32,
    out: &mut [u8],
) -> Result<(), Error> {
    if iterations == 0 {
        return Err("Iterations must be > 0");
    }
    pbkdf2::<HmacSha512>(password, salt, iterations, out).map_err(|_| "PBKDF2 failed")
}

pub fn scrypt(
    password: &[u8],
    salt: &[u8],
    log_n: u8,
    r: u32,
    p: u32,
    out: &mut [u8],
) -> Result<(), Error> {
    let params = scrypt::Params::new(log_n, r, p, out.len().max(10))
        .map_err(|_| "Invalid scrypt parameters")?;
    scrypt::scrypt(password, salt, &params, out).map_err(|_| "scrypt failed")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn bip39_seed_vector() {
        let seed = mnemonic_to_seed(
            b"abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about",
            b"TREZOR",
        );
        assert_eq!(
            hex::encode(seed),
            "c55257c360c07c72029aebc1b53c05ed0362ada38ead3e3e9efa3708e53495531f09a6987599d18264c1e1c92f2cf141630c7a3c4ab7c81b2f001698e7463b04"
        );
    }

    #[test]
    fn scrypt_rfc7914_vector() {
        let mut out = [0u8; 64];
        scrypt(b"password", b"NaCl", 10, 8, 16, &mut out).unwrap();
        assert_eq!(
            hex::encode(out),
            "fdbabe1c9d3472007856e7190d01e9fe7c6ad7cbc8237830e77376634b3731622eaf30d92e22a3886ff109279d9830dac727afb94a83ee6d8360cbdfa2cc0640"
        );
    }

    #[test]
    fn pbkdf2_sha256_vector() {
        let mut out = [0u8; 32];
        pbkdf2_sha256(b"password", b"salt", 1, &mut out).unwrap();
        assert_eq!(
            hex::encode(out),
            "120fb6cffcf8b32c43e7225256c4f837a86548c92ccc35480805987cb70be17b"
        );
    }
}
