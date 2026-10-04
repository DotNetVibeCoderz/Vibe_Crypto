using System.Security.Cryptography;
using System.Text;
using Crypto.Net.Bitcoin;
using Crypto.Net.Core;
using Crypto.Net.Cosmos;
using Crypto.Net.Evm;
using Crypto.Net.Extensions;
using Crypto.Net.Native;
using Crypto.Net.Polkadot;
using Crypto.Net.Solana;
using Crypto.Net.Wallet;

// Crypto.Net Gallery — interactive developer studio.
// Built by Gravicode Studios, led by Kang Fadhil.
//
// Safe sandbox: the Gallery never broadcasts transactions. Keys it generates are demo keys
// and live only in memory for the duration of a request.

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);
if (builder.Configuration["urls"] is null && Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)
    builder.WebHost.UseUrls("http://localhost:5080");

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/status", () => new
{
    nativeLoaded = NativeLoader.IsNativeAvailable,
    abiVersion = $"0x{NativeLoader.AbiVersion:X8}",
    backend = CryptoNative.EffectiveBackend.ToString(),
    rid = NativeLoader.RuntimeIdentifier,
    runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    loadError = NativeLoader.LoadError,
    rpcProviders = RpcProviders.ConfiguredInEnvironment(),
    version = typeof(CryptoNative).Assembly.GetName().Version?.ToString(3),
    attribution = "Gravicode Studios · Kang Fadhil",
});

// ---------------------------------------------------------------- wallet

api.MapPost("/wallet/generate", (WalletRequest req) =>
{
    using var wallet = HdWallet.Generate(req.Words is 12 or 15 or 18 or 21 or 24 ? req.Words.Value : 12, req.Passphrase);
    return Results.Ok(DescribeWallet(wallet, req.Testnet ?? true, 1));
});

api.MapPost("/wallet/restore", (WalletRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Mnemonic) || !Mnemonic.Validate(req.Mnemonic))
        return Problem("That phrase is not a valid BIP-39 mnemonic. Check for a misspelt word or a missing word.");
    using var wallet = HdWallet.FromMnemonic(req.Mnemonic, req.Passphrase);
    return Results.Ok(DescribeWallet(wallet, req.Testnet ?? true, Math.Clamp(req.Count ?? 1, 1, 5)));
});

// ---------------------------------------------------------------- address / units

api.MapPost("/address/inspect", (AddressRequest req) =>
{
    string address = req.Address?.Trim() ?? "";
    var matches = ChainCatalog.DetectAddress(address);
    var details = new List<object>();
    if (matches.Any(m => m.Family == ChainFamily.Bitcoin))
    {
        foreach (var net in BitcoinNetwork.All)
            if (BitcoinAddress.TryParse(address, net, out var info))
            {
                details.Add(new { label = "Output script", value = Convert.ToHexStringLower(info!.ScriptPubKey) });
                details.Add(new { label = "Payload", value = Convert.ToHexStringLower(info.Payload) });
                break;
            }
    }
    if (matches.Any(m => m.Family == ChainFamily.Evm))
        details.Add(new { label = "EIP-55 form", value = EvmAddress.ToChecksumAddress(address) });
    if (matches.Any(m => m.Family == ChainFamily.Polkadot))
    {
        var (prefix, key) = Ss58Address.Decode(address);
        details.Add(new { label = "Account id", value = HexUtil.Encode(key) });
        details.Add(new { label = "On Polkadot", value = Ss58Address.Encode(key, 0) });
        details.Add(new { label = "On Kusama", value = Ss58Address.Encode(key, 2) });
        details.Add(new { label = "Generic (42)", value = Ss58Address.Encode(key, 42) });
    }
    if (matches.Any(m => m.Family == ChainFamily.Cosmos))
    {
        details.Add(new { label = "On Osmosis", value = CosmosAddress.ConvertPrefix(address, "osmo") });
        details.Add(new { label = "On Cosmos Hub", value = CosmosAddress.ConvertPrefix(address, "cosmos") });
    }
    if (matches.Any(m => m.Family == ChainFamily.Solana))
        details.Add(new { label = "Raw key", value = Convert.ToHexStringLower(SolanaAddress.Decode(address)) });

    return Results.Ok(new { address, valid = matches.Count > 0, matches = matches.Select(m => new { family = m.Family.ToString(), m.Format }), details });
});

api.MapGet("/units", () => UnitConverter.Units.GroupBy(u => u.Value.Family)
    .Select(g => new { family = g.Key, units = g.Select(u => new { name = u.Key, decimals = u.Value.Decimals }) }));

api.MapPost("/convert", (ConvertRequest req) =>
{
    try
    {
        return Results.Ok(new { output = UnitConverter.Convert(req.Value ?? "", req.From ?? "", req.To ?? "") });
    }
    catch (Exception ex) when (ex is FormatException or ArgumentException)
    {
        return Problem(ex.Message);
    }
});

// ---------------------------------------------------------------- signing

api.MapPost("/sign", (SignRequest req) =>
{
    string message = req.Message ?? "";
    using var wallet = string.IsNullOrWhiteSpace(req.Mnemonic) ? HdWallet.Generate() : HdWallet.FromMnemonic(req.Mnemonic);
    try
    {
        switch (req.Scheme)
        {
            case "eip191":
            {
                using var acc = wallet.GetEvmAccount();
                byte[] sig = acc.SignMessage(message);
                string recovered = EvmMessageSigner.RecoverPersonalMessageSigner(Encoding.UTF8.GetBytes(message), sig);
                return Results.Ok(new SignResponse("secp256k1 · EIP-191", acc.Address.Value, HexUtil.Encode(EvmMessageSigner.HashPersonalMessage(message)), HexUtil.Encode(sig), recovered == acc.Address.Value, $"Recovered signer {recovered}"));
            }
            case "eip712":
            {
                using var acc = wallet.GetEvmAccount();
                byte[] digest = EvmMessageSigner.HashTypedData(message);
                byte[] sig = acc.SignTypedData(message);
                string recovered = EvmMessageSigner.RecoverTypedDataSigner(message, sig);
                return Results.Ok(new SignResponse("secp256k1 · EIP-712", acc.Address.Value, HexUtil.Encode(digest), HexUtil.Encode(sig), recovered == acc.Address.Value, $"Recovered signer {recovered}"));
            }
            case "ed25519":
            {
                using var acc = wallet.GetSolanaAccount(0, SolanaChain.Devnet);
                byte[] sig = acc.SignMessage(Encoding.UTF8.GetBytes(message));
                bool ok = CryptoNative.Ed25519Verify(acc.PublicKey.Span, Encoding.UTF8.GetBytes(message), sig);
                return Results.Ok(new SignResponse("Ed25519 · Solana", acc.Address.Value, null, HexUtil.Encode(sig), ok, "Verified against the public key"));
            }
            case "sr25519":
            {
                using var acc = wallet.GetPolkadotAccount("", PolkadotChain.Westend);
                byte[] sig = acc.SignMessage(message);
                bool ok = PolkadotAccount.VerifyMessage(message, sig, acc.PublicKey.Span);
                return Results.Ok(new SignResponse("sr25519 · Polkadot", acc.Address.Value, null, HexUtil.Encode(sig), ok, "Verified (signatures are randomized, so each run differs)"));
            }
            case "schnorr":
            {
                using var acc = wallet.GetBitcoinAccount(BitcoinAddressType.TaprootP2TR, 0, BitcoinNetwork.Testnet);
                using var key = wallet.DerivePath(acc.DerivationPath!);
                byte[] digest = CryptoNative.TaggedHash("Crypto.Net/message", Encoding.UTF8.GetBytes(message));
                byte[] sig = CryptoNative.SchnorrSign(key.PrivateKey.Span, digest, RandomNumberGenerator.GetBytes(32));
                byte[] xonly = CryptoNative.Secp256k1XOnlyPublicKey(key.PrivateKey.Span);
                return Results.Ok(new SignResponse("BIP-340 Schnorr", acc.Address.Value, HexUtil.Encode(digest), HexUtil.Encode(sig), CryptoNative.SchnorrVerify(xonly, digest, sig), "Verified against the x-only key"));
            }
            default:
                return Problem($"Unknown scheme '{req.Scheme}'");
        }
    }
    catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or PlatformNotSupportedException)
    {
        return Problem(ex.Message);
    }
});

// ---------------------------------------------------------------- keystore

api.MapPost("/keystore/encrypt", (KeystoreRequest req) =>
{
    if (string.IsNullOrEmpty(req.Password)) return Problem("Choose a password to encrypt the key with.");
    using var key = string.IsNullOrWhiteSpace(req.PrivateKey)
        ? SecureBuffer.FromBytesAndClear(RandomNumberGenerator.GetBytes(32))
        : SecureBuffer.FromHex(req.PrivateKey);
    using var account = new EvmAccount(key.Span);
    var kdf = req.Kdf == "pbkdf2" ? new KeyStoreKdf.Pbkdf2Kdf(262_144) : KeyStoreKdf.Light;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    string json = account.ExportKeyStore(req.Password, kdf);
    return Results.Ok(new { address = account.Address.Value, keystore = json, milliseconds = sw.ElapsedMilliseconds });
});

api.MapPost("/keystore/decrypt", (KeystoreRequest req) =>
{
    try
    {
        using var key = KeyStore.Decrypt(req.Keystore ?? "", req.Password ?? "");
        using var account = new EvmAccount(key.Span);
        return Results.Ok(new { address = account.Address.Value });
    }
    catch (CryptographicException)
    {
        return Problem("Wrong password, or the file was modified.");
    }
    catch (Exception ex) when (ex is FormatException or NotSupportedException or System.Text.Json.JsonException)
    {
        return Problem($"Not a Web3 v3 keystore: {ex.Message}");
    }
});

// ---------------------------------------------------------------- networks

api.MapGet("/networks", () => ChainCatalog.Entries.Select(e => new
{
    key = e.Key,
    family = e.Family.ToString(),
    name = e.Name,
    symbol = e.Chain.Symbol,
    testnet = e.IsTestnet,
    local = e.Key.Contains("local") || e.Key.Contains("regtest"),
}));

api.MapGet("/networks/{key}/head", async (string key, IHttpClientFactory http, CancellationToken ct) =>
{
    var entry = ChainCatalog.Find(key);
    if (entry is null) return Results.NotFound();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        using var client = ChainCatalog.CreateClient(entry, http: http.CreateClient());
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        ulong height = await client.GetBlockNumberAsync(timeout.Token);
        return Results.Ok(new { key, height, milliseconds = sw.ElapsedMilliseconds });
    }
    catch (Exception ex) when (ex is HttpRequestException or RpcException or OperationCanceledException or System.Text.Json.JsonException)
    {
        return Results.Ok(new { key, error = ex is OperationCanceledException ? "Endpoint did not answer within 12 s" : ex.Message, milliseconds = sw.ElapsedMilliseconds });
    }
});

api.MapGet("/networks/{key}/balance/{address}", async (string key, string address, IHttpClientFactory http, CancellationToken ct) =>
{
    var entry = ChainCatalog.Find(key);
    if (entry is null) return Results.NotFound();
    if (!ChainCatalog.IsValidAddress(entry, address)) return Problem($"That is not a valid {entry.Name} address.");
    try
    {
        using var client = ChainCatalog.CreateClient(entry, http: http.CreateClient());
        var amount = await client.GetBalanceAsync(new Address(address, entry.Chain.Id), ct);
        return Results.Ok(new { balance = amount.ToString(), symbol = entry.Chain.Symbol });
    }
    catch (Exception ex) when (ex is HttpRequestException or RpcException or System.Text.Json.JsonException)
    {
        return Problem($"{entry.Name} endpoint error: {ex.Message}");
    }
});

// ---------------------------------------------------------------- benchmark

api.MapPost("/benchmark", (BenchmarkRequest req) =>
{
    var results = CryptoBenchmark.Run(Math.Clamp(req.Scale ?? 0.5, 0.05, 3));
    return new { nativeAvailable = NativeLoader.IsNativeAvailable, results };
});

app.MapFallbackToFile("index.html");

Console.WriteLine("Crypto.Net Gallery — built by Gravicode Studios, led by Kang Fadhil");
app.Run();

static IResult Problem(string message) => Results.BadRequest(new { error = message });

static object DescribeWallet(HdWallet wallet, bool testnet, int count)
{
    var btc = testnet ? BitcoinNetwork.Testnet : BitcoinNetwork.Mainnet;
    var evm = testnet ? EvmChain.Sepolia : EvmChain.Ethereum;
    var sol = testnet ? SolanaChain.Devnet : SolanaChain.MainnetBeta;
    var dot = testnet ? PolkadotChain.Westend : PolkadotChain.Polkadot;
    var atom = testnet ? CosmosChain.CosmosHubTestnet : CosmosChain.CosmosHub;

    var accounts = new List<object>();
    for (uint i = 0; i < count; i++)
    {
        using (var a = wallet.GetEvmAccount(i, evm)) accounts.Add(Row("evm", evm, "secp256k1", a.DerivationPath!, a.Address.Value, i));
        using (var a = wallet.GetBitcoinAccount(BitcoinAddressType.SegWitP2WPKH, i, btc)) accounts.Add(Row("btc-segwit", btc, "secp256k1 · P2WPKH", a.DerivationPath!, a.Address.Value, i));
        using (var a = wallet.GetBitcoinAccount(BitcoinAddressType.TaprootP2TR, i, btc)) accounts.Add(Row("btc-taproot", btc, "Schnorr · P2TR", a.DerivationPath!, a.Address.Value, i));
        using (var a = wallet.GetSolanaAccount(i, sol)) accounts.Add(Row("sol", sol, "ed25519", a.DerivationPath!, a.Address.Value, i));
        if (NativeLoader.IsNativeAvailable)
        {
            string path = i == 0 ? "" : $"//{i}";
            using var a = wallet.GetPolkadotAccount(path, dot);
            accounts.Add(Row("dot", dot, "sr25519", path.Length == 0 ? "root (Polkadot.js)" : path, a.Address.Value, i));
        }
        using (var a = wallet.GetCosmosAccount(i, atom)) accounts.Add(Row("atom", atom, "secp256k1", a.DerivationPath!, a.Address.Value, i));
    }

    // Fingerprint drawn as the guilloché rosette. Derived from the seed, so the picture never reveals the seed.
    byte[] fingerprint = CryptoNative.TaggedHash("Crypto.Net/gallery-rosette", Encoding.UTF8.GetBytes(wallet.MasterKey.ToExtendedPublicKey()));

    return new
    {
        mnemonic = wallet.RevealMnemonic(),
        fingerprint = Convert.ToHexStringLower(fingerprint),
        masterFingerprint = wallet.MasterKey.Fingerprint.ToString("x8"),
        xpub = wallet.GetBitcoinAccountXpub(BitcoinAddressType.SegWitP2WPKH, btc),
        testnet,
        accounts,
    };

    static object Row(string id, IChain chain, string curve, string path, string address, uint index) =>
        new { id, chain = chain.Name, symbol = chain.Symbol, curve, path, address, index };
}

record WalletRequest(int? Words, string? Passphrase, bool? Testnet, string? Mnemonic, int? Count);
record AddressRequest(string? Address);
record ConvertRequest(string? Value, string? From, string? To);
record SignRequest(string? Scheme, string? Message, string? Mnemonic);
record SignResponse(string Scheme, string Signer, string? Digest, string Signature, bool Verified, string Note);
record KeystoreRequest(string? PrivateKey, string? Password, string? Kdf, string? Keystore);
record BenchmarkRequest(double? Scale);
