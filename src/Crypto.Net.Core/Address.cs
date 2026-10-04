namespace Crypto.Net.Core;

/// <summary>
/// A chain-scoped address string. Hex (<c>0x…</c>) addresses compare case-insensitively; Base58, Bech32
/// and SS58 addresses compare exactly because their case is significant.
/// </summary>
public readonly struct Address : IEquatable<Address>
{
    public string Value { get; }
    public ChainId Chain { get; }

    public Address(string value, ChainId chain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim();
        Chain = chain;
    }

    private bool IsHex => Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);

    public bool Equals(Address other) =>
        Chain.Equals(other.Chain) &&
        string.Equals(Value, other.Value, IsHex ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is Address other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(IsHex ? Value.ToLowerInvariant() : Value, Chain);

    public override string ToString() => Value;

    public static bool operator ==(Address left, Address right) => left.Equals(right);
    public static bool operator !=(Address left, Address right) => !left.Equals(right);

    public static implicit operator string(Address address) => address.Value;
}
