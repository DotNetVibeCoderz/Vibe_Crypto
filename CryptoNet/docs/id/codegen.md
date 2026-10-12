# Client kontrak dari ABI dan IDL

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil. · [English](../en/codegen.md)

`Crypto.Net.Generators` mengubah deskripsi kontrak menjadi C# bertipe saat build:

| Input | Output |
| --- | --- |
| `*.abi.json`: array ABI Solidity, atau artifact Hardhat/Foundry yang punya property `abi` | `public partial class <Nama> : EvmContract` |
| `*.idl.json`: IDL Anchor, format 0.30+ atau format lama `isMut`/`isSigner` | `public static partial class <Nama>Program` |

```bash
dotnet add package Crypto.Net.Generators     # development dependency, tidak ikut terkirim bersama aplikasi
cp out/MyToken.sol/MyToken.json Contracts/MyToken.abi.json
dotnet build
```

Atau mulai dari template: `dotnet new cnet-contract-client`.

## EVM

```csharp
var token = new MyToken(rpc, "0x…");
BigInteger balance = await token.BalanceOfAsync(owner);              // view → nilai bertipe
TxHash tx = await token.TransferAsync(signer, to, 1_000_000);        // simulasi, tanda tangan, broadcast
byte[] data = MyToken.EncodeTransfer(to, 1_000_000);                 // calldata untuk multisig / batching
await foreach (var log in ws.SubscribeEvmLogsAsync(token.Address, [MyToken.Events.Transfer.Topic0]))
    if (MyToken.TransferEvent.TryDecode(log, out var e)) Console.WriteLine($"{e!.From} → {e.To}: {e.Value}");
```

| Solidity | C# |
| --- | --- |
| `uintN` / `intN` | `BigInteger` |
| `address`, `string` | `string` |
| `bool` | `bool` |
| `bytes`, `bytesN` | `byte[]` |
| `T[]`, `T[k]` | `T[]` |
| `struct` (tuple) | `record` hasil generate, dinamai dari `internalType` |

Cara bagian ABI lainnya ditangani:

- **View** mengembalikan nilainya, atau record `…Output` jika output-nya lebih dari satu. View menerima
  `block = "latest"` untuk membaca data historis.
- **Fungsi payable** menerima `valueWei`.
- **Overload** tetap menjadi overload. Jika dua overload menghasilkan signature C# yang sama, yang kedua diberi
  akhiran angka.
- **Event** menjadi definisi `EvmEvent` (`Events.Transfer`) ditambah record `…Event` bertipe. Nilai dinamis yang
  indexed di-decode menjadi hash keccak-256-nya.

## Solana / Anchor

```csharp
var ix = CounterProgram.Initialize(counter, user, start: 42, label: "hits", mode: CounterProgram.Mode.Auto);
var tx = new SolanaTransaction(user, blockhash).Add(ix);

CounterProgram.Counter? state = await CounterProgram.FetchCounterAsync(rpc, counter);
string? alasan = CounterProgram.ErrorMessage(6000);
```

- **Instruksi:**
  - Builder menerima key akun lebih dulu, lalu argumen. Akun opsional diletakkan paling akhir.
  - Discriminator 8 byte diambil dari IDL. IDL lama tidak menyertakannya, sehingga dihitung sebagai
    `sha256("global:<nama_snake>")`.
  - Akun dengan alamat tetap (misalnya System Program) diisi otomatis. Akun opsional yang tidak diisi menjadi program
    id, sesuai konvensi Anchor.
- **Argumen dan data akun** di-encode Borsh dengan `BorshWriter` / `BorshReader` dari Crypto.Net.Solana.
  - Tipe yang didukung: integer sampai 256 bit, `f32`/`f64`, `bool`, `string`, `bytes`, `pubkey`, `vec`, `option`,
    `coption`, array tetap, struct, enum unit dan enum berfield.
- **Akun:** `Decode<Nama>` memeriksa discriminator; `Fetch<Nama>Async` membaca akun dari RPC.

## `cnet gen`

Generator yang sama bisa menulis file source jika Anda lebih suka meng-commit kode hasil generate:

```bash
cnet gen MyToken.abi.json --namespace MyApp.Contracts          # → MyToken.g.cs
cnet gen target/idl/counter.json --kind anchor -n Counter -o Counter.g.cs
```

Input yang rusak atau tidak didukung menghasilkan error build `CNETGEN001`, yang menyebut nama file dan masalahnya.
Kode hasil generate adalah C# biasa tanpa reflection, sehingga cocok untuk trimming dan NativeAOT.
