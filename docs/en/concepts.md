# Concepts

[← Documentation](index.md) · [Bahasa Indonesia](../id/konsep.md)

## Amounts are integers

`Amount` stores an integer number of base units (wei, satoshi, lamports, planck, uatom) and the number of decimals
of the display unit. Nothing is ever converted through `double`.

```csharp
Amount a = Amount.Parse("0.1", decimals: 18);    // 100000000000000000 wei, exactly
Amount b = Amount.FromGwei(21_000);              // stored as wei
Console.WriteLine((a + b).ToString("ETH"));      // "0.100021 ETH"

Amount.Parse("0.123456789", 8);                  // FormatException: more than 8 decimals
UnitConverter.Convert("1.5", "ether", "gwei");   // "1500000000"
```

`WithDecimals(n)` rescales and throws instead of losing precision. Amounts with different decimals compare by value.

## Addresses

`Address` is a string plus a `ChainId`. Hex (`0x…`) addresses compare case-insensitively; Base58, Bech32 and SS58
compare exactly because their case is significant. Each chain package has its own validator
(`EvmAddress.IsValid`, `BitcoinAddress.Parse`, `Ss58Address.Decode`, …); `ChainCatalog.DetectAddress` tries them all.

## Accounts and signers

Each chain has an account type (`EvmAccount`, `BitcoinAccount`, `SolanaAccount`, `PolkadotAccount`,
`CosmosAccount`) that holds its key in a `SecureBuffer` and exposes address, public key and signing helpers.
`CreateSigner()` returns an `ISigner` — the abstraction transaction builders accept, so you can substitute an HSM,
a hardware wallet or a remote signer without touching chain code:

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

ECDSA and Schnorr signers sign 32-byte digests; Ed25519 and sr25519 sign message bytes.

## Chain clients

Every client implements `IChainClient`:

```csharp
ValueTask<Amount> GetBalanceAsync(Address address, CancellationToken ct = default);
ValueTask<TxHash> SendRawTransactionAsync(ReadOnlyMemory<byte> signedTx, CancellationToken ct = default);
ValueTask<TxReceipt?> GetReceiptAsync(TxHash hash, CancellationToken ct = default);   // null while pending
ValueTask<ulong> GetBlockNumberAsync(CancellationToken ct = default);
```

Extensions add `WaitForReceiptAsync(hash, pollInterval, timeout)` and `WatchBlocksAsync()` (an
`IAsyncEnumerable<BlockInfo>`). Chain-specific clients add much more (`EstimateFeesAsync`, `GetUtxosAsync`,
`GetMetadataAsync`, `SimulateAsync`…).

## Errors

| Exception | Meaning |
| --- | --- |
| `RpcException` | The node answered with an error. `Code`, `RpcMessage`, `ErrorData` (e.g. EVM revert data) and `Method` are populated. |
| `FormatException` | Invalid input: address, mnemonic, hex, amount precision, ABI value. |
| `CryptographicException` | Invalid key, wrong keystore password, failed signature operation. |
| `PlatformNotSupportedException` | sr25519 requested without the native library. |
| `TimeoutException` | `WaitForReceiptAsync` gave up. |

## Backends

`CryptoNative` is the single gateway to every primitive. It runs in one of three modes:

| `CryptoBackend` | Behaviour |
| --- | --- |
| `Auto` (default) | Rust when it loads; SHA-2/HMAC/PBKDF2 use the OS's hardware-accelerated implementation |
| `Native` | Always Rust; throws `DllNotFoundException` if the library is missing |
| `Managed` | Always pure C# (not constant-time; for portability and testing) |

Set it globally (`CryptoNative.Backend = …` or `CRYPTONET_BACKEND=native`) or for one async flow:

```csharp
using (CryptoNative.UseBackend(CryptoBackend.Managed))
{
    byte[] h = CryptoNative.Keccak256(data);   // same bytes as the Rust path
}
```

Next: [Chains](chains.md).
