# Konsep

[← Dokumentasi](index.md) · [English](../en/concepts.md)

## Jumlah adalah bilangan bulat

`Amount` menyimpan bilangan bulat satuan dasar (wei, satoshi, lamports, planck, uatom) beserta jumlah desimal satuan
tampilan. Tidak ada konversi lewat `double`.

```csharp
Amount a = Amount.Parse("0.1", decimals: 18);    // tepat 100000000000000000 wei
Amount b = Amount.FromGwei(21_000);              // disimpan dalam wei
Console.WriteLine((a + b).ToString("ETH"));      // "0.100021 ETH"

Amount.Parse("0.123456789", 8);                  // FormatException: lebih dari 8 desimal
UnitConverter.Convert("1.5", "ether", "gwei");   // "1500000000"
```

`WithDecimals(n)` mengubah skala dan melempar exception alih-alih kehilangan presisi. Jumlah dengan desimal berbeda
dibandingkan berdasarkan nilainya.

## Alamat

`Address` adalah string ditambah `ChainId`. Alamat hex (`0x…`) dibandingkan tanpa memedulikan huruf besar/kecil;
Base58, Bech32 dan SS58 dibandingkan persis karena huruf besar/kecil bermakna. Setiap paket chain punya validatornya
sendiri (`EvmAddress.IsValid`, `BitcoinAddress.Parse`, `Ss58Address.Decode`, …); `ChainCatalog.DetectAddress`
mencoba semuanya.

## Akun dan signer

Setiap chain punya tipe akun (`EvmAccount`, `BitcoinAccount`, `SolanaAccount`, `PolkadotAccount`, `CosmosAccount`)
yang menyimpan kunci di `SecureBuffer` dan menyediakan alamat, public key serta fungsi tanda tangan. `CreateSigner()`
mengembalikan `ISigner` — abstraksi yang diterima pembangun transaksi, sehingga Anda bisa menggantinya dengan HSM,
hardware wallet atau signer jarak jauh tanpa mengubah kode chain:

```csharp
public interface ISigner : IAsyncDisposable
{
    IChain Chain { get; }
    Address Address { get; }
    ReadOnlyMemory<byte> PublicKey { get; }
    SignatureScheme Scheme { get; }    // Secp256k1Ecdsa, Secp256k1Schnorr, Ed25519, Sr25519
    ValueTask<Signature> SignAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default);
}
```

Signer ECDSA dan Schnorr menandatangani digest 32 byte; Ed25519 dan sr25519 menandatangani byte pesan.

## Client chain

Setiap client mengimplementasikan `IChainClient`:

```csharp
ValueTask<Amount> GetBalanceAsync(Address address, CancellationToken ct = default);
ValueTask<TxHash> SendRawTransactionAsync(ReadOnlyMemory<byte> signedTx, CancellationToken ct = default);
ValueTask<TxReceipt?> GetReceiptAsync(TxHash hash, CancellationToken ct = default);   // null selama pending
ValueTask<ulong> GetBlockNumberAsync(CancellationToken ct = default);
```

Extension menambahkan `WaitForReceiptAsync(hash, pollInterval, timeout)` dan `WatchBlocksAsync()`
(`IAsyncEnumerable<BlockInfo>`). Client per chain menambahkan jauh lebih banyak (`EstimateFeesAsync`,
`GetUtxosAsync`, `GetMetadataAsync`, `SimulateAsync`…).

## Error

| Exception | Arti |
| --- | --- |
| `RpcException` | Node membalas dengan error. `Code`, `RpcMessage`, `ErrorData` (mis. data revert EVM) dan `Method` terisi. |
| `FormatException` | Input tidak valid: alamat, mnemonic, hex, presisi jumlah, nilai ABI. |
| `CryptographicException` | Kunci tidak valid, kata sandi keystore salah, operasi tanda tangan gagal. |
| `PlatformNotSupportedException` | sr25519 diminta tanpa library native. |
| `TimeoutException` | `WaitForReceiptAsync` menyerah. |

## Backend

`CryptoNative` adalah gerbang tunggal ke setiap primitif. Ada tiga mode:

| `CryptoBackend` | Perilaku |
| --- | --- |
| `Auto` (default) | Rust bila termuat; SHA-2/HMAC/PBKDF2 memakai implementasi OS yang diakselerasi hardware |
| `Native` | Selalu Rust; melempar `DllNotFoundException` jika library tidak ada |
| `Managed` | Selalu C# murni (bukan constant-time; untuk portabilitas dan pengujian) |

Atur secara global (`CryptoNative.Backend = …` atau `CRYPTONET_BACKEND=native`) atau untuk satu alur async:

```csharp
using (CryptoNative.UseBackend(CryptoBackend.Managed))
{
    byte[] h = CryptoNative.Keccak256(data);   // byte yang sama dengan jalur Rust
}
```

Berikutnya: [Chain](chain.md).
