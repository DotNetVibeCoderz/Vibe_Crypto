using System.Security.Cryptography;

namespace Crypto.Net.Native;

/// <summary>
/// Holds secret material (private keys, seeds) in a pinned array that the GC never moves or copies,
/// and zeroes it on <see cref="Dispose"/> (or finalization as a last resort).
/// </summary>
public sealed class SecureBuffer : IDisposable
{
    private readonly byte[] _buffer;
    private volatile bool _disposed;

    public SecureBuffer(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _buffer = GC.AllocateArray<byte>(length, pinned: true);
    }

    public int Length => _buffer.Length;

    public bool IsDisposed => _disposed;

    public ReadOnlySpan<byte> Span
    {
        get
        {
            ThrowIfDisposed();
            return _buffer;
        }
    }

    public Span<byte> MutableSpan
    {
        get
        {
            ThrowIfDisposed();
            return _buffer;
        }
    }

    public static SecureBuffer FromBytes(ReadOnlySpan<byte> source)
    {
        var buffer = new SecureBuffer(source.Length);
        source.CopyTo(buffer._buffer);
        return buffer;
    }

    /// <summary>Takes ownership of <paramref name="source"/>: copies it into secure storage and zeroes the original.</summary>
    public static SecureBuffer FromBytesAndClear(byte[] source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var buffer = FromBytes(source);
        CryptographicOperations.ZeroMemory(source);
        return buffer;
    }

    public static SecureBuffer FromHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        ReadOnlySpan<char> chars = hex.AsSpan().Trim();
        if (chars.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) chars = chars[2..];
        if (chars.Length % 2 != 0) throw new FormatException("Hex string must have an even length");

        var buffer = new SecureBuffer(chars.Length / 2);
        if (Convert.FromHexString(chars, buffer._buffer, out _, out _) != System.Buffers.OperationStatus.Done)
        {
            buffer.Dispose();
            throw new FormatException("Invalid hex string");
        }
        return buffer;
    }

    public SecureBuffer Clone() => FromBytes(Span);

    public void CopyTo(Span<byte> destination) => Span.CopyTo(destination);

    /// <summary>Returns an unprotected copy. The caller is responsible for zeroing it.</summary>
    public byte[] ToArray() => Span.ToArray();

    /// <summary>Constant-time comparison.</summary>
    public bool ContentEquals(ReadOnlySpan<byte> other) => CryptographicOperations.FixedTimeEquals(Span, other);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        CryptographicOperations.ZeroMemory(_buffer);
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    ~SecureBuffer()
    {
        CryptographicOperations.ZeroMemory(_buffer);
    }

    public override string ToString() => $"SecureBuffer({Length} bytes)";
}
