using System.Net;
using System.Text;
using System.Text.Json;

namespace Crypto.Net.Testing;

/// <summary>
/// In-memory HTTP handler for unit tests. Responses can be queued in order, or routed by JSON-RPC method
/// or URL path. Every request (with its body) is recorded for assertions.
/// </summary>
public sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _queue = new();
    private readonly Dictionary<string, Func<JsonElement, string>> _rpc = new(StringComparer.Ordinal);
    private readonly List<(string PathPrefix, HttpStatusCode Status, string Body)> _routes = [];
    private readonly List<RecordedRequest> _requests = [];

    public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body)
    {
        /// <summary>JSON-RPC method name when the body is a JSON-RPC request.</summary>
        public string? RpcMethod
        {
            get
            {
                try
                {
                    using var doc = JsonDocument.Parse(Body);
                    return doc.RootElement.TryGetProperty("method", out var m) ? m.GetString() : null;
                }
                catch (JsonException)
                {
                    return null;
                }
            }
        }
    }

    public IReadOnlyList<RecordedRequest> Requests => _requests;

    /// <summary>Queues a raw response returned to the next unmatched request.</summary>
    public MockHttpMessageHandler QueueResponse(string content, HttpStatusCode status = HttpStatusCode.OK)
    {
        _queue.Enqueue(new HttpResponseMessage(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") });
        return this;
    }

    /// <summary>Answers JSON-RPC <paramref name="method"/> with <paramref name="resultJson"/> (raw JSON).</summary>
    public MockHttpMessageHandler OnRpc(string method, string resultJson) => OnRpc(method, _ => resultJson);

    /// <summary>Answers JSON-RPC <paramref name="method"/> using the request params.</summary>
    public MockHttpMessageHandler OnRpc(string method, Func<JsonElement, string> resultFactory)
    {
        _rpc[method] = resultFactory;
        return this;
    }

    /// <summary>Answers JSON-RPC <paramref name="method"/> with an error object.</summary>
    public MockHttpMessageHandler OnRpcError(string method, int code, string message)
    {
        _rpc[method] = _ => throw new RpcErrorSignal(code, message);
        return this;
    }

    /// <summary>Answers any request whose path starts with <paramref name="pathPrefix"/>.</summary>
    public MockHttpMessageHandler OnPath(string pathPrefix, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes.Add((pathPrefix, status, body));
        return this;
    }

    private sealed class RpcErrorSignal(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!, body);
        _requests.Add(recorded);

        string? method = recorded.RpcMethod;
        if (method is not null && _rpc.TryGetValue(method, out var factory))
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string id = root.TryGetProperty("id", out var idEl) ? idEl.GetRawText() : "1";
            JsonElement p = root.TryGetProperty("params", out var pe) ? pe : default;
            string json;
            try
            {
                json = $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{factory(p)}}}";
            }
            catch (RpcErrorSignal e)
            {
                json = $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"error\":{{\"code\":{e.Code},\"message\":{JsonSerializer.Serialize(e.Message)}}}}}";
            }
            return Json(HttpStatusCode.OK, json);
        }

        string path = request.RequestUri!.PathAndQuery;
        foreach (var (prefix, status, routeBody) in _routes)
        {
            if (path.Contains(prefix, StringComparison.Ordinal))
                return Json(status, routeBody);
        }

        if (_queue.Count > 0) return _queue.Dequeue();
        return Json(HttpStatusCode.NotFound, $"{{\"error\":\"no mock for {request.Method} {path} {method}\"}}");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>Creates an <see cref="HttpClient"/> that uses this handler.</summary>
    public HttpClient CreateClient() => new(this, disposeHandler: false);
}
