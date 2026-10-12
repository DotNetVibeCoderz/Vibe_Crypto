using System.Buffers.Binary;
using System.Net.Sockets;
using HidSharp;

namespace Crypto.Net.Ledger;

/// <summary>A device answered with an error status word.</summary>
public sealed class LedgerException(ushort statusWord, string message) : Exception($"{message} (0x{statusWord:x4})")
{
    public ushort StatusWord { get; } = statusWord;

    /// <summary>True when the user rejected the operation on the device.</summary>
    public bool IsUserRejection => StatusWord is 0x6985 or 0x5501;

    internal static string Describe(ushort sw) => sw switch
    {
        0x6985 or 0x5501 => "Rejected on the device",
        0x5515 => "The device is locked; unlock it with your PIN",
        0x6511 or 0x6e00 or 0x6e01 or 0x6d00 => "The required app is not open on the device",
        0x6a80 => "The device rejected the data (invalid or unsupported)",
        0x6a84 or 0x6a85 => "Not enough memory on the device for this request",
        0x6b00 => "Invalid parameter (P1/P2)",
        0x6982 => "Blind signing or the required setting is disabled in the app",
        0x6700 => "Incorrect length",
        _ => "Device error",
    };
}

/// <summary>Sends APDUs to a Ledger. Implementations: <see cref="HidLedgerTransport"/>, <see cref="SpeculosTransport"/>, <see cref="ReplayLedgerTransport"/>.</summary>
public interface ILedgerTransport : IDisposable
{
    /// <summary>Exchanges one APDU and returns the response including the 2-byte status word.</summary>
    Task<byte[]> ExchangeRawAsync(byte[] apdu, CancellationToken ct = default);
}

public static class LedgerTransportExtensions
{
    /// <summary>Sends <c>CLA INS P1 P2 Lc data</c>; returns the data part, or throws <see cref="LedgerException"/>.</summary>
    public static async Task<byte[]> ExchangeAsync(this ILedgerTransport transport, byte cla, byte ins, byte p1, byte p2, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (data.Length > 255) throw new ArgumentException("APDU data is limited to 255 bytes", nameof(data));
        byte[] apdu = [cla, ins, p1, p2, (byte)data.Length, .. data.Span];
        byte[] response = await transport.ExchangeRawAsync(apdu, ct).ConfigureAwait(false);
        if (response.Length < 2) throw new LedgerException(0, "Empty response from the device");
        ushort sw = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(response.Length - 2));
        if (sw != 0x9000) throw new LedgerException(sw, LedgerException.Describe(sw));
        return response[..^2];
    }
}

/// <summary>Ledger HID framing: 64-byte reports with channel, tag 0x05, sequence number and a length prefix.</summary>
public static class LedgerHidFraming
{
    public const int PacketSize = 64;
    private const byte Tag = 0x05;

    public static IReadOnlyList<byte[]> Wrap(ReadOnlySpan<byte> apdu, ushort channel = 0x0101)
    {
        var packets = new List<byte[]>();
        byte[] payload = [(byte)(apdu.Length >> 8), (byte)apdu.Length, .. apdu];
        int offset = 0;
        for (ushort seq = 0; offset < payload.Length || seq == 0; seq++)
        {
            var packet = new byte[PacketSize];
            BinaryPrimitives.WriteUInt16BigEndian(packet, channel);
            packet[2] = Tag;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(3), seq);
            int n = Math.Min(PacketSize - 5, payload.Length - offset);
            payload.AsSpan(offset, n).CopyTo(packet.AsSpan(5));
            offset += n;
            packets.Add(packet);
        }
        return packets;
    }

    /// <summary>Reassembles a response; returns null until all packets have arrived.</summary>
    public static byte[]? Unwrap(IReadOnlyList<byte[]> packets, ushort channel = 0x0101)
    {
        if (packets.Count == 0) return null;
        var data = new List<byte>();
        int expected = -1;
        for (int i = 0; i < packets.Count; i++)
        {
            var p = packets[i];
            if (p.Length < 5 || BinaryPrimitives.ReadUInt16BigEndian(p) != channel || p[2] != Tag || BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(3)) != i)
                throw new InvalidDataException("Unexpected HID packet");
            int start = 5;
            if (i == 0)
            {
                expected = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(5));
                start = 7;
            }
            data.AddRange(p.AsSpan(start).ToArray());
        }
        return data.Count >= expected ? [.. data.Take(expected)] : null;
    }
}

/// <summary>USB HID transport (Windows, macOS, Linux). On Linux, install Ledger's udev rules first.</summary>
public sealed class HidLedgerTransport : ILedgerTransport
{
    public const int LedgerVendorId = 0x2c97;
    private readonly HidStream _stream;

    private HidLedgerTransport(HidStream stream) => _stream = stream;

    /// <summary>Connected Ledger devices (their APDU interface).</summary>
    public static IReadOnlyList<HidDevice> ListDevices() =>
        [.. DeviceList.Local.GetHidDevices(LedgerVendorId).Where(d => d.GetMaxInputReportLength() >= 64 && IsApduInterface(d))];

    private static bool IsApduInterface(HidDevice d)
    {
        try
        {
            return d.GetReportDescriptor().DeviceItems.Any(i => i.Usages.GetAllValues().Any(u => (u >> 16) == 0xffa0));
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return true; // descriptor unavailable; let the open attempt decide
        }
    }

    /// <summary>Opens the first connected Ledger; throws when none is connected.</summary>
    public static HidLedgerTransport Open()
    {
        var device = ListDevices().FirstOrDefault() ?? throw new InvalidOperationException("No Ledger device found. Connect it, unlock it and open the app.");
        var stream = device.Open();
        stream.ReadTimeout = Timeout.Infinite; // the user may take a while to confirm on the device
        return new HidLedgerTransport(stream);
    }

    public async Task<byte[]> ExchangeRawAsync(byte[] apdu, CancellationToken ct = default)
    {
        foreach (var packet in LedgerHidFraming.Wrap(apdu))
            await _stream.WriteAsync((byte[])[0x00, .. packet], ct).ConfigureAwait(false); // report id 0
        var received = new List<byte[]>();
        while (true)
        {
            var buffer = new byte[LedgerHidFraming.PacketSize + 1];
            int n = await _stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            byte[] packet = n == LedgerHidFraming.PacketSize + 1 ? buffer[1..] : buffer[..LedgerHidFraming.PacketSize];
            received.Add(packet);
            if (LedgerHidFraming.Unwrap(received) is { } response) return response;
        }
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>
/// Transport for the Speculos emulator's APDU port (default 9999): each APDU is sent with a 4-byte big-endian length;
/// the reply is a 4-byte length, the data, then the 2-byte status word.
/// </summary>
public sealed class SpeculosTransport : ILedgerTransport
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;

    private SpeculosTransport(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    public static async Task<SpeculosTransport> ConnectAsync(string host = "127.0.0.1", int port = 9999, CancellationToken ct = default)
    {
        var client = new TcpClient();
        await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
        return new SpeculosTransport(client);
    }

    public async Task<byte[]> ExchangeRawAsync(byte[] apdu, CancellationToken ct = default)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, apdu.Length);
        await _stream.WriteAsync(header, ct).ConfigureAwait(false);
        await _stream.WriteAsync(apdu, ct).ConfigureAwait(false);
        await _stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var data = new byte[BinaryPrimitives.ReadInt32BigEndian(header) + 2];
        await _stream.ReadExactlyAsync(data, ct).ConfigureAwait(false);
        return data;
    }

    public void Dispose()
    {
        _stream.Dispose();
        _client.Dispose();
    }
}

/// <summary>
/// Replays a recorded exchange (the <c>=&gt; apdu</c> / <c>&lt;= response</c> format of Ledger's own test suites) and
/// fails on any deviation. For unit tests without a device.
/// </summary>
public sealed class ReplayLedgerTransport : ILedgerTransport
{
    private readonly Queue<(byte[] Apdu, byte[] Response)> _script = new();

    public ReplayLedgerTransport(string recording)
    {
        byte[]? pending = null;
        foreach (string raw in recording.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("=>", StringComparison.Ordinal)) pending = Convert.FromHexString(line[2..].Trim());
            else if (line.StartsWith("<=", StringComparison.Ordinal))
            {
                if (pending is null) throw new FormatException("Response without a request");
                _script.Enqueue((pending, Convert.FromHexString(line[2..].Trim())));
                pending = null;
            }
        }
    }

    public bool Completed => _script.Count == 0;

    public Task<byte[]> ExchangeRawAsync(byte[] apdu, CancellationToken ct = default)
    {
        if (!_script.TryDequeue(out var step)) throw new InvalidOperationException($"Unexpected APDU {Convert.ToHexStringLower(apdu)}");
        if (!step.Apdu.AsSpan().SequenceEqual(apdu))
            throw new InvalidOperationException($"APDU mismatch:\n expected {Convert.ToHexStringLower(step.Apdu)}\n actual   {Convert.ToHexStringLower(apdu)}");
        return Task.FromResult(step.Response);
    }

    public void Dispose() { }
}
