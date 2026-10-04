namespace Crypto.Net.Core;

/// <summary>
/// Immutable transaction hash representation.
/// </summary>
public readonly struct TxHash : IEquatable<TxHash>
{
    public string Value { get; }

    public TxHash(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim();
    }

    public static TxHash FromBytes(ReadOnlySpan<byte> bytes, bool prefix0x = true)
    {
        string hex = Convert.ToHexStringLower(bytes);
        return new TxHash(prefix0x ? "0x" + hex : hex);
    }

    public bool Equals(TxHash other) => string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);
    public override bool Equals(object? obj) => obj is TxHash other && Equals(other);
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
    public override string ToString() => Value;

    public static bool operator ==(TxHash left, TxHash right) => left.Equals(right);
    public static bool operator !=(TxHash left, TxHash right) => !left.Equals(right);

    public static implicit operator string(TxHash hash) => hash.Value;
}
