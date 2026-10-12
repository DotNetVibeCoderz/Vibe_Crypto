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
    AllAnyoneCanPay = 0x81,
    NoneAnyoneCanPay = 0x82,
    SingleAnyoneCanPay = 0x83,
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

    /// <summary>Parses a serialized transaction (with or without SegWit marker).</summary>
    public static BitcoinTransaction Parse(ReadOnlySpan<byte> raw) => Parse(raw, allowWitness: true);

    /// <summary>
    /// Parses a transaction. With <paramref name="allowWitness"/> false the SegWit marker is not recognised,
    /// so a transaction with zero inputs parses (as PSBT unsigned transactions require).
    /// </summary>
    internal static BitcoinTransaction Parse(ReadOnlySpan<byte> raw, bool allowWitness)
    {
        var r = new TxReader(raw);
        var tx = new BitcoinTransaction { Version = r.UInt32() };
        bool segwit = false;
        ulong inputCount = r.VarInt();
        if (inputCount == 0 && allowWitness)
        {
            if (r.Byte() != 0x01) throw new FormatException("Invalid SegWit flag");
            segwit = true;
            inputCount = r.VarInt();
        }
        for (ulong i = 0; i < inputCount; i++)
        {
            byte[] txid = r.Bytes(32).ToArray();
            Array.Reverse(txid);
            uint vout = r.UInt32();
            byte[] scriptSig = r.VarBytes().ToArray();
            uint sequence = r.UInt32();
            tx.Inputs.Add(new BitcoinTxInput { TxId = Convert.ToHexStringLower(txid), Vout = vout, ScriptSig = scriptSig, Sequence = sequence });
        }
        ulong outputCount = r.VarInt();
        for (ulong i = 0; i < outputCount; i++)
        {
            long value = (long)r.UInt64();
            tx.Outputs.Add(new BitcoinTxOutput { ValueSats = value, ScriptPubKey = r.VarBytes().ToArray() });
        }
        if (segwit)
        {
            foreach (var input in tx.Inputs)
            {
                ulong items = r.VarInt();
                for (ulong k = 0; k < items; k++) input.Witness.Add(r.VarBytes().ToArray());
            }
        }
        tx.LockTime = r.UInt32();
        if (r.Remaining != 0) throw new FormatException("Trailing bytes after transaction");
        return tx;
    }

    public static BitcoinTransaction Parse(string hex) => Parse(Convert.FromHexString(hex.Trim()));

    internal ref struct TxReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _pos;

        public readonly int Remaining => _data.Length - _pos;

        public ReadOnlySpan<byte> Bytes(int n)
        {
            if (n < 0 || _pos + n > _data.Length) throw new FormatException("Transaction data is truncated");
            var s = _data.Slice(_pos, n);
            _pos += n;
            return s;
        }

        public byte Byte() => Bytes(1)[0];
        public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4));
        public ulong UInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Bytes(8));

        public ulong VarInt()
        {
            byte b = Byte();
            return b switch
            {
                0xFD => BinaryPrimitives.ReadUInt16LittleEndian(Bytes(2)),
                0xFE => BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4)),
                0xFF => UInt64(),
                _ => b,
            };
        }

        public ReadOnlySpan<byte> VarBytes()
        {
            ulong len = VarInt();
            if (len > (ulong)Remaining) throw new FormatException("Length prefix exceeds the data");
            return Bytes((int)len);
        }
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

    /// <summary>
    /// Legacy (pre-SegWit) signature hash for input <paramref name="index"/>, for any sighash type
    /// (ALL, NONE, SINGLE, optionally | ANYONECANPAY), including the historical SIGHASH_SINGLE "one" quirk.
    /// </summary>
    public byte[] GetLegacySigHash(int index, ReadOnlySpan<byte> scriptCode, SigHashType type = SigHashType.All)
    {
        if (type == SigHashType.Default) throw new ArgumentException("SIGHASH_DEFAULT is Taproot-only");
        return GetLegacySigHash(index, scriptCode, (uint)type);
    }

    /// <summary>
    /// Legacy signature hash with a raw 32-bit hash type, exactly as consensus computes it (OP_CODESEPARATORs are
    /// removed from <paramref name="scriptCode"/>).
    /// </summary>
    public byte[] GetLegacySigHash(int index, ReadOnlySpan<byte> scriptCode, uint hashType)
    {
        int baseType = (int)(hashType & 0x1f);
        bool anyoneCanPay = (hashType & 0x80) != 0;
        if (baseType == (int)SigHashType.Single && index >= Outputs.Count)
        {
            byte[] one = new byte[32];
            one[0] = 1;
            return one;
        }
        scriptCode = RemoveCodeSeparators(scriptCode);

        var copy = new BitcoinTransaction { Version = Version, LockTime = LockTime };
        for (int i = 0; i < Inputs.Count; i++)
        {
            if (anyoneCanPay && i != index) continue;
            uint sequence = i != index && baseType is (int)SigHashType.None or (int)SigHashType.Single ? 0 : Inputs[i].Sequence;
            copy.Inputs.Add(new BitcoinTxInput { TxId = Inputs[i].TxId, Vout = Inputs[i].Vout, Sequence = sequence, ScriptSig = i == index ? scriptCode.ToArray() : [] });
        }
        if (baseType == (int)SigHashType.Single)
        {
            for (int i = 0; i < index; i++) copy.Outputs.Add(new BitcoinTxOutput { ValueSats = -1, ScriptPubKey = [] });
            copy.Outputs.Add(Outputs[index]);
        }
        else if (baseType != (int)SigHashType.None)
        {
            copy.Outputs.AddRange(Outputs);
        }
        byte[] serialized = copy.Serialize(includeWitness: false);
        byte[] preimage = new byte[serialized.Length + 4];
        serialized.CopyTo(preimage, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(preimage.AsSpan(serialized.Length), hashType);
        return CryptoNative.DoubleSha256(preimage);
    }

    private static byte[] RemoveCodeSeparators(ReadOnlySpan<byte> script)
    {
        const byte OP_CODESEPARATOR = 0xab;
        if (script.IndexOf(OP_CODESEPARATOR) < 0) return script.ToArray();
        var result = new List<byte>(script.Length);
        int pos = 0;
        while (pos < script.Length)
        {
            int start = pos;
            byte op = script[pos++];
            int len = op switch
            {
                < 0x4c => op,
                0x4c => pos + 1 <= script.Length ? script[pos] + 1 : script.Length - pos,
                0x4d => pos + 2 <= script.Length ? BinaryPrimitives.ReadUInt16LittleEndian(script[pos..]) + 2 : script.Length - pos,
                0x4e => pos + 4 <= script.Length ? (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(script[pos..]) + 4L, int.MaxValue) : script.Length - pos,
                _ => 0,
            };
            pos = (int)Math.Min((long)pos + len, script.Length);
            if (op != OP_CODESEPARATOR) result.AddRange(script[start..pos]);
        }
        return [.. result];
    }

    /// <summary>BIP-143 SegWit v0 signature hash for any sighash type (ALL, NONE, SINGLE, optionally | ANYONECANPAY).</summary>
    public byte[] GetSegwitV0SigHash(int index, ReadOnlySpan<byte> scriptCode, long amountSats, SigHashType type = SigHashType.All)
    {
        if (type == SigHashType.Default) throw new ArgumentException("SIGHASH_DEFAULT is Taproot-only");
        int baseType = (int)type & 0x1f;
        bool anyoneCanPay = ((int)type & 0x80) != 0;
        byte[] zero = new byte[32];

        byte[] hashPrevouts = zero, hashSequence = zero, hashOutputs = zero;
        if (!anyoneCanPay)
        {
            using var prevouts = new MemoryStream();
            foreach (var i in Inputs) prevouts.Write(i.OutpointBytes());
            hashPrevouts = CryptoNative.DoubleSha256(prevouts.ToArray());
        }
        if (!anyoneCanPay && baseType != (int)SigHashType.Single && baseType != (int)SigHashType.None)
        {
            using var sequences = new MemoryStream();
            foreach (var i in Inputs) WriteUInt32(sequences, i.Sequence);
            hashSequence = CryptoNative.DoubleSha256(sequences.ToArray());
        }
        if (baseType != (int)SigHashType.Single && baseType != (int)SigHashType.None)
        {
            using var outputs = new MemoryStream();
            foreach (var o in Outputs) outputs.Write(o.Serialize());
            hashOutputs = CryptoNative.DoubleSha256(outputs.ToArray());
        }
        else if (baseType == (int)SigHashType.Single && index < Outputs.Count)
        {
            hashOutputs = CryptoNative.DoubleSha256(Outputs[index].Serialize());
        }

        using var ms = new MemoryStream();
        WriteUInt32(ms, Version);
        ms.Write(hashPrevouts);
        ms.Write(hashSequence);
        ms.Write(Inputs[index].OutpointBytes());
        WriteVarBytes(ms, scriptCode);
        Span<byte> amount = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(amount, amountSats);
        ms.Write(amount);
        WriteUInt32(ms, Inputs[index].Sequence);
        ms.Write(hashOutputs);
        WriteUInt32(ms, LockTime);
        WriteUInt32(ms, (uint)type);
        return CryptoNative.DoubleSha256(ms.ToArray());
    }

    /// <summary>
    /// BIP-341 signature message, including the leading epoch byte 0x00 (the input to the TapSighash tagged hash),
    /// for any sighash type. Pass
    /// <paramref name="leafHash"/> for a script-path spend (BIP-342) and <paramref name="annex"/> when present.
    /// </summary>
    public byte[] GetTaprootSigMessage(int index, IReadOnlyList<BitcoinUtxo> spentOutputs, SigHashType type = SigHashType.Default,
        ReadOnlySpan<byte> leafHash = default, ReadOnlySpan<byte> annex = default, uint codeSeparatorPosition = 0xFFFFFFFF)
    {
        int baseType = (int)type & 0x03;
        bool anyoneCanPay = ((int)type & 0x80) != 0;
        if ((int)type is not (0x00 or 0x01 or 0x02 or 0x03 or 0x81 or 0x82 or 0x83))
            throw new ArgumentException($"Invalid Taproot sighash type 0x{(int)type:x2}");
        if (!anyoneCanPay && spentOutputs.Count != Inputs.Count)
            throw new ArgumentException("Taproot signing needs every spent output");
        if (baseType == (int)SigHashType.Single && index >= Outputs.Count)
            throw new ArgumentException("SIGHASH_SINGLE without a matching output");
        if (!annex.IsEmpty && annex[0] != 0x50) throw new ArgumentException("Annex must start with 0x50");
        if (!leafHash.IsEmpty && leafHash.Length != 32) throw new ArgumentException("Leaf hash must be 32 bytes");

        using var msg = new MemoryStream();
        msg.WriteByte(0x00); // epoch
        msg.WriteByte((byte)type);
        WriteUInt32(msg, Version);
        WriteUInt32(msg, LockTime);

        Span<byte> v8 = stackalloc byte[8];
        if (!anyoneCanPay)
        {
            using var prevouts = new MemoryStream();
            using var amounts = new MemoryStream();
            using var scripts = new MemoryStream();
            using var sequences = new MemoryStream();
            for (int i = 0; i < Inputs.Count; i++)
            {
                prevouts.Write(Inputs[i].OutpointBytes());
                BinaryPrimitives.WriteInt64LittleEndian(v8, spentOutputs[i].ValueSats);
                amounts.Write(v8);
                WriteVarBytes(scripts, spentOutputs[i].ScriptPubKey);
                WriteUInt32(sequences, Inputs[i].Sequence);
            }
            msg.Write(CryptoNative.Sha256(prevouts.ToArray()));
            msg.Write(CryptoNative.Sha256(amounts.ToArray()));
            msg.Write(CryptoNative.Sha256(scripts.ToArray()));
            msg.Write(CryptoNative.Sha256(sequences.ToArray()));
        }
        if (baseType is not ((int)SigHashType.None or (int)SigHashType.Single))
        {
            using var outputs = new MemoryStream();
            foreach (var o in Outputs) outputs.Write(o.Serialize());
            msg.Write(CryptoNative.Sha256(outputs.ToArray()));
        }

        int extFlag = leafHash.IsEmpty ? 0 : 1;
        msg.WriteByte((byte)(extFlag * 2 + (annex.IsEmpty ? 0 : 1)));
        if (anyoneCanPay)
        {
            var utxo = spentOutputs[anyoneCanPay && spentOutputs.Count == 1 ? 0 : index];
            msg.Write(Inputs[index].OutpointBytes());
            BinaryPrimitives.WriteInt64LittleEndian(v8, utxo.ValueSats);
            msg.Write(v8);
            WriteVarBytes(msg, utxo.ScriptPubKey);
            WriteUInt32(msg, Inputs[index].Sequence);
        }
        else
        {
            WriteUInt32(msg, (uint)index);
        }
        if (!annex.IsEmpty)
        {
            using var a = new MemoryStream();
            WriteVarBytes(a, annex);
            msg.Write(CryptoNative.Sha256(a.ToArray()));
        }
        if (baseType == (int)SigHashType.Single)
            msg.Write(CryptoNative.Sha256(Outputs[index].Serialize()));
        if (extFlag == 1)
        {
            msg.Write(leafHash);
            msg.WriteByte(0x00); // key_version
            WriteUInt32(msg, codeSeparatorPosition);
        }
        return msg.ToArray();
    }

    /// <summary>BIP-341 signature hash: TaggedHash("TapSighash", 0x00 || sigMsg).</summary>
    public byte[] GetTaprootSigHash(int index, IReadOnlyList<BitcoinUtxo> spentOutputs, SigHashType type = SigHashType.Default,
        ReadOnlySpan<byte> leafHash = default, ReadOnlySpan<byte> annex = default)
    {
        return CryptoNative.TaggedHash("TapSighash", GetTaprootSigMessage(index, spentOutputs, type, leafHash, annex));
    }

    /// <summary>
    /// Schnorr signature for a Taproot key-path spend with an optional script tree. Appends the sighash byte unless
    /// it is SIGHASH_DEFAULT. <paramref name="auxRand"/> defaults to fresh randomness.
    /// </summary>
    public static byte[] SignTaprootKeyPath(ReadOnlySpan<byte> internalPrivateKey, ReadOnlySpan<byte> sigHash, SigHashType type = SigHashType.Default,
        ReadOnlySpan<byte> merkleRoot = default, ReadOnlySpan<byte> auxRand = default)
    {
        byte[] tweaked = CryptoNative.TaprootTweakSecretKey(internalPrivateKey, merkleRoot);
        try
        {
            byte[] aux = auxRand.IsEmpty ? System.Security.Cryptography.RandomNumberGenerator.GetBytes(32) : auxRand.ToArray();
            byte[] sig = CryptoNative.SchnorrSign(tweaked, sigHash, aux);
            return type == SigHashType.Default ? sig : [.. sig, (byte)type];
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(tweaked);
        }
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
                Inputs[index].ScriptSig = [];
                Inputs[index].Witness = [SignTaprootKeyPath(privateKey, hash)];
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
