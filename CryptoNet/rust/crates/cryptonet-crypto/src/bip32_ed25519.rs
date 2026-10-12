//! BIP32-Ed25519 (Khovratovich–Law, the "V2" variant Cardano uses for Icarus / CIP-1852 wallets) and ed25519
//! signing with extended keys `(kL, kR)`, where `kL` is used directly as the scalar (no hashing or clamping).

use curve25519_dalek::{
    constants::ED25519_BASEPOINT_TABLE, edwards::CompressedEdwardsY, scalar::Scalar,
};
use hmac::{Hmac, Mac};
use sha2::{Digest, Sha512};
use zeroize::Zeroize;

pub type Error = &'static str;

type HmacSha512 = Hmac<Sha512>;

const HARDENED: u32 = 0x8000_0000;

fn arr32(b: &[u8], what: Error) -> Result<[u8; 32], Error> {
    b.try_into().map_err(|_| what)
}

/// Public key `kL·G` of an extended secret.
pub fn public_key(kl: &[u8]) -> Result<[u8; 32], Error> {
    let mut k = arr32(kl, "kL must be 32 bytes")?;
    let s = Scalar::from_bytes_mod_order(k);
    k.zeroize();
    Ok((&s * ED25519_BASEPOINT_TABLE).compress().to_bytes())
}

/// Ed25519 signature with an extended key: `r = H(kR ‖ M)`, `S = r + H(R ‖ A ‖ M)·kL`.
pub fn sign(kl: &[u8], kr: &[u8], msg: &[u8]) -> Result<[u8; 64], Error> {
    let mut k = arr32(kl, "kL must be 32 bytes")?;
    if kr.len() != 32 {
        return Err("kR must be 32 bytes");
    }
    let a = Scalar::from_bytes_mod_order(k);
    k.zeroize();
    let pk = (&a * ED25519_BASEPOINT_TABLE).compress();

    let mut h = Sha512::new();
    h.update(kr);
    h.update(msg);
    let mut wide: [u8; 64] = h.finalize().into();
    let r = Scalar::from_bytes_mod_order_wide(&wide);
    wide.zeroize();
    let big_r = (&r * ED25519_BASEPOINT_TABLE).compress();

    let mut h = Sha512::new();
    h.update(big_r.as_bytes());
    h.update(pk.as_bytes());
    h.update(msg);
    let c = Scalar::from_bytes_mod_order_wide(&h.finalize().into());
    let s = r + c * a;

    let mut out = [0u8; 64];
    out[..32].copy_from_slice(big_r.as_bytes());
    out[32..].copy_from_slice(s.as_bytes());
    Ok(out)
}

fn hmac2(key: &[u8], tag: u8, parts: &[&[u8]]) -> [u8; 64] {
    let mut mac = HmacSha512::new_from_slice(key).expect("HMAC takes any key length");
    mac.update(&[tag]);
    for p in parts {
        mac.update(p);
    }
    mac.finalize().into_bytes().into()
}

/// `x + 8·zL` where `zL` is the first 28 bytes of `z` (little-endian, carry kept within 256 bits).
fn add_28_mul8(x: &[u8; 32], z: &[u8]) -> [u8; 32] {
    let mut out = [0u8; 32];
    let mut carry: u16 = 0;
    for i in 0..32 {
        let zi = if i < 28 { (z[i] as u16) << 3 } else { 0 };
        let r = x[i] as u16 + zi + carry;
        out[i] = r as u8;
        carry = r >> 8;
    }
    out
}

fn add_256(x: &[u8], y: &[u8]) -> [u8; 32] {
    let mut out = [0u8; 32];
    let mut carry: u16 = 0;
    for i in 0..32 {
        let r = x[i] as u16 + y[i] as u16 + carry;
        out[i] = r as u8;
        carry = r >> 8;
    }
    out
}

/// Child of an extended private key `kL ‖ kR ‖ chainCode` (96 bytes); hardened when `index >= 2^31`.
pub fn derive_private(xprv: &[u8], index: u32) -> Result<[u8; 96], Error> {
    if xprv.len() != 96 {
        return Err("Extended private key must be 96 bytes");
    }
    let (kl, rest) = xprv.split_at(32);
    let (kr, cc) = rest.split_at(32);
    let idx = index.to_le_bytes();
    let (mut z, mut i) = if index >= HARDENED {
        (
            hmac2(cc, 0x00, &[kl, kr, &idx]),
            hmac2(cc, 0x01, &[kl, kr, &idx]),
        )
    } else {
        let a = public_key(kl)?;
        (hmac2(cc, 0x02, &[&a, &idx]), hmac2(cc, 0x03, &[&a, &idx]))
    };
    let mut kl32 = arr32(kl, "kL must be 32 bytes")?;
    let mut out = [0u8; 96];
    out[..32].copy_from_slice(&add_28_mul8(&kl32, &z[..28]));
    out[32..64].copy_from_slice(&add_256(&z[32..], kr));
    out[64..].copy_from_slice(&i[32..]);
    kl32.zeroize();
    z.zeroize();
    i.zeroize();
    Ok(out)
}

/// Non-hardened child of an extended public key `A ‖ chainCode` (64 bytes): `A + 8·zL·G`.
pub fn derive_public(xpub: &[u8], index: u32) -> Result<[u8; 64], Error> {
    if xpub.len() != 64 {
        return Err("Extended public key must be 64 bytes");
    }
    if index >= HARDENED {
        return Err("Hardened children cannot be derived from a public key");
    }
    let (a, cc) = xpub.split_at(32);
    let point = CompressedEdwardsY(arr32(a, "Public key must be 32 bytes")?)
        .decompress()
        .ok_or("Invalid ed25519 public key")?;
    let idx = index.to_le_bytes();
    let z = hmac2(cc, 0x02, &[a, &idx]);
    let i = hmac2(cc, 0x03, &[a, &idx]);
    let zl8 = Scalar::from_bytes_mod_order(add_28_mul8(&[0u8; 32], &z[..28]));
    let child = point + &zl8 * ED25519_BASEPOINT_TABLE;
    let mut out = [0u8; 64];
    out[..32].copy_from_slice(child.compress().as_bytes());
    out[32..].copy_from_slice(&i[32..]);
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn h(s: &str) -> Vec<u8> {
        hex::decode(s).unwrap()
    }

    // Vectors from @emurgo/cardano-serialization-lib 13.2: "abandon … about", m/1852'/1815'/0'/0/0.
    const ACCOUNT: &str = "c026fb6c43215bf0b2e2132ad4852d6e2b47bb09eae796ea9571f9e06f0e6f53817147a33facaf204927be405e3dbcd2e31abe734b354d5e1bc7317d3ada5b68b0e2df16538508046649d0e6d5b32969555a23f2f1ebf2db2819359b0d88bd16";
    const PAYMENT: &str = "105d2ef2192150655a926bca9cccf5e2f6e496efa9580508192e1f4a790e6f53de06529129511d1cacb0664bcf04853fdc0055a47cc6d2c6d20512702076065288848e8af62a27a57e982215741c9eac17e6e45cbfd6ea65a0e0dcc03bb777b2";
    const PAYMENT_PUB: &str = "7ea09a34aebb13c9841c71397b1cabfec5ddf950405293dee496cac2f437480a";

    #[test]
    fn derives_like_cardano_serialization_lib() {
        let root = h("60ce7dbec3616e9fc17e0c32578b3f380337b1b61a1f3cb9651aee30670e6f53970419a23a2e4e4082d12bf78faa8645dfc882cee2ae7179e2b07fe88098abb2072310084784c7308182dbbdb1449b2706586f1ff5cbf13d15e9b6e78c15f067");
        let mut k = root.to_vec();
        for i in [1852, 1815, 0] {
            k = derive_private(&k, HARDENED + i).unwrap().to_vec();
        }
        assert_eq!(hex::encode(&k), ACCOUNT);
        let pay = derive_private(&derive_private(&k, 0).unwrap(), 0).unwrap();
        assert_eq!(hex::encode(pay), PAYMENT);
        assert_eq!(hex::encode(public_key(&pay[..32]).unwrap()), PAYMENT_PUB);

        let mut xpub = public_key(&k[..32]).unwrap().to_vec();
        xpub.extend_from_slice(&k[64..]);
        let child = derive_public(&derive_public(&xpub, 0).unwrap(), 0).unwrap();
        assert_eq!(hex::encode(&child[..32]), PAYMENT_PUB);
        assert!(derive_public(&xpub, HARDENED).is_err());
    }

    #[test]
    fn signs_like_cardano_serialization_lib() {
        let pay = h(PAYMENT);
        let sig = sign(&pay[..32], &pay[32..64], b"hello cardano").unwrap();
        assert_eq!(hex::encode(sig), "217635a4afcf319ca670c1eeeab1378e16bb84c36c983f55f03b1f317e2bfa11d980ad8c4a60bf288dbf65304ef8495b7a19725be63d1a25996940402ab4c704");
        assert!(crate::ed25519::verify(
            &h(PAYMENT_PUB),
            b"hello cardano",
            &sig
        ));
    }
}
