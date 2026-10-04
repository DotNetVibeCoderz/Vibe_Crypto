using System.CommandLine;
using System.Runtime.InteropServices;
using Crypto.Net.Bitcoin;
using Crypto.Net.Cli;
using Crypto.Net.Core;
using Crypto.Net.Cosmos;
using Crypto.Net.Evm;
using Crypto.Net.Extensions;
using Crypto.Net.Native;
using Crypto.Net.Polkadot;
using Crypto.Net.Solana;
using Crypto.Net.Wallet;
using Spectre.Console;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var json = new Option<bool>("--json") { Description = "Machine-readable JSON output", Recursive = true };
var root = new RootCommand($"cnet — Crypto.Net command line. {Ui.Attribution}.") { json };

// ------------------------------------------------------------------ doctor
var doctor = new Command("doctor", "Inspect the runtime, RID and native Rust library");
doctor.SetAction(r =>
{
    var info = new
    {
        nativeLoaded = NativeLoader.IsNativeAvailable,
        abiVersion = $"0x{NativeLoader.AbiVersion:X8}",
        requiredAbi = $"0x{NativeLoader.RequiredAbiVersion:X8}",
        libraryPath = NativeLoader.LoadedPath,
        loadError = NativeLoader.LoadError,
        backend = CryptoNative.EffectiveBackend.ToString(),
        rid = NativeLoader.RuntimeIdentifier,
        os = RuntimeInformation.OSDescription,
        dotnet = RuntimeInformation.FrameworkDescription,
        rpcProvider = RpcProviders.ConfiguredInEnvironment() is { Count: > 0 } providers ? string.Join(" → ", providers) + " → public" : "public endpoints",
        attribution = Ui.Attribution,
    };
    if (r.GetValue(json)) { Ui.Json(info); return 0; }

    Ui.Banner();
    var t = Ui.Table("Check", "Value");
    t.AddRow("Native Rust core", info.nativeLoaded ? $"[#{Ui.Accent.ToHex()}]loaded[/]" : "[#FFB020]managed fallback[/]");
    t.AddRow("ABI version", $"{info.abiVersion} [grey](requires {info.requiredAbi})[/]");
    t.AddRow("Library", Markup.Escape(info.libraryPath ?? "(default OS search path)"));
    if (!info.nativeLoaded) t.AddRow("Reason", Markup.Escape(info.loadError));
    t.AddRow("Backend", info.backend);
    t.AddRow("Runtime identifier", info.rid);
    t.AddRow("Operating system", Markup.Escape(info.os));
    t.AddRow(".NET", Markup.Escape(info.dotnet));
    t.AddRow("RPC provider", Markup.Escape(info.rpcProvider));
    AnsiConsole.Write(t);
    return 0;
});
root.Subcommands.Add(doctor);

// ------------------------------------------------------------------ networks
var networks = new Command("networks", "List built-in networks and their keys");
networks.SetAction(r =>
{
    var rows = ChainCatalog.Entries.Select(e => new { key = e.Key, family = e.Family.ToString(), name = e.Name, symbol = e.Chain.Symbol, testnet = e.IsTestnet }).ToList();
    if (r.GetValue(json)) { Ui.Json(rows); return 0; }
    var t = Ui.Table("Key", "Family", "Name", "Symbol", "Testnet");
    foreach (var row in rows) t.AddRow(row.key, row.family, Markup.Escape(row.name), row.symbol, row.testnet ? "[#FFB020]yes[/]" : "no");
    AnsiConsole.Write(t);
    return 0;
});
root.Subcommands.Add(networks);

// ------------------------------------------------------------------ wallet
var wallet = new Command("wallet", "Create and inspect HD wallets");
var words = new Option<int>("--words", "-w") { Description = "Mnemonic length", DefaultValueFactory = _ => 12 };
words.AcceptOnlyFromAmong("12", "15", "18", "21", "24");
var passphrase = new Option<string?>("--passphrase") { Description = "Optional BIP-39 passphrase (25th word)" };
var testnet = new Option<bool>("--testnet") { Description = "Derive testnet addresses where applicable" };
var mnemonicOpt = new Option<string?>("--mnemonic", "-m") { Description = "Mnemonic phrase (or set CNET_MNEMONIC)" };
var count = new Option<int>("--count", "-n") { Description = "Number of address indexes", DefaultValueFactory = _ => 1 };
var showKeys = new Option<bool>("--show-keys") { Description = "Also print private keys (dangerous)" };

var walletNew = new Command("new", "Generate a new multi-chain wallet") { words, passphrase, testnet };
walletNew.SetAction(r =>
{
    using var w = HdWallet.Generate(r.GetValue(words), r.GetValue(passphrase));
    return PrintWallet(w, r.GetValue(testnet), 1, false, r.GetValue(json), showMnemonic: true);
});

var walletDerive = new Command("derive", "Derive addresses from an existing mnemonic") { mnemonicOpt, passphrase, testnet, count, showKeys };
walletDerive.SetAction(r =>
{
    string phrase = r.GetValue(mnemonicOpt) ?? Environment.GetEnvironmentVariable("CNET_MNEMONIC") ?? Ui.Secret("Mnemonic:");
    if (!Mnemonic.Validate(phrase)) { Ui.Error("Invalid BIP-39 mnemonic (unknown word or bad checksum)."); return 1; }
    using var w = HdWallet.FromMnemonic(phrase, r.GetValue(passphrase));
    return PrintWallet(w, r.GetValue(testnet), Math.Clamp(r.GetValue(count), 1, 100), r.GetValue(showKeys), r.GetValue(json), showMnemonic: false);
});

var walletXpub = new Command("xpub", "Account-level extended public keys for watch-only wallets") { mnemonicOpt, passphrase, testnet };
walletXpub.SetAction(r =>
{
    string phrase = r.GetValue(mnemonicOpt) ?? Environment.GetEnvironmentVariable("CNET_MNEMONIC") ?? Ui.Secret("Mnemonic:");
    using var w = HdWallet.FromMnemonic(phrase, r.GetValue(passphrase));
    var net = r.GetValue(testnet) ? BitcoinNetwork.Testnet : BitcoinNetwork.Mainnet;
    var keys = new Dictionary<string, string>
    {
        ["BIP-44 (P2PKH)"] = w.GetBitcoinAccountXpub(BitcoinAddressType.LegacyP2PKH, net),
        ["BIP-84 (P2WPKH)"] = w.GetBitcoinAccountXpub(BitcoinAddressType.SegWitP2WPKH, net),
        ["BIP-86 (P2TR)"] = w.GetBitcoinAccountXpub(BitcoinAddressType.TaprootP2TR, net),
    };
    if (r.GetValue(json)) { Ui.Json(keys); return 0; }
    var t = Ui.Table("Scheme", "Extended public key");
    foreach (var (k, v) in keys) t.AddRow(k, $"[#{Ui.Accent.ToHex()}]{v}[/]");
    AnsiConsole.Write(t);
    return 0;
});
wallet.Subcommands.Add(walletNew);
wallet.Subcommands.Add(walletDerive);
wallet.Subcommands.Add(walletXpub);
root.Subcommands.Add(wallet);

// ------------------------------------------------------------------ keystore
var keystore = new Command("keystore", "Web3 Secret Storage v3 keystores (geth / MetaMask compatible)");
var privateKeyOpt = new Option<string?>("--private-key", "-k") { Description = "Hex private key (or set CNET_PRIVATE_KEY)" };
var passwordOpt = new Option<string?>("--password", "-p") { Description = "Password (prompted when omitted)" };
var kdfOpt = new Option<string>("--kdf") { Description = "standard (scrypt 2^18), light (scrypt 2^12) or pbkdf2", DefaultValueFactory = _ => "standard" };
kdfOpt.AcceptOnlyFromAmong("standard", "light", "pbkdf2");
var outOpt = new Option<FileInfo?>("--out", "-o") { Description = "Write the keystore to this file" };
var fileArg = new Argument<FileInfo>("file") { Description = "Keystore JSON file" };

var ksEncrypt = new Command("encrypt", "Encrypt a private key") { privateKeyOpt, passwordOpt, kdfOpt, outOpt };
ksEncrypt.SetAction(r =>
{
    string hex = r.GetValue(privateKeyOpt) ?? Environment.GetEnvironmentVariable("CNET_PRIVATE_KEY") ?? Ui.Secret("Private key (hex):");
    string pw = r.GetValue(passwordOpt) ?? Ui.Secret("New password:");
    using var key = SecureBuffer.FromHex(hex);
    using var account = new EvmAccount(key.Span);
    KeyStoreKdf kdf = r.GetValue(kdfOpt) switch { "light" => KeyStoreKdf.Light, "pbkdf2" => new KeyStoreKdf.Pbkdf2Kdf(262_144), _ => KeyStoreKdf.Standard };
    string ks = AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Deriving key…", _ => account.ExportKeyStore(pw, kdf));
    var file = r.GetValue(outOpt);
    if (file is not null)
    {
        File.WriteAllText(file.FullName, ks);
        Ui.Success($"Keystore for {account.Address} written to {file.FullName}");
    }
    else
    {
        Console.WriteLine(ks);
    }
    return 0;
});

var showKey = new Option<bool>("--show-key") { Description = "Print the decrypted private key (dangerous)" };
var ksDecrypt = new Command("decrypt", "Decrypt a keystore and show its address") { fileArg, passwordOpt, showKey };
ksDecrypt.SetAction(r =>
{
    string content = File.ReadAllText(r.GetValue(fileArg)!.FullName);
    string pw = r.GetValue(passwordOpt) ?? Ui.Secret("Password:");
    using var key = AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Decrypting…", _ => KeyStore.Decrypt(content, pw));
    using var account = new EvmAccount(key.Span);
    var result = new { address = account.Address.Value, privateKey = r.GetValue(showKey) ? HexUtil.Encode(key.Span) : null };
    if (r.GetValue(json)) { Ui.Json(result); return 0; }
    Ui.Success($"Password OK — address {result.address}");
    if (result.privateKey is not null) Ui.SecretPanel("PRIVATE KEY", result.privateKey, "Anyone with this key controls the funds.");
    return 0;
});
keystore.Subcommands.Add(ksEncrypt);
keystore.Subcommands.Add(ksDecrypt);
root.Subcommands.Add(keystore);

// ------------------------------------------------------------------ address
var address = new Command("address", "Address utilities");
var addressArg = new Argument<string>("address") { Description = "Address to inspect" };
var networkOpt = new Option<string?>("--network", "-N") { Description = "Network key (see `cnet networks`)" };
var validate = new Command("validate", "Validate an address and detect its chain") { addressArg, networkOpt };
validate.SetAction(r =>
{
    string addr = r.GetValue(addressArg)!;
    string? net = r.GetValue(networkOpt);
    if (net is not null)
    {
        var entry = ChainCatalog.Get(net);
        bool ok = ChainCatalog.IsValidAddress(entry, addr);
        if (r.GetValue(json)) { Ui.Json(new { address = addr, network = entry.Key, valid = ok }); return ok ? 0 : 1; }
        if (ok) Ui.Success($"Valid {entry.Name} address"); else Ui.Error($"Not a valid {entry.Name} address");
        return ok ? 0 : 1;
    }

    var matches = ChainCatalog.DetectAddress(addr);
    if (r.GetValue(json)) { Ui.Json(new { address = addr, valid = matches.Count > 0, matches }); return matches.Count > 0 ? 0 : 1; }
    if (matches.Count == 0) { Ui.Error("Not recognised by any supported chain"); return 1; }
    var t = Ui.Table("Chain family", "Format");
    foreach (var m in matches) t.AddRow($"[#{Ui.Accent.ToHex()}]{m.Family}[/]", Markup.Escape(m.Format));
    AnsiConsole.Write(t);
    return 0;
});
address.Subcommands.Add(validate);
root.Subcommands.Add(address);

// ------------------------------------------------------------------ convert
var amountArg = new Argument<string>("amount");
var fromArg = new Argument<string>("from") { Description = "wei, gwei, ether, sat, btc, lamports, sol, planck, dot, uatom, atom…" };
var toArg = new Argument<string>("to");
var convert = new Command("convert", "Exact unit conversion, e.g. `cnet convert 1.5 ether gwei`") { amountArg, fromArg, toArg };
convert.SetAction(r =>
{
    string result = UnitConverter.Convert(r.GetValue(amountArg)!, r.GetValue(fromArg)!, r.GetValue(toArg)!);
    if (r.GetValue(json)) { Ui.Json(new { input = r.GetValue(amountArg), from = r.GetValue(fromArg), to = r.GetValue(toArg), output = result }); return 0; }
    AnsiConsole.MarkupLine($"{Markup.Escape(r.GetValue(amountArg)!)} {r.GetValue(fromArg)} = [bold #{Ui.Accent.ToHex()}]{result}[/] {r.GetValue(toArg)}");
    return 0;
});
root.Subcommands.Add(convert);

// ------------------------------------------------------------------ balance / block / tx
var networkRequired = new Option<string>("--network", "-N") { Description = "Network key (see `cnet networks`)", Required = true };
var rpcOpt = new Option<string?>("--rpc") { Description = "Override the RPC / REST endpoint" };

var balance = new Command("balance", "Native balance of an address") { addressArg, networkRequired, rpcOpt };
balance.SetAction(async (r, ct) =>
{
    var entry = ChainCatalog.Get(r.GetValue(networkRequired)!);
    string addr = r.GetValue(addressArg)!;
    if (!ChainCatalog.IsValidAddress(entry, addr)) { Ui.Error($"Not a valid {entry.Name} address"); return 1; }
    await using var client = ChainCatalog.CreateClient(entry, r.GetValue(rpcOpt));
    var amount = await client.GetBalanceAsync(new Address(addr, entry.Chain.Id), ct);
    if (r.GetValue(json)) { Ui.Json(new { address = addr, network = entry.Key, balance = amount.ToString(), baseUnits = amount.BaseUnits.ToString(), symbol = entry.Chain.Symbol }); return 0; }
    AnsiConsole.MarkupLine($"[grey]{Markup.Escape(entry.Name)}[/]  {Markup.Escape(addr)}\n[bold #{Ui.Accent.ToHex()}]{amount}[/] {entry.Chain.Symbol}");
    return 0;
});
root.Subcommands.Add(balance);

var block = new Command("block", "Latest block height (slot on Solana)") { networkRequired, rpcOpt };
block.SetAction(async (r, ct) =>
{
    var entry = ChainCatalog.Get(r.GetValue(networkRequired)!);
    await using var client = ChainCatalog.CreateClient(entry, r.GetValue(rpcOpt));
    ulong height = await client.GetBlockNumberAsync(ct);
    if (r.GetValue(json)) { Ui.Json(new { network = entry.Key, height }); return 0; }
    AnsiConsole.MarkupLine($"{Markup.Escape(entry.Name)} height [bold #{Ui.Accent.ToHex()}]{height:N0}[/]");
    return 0;
});
root.Subcommands.Add(block);

var hashArg = new Argument<string>("hash");
var tx = new Command("tx", "Look up a transaction receipt") { hashArg, networkRequired, rpcOpt };
tx.SetAction(async (r, ct) =>
{
    var entry = ChainCatalog.Get(r.GetValue(networkRequired)!);
    await using var client = ChainCatalog.CreateClient(entry, r.GetValue(rpcOpt));
    var receipt = await client.GetReceiptAsync(new TxHash(r.GetValue(hashArg)!), ct);
    if (r.GetValue(json)) { Ui.Json(new { found = receipt is not null, receipt }); return receipt is null ? 2 : 0; }
    if (receipt is null) { Ui.Warn("Pending or unknown transaction"); return 2; }
    var t = Ui.Table("Field", "Value");
    t.AddRow("Status", receipt.IsSuccess ? $"[#{Ui.Accent.ToHex()}]success[/]" : $"[red]failed[/] {Markup.Escape(receipt.ErrorMessage ?? "")}");
    t.AddRow("Block", receipt.BlockNumber.ToString("N0"));
    if (receipt.GasUsed > 0) t.AddRow("Gas used", receipt.GasUsed.ToString("N0"));
    AnsiConsole.Write(t);
    return 0;
});
root.Subcommands.Add(tx);

// ------------------------------------------------------------------ sign / verify (EVM)
var sign = new Command("sign", "Sign messages (EIP-191) and typed data (EIP-712)");
var messageArg = new Argument<string>("message");
var indexOpt = new Option<uint>("--index", "-i") { Description = "Address index when using --mnemonic" };
var signMessage = new Command("message", "personal_sign a UTF-8 message") { messageArg, privateKeyOpt, mnemonicOpt, indexOpt };
signMessage.SetAction(r =>
{
    using var account = ResolveEvmAccount(r.GetValue(privateKeyOpt), r.GetValue(mnemonicOpt), r.GetValue(indexOpt));
    string sig = HexUtil.Encode(account.SignMessage(r.GetValue(messageArg)!));
    if (r.GetValue(json)) { Ui.Json(new { address = account.Address.Value, message = r.GetValue(messageArg), signature = sig }); return 0; }
    AnsiConsole.MarkupLine($"[grey]signer[/]    {account.Address}\n[grey]signature[/] [#{Ui.Accent.ToHex()}]{sig}[/]");
    return 0;
});
var typedFile = new Argument<FileInfo>("file") { Description = "EIP-712 JSON (eth_signTypedData_v4 format)" };
var signTyped = new Command("typed-data", "Sign EIP-712 typed data") { typedFile, privateKeyOpt, mnemonicOpt, indexOpt };
signTyped.SetAction(r =>
{
    using var account = ResolveEvmAccount(r.GetValue(privateKeyOpt), r.GetValue(mnemonicOpt), r.GetValue(indexOpt));
    string content = File.ReadAllText(r.GetValue(typedFile)!.FullName);
    string digest = HexUtil.Encode(EvmMessageSigner.HashTypedData(content));
    string sig = HexUtil.Encode(account.SignTypedData(content));
    if (r.GetValue(json)) { Ui.Json(new { address = account.Address.Value, digest, signature = sig }); return 0; }
    AnsiConsole.MarkupLine($"[grey]signer[/]    {account.Address}\n[grey]digest[/]    {digest}\n[grey]signature[/] [#{Ui.Accent.ToHex()}]{sig}[/]");
    return 0;
});
sign.Subcommands.Add(signMessage);
sign.Subcommands.Add(signTyped);
root.Subcommands.Add(sign);

var verify = new Command("verify", "Recover the signer of a personal_sign signature") { messageArg };
var signatureOpt = new Option<string>("--signature", "-s") { Required = true, Description = "65-byte 0x signature" };
verify.Options.Add(signatureOpt);
verify.SetAction(r =>
{
    string signer = EvmMessageSigner.RecoverPersonalMessageSigner(r.GetValue(messageArg)!, r.GetValue(signatureOpt)!);
    if (r.GetValue(json)) { Ui.Json(new { signer }); return 0; }
    Ui.Success($"Signed by {signer}");
    return 0;
});
root.Subcommands.Add(verify);

// ------------------------------------------------------------------ bench
var scaleOpt = new Option<double>("--scale") { Description = "Iteration multiplier", DefaultValueFactory = _ => 1.0 };
var bench = new Command("bench", "Benchmark the Rust core against the managed fallback") { scaleOpt };
bench.SetAction(r =>
{
    IReadOnlyList<BenchmarkResult> results = r.GetValue(json)
        ? CryptoBenchmark.Run(r.GetValue(scaleOpt))
        : AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Benchmarking…", ctx =>
            CryptoBenchmark.Run(r.GetValue(scaleOpt), new Progress<string>(s => ctx.Status($"Benchmarking [bold]{Markup.Escape(s)}[/]…"))));
    if (r.GetValue(json)) { Ui.Json(new { nativeAvailable = NativeLoader.IsNativeAvailable, results }); return 0; }

    var t = Ui.Table("Operation", "Category", "Rust ops/s", "Managed ops/s", "Speed-up");
    foreach (var b in results)
    {
        string speed = b.Speedup switch
        {
            0 => "[grey]n/a[/]",
            >= 2 => $"[bold #{Ui.Accent.ToHex()}]{b.Speedup:0.0}×[/]",
            >= 1 => $"{b.Speedup:0.0}×",
            _ => $"[#FFB020]{b.Speedup:0.00}×[/]",
        };
        t.AddRow(Markup.Escape(b.Name), b.Category, b.NativeOpsPerSec > 0 ? b.NativeOpsPerSec.ToString("N0") : "—", b.ManagedOpsPerSec.ToString("N0"), speed);
    }
    AnsiConsole.Write(t);
    if (!NativeLoader.IsNativeAvailable) Ui.Warn("Native library not loaded — only the managed backend was measured.");
    return 0;
});
root.Subcommands.Add(bench);

// CNET_RECORD_HTML=<file> captures the rendered output as HTML (used for documentation screenshots).
string? recordPath = Environment.GetEnvironmentVariable("CNET_RECORD_HTML");
if (recordPath is not null)
{
    AnsiConsole.Profile.Width = 150;
    AnsiConsole.Record();
}
int exitCode = await root.Parse(args).InvokeAsync();
if (recordPath is not null) File.WriteAllText(recordPath, AnsiConsole.ExportHtml());
return exitCode;

// ------------------------------------------------------------------ helpers

static int PrintWallet(HdWallet w, bool testnet, int count, bool showKeys, bool asJson, bool showMnemonic)
{
    var btcNet = testnet ? BitcoinNetwork.Testnet : BitcoinNetwork.Mainnet;
    var evmNet = testnet ? EvmChain.Sepolia : EvmChain.Ethereum;
    var solNet = testnet ? SolanaChain.Devnet : SolanaChain.MainnetBeta;
    var dotNet = testnet ? PolkadotChain.Westend : PolkadotChain.Polkadot;
    var atomNet = testnet ? CosmosChain.CosmosHubTestnet : CosmosChain.CosmosHub;

    var rows = new List<(string Chain, string Path, string Address, string? Key)>();
    for (uint i = 0; i < count; i++)
    {
        using var evm = w.GetEvmAccount(i, evmNet);
        rows.Add(("Ethereum / EVM", evm.DerivationPath!, evm.Address.Value, showKeys ? Key(w, evm.DerivationPath!) : null));
        foreach (var type in new[] { BitcoinAddressType.SegWitP2WPKH, BitcoinAddressType.TaprootP2TR })
        {
            using var btc = w.GetBitcoinAccount(type, i, btcNet);
            rows.Add((type == BitcoinAddressType.TaprootP2TR ? "Bitcoin (Taproot)" : "Bitcoin (SegWit)", btc.DerivationPath!, btc.Address.Value, showKeys ? btc.ExportWif() : null));
        }
        using var sol = w.GetSolanaAccount(i, solNet);
        rows.Add(("Solana", sol.DerivationPath!, sol.Address.Value, showKeys ? sol.ExportBase58SecretKey() : null));
        using var atom = w.GetCosmosAccount(i, atomNet);
        rows.Add(("Cosmos Hub", atom.DerivationPath!, atom.Address.Value, showKeys ? Key(w, atom.DerivationPath!) : null));
        if (NativeLoader.IsNativeAvailable)
        {
            string path = i == 0 ? "" : $"//{i}";
            using var dot = w.GetPolkadotAccount(path, dotNet);
            rows.Add(("Polkadot (sr25519)", path.Length == 0 ? "(root)" : path, dot.Address.Value, null));
        }
    }

    if (asJson)
    {
        Ui.Json(new
        {
            mnemonic = showMnemonic ? w.RevealMnemonic() : null,
            network = testnet ? "testnet" : "mainnet",
            accounts = rows.Select(r => new { chain = r.Chain, path = r.Path, address = r.Address, privateKey = r.Key }),
        });
        return 0;
    }

    if (showMnemonic)
    {
        Ui.Banner();
        Ui.SecretPanel("RECOVERY PHRASE", w.RevealMnemonic(), "Write it down offline. Anyone with these words controls every account below.");
    }
    Ui.Section(testnet ? "Testnet addresses" : "Mainnet addresses");
    var t = showKeys ? Ui.Table("Chain", "Path", "Address", "Private key") : Ui.Table("Chain", "Path", "Address");
    foreach (var r in rows)
    {
        var cells = new List<string> { r.Chain, $"[grey]{Markup.Escape(r.Path)}[/]", $"[#{Ui.Accent.ToHex()}]{r.Address}[/]" };
        if (showKeys) cells.Add($"[#FFB020]{Markup.Escape(r.Key ?? "—")}[/]");
        t.AddRow(cells.ToArray());
    }
    AnsiConsole.Write(t);
    if (!NativeLoader.IsNativeAvailable) Ui.Warn("Polkadot (sr25519) needs the native library; run `cnet doctor`.");
    return 0;

    static string Key(HdWallet w, string path)
    {
        using var k = w.DerivePath(path);
        return HexUtil.Encode(k.PrivateKey.Span);
    }
}

static EvmAccount ResolveEvmAccount(string? privateKey, string? mnemonic, uint index)
{
    privateKey ??= Environment.GetEnvironmentVariable("CNET_PRIVATE_KEY");
    mnemonic ??= Environment.GetEnvironmentVariable("CNET_MNEMONIC");
    if (privateKey is not null) return EvmAccount.FromPrivateKeyHex(privateKey);
    mnemonic ??= Ui.Secret("Mnemonic:");
    using var w = HdWallet.FromMnemonic(mnemonic);
    return w.GetEvmAccount(index);
}
