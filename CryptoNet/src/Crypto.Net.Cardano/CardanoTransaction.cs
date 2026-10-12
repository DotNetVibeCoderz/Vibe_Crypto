using System.Formats.Cbor;
using System.Text;
using Crypto.Net.Native;

namespace Crypto.Net.Cardano;

/// <summary>A reference to an unspent output: transaction id (hex) and output index.</summary>
public sealed record CardanoInput(string TxHash, uint Index);

/// <summary>A native asset quantity: policy id (hex, 28 bytes) and asset name (hex, up to 32 bytes).</summary>
public sealed record CardanoAsset(string PolicyId, string AssetNameHex, ulong Quantity)
{
    public string Unit => PolicyId + AssetNameHex;
}

/// <summary>A transaction output: address, lovelace and optional native assets.</summary>
public sealed record CardanoOutput(string Address, ulong Lovelace, IReadOnlyList<CardanoAsset>? Assets = null)
{
    internal void Write(CborWriter w)
    {
        w.WriteStartArray(2);
        w.WriteByteString(CardanoAddress.ToBytes(Address));
        CardanoValue.Write(w, Lovelace, Assets);
        w.WriteEndArray();
    }

    public byte[] ToCbor()
    {
        var w = new CborWriter();
        Write(w);
        return w.Encode();
    }

    /// <summary>Babbage/Conway minimum: <c>(160 + serialized size) × coinsPerUtxoByte</c>.</summary>
    public ulong MinLovelace(ulong coinsPerUtxoByte) => (160 + (ulong)ToCbor().Length) * coinsPerUtxoByte;
}

/// <summary>An unspent output as reported by an indexer.</summary>
public sealed record CardanoUtxo(CardanoInput Input, ulong Lovelace, IReadOnlyList<CardanoAsset> Assets);

/// <summary>Fee and deposit parameters of the current epoch.</summary>
public sealed record CardanoProtocolParameters(ulong MinFeeA, ulong MinFeeB, ulong CoinsPerUtxoByte, uint MaxTxSize)
{
    /// <summary>Linear fee: <c>minFeeA × size + minFeeB</c>.</summary>
    public ulong MinFee(int txSizeBytes) => MinFeeA * (ulong)txSizeBytes + MinFeeB;
}

internal static class CardanoValue
{
    // coin, or [coin, { policy => { name => quantity } }] with policies in byte order and names length-first (canonical CBOR).
    public static void Write(CborWriter w, ulong lovelace, IReadOnlyList<CardanoAsset>? assets)
    {
        if (assets is null || assets.Count == 0)
        {
            w.WriteUInt64(lovelace);
            return;
        }
        var policies = assets
            .GroupBy(a => a.PolicyId.ToLowerInvariant())
            .Select(g => (Policy: System.Convert.FromHexString(g.Key), Names: g.GroupBy(a => a.AssetNameHex.ToLowerInvariant())
                .Select(n => (Name: System.Convert.FromHexString(n.Key), Quantity: n.Aggregate(0UL, (s, a) => checked(s + a.Quantity))))
                .Where(n => n.Quantity > 0)
                .OrderBy(n => n.Name.Length).ThenBy(n => n.Name, ByteComparer.Instance).ToList()))
            .Where(p => p.Names.Count > 0)
            .OrderBy(p => p.Policy, ByteComparer.Instance)
            .ToList();
        if (policies.Count == 0)
        {
            w.WriteUInt64(lovelace);
            return;
        }
        w.WriteStartArray(2);
        w.WriteUInt64(lovelace);
        w.WriteStartMap(policies.Count);
        foreach (var (policy, names) in policies)
        {
            if (policy.Length != 28) throw new FormatException("Policy ids are 28 bytes");
            w.WriteByteString(policy);
            w.WriteStartMap(names.Count);
            foreach (var (name, quantity) in names)
            {
                if (name.Length > 32) throw new FormatException("Asset names are at most 32 bytes");
                w.WriteByteString(name);
                w.WriteUInt64(quantity);
            }
            w.WriteEndMap();
        }
        w.WriteEndMap();
        w.WriteEndArray();
    }
}

internal sealed class ByteComparer : IComparer<byte[]>
{
    public static readonly ByteComparer Instance = new();
    public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
}

/// <summary>Transaction metadata helpers (auxiliary data).</summary>
public static class CardanoMetadata
{
    /// <summary>CIP-20 message: label 674 with <c>{"msg": [...]}</c>, the text split into chunks of at most 64 bytes.</summary>
    public static byte[] Message(string text)
    {
        var chunks = SplitUtf8(text, 64);
        var w = new CborWriter();
        w.WriteStartMap(1);
        w.WriteUInt64(674);
        w.WriteStartMap(1);
        w.WriteTextString("msg");
        w.WriteStartArray(chunks.Count);
        foreach (var c in chunks) w.WriteTextString(c);
        w.WriteEndArray();
        w.WriteEndMap();
        w.WriteEndMap();
        return w.Encode();
    }

    /// <summary>Reads the CIP-20 message back from auxiliary data, or null if there is none.</summary>
    public static string? ReadMessage(ReadOnlySpan<byte> auxiliaryData)
    {
        try
        {
            return ReadMessage(new CborReader(auxiliaryData.ToArray()));
        }
        catch (Exception ex) when (ex is CborContentException or InvalidOperationException or OverflowException)
        {
            throw new FormatException("Invalid auxiliary data", ex);
        }
    }

    private static string? ReadMessage(CborReader r)
    {
        if (r.PeekState() != CborReaderState.StartMap) return null;
        int? n = r.ReadStartMap();
        for (int i = 0; n is null ? r.PeekState() != CborReaderState.EndMap : i < n; i++)
        {
            ulong label = r.ReadUInt64();
            if (label != 674)
            {
                r.SkipValue();
                continue;
            }
            int? m = r.ReadStartMap();
            for (int j = 0; m is null ? r.PeekState() != CborReaderState.EndMap : j < m; j++)
            {
                string key = r.ReadTextString();
                if (key != "msg")
                {
                    r.SkipValue();
                    continue;
                }
                var sb = new StringBuilder();
                int? len = r.ReadStartArray();
                for (int k = 0; len is null ? r.PeekState() != CborReaderState.EndArray : k < len; k++) sb.Append(r.ReadTextString());
                return sb.ToString();
            }
            return null;
        }
        return null;
    }

    private static List<string> SplitUtf8(string text, int maxBytes)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        int bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            int n = rune.Utf8SequenceLength;
            if (bytes + n > maxBytes)
            {
                parts.Add(current.ToString());
                current.Clear();
                bytes = 0;
            }
            current.Append(rune.ToString());
            bytes += n;
        }
        if (current.Length > 0 || parts.Count == 0) parts.Add(current.ToString());
        return parts;
    }
}

/// <summary>A Conway-era transaction: inputs, outputs, fee, time to live and optional metadata.</summary>
public sealed class CardanoTransaction
{
    public List<CardanoInput> Inputs { get; init; } = [];
    public List<CardanoOutput> Outputs { get; init; } = [];
    public ulong Fee { get; set; }

    /// <summary>Absolute slot after which the transaction is invalid, or null for none.</summary>
    public ulong? Ttl { get; set; }

    /// <summary>Auxiliary data CBOR (for example <see cref="CardanoMetadata.Message"/>), hashed into the body.</summary>
    public byte[]? AuxiliaryData { get; set; }

    public byte[] SerializeBody()
    {
        var w = new CborWriter();
        WriteBody(w);
        return w.Encode();
    }

    private void WriteBody(CborWriter w)
    {
        if (Inputs.Count == 0) throw new InvalidOperationException("A transaction needs at least one input");
        w.WriteStartMap(3 + (Ttl is null ? 0 : 1) + (AuxiliaryData is null ? 0 : 1));
        w.WriteUInt64(0);
        w.WriteTag((CborTag)258);
        w.WriteStartArray(Inputs.Count);
        foreach (var i in Inputs)
        {
            w.WriteStartArray(2);
            byte[] id = System.Convert.FromHexString(i.TxHash);
            if (id.Length != 32) throw new FormatException("Transaction ids are 32 bytes");
            w.WriteByteString(id);
            w.WriteUInt64(i.Index);
            w.WriteEndArray();
        }
        w.WriteEndArray();
        w.WriteUInt64(1);
        w.WriteStartArray(Outputs.Count);
        foreach (var o in Outputs) o.Write(w);
        w.WriteEndArray();
        w.WriteUInt64(2);
        w.WriteUInt64(Fee);
        if (Ttl is { } ttl)
        {
            w.WriteUInt64(3);
            w.WriteUInt64(ttl);
        }
        if (AuxiliaryData is not null)
        {
            w.WriteUInt64(7);
            w.WriteByteString(CryptoNative.Blake2b256(AuxiliaryData));
        }
        w.WriteEndMap();
    }

    /// <summary>The transaction id: blake2b-256 of the body (hex).</summary>
    public string Id => System.Convert.ToHexStringLower(CryptoNative.Blake2b256(SerializeBody()));

    /// <summary>Signs with each account's payment key and returns the full transaction CBOR.</summary>
    public byte[] Sign(params CardanoAccount[] signers)
    {
        byte[] body = SerializeBody();
        byte[] id = CryptoNative.Blake2b256(body);
        return Assemble(body, signers.Select(s => (s.PublicKey.ToArray(), s.Sign(id))).ToList());
    }

    /// <summary>Builds the transaction from a body and vkey witnesses <c>(publicKey, signature)</c>.</summary>
    public byte[] Assemble(byte[] body, IReadOnlyList<(byte[] PublicKey, byte[] Signature)> witnesses)
    {
        var w = new CborWriter();
        w.WriteStartArray(4);
        w.WriteEncodedValue(body);
        w.WriteStartMap(1);
        w.WriteUInt64(0);
        w.WriteTag((CborTag)258);
        w.WriteStartArray(witnesses.Count);
        foreach (var (pk, sig) in witnesses)
        {
            w.WriteStartArray(2);
            w.WriteByteString(pk);
            w.WriteByteString(sig);
            w.WriteEndArray();
        }
        w.WriteEndArray();
        w.WriteEndMap();
        w.WriteBoolean(true);
        if (AuxiliaryData is null) w.WriteNull();
        else w.WriteEncodedValue(AuxiliaryData);
        w.WriteEndArray();
        return w.Encode();
    }

    /// <summary>Id of a serialized transaction: blake2b-256 of its body exactly as encoded.</summary>
    public static string IdOf(ReadOnlySpan<byte> transaction)
    {
        try
        {
            var r = new CborReader(transaction.ToArray());
            r.ReadStartArray();
            return System.Convert.ToHexStringLower(CryptoNative.Blake2b256(r.ReadEncodedValue().Span));
        }
        catch (Exception ex) when (ex is CborContentException or InvalidOperationException or OverflowException)
        {
            throw new FormatException("Not a Cardano transaction", ex);
        }
    }
}
