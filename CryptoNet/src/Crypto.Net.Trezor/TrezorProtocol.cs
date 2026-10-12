using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace Crypto.Net.Trezor;

/// <summary>A device error (<c>Failure</c> message): code and the device's message.</summary>
public sealed class TrezorException(int code, string message) : Exception($"Trezor failure {code}: {message}")
{
    public int Code { get; } = code;

    /// <summary>The user pressed Cancel or rejected on the device.</summary>
    public bool IsUserRejection => Code is 4 or 2;   // ActionCancelled, ButtonExpected
}

/// <summary>Message type numbers used by Crypto.Net (from trezor-firmware's messages.proto).</summary>
public static class TrezorMessageType
{
    public const ushort Initialize = 0, Failure = 3, Features = 17, PinMatrixRequest = 18, PinMatrixAck = 19;
    public const ushort ButtonRequest = 26, ButtonAck = 27, PassphraseRequest = 41, PassphraseAck = 42, GetFeatures = 55;
    public const ushort EthereumGetAddress = 56, EthereumAddress = 57, EthereumSignTx = 58, EthereumTxRequest = 59, EthereumTxAck = 60;
    public const ushort EthereumSignMessage = 64, EthereumMessageSignature = 66, DebugLinkDecision = 100;
    public const ushort EthereumSignTxEip1559 = 452, EthereumTypedDataSignature = 469, EthereumSignTypedHash = 470;
}

/// <summary>Minimal protobuf (proto2) writer for Trezor messages.</summary>
public sealed class ProtoWriter
{
    private readonly MemoryStream _ms = new();

    public ProtoWriter Varint(int field, ulong value)
    {
        Key(field, 0);
        WriteVarint(value);
        return this;
    }

    public ProtoWriter Bool(int field, bool value) => Varint(field, value ? 1UL : 0UL);

    public ProtoWriter Bytes(int field, ReadOnlySpan<byte> value)
    {
        Key(field, 2);
        WriteVarint((ulong)value.Length);
        _ms.Write(value);
        return this;
    }

    public ProtoWriter String(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

    /// <summary>A repeated <c>uint32</c> written unpacked (one key per element), as proto2 does by default.</summary>
    public ProtoWriter RepeatedUInt32(int field, IEnumerable<uint> values)
    {
        foreach (uint v in values) Varint(field, v);
        return this;
    }

    public byte[] ToArray() => _ms.ToArray();

    private void Key(int field, int wireType) => WriteVarint((ulong)((field << 3) | wireType));

    private void WriteVarint(ulong v)
    {
        while (v >= 0x80)
        {
            _ms.WriteByte((byte)(v | 0x80));
            v >>= 7;
        }
        _ms.WriteByte((byte)v);
    }
}

/// <summary>Reads protobuf fields into a lookup of field number → values (varints as ulong, length-delimited as bytes).</summary>
public sealed class ProtoMessage
{
    private readonly Dictionary<int, List<object>> _fields = [];

    public static ProtoMessage Parse(ReadOnlySpan<byte> data)
    {
        var m = new ProtoMessage();
        int pos = 0;
        while (pos < data.Length)
        {
            ulong key = ReadVarint(data, ref pos);
            int field = (int)(key >> 3);
            object value = (int)(key & 7) switch
            {
                0 => ReadVarint(data, ref pos),
                1 => Take(data, ref pos, 8),
                2 => Take(data, ref pos, checked((int)ReadVarint(data, ref pos))),
                5 => Take(data, ref pos, 4),
                var w => throw new FormatException($"Unsupported protobuf wire type {w}"),
            };
            if (!m._fields.TryGetValue(field, out var list)) m._fields[field] = list = [];
            list.Add(value);
        }
        return m;
    }

    public bool Has(int field) => _fields.ContainsKey(field);
    public ulong? Varint(int field) => _fields.TryGetValue(field, out var l) && l[^1] is ulong v ? v : null;
    public byte[]? Bytes(int field) => _fields.TryGetValue(field, out var l) && l[^1] is byte[] b ? b : null;
    public string? String(int field) => Bytes(field) is { } b ? Encoding.UTF8.GetString(b) : null;

    private static byte[] Take(ReadOnlySpan<byte> data, ref int pos, int n)
    {
        if (n < 0 || pos + n > data.Length) throw new FormatException("Protobuf data is truncated");
        byte[] b = data.Slice(pos, n).ToArray();
        pos += n;
        return b;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int pos)
    {
        ulong v = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            if (pos >= data.Length) throw new FormatException("Protobuf varint is truncated");
            byte b = data[pos++];
            v |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0) return v;
        }
        throw new FormatException("Protobuf varint is too long");
    }
}

/// <summary>Moves one encoded message (type and protobuf payload) to the device and returns its reply.</summary>
public interface ITrezorTransport : IDisposable
{
    Task<(ushort Type, byte[] Payload)> CallAsync(ushort type, byte[] payload, CancellationToken ct = default);
}

/// <summary>
/// Protocol v1 framing over 64-byte reports: the first carries <c>"?##" ‖ type (u16 BE) ‖ length (u32 BE)</c>, every
/// report starts with <c>'?'</c>.
/// </summary>
public static class TrezorFraming
{
    public const int ReportSize = 64;

    public static IReadOnlyList<byte[]> Wrap(ushort type, ReadOnlySpan<byte> payload)
    {
        byte[] stream = new byte[8 + payload.Length];
        stream[0] = (byte)'#';
        stream[1] = (byte)'#';
        BinaryPrimitives.WriteUInt16BigEndian(stream.AsSpan(2), type);
        BinaryPrimitives.WriteUInt32BigEndian(stream.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(stream.AsSpan(8));
        var reports = new List<byte[]>();
        for (int offset = 0; offset < stream.Length; offset += ReportSize - 1)
        {
            byte[] report = new byte[ReportSize];
            report[0] = (byte)'?';
            stream.AsSpan(offset, Math.Min(ReportSize - 1, stream.Length - offset)).CopyTo(report.AsSpan(1));
            reports.Add(report);
        }
        return reports;
    }

    /// <summary>Reassembles a reply; <paramref name="readReport"/> returns the next 64-byte report.</summary>
    public static async Task<(ushort Type, byte[] Payload)> UnwrapAsync(Func<CancellationToken, Task<byte[]>> readReport, CancellationToken ct)
    {
        byte[] first = await readReport(ct).ConfigureAwait(false);
        if (first.Length < 9 || first[0] != '?' || first[1] != '#' || first[2] != '#') throw new FormatException("Unexpected Trezor report header");
        ushort type = BinaryPrimitives.ReadUInt16BigEndian(first.AsSpan(3));
        int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(first.AsSpan(5)));
        if (length > 1 << 20) throw new FormatException("Trezor message is too large");
        var payload = new List<byte>(length);
        payload.AddRange(first.AsSpan(9, Math.Min(length, first.Length - 9)).ToArray());
        while (payload.Count < length)
        {
            byte[] next = await readReport(ct).ConfigureAwait(false);
            if (next.Length < 2 || next[0] != '?') throw new FormatException("Unexpected Trezor continuation report");
            payload.AddRange(next.AsSpan(1, Math.Min(length - payload.Count, next.Length - 1)).ToArray());
        }
        return (type, [.. payload]);
    }
}

/// <summary>The emulator's UDP port (21324 for the main interface, 21325 for the debug link).</summary>
public sealed class TrezorUdpTransport : ITrezorTransport
{
    private readonly UdpClient _udp;

    public TrezorUdpTransport(string host = "127.0.0.1", int port = 21324)
    {
        _udp = new UdpClient();
        _udp.Connect(host, port);
    }

    /// <summary>True when the emulator answers the <c>PINGPING</c> probe.</summary>
    public async Task<bool> PingAsync(TimeSpan? timeout = null)
    {
        await _udp.SendAsync("PINGPING"u8.ToArray()).ConfigureAwait(false);
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(1));
        try
        {
            var r = await _udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
            return r.Buffer.AsSpan().SequenceEqual("PONGPONG"u8);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public async Task<(ushort Type, byte[] Payload)> CallAsync(ushort type, byte[] payload, CancellationToken ct = default)
    {
        await SendAsync(type, payload, ct).ConfigureAwait(false);
        return await TrezorFraming.UnwrapAsync(async c => (await _udp.ReceiveAsync(c).ConfigureAwait(false)).Buffer, ct).ConfigureAwait(false);
    }

    /// <summary>Sends without waiting for a reply (debug-link decisions have none).</summary>
    public async Task SendAsync(ushort type, byte[] payload, CancellationToken ct = default)
    {
        foreach (var report in TrezorFraming.Wrap(type, payload)) await _udp.SendAsync(report, ct).ConfigureAwait(false);
    }

    public void Dispose() => _udp.Dispose();
}

/// <summary>
/// Trezor Bridge (bundled with Trezor Suite) on <c>http://127.0.0.1:21325</c>: the usual way to reach a USB device.
/// The first device found is acquired for the lifetime of the transport.
/// </summary>
public sealed class TrezorBridgeTransport : ITrezorTransport
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _session;

    private TrezorBridgeTransport(HttpClient http, bool ownsHttp, string session)
    {
        _http = http;
        _ownsHttp = ownsHttp;
        _session = session;
    }

    public static async Task<TrezorBridgeTransport> OpenAsync(string bridgeUrl = "http://127.0.0.1:21325", HttpClient? httpClient = null, CancellationToken ct = default)
    {
        var http = httpClient ?? new HttpClient { BaseAddress = new Uri(bridgeUrl), Timeout = TimeSpan.FromMinutes(5) };
        http.BaseAddress ??= new Uri(bridgeUrl);
        if (!http.DefaultRequestHeaders.Contains("Origin")) http.DefaultRequestHeaders.Add("Origin", "http://localhost:5000");
        try
        {
            using var devices = System.Text.Json.JsonDocument.Parse(await PostAsync(http, "enumerate", null, ct).ConfigureAwait(false));
            var device = devices.RootElement.EnumerateArray().FirstOrDefault();
            if (device.ValueKind == System.Text.Json.JsonValueKind.Undefined) throw new TrezorException(0, "No Trezor connected to Bridge");
            string path = device.GetProperty("path").GetString()!;
            string previous = device.TryGetProperty("session", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.String ? s.GetString()! : "null";
            using var acquired = System.Text.Json.JsonDocument.Parse(await PostAsync(http, $"acquire/{path}/{previous}", null, ct).ConfigureAwait(false));
            return new TrezorBridgeTransport(http, httpClient is null, acquired.RootElement.GetProperty("session").GetString()!);
        }
        catch
        {
            if (httpClient is null) http.Dispose();
            throw;
        }
    }

    public async Task<(ushort Type, byte[] Payload)> CallAsync(ushort type, byte[] payload, CancellationToken ct = default)
    {
        byte[] body = new byte[6 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(body, type);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(2), (uint)payload.Length);
        payload.CopyTo(body, 6);
        byte[] reply = System.Convert.FromHexString(await PostAsync(_http, $"call/{_session}", System.Convert.ToHexStringLower(body), ct).ConfigureAwait(false));
        if (reply.Length < 6) throw new FormatException("Short reply from Bridge");
        int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(2)));
        if (reply.Length < 6 + length) throw new FormatException("Truncated reply from Bridge");
        return (BinaryPrimitives.ReadUInt16BigEndian(reply), reply[6..(6 + length)]);
    }

    private static async Task<string> PostAsync(HttpClient http, string path, string? body, CancellationToken ct)
    {
        using var content = new StringContent(body ?? "", Encoding.ASCII);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        using var r = await http.PostAsync(path, content, ct).ConfigureAwait(false);
        string text = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!r.IsSuccessStatusCode) throw new TrezorException((int)r.StatusCode, $"Bridge {path}: {text}");
        return text;
    }

    public void Dispose()
    {
        try
        {
            using var content = new StringContent("");
            _http.PostAsync($"release/{_session}", content).GetAwaiter().GetResult().Dispose();
        }
        catch (HttpRequestException)
        {
        }
        if (_ownsHttp) _http.Dispose();
    }
}

/// <summary>Replays a scripted conversation for unit tests: each call must match the next expected request.</summary>
public sealed class ReplayTrezorTransport(IEnumerable<(ushort Type, byte[] Payload, ushort ReplyType, byte[] Reply)> script) : ITrezorTransport
{
    private readonly Queue<(ushort Type, byte[] Payload, ushort ReplyType, byte[] Reply)> _script = new(script);

    public bool Completed => _script.Count == 0;

    public Task<(ushort Type, byte[] Payload)> CallAsync(ushort type, byte[] payload, CancellationToken ct = default)
    {
        if (_script.Count == 0) throw new InvalidOperationException($"Unexpected message {type}");
        var next = _script.Dequeue();
        if (next.Type != type || !next.Payload.AsSpan().SequenceEqual(payload))
            throw new InvalidOperationException($"Expected message {next.Type} {System.Convert.ToHexStringLower(next.Payload)}, got {type} {System.Convert.ToHexStringLower(payload)}");
        return Task.FromResult((next.ReplyType, next.Reply));
    }

    public void Dispose() { }
}
