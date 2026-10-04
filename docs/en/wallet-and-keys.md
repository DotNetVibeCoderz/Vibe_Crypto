# Wallets and keys

[← Documentation](index.md) · [Bahasa Indonesia](../id/dompet-dan-kunci.md)

## Mnemonics (BIP-39)

```csharp
string phrase = Mnemonic.Generate(24);              // 12, 15, 18, 21 or 24 words
bool ok = Mnemonic.Validate(phrase);                // wordlist + checksum
byte[] entropy = Mnemonic.ToEntropy(phrase);
byte[] seed = Mnemonic.ToSeed(phrase, "optional passphrase");   // PBKDF2-HMAC-SHA512 ×2048, NFKD-normalised
```

Input is normalised (extra spaces, tabs, capital letters) before validation. Only the English wordlist is bundled.

## HD derivation

`HdWallet` holds three roots derived from one phrase:

| Root | Used by | Path style |
| --- | --- | --- |
| BIP-32 secp256k1 | EVM, Bitcoin, Cosmos | `m/44'/60'/0'/0/0` |
| SLIP-10 ed25519 | Solana | `m/44'/501'/0'/0'` (all hardened) |
| substrate-bip39 | Polkadot, Kusama | `//polkadot//0/soft` |

```csharp
using var wallet = HdWallet.FromMnemonic(phrase);
using HdKey key = wallet.DerivePath("m/84'/0'/0'/0/5");
string xprv = key.ToExtendedPrivateKey();
string zpub = wallet.GetBitcoinAccountXpub(BitcoinAddressType.SegWitP2WPKH);   // watch-only
using HdKey parsed = HdKey.ParseExtendedPrivateKey(xprv);
```

Standard paths are available as `DerivationPath.Standard.*`. Every intermediate key is disposed during
derivation; dispose the keys you receive.

### Reference addresses for the BIP-39 test phrase

`abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about`

| Chain | Path | Address |
| --- | --- | --- |
| Ethereum | m/44'/60'/0'/0/0 | `0x9858EfFD232B4033E47d90003D41EC34EcaEda94` |
| Bitcoin legacy | m/44'/0'/0'/0/0 | `1LqBGSKuX5yYUonjxT5qGfpUsXKYYWeabA` |
| Bitcoin SegWit | m/84'/0'/0'/0/0 | `bc1qcr8te4kr609gcawutmrza0j4xv80jy8z306fyu` |
| Bitcoin Taproot | m/86'/0'/0'/0/0 | `bc1p5cyxnuxmeuwuvkwfem96lqzszd02n6xdcjrs20cac6yqjjwudpxqkedrcr` |
| Solana | m/44'/501'/0'/0' | `HAgk14JpMQLgt6rVgv7cBQFJWFto5Dqxi472uT3DKpqk` |
| Cosmos Hub | m/44'/118'/0'/0/0 | `cosmos19rl4cm2hmr8afy4kldpxz3fka4jguq0auqdal4` |

These are asserted in the test suite. Never send funds to them; the phrase is public.

## Keystores (Web3 Secret Storage v3)

```csharp
string json = KeyStore.Encrypt(privateKey, password, address, KeyStoreKdf.Standard);  // scrypt N=2^18, r=8, p=1
using SecureBuffer key = KeyStore.Decrypt(json, password);    // scrypt or PBKDF2; AES-128-CTR; Keccak MAC
```

The format is the one geth, MetaMask, MyEtherWallet and Nethereum use. `KeyStoreKdf.Light` (N=2^12) is faster for
tests; `new KeyStoreKdf.Pbkdf2Kdf(262_144)` selects PBKDF2. A wrong password raises `CryptographicException`
(MAC mismatch) without revealing anything else.

## Secure buffers

`SecureBuffer` allocates on the pinned object heap (the GC never copies it), zeroes on `Dispose`, and compares in
constant time (`ContentEquals`). `ToArray()` returns an unprotected copy you must clear yourself. Mnemonics are kept
as UTF-8 in a `SecureBuffer`; `RevealMnemonic()` creates a `string` only when you ask.
