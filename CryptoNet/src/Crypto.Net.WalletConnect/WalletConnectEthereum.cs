using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Crypto.Net.Evm;

namespace Crypto.Net.WalletConnect;

/// <summary>EIP-155 (Ethereum and EVM) requests over a WalletConnect session: the wallet signs and sends.</summary>
public sealed class WalletConnectEthereum(WalletConnectSession session, ulong chainId)
{
    /// <summary>Methods and events apps usually ask for.</summary>
    public static readonly string[] DefaultMethods = ["eth_sendTransaction", "personal_sign", "eth_signTypedData_v4", "eth_signTransaction"];
    public static readonly string[] DefaultEvents = ["chainChanged", "accountsChanged"];

    /// <summary>The <c>eip155</c> namespace for <paramref name="chainIds"/>.</summary>
    public static IReadOnlyDictionary<string, WalletConnectNamespace> Namespace(params ulong[] chainIds) =>
        new Dictionary<string, WalletConnectNamespace> { ["eip155"] = new([.. chainIds.Select(c => $"eip155:{c}")], DefaultMethods, DefaultEvents) };

    public string ChainId => $"eip155:{chainId}";

    /// <summary>The first account the wallet approved for this chain (EIP-55).</summary>
    public string Address
    {
        get
        {
            string prefix = ChainId + ":";
            string account = session.Accounts.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"The wallet approved no account on {ChainId}");
            return EvmAddress.ToChecksumAddress(Convert.FromHexString(account[(prefix.Length + 2)..]));
        }
    }

    /// <summary>EIP-191 <c>personal_sign</c>; returns the 65-byte signature as 0x-hex.</summary>
    public async Task<string> PersonalSignAsync(string message, CancellationToken ct = default)
    {
        string hex = "0x" + Convert.ToHexStringLower(Encoding.UTF8.GetBytes(message));
        var r = await session.RequestAsync("personal_sign", new JsonArray(hex, Address), ChainId, ct: ct).ConfigureAwait(false);
        return r!.GetValue<string>();
    }

    /// <summary>EIP-712 <c>eth_signTypedData_v4</c> with the typed data as JSON.</summary>
    public async Task<string> SignTypedDataAsync(string typedDataJson, CancellationToken ct = default)
    {
        var r = await session.RequestAsync("eth_signTypedData_v4", new JsonArray(Address, typedDataJson), ChainId, ct: ct).ConfigureAwait(false);
        return r!.GetValue<string>();
    }

    /// <summary><c>eth_sendTransaction</c>: the wallet fills in what is missing, signs and broadcasts; returns the hash.</summary>
    public async Task<string> SendTransactionAsync(string to, BigInteger value, byte[]? data = null, ulong? gas = null, CancellationToken ct = default)
    {
        var tx = new JsonObject { ["from"] = Address, ["to"] = to, ["value"] = Quantity(value) };
        if (data is { Length: > 0 }) tx["data"] = "0x" + Convert.ToHexStringLower(data);
        if (gas is { } g) tx["gas"] = Quantity(g);
        var r = await session.RequestAsync("eth_sendTransaction", new JsonArray(tx), ChainId, ct: ct).ConfigureAwait(false);
        return r!.GetValue<string>();
    }

    private static string Quantity(BigInteger v) => "0x" + (v.IsZero ? "0" : v.ToString("x", System.Globalization.CultureInfo.InvariantCulture).TrimStart('0'));
}
