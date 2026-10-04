using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Crypto.Net.Core;

/// <summary>
/// Lightweight JSON-RPC 2.0 transport over HTTP shared by the EVM, Solana and Substrate clients.
/// Errors returned by the node are raised as <see cref="RpcException"/>.
/// <para>
/// With several endpoints (e.g. Ankr, dRPC, then the public node) the client fails over when a provider
/// refuses <em>access</em> (HTTP 401/402/403/429, plan or chain not allowed, rate limited). Such requests were
/// rejected before execution, so failing over never submits a transaction twice. Ordinary RPC errors
/// (reverts, nonce too low and so on) are returned to the caller unchanged.
/// </para>
/// </summary>
public sealed class JsonRpcClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly RpcEndpoint[] _endpoints;
    private int _current;
    private long _id;

    public JsonRpcClient(string endpoint, HttpClient? httpClient = null)
        : this([new RpcEndpoint(endpoint)], httpClient)
    {
    }

    public JsonRpcClient(IReadOnlyList<RpcEndpoint> endpoints, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.Count == 0) throw new ArgumentException("At least one endpoint is required", nameof(endpoints));
        foreach (var e in endpoints) ArgumentException.ThrowIfNullOrWhiteSpace(e.Url);
        _endpoints = [.. endpoints];
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>The endpoint currently in use (after any failover).</summary>
    public RpcEndpoint CurrentEndpoint => _endpoints[Volatile.Read(ref _current)];

    public IReadOnlyList<RpcEndpoint> Endpoints => _endpoints;

    /// <summary>Calls <paramref name="method"/> and returns the raw <c>result</c> element (cloned).</summary>
    public async Task<JsonElement> CallAsync(string method, object?[]? parameters = null, CancellationToken ct = default)
    {
        long id = Interlocked.Increment(ref _id);
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters ?? [],
        });

        while (true)
        {
            int index = Volatile.Read(ref _current);
            try
            {
                return await SendAsync(_endpoints[index], method, body, ct).ConfigureAwait(false);
            }
            catch (RpcException ex) when (IsAccessDenied(ex) && index < _endpoints.Length - 1)
            {
                Interlocked.CompareExchange(ref _current, index + 1, index);
            }
        }
    }

    private async Task<JsonElement> SendAsync(RpcEndpoint endpoint, string method, byte[] body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (endpoint.Headers is not null)
            foreach (var (name, value) in endpoint.Headers) request.Headers.TryAddWithoutValidation(name, value);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new RpcException((long)response.StatusCode, $"Non-JSON response (HTTP {(int)response.StatusCode}): {Truncate(text)}", method: method);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
            {
                if (err.ValueKind != JsonValueKind.Object)
                    throw new RpcException(response.IsSuccessStatusCode ? -32603 : (long)response.StatusCode, err.ToString(), method: method);
                long code = err.TryGetProperty("code", out var c) && c.TryGetInt64(out long cv) ? cv : -1;
                string msg = err.TryGetProperty("message", out var m) ? m.ToString() : err.ToString();
                string? data = err.TryGetProperty("data", out var d) ? d.ToString() : null;
                throw new RpcException(code, msg, data, method);
            }
            if (!response.IsSuccessStatusCode)
                throw new RpcException((long)response.StatusCode, $"HTTP {(int)response.StatusCode}: {Truncate(text)}", method: method);
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("result", out var result))
                throw new RpcException(-32603, "Response has no result", method: method);
            return result.Clone();
        }
    }

    /// <summary>True for errors meaning "this provider will not serve the request", as opposed to an execution error.</summary>
    public static bool IsAccessDenied(RpcException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (ex.Code is 401 or 402 or 403 or 429 or -32052) return true;
        string m = ex.RpcMessage;
        return m.Contains("API key", StringComparison.OrdinalIgnoreCase)
            || m.Contains("free plan", StringComparison.OrdinalIgnoreCase)
            || m.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || m.Contains("token is invalid", StringComparison.OrdinalIgnoreCase)
            || m.Contains("not allowed to access", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Calls <paramref name="method"/> and deserializes the result.</summary>
    public async Task<T?> CallAsync<T>(string method, object?[]? parameters = null, CancellationToken ct = default)
    {
        var result = await CallAsync(method, parameters, ct).ConfigureAwait(false);
        return result.Deserialize<T>(JsonOptions.Default);
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

/// <summary>Shared serializer settings.</summary>
public static class JsonOptions
{
    public static JsonSerializerOptions Default { get; } = new(JsonSerializerDefaults.Web);
}

/// <summary>Helpers for hex encoded data and quantities.</summary>
public static class HexUtil
{
    /// <summary>Parses <c>0x</c>-prefixed (or bare) hex into bytes. Odd-length input is left-padded.</summary>
    public static byte[] Decode(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        ReadOnlySpan<char> s = hex.AsSpan().Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (s.Length % 2 == 1) return Convert.FromHexString("0" + s.ToString());
        return Convert.FromHexString(s);
    }

    public static string Encode(ReadOnlySpan<byte> bytes, bool prefix = true) =>
        prefix ? "0x" + Convert.ToHexStringLower(bytes) : Convert.ToHexStringLower(bytes);

    /// <summary>Parses an Ethereum-style hex quantity (<c>0x1a</c>) into an unsigned integer.</summary>
    public static System.Numerics.BigInteger ParseQuantity(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return System.Numerics.BigInteger.Zero;
        ReadOnlySpan<char> s = hex.AsSpan().Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (s.IsEmpty) return System.Numerics.BigInteger.Zero;
        return System.Numerics.BigInteger.Parse("0" + s.ToString(), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Formats a quantity as minimal <c>0x</c> hex (<c>0x0</c> for zero).</summary>
    public static string FormatQuantity(System.Numerics.BigInteger value)
    {
        if (value.Sign < 0) throw new ArgumentOutOfRangeException(nameof(value));
        if (value.IsZero) return "0x0";
        return "0x" + Convert.ToHexStringLower(value.ToByteArray(isUnsigned: true, isBigEndian: true)).TrimStart('0');
    }

    public static string FormatQuantity(ulong value) => "0x" + value.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
}
