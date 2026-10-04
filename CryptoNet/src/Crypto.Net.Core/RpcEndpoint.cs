namespace Crypto.Net.Core;

/// <summary>
/// An RPC endpoint plus optional request headers (e.g. an API key header). <see cref="ToString"/> never
/// reveals secrets, so endpoints can be logged safely.
/// </summary>
public sealed record RpcEndpoint(string Url, IReadOnlyDictionary<string, string>? Headers = null, string? Provider = null)
{
    /// <summary>Human-readable form with any API key redacted.</summary>
    public string DisplayName { get; init; } = Provider is null ? Url : $"{Provider} ({Redact(Url)})";

    public static implicit operator RpcEndpoint(string url) => new(url);

    public override string ToString() => DisplayName;

    private static string Redact(string url)
    {
        var uri = new Uri(url);
        return $"{uri.Scheme}://{uri.Host}/…";
    }
}
