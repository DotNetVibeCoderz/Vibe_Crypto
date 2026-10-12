# Roslyn analyzer

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil. · [English](../en/analyzers.md)

`Crypto.Net.Analyzers` memeriksa kode saat Anda mengetik dan saat build. Analyzer ini menangkap kesalahan yang bisa
merugikan secara finansial di aplikasi kripto.

```bash
dotnet add package Crypto.Net.Analyzers
```

Paket ini adalah development dependency. Tidak ada yang ditambahkan ke output, dan aturannya berjalan di Visual Studio,
Rider, VS Code (C# Dev Kit) dan `dotnet build`.

| Aturan | Tingkat | Mendeteksi |
| --- | --- | --- |
| [CNET001](#cnet001) | Warning | Private key, string WIF, kunci xprv dan frasa BIP-39 yang ditulis langsung di source |
| [CNET002](#cnet002) | Warning | Field dan property yang menyimpan rahasia dalam `string` |
| [CNET003](#cnet003) | Warning | `Amount` yang dibuat dari `double` atau `float` |
| [CNET004](#cnet004) | Info | Client mainnet yang dibuat di build Debug |
| [CNET005](#cnet005) | Info | Panggilan RPC yang mengabaikan `CancellationToken` yang sudah tersedia (ada code fix) |

Ubah tingkatnya di `.editorconfig`:

```ini
[*.cs]
dotnet_diagnostic.CNET004.severity = warning
dotnet_diagnostic.CNET001.severity = error
```

## CNET001

Literal yang berupa frasa BIP-39 lengkap (12–24 kata dari daftar bahasa Inggris) atau extended private key selalu
dilaporkan. String hex 64 karakter atau string WIF dilaporkan jika konteksnya menunjukkan kunci: variabel, field,
property atau parameter yang namanya mengandung `privateKey`, `secret`, `mnemonic`, `seed`, `password` dan
sejenisnya. Hash transaksi dan konstanta hex lainnya tidak dilaporkan.

```csharp
using var wallet = HdWallet.FromMnemonic("legal winner thank year wave …");   // CNET001
using var wallet = HdWallet.FromMnemonic(Environment.GetEnvironmentVariable("WALLET_MNEMONIC")!);
```

Muat rahasia dari user secrets, environment variable, keystore Web3 (`KeyStore.Decrypt`) atau hardware signer.
Test vector adalah pengecualian yang sah. Nonaktifkan dengan `#pragma warning disable CNET001` di file test.

## CNET002

String tidak bisa dihapus dari memori. Setiap salinan mnemonic atau private key tetap ada di memori sampai garbage
collector memakai ulang ruangnya, dan muncul di crash dump. Simpan rahasia di `SecureBuffer` (di-pin dan dinolkan saat
dispose) atau di `byte[]` yang Anda bersihkan sendiri. Nama yang berakhiran `Name`, `Path`, `File`, `Hint`, `Label`,
`EnvVar` atau `Variable` tidak dilaporkan.

```csharp
public string Mnemonic { get; set; }                  // CNET002
public SecureBuffer PrivateKey { get; }               // aman
public string MnemonicEnvVar { get; set; }            // aman: berisi nama variabel
```

## CNET003

`double` dan `float` tidak bisa merepresentasikan `0.1` secara tepat. Saldo yang dihitung lewat tipe ini bisa meleset
satu base unit. Itu cukup untuk menggagalkan transfer dengan jumlah pas atau menyisakan dust. Pakai `decimal`,
`Amount.Parse` atau base unit.

```csharp
Amount.FromEther((decimal)price * 0.1);                // CNET003
Amount.FromEther(0.1m);                                // aman
Amount.Parse(userInput, 18);                           // aman
```

## CNET004

Di build Debug (`Optimize=false`), membuat client chain untuk mainnet seperti `EvmChain.Ethereum`,
`BitcoinNetwork.Mainnet`, `SolanaChain.MainnetBeta`, `PolkadotChain.Polkadot` atau `CosmosChain.CosmosHub` akan
dilaporkan. Kembangkan di Sepolia/Hoodi, testnet/signet, devnet, Westend/Paseo atau testnet Cosmos, lalu ganti
jaringan lewat konfigurasi.

## CNET005

Aturan ini berlaku jika sebuah method memanggil method `IChainClient`, `JsonRpcClient` atau `WebSocketRpcClient` tanpa
`CancellationToken` opsionalnya, padahal token sudah tersedia sebagai parameter method, lambda atau local function.
Tanpa token, endpoint RPC yang macet menahan permintaan (dan request HTTP yang memicunya) sampai timeout. Code fix
menambahkan `ct: cancellationToken`.

```csharp
async Task<ulong> HeadAsync(EvmRpcClient rpc, CancellationToken cancellationToken) =>
    await rpc.GetBlockNumberAsync();                   // CNET005 → GetBlockNumberAsync(ct: cancellationToken)
```
