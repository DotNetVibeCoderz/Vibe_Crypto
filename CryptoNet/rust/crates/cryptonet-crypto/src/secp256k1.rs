//! secp256k1: ECDSA (RFC 6979, low-S, recoverable), BIP-340 Schnorr, BIP-341 Taproot tweaks
//! and BIP-32 private child key derivation.

use k256::ecdsa::{
    signature::hazmat::PrehashVerifier, RecoveryId, Signature, SigningKey, VerifyingKey,
};
use k256::elliptic_curve::ops::Reduce;
use k256::elliptic_curve::point::AffineCoordinates;
use k256::elliptic_curve::sec1::ToEncodedPoint;
use k256::elliptic_curve::PrimeField;
use k256::{schnorr, AffinePoint, ProjectivePoint, PublicKey, Scalar, SecretKey, U256};
use zeroize::Zeroize;

use crate::hash::tagged_hash;
use crate::kdf::hmac_sha512;

pub type Error = &'static str;

fn secret_key(bytes: &[u8]) -> Result<SecretKey, Error> {
    if bytes.len() != 32 {
        return Err("Secret key must be 32 bytes");
    }
    SecretKey::from_slice(bytes).map_err(|_| "Invalid secp256k1 secret key")
}

fn scalar_from_bytes_checked(bytes: &[u8; 32]) -> Option<Scalar> {
    Option::from(Scalar::from_repr((*bytes).into()))
}

pub fn derive_public_key(secret_key_bytes: &[u8], compressed: bool) -> Result<Vec<u8>, Error> {
    let sk = secret_key(secret_key_bytes)?;
    Ok(sk
        .public_key()
        .to_encoded_point(compressed)
        .as_bytes()
        .to_vec())
}

/// Converts any SEC1 public key (33 or 65 bytes) to the requested encoding.
pub fn convert_public_key(public_key: &[u8], compressed: bool) -> Result<Vec<u8>, Error> {
    let pk = PublicKey::from_sec1_bytes(public_key).map_err(|_| "Invalid secp256k1 public key")?;
    Ok(pk.to_encoded_point(compressed).as_bytes().to_vec())
}

pub fn sign_recoverable(secret_key_bytes: &[u8], digest: &[u8]) -> Result<([u8; 64], u8), Error> {
    if digest.len() != 32 {
        return Err("Digest must be exactly 32 bytes");
    }
    let signing_key = SigningKey::from(secret_key(secret_key_bytes)?);
    let (sig, rec_id): (Signature, RecoveryId) = signing_key
        .sign_prehash_recoverable(digest)
        .map_err(|_| "Failed to sign prehash")?;
    // k256 already produces low-S signatures (BIP-62 / EIP-2); the recovery id matches that S.
    let mut out = [0u8; 64];
    out.copy_from_slice(&sig.to_bytes());
    Ok((out, rec_id.to_byte()))
}

pub fn verify(public_key_bytes: &[u8], digest: &[u8], signature_bytes: &[u8]) -> bool {
    if digest.len() != 32 || signature_bytes.len() != 64 {
        return false;
    }
    let Ok(vk) = VerifyingKey::from_sec1_bytes(public_key_bytes) else {
        return false;
    };
    let Ok(sig) = Signature::from_slice(signature_bytes) else {
        return false;
    };
    vk.verify_prehash(digest, &sig).is_ok()
}

pub fn recover_public_key(
    digest: &[u8],
    signature_bytes: &[u8],
    recovery_id: u8,
    compressed: bool,
) -> Result<Vec<u8>, Error> {
    if digest.len() != 32 || signature_bytes.len() != 64 {
        return Err("Invalid digest or signature length");
    }
    let sig = Signature::from_slice(signature_bytes).map_err(|_| "Invalid signature format")?;
    let rec_id = RecoveryId::from_byte(recovery_id).ok_or("Invalid recovery ID")?;
    let vk = VerifyingKey::recover_from_prehash(digest, &sig, rec_id)
        .map_err(|_| "Failed to recover public key")?;
    Ok(vk.to_encoded_point(compressed).as_bytes().to_vec())
}

// ---------------------------------------------------------------- BIP-340 Schnorr

/// Returns the 32-byte x-only public key for a secret key.
pub fn xonly_public_key(secret_key_bytes: &[u8]) -> Result<[u8; 32], Error> {
    let sk = schnorr::SigningKey::from_bytes(secret_key_bytes)
        .map_err(|_| "Invalid secp256k1 secret key")?;
    Ok(sk.verifying_key().to_bytes().into())
}

pub fn schnorr_sign(
    secret_key_bytes: &[u8],
    msg: &[u8],
    aux_rand: &[u8; 32],
) -> Result<[u8; 64], Error> {
    let sk = schnorr::SigningKey::from_bytes(secret_key_bytes)
        .map_err(|_| "Invalid secp256k1 secret key")?;
    let sig = sk
        .sign_raw(msg, aux_rand)
        .map_err(|_| "Schnorr signing failed")?;
    Ok(sig.to_bytes())
}

pub fn schnorr_verify(xonly_pubkey: &[u8], msg: &[u8], signature: &[u8]) -> bool {
    let Ok(vk) = schnorr::VerifyingKey::from_bytes(xonly_pubkey) else {
        return false;
    };
    let Ok(sig) = schnorr::Signature::try_from(signature) else {
        return false;
    };
    vk.verify_raw(msg, &sig).is_ok()
}

fn lift_x(xonly: &[u8]) -> Result<ProjectivePoint, Error> {
    if xonly.len() != 32 {
        return Err("x-only public key must be 32 bytes");
    }
    let mut sec1 = [0u8; 33];
    sec1[0] = 0x02;
    sec1[1..].copy_from_slice(xonly);
    let pk = PublicKey::from_sec1_bytes(&sec1).map_err(|_| "Invalid x-only public key")?;
    Ok(pk.to_projective())
}

fn taptweak_scalar(xonly: &[u8], merkle_root: &[u8]) -> Result<Scalar, Error> {
    if !(merkle_root.is_empty() || merkle_root.len() == 32) {
        return Err("Merkle root must be empty or 32 bytes");
    }
    let mut msg = Vec::with_capacity(64);
    msg.extend_from_slice(xonly);
    msg.extend_from_slice(merkle_root);
    let t = tagged_hash("TapTweak", &msg);
    scalar_from_bytes_checked(&t).ok_or("Taproot tweak out of range")
}

/// BIP-341 output key: `Q = lift_x(P) + int(hash_TapTweak(P || merkle_root))·G`.
/// Returns the x-only output key and its y parity (0 = even, 1 = odd).
pub fn taproot_tweak_public_key(
    internal_xonly: &[u8],
    merkle_root: &[u8],
) -> Result<([u8; 32], u8), Error> {
    let p = lift_x(internal_xonly)?;
    let t = taptweak_scalar(internal_xonly, merkle_root)?;
    let q = (p + ProjectivePoint::GENERATOR * t).to_affine();
    if q == AffinePoint::IDENTITY {
        return Err("Taproot output key is the point at infinity");
    }
    let parity = if bool::from(q.y_is_odd()) { 1 } else { 0 };
    Ok((q.x().into(), parity))
}

/// BIP-341 tweaked secret key used for key-path spending.
pub fn taproot_tweak_secret_key(
    secret_key_bytes: &[u8],
    merkle_root: &[u8],
) -> Result<[u8; 32], Error> {
    let sk = secret_key(secret_key_bytes)?;
    let mut d: Scalar = *sk.to_nonzero_scalar();
    let p = (ProjectivePoint::GENERATOR * d).to_affine();
    if bool::from(p.y_is_odd()) {
        d = -d;
    }
    let xonly: [u8; 32] = p.x().into();
    let t = taptweak_scalar(&xonly, merkle_root)?;
    let tweaked = d + t;
    if bool::from(tweaked.is_zero()) {
        return Err("Tweaked secret key is zero");
    }
    Ok(tweaked.to_bytes().into())
}

// ---------------------------------------------------------------- BIP-32

/// BIP-32 CKDpriv. Returns `(child_key, child_chain_code)`.
pub fn bip32_ckd_priv(
    parent_key: &[u8],
    parent_chain_code: &[u8],
    index: u32,
) -> Result<([u8; 32], [u8; 32]), Error> {
    if parent_chain_code.len() != 32 {
        return Err("Chain code must be 32 bytes");
    }
    let sk = secret_key(parent_key)?;
    let mut data = [0u8; 37];
    if index & 0x8000_0000 != 0 {
        data[1..33].copy_from_slice(parent_key);
    } else {
        data[..33].copy_from_slice(sk.public_key().to_encoded_point(true).as_bytes());
    }
    data[33..].copy_from_slice(&index.to_be_bytes());

    let mut i = hmac_sha512(parent_chain_code, &data);
    data.zeroize();

    let mut il = [0u8; 32];
    il.copy_from_slice(&i[..32]);
    let il_scalar = scalar_from_bytes_checked(&il);
    il.zeroize();
    let il_scalar = il_scalar.ok_or("Derived key invalid (IL >= n); use the next index")?;

    let child = il_scalar + *sk.to_nonzero_scalar();
    if bool::from(child.is_zero()) {
        i.zeroize();
        return Err("Derived key invalid (zero); use the next index");
    }

    let mut cc = [0u8; 32];
    cc.copy_from_slice(&i[32..]);
    i.zeroize();
    Ok((child.to_bytes().into(), cc))
}

/// Adds a 32-byte tweak to a secret key modulo n (useful for custom derivation schemes).
pub fn secret_key_tweak_add(secret_key_bytes: &[u8], tweak: &[u8]) -> Result<[u8; 32], Error> {
    if tweak.len() != 32 {
        return Err("Tweak must be 32 bytes");
    }
    let sk = secret_key(secret_key_bytes)?;
    let mut tb = [0u8; 32];
    tb.copy_from_slice(tweak);
    let t = <Scalar as Reduce<U256>>::reduce_bytes(&tb.into());
    let r = t + *sk.to_nonzero_scalar();
    if bool::from(r.is_zero()) {
        return Err("Result is zero");
    }
    Ok(r.to_bytes().into())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn unhex(s: &str) -> Vec<u8> {
        hex::decode(s).unwrap()
    }

    #[test]
    fn pubkey_of_one_is_generator() {
        let mut k = [0u8; 32];
        k[31] = 1;
        let pk = derive_public_key(&k, true).unwrap();
        assert_eq!(
            hex::encode(pk),
            "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798"
        );
    }

    #[test]
    fn ecdsa_sign_verify_recover_low_s() {
        let sk = unhex("4646464646464646464646464646464646464646464646464646464646464646");
        let digest = crate::hash::keccak256(b"crypto.net");
        let (sig, rec) = sign_recoverable(&sk, &digest).unwrap();
        let pk = derive_public_key(&sk, false).unwrap();
        assert!(verify(&pk, &digest, &sig));
        assert_eq!(recover_public_key(&digest, &sig, rec, false).unwrap(), pk);
        // low-S: s <= n/2
        let half_n = unhex("7fffffffffffffffffffffffffffffff5d576e7357a4501ddfe92f46681b20a0");
        assert!(sig[32..].to_vec() <= half_n);
    }

    #[test]
    fn bip340_vector_0() {
        let sk = unhex("0000000000000000000000000000000000000000000000000000000000000003");
        let pk = xonly_public_key(&sk).unwrap();
        assert_eq!(
            hex::encode(pk).to_uppercase(),
            "F9308A019258C31049344F85F89D5229B531C845836F99B08601F113BCE036F9"
        );
        let sig = schnorr_sign(&sk, &[0u8; 32], &[0u8; 32]).unwrap();
        assert_eq!(
            hex::encode(sig).to_uppercase(),
            "E907831F80848D1069A5371B402410364BDF1C5F8307B0084C55F1CE2DCA821525F66A4A85EA8B71E482A74F382D2CE5EBEEE8FDB2172F477DF4900D310536C0"
        );
        assert!(schnorr_verify(&pk, &[0u8; 32], &sig));
        assert!(!schnorr_verify(&pk, &[1u8; 32], &sig));
    }

    #[test]
    fn bip86_taproot_output_key() {
        // BIP-86 test vector: internal key of m/86'/0'/0'/0/0 for "abandon ... about"
        let internal = unhex("cc8a4bc64d897bddc5fbc2f670f7a8ba0b386779106cf1223c6fc5d7cd6fc115");
        let (q, _) = taproot_tweak_public_key(&internal, &[]).unwrap();
        assert_eq!(
            hex::encode(q),
            "a60869f0dbcf1dc659c9cecbaf8050135ea9e8cdc487053f1dc6880949dc684c"
        );
    }

    #[test]
    fn taproot_tweaked_secret_matches_tweaked_public() {
        let sk = unhex("4646464646464646464646464646464646464646464646464646464646464646");
        let internal = xonly_public_key(&sk).unwrap();
        let (q, _) = taproot_tweak_public_key(&internal, &[]).unwrap();
        let tweaked_sk = taproot_tweak_secret_key(&sk, &[]).unwrap();
        assert_eq!(xonly_public_key(&tweaked_sk).unwrap(), q);
    }

    #[test]
    fn bip32_vector_1() {
        let seed = unhex("000102030405060708090a0b0c0d0e0f");
        let i = hmac_sha512(b"Bitcoin seed", &seed);
        let (mut k, mut c) = (i[..32].to_vec(), i[32..].to_vec());
        for idx in [0x8000_0000u32, 1, 0x8000_0002, 2, 1_000_000_000] {
            let (nk, nc) = bip32_ckd_priv(&k, &c, idx).unwrap();
            k = nk.to_vec();
            c = nc.to_vec();
        }
        assert_eq!(
            hex::encode(&k),
            "471b76e389e528d6de6d816857e012c5455051cad6660850e58372a6c3e6e7c8"
        );
        assert_eq!(
            hex::encode(&c),
            "c783e67b921d2beb8f6b389cc646d7263b4145701dadd2161548a8b078e65e9e"
        );
    }

    #[test]
    fn rejects_invalid_secret_keys() {
        assert!(derive_public_key(&[0u8; 32], true).is_err());
        assert!(derive_public_key(&[0xffu8; 32], true).is_err());
        assert!(derive_public_key(&[1u8; 31], true).is_err());
    }
}
