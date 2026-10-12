# Contract clients from ABI and IDL

> Built by Gravicode Studios, led by Kang Fadhil. · [Bahasa Indonesia](../id/codegen.md)

`Crypto.Net.Generators` turns contract descriptions into typed C# while you build:

| Input | Output |
| --- | --- |
| `*.abi.json`: a Solidity ABI array, or a Hardhat/Foundry artifact with an `abi` property | `public partial class <Name> : EvmContract` |
| `*.idl.json`: an Anchor IDL, in the 0.30+ format or the older `isMut`/`isSigner` format | `public static partial class <Name>Program` |

```bash
dotnet add package Crypto.Net.Generators     # development dependency, nothing ships with your app
cp out/MyToken.sol/MyToken.json Contracts/MyToken.abi.json
dotnet build
```

Or start from the template: `dotnet new cnet-contract-client`.

## EVM

```csharp
var token = new MyToken(rpc, "0x…");
BigInteger balance = await token.BalanceOfAsync(owner);              // view → typed return value
TxHash tx = await token.TransferAsync(signer, to, 1_000_000);        // simulate, sign, broadcast
byte[] data = MyToken.EncodeTransfer(to, 1_000_000);                 // calldata for multisigs / batching
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
| `struct` (tuple) | a generated `record`, named after `internalType` |

How the rest of the ABI is handled:

- **Views** return the value, or an `…Output` record when there are several outputs. They take
  `block = "latest"` for historical reads.
- **Payable functions** take `valueWei`.
- **Overloads** stay overloads. When two map to the same C# signature, the second gets a numeric suffix.
- **Events** become `EvmEvent` definitions (`Events.Transfer`) plus typed `…Event` records. Indexed dynamic values
  decode to their keccak-256 hash.

## Solana / Anchor

```csharp
var ix = CounterProgram.Initialize(counter, user, start: 42, label: "hits", mode: CounterProgram.Mode.Auto);
var tx = new SolanaTransaction(user, blockhash).Add(ix);

CounterProgram.Counter? state = await CounterProgram.FetchCounterAsync(rpc, counter);
string? why = CounterProgram.ErrorMessage(6000);
```

- **Instructions:**
  - Builders take account keys first, then the arguments. Optional accounts come last.
  - The 8-byte discriminator is taken from the IDL. Older IDLs don't include it, so it is computed as
    `sha256("global:<snake_name>")`.
  - Accounts with a fixed address (the System Program, for example) are filled in automatically. An omitted optional
    account becomes the program id, as Anchor expects.
- **Arguments and account data** are Borsh-encoded with `BorshWriter` / `BorshReader` from Crypto.Net.Solana.
  - Supported types: integers up to 256 bits, `f32`/`f64`, `bool`, `string`, `bytes`, `pubkey`, `vec`, `option`,
    `coption`, fixed arrays, structs, unit enums and enums with fields.
- **Accounts:** `Decode<Name>` checks the discriminator; `Fetch<Name>Async` reads the account from RPC.

## `cnet gen`

The same generator writes a source file when you'd rather check generated code in:

```bash
cnet gen MyToken.abi.json --namespace MyApp.Contracts          # → MyToken.g.cs
cnet gen target/idl/counter.json --kind anchor -n Counter -o Counter.g.cs
```

A malformed or unsupported input produces build error `CNETGEN001`, which names the file and the problem.
Generated code is plain C# with no reflection, so it works with trimming and NativeAOT.
