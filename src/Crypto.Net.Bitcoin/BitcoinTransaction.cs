using System.Buffers.Binary;
using System.Numerics;
using Crypto.Net.Core;
using Crypto.Net.Native;

namespace Crypto.Net.Bitcoin;

/// <summary>A previous output being spent: outpoint plus the data needed to sign it.</summary>
public sealed record BitcoinUtxo(string TxId, uint Vout, long ValueSats, byte[] ScriptPubKey)
{
    /// <summary>Address type inferred from the script.</summary>
    public BitcoinAddressType? Type => ScriptPubKey switch
    {
        [0x76, 0xa9, 0x14, .., 0x88, 0xac] when ScriptPubKey.Length == 25 => BitcoinAddressType.LegacyP2PKH,
        [0xa9, 0x14, .., 0x87] when ScriptPubKey.Length == 23 => BitcoinAddressType.P2SH,
        [0x00, 0x14, ..] when ScriptPubKey.Length == 22 => BitcoinAddressType.SegWitP2WPKH,
        [0x00, 0x20, ..] when ScriptPubKey.Length == 34 => BitcoinAddressType.SegWitP2WSH,
        [0x51, 0x20, ..] when ScriptPubKey.Length == 34 => BitcoinAddressType.TaprootP2TR,
        _ => null,
    };
}

public sealed class BitcoinTxInput
{
    /// <summary>Previous transaction id in display (big-endian) hex, as shown by explorers.</summary>
    public required string TxId { get; init; }
    public required uint Vout { get; init; }
    public byte[] ScriptSig { get; set; } = [];
    public uint Sequence { get; set; } = 0xFFFFFFFD; // opt-in RBF (BIP-125)
    public List<byte[]> Witness { get; set; } = [];

    internal byte[] OutpointBytes()
    {
        byte[] txid = Convert.FromHexString(TxId);
        if (txid.Length != 32) throw new FormatException("txid must be 32 bytes");
        Array.Reverse(txid);
        byte[] o = new byte[36];
        txid.CopyTo(o, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(32), Vout);
        return o;
    }
}

public sealed class BitcoinTxOutput
{
    public required long ValueSats { get; init; }
    public required byte[] ScriptPubKey { get; init; }

    internal byte[] Serialize()
    {
        using var ms = new MemoryStream();
        Span<byte> v = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(v, ValueSats);
        ms.Write(v);
        BitcoinTransaction.WriteVarBytes(ms, ScriptPubKey);
        return ms.ToArray();
    }
}

/// <summary>Sighash flags. Taproot uses <see cref="Default"/> (0x00) which commits to everything like ALL.</summary>
public enum SigHashType : byte
{
    Default = 0x00,
    All = 0x01,
    None = 0x02,
    Single = 0x03,
    AnyoneCanPay = 0x80,
}

/// <summary>
/// Bitcoin transaction with SegWit serialization and signing for P2PKH (legacy), P2WPKH (BIP-143)
/// and Taproot key-path P2TR (BIP-341 / BIP-86) inputs using SIGHASH_ALL / SIGHASH_DEFAULT.
/// </summary>
public sealed class BitcoinTransaction
{
    public uint Version { get; set; } = 2;
    public List<BitcoinTxInput> Inputs { get; } = [];
    public List<BitcoinTxOutput> Outputs { get; } = [];
    public uint LockTime { get; set; }

    public bool HasWitness => Inputs.Any(i => i.Witness.Count > 0);

    public BitcoinTransaction AddInput(string txId, uint vout, uint sequence = 0xFFFFFFFD)
    {
        Inputs.Add(new BitcoinTxInput { TxId = txId, Vout = vout, Sequence = sequence });
        return this;
    }

    public BitcoinTransaction AddOutput(string address, long valueSats, BitcoinNetwork? network = null)
    {
        Outputs.Add(new BitcoinTxOutput { ValueSats = valueSats, ScriptPubKey = BitcoinAddress.GetScriptPubKey(address, network) });
        return this;
    }

    public BitcoinTransaction AddOutput(byte[] scriptPubKey, long valueSats)
    {
        Outputs.Add(new BitcoinTxOutput { ValueSats = valueSats, ScriptPubKey = scriptPubKey });
        return this;
    }

    #region Serialization

    public byte[] Serialize(bool includeWitness = true)
    {
        bool witness = includeWitness && HasWitness;
        using var ms = new MemoryStream();
        WriteUInt32(ms, Version);
        if (witness)
        {
            ms.WriteByte(0x00);
            ms.WriteByte(0x01);
        }
        WriteVarInt(ms, (ulong)Inputs.Count);
        foreach (var input in Inputs)
        {
            ms.Write(input.OutpointBytes());
            WriteVarBytes(ms, input.ScriptSig);
            WriteUInt32(ms, input.Sequence);
        }
        WriteVarInt(ms, (ulong)Outputs.Count);
        foreach (var output in Outputs) ms.Write(output.Serialize());
        if (witness)
        {
            foreach (var input in Inputs)
            {
                WriteVarInt(ms, (ulong)input.Witness.Count);
                foreach (var item in input.Witness) WriteVarBytes(ms, item);
            }
        }
        WriteUInt32(ms, LockTime);
        return ms.ToArray();
    }

    /// <summary>Transaction id (double SHA-256 of the non-witness serialization, displayed reversed).</summary>
    public string TxId => DisplayHash(CryptoNative.DoubleSha256(Serialize(includeWitness: false)));

    /// <summary>Witness transaction id.</summary>
    public string WTxId => DisplayHash(CryptoNative.DoubleSha256(Serialize()));

    /// <summary>BIP-141 weight units.</summary>
    public int Weight => Serialize(includeWitness: false).Length * 3 + Serialize().Length;

    /// <summary>Virtual size in vbytes (weight / 4, rounded up) — the size fee rates apply to.</summary>
    public int VirtualSize => (Weight + 3) / 4;

    public string ToHex() => Convert.ToHexStringLower(Serialize());

    private static string DisplayHash(byte[] hash)
    {
        Array.Reverse(hash);
        return Convert.ToHexStringLower(hash);
    }

    internal static void WriteUInt32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        s.Write(b);
    }

    internal static void WriteVarInt(Stream s, ulong v)
    {
        Span<byte> b = stackalloc byte[9];
        int len;
        if (v < 0xFD) { b[0] = (byte)v; len = 1; }
        else if (v <= 0xFFFF) { b[0] = 0xFD; BinaryPrimitives.WriteUInt16LittleEndian(b[1..], (ushort)v); len = 3; }
        else if (v <= 0xFFFFFFFF) { b[0] = 0xFE; BinaryPrimitives.WriteUInt32LittleEndian(b[1..], (uint)v); len = 5; }
        else { b[0] = 0xFF; BinaryPrimitives.WriteUInt64LittleEndian(b[1..], v); len = 9; }
        s.Write(b[..len]);
    }

    internal static void WriteVarBytes(Stream s, ReadOnlySpan<byte> data)
    {
        WriteVarInt(s, (ulong)data.Length);
        s.Write(data);
    }

    #endregion

    #region Sighash

    /// <summary>Legacy (pre-SegWit) signature hash for input <paramref name="index"/>.</summary>
    public byte[] GetLegacySigHash(int index, ReadOnlySpan<byte> scriptCode, SigHashType type = SigHashType.All)
    {
        if (type != SigHashType.All) throw new NotSupportedException("Only SIGHASH_ALL is supported for legacy inputs");
        var copy = new BitcoinTransaction { Version = Version, LockTime = LockTime };
        for (int i = 0; i < Inputs.Count; i++)
            copy.Inputs.Add(new BitcoinTxInput { TxId = Inputs[i].TxId, Vout = Inputs[i].Vout, Sequence = Inputs[i].Sequence, ScriptSig = i == index ? scriptCode.ToArray() : [] });
        copy.Outputs.AddRange(Outputs);
        byte[] serialized = copy.Serialize(includeWitness: false);
        byte[] preimage = new byte[serialized.Length + 4];
        serialized.CopyTo(preimage, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(preimage.AsSpan(serialized.Length), (uint)type);
        return CryptoNative.DoubleSha256(preimage);
    }

    /// <summary>BIP-143 SegWit v0 signature hash (SIGHASH_ALL).</summary>
    public byte[] GetSegwitV0SigHash(int index, ReadOnlySpan<byte> scriptCode, long amountSats, SigHashType type = SigHashType.All)
    {
        if (type != SigHashType.All) throw new NotSupportedException("Only SIGHASH_ALL is supported for SegWit v0 inputs");
        using var prevouts = new MemoryStream();
        using var sequences = new MemoryStream();
        foreach (var i in Inputs)
        {
            prevouts.Write(i.OutpointBytes());
            WriteUInt32(sequences, i.Sequence);
        }
        using var outputs = new MemoryStream();
        foreach (var o in Outputs) outputs.Write(o.Serialize());

        using var ms = new MemoryStream();
        WriteUInt32(ms, Version);
        ms.Write(CryptoNative.DoubleSha256(prevouts.ToArray()));
        ms.Write(CryptoNative.DoubleSha256(sequences.ToArray()));
        ms.Write(Inputs[index].OutpointBytes());
        WriteVarBytes(ms, scriptCode);
        Span<byte> amount = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(amount, amountSats);
        ms.Write(amount);
        WriteUInt32(ms, Inputs[index].Sequence);
        ms.Write(CryptoNative.DoubleSha256(outputs.ToArray()));
        WriteUInt32(ms, LockTime);
        WriteUInt32(ms, (uint)type);
        return CryptoNative.DoubleSha256(ms.ToArray());
    }

    /// <summary>BIP-341 Taproot key-path signature hash (SIGHASH_DEFAULT, no annex).</summary>
    public byte[] GetTaprootSigHash(int index, IReadOnlyList<BitcoinUtxo> spentOutputs, SigHashType type = SigHashType.Default)
    {
        if (type is not (SigHashType.Default or SigHashType.All)) throw new NotSupportedException("Only SIGHASH_DEFAULT/ALL are supported for Taproot inputs");
        if (spentOutputs.Count != Inputs.Count) throw new ArgumentException("Taproot signing needs every spent output");

        using var prevouts = new MemoryStream();
        using var amounts = new MemoryStream();
        using var scripts = new MemoryStream();
        using var sequences = new MemoryStream();
        Span<byte> v8 = stackalloc byte[8];
        for (int i = 0; i < Inputs.Count; i++)
        {
            prevouts.Write(Inputs[i].OutpointBytes());
            BinaryPrimitives.WriteInt64LittleEndian(v8, spentOutputs[i].ValueSats);
            amounts.Write(v8);
            WriteVarBytes(scripts, spentOutputs[i].ScriptPubKey);
            WriteUInt32(sequences, Inputs[i].Sequence);
        }
        using var outputs = new MemoryStream();
        foreach (var o in Outputs) outputs.Write(o.Serialize());

        using var msg = new MemoryStream();
        msg.WriteByte(0x00); // epoch
        msg.WriteByte((byte)type);
        WriteUInt32(msg, Version);
        WriteUInt32(msg, LockTime);
        msg.Write(CryptoNative.Sha256(prevouts.ToArray()));
        msg.Write(CryptoNative.Sha256(amounts.ToArray()));
        msg.Write(CryptoNative.Sha256(scripts.ToArray()));
        msg.Write(CryptoNative.Sha256(sequences.ToArray()));
        msg.Write(CryptoNative.Sha256(outputs.ToArray()));
        msg.WriteByte(0x00); // spend_type: key path, no annex
        WriteUInt32(msg, (uint)index);
        return CryptoNative.TaggedHash("TapSighash", msg.ToArray());
    }

    #endregion

    #region Signing

    /// <summary>
    /// Signs every input with <paramref name="privateKey"/> according to the type of the spent output
    /// (P2PKH, P2WPKH or BIP-86 P2TR). <paramref name="spentOutputs"/> must be in input order.
    /// </summary>
    public void SignAllInputs(ReadOnlySpan<byte> privateKey, IReadOnlyList<BitcoinUtxo> spentOutputs)
    {
        for (int i = 0; i < Inputs.Count; i++)
            SignInput(i, privateKey, spentOutputs);
    }

    public void SignInput(int index, ReadOnlySpan<byte> privateKey, IReadOnlyList<BitcoinUtxo> spentOutputs)
    {
        var utxo = spentOutputs[index];
        byte[] pub = CryptoNative.Secp256k1GetPublicKey(privateKey, compressed: true);
        switch (utxo.Type)
        {
            case BitcoinAddressType.LegacyP2PKH:
            {
                EnsureKeyMatches(utxo.ScriptPubKey.AsSpan(3, 20), pub);
                byte[] hash = GetLegacySigHash(index, utxo.ScriptPubKey);
                byte[] sig = EcdsaDer(privateKey, hash, SigHashType.All);
                Inputs[index].ScriptSig = [.. BitcoinScript.PushData(sig), .. BitcoinScript.PushData(pub)];
                Inputs[index].Witness = [];
                break;
            }
            case BitcoinAddressType.SegWitP2WPKH:
            {
                byte[] pkh = utxo.ScriptPubKey[2..];
                EnsureKeyMatches(pkh, pub);
                byte[] hash = GetSegwitV0SigHash(index, BitcoinScript.P2PKH(pkh), utxo.ValueSats);
                Inputs[index].ScriptSig = [];
                Inputs[index].Witness = [EcdsaDer(privateKey, hash, SigHashType.All), pub];
                break;
            }
            case BitcoinAddressType.TaprootP2TR:
            {
                byte[] outputKey = BitcoinAddress.TaprootOutputKey(pub.AsSpan(1));
                if (!outputKey.AsSpan().SequenceEqual(utxo.ScriptPubKey.AsSpan(2)))
                    throw new InvalidOperationException($"Input {index}: key does not match the Taproot output");
                byte[] hash = GetTaprootSigHash(index, spentOutputs);
                byte[] tweaked = CryptoNative.TaprootTweakSecretKey(privateKey);
                try
                {
                    Inputs[index].ScriptSig = [];
                    Inputs[index].Witness = [CryptoNative.SchnorrSign(tweaked, hash, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))];
                }
                finally
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(tweaked);
                }
                break;
            }
            default:
                throw new NotSupportedException($"Input {index}: unsupported output script type");
        }
    }

    private static void EnsureKeyMatches(ReadOnlySpan<byte> pubKeyHash, byte[] pub)
    {
        if (!CryptoNative.Hash160(pub).AsSpan().SequenceEqual(pubKeyHash))
            throw new InvalidOperationException("Private key does not match the spent output");
    }

    /// <summary>DER-encoded low-S ECDSA signature followed by the sighash byte.</summary>
    public static byte[] EcdsaDer(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> sigHash, SigHashType type)
    {
        var (sig, _) = CryptoNative.Secp256k1SignRecoverable(privateKey, sigHash);
        return [.. ToDer(sig), (byte)type];
    }

    /// <summary>Converts a 64-byte r||s signature into strict DER.</summary>
    public static byte[] ToDer(ReadOnlySpan<byte> rs)
    {
        static byte[] Int(ReadOnlySpan<byte> v)
        {
            int start = 0;
            while (start < v.Length - 1 && v[start] == 0) start++;
            v = v[start..];
            return (v[0] & 0x80) != 0 ? [0x02, (byte)(v.Length + 1), 0x00, .. v] : [0x02, (byte)v.Length, .. v];
        }
        byte[] r = Int(rs[..32]);
        byte[] s = Int(rs[32..]);
        return [0x30, (byte)(r.Length + s.Length), .. r, .. s];
    }

    #endregion

    /// <summary>Estimated vsize for a transaction spending <paramref name="inputType"/> inputs.</summary>
    public static int EstimateVirtualSize(int inputs, int outputs, BitcoinAddressType inputType = BitcoinAddressType.SegWitP2WPKH)
    {
        double perInput = inputType switch
        {
            BitcoinAddressType.LegacyP2PKH => 148,
            BitcoinAddressType.TaprootP2TR => 57.5,
            _ => 68,
        };
        return (int)Math.Ceiling(10.5 + inputs * perInput + outputs * 43);
    }
}

/// <summary>Result of <see cref="CoinSelector.Select"/>.</summary>
public sealed record CoinSelection(IReadOnlyList<BitcoinUtxo> Inputs, long FeeSats, long ChangeSats);

/// <summary>Simple largest-first coin selection with dust-aware change.</summary>
public static class CoinSelector
{
    public const long DustLimit = 546;

    public static CoinSelection Select(IEnumerable<BitcoinUtxo> utxos, long targetSats, decimal feeRateSatPerVb, BitcoinAddressType inputType = BitcoinAddressType.SegWitP2WPKH)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetSats);
        var selected = new List<BitcoinUtxo>();
        long total = 0;
        foreach (var u in utxos.OrderByDescending(u => u.ValueSats))
        {
            selected.Add(u);
            total += u.ValueSats;

            long feeWithChange = Fee(selected.Count, 2);
            if (total >= targetSats + feeWithChange)
            {
                long change = total - targetSats - feeWithChange;
                if (change >= DustLimit) return new CoinSelection(selected, feeWithChange, change);
                // Change would be dust: drop it and give the remainder to the miner.
                return new CoinSelection(selected, total - targetSats, 0);
            }
            long feeNoChange = Fee(selected.Count, 1);
            if (total >= targetSats + feeNoChange)
                return new CoinSelection(selected, total - targetSats, 0);
        }
        throw new InvalidOperationException($"Insufficient funds: have {total} sats, need {targetSats} sats plus fees");

        long Fee(int inputs, int outputs) =>
            (long)Math.Ceiling(BitcoinTransaction.EstimateVirtualSize(inputs, outputs, inputType) * feeRateSatPerVb);
    }
}
