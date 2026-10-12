using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Text;

namespace Crypto.Net.Polkadot;

/// <summary>
/// Encodes .NET values as SCALE following the runtime type registry; the inverse of <see cref="ScaleDecoder"/>.
/// <list type="bullet">
/// <item>composites: a dictionary keyed by field name, or any object whose properties match the field names
/// (<c>snake_case</c> fields also match <c>PascalCase</c> properties); a single unnamed field takes the value itself;</item>
/// <item>enums: a <see cref="ScaleVariant"/>, or the variant name as a string for variants without fields;</item>
/// <item>sequences and arrays: any <see cref="IEnumerable"/> (<c>byte[]</c> for <c>u8</c>);</item>
/// <item>account ids (<c>AccountId32</c>, <c>MultiAddress::Id</c>): an SS58 string or 32 raw bytes.</item>
/// </list>
/// </summary>
public static class ScaleEncoder
{
    public static byte[] Encode(RuntimeMetadata metadata, uint typeId, object? value)
    {
        var w = new ScaleWriter();
        Encode(metadata, typeId, value, w, 0);
        return w.ToArray();
    }

    /// <summary>
    /// Encodes <c>pallet.call(args)</c> with arguments by name, e.g.
    /// <c>EncodeCall(md, "Staking", "nominate", new { targets = new[] { "1…", "1…" } })</c>.
    /// </summary>
    public static byte[] EncodeCall(RuntimeMetadata metadata, string pallet, string call, object? args = null)
    {
        if (!metadata.Pallets.TryGetValue(pallet, out var p) || p.CallsType is not uint callsType)
            throw new KeyNotFoundException($"Pallet '{pallet}' has no calls in this runtime");
        var variant = metadata.Types[callsType].Variants.FirstOrDefault(v => v.Name == call)
            ?? throw new KeyNotFoundException($"Call '{pallet}.{call}' not found in runtime metadata");
        var w = new ScaleWriter().U8(p.Index).U8(variant.Index);
        EncodeFields(metadata, variant.Fields, args, w, 0, $"{pallet}.{call}");
        return w.ToArray();
    }

    private static void Encode(RuntimeMetadata md, uint typeId, object? value, ScaleWriter w, int depth)
    {
        if (depth > 64) throw new ArgumentException("SCALE value nesting too deep");
        var t = md.Types[typeId];
        switch (t.Kind)
        {
            case ScaleTypeKind.Composite:
                if (IsAccountId(t) && value is string or byte[])
                {
                    w.Bytes(AccountBytes(value));
                    return;
                }
                EncodeFields(md, t.Fields, value, w, depth, t.Path);
                return;
            case ScaleTypeKind.Variant:
            {
                ScaleVariantInfo variant;
                object? inner = null;
                if (value is ScaleVariant sv)
                {
                    variant = t.Variants.FirstOrDefault(v => v.Name == sv.Name) ?? throw new ArgumentException($"{t.Path} has no variant '{sv.Name}'");
                    inner = sv.Value;
                }
                else if (value is string name && t.Variants.FirstOrDefault(v => v.Name == name) is { } byName)
                {
                    variant = byName;
                }
                else if (t.Path.EndsWith("MultiAddress", StringComparison.Ordinal) && value is string or byte[])
                {
                    variant = t.Variants.First(v => v.Name == "Id");
                    inner = value;
                }
                else if (t.Path == "Option" || t.Variants.Count == 2 && t.Variants.Any(v => v.Name == "None") && t.Variants.Any(v => v.Name == "Some"))
                {
                    variant = value is null ? t.Variants.First(v => v.Name == "None") : t.Variants.First(v => v.Name == "Some");
                    inner = value;
                }
                else
                {
                    throw new ArgumentException($"Expected a ScaleVariant or variant name for {t.Path}, got {value?.GetType().Name ?? "null"}");
                }
                w.U8(variant.Index);
                if (variant.Fields.Count > 0) EncodeFields(md, variant.Fields, inner, w, depth, $"{t.Path}::{variant.Name}");
                return;
            }
            case ScaleTypeKind.Sequence:
            {
                var element = t.Children[0];
                if (IsU8(md, element))
                {
                    byte[] bytes = Bytes(value);
                    w.Compact(bytes.Length).Bytes(bytes);
                    return;
                }
                var items = Items(value);
                w.Compact(items.Count);
                foreach (var item in items) Encode(md, element, item, w, depth + 1);
                return;
            }
            case ScaleTypeKind.Array:
            {
                var element = t.Children[0];
                if (IsU8(md, element))
                {
                    byte[] bytes = value is string && t.ArrayLength == 32 ? AccountBytes(value) : Bytes(value);
                    if (bytes.Length != t.ArrayLength) throw new ArgumentException($"Expected {t.ArrayLength} bytes, got {bytes.Length}");
                    w.Bytes(bytes);
                    return;
                }
                var items = Items(value);
                if (items.Count != t.ArrayLength) throw new ArgumentException($"Expected {t.ArrayLength} items, got {items.Count}");
                foreach (var item in items) Encode(md, element, item, w, depth + 1);
                return;
            }
            case ScaleTypeKind.Tuple:
            {
                if (t.Children.Count == 0) return;
                var items = Items(value);
                if (items.Count != t.Children.Count) throw new ArgumentException($"Tuple expects {t.Children.Count} items");
                for (int i = 0; i < items.Count; i++) Encode(md, t.Children[i], items[i], w, depth + 1);
                return;
            }
            case ScaleTypeKind.Primitive:
                EncodePrimitive(t.Primitive, value, w);
                return;
            case ScaleTypeKind.Compact:
                w.Compact(ToBigInteger(value));
                return;
            default:
                throw new NotSupportedException($"Encoding {t.Kind} ({t.Path}) is not supported");
        }
    }

    private static void EncodeFields(RuntimeMetadata md, IReadOnlyList<ScaleField> fields, object? value, ScaleWriter w, int depth, string context)
    {
        if (fields.Count == 0) return;
        if (fields.Count == 1 && fields[0].Name is null)
        {
            Encode(md, fields[0].Type, value, w, depth + 1);
            return;
        }
        if (fields.All(f => f.Name is null))
        {
            var items = Items(value);
            if (items.Count != fields.Count) throw new ArgumentException($"{context} expects {fields.Count} values");
            for (int i = 0; i < fields.Count; i++) Encode(md, fields[i].Type, items[i], w, depth + 1);
            return;
        }
        foreach (var f in fields)
        {
            if (!TryGetMember(value, f.Name!, out var member)) throw new ArgumentException($"{context}: missing argument '{f.Name}'");
            Encode(md, f.Type, member, w, depth + 1);
        }
    }

    private static bool TryGetMember(object? value, string name, out object? member)
    {
        member = null;
        if (value is null) return false;
        if (value is IReadOnlyDictionary<string, object?> ro) return ro.TryGetValue(name, out member) || ro.TryGetValue(Pascal(name), out member);
        if (value is IDictionary dict)
        {
            if (dict.Contains(name)) { member = dict[name]; return true; }
            if (dict.Contains(Pascal(name))) { member = dict[Pascal(name)]; return true; }
            return false;
        }
        string pascal = Pascal(name);
        var prop = value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => p.Name == name || string.Equals(p.Name, pascal, StringComparison.OrdinalIgnoreCase));
        if (prop is null) return false;
        member = prop.GetValue(value);
        return true;
    }

    private static string Pascal(string snake) => string.Concat(snake.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(s => char.ToUpperInvariant(s[0]) + s[1..]));

    private static bool IsAccountId(ScaleTypeInfo t) => t.Path.EndsWith("AccountId32", StringComparison.Ordinal);

    private static bool IsU8(RuntimeMetadata md, uint id) => md.Types[id] is { Kind: ScaleTypeKind.Primitive, Primitive: ScalePrimitive.U8 };

    private static byte[] AccountBytes(object? value) => value switch
    {
        byte[] { Length: 32 } b => b,
        string s when s.StartsWith("0x", StringComparison.Ordinal) && s.Length == 66 => Convert.FromHexString(s[2..]),
        string s => Ss58Address.Decode(s).PublicKey,
        _ => throw new ArgumentException("Expected an SS58 address, 0x-hex or 32 bytes"),
    };

    private static byte[] Bytes(object? value) => value switch
    {
        byte[] b => b,
        string s when s.StartsWith("0x", StringComparison.Ordinal) => Convert.FromHexString(s[2..]),
        string s => Encoding.UTF8.GetBytes(s),
        ReadOnlyMemory<byte> m => m.ToArray(),
        IEnumerable e => e.Cast<object>().Select(x => Convert.ToByte(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray(),
        _ => throw new ArgumentException($"Cannot convert {value?.GetType().Name ?? "null"} to bytes"),
    };

    private static IList<object?> Items(object? value) => value switch
    {
        null => [],
        string => throw new ArgumentException("Expected a collection, got a string"),
        IEnumerable e => e.Cast<object?>().ToList(),
        _ => throw new ArgumentException($"Expected a collection, got {value.GetType().Name}"),
    };

    private static BigInteger ToBigInteger(object? value) => value switch
    {
        BigInteger b => b,
        Crypto.Net.Core.Amount a => a.BaseUnits,
        string s => BigInteger.Parse(s, System.Globalization.CultureInfo.InvariantCulture),
        null => throw new ArgumentException("Expected a number, got null"),
        decimal d when d == decimal.Truncate(d) => new BigInteger(d),
        double or float => throw new ArgumentException("Floating-point values are not accepted; use integers or BigInteger"),
        IConvertible c => new BigInteger(c.ToDecimal(System.Globalization.CultureInfo.InvariantCulture)),
        _ => throw new ArgumentException($"Expected a number, got {value.GetType().Name}"),
    };

    private static void EncodePrimitive(ScalePrimitive p, object? value, ScaleWriter w)
    {
        if (p == ScalePrimitive.Bool) { w.Bool(value is bool b ? b : throw new ArgumentException("Expected bool")); return; }
        if (p == ScalePrimitive.Str) { w.String(value as string ?? throw new ArgumentException("Expected string")); return; }
        if (p == ScalePrimitive.Char) { w.U32((uint)char.ConvertToUtf32(value as string ?? value?.ToString() ?? "", 0)); return; }
        BigInteger v = ToBigInteger(value);
        (int bytes, bool signed) = p switch
        {
            ScalePrimitive.U8 => (1, false), ScalePrimitive.U16 => (2, false), ScalePrimitive.U32 => (4, false), ScalePrimitive.U64 => (8, false),
            ScalePrimitive.U128 => (16, false), ScalePrimitive.U256 => (32, false),
            ScalePrimitive.I8 => (1, true), ScalePrimitive.I16 => (2, true), ScalePrimitive.I32 => (4, true), ScalePrimitive.I64 => (8, true),
            ScalePrimitive.I128 => (16, true), _ => (32, true),
        };
        BigInteger min = signed ? -(BigInteger.One << (bytes * 8 - 1)) : BigInteger.Zero;
        BigInteger max = signed ? (BigInteger.One << (bytes * 8 - 1)) - 1 : (BigInteger.One << (bytes * 8)) - 1;
        if (v < min || v > max) throw new ArgumentOutOfRangeException(nameof(value), $"{v} does not fit in {p}");
        var buffer = new byte[bytes];
        if (v.Sign < 0) Array.Fill(buffer, (byte)0xFF);
        v.TryWriteBytes(buffer, out _, isUnsigned: !signed, isBigEndian: false);
        w.Bytes(buffer);
    }
}

/// <summary>Staking and nomination-pool calls (Polkadot moved staking to Asset Hub; use that chain's metadata).</summary>
public static class PolkadotStaking
{
    /// <summary><c>Staking.bond(value, payee)</c>; <paramref name="payee"/> is Staked, Stash, Account(addr) or None.</summary>
    public static byte[] Bond(RuntimeMetadata md, BigInteger value, string payee = "Staked", string? payeeAccount = null) =>
        ScaleEncoder.EncodeCall(md, "Staking", "bond", new Dictionary<string, object?> { ["value"] = value, ["payee"] = Payee(payee, payeeAccount) });

    public static byte[] BondExtra(RuntimeMetadata md, BigInteger maxAdditional) =>
        ScaleEncoder.EncodeCall(md, "Staking", "bond_extra", new Dictionary<string, object?> { ["max_additional"] = maxAdditional });

    /// <summary><c>Staking.nominate(targets)</c> with SS58 validator addresses.</summary>
    public static byte[] Nominate(RuntimeMetadata md, IEnumerable<string> validators) =>
        ScaleEncoder.EncodeCall(md, "Staking", "nominate", new Dictionary<string, object?> { ["targets"] = validators.ToArray() });

    public static byte[] Unbond(RuntimeMetadata md, BigInteger value) =>
        ScaleEncoder.EncodeCall(md, "Staking", "unbond", new Dictionary<string, object?> { ["value"] = value });

    public static byte[] WithdrawUnbonded(RuntimeMetadata md, uint numSlashingSpans = 0) =>
        ScaleEncoder.EncodeCall(md, "Staking", "withdraw_unbonded", new Dictionary<string, object?> { ["num_slashing_spans"] = numSlashingSpans });

    public static byte[] Chill(RuntimeMetadata md) => ScaleEncoder.EncodeCall(md, "Staking", "chill");

    public static byte[] SetPayee(RuntimeMetadata md, string payee, string? payeeAccount = null) =>
        ScaleEncoder.EncodeCall(md, "Staking", "set_payee", new Dictionary<string, object?> { ["payee"] = Payee(payee, payeeAccount) });

    /// <summary><c>NominationPools.join(amount, pool_id)</c>.</summary>
    public static byte[] JoinPool(RuntimeMetadata md, BigInteger amount, uint poolId) =>
        ScaleEncoder.EncodeCall(md, "NominationPools", "join", new Dictionary<string, object?> { ["amount"] = amount, ["pool_id"] = poolId });

    /// <summary><c>NominationPools.bond_extra(FreeBalance(amount))</c>.</summary>
    public static byte[] PoolBondExtra(RuntimeMetadata md, BigInteger amount) =>
        ScaleEncoder.EncodeCall(md, "NominationPools", "bond_extra", new Dictionary<string, object?> { ["extra"] = new ScaleVariant("FreeBalance", 0, amount) });

    public static byte[] PoolClaimPayout(RuntimeMetadata md) => ScaleEncoder.EncodeCall(md, "NominationPools", "claim_payout");

    /// <summary><c>NominationPools.unbond(member, points)</c>.</summary>
    public static byte[] PoolUnbond(RuntimeMetadata md, string member, BigInteger points) =>
        ScaleEncoder.EncodeCall(md, "NominationPools", "unbond", new Dictionary<string, object?> { ["member_account"] = member, ["unbonding_points"] = points });

    public static byte[] PoolWithdrawUnbonded(RuntimeMetadata md, string member, uint numSlashingSpans = 0) =>
        ScaleEncoder.EncodeCall(md, "NominationPools", "withdraw_unbonded", new Dictionary<string, object?> { ["member_account"] = member, ["num_slashing_spans"] = numSlashingSpans });

    private static object Payee(string kind, string? account) =>
        kind == "Account" ? new ScaleVariant("Account", 0, account ?? throw new ArgumentException("payeeAccount is required for Account")) : kind;
}
