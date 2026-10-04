//! C ABI for Crypto.Net (`cryptonet` shared library).
//!
//! Conventions
//! - Every function returns an `i32` status: `CN_SUCCESS` (0) or a negative `CN_ERR_*` code.
//!   Boolean checks (`*_verify`) return 1 (valid) / 0 (invalid) / negative on error.
//! - Inputs are `(ptr, len)` pairs. Fixed-size outputs are caller-allocated buffers.
//! - Variable-size outputs take `(out, cap, *written)`. When `cap` is too small the function
//!   writes the required size to `*written` and returns `CN_ERR_BUFFER_TOO_SMALL`.
//! - No panic crosses the boundary: every entry point runs inside `catch_unwind`.
//!
//! Built by Gravicode Studios, led by Kang Fadhil.

#![allow(clippy::missing_safety_doc)]

use std::panic::{catch_unwind, UnwindSafe};
use std::slice;

use cryptonet_codec as codec;
use cryptonet_crypto as crypto;
use zeroize::Zeroize;

pub const CN_SUCCESS: i32 = 0;
pub const CN_ERR_NULL_POINTER: i32 = -1;
pub const CN_ERR_INVALID_LENGTH: i32 = -2;
pub const CN_ERR_CRYPTO_FAILED: i32 = -3;
pub const CN_ERR_BUFFER_TOO_SMALL: i32 = -4;
pub const CN_ERR_INVALID_INPUT: i32 = -5;
pub const CN_ERR_PANIC: i32 = -99;

/// ABI version `0xMMmmpppp`. Bump the minor for additive changes, the major for breaking ones.
pub const CN_ABI_VERSION: u32 = 0x0001_0100;

type FfiResult = Result<(), i32>;

fn guard<F: FnOnce() -> FfiResult + UnwindSafe>(f: F) -> i32 {
    match catch_unwind(f) {
        Ok(Ok(())) => CN_SUCCESS,
        Ok(Err(code)) => code,
        Err(_) => CN_ERR_PANIC,
    }
}

fn guard_bool<F: FnOnce() -> Result<bool, i32> + UnwindSafe>(f: F) -> i32 {
    match catch_unwind(f) {
        Ok(Ok(true)) => 1,
        Ok(Ok(false)) => 0,
        Ok(Err(code)) => code,
        Err(_) => CN_ERR_PANIC,
    }
}

unsafe fn input<'a>(ptr: *const u8, len: usize) -> Result<&'a [u8], i32> {
    if len == 0 {
        return Ok(&[]);
    }
    if ptr.is_null() {
        return Err(CN_ERR_NULL_POINTER);
    }
    Ok(slice::from_raw_parts(ptr, len))
}

unsafe fn output<'a>(ptr: *mut u8, len: usize) -> Result<&'a mut [u8], i32> {
    if len == 0 {
        return Ok(&mut []);
    }
    if ptr.is_null() {
        return Err(CN_ERR_NULL_POINTER);
    }
    Ok(slice::from_raw_parts_mut(ptr, len))
}

unsafe fn input_str<'a>(ptr: *const u8, len: usize) -> Result<&'a str, i32> {
    std::str::from_utf8(input(ptr, len)?).map_err(|_| CN_ERR_INVALID_INPUT)
}

unsafe fn write_var(data: &[u8], out: *mut u8, cap: usize, written: *mut usize) -> FfiResult {
    if written.is_null() {
        return Err(CN_ERR_NULL_POINTER);
    }
    *written = data.len();
    if out.is_null() || cap < data.len() {
        return Err(CN_ERR_BUFFER_TOO_SMALL);
    }
    output(out, data.len())?.copy_from_slice(data);
    Ok(())
}

unsafe fn write_fixed(data: &[u8], out: *mut u8) -> FfiResult {
    output(out, data.len())?.copy_from_slice(data);
    Ok(())
}

fn crypto_err<E>(_: E) -> i32 {
    CN_ERR_CRYPTO_FAILED
}

// ============================================================ meta

#[no_mangle]
pub extern "C" fn cn_abi_version() -> u32 {
    CN_ABI_VERSION
}

/// Frees a buffer allocated by Rust. Reserved for future handle-based APIs.
#[no_mangle]
pub unsafe extern "C" fn cn_free_buffer(ptr: *mut u8, len: usize) {
    if !ptr.is_null() && len > 0 {
        let mut v = Vec::from_raw_parts(ptr, len, len);
        v.zeroize();
    }
}

// ============================================================ hashes

macro_rules! fixed_hash {
    ($name:ident, $func:path, $n:expr) => {
        #[no_mangle]
        pub unsafe extern "C" fn $name(data: *const u8, len: usize, out: *mut u8) -> i32 {
            guard(|| write_fixed(&$func(input(data, len)?), out))
        }
    };
}

fixed_hash!(cn_sha256, crypto::hash::sha256, 32);
fixed_hash!(cn_double_sha256, crypto::hash::double_sha256, 32);
fixed_hash!(cn_sha512, crypto::hash::sha512, 64);
fixed_hash!(cn_keccak256, crypto::hash::keccak256, 32);
fixed_hash!(cn_ripemd160, crypto::hash::ripemd160, 20);
fixed_hash!(cn_hash160, crypto::hash::hash160, 20);
fixed_hash!(cn_blake2b_256, crypto::hash::blake2b_256, 32);

/// BLAKE2b with `out_len` in 1..=64.
#[no_mangle]
pub unsafe extern "C" fn cn_blake2b(
    data: *const u8,
    len: usize,
    out: *mut u8,
    out_len: usize,
) -> i32 {
    guard(|| {
        let d = input(data, len)?;
        if out_len == 0 || out_len > 64 {
            return Err(CN_ERR_INVALID_LENGTH);
        }
        crypto::hash::blake2b(d, output(out, out_len)?).map_err(crypto_err)
    })
}

#[no_mangle]
pub unsafe extern "C" fn cn_tagged_hash(
    tag: *const u8,
    tag_len: usize,
    msg: *const u8,
    msg_len: usize,
    out32: *mut u8,
) -> i32 {
    guard(|| {
        let t = input_str(tag, tag_len)?;
        write_fixed(&crypto::hash::tagged_hash(t, input(msg, msg_len)?), out32)
    })
}

// ============================================================ secp256k1

#[no_mangle]
pub unsafe extern "C" fn cn_secp256k1_pubkey(
    secret32: *const u8,
    compressed: i32,
    out: *mut u8,
    out_len: *mut usize,
) -> i32 {
    guard(|| {
        let pk = crypto::secp256k1::derive_public_key(input(secret32, 32)?, compressed != 0)
            .map_err(crypto_err)?;
        let cap = if out_len.is_null() { 0 } else { *out_len };
        write_var(&pk, out, cap, out_len)
    })
}

#[no_mangle]
pub unsafe extern "C" fn cn_secp256k1_convert_pubkey(
    pubkey: *const u8,
    pubkey_len: usize,
    compressed: i32,
    out: *mut u8,
    out_len: *mut usize,
) -> i32 {
    guard(|| {
        let pk = crypto::secp256k1::convert_public_key(input(pubkey, pubkey_len)?, compressed != 0)
            .map_err(crypto_err)?;
        let cap = if out_len.is_null() { 0 } else { *out_len };
        write_var(&pk, out, cap, out_len)
    })
}

#[no_mangle]
pub unsafe extern "C" fn cn_secp256k1_sign_recoverable(
    secret32: *const u8,
    digest32: *const u8,
    sig_out64: *mut u8,
    rec_id_out: *mut u8,
) -> i32 {
    guard(|| {
        if rec_id_out.is_null() {
            return Err(CN_ERR_NULL_POINTER);
        }
        let (sig, rec) =
            crypto::secp256k1::sign_recoverable(input(secret32, 32)?, input(digest32, 32)?)
                .map_err(crypto_err)?;
        write_fixed(&sig, sig_out64)?;
        *rec_id_out = rec;
        Ok(())
    })
}

#[no_mangle]
pub unsafe extern "C" fn cn_secp256k1_verify(
    pubkey: *const u8,
    pubkey_len: usize,
    digest32: *const u8,
    sig64: *const u8,
) -> i32 {
    guard_bool(|| {
        Ok(crypto::secp256k1::verify(
            input(pubkey, pubkey_len)?,
            input(digest32, 32)?,
            input(sig64, 64)?,
        ))
    })
}

#[no_mangle]
pub unsafe extern "C" fn cn_secp256k1_recover_pubkey(
    digest32: *const u8,
    sig64: *const u8,
    rec_id: u8,
    compressed: i32,
    out: *mut u8,
    out_len: *mut usize,
) -> i32 {
    guard(|| {
        let pk = crypto::secp256k1::recover_public_key(
            input(digest32, 32)?,
            input(sig64, 64)?,
            rec_id,
            compressed != 0,
        )
        .map_err(crypto_err)?;
        let cap = if out_len.is_null() { 0 } else { *out_len };
        write_var(&pk, out, cap, out_len)
    })
}

#[no_mangle]
pub unsafe extern "C" fn cn_secp256k1_xonly_pubkey(secret32: *const u8, out32: *mut u8) -> i32 {
    guard(|| {
        write_fixed(
            &crypto::secp256k1::xonly_public_key(input(secret32, 32)?).map_err(crypto_err)?,
            out32,
        )
    })
}

/// BIP-340 Schnorr signature. `aux32` may be null (all-zero auxiliary randomness).
#[no_mangle]
pub unsafe extern "C" fn cn_schnorr_sign(
    secret32: *const u8,
    msg: *const u8,
    msg_len: usize,
    aux32: *const u8,
    out64: *mut u8,
) -> i32 {
    guard(|| {
        let mut aux = [0u8; 32];
        if !aux32.is_null() {
            aux.copy_from_slice(input(aux32, 32)?);
        }
        let sig = crypto::secp256k1::schnorr_sign(input(secret32, 32)?, input(msg, msg_len)?, &aux)
            .map_err(crypto_err)?;
        write_fixed(&sig, out64)
    })
}

#[no_mangle]
pub unsafe extern "C" fn cn_schnorr_verify(
    xonly32: *const u8,
    msg: *const u8,
    msg_len: usize,
    sig64: *const u8,
) -> i32 {
    guard_bool(|| {
        Ok(crypto::secp256k1::schnorr_verify(
            input(xonly32, 32)?,
            input(msg, msg_len)?,
            input(sig64, 64)?,
        ))
    })
}

#[no_mangle]
pub unsafe extern "C" fn cn_taproot_tweak_pubkey(
    xonly32: *const u8,
    merkle_root: *const u8,
    merkle_len: usize,
    out32: *mut u8,
    parity_out: *mut u8,
) -> i32 {
    guard(|| {
        let (q, parity) = crypto::secp256k1::taproot_tweak_public_key(
            input(xonly32, 32)?,
            input(merkle_root, merkle_len)?,
        )
        .map_err(crypto_err)?;
        write_fixed(&q, out32)?;
        if !parity_out.is_null() {
            *parity_out = parity;
        }
        Ok(())
    })
}

#[no_mangle]
pub unsafe extern "C" fn cn_taproot_tweak_seckey(
    secret32: *const u8,
    merkle_root: *const u8,
    merkle_len: usize,
    out32: *mut u8,
) -> i32 {
    guard(|| {
        let mut k = crypto::secp256k1::taproot_tweak_secret_key(
            input(secret32, 32)?,
            input(merkle_root, merkle_len)?,
        )
        .map_err(crypto_err)?;
        let r = write_fixed(&k, out32);
        k.zeroize();
        r
    })
}

#[no_mangle]
pub unsafe extern "C" fn cn_bip32_ckd_priv(
    key32: *const u8,
    chain_code32: *const u8,
    index: u32,
    out_key32: *mut u8,
    out_cc32: *mut u8,
) -> i32 {
    guard(|| {
        let (mut k, c) =
            crypto::secp256k1::bip32_ckd_priv(input(key32, 32)?, input(chain_code32, 32)?, index)
                .map_err(crypto_err)?;
        let r = write_fixed(&k, out_key32).and_then(|_| write_fixed(&c, out_cc32));
        k.zeroize();
        r
    })
}

// ============================================================ ed25519

#[no_mangle]
pub unsafe extern "C" fn cn_ed25519_pubkey(secret32: *const u8, out_pk32: *mut u8) -> i32 {
    guard(|| {
        write_fixed(
            &crypto::ed25519::derive_public_key(input(secret32, 32)?).map_err(crypto_err)?,
            out_pk32,
        )
    })
}

#[no_mangle]
pub unsafe extern "C" fn cn_ed25519_sign(
    secret32: *const u8,
    msg: *const u8,
    msg_len: usize,
    out_sig64: *mut u8,
) -> i32 {
    guard(|| {
        write_fixed(
            &crypto::ed25519::sign(input(secret32, 32)?, input(msg, msg_len)?)
                .map_err(crypto_err)?,
            out_sig64,
        )
    })
}

#[no_mangle]
pub unsafe extern "C" fn cn_ed25519_verify(
    pk32: *const u8,
    msg: *const u8,
    msg_len: usize,
    sig64: *const u8,
) -> i32 {
    guard_bool(|| {
        Ok(crypto::ed25519::verify(
            input(pk32, 32)?,
            input(msg, msg_len)?,
            input(sig64, 64)?,
        ))
    })
}

#[no_mangle]
pub unsafe extern "C" fn cn_slip10_ed25519_ckd_priv(
    key32: *const u8,
    chain_code32: *const u8,
    index: u32,
    out_key32: *mut u8,
    out_cc32: *mut u8,
) -> i32 {
    guard(|| {
        let (mut k, c) =
            crypto::ed25519::slip10_ckd_priv(input(key32, 32)?, input(chain_code32, 32)?, index)
                .map_err(crypto_err)?;
        let r = write_fixed(&k, out_key32).and_then(|_| write_fixed(&c, out_cc32));
        k.zeroize();
        r
    })
}

/// Substrate ed25519 hard derivation (`//junction` only).
#[no_mangle]
pub unsafe extern "C" fn cn_substrate_ed25519_derive(
    seed32: *const u8,
    path: *const u8,
    path_len: usize,
    out_seed32: *mut u8,
) -> i32 {
    guard(|| {
        let mut s = crypto::sr25519::ed25519_derive(input(seed32, 32)?, input_str(path, path_len)?)
            .map_err(|_| CN_ERR_INVALID_INPUT)?;
        let r = write_fixed(&s, out_seed32);
        s.zeroize();
        r
    })
}

// ============================================================ sr25519

#[no_mangle]
pub unsafe extern "C" fn cn_sr25519_from_seed(
    seed32: *const u8,
    out_secret64: *mut u8,
    out_public32: *mut u8,
) -> i32 {
    guard(|| {
        let (mut sk, pk) =
            crypto::sr25519::keypair_from_seed(input(seed32, 32)?).map_err(crypto_err)?;
        let r = write_fixed(&sk, out_secret64).and_then(|_| write_fixed(&pk, out_public32));
        sk.zeroize();
        r
    })
}

#[no_mangle]
pub unsafe extern "C" fn cn_sr25519_derive(
    secret64: *const u8,
    path: *const u8,
    path_len: usize,
    out_secret64: *mut u8,
    out_public32: *mut u8,
) -> i32 {
    guard(|| {
        let (mut sk, pk) =
            crypto::sr25519::derive(input(secret64, 64)?, input_str(path, path_len)?)
                .map_err(|_| CN_ERR_INVALID_INPUT)?;
        let r = write_fixed(&sk, out_secret64).and_then(|_| write_fixed(&pk, out_public32));
        sk.zeroize();
        r
    })
}

#[no_mangle]
pub unsafe extern "C" fn cn_sr25519_public(secret64: *const u8, out_public32: *mut u8) -> i32 {
    guard(|| {
        write_fixed(
            &crypto::sr25519::public_key(input(secret64, 64)?).map_err(crypto_err)?,
            out_public32,
        )
    })
}

#[no_mangle]
pub unsafe extern "C" fn cn_sr25519_sign(
    secret64: *const u8,
    msg: *const u8,
    msg_len: usize,
    out_sig64: *mut u8,
) -> i32 {
    guard(|| {
        write_fixed(
            &crypto::sr25519::sign(input(secret64, 64)?, input(msg, msg_len)?)
                .map_err(crypto_err)?,
            out_sig64,
        )
    })
}

#[no_mangle]
pub unsafe extern "C" fn cn_sr25519_verify(
    public32: *const u8,
    msg: *const u8,
    msg_len: usize,
    sig64: *const u8,
) -> i32 {
    guard_bool(|| {
        Ok(crypto::sr25519::verify(
            input(public32, 32)?,
            input(msg, msg_len)?,
            input(sig64, 64)?,
        ))
    })
}

// ============================================================ KDF

#[no_mangle]
pub unsafe extern "C" fn cn_bip39_mnemonic_to_seed(
    mnemonic: *const u8,
    mnemonic_len: usize,
    pass: *const u8,
    pass_len: usize,
    out_seed64: *mut u8,
) -> i32 {
    guard(|| {
        let mut seed =
            crypto::kdf::mnemonic_to_seed(input(mnemonic, mnemonic_len)?, input(pass, pass_len)?);
        let r = write_fixed(&seed, out_seed64);
        seed.zeroize();
        r
    })
}

#[no_mangle]
pub unsafe extern "C" fn cn_hmac_sha512(
    key: *const u8,
    key_len: usize,
    data: *const u8,
    data_len: usize,
    out64: *mut u8,
) -> i32 {
    guard(|| {
        let mut mac = crypto::kdf::hmac_sha512(input(key, key_len)?, input(data, data_len)?);
        let r = write_fixed(&mac, out64);
        mac.zeroize();
        r
    })
}

/// PBKDF2. `prf`: 0 = HMAC-SHA256, 1 = HMAC-SHA512.
#[no_mangle]
pub unsafe extern "C" fn cn_pbkdf2(
    prf: i32,
    pass: *const u8,
    pass_len: usize,
    salt: *const u8,
    salt_len: usize,
    iterations: u32,
    out: *mut u8,
    out_len: usize,
) -> i32 {
    guard(|| {
        let (p, s, o) = (
            input(pass, pass_len)?,
            input(salt, salt_len)?,
            output(out, out_len)?,
        );
        match prf {
            0 => crypto::kdf::pbkdf2_sha256(p, s, iterations, o),
            1 => crypto::kdf::pbkdf2_sha512(p, s, iterations, o),
            _ => return Err(CN_ERR_INVALID_INPUT),
        }
        .map_err(|_| CN_ERR_INVALID_INPUT)
    })
}

#[no_mangle]
pub unsafe extern "C" fn cn_scrypt(
    pass: *const u8,
    pass_len: usize,
    salt: *const u8,
    salt_len: usize,
    log_n: u8,
    r: u32,
    p: u32,
    out: *mut u8,
    out_len: usize,
) -> i32 {
    guard(|| {
        crypto::kdf::scrypt(
            input(pass, pass_len)?,
            input(salt, salt_len)?,
            log_n,
            r,
            p,
            output(out, out_len)?,
        )
        .map_err(|_| CN_ERR_INVALID_INPUT)
    })
}

// ============================================================ codecs

#[no_mangle]
pub unsafe extern "C" fn cn_base58_encode(
    data: *const u8,
    data_len: usize,
    out: *mut u8,
    cap: usize,
    written: *mut usize,
) -> i32 {
    guard(|| {
        write_var(
            codec::base58::encode(input(data, data_len)?).as_bytes(),
            out,
            cap,
            written,
        )
    })
}

#[no_mangle]
pub unsafe extern "C" fn cn_base58_decode(
    s: *const u8,
    s_len: usize,
    out: *mut u8,
    cap: usize,
    written: *mut usize,
) -> i32 {
    guard(|| {
        let decoded =
            codec::base58::decode(input_str(s, s_len)?).map_err(|_| CN_ERR_INVALID_INPUT)?;
        write_var(&decoded, out, cap, written)
    })
}

#[no_mangle]
pub unsafe extern "C" fn cn_base58check_encode(
    data: *const u8,
    data_len: usize,
    out: *mut u8,
    cap: usize,
    written: *mut usize,
) -> i32 {
    guard(|| {
        write_var(
            codec::base58::encode_check(input(data, data_len)?).as_bytes(),
            out,
            cap,
            written,
        )
    })
}

#[no_mangle]
pub unsafe extern "C" fn cn_base58check_decode(
    s: *const u8,
    s_len: usize,
    out: *mut u8,
    cap: usize,
    written: *mut usize,
) -> i32 {
    guard(|| {
        let decoded =
            codec::base58::decode_check(input_str(s, s_len)?).map_err(|_| CN_ERR_INVALID_INPUT)?;
        write_var(&decoded, out, cap, written)
    })
}

#[no_mangle]
pub unsafe extern "C" fn cn_bech32_encode(
    hrp: *const u8,
    hrp_len: usize,
    data: *const u8,
    data_len: usize,
    bech32m: i32,
    out: *mut u8,
    cap: usize,
    written: *mut usize,
) -> i32 {
    guard(|| {
        let s = codec::bech32_codec::encode(
            input_str(hrp, hrp_len)?,
            input(data, data_len)?,
            bech32m != 0,
        )
        .map_err(|_| CN_ERR_INVALID_INPUT)?;
        write_var(s.as_bytes(), out, cap, written)
    })
}

/// Decodes bech32/bech32m. Writes the HRP and the 8-bit data; `variant_out` = 1 for bech32m.
#[no_mangle]
pub unsafe extern "C" fn cn_bech32_decode(
    s: *const u8,
    s_len: usize,
    hrp_out: *mut u8,
    hrp_cap: usize,
    hrp_written: *mut usize,
    data_out: *mut u8,
    data_cap: usize,
    data_written: *mut usize,
    variant_out: *mut i32,
) -> i32 {
    guard(|| {
        let (hrp, data, is_m) =
            codec::bech32_codec::decode(input_str(s, s_len)?).map_err(|_| CN_ERR_INVALID_INPUT)?;
        let r1 = write_var(hrp.as_bytes(), hrp_out, hrp_cap, hrp_written);
        let r2 = write_var(&data, data_out, data_cap, data_written);
        r1?;
        r2?;
        if !variant_out.is_null() {
            *variant_out = is_m as i32;
        }
        Ok(())
    })
}

#[no_mangle]
pub unsafe extern "C" fn cn_segwit_encode(
    hrp: *const u8,
    hrp_len: usize,
    version: u8,
    program: *const u8,
    program_len: usize,
    out: *mut u8,
    cap: usize,
    written: *mut usize,
) -> i32 {
    guard(|| {
        let s = codec::bech32_codec::segwit_encode(
            input_str(hrp, hrp_len)?,
            version,
            input(program, program_len)?,
        )
        .map_err(|_| CN_ERR_INVALID_INPUT)?;
        write_var(s.as_bytes(), out, cap, written)
    })
}

/// Decodes a SegWit address. The program buffer must hold at least 40 bytes.
#[no_mangle]
pub unsafe extern "C" fn cn_segwit_decode(
    s: *const u8,
    s_len: usize,
    hrp_out: *mut u8,
    hrp_cap: usize,
    hrp_written: *mut usize,
    version_out: *mut u8,
    program_out: *mut u8,
    program_cap: usize,
    program_written: *mut usize,
) -> i32 {
    guard(|| {
        if version_out.is_null() {
            return Err(CN_ERR_NULL_POINTER);
        }
        let (hrp, version, program) = codec::bech32_codec::segwit_decode(input_str(s, s_len)?)
            .map_err(|_| CN_ERR_INVALID_INPUT)?;
        let r1 = write_var(hrp.as_bytes(), hrp_out, hrp_cap, hrp_written);
        let r2 = write_var(&program, program_out, program_cap, program_written);
        r1?;
        r2?;
        *version_out = version;
        Ok(())
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn abi_version() {
        assert_eq!(cn_abi_version(), CN_ABI_VERSION);
    }

    #[test]
    fn null_and_length_errors() {
        unsafe {
            let mut out = [0u8; 32];
            assert_eq!(
                cn_sha256(std::ptr::null(), 4, out.as_mut_ptr()),
                CN_ERR_NULL_POINTER
            );
            assert_eq!(cn_sha256(std::ptr::null(), 0, out.as_mut_ptr()), CN_SUCCESS);
            assert_eq!(
                cn_blake2b(b"x".as_ptr(), 1, out.as_mut_ptr(), 65),
                CN_ERR_INVALID_LENGTH
            );
        }
    }

    #[test]
    fn buffer_too_small_reports_required_size() {
        unsafe {
            let data = [1u8; 20];
            let mut written = 0usize;
            let rc = cn_base58_encode(
                data.as_ptr(),
                data.len(),
                std::ptr::null_mut(),
                0,
                &mut written,
            );
            assert_eq!(rc, CN_ERR_BUFFER_TOO_SMALL);
            let mut buf = vec![0u8; written];
            let rc = cn_base58_encode(
                data.as_ptr(),
                data.len(),
                buf.as_mut_ptr(),
                buf.len(),
                &mut written,
            );
            assert_eq!(rc, CN_SUCCESS);
        }
    }

    #[test]
    fn verify_returns_tristate() {
        unsafe {
            let sk = [7u8; 32];
            let mut pk = [0u8; 32];
            let mut sig = [0u8; 64];
            assert_eq!(cn_ed25519_pubkey(sk.as_ptr(), pk.as_mut_ptr()), CN_SUCCESS);
            assert_eq!(
                cn_ed25519_sign(sk.as_ptr(), b"m".as_ptr(), 1, sig.as_mut_ptr()),
                CN_SUCCESS
            );
            assert_eq!(
                cn_ed25519_verify(pk.as_ptr(), b"m".as_ptr(), 1, sig.as_ptr()),
                1
            );
            assert_eq!(
                cn_ed25519_verify(pk.as_ptr(), b"n".as_ptr(), 1, sig.as_ptr()),
                0
            );
            assert_eq!(
                cn_ed25519_verify(std::ptr::null(), b"n".as_ptr(), 1, sig.as_ptr()),
                CN_ERR_NULL_POINTER
            );
        }
    }

    #[test]
    fn invalid_secret_key_is_error_not_panic() {
        unsafe {
            let zero = [0u8; 32];
            let mut out = [0u8; 33];
            let mut len = out.len();
            assert_eq!(
                cn_secp256k1_pubkey(zero.as_ptr(), 1, out.as_mut_ptr(), &mut len),
                CN_ERR_CRYPTO_FAILED
            );
        }
    }
}
