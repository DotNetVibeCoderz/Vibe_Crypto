using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Crypto.Net.Core;
using Crypto.Net.Native;

namespace Crypto.Net.Evm;

/// <summary>
/// Off-chain message signing for EVM wallets: EIP-191 <c>personal_sign</c> and EIP-712 typed data
/// (<c>eth_signTypedData_v4</c>). Signatures are 65 bytes: r || s || v with v ∈ {27, 28}.
/// </summary>
public static class EvmMessageSigner
{
    // ------------------------------------------------------------------ EIP-191

    /// <summary>keccak256("\x19Ethereum Signed Message:\n" + len(message) + message).</summary>
    public static byte[] HashPersonalMessage(ReadOnlySpan<byte> message)
    {
        byte[] prefix = Encoding.ASCII.GetBytes($"\u0019Ethereum Signed Message:\n{message.Length.ToString(CultureInfo.InvariantCulture)}");
        return CryptoNative.Keccak256([.. prefix, .. message]);
    }

    public static byte[] HashPersonalMessage(string message) => HashPersonalMessage(Encoding.UTF8.GetBytes(message));

    /// <summary>Signs like MetaMask <c>personal_sign</c>. Returns the 65-byte signature.</summary>
    public static byte[] SignPersonalMessage(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> message) =>
        SignDigest(privateKey, HashPersonalMessage(message));

    public static byte[] SignPersonalMessage(ReadOnlySpan<byte> privateKey, string message) =>
        SignPersonalMessage(privateKey, Encoding.UTF8.GetBytes(message));

    public static async ValueTask<byte[]> SignPersonalMessageAsync(ISigner signer, ReadOnlyMemory<byte> message, CancellationToken ct = default) =>
        ToRsv(await signer.SignAsync(HashPersonalMessage(message.Span), ct).ConfigureAwait(false));

    /// <summary>Recovers the checksummed address that produced a personal_sign signature.</summary>
    public static string RecoverPersonalMessageSigner(ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature) =>
        RecoverDigestSigner(HashPersonalMessage(message), signature);

    public static string RecoverPersonalMessageSigner(string message, string signatureHex) =>
        RecoverPersonalMessageSigner(Encoding.UTF8.GetBytes(message), HexUtil.Decode(signatureHex));

    // ------------------------------------------------------------------ EIP-712

    /// <summary>EIP-712 digest: keccak256(0x1901 || domainSeparator || hashStruct(primaryType, message)).</summary>
    public static byte[] HashTypedData(string typedDataJson)
    {
        using var doc = JsonDocument.Parse(typedDataJson);
        var root = doc.RootElement;
        var types = ParseTypes(root.GetProperty("types"));
        string primary = root.GetProperty("primaryType").GetString()!;

        byte[] domainSeparator = HashStruct("EIP712Domain", root.GetProperty("domain"), types);
        byte[] messageHash = primary == "EIP712Domain" ? [] : HashStruct(primary, root.GetProperty("message"), types);
        return CryptoNative.Keccak256([0x19, 0x01, .. domainSeparator, .. messageHash]);
    }

    public static byte[] SignTypedData(ReadOnlySpan<byte> privateKey, string typedDataJson) =>
        SignDigest(privateKey, HashTypedData(typedDataJson));

    public static async ValueTask<byte[]> SignTypedDataAsync(ISigner signer, string typedDataJson, CancellationToken ct = default) =>
        ToRsv(await signer.SignAsync(HashTypedData(typedDataJson), ct).ConfigureAwait(false));

    public static string RecoverTypedDataSigner(string typedDataJson, ReadOnlySpan<byte> signature) =>
        RecoverDigestSigner(HashTypedData(typedDataJson), signature);

    /// <summary>The canonical <c>encodeType</c> string, e.g. <c>Mail(Person from,Person to,string contents)Person(...)</c>.</summary>
    public static string EncodeType(string typedDataJson, string typeName)
    {
        using var doc = JsonDocument.Parse(typedDataJson);
        return EncodeType(typeName, ParseTypes(doc.RootElement.GetProperty("types")));
    }

    private sealed record Field(string Name, string Type);

    private static Dictionary<string, List<Field>> ParseTypes(JsonElement types)
    {
        var result = new Dictionary<string, List<Field>>(StringComparer.Ordinal);
        foreach (var t in types.EnumerateObject())
            result[t.Name] = t.Value.EnumerateArray()
                .Select(f => new Field(f.GetProperty("name").GetString()!, f.GetProperty("type").GetString()!))
                .ToList();
        return result;
    }

    private static string BaseType(string type)
    {
        int bracket = type.IndexOf('[');
        return bracket < 0 ? type : type[..bracket];
    }

    private static void CollectDependencies(string type, Dictionary<string, List<Field>> types, SortedSet<string> found)
    {
        type = BaseType(type);
        if (!types.TryGetValue(type, out var fields) || !found.Add(type)) return;
        foreach (var f in fields) CollectDependencies(f.Type, types, found);
    }

    private static string EncodeType(string primary, Dictionary<string, List<Field>> types)
    {
        var deps = new SortedSet<string>(StringComparer.Ordinal);
        CollectDependencies(primary, types, deps);
        deps.Remove(primary);
        var sb = new StringBuilder();
        foreach (string t in new[] { primary }.Concat(deps))
            sb.Append(t).Append('(').Append(string.Join(",", types[t].Select(f => $"{f.Type} {f.Name}"))).Append(')');
        return sb.ToString();
    }

    private static byte[] HashStruct(string type, JsonElement data, Dictionary<string, List<Field>> types)
    {
        if (!types.TryGetValue(type, out var fields)) throw new FormatException($"Unknown EIP-712 type '{type}'");
        using var ms = new MemoryStream();
        ms.Write(CryptoNative.Keccak256(Encoding.UTF8.GetBytes(EncodeType(type, types))));
        foreach (var f in fields)
        {
            data.TryGetProperty(f.Name, out var value);
            ms.Write(EncodeField(f.Type, value, types));
        }
        return CryptoNative.Keccak256(ms.ToArray());
    }

    private static byte[] EncodeField(string type, JsonElement value, Dictionary<string, List<Field>> types)
    {
        if (type.EndsWith(']'))
        {
            string element = type[..type.LastIndexOf('[')];
            using var ms = new MemoryStream();
            if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray())
                    ms.Write(EncodeField(element, item, types));
            return CryptoNative.Keccak256(ms.ToArray());
        }

        if (types.ContainsKey(type))
            return value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? new byte[32] : HashStruct(type, value, types);

        switch (type)
        {
            case "string":
                return CryptoNative.Keccak256(Encoding.UTF8.GetBytes(value.ValueKind == JsonValueKind.String ? value.GetString()! : ""));
            case "bytes":
                return CryptoNative.Keccak256(value.ValueKind == JsonValueKind.String ? HexUtil.Decode(value.GetString()!) : []);
            case "bool":
                return EvmAbi.Encode([new AbiType.Bool()], [value.ValueKind == JsonValueKind.True || (value.ValueKind == JsonValueKind.String && bool.Parse(value.GetString()!))]);
            case "address":
                return EvmAbi.Encode([new AbiType.AddressType()], [value.GetString()]);
        }

        var abiType = AbiType.Parse(type);
        object? v = abiType switch
        {
            AbiType.UInt or AbiType.Int => ParseNumber(value),
            AbiType.FixedBytes => HexUtil.Decode(value.GetString()!),
            _ => throw new NotSupportedException($"EIP-712 type '{type}' is not supported"),
        };
        return EvmAbi.Encode([abiType], [v]);
    }

    private static BigInteger ParseNumber(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => BigInteger.Parse(value.GetRawText(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
        JsonValueKind.String when value.GetString()!.StartsWith("0x", StringComparison.OrdinalIgnoreCase) => HexUtil.ParseQuantity(value.GetString()),
        JsonValueKind.String => BigInteger.Parse(value.GetString()!, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
        _ => BigInteger.Zero,
    };

    // ------------------------------------------------------------------ helpers

    private static byte[] SignDigest(ReadOnlySpan<byte> privateKey, byte[] digest)
    {
        var (sig, recId) = CryptoNative.Secp256k1SignRecoverable(privateKey, digest);
        return [.. sig, (byte)(27 + recId)];
    }

    private static byte[] ToRsv(Signature signature) =>
        [.. signature.Bytes.Span, (byte)(27 + (signature.RecoveryId ?? throw new InvalidOperationException("Signer did not return a recovery id")))];

    /// <summary>Recovers the signer of a 32-byte digest from a 65-byte r||s||v signature (v = 0/1/27/28).</summary>
    public static string RecoverDigestSigner(ReadOnlySpan<byte> digest, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != 65) throw new ArgumentException("Signature must be 65 bytes (r||s||v)", nameof(signature));
        byte v = signature[64];
        byte recId = v >= 27 ? (byte)(v - 27) : v;
        byte[] pub = CryptoNative.Secp256k1RecoverPublicKey(digest, signature[..64], recId, compressed: false);
        return EvmAddress.FromPublicKey(pub).Value;
    }
}
