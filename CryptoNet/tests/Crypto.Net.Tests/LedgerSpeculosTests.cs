using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Crypto.Net.Evm;
using Crypto.Net.Ledger;
using Crypto.Net.Testing;

namespace Crypto.Net.Tests;

/// <summary>Runs only when CRYPTONET_SPECULOS=1 (CI starts the Speculos emulator with Ledger's Ethereum app).</summary>
public sealed class SpeculosFactAttribute : FactAttribute
{
    public SpeculosFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CRYPTONET_SPECULOS") != "1")
            Skip = "Set CRYPTONET_SPECULOS=1 with Speculos on ports 9999 (APDU) and 5000 (REST)";
    }
}

/// <summary>
/// End-to-end against the real Ethereum app in Speculos, seeded with the public BIP-39 test phrase. Each signature is
/// checked against the address Crypto.Net derives in software from the same phrase.
/// </summary>
public class LedgerSpeculosTests
{
    private const string Path = "44'/60'/0'/0/0";
    private static readonly HttpClient Rest = new() { BaseAddress = new Uri("http://127.0.0.1:5000/") };

    /// <summary>Presses through review screens and approves, until <paramref name="work"/> completes.</summary>
    internal static async Task<T> WithApproval<T>(Task<T> work)
    {
        var approve = new Regex(@"^(Accept|Approve|Sign transaction|Sign message|Sign typed message|Confirm)", RegexOptions.IgnoreCase);
        var seen = new List<string>();
        for (int i = 0; i < 200 && !work.IsCompleted; i++)
        {
            await Task.Delay(400);
            var events = await Rest.GetFromJsonAsync<JsonElement>("events?currentscreenonly=true");
            string screen = string.Join(" | ", events.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("text").GetString()));
            if (screen.Length > 0 && (seen.Count == 0 || seen[^1] != screen)) seen.Add(screen);
            string button = events.GetProperty("events").EnumerateArray().Any(e => approve.IsMatch(e.GetProperty("text").GetString() ?? "")) ? "both" : "right";
            await Rest.PostAsJsonAsync($"button/{button}", new { action = "press-and-release" });
        }
        if (!work.IsCompleted) throw new TimeoutException("Device flow did not finish. Screens: " + string.Join(" → ", seen));
        return await work;
    }

    [SpeculosFact]
    public async Task EthereumApp_SignsWithTheDeviceKey()
    {
        using var transport = await SpeculosTransport.ConnectAsync();
        var app = new LedgerEthereumApp(transport);

        var config = await app.GetConfigurationAsync();
        Assert.Matches(@"^\d+\.\d+\.\d+$", config.Version);

        var address = await app.GetAddressAsync(Path);
        Assert.Equal(TestVectors.AbandonEthereum, address.Address);

        byte[] signature = await WithApproval(app.SignPersonalMessageAsync(Path, "Crypto.Net + Ledger"));
        Assert.Equal(TestVectors.AbandonEthereum, EvmMessageSigner.RecoverPersonalMessageSigner("Crypto.Net + Ledger", Crypto.Net.Core.HexUtil.Encode(signature)));

        var tx = new EvmTransaction
        {
            Type = EvmTransactionType.DynamicFee, ChainId = 11155111, Nonce = 7, MaxPriorityFeePerGas = 1_000_000_000, MaxFeePerGas = 30_000_000_000,
            GasLimit = 21000, To = "0x000000000000000000000000000000000000dead", Value = 1_000_000_000_000_000,
        };
        var signed = await WithApproval(app.SignTransactionAsync(Path, tx));
        Assert.Equal(TestVectors.AbandonEthereum, EvmTransaction.DecodeSigned(signed.RawBytes).RecoverSender());

        var legacy = new EvmTransaction { Type = EvmTransactionType.Legacy, ChainId = 1, Nonce = 1, GasPrice = 20_000_000_000, GasLimit = 21000, To = "0x000000000000000000000000000000000000dead", Value = 5 };
        var signedLegacy = await WithApproval(app.SignTransactionAsync(Path, legacy));
        Assert.Equal(TestVectors.AbandonEthereum, EvmTransaction.DecodeSigned(signedLegacy.RawBytes).RecoverSender());
    }
}

/// <summary>
/// End-to-end against Ledger's Solana app in Speculos (CI builds it from source with Ledger's app builder), seeded with
/// the public test phrase. The device address must equal the Phantom-path address derived in software.
/// </summary>
public class LedgerSolanaSpeculosTests
{
    [SpeculosFact]
    public async Task SolanaApp_SignsWithTheDeviceKey()
    {
        using var transport = await SpeculosTransport.ConnectAsync();
        var app = new LedgerSolanaApp(transport);
        string path = LedgerSolanaApp.PhantomPath(0);

        var config = await app.GetConfigurationAsync();
        Assert.Matches(@"^\d+\.\d+\.\d+$", config.Version);
        string address = await app.GetAddressAsync(path);
        Assert.Equal(TestVectors.AbandonSolana, address);

        var tx = new Crypto.Net.Solana.SolanaTransaction(address, "11111111111111111111111111111111");
        tx.Instructions.Add(Crypto.Net.Solana.SystemProgram.Transfer(address, "Vote111111111111111111111111111111111111111", 1_000_000));
        var signed = await LedgerSpeculosTests.WithApproval(app.SignTransactionAsync(path, tx));
        Assert.Equal(64, Crypto.Net.Native.CryptoNative.Base58Decode(signed.Signature).Length);   // AddSignature verified it

        byte[] message = LedgerSolanaApp.FormatOffchainMessage("Crypto.Net + Ledger", [address]);
        byte[] sig = await LedgerSpeculosTests.WithApproval(app.SignOffchainMessageAsync(path, message));
        Assert.True(Crypto.Net.Native.CryptoNative.Ed25519Verify(Crypto.Net.Native.CryptoNative.Base58Decode(address), message, sig));
    }
}
