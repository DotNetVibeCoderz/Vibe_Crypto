using Crypto.Net.Core;
using Crypto.Net.Extensions;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();
builder.Services.AddHttpClient();
var app = builder.Build();
app.MapOpenApi();

bool allowMainnet = app.Configuration.GetValue<bool>("CryptoNet:AllowMainnet");
string defaultNetwork = app.Configuration["CryptoNet:DefaultNetwork"] ?? "CNET_DEFAULT_NETWORK";

CatalogEntry? Resolve(string? key)
{
    var entry = ChainCatalog.Find(key ?? defaultNetwork);
    return entry is not null && (entry.IsTestnet || allowMainnet) ? entry : null;
}

app.MapGet("/networks", () => ChainCatalog.Entries
        .Where(e => e.IsTestnet || allowMainnet)
        .Select(e => new NetworkDto(e.Key, e.Name, e.Family.ToString(), e.Chain.Symbol, e.IsTestnet)))
    .WithSummary("Networks this API serves");

app.MapGet("/networks/{network}/head", async (string network, IHttpClientFactory http, CancellationToken ct) =>
    {
        if (Resolve(network) is not { } entry) return Results.NotFound();
        using var client = ChainCatalog.CreateClient(entry, http: http.CreateClient(entry.Key));
        return Results.Ok(new HeadDto(entry.Key, await client.GetBlockNumberAsync(ct)));
    })
    .WithSummary("Latest block height");

app.MapGet("/networks/{network}/balances/{address}", async (string network, string address, IHttpClientFactory http, CancellationToken ct) =>
    {
        if (Resolve(network) is not { } entry) return Results.NotFound();
        if (!ChainCatalog.IsValidAddress(entry, address)) return Results.BadRequest(new { error = $"Not a valid {entry.Name} address" });
        using var client = ChainCatalog.CreateClient(entry, http: http.CreateClient(entry.Key));
        Amount balance = await client.GetBalanceAsync(new Address(address, entry.Chain.Id), ct);
        return Results.Ok(new BalanceDto(entry.Key, address, balance.ToString(), balance.BaseUnits.ToString(), balance.Decimals, entry.Chain.Symbol));
    })
    .WithSummary("Native balance of an address");

app.MapGet("/addresses/{address}", (string address) => ChainCatalog.DetectAddress(address))
    .WithSummary("Which chains an address string is valid for");

app.Run();

public sealed record NetworkDto(string Key, string Name, string Family, string Symbol, bool IsTestnet);
public sealed record HeadDto(string Network, ulong Height);
public sealed record BalanceDto(string Network, string Address, string Amount, string BaseUnits, int Decimals, string Symbol);

public partial class Program;
