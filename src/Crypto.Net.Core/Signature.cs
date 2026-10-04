namespace Crypto.Net.Core;

/// <summary>
/// Cryptographic signature container supporting raw bytes and recovery ID.
/// </summary>
public readonly struct Signature : IEquatable<Signature>
{
    public ReadOnlyMemory<byte> Bytes { get; }
    public byte? RecoveryId { get; }

    public Signature(ReadOnlyMemory<byte> bytes, byte? recoveryId = null)
    {
        Bytes = bytes;
        RecoveryId = recoveryId;
    }

    public string ToHex(bool prefix0x = false)
    {
        string hex = Convert.ToHexStringLower(Bytes.Span);
        return prefix0x ? "0x" + hex : hex;
    }

    public bool Equals(Signature other) =>
        Bytes.Span.SequenceEqual(other.Bytes.Span) && RecoveryId == other.RecoveryId;

    public override bool Equals(object? obj) => obj is Signature other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Bytes.Length, RecoveryId);
    public override string ToString() => ToHex(true);

    public static bool operator ==(Signature left, Signature right) => left.Equals(right);
    public static bool operator !=(Signature left, Signature right) => !left.Equals(right);
}
