//! Substrate key schemes: sr25519 (schnorrkel, signing context "substrate") and the
//! Substrate `//hard` / `/soft` derivation paths for sr25519 and ed25519.

use schnorrkel::derive::{ChainCode, Derivation};
use schnorrkel::{ExpansionMode, Keypair, MiniSecretKey, PublicKey, SecretKey, Signature};
use zeroize::Zeroize;

use crate::hash::blake2b_256;

pub type Error = &'static str;

const SIGNING_CTX: &[u8] = b"substrate";

/// A parsed derivation junction: `(hard, chain_code)`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Junction {
    pub hard: bool,
    pub chain_code: [u8; 32],
}

fn compact_len_prefix(len: usize, out: &mut Vec<u8>) {
    // SCALE compact encoding for the string length.
    if len < 1 << 6 {
        out.push((len as u8) << 2);
    } else if len < 1 << 14 {
        out.extend_from_slice(&(((len as u16) << 2) | 0b01).to_le_bytes());
    } else if len < 1 << 30 {
        out.extend_from_slice(&(((len as u32) << 2) | 0b10).to_le_bytes());
    } else {
        let bytes = (len as u64).to_le_bytes();
        let n = 8 - bytes.iter().rev().take_while(|b| **b == 0).count();
        out.push((((n - 4) as u8) << 2) | 0b11);
        out.extend_from_slice(&bytes[..n]);
    }
}

fn junction_chain_code(segment: &str) -> [u8; 32] {
    let mut cc = [0u8; 32];
    if let Ok(n) = segment.parse::<u64>() {
        cc[..8].copy_from_slice(&n.to_le_bytes());
        return cc;
    }
    let mut encoded = Vec::with_capacity(segment.len() + 5);
    compact_len_prefix(segment.len(), &mut encoded);
    encoded.extend_from_slice(segment.as_bytes());
    if encoded.len() > 32 {
        cc = blake2b_256(&encoded);
    } else {
        cc[..encoded.len()].copy_from_slice(&encoded);
    }
    cc
}

/// Parses a Substrate derivation path such as `//polkadot//0/soft`.
pub fn parse_path(path: &str) -> Result<Vec<Junction>, Error> {
    let mut out = Vec::new();
    let mut rest = path;
    while !rest.is_empty() {
        let hard = if let Some(r) = rest.strip_prefix("//") {
            rest = r;
            true
        } else if let Some(r) = rest.strip_prefix('/') {
            rest = r;
            false
        } else {
            return Err("Derivation path junctions must start with '/' or '//'");
        };
        let end = rest.find('/').unwrap_or(rest.len());
        let segment = &rest[..end];
        if segment.is_empty() {
            return Err("Empty derivation junction");
        }
        out.push(Junction {
            hard,
            chain_code: junction_chain_code(segment),
        });
        rest = &rest[end..];
    }
    Ok(out)
}

fn keypair_from_secret(secret: &[u8]) -> Result<Keypair, Error> {
    let sk = SecretKey::from_bytes(secret).map_err(|_| "sr25519 secret key must be 64 bytes")?;
    Ok(sk.to_keypair())
}

/// Expands a 32-byte mini secret (Substrate "secret seed") to a 64-byte secret key + public key.
pub fn keypair_from_seed(seed: &[u8]) -> Result<([u8; 64], [u8; 32]), Error> {
    let mini = MiniSecretKey::from_bytes(seed).map_err(|_| "sr25519 seed must be 32 bytes")?;
    let kp = mini.expand_to_keypair(ExpansionMode::Ed25519);
    Ok((kp.secret.to_bytes(), kp.public.to_bytes()))
}

/// Applies a Substrate derivation path to a 64-byte sr25519 secret key.
pub fn derive(secret: &[u8], path: &str) -> Result<([u8; 64], [u8; 32]), Error> {
    let mut kp = keypair_from_secret(secret)?;
    for j in parse_path(path)? {
        kp = if j.hard {
            let (mini, _) = kp
                .secret
                .hard_derive_mini_secret_key(Some(ChainCode(j.chain_code)), b"");
            mini.expand_to_keypair(ExpansionMode::Ed25519)
        } else {
            kp.derived_key_simple(ChainCode(j.chain_code), []).0
        };
    }
    Ok((kp.secret.to_bytes(), kp.public.to_bytes()))
}

pub fn public_key(secret: &[u8]) -> Result<[u8; 32], Error> {
    Ok(keypair_from_secret(secret)?.public.to_bytes())
}

pub fn sign(secret: &[u8], message: &[u8]) -> Result<[u8; 64], Error> {
    let kp = keypair_from_secret(secret)?;
    Ok(kp.sign_simple(SIGNING_CTX, message).to_bytes())
}

pub fn verify(public: &[u8], message: &[u8], signature: &[u8]) -> bool {
    let Ok(pk) = PublicKey::from_bytes(public) else {
        return false;
    };
    let Ok(sig) = Signature::from_bytes(signature) else {
        return false;
    };
    pk.verify_simple(SIGNING_CTX, message, &sig).is_ok()
}

/// Substrate ed25519 hard derivation: `seed' = blake2_256(SCALE("Ed25519HDKD", seed, cc))`.
/// Soft junctions are not supported by ed25519.
pub fn ed25519_derive(seed: &[u8], path: &str) -> Result<[u8; 32], Error> {
    let mut current: [u8; 32] = seed
        .try_into()
        .map_err(|_| "ed25519 seed must be 32 bytes")?;
    for j in parse_path(path)? {
        if !j.hard {
            current.zeroize();
            return Err("ed25519 supports only hard (//) junctions");
        }
        let mut buf = Vec::with_capacity(1 + 11 + 32 + 32);
        compact_len_prefix(11, &mut buf);
        buf.extend_from_slice(b"Ed25519HDKD");
        buf.extend_from_slice(&current);
        buf.extend_from_slice(&j.chain_code);
        current = blake2b_256(&buf);
        buf.zeroize();
    }
    Ok(current)
}

#[cfg(test)]
mod tests {
    use super::*;

    // Substrate DEV_PHRASE "bottom drive obey lake curtain smoke basket hold race lonely fit walk"
    const DEV_SEED: &str = "fac7959dbfe72f052e5a0c3c8d6530f202b02fd8f9f5ca3580ec8deb7797479e";
    const ALICE_SR25519_PUB: &str =
        "d43593c715fdd31c61141abd04a99fd6822c8558854ccde39a5684e7a56da27d";

    #[test]
    fn alice_sr25519() {
        let (root, _) = keypair_from_seed(&hex::decode(DEV_SEED).unwrap()).unwrap();
        let (secret, public) = derive(&root, "//Alice").unwrap();
        assert_eq!(hex::encode(public), ALICE_SR25519_PUB);

        let alice_seed =
            hex::decode("e5be9a5092b81bca64be81d212e7f2f9eba183bb7a90954f7b76361f6edb5c0a")
                .unwrap();
        let (_, p2) = keypair_from_seed(&alice_seed).unwrap();
        assert_eq!(hex::encode(p2), ALICE_SR25519_PUB);

        let sig = sign(&secret, b"hello").unwrap();
        assert!(verify(&public, b"hello", &sig));
        assert!(!verify(&public, b"hellO", &sig));
    }

    #[test]
    fn alice_ed25519() {
        let seed = ed25519_derive(&hex::decode(DEV_SEED).unwrap(), "//Alice").unwrap();
        let pk = crate::ed25519::derive_public_key(&seed).unwrap();
        assert_eq!(
            hex::encode(pk),
            "88dc3417d5058ec4b4503e0c12ea1a0a89be200fe98922423d4334014fa6b0ee"
        );
    }

    #[test]
    fn path_parsing() {
        let p = parse_path("//polkadot/0//long-junction-name-exceeding-thirty-two-bytes").unwrap();
        assert_eq!(p.len(), 3);
        assert!(p[0].hard && !p[1].hard && p[2].hard);
        assert_eq!(p[1].chain_code, [0u8; 32]);
        assert!(parse_path("Alice").is_err());
        assert!(parse_path("///").is_err());
    }
}
