using Crypto.Net.Core;
using Crypto.Net.Extensions;
using Crypto.Net.Wallet;
using Microsoft.Extensions.Configuration;

// Configuration: appsettings.json < user secrets < CNET_* environment variables < command line.
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables("CNET_")
    .AddCommandLine(args)
    .Build();

var network = ChainCatalog.Get(config["Network"] ?? "CNET_DEFAULT_NETWORK");
uint index = uint.TryParse(config["AccountIndex"], out uint i) ? i : 0;

// A throwaway wallet unless a phrase was configured (dotnet user-secrets set Mnemonic "...").
string? phrase = config["Mnemonic"];
using var wallet = string.IsNullOrWhiteSpace(phrase) ? HdWallet.Generate() : HdWallet.FromMnemonic(phrase);
var account = ChainCatalog.DeriveAccount(wallet, network, index);

Console.WriteLine($"Network : {network.Name} ({network.Key}){(network.IsTestnet ? "" : "  ** MAINNET **")}");
Console.WriteLine($"Account : {account.Address}");
if (string.IsNullOrWhiteSpace(phrase))
    Console.WriteLine("          (temporary wallet - set a Mnemonic in user secrets to keep the same address)");

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
using var client = ChainCatalog.CreateClient(network);
try
{
    ulong head = await client.GetBlockNumberAsync(cts.Token);
    Amount balance = await client.GetBalanceAsync(account.Address, cts.Token);
    Console.WriteLine($"Head    : {head}");
    Console.WriteLine($"Balance : {balance} {network.Chain.Symbol}");
}
catch (Exception ex) when (ex is RpcException or HttpRequestException or OperationCanceledException)
{
    Console.WriteLine($"Network error: {ex.Message}");
    return 1;
}
finally
{
    (account as IDisposable)?.Dispose();
}
return 0;
