using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Crypto.Net.Bitcoin;
using Crypto.Net.Core;
using Crypto.Net.Cosmos;
using Crypto.Net.Evm;
using Crypto.Net.Extensions;
using Crypto.Net.Native;
using Crypto.Net.Polkadot;
using Crypto.Net.Solana;
using Crypto.Net.Wallet;

namespace Crypto.Net.Gallery;

/// <summary>A user-facing error (HTTP 400 in the web host).</summary>
public sealed class GalleryProblem(string message) : Exception(message);

/// <summary>
/// The Gallery API, independent of any host. The web and desktop Galleries expose it under <c>/api</c>; the WebAssembly
/// Gallery calls <see cref="InvokeAsync"/> directly from JavaScript. Safe sandbox: nothing is ever broadcast, and
/// generated keys live only for the duration of a call.
/// </summary>
public static class GalleryApi
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Name of the host ("web", "desktop", "browser") reported by <c>/status</c>.</summary>
    public static string Host { get; set; } = "web";

    /// <summary>
    /// Routes <paramref name="method"/> <paramref name="path"/> (relative to <c>/api</c>) and returns the status code and
    /// JSON body, exactly as the HTTP API would.
    /// </summary>
    public static async Task<(int Status, string Body)> InvokeAsync(string method, string path, string? body, HttpClient http, CancellationToken ct = default)
    {
        try
        {
            object? result = await DispatchAsync(method.ToUpperInvariant(), path.Trim('/'), body, http, ct).ConfigureAwait(false);
            return result is null ? (404, "{\"error\":\"Not found\"}") : (200, JsonSerializer.Serialize(result, Json));
        }
        catch (GalleryProblem p)
        {
            return (400, JsonSerializer.Serialize(new { error = p.Message }, Json));
        }
        catch (JsonException)
        {
            return (400, "{\"error\":\"Malformed request\"}");
        }
    }

    private static T Body<T>(string? body) where T : new() =>
        string.IsNullOrWhiteSpace(body) ? new T() : JsonSerializer.Deserialize<T>(body, Json) ?? new T();

    private static async Task<object?> DispatchAsync(string method, string path, string? body, HttpClient http, CancellationToken ct)
    {
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        return (method, parts) switch
        {
            ("GET", ["status"]) => Status(),
            ("POST", ["wallet", "generate"]) => GenerateWallet(Body<WalletRequest>(body)),
            ("POST", ["wallet", "restore"]) => RestoreWallet(Body<WalletRequest>(body)),
            ("POST", ["address", "inspect"]) => InspectAddress(Body<AddressRequest>(body)),
            ("GET", ["units"]) => Units(),
            ("POST", ["convert"]) => Convert(Body<ConvertRequest>(body)),
            ("POST", ["sign"]) => Sign(Body<SignRequest>(body)),
            ("POST", ["keystore", "encrypt"]) => KeystoreEncrypt(Body<KeystoreRequest>(body)),
            ("POST", ["keystore", "decrypt"]) => KeystoreDecrypt(Body<KeystoreRequest>(body)),
            ("GET", ["networks"]) => Networks(),
            ("GET", ["networks", var key, "head"]) => await HeadAsync(key, http, ct).ConfigureAwait(false),
            ("GET", ["networks", var key, "balance", var address]) => await BalanceAsync(key, address, http, ct).ConfigureAwait(false),
            ("POST", ["benchmark"]) => Benchmark(Body<BenchmarkRequest>(body)),
            _ => null,
        };
    }

    public static object Status() => new
    {
        nativeLoaded = NativeLoader.IsNativeAvailable,
        abiVersion = $"0x{NativeLoader.AbiVersion:X8}",
        backend = CryptoNative.EffectiveBackend.ToString(),
        rid = NativeLoader.RuntimeIdentifier,
        runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        loadError = NativeLoader.LoadError,
        rpcProviders = RpcProviders.ConfiguredInEnvironment(),
        version = typeof(CryptoNative).Assembly.GetName().Version?.ToString(3),
        host = Host,
        attribution = "Gravicode Studios · Kang Fadhil",
    };

    public static object GenerateWallet(WalletRequest req)
    {
        using var wallet = HdWallet.Generate(req.Words is 12 or 15 or 18 or 21 or 24 ? req.Words.Value : 12, req.Passphrase);
        return DescribeWallet(wallet, req.Testnet ?? true, 1);
    }

    public static object RestoreWallet(WalletRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Mnemonic) || !Mnemonic.Validate(req.Mnemonic))
            throw new GalleryProblem("That phrase is not a valid BIP-39 mnemonic. Check for a misspelt word or a missing word.");
        using var wallet = HdWallet.FromMnemonic(req.Mnemonic, req.Passphrase);
        return DescribeWallet(wallet, req.Testnet ?? true, Math.Clamp(req.Count ?? 1, 1, 5));
    }

    public static object InspectAddress(AddressRequest req)
    {
        string address = req.Address?.Trim() ?? "";
        var matches = ChainCatalog.DetectAddress(address);
        var details = new List<object>();
        if (matches.Any(m => m.Family == ChainFamily.Bitcoin))
        {
            foreach (var net in BitcoinNetwork.All)
                if (BitcoinAddress.TryParse(address, net, out var info))
                {
                    details.Add(new { label = "Output script", value = System.Convert.ToHexStringLower(info!.ScriptPubKey) });
                    details.Add(new { label = "Payload", value = System.Convert.ToHexStringLower(info.Payload) });
                    break;
                }
        }
        if (matches.Any(m => m.Family == ChainFamily.Evm))
            details.Add(new { label = "EIP-55 form", value = EvmAddress.ToChecksumAddress(address) });
        if (matches.Any(m => m.Family == ChainFamily.Polkadot))
        {
            var (_, key) = Ss58Address.Decode(address);
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
            details.Add(new { label = "Raw key", value = System.Convert.ToHexStringLower(SolanaAddress.Decode(address)) });

        return new { address, valid = matches.Count > 0, matches = matches.Select(m => new { family = m.Family.ToString(), m.Format }), details };
    }

    public static object Units() => UnitConverter.Units.GroupBy(u => u.Value.Family)
        .Select(g => new { family = g.Key, units = g.Select(u => new { name = u.Key, decimals = u.Value.Decimals }) }).ToList();

    public static object Convert(ConvertRequest req)
    {
        try
        {
            return new { output = UnitConverter.Convert(req.Value ?? "", req.From ?? "", req.To ?? "") };
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new GalleryProblem(ex.Message);
        }
    }

    public static object Sign(SignRequest req)
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
                    return new SignResponse("secp256k1 · EIP-191", acc.Address.Value, HexUtil.Encode(EvmMessageSigner.HashPersonalMessage(message)), HexUtil.Encode(sig), recovered == acc.Address.Value, $"Recovered signer {recovered}");
                }
                case "eip712":
                {
                    using var acc = wallet.GetEvmAccount();
                    byte[] digest = EvmMessageSigner.HashTypedData(message);
                    byte[] sig = acc.SignTypedData(message);
                    string recovered = EvmMessageSigner.RecoverTypedDataSigner(message, sig);
                    return new SignResponse("secp256k1 · EIP-712", acc.Address.Value, HexUtil.Encode(digest), HexUtil.Encode(sig), recovered == acc.Address.Value, $"Recovered signer {recovered}");
                }
                case "ed25519":
                {
                    using var acc = wallet.GetSolanaAccount(0, SolanaChain.Devnet);
                    byte[] sig = acc.SignMessage(Encoding.UTF8.GetBytes(message));
                    bool ok = CryptoNative.Ed25519Verify(acc.PublicKey.Span, Encoding.UTF8.GetBytes(message), sig);
                    return new SignResponse("Ed25519 · Solana", acc.Address.Value, null, HexUtil.Encode(sig), ok, "Verified against the public key");
                }
                case "sr25519":
                {
                    if (!NativeLoader.IsNativeAvailable) throw new GalleryProblem("sr25519 needs the native Rust library, which this host does not have (the WebAssembly Gallery runs the managed engine only).");
                    using var acc = wallet.GetPolkadotAccount("", PolkadotChain.Westend);
                    byte[] sig = acc.SignMessage(message);
                    bool ok = PolkadotAccount.VerifyMessage(message, sig, acc.PublicKey.Span);
                    return new SignResponse("sr25519 · Polkadot", acc.Address.Value, null, HexUtil.Encode(sig), ok, "Verified (signatures are randomized, so each run differs)");
                }
                case "schnorr":
                {
                    using var acc = wallet.GetBitcoinAccount(BitcoinAddressType.TaprootP2TR, 0, BitcoinNetwork.Testnet);
                    using var key = wallet.DerivePath(acc.DerivationPath!);
                    byte[] digest = CryptoNative.TaggedHash("Crypto.Net/message", Encoding.UTF8.GetBytes(message));
                    byte[] sig = CryptoNative.SchnorrSign(key.PrivateKey.Span, digest, RandomNumberGenerator.GetBytes(32));
                    byte[] xonly = CryptoNative.Secp256k1XOnlyPublicKey(key.PrivateKey.Span);
                    return new SignResponse("BIP-340 Schnorr", acc.Address.Value, HexUtil.Encode(digest), HexUtil.Encode(sig), CryptoNative.SchnorrVerify(xonly, digest, sig), "Verified against the x-only key");
                }
                default:
                    throw new GalleryProblem($"Unknown scheme '{req.Scheme}'");
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException or InvalidOperationException or PlatformNotSupportedException)
        {
            throw new GalleryProblem(ex.Message);
        }
    }

    public static object KeystoreEncrypt(KeystoreRequest req)
    {
        if (string.IsNullOrEmpty(req.Password)) throw new GalleryProblem("Choose a password to encrypt the key with.");
        using var key = string.IsNullOrWhiteSpace(req.PrivateKey)
            ? SecureBuffer.FromBytesAndClear(RandomNumberGenerator.GetBytes(32))
            : SecureBuffer.FromHex(req.PrivateKey);
        using var account = new EvmAccount(key.Span);
        var kdf = req.Kdf == "pbkdf2" ? new KeyStoreKdf.Pbkdf2Kdf(262_144) : KeyStoreKdf.Light;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string json = account.ExportKeyStore(req.Password, kdf);
        return new { address = account.Address.Value, keystore = json, milliseconds = sw.ElapsedMilliseconds };
    }

    public static object KeystoreDecrypt(KeystoreRequest req)
    {
        try
        {
            using var key = KeyStore.Decrypt(req.Keystore ?? "", req.Password ?? "");
            using var account = new EvmAccount(key.Span);
            return new { address = account.Address.Value };
        }
        catch (CryptographicException)
        {
            throw new GalleryProblem("Wrong password, or the file was modified.");
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or JsonException)
        {
            throw new GalleryProblem($"Not a Web3 v3 keystore: {ex.Message}");
        }
    }

    public static object Networks() => ChainCatalog.Entries.Select(e => new
    {
        key = e.Key,
        family = e.Family.ToString(),
        name = e.Name,
        symbol = e.Chain.Symbol,
        testnet = e.IsTestnet,
        local = e.Key.Contains("local") || e.Key.Contains("regtest"),
    }).ToList();

    public static async Task<object?> HeadAsync(string key, HttpClient http, CancellationToken ct)
    {
        var entry = ChainCatalog.Find(key);
        if (entry is null) return null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var client = ChainCatalog.CreateClient(entry, http: http);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            ulong height = await client.GetBlockNumberAsync(timeout.Token).ConfigureAwait(false);
            return new { key, height, milliseconds = sw.ElapsedMilliseconds };
        }
        catch (Exception ex) when (ex is HttpRequestException or RpcException or OperationCanceledException or JsonException)
        {
            return new { key, error = ex is OperationCanceledException ? "Endpoint did not answer within 12 s" : ex.Message, milliseconds = sw.ElapsedMilliseconds };
        }
    }

    public static async Task<object?> BalanceAsync(string key, string address, HttpClient http, CancellationToken ct)
    {
        var entry = ChainCatalog.Find(key);
        if (entry is null) return null;
        if (!ChainCatalog.IsValidAddress(entry, address)) throw new GalleryProblem($"That is not a valid {entry.Name} address.");
        try
        {
            using var client = ChainCatalog.CreateClient(entry, http: http);
            var amount = await client.GetBalanceAsync(new Address(address, entry.Chain.Id), ct).ConfigureAwait(false);
            return new { balance = amount.ToString(), symbol = entry.Chain.Symbol };
        }
        catch (Exception ex) when (ex is HttpRequestException or RpcException or JsonException)
        {
            throw new GalleryProblem($"{entry.Name} endpoint error: {ex.Message}");
        }
    }

    public static object Benchmark(BenchmarkRequest req) =>
        new { nativeAvailable = NativeLoader.IsNativeAvailable, results = CryptoBenchmark.Run(Math.Clamp(req.Scale ?? 0.5, 0.05, 3)) };

    private static object DescribeWallet(HdWallet wallet, bool testnet, int count)
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

        // Fingerprint drawn as the guilloché rosette. Derived from the xpub, so the picture never reveals the seed.
        byte[] fingerprint = CryptoNative.TaggedHash("Crypto.Net/gallery-rosette", Encoding.UTF8.GetBytes(wallet.MasterKey.ToExtendedPublicKey()));

        return new
        {
            mnemonic = wallet.RevealMnemonic(),
            fingerprint = System.Convert.ToHexStringLower(fingerprint),
            masterFingerprint = wallet.MasterKey.Fingerprint.ToString("x8"),
            xpub = wallet.GetBitcoinAccountXpub(BitcoinAddressType.SegWitP2WPKH, btc),
            testnet,
            accounts,
        };

        static object Row(string id, IChain chain, string curve, string path, string address, uint index) =>
            new { id, chain = chain.Name, symbol = chain.Symbol, curve, path, address, index };
    }
}

// CNET002: these DTOs receive secrets from the UI as JSON text, so a string is unavoidable here; the Gallery is a
// local demo and never persists them.
#pragma warning disable CNET002
public sealed class WalletRequest { public int? Words { get; set; } public string? Passphrase { get; set; } public bool? Testnet { get; set; } public string? Mnemonic { get; set; } public int? Count { get; set; } }
public sealed class AddressRequest { public string? Address { get; set; } }
public sealed class ConvertRequest { public string? Value { get; set; } public string? From { get; set; } public string? To { get; set; } }
public sealed class SignRequest { public string? Scheme { get; set; } public string? Message { get; set; } public string? Mnemonic { get; set; } }
public sealed record SignResponse(string Scheme, string Signer, string? Digest, string Signature, bool Verified, string Note);
public sealed class KeystoreRequest { public string? PrivateKey { get; set; } public string? Password { get; set; } public string? Kdf { get; set; } public string? Keystore { get; set; } }
public sealed class BenchmarkRequest { public double? Scale { get; set; } }
#pragma warning restore CNET002
