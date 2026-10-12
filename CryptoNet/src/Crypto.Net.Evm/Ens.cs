using System.Text;
using Crypto.Net.Core;
using Crypto.Net.Native;

namespace Crypto.Net.Evm;

/// <summary>
/// Ethereum Name Service: name normalization, EIP-137 namehash, and resolution through the ENS registry
/// (deployed at the same address on Ethereum mainnet, Sepolia and Holesky).
/// </summary>
/// <remarks>
/// Normalization lower-cases and NFC-normalizes names and rejects empty labels, whitespace and control characters.
/// That covers ASCII and most Unicode names, but not every ENSIP-15 rule (confusables, emoji sequences). Names with
/// such characters should be normalized by an ENSIP-15 implementation first. Off-chain (CCIP-read, ENSIP-10 wildcard)
/// resolvers are not followed; those names resolve to null.
/// </remarks>
public sealed class EnsClient
{
    public const string RegistryAddress = "0x00000000000C2E074eC69A0dFb2997BA6C7d2e1e";
    private const string ZeroAddress = "0x0000000000000000000000000000000000000000";

    private readonly EvmRpcClient _rpc;
    private readonly string _registry;

    public EnsClient(EvmRpcClient rpc, string registry = RegistryAddress)
    {
        _rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));
        _registry = registry;
    }

    /// <summary>Lower-cases and NFC-normalizes a name; throws <see cref="FormatException"/> for invalid names.</summary>
    public static string Normalize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string n = name.Trim().Normalize(NormalizationForm.FormC).ToLowerInvariant();
        if (n.Length == 0) return n;
        foreach (string label in n.Split('.'))
        {
            if (label.Length == 0) throw new FormatException($"'{name}' has an empty label");
            if (label.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) throw new FormatException($"'{name}' contains whitespace or control characters");
        }
        return n;
    }

    /// <summary>EIP-137 namehash of a (normalized) name.</summary>
    public static byte[] NameHash(string name)
    {
        byte[] node = new byte[32];
        string normalized = Normalize(name);
        if (normalized.Length == 0) return node;
        foreach (string label in normalized.Split('.').Reverse())
            node = CryptoNative.Keccak256([.. node, .. CryptoNative.Keccak256(Encoding.UTF8.GetBytes(label))]);
        return node;
    }

    /// <summary>The resolver contract of <paramref name="name"/>, or null when none is set.</summary>
    public async ValueTask<string?> GetResolverAsync(string name, CancellationToken ct = default)
    {
        var r = await _rpc.CallFunctionAsync(_registry, "resolver(bytes32)", "address", [NameHash(name)], ct).ConfigureAwait(false);
        string resolver = (string)r[0]!;
        return IsZero(resolver) ? null : resolver;
    }

    /// <summary>The ETH address <paramref name="name"/> points to, or null.</summary>
    public async ValueTask<string?> ResolveAddressAsync(string name, CancellationToken ct = default)
    {
        string? resolver = await GetResolverAsync(name, ct).ConfigureAwait(false);
        if (resolver is null) return null;
        try
        {
            var r = await _rpc.CallFunctionAsync(resolver, "addr(bytes32)", "address", [NameHash(name)], ct).ConfigureAwait(false);
            string address = (string)r[0]!;
            return IsZero(address) ? null : address;
        }
        catch (RpcException)
        {
            return null; // resolver without addr() or an off-chain (CCIP-read) resolver
        }
    }

    /// <summary>
    /// The primary name of <paramref name="address"/> (reverse record), verified by resolving it forward again;
    /// null when unset or when the forward record does not point back.
    /// </summary>
    public async ValueTask<string?> LookupAddressAsync(string address, CancellationToken ct = default)
    {
        if (!EvmAddress.IsValid(address)) throw new FormatException($"Invalid address '{address}'");
        string reverseName = address[2..].ToLowerInvariant() + ".addr.reverse";
        string? resolver = await GetResolverAsync(reverseName, ct).ConfigureAwait(false);
        if (resolver is null) return null;
        string name;
        try
        {
            name = (string)(await _rpc.CallFunctionAsync(resolver, "name(bytes32)", "string", [NameHash(reverseName)], ct).ConfigureAwait(false))[0]!;
        }
        catch (RpcException)
        {
            return null;
        }
        if (string.IsNullOrEmpty(name)) return null;
        string? forward = await ResolveAddressAsync(name, ct).ConfigureAwait(false);
        return forward is not null && string.Equals(forward, address, StringComparison.OrdinalIgnoreCase) ? name : null;
    }

    /// <summary>A text record such as <c>avatar</c>, <c>url</c>, <c>com.twitter</c>; null when unset.</summary>
    public async ValueTask<string?> GetTextAsync(string name, string key, CancellationToken ct = default)
    {
        string? resolver = await GetResolverAsync(name, ct).ConfigureAwait(false);
        if (resolver is null) return null;
        var r = await _rpc.CallFunctionAsync(resolver, "text(bytes32,string)", "string", [NameHash(name), key], ct).ConfigureAwait(false);
        string value = (string)r[0]!;
        return value.Length == 0 ? null : value;
    }

    /// <summary>Raw EIP-1577 contenthash (IPFS/Swarm/…); null when unset.</summary>
    public async ValueTask<byte[]?> GetContentHashAsync(string name, CancellationToken ct = default)
    {
        string? resolver = await GetResolverAsync(name, ct).ConfigureAwait(false);
        if (resolver is null) return null;
        var r = await _rpc.CallFunctionAsync(resolver, "contenthash(bytes32)", "bytes", [NameHash(name)], ct).ConfigureAwait(false);
        byte[] value = (byte[])r[0]!;
        return value.Length == 0 ? null : value;
    }

    private static bool IsZero(string address) => string.Equals(address, ZeroAddress, StringComparison.OrdinalIgnoreCase);
}
