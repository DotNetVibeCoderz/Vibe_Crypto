//! Ed25519 (RFC 8032) signing and SLIP-10 ed25519 derivation.

use ed25519_dalek::{Signature, Signer, SigningKey, Verifier, VerifyingKey};
use zeroize::Zeroize;

use crate::kdf::hmac_sha512;

pub type Error = &'static str;

fn signing_key(secret: &[u8]) -> Result<SigningKey, Error> {
    let mut seed: [u8; 32] = secret
        .try_into()
        .map_err(|_| "Secret key must be 32 bytes")?;
    let key = SigningKey::from_bytes(&seed);
    seed.zeroize();
    Ok(key)
}

pub fn derive_public_key(secret: &[u8]) -> Result<[u8; 32], Error> {
    Ok(signing_key(secret)?.verifying_key().to_bytes())
}

pub fn sign(secret: &[u8], message: &[u8]) -> Result<[u8; 64], Error> {
    Ok(signing_key(secret)?.sign(message).to_bytes())
}

pub fn verify(public_key: &[u8], message: &[u8], signature: &[u8]) -> bool {
    let Ok(pk) = <[u8; 32]>::try_from(public_key) else {
        return false;
    };
    let Ok(sig) = <[u8; 64]>::try_from(signature) else {
        return false;
    };
    let Ok(vk) = VerifyingKey::from_bytes(&pk) else {
        return false;
    };
    vk.verify(message, &Signature::from_bytes(&sig)).is_ok()
}

/// SLIP-10 ed25519 CKDpriv (hardened only; the hardened bit is forced on).
pub fn slip10_ckd_priv(
    parent_key: &[u8],
    parent_chain_code: &[u8],
    index: u32,
) -> Result<([u8; 32], [u8; 32]), Error> {
    if parent_key.len() != 32 || parent_chain_code.len() != 32 {
        return Err("Key and chain code must be 32 bytes");
    }
    let index = index | 0x8000_0000;
    let mut data = [0u8; 37];
    data[1..33].copy_from_slice(parent_key);
    data[33..].copy_from_slice(&index.to_be_bytes());
    let mut i = hmac_sha512(parent_chain_code, &data);
    data.zeroize();
    let mut k = [0u8; 32];
    let mut c = [0u8; 32];
    k.copy_from_slice(&i[..32]);
    c.copy_from_slice(&i[32..]);
    i.zeroize();
    Ok((k, c))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rfc8032_test_1() {
        let sk = hex::decode("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60")
            .unwrap();
        let pk = derive_public_key(&sk).unwrap();
        assert_eq!(
            hex::encode(pk),
            "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a"
        );
        let sig = sign(&sk, b"").unwrap();
        assert_eq!(
            hex::encode(sig),
            "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b"
        );
        assert!(verify(&pk, b"", &sig));
        assert!(!verify(&pk, b"x", &sig));
    }

    #[test]
    fn slip10_vector_1() {
        let seed = hex::decode("000102030405060708090a0b0c0d0e0f").unwrap();
        let i = hmac_sha512(b"ed25519 seed", &seed);
        let (mut k, mut c) = (i[..32].to_vec(), i[32..].to_vec());
        assert_eq!(
            hex::encode(&k),
            "2b4be7f19ee27bbf30c667b642d5f4aa69fd169872f8fc3059c08ebae2eb19e7"
        );
        for idx in [0u32, 1, 2, 2, 1_000_000_000] {
            let (nk, nc) = slip10_ckd_priv(&k, &c, idx).unwrap();
            k = nk.to_vec();
            c = nc.to_vec();
        }
        assert_eq!(
            hex::encode(&k),
            "8f94d394a8e8fd6b1bc2f3f49f5c47e385281d5c17e65324b0f62483e37e8793"
        );
        assert_eq!(
            hex::encode(derive_public_key(&k).unwrap()),
            "3c24da049451555d51a7014a37337aa4e12d41e485abccfa46b47dfb2af54b7a"
        );
    }
}
