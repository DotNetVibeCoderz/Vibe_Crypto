# Dompet dan kunci

[← Dokumentasi](index.md) · [English](../en/wallet-and-keys.md)

## Mnemonic (BIP-39)

```csharp
string phrase = Mnemonic.Generate(24);              // 12, 15, 18, 21 atau 24 kata
bool ok = Mnemonic.Validate(phrase);                // daftar kata + checksum
byte[] entropy = Mnemonic.ToEntropy(phrase);
byte[] seed = Mnemonic.ToSeed(phrase, "passphrase opsional");   // PBKDF2-HMAC-SHA512 ×2048, dinormalisasi NFKD
```

Input dinormalisasi (spasi berlebih, tab, huruf kapital) sebelum divalidasi. Hanya daftar kata bahasa Inggris yang disertakan.

## Derivasi HD

`HdWallet` menyimpan tiga root yang diturunkan dari satu frasa:

| Root | Dipakai oleh | Gaya path |
| --- | --- | --- |
| BIP-32 secp256k1 | EVM, Bitcoin, Cosmos | `m/44'/60'/0'/0/0` |
| SLIP-10 ed25519 | Solana | `m/44'/501'/0'/0'` (semua hardened) |
| substrate-bip39 | Polkadot, Kusama | `//polkadot//0/soft` |

```csharp
using var wallet = HdWallet.FromMnemonic(phrase);
using HdKey key = wallet.DerivePath("m/84'/0'/0'/0/5");
string xprv = key.ToExtendedPrivateKey();
string zpub = wallet.GetBitcoinAccountXpub(BitcoinAddressType.SegWitP2WPKH);   // watch-only
using HdKey parsed = HdKey.ParseExtendedPrivateKey(xprv);
```

Path standar tersedia sebagai `DerivationPath.Standard.*`. Setiap kunci perantara di-dispose selama derivasi;
dispose kunci yang Anda terima.

### Alamat referensi untuk frasa uji BIP-39

`abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about`

| Chain | Path | Alamat |
| --- | --- | --- |
| Ethereum | m/44'/60'/0'/0/0 | `0x9858EfFD232B4033E47d90003D41EC34EcaEda94` |
| Bitcoin legacy | m/44'/0'/0'/0/0 | `1LqBGSKuX5yYUonjxT5qGfpUsXKYYWeabA` |
| Bitcoin SegWit | m/84'/0'/0'/0/0 | `bc1qcr8te4kr609gcawutmrza0j4xv80jy8z306fyu` |
| Bitcoin Taproot | m/86'/0'/0'/0/0 | `bc1p5cyxnuxmeuwuvkwfem96lqzszd02n6xdcjrs20cac6yqjjwudpxqkedrcr` |
| Solana | m/44'/501'/0'/0' | `HAgk14JpMQLgt6rVgv7cBQFJWFto5Dqxi472uT3DKpqk` |
| Cosmos Hub | m/44'/118'/0'/0/0 | `cosmos19rl4cm2hmr8afy4kldpxz3fka4jguq0auqdal4` |

Semua ini diuji di test suite. Jangan pernah mengirim dana ke alamat-alamat ini; frasanya publik.

## Keystore (Web3 Secret Storage v3)

```csharp
string json = KeyStore.Encrypt(privateKey, password, address, KeyStoreKdf.Standard);  // scrypt N=2^18, r=8, p=1
using SecureBuffer key = KeyStore.Decrypt(json, password);    // scrypt atau PBKDF2; AES-128-CTR; MAC Keccak
```

Formatnya sama dengan yang dipakai geth, MetaMask, MyEtherWallet dan Nethereum. `KeyStoreKdf.Light` (N=2^12) lebih
cepat untuk pengujian; `new KeyStoreKdf.Pbkdf2Kdf(262_144)` memilih PBKDF2. Kata sandi yang salah melempar
`CryptographicException` (MAC tidak cocok) tanpa membocorkan apa pun.

## Secure buffer

`SecureBuffer` dialokasikan di pinned object heap (GC tidak pernah menyalinnya), di-nol-kan saat `Dispose`, dan
dibandingkan secara constant-time (`ContentEquals`). `ToArray()` mengembalikan salinan tanpa proteksi yang harus Anda
bersihkan sendiri. Mnemonic disimpan sebagai UTF-8 di `SecureBuffer`; `RevealMnemonic()` hanya membuat `string`
ketika Anda memintanya.
