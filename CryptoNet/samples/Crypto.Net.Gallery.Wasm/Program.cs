using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using Crypto.Net.Gallery;

// Crypto.Net Gallery in the browser. Built by Gravicode Studios, led by Kang Fadhil.
// The page's JavaScript (app.js) calls Interop.Invoke instead of fetch("/api/..."), so the whole Gallery API runs
// here in .NET WebAssembly. The Rust core is not available in the browser; Crypto.Net uses its managed engine.
GalleryApi.Host = "browser";
Console.WriteLine("Crypto.Net Gallery (WebAssembly) ready");

[System.Runtime.Versioning.SupportedOSPlatform("browser")]
public static partial class Interop
{
    private static readonly HttpClient Http = new();

    /// <summary>Runs one Gallery API call; returns <c>{"status":200,"body":"…json…"}</c>.</summary>
    [JSExport]
    public static async Task<string> Invoke(string method, string path, string? body)
    {
        var (status, json) = await GalleryApi.InvokeAsync(method, path, body, Http);
        return JsonSerializer.Serialize(new { status, body = json });
    }
}
