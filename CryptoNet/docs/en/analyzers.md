# Roslyn analyzers

> Built by Gravicode Studios, led by Kang Fadhil. · [Bahasa Indonesia](../id/analyzer.md)

`Crypto.Net.Analyzers` checks your code while you type and while you build. It catches the mistakes that cost money
in crypto applications.

```bash
dotnet add package Crypto.Net.Analyzers
```

It is a development dependency. Nothing is added to your output, and the rules run in Visual Studio, Rider,
VS Code (C# Dev Kit) and `dotnet build`.

| Rule | Severity | Finds |
| --- | --- | --- |
| [CNET001](#cnet001) | Warning | Private keys, WIF strings, xprv keys and BIP-39 phrases hard-coded in source |
| [CNET002](#cnet002) | Warning | Fields and properties that keep a secret in a `string` |
| [CNET003](#cnet003) | Warning | `Amount` built from a `double` or `float` |
| [CNET004](#cnet004) | Info | Mainnet clients created in a Debug build |
| [CNET005](#cnet005) | Info | RPC calls that ignore a `CancellationToken` already in scope (code fix available) |

Change a severity in `.editorconfig`:

```ini
[*.cs]
dotnet_diagnostic.CNET004.severity = warning
dotnet_diagnostic.CNET001.severity = error
```

## CNET001

A literal that is a complete BIP-39 phrase (12–24 words from the English list) or an extended private key is always
reported. A 64-character hex string or a WIF string is reported when its context suggests a key: a variable,
field, property or parameter whose name contains `privateKey`, `secret`, `mnemonic`, `seed`, `password` and similar.
Transaction hashes and other hex constants are left alone.

```csharp
using var wallet = HdWallet.FromMnemonic("legal winner thank year wave …");   // CNET001
using var wallet = HdWallet.FromMnemonic(Environment.GetEnvironmentVariable("WALLET_MNEMONIC")!);
```

Load secrets from user secrets, environment variables, a Web3 keystore (`KeyStore.Decrypt`) or a hardware signer.
Test vectors are a legitimate exception. Suppress them with `#pragma warning disable CNET001` in the test file.

## CNET002

Strings cannot be wiped. Every copy of a mnemonic or private key stays in memory until the garbage collector reuses
the space, and it shows up in crash dumps. Keep secrets in `SecureBuffer`, which is pinned and zeroed on dispose,
or in a `byte[]` you clear yourself. Names that end in `Name`, `Path`, `File`, `Hint`, `Label`, `EnvVar` or
`Variable` are not reported.

```csharp
public string Mnemonic { get; set; }                  // CNET002
public SecureBuffer PrivateKey { get; }               // fine
public string MnemonicEnvVar { get; set; }            // fine: it holds a variable name
```

## CNET003

`double` and `float` cannot represent `0.1` exactly. A balance computed through them can be off by one base unit.
That is enough to fail an exact-amount transfer or leave dust behind. Use `decimal`, `Amount.Parse` or base units.

```csharp
Amount.FromEther((decimal)price * 0.1);                // CNET003
Amount.FromEther(0.1m);                                // fine
Amount.Parse(userInput, 18);                           // fine
```

## CNET004

In a Debug build (`Optimize=false`), creating a chain client for a mainnet such as `EvmChain.Ethereum`,
`BitcoinNetwork.Mainnet`, `SolanaChain.MainnetBeta`, `PolkadotChain.Polkadot` or `CosmosChain.CosmosHub` is
reported. Develop on Sepolia/Hoodi, testnet/signet, devnet, Westend/Paseo or the Cosmos testnet, and switch networks
through configuration.

## CNET005

The rule applies when a method calls an `IChainClient`, `JsonRpcClient` or `WebSocketRpcClient` method without its
optional `CancellationToken`, and a token is already available as a parameter of the method, lambda or local
function. Without the token, a hung RPC endpoint keeps the request (and the HTTP request that triggered it) alive
until the timeout. The code fix adds `ct: cancellationToken`.

```csharp
async Task<ulong> HeadAsync(EvmRpcClient rpc, CancellationToken cancellationToken) =>
    await rpc.GetBlockNumberAsync();                   // CNET005 → GetBlockNumberAsync(ct: cancellationToken)
```
