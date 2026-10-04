namespace Crypto.Net.Polkadot;

/// <summary>
/// Minimal decoder for Substrate runtime metadata V14/V15 (from <c>state_getMetadata</c>). It extracts what is
/// needed to build extrinsics against any runtime: pallet and call indices, the signed-extension list, and enough
/// of the type registry to know which extensions carry data.
/// </summary>
public sealed class RuntimeMetadata
{
    public int Version { get; private init; }
    public IReadOnlyDictionary<string, PalletInfo> Pallets { get; private init; } = new Dictionary<string, PalletInfo>();
    public IReadOnlyList<SignedExtensionInfo> SignedExtensions { get; private init; } = [];
    public byte ExtrinsicVersion { get; private init; }
    internal IReadOnlyDictionary<uint, TypeInfo> Types { get; private init; } = new Dictionary<uint, TypeInfo>();

    public sealed record PalletInfo(string Name, byte Index, IReadOnlyDictionary<string, byte> Calls, IReadOnlyDictionary<string, byte[]> Constants);
    public sealed record SignedExtensionInfo(string Identifier, uint ExtraType, uint AdditionalType);

    internal enum TypeKind { Composite, Variant, Sequence, Array, Tuple, Primitive, Compact, BitSequence }

    internal sealed record TypeInfo(TypeKind Kind, IReadOnlyList<uint> Children, IReadOnlyList<(string Name, byte Index, IReadOnlyList<uint> Fields)> Variants, uint ArrayLength, string Path);

    /// <summary>Returns <c>(palletIndex, callIndex)</c> for e.g. <c>("Balances", "transfer_keep_alive")</c>.</summary>
    public (byte Pallet, byte Call) GetCallIndex(string pallet, string call)
    {
        if (!Pallets.TryGetValue(pallet, out var p)) throw new KeyNotFoundException($"Pallet '{pallet}' not found in runtime metadata");
        if (!p.Calls.TryGetValue(call, out byte c)) throw new KeyNotFoundException($"Call '{pallet}.{call}' not found in runtime metadata");
        return (p.Index, c);
    }

    /// <summary>True when values of <paramref name="typeId"/> encode to zero bytes (e.g. <c>()</c>).</summary>
    public bool IsZeroSized(uint typeId) => IsZeroSized(typeId, 0);

    private bool IsZeroSized(uint id, int depth)
    {
        if (depth > 32 || !Types.TryGetValue(id, out var t)) return false;
        return t.Kind switch
        {
            TypeKind.Composite or TypeKind.Tuple => t.Children.All(c => IsZeroSized(c, depth + 1)),
            TypeKind.Array => t.ArrayLength == 0 || IsZeroSized(t.Children[0], depth + 1),
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
        var pending = new List<(string Name, byte Index, uint? CallsType, Dictionary<string, byte[]> Constants)>();

        for (int i = 0; i < palletCount; i++)
        {
            string name = r.String();
            if (r.Option()) SkipStorage(ref r);
            uint? calls = r.Option() ? (uint)r.Compact() : null;
            if (r.Option()) r.Compact(); // events
            int constCount = r.CompactInt();
            var constants = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            for (int c = 0; c < constCount; c++)
            {
                string cname = r.String();
                r.Compact();
                constants[cname] = r.VecU8();
                r.SkipStrings();
            }
            if (r.Option()) r.Compact(); // errors
            byte index = r.U8();
            if (version == 15) r.SkipStrings(); // docs
            pending.Add((name, index, calls, constants));
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
            extensions.Add(new SignedExtensionInfo(r.String(), (uint)r.Compact(), (uint)r.Compact()));

        foreach (var (name, index, callsType, constants) in pending)
        {
            var calls = new Dictionary<string, byte>(StringComparer.Ordinal);
            if (callsType is uint ct && types.TryGetValue(ct, out var callEnum) && callEnum.Kind == TypeKind.Variant)
                foreach (var v in callEnum.Variants) calls[v.Name] = v.Index;
            pallets[name] = new PalletInfo(name, index, calls, constants);
        }

        return new RuntimeMetadata
        {
            Version = version,
            Pallets = pallets,
            SignedExtensions = extensions,
            ExtrinsicVersion = extrinsicVersion,
            Types = types,
        };
    }

    private static Dictionary<uint, TypeInfo> ReadRegistry(ref ScaleReader r)
    {
        int count = r.CompactInt();
        var types = new Dictionary<uint, TypeInfo>(count);
        for (int i = 0; i < count; i++)
        {
            uint id = (uint)r.Compact();
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
            var children = new List<uint>();
            var variants = new List<(string, byte, IReadOnlyList<uint>)>();
            uint arrayLen = 0;
            switch (tag)
            {
                case 0:
                    children.AddRange(ReadFields(ref r));
                    break;
                case 1:
                    int vc = r.CompactInt();
                    for (int v = 0; v < vc; v++)
                    {
                        string vname = r.String();
                        var fields = ReadFields(ref r);
                        byte vindex = r.U8();
                        r.SkipStrings();
                        variants.Add((vname, vindex, fields));
                    }
                    break;
                case 2:
                    children.Add((uint)r.Compact());
                    break;
                case 3:
                    arrayLen = r.U32();
                    children.Add((uint)r.Compact());
                    break;
                case 4:
                    int tc = r.CompactInt();
                    for (int t = 0; t < tc; t++) children.Add((uint)r.Compact());
                    break;
                case 5:
                    r.U8();
                    break;
                case 6:
                    children.Add((uint)r.Compact());
                    break;
                case 7:
                    children.Add((uint)r.Compact());
                    children.Add((uint)r.Compact());
                    break;
                default:
                    throw new FormatException($"Unknown TypeDef tag {tag}");
            }
            r.SkipStrings(); // docs
            types[id] = new TypeInfo((TypeKind)tag, children, variants, arrayLen, string.Join("::", path));
        }
        return types;
    }

    private static List<uint> ReadFields(ref ScaleReader r)
    {
        int n = r.CompactInt();
        var list = new List<uint>(n);
        for (int i = 0; i < n; i++)
        {
            r.OptionString();
            list.Add((uint)r.Compact());
            r.OptionString();
            r.SkipStrings();
        }
        return list;
    }

    private static void SkipStorage(ref ScaleReader r)
    {
        r.String(); // prefix
        int entries = r.CompactInt();
        for (int i = 0; i < entries; i++)
        {
            r.String();
            r.U8(); // modifier
            byte kind = r.U8();
            if (kind == 0)
            {
                r.Compact();
            }
            else if (kind == 1)
            {
                int hashers = r.CompactInt();
                for (int h = 0; h < hashers; h++) r.U8();
                r.Compact();
                r.Compact();
            }
            else
            {
                throw new FormatException($"Unknown storage entry type {kind}");
            }
            r.VecU8(); // default
            r.SkipStrings();
        }
    }
}
