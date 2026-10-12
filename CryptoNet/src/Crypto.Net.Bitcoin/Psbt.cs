using System.Buffers.Binary;
using Crypto.Net.Native;
using Crypto.Net.Wallet;

namespace Crypto.Net.Bitcoin;

/// <summary>One key-value record of a PSBT map. <see cref="Type"/> is the compact-size key type.</summary>
public sealed record PsbtRecord(byte[] Key, byte[] Value)
{
    public ulong Type => ReadKeyType(Key, out _);

    public ReadOnlySpan<byte> KeyData
    {
        get
        {
            ReadKeyType(Key, out int length);
            return Key.AsSpan(length);
        }
    }

    internal static ulong ReadKeyType(ReadOnlySpan<byte> key, out int length)
    {
        var r = new BitcoinTransaction.TxReader(key);
        ulong type = r.VarInt();
        length = key.Length - r.Remaining;
        return type;
    }

    internal static byte[] MakeKey(ulong type, ReadOnlySpan<byte> keyData)
    {
        using var ms = new MemoryStream();
        BitcoinTransaction.WriteVarInt(ms, type);
        ms.Write(keyData);
        return ms.ToArray();
    }
}

/// <summary>BIP-32 key origin: public key (33/65 bytes, or 32-byte x-only for Taproot), master fingerprint and path.</summary>
public sealed record PsbtKeyOrigin(byte[] PublicKey, uint Fingerprint, uint[] Path)
{
    /// <summary>Taproot leaf hashes the key signs for (PSBT_IN/OUT_TAP_BIP32_DERIVATION only).</summary>
    public IReadOnlyList<byte[]> LeafHashes { get; init; } = [];

    public string PathString => DerivationPath.Format(Path);

    public static PsbtKeyOrigin Create(byte[] publicKey, uint fingerprint, string path) => new(publicKey, fingerprint, DerivationPath.Parse(path));

    internal byte[] EncodeOrigin()
    {
        var value = new byte[4 + Path.Length * 4];
        BinaryPrimitives.WriteUInt32BigEndian(value, Fingerprint);
        for (int i = 0; i < Path.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(4 + i * 4), Path[i]);
        return value;
    }

    internal static (uint Fingerprint, uint[] Path) DecodeOrigin(ReadOnlySpan<byte> value)
    {
        if (value.Length < 4 || value.Length % 4 != 0) throw new FormatException("Invalid BIP-32 key origin");
        var path = new uint[(value.Length - 4) / 4];
        for (int i = 0; i < path.Length; i++) path[i] = BinaryPrimitives.ReadUInt32LittleEndian(value[(4 + i * 4)..]);
        return (BinaryPrimitives.ReadUInt32BigEndian(value), path);
    }
}

/// <summary>Which map of a PSBT a <see cref="PsbtMap"/> is; it decides the canonical record order.</summary>
public enum PsbtMapKind
{
    Global,
    Input,
    Output,
}

/// <summary>
/// An ordered PSBT key-value map. Parsed maps keep their record order, so unmodified PSBTs re-serialize byte for byte;
/// new records are inserted where Bitcoin Core would write them. Unknown and proprietary records are always preserved.
/// </summary>
public sealed class PsbtMap
{
    private readonly List<PsbtRecord> _records = [];

    public PsbtMap(PsbtMapKind kind) => Kind = kind;

    public PsbtMapKind Kind { get; }
    public IReadOnlyList<PsbtRecord> Records => _records;

    public byte[]? Get(ulong type, ReadOnlySpan<byte> keyData = default)
    {
        byte[] key = PsbtRecord.MakeKey(type, keyData);
        foreach (var r in _records)
            if (r.Key.AsSpan().SequenceEqual(key)) return r.Value;
        return null;
    }

    public bool Contains(ulong type) => _records.Any(r => r.Type == type);

    public IEnumerable<PsbtRecord> GetAll(ulong type) => _records.Where(r => r.Type == type);

    /// <summary>Adds or replaces the record with key <c>type || keyData</c>.</summary>
    public void Set(ulong type, ReadOnlySpan<byte> keyData, byte[] value) => Set(new PsbtRecord(PsbtRecord.MakeKey(type, keyData), value));

    public void Set(ulong type, byte[] value) => Set(type, default, value);

    public void Set(PsbtRecord record)
    {
        int existing = _records.FindIndex(r => r.Key.AsSpan().SequenceEqual(record.Key));
        if (existing >= 0)
        {
            _records[existing] = record;
            return;
        }
        int at = _records.FindIndex(r => Compare(r, record) > 0);
        if (at < 0) _records.Add(record);
        else _records.Insert(at, record);
    }

    public bool Remove(ulong type, ReadOnlySpan<byte> keyData = default)
    {
        byte[] key = PsbtRecord.MakeKey(type, keyData);
        return _records.RemoveAll(r => r.Key.AsSpan().SequenceEqual(key)) > 0;
    }

    public int RemoveAll(ulong type) => _records.RemoveAll(r => r.Type == type);

    public int RemoveWhere(Func<PsbtRecord, bool> predicate) => _records.RemoveAll(r => predicate(r));

    internal void AddParsed(PsbtRecord record) => _records.Add(record);

    internal PsbtMap Clone()
    {
        var copy = new PsbtMap(Kind);
        copy._records.AddRange(_records);
        return copy;
    }

    internal void WriteTo(Stream s)
    {
        foreach (var r in _records)
        {
            BitcoinTransaction.WriteVarBytes(s, r.Key);
            BitcoinTransaction.WriteVarBytes(s, r.Value);
        }
        s.WriteByte(0x00);
    }

    // Order used by Bitcoin Core: known fields in its serialization order, then proprietary, then unknown by key.
    private int Rank(ulong type) => Kind switch
    {
        PsbtMapKind.Global => type switch
        {
            <= 0x06 => (int)type,
            0xFB => 10,
            0xFC => 20,
            _ => 30,
        },
        PsbtMapKind.Input => type switch
        {
            0x00 or 0x01 => (int)type,
            >= 0x0E and <= 0x12 => (int)type - 0x0E + 2,
            >= 0x02 and <= 0x06 => (int)type + 10,
            >= 0x0A and <= 0x0D => (int)type + 10,
            >= 0x13 and <= 0x1C => (int)type + 10,
            0x07 => 50,
            0x08 => 51,
            0x09 => 52,
            0xFC => 60,
            _ => 70,
        },
        _ => type switch
        {
            <= 0x08 => (int)type,
            0xFC => 20,
            _ => 30,
        },
    };

    private int Compare(PsbtRecord a, PsbtRecord b)
    {
        ulong ta = a.Type, tb = b.Type;
        int c = Rank(ta).CompareTo(Rank(tb));
        if (c != 0) return c;
        if (Kind == PsbtMapKind.Input && ta == Psbt.InPartialSig && tb == Psbt.InPartialSig)
            return CryptoNative.Hash160(a.KeyData).AsSpan().SequenceCompareTo(CryptoNative.Hash160(b.KeyData));
        return a.Key.AsSpan().SequenceCompareTo(b.Key);
    }
}

/// <summary>Typed view of a PSBT input map.</summary>
public sealed class PsbtInput
{
    internal PsbtInput(PsbtMap map) => Map = map;

    /// <summary>The raw key-value map, including unknown and proprietary records.</summary>
    public PsbtMap Map { get; }

    public BitcoinTransaction? NonWitnessUtxo
    {
        get => Map.Get(Psbt.InNonWitnessUtxo) is { } v ? BitcoinTransaction.Parse(v) : null;
        set => SetOrRemove(Psbt.InNonWitnessUtxo, value?.Serialize());
    }

    public BitcoinTxOutput? WitnessUtxo
    {
        get => Map.Get(Psbt.InWitnessUtxo) is { } v ? Psbt.DecodeTxOut(v) : null;
        set => SetOrRemove(Psbt.InWitnessUtxo, value?.Serialize());
    }

    /// <summary>ECDSA partial signatures keyed by public key (signature includes the sighash byte).</summary>
    public IReadOnlyList<(byte[] PublicKey, byte[] Signature)> PartialSignatures =>
        [.. Map.GetAll(Psbt.InPartialSig).Select(r => (r.KeyData.ToArray(), r.Value))];

    public void AddPartialSignature(byte[] publicKey, byte[] signature) => Map.Set(Psbt.InPartialSig, publicKey, signature);

    public SigHashType? SighashType
    {
        get => Map.Get(Psbt.InSighashType) is { } v ? (SigHashType)BinaryPrimitives.ReadUInt32LittleEndian(v) : null;
        set => SetOrRemove(Psbt.InSighashType, value is { } t ? Psbt.UInt32Le((uint)t) : null);
    }

    public byte[]? RedeemScript
    {
        get => Map.Get(Psbt.InRedeemScript);
        set => SetOrRemove(Psbt.InRedeemScript, value);
    }

    public byte[]? WitnessScript
    {
        get => Map.Get(Psbt.InWitnessScript);
        set => SetOrRemove(Psbt.InWitnessScript, value);
    }

    public IReadOnlyList<PsbtKeyOrigin> Bip32Derivations => Psbt.ReadOrigins(Map, Psbt.InBip32Derivation);

    public void AddBip32Derivation(PsbtKeyOrigin origin) => Map.Set(Psbt.InBip32Derivation, origin.PublicKey, origin.EncodeOrigin());

    public byte[]? FinalScriptSig
    {
        get => Map.Get(Psbt.InFinalScriptSig);
        set => SetOrRemove(Psbt.InFinalScriptSig, value);
    }

    public IReadOnlyList<byte[]>? FinalScriptWitness
    {
        get => Map.Get(Psbt.InFinalScriptWitness) is { } v ? Psbt.DecodeWitness(v) : null;
        set => SetOrRemove(Psbt.InFinalScriptWitness, value is null ? null : Psbt.EncodeWitness(value));
    }

    public bool IsFinalized => Map.Contains(Psbt.InFinalScriptSig) || Map.Contains(Psbt.InFinalScriptWitness);

    /// <summary>BIP-371 Taproot key-path signature.</summary>
    public byte[]? TapKeySignature
    {
        get => Map.Get(Psbt.InTapKeySig);
        set => SetOrRemove(Psbt.InTapKeySig, value);
    }

    /// <summary>BIP-371 Taproot script-path signatures keyed by (x-only key, leaf hash).</summary>
    public IReadOnlyList<(byte[] XOnlyKey, byte[] LeafHash, byte[] Signature)> TapScriptSignatures =>
        [.. Map.GetAll(Psbt.InTapScriptSig).Select(r => (r.KeyData[..32].ToArray(), r.KeyData[32..].ToArray(), r.Value))];

    /// <summary>BIP-371 leaf scripts: control block, script and leaf version.</summary>
    public IReadOnlyList<(byte[] ControlBlock, byte[] Script, byte LeafVersion)> TapLeafScripts =>
        [.. Map.GetAll(Psbt.InTapLeafScript).Select(r => (r.KeyData.ToArray(), r.Value[..^1], r.Value[^1]))];

    public void AddTapLeafScript(TaprootLeafSpend leaf) => Map.Set(Psbt.InTapLeafScript, leaf.ControlBlock, [.. leaf.Script, leaf.LeafVersion]);

    public IReadOnlyList<PsbtKeyOrigin> TapBip32Derivations => Psbt.ReadTapOrigins(Map, Psbt.InTapBip32Derivation);

    public void AddTapBip32Derivation(PsbtKeyOrigin origin) => Map.Set(Psbt.InTapBip32Derivation, origin.PublicKey, Psbt.EncodeTapOrigin(origin));

    public byte[]? TapInternalKey
    {
        get => Map.Get(Psbt.InTapInternalKey);
        set => SetOrRemove(Psbt.InTapInternalKey, value);
    }

    public byte[]? TapMerkleRoot
    {
        get => Map.Get(Psbt.InTapMerkleRoot);
        set => SetOrRemove(Psbt.InTapMerkleRoot, value);
    }

    /// <summary>PSBTv2 previous txid in display (big-endian) hex.</summary>
    public string? PreviousTxId => Map.Get(Psbt.InPreviousTxId) is { } v ? Convert.ToHexStringLower([.. v.Reverse()]) : null;

    public uint? OutputIndex => Map.Get(Psbt.InOutputIndex) is { } v ? BinaryPrimitives.ReadUInt32LittleEndian(v) : null;

    public uint? Sequence => Map.Get(Psbt.InSequence) is { } v ? BinaryPrimitives.ReadUInt32LittleEndian(v) : null;

    public uint? RequiredTimeLocktime => Map.Get(Psbt.InRequiredTimeLocktime) is { } v ? BinaryPrimitives.ReadUInt32LittleEndian(v) : null;

    public uint? RequiredHeightLocktime => Map.Get(Psbt.InRequiredHeightLocktime) is { } v ? BinaryPrimitives.ReadUInt32LittleEndian(v) : null;

    private void SetOrRemove(ulong type, byte[]? value)
    {
        if (value is null) Map.Remove(type);
        else Map.Set(type, value);
    }
}

/// <summary>Typed view of a PSBT output map.</summary>
public sealed class PsbtOutput
{
    internal PsbtOutput(PsbtMap map) => Map = map;

    public PsbtMap Map { get; }

    public byte[]? RedeemScript
    {
        get => Map.Get(Psbt.OutRedeemScript);
        set => SetOrRemove(Psbt.OutRedeemScript, value);
    }

    public byte[]? WitnessScript
    {
        get => Map.Get(Psbt.OutWitnessScript);
        set => SetOrRemove(Psbt.OutWitnessScript, value);
    }

    public IReadOnlyList<PsbtKeyOrigin> Bip32Derivations => Psbt.ReadOrigins(Map, Psbt.OutBip32Derivation);

    public void AddBip32Derivation(PsbtKeyOrigin origin) => Map.Set(Psbt.OutBip32Derivation, origin.PublicKey, origin.EncodeOrigin());

    public byte[]? TapInternalKey
    {
        get => Map.Get(Psbt.OutTapInternalKey);
        set => SetOrRemove(Psbt.OutTapInternalKey, value);
    }

    public IReadOnlyList<PsbtKeyOrigin> TapBip32Derivations => Psbt.ReadTapOrigins(Map, Psbt.OutTapBip32Derivation);

    public void AddTapBip32Derivation(PsbtKeyOrigin origin) => Map.Set(Psbt.OutTapBip32Derivation, origin.PublicKey, Psbt.EncodeTapOrigin(origin));

    /// <summary>PSBTv2 amount.</summary>
    public long? Amount => Map.Get(Psbt.OutAmount) is { } v ? BinaryPrimitives.ReadInt64LittleEndian(v) : null;

    /// <summary>PSBTv2 output script.</summary>
    public byte[]? Script => Map.Get(Psbt.OutScript);

    private void SetOrRemove(ulong type, byte[]? value)
    {
        if (value is null) Map.Remove(type);
        else Map.Set(type, value);
    }
}

/// <summary>
/// Partially Signed Bitcoin Transaction (BIP-174 v0 and BIP-370 v2, with BIP-371 Taproot fields).
/// Implements every role: creator, updater, signer (P2PKH, P2WPKH, P2SH-P2WPKH, bare/P2SH/P2WSH/P2SH-P2WSH multisig,
/// Taproot key path and script path), combiner, finalizer and extractor.
/// </summary>
public sealed class Psbt
{
    #region Field types

    public const ulong GlobalUnsignedTx = 0x00, GlobalXpub = 0x01, GlobalTxVersion = 0x02, GlobalFallbackLocktime = 0x03,
        GlobalInputCount = 0x04, GlobalOutputCount = 0x05, GlobalTxModifiable = 0x06, GlobalVersion = 0xFB, Proprietary = 0xFC;

    public const ulong InNonWitnessUtxo = 0x00, InWitnessUtxo = 0x01, InPartialSig = 0x02, InSighashType = 0x03,
        InRedeemScript = 0x04, InWitnessScript = 0x05, InBip32Derivation = 0x06, InFinalScriptSig = 0x07,
        InFinalScriptWitness = 0x08, InPorCommitment = 0x09, InRipemd160 = 0x0A, InSha256 = 0x0B, InHash160 = 0x0C,
        InHash256 = 0x0D, InPreviousTxId = 0x0E, InOutputIndex = 0x0F, InSequence = 0x10, InRequiredTimeLocktime = 0x11,
        InRequiredHeightLocktime = 0x12, InTapKeySig = 0x13, InTapScriptSig = 0x14, InTapLeafScript = 0x15,
        InTapBip32Derivation = 0x16, InTapInternalKey = 0x17, InTapMerkleRoot = 0x18;

    public const ulong OutRedeemScript = 0x00, OutWitnessScript = 0x01, OutBip32Derivation = 0x02, OutAmount = 0x03,
        OutScript = 0x04, OutTapInternalKey = 0x05, OutTapTree = 0x06, OutTapBip32Derivation = 0x07;

    private static readonly byte[] Magic = [0x70, 0x73, 0x62, 0x74, 0xFF];

    #endregion

    private readonly List<PsbtInput> _inputs = [];
    private readonly List<PsbtOutput> _outputs = [];

    private Psbt(uint version, PsbtMap global)
    {
        PsbtVersion = version;
        Global = global;
    }

    /// <summary>PSBT format version: 0 (BIP-174) or 2 (BIP-370).</summary>
    public uint PsbtVersion { get; private set; }

    public PsbtMap Global { get; private set; }
    public IReadOnlyList<PsbtInput> Inputs => _inputs;
    public IReadOnlyList<PsbtOutput> Outputs => _outputs;

    /// <summary>Global extended public keys (PSBT_GLOBAL_XPUB): Base58 xpub with its origin.</summary>
    public IReadOnlyList<(byte[] ExtendedKey, uint Fingerprint, uint[] Path)> GlobalXpubs =>
        [.. Global.GetAll(GlobalXpub).Select(r =>
        {
            var (fp, path) = PsbtKeyOrigin.DecodeOrigin(r.Value);
            return (r.KeyData.ToArray(), fp, path);
        })];

    #region Creator

    /// <summary>Creates a v0 PSBT from an unsigned transaction (scriptSigs and witnesses must be empty).</summary>
    public static Psbt Create(BitcoinTransaction unsignedTx)
    {
        if (unsignedTx.Inputs.Any(i => i.ScriptSig.Length > 0 || i.Witness.Count > 0))
            throw new ArgumentException("The unsigned transaction must have empty scriptSigs and witnesses", nameof(unsignedTx));
        var global = new PsbtMap(PsbtMapKind.Global);
        global.Set(GlobalUnsignedTx, unsignedTx.Serialize(includeWitness: false));
        var psbt = new Psbt(0, global);
        foreach (var _ in unsignedTx.Inputs) psbt._inputs.Add(new PsbtInput(new PsbtMap(PsbtMapKind.Input)));
        foreach (var _ in unsignedTx.Outputs) psbt._outputs.Add(new PsbtOutput(new PsbtMap(PsbtMapKind.Output)));
        return psbt;
    }

    /// <summary>Creates an empty v2 PSBT; add inputs and outputs with <see cref="AddInput"/> and <see cref="AddOutput(byte[], long)"/>.</summary>
    public static Psbt CreateV2(uint txVersion = 2, uint? fallbackLocktime = null, bool inputsModifiable = true, bool outputsModifiable = true)
    {
        var global = new PsbtMap(PsbtMapKind.Global);
        global.Set(GlobalTxVersion, UInt32Le(txVersion));
        if (fallbackLocktime is { } lt) global.Set(GlobalFallbackLocktime, UInt32Le(lt));
        global.Set(GlobalInputCount, CompactSize(0));
        global.Set(GlobalOutputCount, CompactSize(0));
        byte flags = (byte)((inputsModifiable ? 1 : 0) | (outputsModifiable ? 2 : 0));
        if (flags != 0) global.Set(GlobalTxModifiable, [flags]);
        global.Set(GlobalVersion, UInt32Le(2));
        return new Psbt(2, global);
    }

    /// <summary>Adds an input to a v2 PSBT.</summary>
    public PsbtInput AddInput(string txId, uint vout, uint? sequence = null, uint? requiredTimeLocktime = null, uint? requiredHeightLocktime = null)
    {
        RequireV2();
        if (Global.Get(GlobalTxModifiable) is { } m && (m[0] & 1) == 0) throw new InvalidOperationException("Inputs are not modifiable");
        var map = new PsbtMap(PsbtMapKind.Input);
        map.Set(InPreviousTxId, [.. Convert.FromHexString(txId).Reverse()]);
        map.Set(InOutputIndex, UInt32Le(vout));
        if (sequence is { } seq) map.Set(InSequence, UInt32Le(seq));
        if (requiredTimeLocktime is { } t)
        {
            if (t < 500_000_000) throw new ArgumentOutOfRangeException(nameof(requiredTimeLocktime));
            map.Set(InRequiredTimeLocktime, UInt32Le(t));
        }
        if (requiredHeightLocktime is { } h)
        {
            if (h is 0 or >= 500_000_000) throw new ArgumentOutOfRangeException(nameof(requiredHeightLocktime));
            map.Set(InRequiredHeightLocktime, UInt32Le(h));
        }
        var input = new PsbtInput(map);
        _inputs.Add(input);
        Global.Set(GlobalInputCount, CompactSize((ulong)_inputs.Count));
        return input;
    }

    /// <summary>Adds an output to a v2 PSBT.</summary>
    public PsbtOutput AddOutput(byte[] scriptPubKey, long valueSats)
    {
        RequireV2();
        if (Global.Get(GlobalTxModifiable) is { } m && (m[0] & 2) == 0) throw new InvalidOperationException("Outputs are not modifiable");
        var map = new PsbtMap(PsbtMapKind.Output);
        map.Set(OutAmount, Int64Le(valueSats));
        map.Set(OutScript, scriptPubKey);
        var output = new PsbtOutput(map);
        _outputs.Add(output);
        Global.Set(GlobalOutputCount, CompactSize((ulong)_outputs.Count));
        return output;
    }

    public PsbtOutput AddOutput(string address, long valueSats, BitcoinNetwork? network = null) =>
        AddOutput(BitcoinAddress.GetScriptPubKey(address, network), valueSats);

    #endregion

    #region Parse / serialize

    public static Psbt Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Magic.Length || !data[..Magic.Length].SequenceEqual(Magic)) throw new FormatException("Missing PSBT magic bytes");
        var r = new BitcoinTransaction.TxReader(data[Magic.Length..]);

        var global = ReadMap(ref r, PsbtMapKind.Global);
        uint version = global.Get(GlobalVersion) is { } v ? BinaryPrimitives.ReadUInt32LittleEndian(v) : 0;
        if (version is not (0 or 2)) throw new FormatException($"Unsupported PSBT version {version}");
        var psbt = new Psbt(version, global);

        int inputCount, outputCount;
        BitcoinTransaction? tx = null;
        if (version == 0)
        {
            foreach (var t in new[] { GlobalTxVersion, GlobalFallbackLocktime, GlobalInputCount, GlobalOutputCount, GlobalTxModifiable })
                if (global.Contains(t)) throw new FormatException("PSBTv0 must not contain PSBTv2 global fields");
            tx = psbt.GetGlobalTransaction() ?? throw new FormatException("PSBTv0 requires PSBT_GLOBAL_UNSIGNED_TX");
            inputCount = tx.Inputs.Count;
            outputCount = tx.Outputs.Count;
        }
        else
        {
            if (global.Contains(GlobalUnsignedTx)) throw new FormatException("PSBTv2 must not contain PSBT_GLOBAL_UNSIGNED_TX");
            if (!global.Contains(GlobalTxVersion)) throw new FormatException("PSBTv2 requires PSBT_GLOBAL_TX_VERSION");
            inputCount = ReadCount(global.Get(GlobalInputCount) ?? throw new FormatException("PSBTv2 requires PSBT_GLOBAL_INPUT_COUNT"));
            outputCount = ReadCount(global.Get(GlobalOutputCount) ?? throw new FormatException("PSBTv2 requires PSBT_GLOBAL_OUTPUT_COUNT"));
        }

        for (int i = 0; i < inputCount; i++)
        {
            var map = ReadMap(ref r, PsbtMapKind.Input);
            ValidateInput(map, version, tx, i);
            psbt._inputs.Add(new PsbtInput(map));
        }
        for (int i = 0; i < outputCount; i++)
        {
            var map = ReadMap(ref r, PsbtMapKind.Output);
            ValidateOutput(map, version);
            psbt._outputs.Add(new PsbtOutput(map));
        }
        if (r.Remaining != 0) throw new FormatException("Trailing bytes after PSBT");
        return psbt;
    }

    /// <summary>Parses a Base64 (the usual interchange form) or hex PSBT.</summary>
    public static Psbt Parse(string text)
    {
        text = text.Trim();
        bool hex = text.StartsWith("70736274ff", StringComparison.OrdinalIgnoreCase) && text.Length % 2 == 0 && text.All(Uri.IsHexDigit);
        return Parse(hex ? Convert.FromHexString(text) : Convert.FromBase64String(text));
    }

    public static bool TryParse(string? text, out Psbt? psbt)
    {
        psbt = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            psbt = Parse(text);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        ms.Write(Magic);
        Global.WriteTo(ms);
        foreach (var i in _inputs) i.Map.WriteTo(ms);
        foreach (var o in _outputs) o.Map.WriteTo(ms);
        return ms.ToArray();
    }

    public string ToBase64() => Convert.ToBase64String(Serialize());
    public string ToHex() => Convert.ToHexStringLower(Serialize());
    public override string ToString() => ToBase64();

    public Psbt Clone()
    {
        var copy = new Psbt(PsbtVersion, Global.Clone());
        copy._inputs.AddRange(_inputs.Select(i => new PsbtInput(i.Map.Clone())));
        copy._outputs.AddRange(_outputs.Select(o => new PsbtOutput(o.Map.Clone())));
        return copy;
    }

    private static PsbtMap ReadMap(ref BitcoinTransaction.TxReader r, PsbtMapKind kind)
    {
        var map = new PsbtMap(kind);
        var seen = new HashSet<string>();
        while (true)
        {
            ulong keyLength = r.VarInt();
            if (keyLength == 0) return map;
            if (keyLength > (ulong)r.Remaining) throw new FormatException("PSBT key exceeds the data");
            byte[] key = r.Bytes((int)keyLength).ToArray();
            byte[] value = r.VarBytes().ToArray();
            if (!seen.Add(Convert.ToHexString(key))) throw new FormatException("Duplicate key in PSBT map");
            var record = new PsbtRecord(key, value);
            if (kind == PsbtMapKind.Global) ValidateGlobal(record);
            map.AddParsed(record);
        }
    }

    private static void ValidateGlobal(PsbtRecord r)
    {
        switch (r.Type)
        {
            case GlobalUnsignedTx:
                NoKeyData(r);
                var tx = BitcoinTransaction.Parse(r.Value, allowWitness: false);
                if (tx.Inputs.Any(i => i.ScriptSig.Length > 0)) throw new FormatException("Unsigned transaction has a non-empty scriptSig");
                break;
            case GlobalXpub:
                if (r.KeyData.Length != 78) throw new FormatException("PSBT_GLOBAL_XPUB key must hold a 78-byte extended key");
                PsbtKeyOrigin.DecodeOrigin(r.Value);
                break;
            case GlobalTxVersion or GlobalFallbackLocktime or GlobalVersion:
                NoKeyData(r);
                Length(r, 4);
                break;
            case GlobalInputCount or GlobalOutputCount:
                NoKeyData(r);
                ReadCount(r.Value);
                break;
            case GlobalTxModifiable:
                NoKeyData(r);
                Length(r, 1);
                break;
        }
    }

    private static void ValidateInput(PsbtMap map, uint version, BitcoinTransaction? tx, int index)
    {
        foreach (var r in map.Records)
        {
            switch (r.Type)
            {
                case InNonWitnessUtxo:
                    NoKeyData(r);
                    var prev = BitcoinTransaction.Parse(r.Value);
                    if (tx is not null && !string.Equals(prev.TxId, tx.Inputs[index].TxId, StringComparison.OrdinalIgnoreCase))
                        throw new FormatException($"Input {index}: non-witness UTXO does not match the previous txid");
                    break;
                case InWitnessUtxo:
                    NoKeyData(r);
                    DecodeTxOut(r.Value);
                    break;
                case InPartialSig or InBip32Derivation:
                    PublicKey(r.KeyData);
                    if (r.Type == InBip32Derivation) PsbtKeyOrigin.DecodeOrigin(r.Value);
                    else if (r.Value.Length == 0) throw new FormatException("Empty partial signature");
                    break;
                case InSighashType:
                    NoKeyData(r);
                    Length(r, 4);
                    break;
                case InRedeemScript or InWitnessScript or InFinalScriptSig:
                    NoKeyData(r);
                    break;
                case InFinalScriptWitness:
                    NoKeyData(r);
                    DecodeWitness(r.Value);
                    break;
                case InRipemd160 or InHash160:
                    if (r.KeyData.Length != 20) throw new FormatException("Invalid hash preimage key");
                    break;
                case InSha256 or InHash256:
                    if (r.KeyData.Length != 32) throw new FormatException("Invalid hash preimage key");
                    break;
                case InPreviousTxId:
                    V2Only(version, r);
                    NoKeyData(r);
                    Length(r, 32);
                    break;
                case InOutputIndex or InSequence:
                    V2Only(version, r);
                    NoKeyData(r);
                    Length(r, 4);
                    break;
                case InRequiredTimeLocktime:
                    V2Only(version, r);
                    NoKeyData(r);
                    Length(r, 4);
                    if (BinaryPrimitives.ReadUInt32LittleEndian(r.Value) < 500_000_000) throw new FormatException("Required time locktime must be >= 500000000");
                    break;
                case InRequiredHeightLocktime:
                    V2Only(version, r);
                    NoKeyData(r);
                    Length(r, 4);
                    uint h = BinaryPrimitives.ReadUInt32LittleEndian(r.Value);
                    if (h is 0 or >= 500_000_000) throw new FormatException("Required height locktime must be in 1..499999999");
                    break;
                case InTapKeySig:
                    NoKeyData(r);
                    if (r.Value.Length is not (64 or 65)) throw new FormatException("Invalid Taproot key signature");
                    break;
                case InTapScriptSig:
                    if (r.KeyData.Length != 64 || r.Value.Length is not (64 or 65)) throw new FormatException("Invalid Taproot script signature");
                    break;
                case InTapLeafScript:
                    if (r.KeyData.Length < 33 || (r.KeyData.Length - 33) % 32 != 0 || r.Value.Length < 1) throw new FormatException("Invalid Taproot leaf script");
                    break;
                case InTapBip32Derivation:
                    if (r.KeyData.Length != 32) throw new FormatException("Invalid Taproot BIP-32 key");
                    DecodeTapOrigin(r.Value);
                    break;
                case InTapInternalKey or InTapMerkleRoot:
                    NoKeyData(r);
                    Length(r, 32);
                    break;
            }
        }
        if (version == 2 && (!map.Contains(InPreviousTxId) || !map.Contains(InOutputIndex)))
            throw new FormatException($"PSBTv2 input {index} requires PSBT_IN_PREVIOUS_TXID and PSBT_IN_OUTPUT_INDEX");
    }

    private static void ValidateOutput(PsbtMap map, uint version)
    {
        foreach (var r in map.Records)
        {
            switch (r.Type)
            {
                case OutRedeemScript or OutWitnessScript:
                    NoKeyData(r);
                    break;
                case OutBip32Derivation:
                    PublicKey(r.KeyData);
                    PsbtKeyOrigin.DecodeOrigin(r.Value);
                    break;
                case OutAmount:
                    V2Only(version, r);
                    NoKeyData(r);
                    Length(r, 8);
                    break;
                case OutScript:
                    V2Only(version, r);
                    NoKeyData(r);
                    break;
                case OutTapInternalKey:
                    NoKeyData(r);
                    Length(r, 32);
                    break;
                case OutTapBip32Derivation:
                    if (r.KeyData.Length != 32) throw new FormatException("Invalid Taproot BIP-32 key");
                    DecodeTapOrigin(r.Value);
                    break;
            }
        }
        if (version == 2 && (!map.Contains(OutAmount) || !map.Contains(OutScript)))
            throw new FormatException("PSBTv2 outputs require PSBT_OUT_AMOUNT and PSBT_OUT_SCRIPT");
    }

    private static void NoKeyData(PsbtRecord r)
    {
        if (r.KeyData.Length != 0) throw new FormatException($"PSBT key type 0x{r.Type:x2} must not carry key data");
    }

    private static void Length(PsbtRecord r, int length)
    {
        if (r.Value.Length != length) throw new FormatException($"PSBT key type 0x{r.Type:x2} must have a {length}-byte value");
    }

    private static void V2Only(uint version, PsbtRecord r)
    {
        if (version < 2) throw new FormatException($"PSBT key type 0x{r.Type:x2} is only allowed in PSBTv2");
    }

    private static void PublicKey(ReadOnlySpan<byte> key)
    {
        bool ok = key.Length switch
        {
            33 => key[0] is 0x02 or 0x03,
            65 => key[0] == 0x04,
            _ => false,
        };
        if (!ok) throw new FormatException("Invalid public key in PSBT key");
    }

    private static int ReadCount(ReadOnlySpan<byte> value)
    {
        var r = new BitcoinTransaction.TxReader(value);
        ulong n = r.VarInt();
        if (r.Remaining != 0 || n > 100_000) throw new FormatException("Invalid PSBT count");
        return (int)n;
    }

    #endregion

    #region Transaction view

    private BitcoinTransaction? GetGlobalTransaction() =>
        Global.Get(GlobalUnsignedTx) is { } raw ? BitcoinTransaction.Parse(raw, allowWitness: false) : null;

    /// <summary>
    /// The unsigned transaction: the global transaction for v0, or the one assembled from per-input/output fields for v2
    /// (with the BIP-370 locktime determination).
    /// </summary>
    public BitcoinTransaction GetUnsignedTransaction()
    {
        if (PsbtVersion == 0) return GetGlobalTransaction()!;
        var tx = new BitcoinTransaction
        {
            Version = BinaryPrimitives.ReadUInt32LittleEndian(Global.Get(GlobalTxVersion)!),
            LockTime = ComputeLockTime(),
        };
        foreach (var i in _inputs)
            tx.Inputs.Add(new BitcoinTxInput { TxId = i.PreviousTxId!, Vout = i.OutputIndex!.Value, Sequence = i.Sequence ?? 0xFFFFFFFF });
        foreach (var o in _outputs)
            tx.Outputs.Add(new BitcoinTxOutput { ValueSats = o.Amount!.Value, ScriptPubKey = o.Script! });
        return tx;
    }

    private uint ComputeLockTime()
    {
        var withLock = _inputs.Where(i => i.RequiredTimeLocktime is not null || i.RequiredHeightLocktime is not null).ToList();
        if (withLock.Count == 0)
            return Global.Get(GlobalFallbackLocktime) is { } f ? BinaryPrimitives.ReadUInt32LittleEndian(f) : 0;
        if (withLock.All(i => i.RequiredHeightLocktime is not null))
            return withLock.Max(i => i.RequiredHeightLocktime!.Value);
        if (withLock.All(i => i.RequiredTimeLocktime is not null))
            return withLock.Max(i => i.RequiredTimeLocktime!.Value);
        throw new InvalidOperationException("Inputs require incompatible locktime types");
    }

    /// <summary>The output each input spends, when its UTXO is known.</summary>
    public BitcoinTxOutput? GetSpentOutput(int index)
    {
        var input = _inputs[index];
        if (input.WitnessUtxo is { } w) return w;
        if (input.NonWitnessUtxo is { } prev)
        {
            uint vout = GetUnsignedTransaction().Inputs[index].Vout;
            return vout < prev.Outputs.Count ? prev.Outputs[(int)vout] : null;
        }
        return null;
    }

    /// <summary>Fee in satoshis, or null when any input amount is unknown.</summary>
    public long? Fee
    {
        get
        {
            long total = 0;
            for (int i = 0; i < _inputs.Count; i++)
            {
                if (GetSpentOutput(i) is not { } o) return null;
                total += o.ValueSats;
            }
            return total - GetUnsignedTransaction().Outputs.Sum(o => o.ValueSats);
        }
    }

    /// <summary>Converts between PSBTv0 and PSBTv2, keeping every per-input and per-output record.</summary>
    public Psbt ConvertTo(uint version)
    {
        if (version is not (0 or 2)) throw new ArgumentOutOfRangeException(nameof(version));
        if (version == PsbtVersion) return Clone();
        var tx = GetUnsignedTransaction();
        var copy = Clone();
        var g = copy.Global;
        if (version == 2)
        {
            g.Remove(GlobalUnsignedTx);
            g.Set(GlobalTxVersion, UInt32Le(tx.Version));
            g.Set(GlobalFallbackLocktime, UInt32Le(tx.LockTime));
            g.Set(GlobalInputCount, CompactSize((ulong)tx.Inputs.Count));
            g.Set(GlobalOutputCount, CompactSize((ulong)tx.Outputs.Count));
            g.Set(GlobalVersion, UInt32Le(2));
            for (int i = 0; i < tx.Inputs.Count; i++)
            {
                var m = copy._inputs[i].Map;
                m.Set(InPreviousTxId, [.. Convert.FromHexString(tx.Inputs[i].TxId).Reverse()]);
                m.Set(InOutputIndex, UInt32Le(tx.Inputs[i].Vout));
                m.Set(InSequence, UInt32Le(tx.Inputs[i].Sequence));
            }
            for (int i = 0; i < tx.Outputs.Count; i++)
            {
                copy._outputs[i].Map.Set(OutAmount, Int64Le(tx.Outputs[i].ValueSats));
                copy._outputs[i].Map.Set(OutScript, tx.Outputs[i].ScriptPubKey);
            }
        }
        else
        {
            foreach (var t in new[] { GlobalTxVersion, GlobalFallbackLocktime, GlobalInputCount, GlobalOutputCount, GlobalTxModifiable, GlobalVersion })
                g.RemoveAll(t);
            g.Set(GlobalUnsignedTx, tx.Serialize(includeWitness: false));
            foreach (var i in copy._inputs)
                i.Map.RemoveWhere(r => r.Type is >= InPreviousTxId and <= InRequiredHeightLocktime);
            foreach (var o in copy._outputs)
                o.Map.RemoveWhere(r => r.Type is OutAmount or OutScript);
        }
        copy.PsbtVersion = version;
        return copy;
    }

    #endregion

    #region Updater

    /// <summary>
    /// Updater: attaches whatever applies from the given material to each input and output — previous transactions
    /// (full transaction for legacy inputs, witness UTXO for SegWit inputs), redeem and witness scripts matched by hash,
    /// and BIP-32 key origins for keys that appear in the scripts.
    /// </summary>
    public Psbt Update(IEnumerable<byte[]>? redeemScripts = null, IEnumerable<byte[]>? witnessScripts = null,
        IEnumerable<BitcoinTransaction>? previousTransactions = null, IEnumerable<PsbtKeyOrigin>? keyOrigins = null,
        bool includeFullPreviousTxForSegwit = false)
    {
        var redeem = redeemScripts?.ToList() ?? [];
        var witness = witnessScripts?.ToList() ?? [];
        var prevs = previousTransactions?.ToList() ?? [];
        var origins = keyOrigins?.ToList() ?? [];
        var tx = GetUnsignedTransaction();

        for (int i = 0; i < _inputs.Count; i++)
        {
            var input = _inputs[i];
            var prev = prevs.FirstOrDefault(p => string.Equals(p.TxId, tx.Inputs[i].TxId, StringComparison.OrdinalIgnoreCase));
            var spent = prev is not null && tx.Inputs[i].Vout < prev.Outputs.Count ? prev.Outputs[(int)tx.Inputs[i].Vout] : GetSpentOutput(i);
            if (spent is null) continue;

            var (rs, ws) = MatchScripts(spent.ScriptPubKey, redeem, witness);
            rs ??= input.RedeemScript;
            ws ??= input.WitnessScript;
            if (rs is not null) input.RedeemScript = rs;
            if (ws is not null) input.WitnessScript = ws;

            if (prev is not null)
            {
                bool segwit = IsWitnessProgram(rs ?? spent.ScriptPubKey);
                if (segwit) input.WitnessUtxo = spent;
                if (!segwit || includeFullPreviousTxForSegwit) input.NonWitnessUtxo = prev;
            }

            foreach (var o in origins.Where(o => KeyInvolved(o.PublicKey, spent.ScriptPubKey, rs, ws)))
                input.AddBip32Derivation(o);
        }

        for (int i = 0; i < _outputs.Count; i++)
        {
            var output = _outputs[i];
            byte[] spk = tx.Outputs[i].ScriptPubKey;
            var (rs, ws) = MatchScripts(spk, redeem, witness);
            if (rs is not null) output.RedeemScript = rs;
            if (ws is not null) output.WitnessScript = ws;
            foreach (var o in origins.Where(o => KeyInvolved(o.PublicKey, spk, rs ?? output.RedeemScript, ws ?? output.WitnessScript)))
                output.AddBip32Derivation(o);
        }
        return this;
    }

    /// <summary>Sets the sighash type of every input (or of the given inputs).</summary>
    public Psbt SetSighashType(SigHashType type, params int[] inputs)
    {
        foreach (int i in inputs.Length == 0 ? Enumerable.Range(0, _inputs.Count) : inputs) _inputs[i].SighashType = type;
        return this;
    }

    private static (byte[]? Redeem, byte[]? Witness) MatchScripts(byte[] spk, List<byte[]> redeem, List<byte[]> witness)
    {
        byte[]? rs = null, ws = null;
        byte[] inner = spk;
        if (IsP2SH(spk))
        {
            rs = redeem.FirstOrDefault(s => CryptoNative.Hash160(s).AsSpan().SequenceEqual(spk.AsSpan(2, 20)));
            if (rs is not null) inner = rs;
        }
        if (IsP2WSH(inner))
            ws = witness.FirstOrDefault(s => CryptoNative.Sha256(s).AsSpan().SequenceEqual(inner.AsSpan(2, 32)));
        return (rs, ws);
    }

    private static bool KeyInvolved(byte[] pubKey, byte[] spk, byte[]? rs, byte[]? ws)
    {
        byte[] hash = CryptoNative.Hash160(pubKey);
        foreach (var script in new[] { spk, rs, ws })
        {
            if (script is null) continue;
            if (IsP2WPKH(script) && script.AsSpan(2).SequenceEqual(hash)) return true;
            if (IsP2PKH(script) && script.AsSpan(3, 20).SequenceEqual(hash)) return true;
            if (ContainsPush(script, pubKey)) return true;
        }
        return false;
    }

    #endregion

    #region Signer

    /// <summary>
    /// Signer: adds signatures from <paramref name="privateKey"/> to every input it can sign. Returns the number of
    /// signatures added. Throws <see cref="InvalidOperationException"/> when an input fails the BIP-174 signer checks
    /// (UTXO/txid mismatch, scripts that do not hash to the spent output, witness UTXO on a non-witness input).
    /// </summary>
    public int Sign(ReadOnlySpan<byte> privateKey)
    {
        int count = 0;
        for (int i = 0; i < _inputs.Count; i++)
            if (SignInput(i, privateKey)) count++;
        return count;
    }

    /// <summary>
    /// Signs with keys derived from <paramref name="masterKey"/> for every BIP-32 origin (including Taproot origins)
    /// whose fingerprint matches the master key.
    /// </summary>
    public int Sign(HdKey masterKey)
    {
        uint fingerprint = masterKey.Fingerprint;
        int count = 0;
        for (int i = 0; i < _inputs.Count; i++)
        {
            var paths = _inputs[i].Bip32Derivations.Concat(_inputs[i].TapBip32Derivations)
                .Where(o => o.Fingerprint == fingerprint).Select(o => o.PathString).Distinct();
            foreach (string path in paths)
            {
                using var child = masterKey.Derive(path);
                if (SignInput(i, child.PrivateKey.Span)) count++;
            }
        }
        return count;
    }

    /// <summary>Signs one input; returns false when the key is not involved in it or it is already finalized.</summary>
    public bool SignInput(int index, ReadOnlySpan<byte> privateKey)
    {
        var input = _inputs[index];
        if (input.IsFinalized) return false;
        var tx = GetUnsignedTransaction();
        var ctx = Resolve(index, tx, strict: true);
        byte[] pub = CryptoNative.Secp256k1GetPublicKey(privateKey, compressed: true);

        if (ctx.Kind == ScriptKind.Taproot) return SignTaproot(index, tx, ctx, privateKey, pub);

        var type = input.SighashType ?? SigHashType.All;
        if (type == SigHashType.Default) throw new InvalidOperationException($"Input {index}: SIGHASH_DEFAULT is Taproot-only");
        byte[] script = ctx.SigningScript;
        if (!KeyCanSign(pub, script, ctx.Kind)) return false;

        byte[] sighash = ctx.SegwitV0
            ? tx.GetSegwitV0SigHash(index, ctx.Kind == ScriptKind.P2WPKH ? BitcoinScript.P2PKH(script.AsSpan(2)) : script, ctx.Spent.ValueSats, type)
            : tx.GetLegacySigHash(index, script, type);
        input.AddPartialSignature(pub, BitcoinTransaction.EcdsaDer(privateKey, sighash, type));
        return true;
    }

    private bool SignTaproot(int index, BitcoinTransaction tx, InputContext ctx, ReadOnlySpan<byte> privateKey, byte[] pub)
    {
        var input = _inputs[index];
        var spent = new List<BitcoinUtxo>();
        for (int i = 0; i < _inputs.Count; i++)
        {
            var o = GetSpentOutput(i) ?? throw new InvalidOperationException($"Taproot signing needs the UTXO of every input (input {i} is missing)");
            spent.Add(new BitcoinUtxo(tx.Inputs[i].TxId, tx.Inputs[i].Vout, o.ValueSats, o.ScriptPubKey));
        }
        var type = input.SighashType ?? SigHashType.Default;
        byte[] xonly = pub[1..];
        bool signed = false;

        byte[] merkleRoot = input.TapMerkleRoot ?? [];
        bool internalMatches = input.TapInternalKey is { } ik ? ik.AsSpan().SequenceEqual(xonly) : true;
        if (internalMatches)
        {
            var (outputKey, _) = CryptoNative.TaprootTweakPublicKey(xonly, merkleRoot);
            if (outputKey.AsSpan().SequenceEqual(ctx.Spent.ScriptPubKey.AsSpan(2)))
            {
                byte[] hash = tx.GetTaprootSigHash(index, spent, type);
                input.TapKeySignature = BitcoinTransaction.SignTaprootKeyPath(privateKey, hash, type, merkleRoot);
                signed = true;
            }
        }

        byte[] push = [0x20, .. xonly];
        foreach (var (_, script, leafVersion) in input.TapLeafScripts)
        {
            if (script.AsSpan().IndexOf(push) < 0) continue;
            byte[] leafHash = Taproot.LeafHash(script, leafVersion);
            byte[] hash = tx.GetTaprootSigHash(index, spent, type, leafHash);
            byte[] sig = CryptoNative.SchnorrSign(privateKey, hash, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            input.Map.Set(InTapScriptSig, [.. xonly, .. leafHash], type == SigHashType.Default ? sig : [.. sig, (byte)type]);
            signed = true;
        }
        return signed;
    }

    private static bool KeyCanSign(byte[] pub, byte[] script, ScriptKind kind)
    {
        byte[] hash = CryptoNative.Hash160(pub);
        return kind switch
        {
            ScriptKind.P2PKH => script.AsSpan(3, 20).SequenceEqual(hash),
            ScriptKind.P2WPKH => script.AsSpan(2).SequenceEqual(hash),
            _ => ContainsPush(script, pub),
        };
    }

    #endregion

    #region Combiner

    /// <summary>
    /// Combiner: merges PSBTs for the same unsigned transaction. On conflicting values the first PSBT wins; records are
    /// ordered canonically, so the result does not depend on the order of the inputs.
    /// </summary>
    public static Psbt Combine(params Psbt[] psbts)
    {
        if (psbts.Length == 0) throw new ArgumentException("Nothing to combine", nameof(psbts));
        var result = psbts[0].Clone();
        string txid = result.GetUnsignedTransaction().TxId;
        foreach (var other in psbts.Skip(1))
        {
            if (other.PsbtVersion != result.PsbtVersion || other._inputs.Count != result._inputs.Count ||
                other._outputs.Count != result._outputs.Count || other.GetUnsignedTransaction().TxId != txid)
                throw new InvalidOperationException("PSBTs describe different transactions");
            Merge(result.Global, other.Global);
            for (int i = 0; i < result._inputs.Count; i++) Merge(result._inputs[i].Map, other._inputs[i].Map);
            for (int i = 0; i < result._outputs.Count; i++) Merge(result._outputs[i].Map, other._outputs[i].Map);
        }
        return result;

        static void Merge(PsbtMap into, PsbtMap from)
        {
            foreach (var r in from.Records)
                if (!into.Records.Any(x => x.Key.AsSpan().SequenceEqual(r.Key))) into.Set(r);
        }
    }

    public Psbt Combine(Psbt other) => Combine(this, other);

    #endregion

    #region Finalizer / extractor

    /// <summary>Finalizer: finalizes every input it can. Returns true when all inputs are finalized.</summary>
    public bool Finalize()
    {
        bool all = true;
        for (int i = 0; i < _inputs.Count; i++) all &= FinalizeInput(i);
        return all;
    }

    /// <summary>
    /// Builds the final scriptSig/witness for one input from its partial signatures and clears the other fields
    /// (keeping UTXOs, PSBTv2 fields, proprietary and unknown records). Returns false when signatures are missing.
    /// </summary>
    public bool FinalizeInput(int index)
    {
        var input = _inputs[index];
        if (input.IsFinalized) return true;
        var ctx = Resolve(index, GetUnsignedTransaction(), strict: false);
        var sigs = input.PartialSignatures;
        byte[]? SigFor(byte[] key) => sigs.FirstOrDefault(s => s.PublicKey.AsSpan().SequenceEqual(key)).Signature;

        List<byte[]>? stack = null;    // satisfying stack for the signing script, bottom first
        List<byte[]>? witness = null;
        switch (ctx.Kind)
        {
            case ScriptKind.P2PKH or ScriptKind.P2WPKH:
            {
                var match = sigs.FirstOrDefault(s => KeyCanSign(s.PublicKey, ctx.SigningScript, ctx.Kind));
                if (match.Signature is null) return false;
                stack = [match.Signature, match.PublicKey];
                break;
            }
            case ScriptKind.P2PK:
            {
                byte[] key = ctx.SigningScript[1..^1];
                if (SigFor(key) is not { } sig) return false;
                stack = [sig];
                break;
            }
            case ScriptKind.Multisig:
            {
                BitcoinScript.TryParseMultisig(ctx.SigningScript, out int m, out var keys);
                var chosen = keys.Select(SigFor).OfType<byte[]>().Take(m).ToList();
                if (chosen.Count < m) return false;
                stack = [[], .. chosen];
                break;
            }
            case ScriptKind.Taproot:
            {
                if (input.TapKeySignature is { } keySig)
                {
                    witness = [keySig];
                    break;
                }
                foreach (var (controlBlock, script, leafVersion) in input.TapLeafScripts)
                {
                    byte[] leafHash = Taproot.LeafHash(script, leafVersion);
                    if (script.Length == 34 && script[0] == 0x20 && script[^1] == BitcoinScript.OP_CHECKSIG)
                    {
                        var hit = input.TapScriptSignatures.FirstOrDefault(s => s.XOnlyKey.AsSpan().SequenceEqual(script.AsSpan(1, 32)) && s.LeafHash.AsSpan().SequenceEqual(leafHash));
                        if (hit.Signature is not null)
                        {
                            witness = [hit.Signature, script, controlBlock];
                            break;
                        }
                    }
                }
                if (witness is null) return false;
                break;
            }
            default:
                return false;
        }

        byte[] scriptSig = [];
        if (witness is null)
        {
            if (ctx.SegwitV0)
            {
                witness = [.. stack!];
                if (ctx.Kind != ScriptKind.P2WPKH) witness.Add(ctx.SigningScript);
            }
            else
            {
                scriptSig = [.. stack!.SelectMany(PushItem)];
            }
        }
        if (ctx.RedeemScript is { } rs) scriptSig = [.. scriptSig, .. BitcoinScript.PushData(rs)];

        input.Map.RemoveWhere(r => r.Type is not (InNonWitnessUtxo or InWitnessUtxo or Proprietary)
            and not (>= InPreviousTxId and <= InRequiredHeightLocktime) and <= InTapMerkleRoot);
        if (scriptSig.Length > 0) input.FinalScriptSig = scriptSig;
        if (witness is { Count: > 0 }) input.FinalScriptWitness = witness;
        return true;
    }

    /// <summary>Extractor: the fully signed network transaction. Every input must be finalized.</summary>
    public BitcoinTransaction ExtractTransaction()
    {
        var tx = GetUnsignedTransaction();
        for (int i = 0; i < _inputs.Count; i++)
        {
            var input = _inputs[i];
            if (!input.IsFinalized) throw new InvalidOperationException($"Input {i} is not finalized");
            tx.Inputs[i].ScriptSig = input.FinalScriptSig ?? [];
            tx.Inputs[i].Witness = input.FinalScriptWitness?.ToList() ?? [];
        }
        return tx;
    }

    public bool IsFullySigned => _inputs.All(i => i.IsFinalized);

    // Minimal push for stack items (OP_0 for empty) when building a scriptSig.
    private static byte[] PushItem(byte[] item) => item.Length == 0 ? [BitcoinScript.OP_0] : BitcoinScript.PushData(item);

    #endregion

    #region Script resolution

    private enum ScriptKind
    {
        P2PKH,
        P2PK,
        P2WPKH,
        Multisig,
        Taproot,
        Other,
    }

    private sealed record InputContext(BitcoinTxOutput Spent, byte[] SigningScript, ScriptKind Kind, bool SegwitV0, byte[]? RedeemScript);

    private InputContext Resolve(int index, BitcoinTransaction tx, bool strict)
    {
        var input = _inputs[index];
        BitcoinTxOutput spent;
        bool haveFull = false;
        if (input.NonWitnessUtxo is { } prev)
        {
            if (!string.Equals(prev.TxId, tx.Inputs[index].TxId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Input {index}: non-witness UTXO does not match the previous txid");
            uint vout = tx.Inputs[index].Vout;
            if (vout >= prev.Outputs.Count) throw new InvalidOperationException($"Input {index}: output index out of range");
            spent = prev.Outputs[(int)vout];
            haveFull = true;
        }
        else
        {
            spent = input.WitnessUtxo ?? throw new InvalidOperationException($"Input {index}: missing UTXO information");
        }

        byte[] script = spent.ScriptPubKey;
        byte[]? redeem = null;
        if (IsP2SH(script))
        {
            redeem = input.RedeemScript ?? throw new InvalidOperationException($"Input {index}: P2SH input needs a redeem script");
            if (!CryptoNative.Hash160(redeem).AsSpan().SequenceEqual(script.AsSpan(2, 20)))
                throw new InvalidOperationException($"Input {index}: redeem script does not match the scriptPubKey");
            script = redeem;
        }

        if (IsP2WPKH(script)) return new InputContext(spent, script, ScriptKind.P2WPKH, true, redeem);
        if (IsP2WSH(script))
        {
            byte[] ws = input.WitnessScript ?? throw new InvalidOperationException($"Input {index}: P2WSH input needs a witness script");
            if (!CryptoNative.Sha256(ws).AsSpan().SequenceEqual(script.AsSpan(2, 32)))
                throw new InvalidOperationException($"Input {index}: witness script does not match the {(redeem is null ? "scriptPubKey" : "redeem script")}");
            return new InputContext(spent, ws, Classify(ws), true, redeem);
        }
        if (redeem is null && IsP2TR(script)) return new InputContext(spent, script, ScriptKind.Taproot, false, null);
        if (IsWitnessProgram(script)) throw new InvalidOperationException($"Input {index}: unsupported witness program");

        if (strict && !haveFull)
            throw new InvalidOperationException($"Input {index}: a witness UTXO was provided for a non-witness input");
        return new InputContext(spent, script, Classify(script), false, redeem);
    }

    private static ScriptKind Classify(byte[] script)
    {
        if (IsP2PKH(script)) return ScriptKind.P2PKH;
        if (script.Length is 35 or 67 && script[0] == script.Length - 2 && script[^1] == BitcoinScript.OP_CHECKSIG) return ScriptKind.P2PK;
        if (BitcoinScript.TryParseMultisig(script, out _, out _)) return ScriptKind.Multisig;
        return ScriptKind.Other;
    }

    private static bool IsP2PKH(byte[] s) => s.Length == 25 && s[0] == 0x76 && s[1] == 0xa9 && s[2] == 0x14 && s[23] == 0x88 && s[24] == 0xac;
    private static bool IsP2SH(byte[] s) => s.Length == 23 && s[0] == 0xa9 && s[1] == 0x14 && s[22] == 0x87;
    private static bool IsP2WPKH(byte[] s) => s.Length == 22 && s[0] == 0x00 && s[1] == 0x14;
    private static bool IsP2WSH(byte[] s) => s.Length == 34 && s[0] == 0x00 && s[1] == 0x20;
    private static bool IsP2TR(byte[] s) => s.Length == 34 && s[0] == 0x51 && s[1] == 0x20;

    private static bool IsWitnessProgram(byte[] s) =>
        s.Length is >= 4 and <= 42 && (s[0] == 0x00 || s[0] is >= 0x51 and <= 0x60) && s[1] == s.Length - 2;

    private static bool ContainsPush(byte[] script, byte[] data)
    {
        byte[] push = BitcoinScript.PushData(data);
        return script.AsSpan().IndexOf(push) >= 0;
    }

    #endregion

    #region Encoding helpers

    internal static byte[] UInt32Le(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }

    internal static byte[] Int64Le(long v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b, v);
        return b;
    }

    internal static byte[] CompactSize(ulong v)
    {
        using var ms = new MemoryStream();
        BitcoinTransaction.WriteVarInt(ms, v);
        return ms.ToArray();
    }

    internal static BitcoinTxOutput DecodeTxOut(ReadOnlySpan<byte> value)
    {
        var r = new BitcoinTransaction.TxReader(value);
        long amount = (long)r.UInt64();
        byte[] script = r.VarBytes().ToArray();
        if (r.Remaining != 0) throw new FormatException("Invalid witness UTXO");
        return new BitcoinTxOutput { ValueSats = amount, ScriptPubKey = script };
    }

    internal static IReadOnlyList<byte[]> DecodeWitness(ReadOnlySpan<byte> value)
    {
        var r = new BitcoinTransaction.TxReader(value);
        ulong n = r.VarInt();
        var items = new List<byte[]>();
        for (ulong i = 0; i < n; i++) items.Add(r.VarBytes().ToArray());
        if (r.Remaining != 0) throw new FormatException("Invalid final script witness");
        return items;
    }

    internal static byte[] EncodeWitness(IReadOnlyList<byte[]> items)
    {
        using var ms = new MemoryStream();
        BitcoinTransaction.WriteVarInt(ms, (ulong)items.Count);
        foreach (var item in items) BitcoinTransaction.WriteVarBytes(ms, item);
        return ms.ToArray();
    }

    internal static IReadOnlyList<PsbtKeyOrigin> ReadOrigins(PsbtMap map, ulong type) =>
        [.. map.GetAll(type).Select(r =>
        {
            var (fp, path) = PsbtKeyOrigin.DecodeOrigin(r.Value);
            return new PsbtKeyOrigin(r.KeyData.ToArray(), fp, path);
        })];

    internal static IReadOnlyList<PsbtKeyOrigin> ReadTapOrigins(PsbtMap map, ulong type) =>
        [.. map.GetAll(type).Select(r =>
        {
            var (leaves, fp, path) = DecodeTapOrigin(r.Value);
            return new PsbtKeyOrigin(r.KeyData.ToArray(), fp, path) { LeafHashes = leaves };
        })];

    internal static byte[] EncodeTapOrigin(PsbtKeyOrigin origin)
    {
        using var ms = new MemoryStream();
        BitcoinTransaction.WriteVarInt(ms, (ulong)origin.LeafHashes.Count);
        foreach (var h in origin.LeafHashes) ms.Write(h);
        ms.Write(origin.EncodeOrigin());
        return ms.ToArray();
    }

    internal static (IReadOnlyList<byte[]> Leaves, uint Fingerprint, uint[] Path) DecodeTapOrigin(ReadOnlySpan<byte> value)
    {
        var r = new BitcoinTransaction.TxReader(value);
        ulong n = r.VarInt();
        var leaves = new List<byte[]>();
        for (ulong i = 0; i < n; i++) leaves.Add(r.Bytes(32).ToArray());
        var (fp, path) = PsbtKeyOrigin.DecodeOrigin(r.Bytes(r.Remaining));
        return (leaves, fp, path);
    }

    private void RequireV2()
    {
        if (PsbtVersion != 2) throw new InvalidOperationException("Only PSBTv2 supports adding inputs and outputs; use ConvertTo(2)");
    }

    #endregion
}
