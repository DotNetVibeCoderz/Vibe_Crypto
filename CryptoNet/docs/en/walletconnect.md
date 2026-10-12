# WalletConnect v2

> Built by Gravicode Studios, led by Kang Fadhil. · [Bahasa Indonesia](../id/walletconnect.md)

`Crypto.Net.WalletConnect` connects your app to the user's mobile or browser wallet (MetaMask, Trust, Rainbow,
Phantom's EVM side, …) through Reown's WalletConnect relay. Keys stay in the wallet: your app sends requests, and the
user approves them on their phone. It also works the other way round: a .NET wallet can answer apps.

```bash
dotnet add package Crypto.Net.WalletConnect
```

You need a project id from the [Reown dashboard](https://dashboard.reown.com). Keep it in configuration, for example
`CRYPTONET_WALLETCONNECT_PROJECT_ID`.

## App (dapp) side

```csharp
await using var wc = await WalletConnectClient.CreateAsync(new()
{
    ProjectId = projectId,
    Metadata = new("My app", "Pays invoices", "https://example.org", ["https://example.org/icon.png"]),
});

var pairing = await wc.ConnectAsync(WalletConnectEthereum.Namespace(1, 11155111));
ShowQrCode(pairing.Uri);                               // wc:…@2?relay-protocol=irn&symKey=…
WalletConnectSession session = await pairing.Approval; // the user scanned and approved

var eth = new WalletConnectEthereum(session, chainId: 11155111);
string signature = await eth.PersonalSignAsync("Sign in to example.org");
string txHash = await eth.SendTransactionAsync("0x…", Amount.FromEther(0.01m).BaseUnits);
JsonNode? any = await session.RequestAsync("wallet_switchEthereumChain", new JsonArray(new JsonObject { ["chainId"] = "0x1" }), "eip155:1");

session.EventReceived += (name, data, chain) => Console.WriteLine($"{name} on {chain}");   // accountsChanged, chainChanged
await session.DisconnectAsync();
```

A rejection in the wallet throws `WalletConnectException` with `IsUserRejection`.

## Wallet side

```csharp
await using var wallet = await WalletConnectWallet.CreateAsync(new() { ProjectId = projectId, Metadata = new("My wallet", "…", "https://example.org") });
wallet.RequestHandler = async request => request.Method switch
{
    "personal_sign" => JsonValue.Create(await SignAfterUserConfirms(request)),
    _ => throw new WalletConnectException(4001, "User rejected the request."),
};

var proposal = await wallet.PairAsync(scannedUri);
var session = await wallet.ApproveAsync(proposal, new Dictionary<string, WalletConnectNamespace>
{
    ["eip155"] = new(["eip155:1"], ["personal_sign", "eth_sendTransaction"], ["accountsChanged"], [$"eip155:1:{address}"]),
});
```

## How it works and how it is tested

- **Key agreement and topics:** X25519 with HKDF-SHA256 (X25519 is in the Rust core, with a managed fallback). Topics
  are `sha256(symKey)`.
- **Messages:** ChaCha20-Poly1305 envelopes of type 0 and 1. The relay connection authenticates with an EdDSA
  `did:key` JWT.
- **Byte-for-byte checks:** unit tests compare key agreement, envelopes, the JWT and the pairing URI with
  `@walletconnect/utils` and `relay-auth`.
- **Live test:** with `CRYPTONET_WALLETCONNECT_PROJECT_ID` set, a test pairs a Crypto.Net app with a Crypto.Net wallet
  through the real relay. It then signs, checks a rejection, pings and disconnects.
- **Interop:** before release, both directions were run against Reown's official `@walletconnect/sign-client`. The
  official wallet signed for a Crypto.Net app, and the official app verified a Crypto.Net wallet's signature.

Not supported yet: one-click auth (`wc_sessionAuthenticate`), link mode, and restoring sessions after a restart
(sessions live as long as the client object).
