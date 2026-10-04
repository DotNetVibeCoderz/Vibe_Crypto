using System.Runtime.InteropServices;

namespace Crypto.Net.Native;

/// <summary>
/// Raw P/Invoke signatures for the <c>cryptonet</c> Rust library. Mirrors
/// <c>rust/crates/cryptonet-ffi/src/lib.rs</c>; keep both files in sync.
/// </summary>
internal static unsafe partial class NativeMethods
{
    private const string LibName = "cryptonet";

    internal const int CN_SUCCESS = 0;
    internal const int CN_ERR_NULL_POINTER = -1;
    internal const int CN_ERR_INVALID_LENGTH = -2;
    internal const int CN_ERR_CRYPTO_FAILED = -3;
    internal const int CN_ERR_BUFFER_TOO_SMALL = -4;
    internal const int CN_ERR_INVALID_INPUT = -5;
    internal const int CN_ERR_PANIC = -99;

    [LibraryImport(LibName)] internal static partial uint cn_abi_version();

    // Hashes
    [LibraryImport(LibName)] internal static partial int cn_sha256(byte* data, nuint len, byte* out32);
    [LibraryImport(LibName)] internal static partial int cn_double_sha256(byte* data, nuint len, byte* out32);
    [LibraryImport(LibName)] internal static partial int cn_sha512(byte* data, nuint len, byte* out64);
    [LibraryImport(LibName)] internal static partial int cn_keccak256(byte* data, nuint len, byte* out32);
    [LibraryImport(LibName)] internal static partial int cn_ripemd160(byte* data, nuint len, byte* out20);
    [LibraryImport(LibName)] internal static partial int cn_hash160(byte* data, nuint len, byte* out20);
    [LibraryImport(LibName)] internal static partial int cn_blake2b(byte* data, nuint len, byte* output, nuint outLen);
    [LibraryImport(LibName)] internal static partial int cn_tagged_hash(byte* tag, nuint tagLen, byte* msg, nuint msgLen, byte* out32);

    // secp256k1
    [LibraryImport(LibName)] internal static partial int cn_secp256k1_pubkey(byte* secret32, int compressed, byte* output, nuint* outLen);
    [LibraryImport(LibName)] internal static partial int cn_secp256k1_convert_pubkey(byte* pubkey, nuint pubkeyLen, int compressed, byte* output, nuint* outLen);
    [LibraryImport(LibName)] internal static partial int cn_secp256k1_sign_recoverable(byte* secret32, byte* digest32, byte* sigOut64, byte* recIdOut);
    [LibraryImport(LibName)] internal static partial int cn_secp256k1_verify(byte* pubkey, nuint pubkeyLen, byte* digest32, byte* sig64);
    [LibraryImport(LibName)] internal static partial int cn_secp256k1_recover_pubkey(byte* digest32, byte* sig64, byte recId, int compressed, byte* output, nuint* outLen);
    [LibraryImport(LibName)] internal static partial int cn_secp256k1_xonly_pubkey(byte* secret32, byte* out32);
    [LibraryImport(LibName)] internal static partial int cn_schnorr_sign(byte* secret32, byte* msg, nuint msgLen, byte* aux32, byte* out64);
    [LibraryImport(LibName)] internal static partial int cn_schnorr_verify(byte* xonly32, byte* msg, nuint msgLen, byte* sig64);
    [LibraryImport(LibName)] internal static partial int cn_taproot_tweak_pubkey(byte* xonly32, byte* merkleRoot, nuint merkleLen, byte* out32, byte* parityOut);
    [LibraryImport(LibName)] internal static partial int cn_taproot_tweak_seckey(byte* secret32, byte* merkleRoot, nuint merkleLen, byte* out32);
    [LibraryImport(LibName)] internal static partial int cn_bip32_ckd_priv(byte* key32, byte* chainCode32, uint index, byte* outKey32, byte* outCc32);

    // ed25519
    [LibraryImport(LibName)] internal static partial int cn_ed25519_pubkey(byte* secret32, byte* outPk32);
    [LibraryImport(LibName)] internal static partial int cn_ed25519_sign(byte* secret32, byte* msg, nuint msgLen, byte* outSig64);
    [LibraryImport(LibName)] internal static partial int cn_ed25519_verify(byte* pk32, byte* msg, nuint msgLen, byte* sig64);
    [LibraryImport(LibName)] internal static partial int cn_slip10_ed25519_ckd_priv(byte* key32, byte* chainCode32, uint index, byte* outKey32, byte* outCc32);
    [LibraryImport(LibName)] internal static partial int cn_substrate_ed25519_derive(byte* seed32, byte* path, nuint pathLen, byte* outSeed32);

    // sr25519
    [LibraryImport(LibName)] internal static partial int cn_sr25519_from_seed(byte* seed32, byte* outSecret64, byte* outPublic32);
    [LibraryImport(LibName)] internal static partial int cn_sr25519_derive(byte* secret64, byte* path, nuint pathLen, byte* outSecret64, byte* outPublic32);
    [LibraryImport(LibName)] internal static partial int cn_sr25519_public(byte* secret64, byte* outPublic32);
    [LibraryImport(LibName)] internal static partial int cn_sr25519_sign(byte* secret64, byte* msg, nuint msgLen, byte* outSig64);
    [LibraryImport(LibName)] internal static partial int cn_sr25519_verify(byte* public32, byte* msg, nuint msgLen, byte* sig64);

    // KDF
    [LibraryImport(LibName)] internal static partial int cn_bip39_mnemonic_to_seed(byte* mnemonic, nuint mnemonicLen, byte* pass, nuint passLen, byte* outSeed64);
    [LibraryImport(LibName)] internal static partial int cn_hmac_sha512(byte* key, nuint keyLen, byte* data, nuint dataLen, byte* out64);
    [LibraryImport(LibName)] internal static partial int cn_pbkdf2(int prf, byte* pass, nuint passLen, byte* salt, nuint saltLen, uint iterations, byte* output, nuint outLen);
    [LibraryImport(LibName)] internal static partial int cn_scrypt(byte* pass, nuint passLen, byte* salt, nuint saltLen, byte logN, uint r, uint p, byte* output, nuint outLen);

    // Codecs
    [LibraryImport(LibName)] internal static partial int cn_base58_encode(byte* data, nuint dataLen, byte* output, nuint cap, nuint* written);
    [LibraryImport(LibName)] internal static partial int cn_base58_decode(byte* s, nuint sLen, byte* output, nuint cap, nuint* written);
    [LibraryImport(LibName)] internal static partial int cn_base58check_encode(byte* data, nuint dataLen, byte* output, nuint cap, nuint* written);
    [LibraryImport(LibName)] internal static partial int cn_base58check_decode(byte* s, nuint sLen, byte* output, nuint cap, nuint* written);
    [LibraryImport(LibName)] internal static partial int cn_bech32_encode(byte* hrp, nuint hrpLen, byte* data, nuint dataLen, int bech32m, byte* output, nuint cap, nuint* written);
    [LibraryImport(LibName)]
    internal static partial int cn_bech32_decode(byte* s, nuint sLen,
        byte* hrpOut, nuint hrpCap, nuint* hrpWritten,
        byte* dataOut, nuint dataCap, nuint* dataWritten, int* variantOut);
    [LibraryImport(LibName)] internal static partial int cn_segwit_encode(byte* hrp, nuint hrpLen, byte version, byte* program, nuint programLen, byte* output, nuint cap, nuint* written);
    [LibraryImport(LibName)]
    internal static partial int cn_segwit_decode(byte* s, nuint sLen,
        byte* hrpOut, nuint hrpCap, nuint* hrpWritten, byte* versionOut,
        byte* programOut, nuint programCap, nuint* programWritten);
}
