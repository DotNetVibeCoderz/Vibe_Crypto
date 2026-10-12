using Crypto.Net.Native;

namespace Crypto.Net.Polkadot;

/// <summary>SCALE primitive kinds of the type registry (<c>scale-info</c> <c>TypeDefPrimitive</c>).</summary>
public enum ScalePrimitive : byte
{
    Bool, Char, Str, U8, U16, U32, U64, U128, U256, I8, I16, I32, I64, I128, I256,
}

/// <summary>Kinds of type definitions in the registry.</summary>
public enum ScaleTypeKind : byte
{
    Composite, Variant, Sequence, Array, Tuple, Primitive, Compact, BitSequence,
}

/// <summary>A named or unnamed field of a composite type or enum variant.</summary>
public sealed record ScaleField(string? Name, uint Type, string? TypeName);

/// <summary>An enum variant: name, index and fields.</summary>
public sealed record ScaleVariantInfo(string Name, byte Index, IReadOnlyList<ScaleField> Fields);

/// <summary>One entry of the portable type registry.</summary>
public sealed record ScaleTypeInfo(
    uint Id,
    string Path,
    ScaleTypeKind Kind,
    IReadOnlyList<ScaleField> Fields,
    IReadOnlyList<ScaleVariantInfo> Variants,
    IReadOnlyList<uint> Children,
    uint ArrayLength,
    ScalePrimitive Primitive);

/// <summary>Storage key hashers.</summary>
public enum StorageHasher : byte
{
    Blake2_128, Blake2_256, Blake2_128Concat, Twox128, Twox256, Twox64Concat, Identity,
}

/// <summary>A storage item: plain (no keys) or a map with one hasher per key.</summary>
public sealed record StorageEntryInfo(string Prefix, string Name, IReadOnlyList<StorageHasher> Hashers, uint? KeyType, uint ValueType, bool IsOptional);

/// <summary>
/// Decoder for Substrate runtime metadata V14/V15 (from <c>state_getMetadata</c>): pallets with call, event and error
/// enums, storage entries, constants, signed extensions and the full portable type registry, enough to build
/// extrinsics and decode any storage value or event with <see cref="ScaleDecoder"/>.
/// </summary>
public sealed class RuntimeMetadata
{
    public int Version { get; private init; }
    public IReadOnlyDictionary<string, PalletInfo> Pallets { get; private init; } = new Dictionary<string, PalletInfo>();
    public IReadOnlyList<SignedExtensionInfo> SignedExtensions { get; private init; } = [];
    public byte ExtrinsicVersion { get; private init; }

    /// <summary>The portable type registry, by type id.</summary>
    public IReadOnlyDictionary<uint, ScaleTypeInfo> Types { get; private init; } = new Dictionary<uint, ScaleTypeInfo>();

    public sealed record PalletInfo(string Name, byte Index, IReadOnlyDictionary<string, byte> Calls, IReadOnlyDictionary<string, byte[]> Constants)
    {
        public uint? CallsType { get; init; }
        public uint? EventsType { get; init; }
        public uint? ErrorsType { get; init; }
        public IReadOnlyDictionary<string, StorageEntryInfo> Storage { get; init; } = new Dictionary<string, StorageEntryInfo>();
    }

    public sealed record SignedExtensionInfo(string Identifier, uint ExtraType, uint AdditionalType);

    /// <summary>Returns <c>(palletIndex, callIndex)</c> for e.g. <c>("Balances", "transfer_keep_alive")</c>.</summary>
    public (byte Pallet, byte Call) GetCallIndex(string pallet, string call)
    {
        if (!Pallets.TryGetValue(pallet, out var p)) throw new KeyNotFoundException($"Pallet '{pallet}' not found in runtime metadata");
        if (!p.Calls.TryGetValue(call, out byte c)) throw new KeyNotFoundException($"Call '{pallet}.{call}' not found in runtime metadata");
        return (p.Index, c);
    }

    public PalletInfo? FindPallet(byte index) => Pallets.Values.FirstOrDefault(p => p.Index == index);

    /// <summary>The storage entry <paramref name="pallet"/>.<paramref name="entry"/>.</summary>
    public StorageEntryInfo GetStorageEntry(string pallet, string entry)
    {
        if (!Pallets.TryGetValue(pallet, out var p)) throw new KeyNotFoundException($"Pallet '{pallet}' not found in runtime metadata");
        return p.Storage.TryGetValue(entry, out var e) ? e : throw new KeyNotFoundException($"Storage '{pallet}.{entry}' not found in runtime metadata");
    }

    /// <summary>
    /// Storage key for <paramref name="pallet"/>.<paramref name="entry"/>: <c>twox128(prefix) ++ twox128(name)</c> followed
    /// by each SCALE-encoded map key hashed with the entry's hasher.
    /// </summary>
    public byte[] StorageKey(string pallet, string entry, params byte[][] encodedKeys)
    {
        var e = GetStorageEntry(pallet, entry);
        if (encodedKeys.Length > e.Hashers.Count) throw new ArgumentException($"'{pallet}.{entry}' takes at most {e.Hashers.Count} keys");
        var key = new List<byte>(StorageKeys.Prefix(e.Prefix, e.Name));
        for (int i = 0; i < encodedKeys.Length; i++) key.AddRange(StorageKeys.Hash(e.Hashers[i], encodedKeys[i]));
        return [.. key];
    }

    /// <summary>True when values of <paramref name="typeId"/> encode to zero bytes (e.g. <c>()</c>).</summary>
    public bool IsZeroSized(uint typeId) => IsZeroSized(typeId, 0);

    private bool IsZeroSized(uint id, int depth)
    {
        if (depth > 32 || !Types.TryGetValue(id, out var t)) return false;
        return t.Kind switch
        {
            ScaleTypeKind.Composite => t.Fields.All(f => IsZeroSized(f.Type, depth + 1)),
            ScaleTypeKind.Tuple => t.Children.All(c => IsZeroSized(c, depth + 1)),
            ScaleTypeKind.Array => t.ArrayLength == 0 || IsZeroSized(t.Children[0], depth + 1),
            _ => false,
        };
    }

    public static RuntimeMetadata Parse(ReadOnlySpan<byte> raw)
    {
        var r = new ScaleReader(raw);
        if (r.U32() != 0x6174656d) throw new FormatException("Missing 'meta' magic");
        byte version = r.U8();
        if (version is not (14 or 15)) throw new NotSupportedException($"Runtime metadata V{version} is not supported (V14/V15 only)");

        var types = ReadRegistry(ref r);
        int palletCount = r.CompactInt();
        var pallets = new Dictionary<string, PalletInfo>(StringComparer.Ordinal);

        for (int i = 0; i < palletCount; i++)
        {
            string name = r.String();
            var storage = r.Option() ? ReadStorage(ref r) : new Dictionary<string, StorageEntryInfo>(StringComparer.Ordinal);
            uint? calls = r.Option() ? r.CompactU32() : null;
            uint? events = r.Option() ? r.CompactU32() : null;
            int constCount = r.CompactInt();
            var constants = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            for (int c = 0; c < constCount; c++)
            {
                string cname = r.String();
                r.Compact();
                constants[cname] = r.VecU8();
                r.SkipStrings();
            }
            uint? errors = r.Option() ? r.CompactU32() : null;
            byte index = r.U8();
            if (version == 15) r.SkipStrings(); // docs

            var callIndex = new Dictionary<string, byte>(StringComparer.Ordinal);
            if (calls is uint ct && types.TryGetValue(ct, out var callEnum) && callEnum.Kind == ScaleTypeKind.Variant)
                foreach (var v in callEnum.Variants) callIndex[v.Name] = v.Index;
            pallets[name] = new PalletInfo(name, index, callIndex, constants)
            {
                CallsType = calls,
                EventsType = events,
                ErrorsType = errors,
                Storage = storage,
            };
        }

        byte extrinsicVersion;
        if (version == 14)
        {
            r.Compact();
            extrinsicVersion = r.U8();
        }
        else
        {
            extrinsicVersion = r.U8();
            r.Compact(); r.Compact(); r.Compact(); r.Compact(); // address, call, signature, extra types
        }

        int extCount = r.CompactInt();
        var extensions = new List<SignedExtensionInfo>(extCount);
        for (int i = 0; i < extCount; i++)
            extensions.Add(new SignedExtensionInfo(r.String(), r.CompactU32(), r.CompactU32()));

        return new RuntimeMetadata
        {
            Version = version,
            Pallets = pallets,
            SignedExtensions = extensions,
            ExtrinsicVersion = extrinsicVersion,
            Types = types,
        };
    }

    private static Dictionary<uint, ScaleTypeInfo> ReadRegistry(ref ScaleReader r)
    {
        int count = r.CompactInt();
        var types = new Dictionary<uint, ScaleTypeInfo>(count);
        for (int i = 0; i < count; i++)
        {
            uint id = r.CompactU32();
            int pathLen = r.CompactInt();
            var path = new string[pathLen];
            for (int p = 0; p < pathLen; p++) path[p] = r.String();
            int paramCount = r.CompactInt();
            for (int p = 0; p < paramCount; p++)
            {
                r.String();
                if (r.Option()) r.Compact();
            }

            byte tag = r.U8();
            IReadOnlyList<ScaleField> fields = [];
            var children = new List<uint>();
            var variants = new List<ScaleVariantInfo>();
            uint arrayLen = 0;
            var primitive = ScalePrimitive.Bool;
            switch (tag)
            {
                case 0:
                    fields = ReadFields(ref r);
                    break;
                case 1:
                    int vc = r.CompactInt();
                    for (int v = 0; v < vc; v++)
                    {
                        string vname = r.String();
                        var vfields = ReadFields(ref r);
                        byte vindex = r.U8();
                        r.SkipStrings();
                        variants.Add(new ScaleVariantInfo(vname, vindex, vfields));
                    }
                    break;
                case 2:
                    children.Add(r.CompactU32());
                    break;
                case 3:
                    arrayLen = r.U32();
                    children.Add(r.CompactU32());
                    break;
                case 4:
                    int tc = r.CompactInt();
                    for (int t = 0; t < tc; t++) children.Add(r.CompactU32());
                    break;
                case 5:
                    byte prim = r.U8();
                    if (prim > (byte)ScalePrimitive.I256) throw new FormatException($"Unknown primitive {prim}");
                    primitive = (ScalePrimitive)prim;
                    break;
                case 6:
                    children.Add(r.CompactU32());
                    break;
                case 7:
                    children.Add(r.CompactU32()); // bit store
                    children.Add(r.CompactU32()); // bit order
                    break;
                default:
                    throw new FormatException($"Unknown TypeDef tag {tag}");
            }
            r.SkipStrings(); // docs
            types[id] = new ScaleTypeInfo(id, string.Join("::", path), (ScaleTypeKind)tag, fields, variants, children, arrayLen, primitive);
        }
        return types;
    }

    private static List<ScaleField> ReadFields(ref ScaleReader r)
    {
        int n = r.CompactInt();
        var list = new List<ScaleField>(n);
        for (int i = 0; i < n; i++)
        {
            string? name = r.OptionString();
            uint type = r.CompactU32();
            string? typeName = r.OptionString();
            r.SkipStrings();
            list.Add(new ScaleField(name, type, typeName));
        }
        return list;
    }

    private static Dictionary<string, StorageEntryInfo> ReadStorage(ref ScaleReader r)
    {
        string prefix = r.String();
        int entries = r.CompactInt();
        var result = new Dictionary<string, StorageEntryInfo>(entries, StringComparer.Ordinal);
        for (int i = 0; i < entries; i++)
        {
            string name = r.String();
            bool optional = r.U8() == 0; // 0 = Optional, 1 = Default
            byte kind = r.U8();
            var hashers = new List<StorageHasher>();
            uint? keyType = null;
            uint valueType;
            if (kind == 0)
            {
                valueType = r.CompactU32();
            }
            else if (kind == 1)
            {
                int n = r.CompactInt();
                for (int h = 0; h < n; h++)
                {
                    byte hasher = r.U8();
                    if (hasher > (byte)StorageHasher.Identity) throw new FormatException($"Unknown storage hasher {hasher}");
                    hashers.Add((StorageHasher)hasher);
                }
                keyType = r.CompactU32();
                valueType = r.CompactU32();
            }
            else
            {
                throw new FormatException($"Unknown storage entry type {kind}");
            }
            r.VecU8(); // default
            r.SkipStrings();
            result[name] = new StorageEntryInfo(prefix, name, hashers, keyType, valueType, optional);
        }
        return result;
    }
}

/// <summary>Substrate storage key hashing (xxHash-based <c>twox</c> and BLAKE2 hashers).</summary>
public static class StorageKeys
{
    /// <summary><c>twox128(pallet) ++ twox128(item)</c>.</summary>
    public static byte[] Prefix(string pallet, string item) =>
        [.. Twox128(System.Text.Encoding.UTF8.GetBytes(pallet)), .. Twox128(System.Text.Encoding.UTF8.GetBytes(item))];

    public static byte[] Hash(StorageHasher hasher, ReadOnlySpan<byte> data) => hasher switch
    {
        StorageHasher.Blake2_128 => CryptoNative.Blake2b128(data),
        StorageHasher.Blake2_256 => CryptoNative.Blake2b256(data),
        StorageHasher.Blake2_128Concat => [.. CryptoNative.Blake2b128(data), .. data],
        StorageHasher.Twox128 => Twox128(data),
        StorageHasher.Twox256 => TwoxN(data, 4),
        StorageHasher.Twox64Concat => [.. TwoxN(data, 1), .. data],
        StorageHasher.Identity => data.ToArray(),
        _ => throw new ArgumentOutOfRangeException(nameof(hasher)),
    };

    public static byte[] Twox128(ReadOnlySpan<byte> data) => TwoxN(data, 2);

    private static byte[] TwoxN(ReadOnlySpan<byte> data, int words)
    {
        var result = new byte[words * 8];
        for (int i = 0; i < words; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(i * 8), XxHash64(data, (ulong)i));
        return result;
    }

    /// <summary>xxHash64 (non-cryptographic; used only for storage key prefixes).</summary>
    public static ulong XxHash64(ReadOnlySpan<byte> data, ulong seed)
    {
        const ulong P1 = 11400714785074694791UL, P2 = 14029467366897019727UL, P3 = 1609587929392839161UL,
            P4 = 9650029242287828579UL, P5 = 2870177450012600261UL;
        static ulong Round(ulong acc, ulong input) => System.Numerics.BitOperations.RotateLeft(acc + input * P2, 31) * P1;
        static ulong Merge(ulong acc, ulong val) => (acc ^ Round(0, val)) * P1 + P4;
        static ulong Read64(ReadOnlySpan<byte> s) => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(s);

        int pos = 0, len = data.Length;
        ulong h;
        if (len >= 32)
        {
            ulong v1 = seed + P1 + P2, v2 = seed + P2, v3 = seed, v4 = seed - P1;
            for (; pos <= len - 32; pos += 32)
            {
                v1 = Round(v1, Read64(data[pos..]));
                v2 = Round(v2, Read64(data[(pos + 8)..]));
                v3 = Round(v3, Read64(data[(pos + 16)..]));
                v4 = Round(v4, Read64(data[(pos + 24)..]));
            }
            h = System.Numerics.BitOperations.RotateLeft(v1, 1) + System.Numerics.BitOperations.RotateLeft(v2, 7) +
                System.Numerics.BitOperations.RotateLeft(v3, 12) + System.Numerics.BitOperations.RotateLeft(v4, 18);
            h = Merge(h, v1);
            h = Merge(h, v2);
            h = Merge(h, v3);
            h = Merge(h, v4);
        }
        else
        {
            h = seed + P5;
        }
        h += (ulong)len;
        for (; pos <= len - 8; pos += 8)
            h = System.Numerics.BitOperations.RotateLeft(h ^ Round(0, Read64(data[pos..])), 27) * P1 + P4;
        if (pos <= len - 4)
        {
            h = System.Numerics.BitOperations.RotateLeft(h ^ (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]) * P1), 23) * P2 + P3;
            pos += 4;
        }
        for (; pos < len; pos++)
            h = System.Numerics.BitOperations.RotateLeft(h ^ (data[pos] * P5), 11) * P1;
        h ^= h >> 33;
        h *= P2;
        h ^= h >> 29;
        h *= P3;
        h ^= h >> 32;
        return h;
    }
}
