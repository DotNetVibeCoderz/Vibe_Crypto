using CnetWorker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<WatcherOptions>(builder.Configuration.GetSection("Watcher"));
builder.Services.AddHttpClient();
builder.Services.AddHostedService<BlockWatcher>();
builder.Build().Run();
