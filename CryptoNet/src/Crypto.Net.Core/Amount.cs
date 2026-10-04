using System.Globalization;
using System.Numerics;

namespace Crypto.Net.Core;

/// <summary>
/// An exact token amount: an integer number of base units (wei, satoshi, lamports, planck, uatom…)
/// plus the number of decimals of the display unit. No floating point is ever involved.
/// </summary>
public readonly struct Amount : IComparable<Amount>, IEquatable<Amount>, ISpanFormattable
{
    public Amount(BigInteger baseUnits, int decimals)
    {
        if (decimals is < 0 or > 77)
            throw new ArgumentOutOfRangeException(nameof(decimals), "Decimals must be between 0 and 77");
        BaseUnits = baseUnits;
        Decimals = decimals;
    }

    /// <summary>Integer value in the smallest unit.</summary>
    public BigInteger BaseUnits { get; }

    /// <summary>Number of decimals of the display unit (18 for ETH, 8 for BTC, 9 for SOL…).</summary>
    public int Decimals { get; }

    [Obsolete("Use BaseUnits")]
    public BigInteger BaseValue => BaseUnits;

    public bool IsZero => BaseUnits.IsZero;
    public bool IsNegative => BaseUnits.Sign < 0;

    public static Amount Zero(int decimals = 18) => new(BigInteger.Zero, decimals);

    #region Factories

    public static Amount FromWei(BigInteger wei) => new(wei, 18);
    public static Amount FromGwei(decimal gwei) => FromWei(FromDecimal(gwei, 9).BaseUnits);
    public static Amount FromEther(decimal ether) => FromDecimal(ether, 18);

    public static Amount FromSatoshi(long satoshi) => new(satoshi, 8);
    public static Amount FromBtc(decimal btc) => FromDecimal(btc, 8);

    public static Amount FromLamports(ulong lamports) => new(lamports, 9);
    public static Amount FromSol(decimal sol) => FromDecimal(sol, 9);

    public static Amount FromPlanck(BigInteger planck, int decimals = 10) => new(planck, decimals);
    public static Amount FromDot(decimal dot) => FromDecimal(dot, 10);

    public static Amount FromMicroAtom(BigInteger uatom) => new(uatom, 6);
    public static Amount FromAtom(decimal atom) => FromDecimal(atom, 6);

    /// <summary>Converts a decimal display value. Throws if it has more fractional digits than <paramref name="decimals"/>.</summary>
    public static Amount FromDecimal(decimal value, int decimals) =>
        Parse(value.ToString(CultureInfo.InvariantCulture), decimals);

    /// <summary>
    /// Parses a display string such as <c>"1.5"</c> or <c>"-0.000000000000000001"</c> exactly.
    /// Throws <see cref="FormatException"/> when the precision exceeds <paramref name="decimals"/>.
    /// </summary>
    public static Amount Parse(string value, int decimals)
    {
        if (!TryParse(value, decimals, out var amount, out string? error))
            throw new FormatException(error);
        return amount;
    }

    public static bool TryParse(string? value, int decimals, out Amount amount) => TryParse(value, decimals, out amount, out _);

    private static bool TryParse(string? value, int decimals, out Amount amount, out string? error)
    {
        amount = default;
        error = null;
        if (string.IsNullOrWhiteSpace(value)) { error = "Amount is empty"; return false; }

        ReadOnlySpan<char> s = value.AsSpan().Trim();
        bool negative = false;
        if (s[0] is '-' or '+')
        {
            negative = s[0] == '-';
            s = s[1..];
        }

        int dot = s.IndexOf('.');
        ReadOnlySpan<char> intPart = dot < 0 ? s : s[..dot];
        ReadOnlySpan<char> fracPart = dot < 0 ? [] : s[(dot + 1)..];
        fracPart = fracPart.TrimEnd('0');

        if (intPart.IsEmpty && fracPart.IsEmpty && dot < 0) { error = $"Invalid amount '{value}'"; return false; }
        foreach (char c in intPart) if (!char.IsAsciiDigit(c)) { error = $"Invalid amount '{value}'"; return false; }
        foreach (char c in fracPart) if (!char.IsAsciiDigit(c)) { error = $"Invalid amount '{value}'"; return false; }
        if (fracPart.Length > decimals) { error = $"'{value}' has more than {decimals} decimal places"; return false; }

        BigInteger whole = intPart.IsEmpty ? BigInteger.Zero : BigInteger.Parse(intPart, NumberStyles.None, CultureInfo.InvariantCulture);
        BigInteger frac = fracPart.IsEmpty ? BigInteger.Zero : BigInteger.Parse(fracPart, NumberStyles.None, CultureInfo.InvariantCulture);
        BigInteger units = whole * BigInteger.Pow(10, decimals) + frac * BigInteger.Pow(10, decimals - fracPart.Length);
        amount = new Amount(negative ? -units : units, decimals);
        return true;
    }

    #endregion

    /// <summary>Re-expresses the amount with a different number of decimals (must not lose precision).</summary>
    public Amount WithDecimals(int decimals)
    {
        if (decimals == Decimals) return this;
        if (decimals > Decimals)
            return new Amount(BaseUnits * BigInteger.Pow(10, decimals - Decimals), decimals);
        var divisor = BigInteger.Pow(10, Decimals - decimals);
        var q = BigInteger.DivRem(BaseUnits, divisor, out var r);
        if (!r.IsZero) throw new InvalidOperationException("Conversion would lose precision");
        return new Amount(q, decimals);
    }

    /// <summary>Approximate decimal value (may throw <see cref="OverflowException"/> for huge values).</summary>
    public decimal ToDecimal() => decimal.Parse(ToString(), NumberStyles.Number, CultureInfo.InvariantCulture);

    /// <summary>Exact display string with trailing zeros removed, e.g. <c>1.5</c>.</summary>
    public override string ToString() => ToString(null, null);

    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        BigInteger abs = BigInteger.Abs(BaseUnits);
        string digits = abs.ToString(CultureInfo.InvariantCulture);
        string sign = BaseUnits.Sign < 0 ? "-" : "";
        if (Decimals == 0) return sign + digits;

        digits = digits.PadLeft(Decimals + 1, '0');
        string whole = digits[..^Decimals];
        string frac = digits[^Decimals..].TrimEnd('0');
        return frac.Length == 0 ? sign + whole : $"{sign}{whole}.{frac}";
    }

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        string s = ToString();
        charsWritten = 0;
        if (s.Length > destination.Length) return false;
        s.CopyTo(destination);
        charsWritten = s.Length;
        return true;
    }

    public string ToString(string symbol) => $"{ToString()} {symbol}";

    [Obsolete("Use ToString(symbol)")]
    public string ToStringWithUnit(string symbol) => ToString(symbol);

    #region Operators

    private static void SameScale(Amount a, Amount b)
    {
        if (a.Decimals != b.Decimals) throw new InvalidOperationException($"Cannot combine amounts with {a.Decimals} and {b.Decimals} decimals");
    }

    public static Amount operator +(Amount a, Amount b) { SameScale(a, b); return new(a.BaseUnits + b.BaseUnits, a.Decimals); }
    public static Amount operator -(Amount a, Amount b) { SameScale(a, b); return new(a.BaseUnits - b.BaseUnits, a.Decimals); }
    public static Amount operator -(Amount a) => new(-a.BaseUnits, a.Decimals);
    public static Amount operator *(Amount a, BigInteger factor) => new(a.BaseUnits * factor, a.Decimals);
    public static Amount operator /(Amount a, BigInteger divisor) => new(a.BaseUnits / divisor, a.Decimals);

    public static bool operator ==(Amount left, Amount right) => left.Equals(right);
    public static bool operator !=(Amount left, Amount right) => !left.Equals(right);
    public static bool operator <(Amount left, Amount right) => left.CompareTo(right) < 0;
    public static bool operator <=(Amount left, Amount right) => left.CompareTo(right) <= 0;
    public static bool operator >(Amount left, Amount right) => left.CompareTo(right) > 0;
    public static bool operator >=(Amount left, Amount right) => left.CompareTo(right) >= 0;

    /// <summary>Value equality across scales: 1.0 ETH (18) equals 1 ETH expressed with 9 decimals × 10^9.</summary>
    public bool Equals(Amount other) => CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is Amount other && Equals(other);

    public override int GetHashCode()
    {
        // Normalize trailing zeros so equal values hash identically.
        BigInteger v = BaseUnits;
        int d = Decimals;
        while (d > 0 && !v.IsZero && (v % 10).IsZero) { v /= 10; d--; }
        if (v.IsZero) d = 0;
        return HashCode.Combine(v, d);
    }

    public int CompareTo(Amount other)
    {
        if (Decimals == other.Decimals) return BaseUnits.CompareTo(other.BaseUnits);
        int max = Math.Max(Decimals, other.Decimals);
        return (BaseUnits * BigInteger.Pow(10, max - Decimals)).CompareTo(other.BaseUnits * BigInteger.Pow(10, max - other.Decimals));
    }

    #endregion
}
