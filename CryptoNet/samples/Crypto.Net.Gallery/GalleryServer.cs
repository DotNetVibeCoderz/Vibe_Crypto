using System.Text;

namespace Crypto.Net.Gallery;

/// <summary>
/// Hosts the Gallery UI and its API. The web Gallery runs it on http://localhost:5080; the desktop Gallery runs it
/// in-process on a random loopback port behind a native window.
/// </summary>
public static class GalleryServer
{
    public static WebApplication Create(string[] args, string? urls = null, string? webRoot = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, WebRootPath = webRoot });
        builder.Services.AddHttpClient();
        if (urls is not null)
        {
            builder.WebHost.UseUrls(urls);
            builder.Logging.SetMinimumLevel(LogLevel.Warning);   // embedded (desktop) host: keep the console quiet
        }
        else if (builder.Configuration["urls"] is null && Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)
            builder.WebHost.UseUrls("http://localhost:5080");

        var app = builder.Build();
        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.Map("/api/{**path}", async (HttpContext ctx, string? path, IHttpClientFactory http) =>
        {
            string? body = null;
            if (HttpMethods.IsPost(ctx.Request.Method))
            {
                using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
                body = await reader.ReadToEndAsync(ctx.RequestAborted);
            }
            var (status, json) = await GalleryApi.InvokeAsync(ctx.Request.Method, path ?? "", body, http.CreateClient(), ctx.RequestAborted);
            return Results.Text(json, "application/json", Encoding.UTF8, status);
        });

        app.MapFallbackToFile("index.html");
        return app;
    }
}
