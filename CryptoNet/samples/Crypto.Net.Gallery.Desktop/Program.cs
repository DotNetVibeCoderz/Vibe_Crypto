using Crypto.Net.Gallery;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Photino.NET;

// Crypto.Net Gallery for the desktop. Built by Gravicode Studios, led by Kang Fadhil.
// The Gallery server runs in-process on a random loopback port; the UI is shown in the OS webview
// (WebView2 on Windows, WKWebView on macOS, WebKitGTK on Linux). Nothing is exposed beyond 127.0.0.1.

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool smoke = args.Contains("--smoke");   // CI/self-test: open the window, verify the API, close after a few seconds
        GalleryApi.Host = "desktop";

        var app = GalleryServer.Create([], "http://127.0.0.1:0", Path.Combine(AppContext.BaseDirectory, "wwwroot"));
        app.StartAsync().GetAwaiter().GetResult();
        string url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

        if (smoke)
        {
            using var http = new HttpClient();
            string status = http.GetStringAsync(url + "/api/status").GetAwaiter().GetResult();
            Console.WriteLine($"Gallery API at {url}: {status}");
        }

        var window = new PhotinoWindow()
            .SetTitle("Crypto.Net Gallery")
            .SetUseOsDefaultSize(false)
            .SetSize(1360, 900)
            .SetMinSize(900, 640)
            .Center()
            .SetIconFile(Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "icon.ico" : "icon.png"))
            .Load(new Uri(url + "/" + (Environment.GetEnvironmentVariable("CRYPTONET_GALLERY_QUERY") ?? "")));   // e.g. ?theme=dark

        if (smoke)
        {
            var timer = new System.Threading.Timer(_ => window.Close(), null, TimeSpan.FromSeconds(6), Timeout.InfiniteTimeSpan);
            window.WindowCreated += (_, _) => Console.WriteLine("window created");
        }

        window.WaitForClose();
        app.StopAsync().GetAwaiter().GetResult();
        Console.WriteLine("closed");
        return 0;
    }
}
