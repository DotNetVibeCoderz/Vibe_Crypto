using System.Numerics;
using System.Text;

namespace Crypto.Net.Polkadot;

/// <summary>A decoded enum value: variant name and index plus its fields (null when it has none).</summary>
public sealed record ScaleVariant(string Name, byte Index, object? Value)
{
    public override string ToString() => Value is null ? Name : $"{Name}({ScaleDecoder.Format(Value)})";
}

/// <summary>
/// Decodes any SCALE value described by the runtime type registry into plain .NET values:
/// <list type="bullet">
/// <item>composites with named fields → <see cref="IReadOnlyDictionary{TKey,TValue}"/> (field order kept);</item>
/// <item>a single unnamed field → its value; several unnamed fields or tuples → <c>object?[]</c>;</item>
/// <item>enums → <see cref="ScaleVariant"/>; byte sequences and arrays → <c>byte[]</c>; other sequences → <c>object?[]</c>;</item>
/// <item>integers → <c>byte</c>…<c>ulong</c>, signed counterparts, or <see cref="BigInteger"/> for 128/256-bit and compacts.</item>
/// </list>
/// </summary>
public static class ScaleDecoder
{
    private const int MaxDepth = 64;

    /// <summary>Decodes <paramref name="data"/> as <paramref name="typeId"/>; all bytes must be consumed.</summary>
    public static object? Decode(RuntimeMetadata metadata, uint typeId, ReadOnlySpan<byte> data)
    {
        var r = new ScaleReader(data);
        object? value = Decode(metadata, typeId, ref r);
        if (r.Remaining != 0) throw new FormatException($"{r.Remaining} trailing bytes after SCALE value");
        return value;
    }

    public static object? Decode(RuntimeMetadata metadata, uint typeId, ref ScaleReader r) => Decode(metadata, typeId, ref r, 0);

    private static object? Decode(RuntimeMetadata md, uint typeId, ref ScaleReader r, int depth)
    {
        if (depth > MaxDepth) throw new FormatException("SCALE type nesting too deep");
        if (!md.Types.TryGetValue(typeId, out var t)) throw new FormatException($"Unknown type id {typeId}");
        switch (t.Kind)
        {
            case ScaleTypeKind.Composite:
                return DecodeFields(md, t.Fields, ref r, depth);
            case ScaleTypeKind.Variant:
            {
                byte index = r.U8();
                var variant = t.Variants.FirstOrDefault(v => v.Index == index)
                    ?? throw new FormatException($"Variant index {index} not defined for {t.Path}");
                return new ScaleVariant(variant.Name, index, DecodeFields(md, variant.Fields, ref r, depth));
            }
            case ScaleTypeKind.Sequence:
            {
                int n = r.CompactInt();
                if (n > r.Remaining && !md.IsZeroSized(t.Children[0])) throw new FormatException("Sequence length exceeds the data");
                return DecodeItems(md, t.Children[0], n, ref r, depth);
            }
            case ScaleTypeKind.Array:
                return DecodeItems(md, t.Children[0], checked((int)t.ArrayLength), ref r, depth);
            case ScaleTypeKind.Tuple:
            {
                if (t.Children.Count == 0) return null;
                var items = new object?[t.Children.Count];
                for (int i = 0; i < items.Length; i++) items[i] = Decode(md, t.Children[i], ref r, depth + 1);
                return items;
            }
            case ScaleTypeKind.Primitive:
                return DecodePrimitive(t.Primitive, ref r);
            case ScaleTypeKind.Compact:
                return r.Compact();
            case ScaleTypeKind.BitSequence:
            {
                int bits = r.CompactInt();
                int storeBytes = md.Types.TryGetValue(t.Children[0], out var store) ? store.Primitive switch
                {
                    ScalePrimitive.U16 => 2,
                    ScalePrimitive.U32 => 4,
                    ScalePrimitive.U64 => 8,
                    _ => 1,
                } : 1;
                int words = (bits + storeBytes * 8 - 1) / (storeBytes * 8);
                return r.Bytes(words * storeBytes).ToArray();
            }
            default:
                throw new FormatException($"Unsupported type kind {t.Kind}");
        }
    }

    private static object? DecodeFields(RuntimeMetadata md, IReadOnlyList<ScaleField> fields, ref ScaleReader r, int depth)
    {
        if (fields.Count == 0) return null;
        if (fields.All(f => f.Name is not null))
        {
            var map = new Dictionary<string, object?>(fields.Count, StringComparer.Ordinal);
            foreach (var f in fields) map[f.Name!] = Decode(md, f.Type, ref r, depth + 1);
            return map;
        }
        if (fields.Count == 1) return Decode(md, fields[0].Type, ref r, depth + 1);
        var items = new object?[fields.Count];
        for (int i = 0; i < items.Length; i++) items[i] = Decode(md, fields[i].Type, ref r, depth + 1);
        return items;
    }

    private static object DecodeItems(RuntimeMetadata md, uint element, int count, ref ScaleReader r, int depth)
    {
        if (md.Types.TryGetValue(element, out var e) && e.Kind == ScaleTypeKind.Primitive && e.Primitive == ScalePrimitive.U8)
            return r.Bytes(count).ToArray();
        var items = new object?[count];
        for (int i = 0; i < count; i++) items[i] = Decode(md, element, ref r, depth + 1);
        return items;
    }

    private static object DecodePrimitive(ScalePrimitive p, ref ScaleReader r) => p switch
    {
        ScalePrimitive.Bool => r.Bool(),
        ScalePrimitive.Char => char.ConvertFromUtf32((int)r.U32()),
        ScalePrimitive.Str => r.String(),
        ScalePrimitive.U8 => r.U8(),
        ScalePrimitive.U16 => r.U16(),
        ScalePrimitive.U32 => r.U32(),
        ScalePrimitive.U64 => r.U64(),
        ScalePrimitive.U128 => r.U128(),
        ScalePrimitive.U256 => new BigInteger(r.Bytes(32), isUnsigned: true, isBigEndian: false),
        ScalePrimitive.I8 => (sbyte)r.U8(),
        ScalePrimitive.I16 => (short)r.U16(),
        ScalePrimitive.I32 => (int)r.U32(),
        ScalePrimitive.I64 => (long)r.U64(),
        ScalePrimitive.I128 => new BigInteger(r.Bytes(16), isUnsigned: false, isBigEndian: false),
        ScalePrimitive.I256 => new BigInteger(r.Bytes(32), isUnsigned: false, isBigEndian: false),
        _ => throw new FormatException($"Unknown primitive {p}"),
    };

    /// <summary>Compact human-readable rendering of a decoded value (byte arrays as 0x-hex).</summary>
    public static string Format(object? value)
    {
        var sb = new StringBuilder();
        Write(sb, value, 0);
        return sb.ToString();

        static void Write(StringBuilder sb, object? v, int depth)
        {
            if (depth > MaxDepth)
            {
                sb.Append('…');
                return;
            }
            switch (v)
            {
                case null:
                    sb.Append("()");
                    break;
                case byte[] bytes:
                    sb.Append("0x").Append(Convert.ToHexStringLower(bytes));
                    break;
                case string s:
                    sb.Append('"').Append(s).Append('"');
                    break;
                case ScaleVariant sv:
                    sb.Append(sv.Name);
                    if (sv.Value is not null)
                    {
                        sb.Append('(');
                        Write(sb, sv.Value, depth + 1);
                        sb.Append(')');
                    }
                    break;
                case IReadOnlyDictionary<string, object?> map:
                    sb.Append('{');
                    bool first = true;
                    foreach (var (k, item) in map)
                    {
                        if (!first) sb.Append(", ");
                        first = false;
                        sb.Append(k).Append(": ");
                        Write(sb, item, depth + 1);
                    }
                    sb.Append('}');
                    break;
                case object?[] items:
                    sb.Append('[');
                    for (int i = 0; i < items.Length; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        Write(sb, items[i], depth + 1);
                    }
                    sb.Append(']');
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                case IFormattable f:
                    sb.Append(f.ToString(null, System.Globalization.CultureInfo.InvariantCulture));
                    break;
                default:
                    sb.Append(v);
                    break;
            }
        }
    }
}

/// <summary>Phase of block execution an event was emitted in.</summary>
public enum EventPhase
{
    ApplyExtrinsic,
    Finalization,
    Initialization,
}

/// <summary>A decoded runtime event from <c>System.Events</c>.</summary>
public sealed record SubstrateEvent(EventPhase Phase, uint? ExtrinsicIndex, string Pallet, string Name, object? Fields, IReadOnlyList<byte[]> Topics)
{
    public bool Is(string pallet, string name) => Pallet == pallet && Name == name;

    /// <summary>A named field, or null when the event has no field of that name.</summary>
    public object? this[string field] => Fields is IReadOnlyDictionary<string, object?> map && map.TryGetValue(field, out var v) ? v : null;

    public override string ToString() => $"{Pallet}.{Name}{(Fields is null ? "" : " " + ScaleDecoder.Format(Fields))}";
}

/// <summary>Decodes <c>System.Events</c> and dispatch errors with the runtime metadata.</summary>
public static class SubstrateEvents
{
    /// <summary>Storage key of <c>System.Events</c>.</summary>
    public static readonly byte[] StorageKey = StorageKeys.Prefix("System", "Events");

    /// <summary>Decodes the raw <c>System.Events</c> storage value.</summary>
    public static IReadOnlyList<SubstrateEvent> Decode(RuntimeMetadata metadata, ReadOnlySpan<byte> raw)
    {
        var entry = metadata.GetStorageEntry("System", "Events");
        var records = ScaleDecoder.Decode(metadata, entry.ValueType, raw) as object?[] ?? [];
        var events = new List<SubstrateEvent>(records.Length);
        foreach (var record in records)
        {
            if (record is not IReadOnlyDictionary<string, object?> map) throw new FormatException("Unexpected EventRecord shape");
            var phase = map["phase"] as ScaleVariant ?? throw new FormatException("EventRecord without phase");
            var outer = map["event"] as ScaleVariant ?? throw new FormatException("EventRecord without event");
            var inner = outer.Value as ScaleVariant ?? throw new FormatException("Pallet event is not an enum");
            var topics = map.TryGetValue("topics", out var t) && t is object?[] arr ? arr.OfType<byte[]>().ToList() : [];
            var kind = Enum.TryParse<EventPhase>(phase.Name, out var k) ? k : EventPhase.Finalization;
            uint? index = kind == EventPhase.ApplyExtrinsic ? Convert.ToUInt32(phase.Value, System.Globalization.CultureInfo.InvariantCulture) : null;
            events.Add(new SubstrateEvent(kind, index, outer.Name, inner.Name, inner.Value, topics));
        }
        return events;
    }

    /// <summary>
    /// Renders a decoded <c>DispatchError</c>; module errors resolve to <c>Pallet.Error</c> through the pallet's error enum.
    /// </summary>
    public static string DescribeDispatchError(RuntimeMetadata metadata, object? error)
    {
        if (error is not ScaleVariant v) return ScaleDecoder.Format(error);
        if (v.Name == "Module" && v.Value is IReadOnlyDictionary<string, object?> m &&
            m.TryGetValue("index", out var pi) && pi is byte palletIndex &&
            m.TryGetValue("error", out var e) && e is byte[] { Length: > 0 } errBytes)
        {
            var pallet = metadata.FindPallet(palletIndex);
            if (pallet?.ErrorsType is uint et && metadata.Types.TryGetValue(et, out var errType))
            {
                var variant = errType.Variants.FirstOrDefault(x => x.Index == errBytes[0]);
                if (variant is not null) return $"{pallet.Name}.{variant.Name}";
            }
            return $"Module(pallet {palletIndex}, error {errBytes[0]})";
        }
        return v.Value is ScaleVariant inner ? $"{v.Name}.{inner.Name}" : v.ToString();
    }

    /// <summary>
    /// Outcome of extrinsic <paramref name="index"/>: success, or the described dispatch error; null when the block has
    /// no <c>ExtrinsicSuccess</c>/<c>ExtrinsicFailed</c> event for it.
    /// </summary>
    public static (bool Success, string? Error)? Outcome(RuntimeMetadata metadata, IEnumerable<SubstrateEvent> events, uint index)
    {
        foreach (var ev in events.Where(e => e.ExtrinsicIndex == index))
        {
            if (ev.Is("System", "ExtrinsicSuccess")) return (true, null);
            if (ev.Is("System", "ExtrinsicFailed")) return (false, DescribeDispatchError(metadata, ev["dispatch_error"]));
        }
        return null;
    }
}
