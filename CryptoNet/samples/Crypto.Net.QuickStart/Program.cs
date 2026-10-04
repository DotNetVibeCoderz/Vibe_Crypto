// Crypto.Net quick start — the code from README.md as a runnable program.
// Built by Gravicode Studios, led by Kang Fadhil.
//
//   dotnet run --project samples/Crypto.Net.QuickStart                  # derive + sign offline, read balances
//   dotnet run --project samples/Crypto.Net.QuickStart -- --send "<phrase>"  # broadcast tiny testnet self-transfers
//
// --send needs a funded testnet phrase. It never touches mainnet.

using Crypto.Net.Bitcoin;
using Crypto.Net.Core;
using Crypto.Net.Cosmos;
using Crypto.Net.Evm;
using Crypto.Net.Extensions;
using Crypto.Net.Polkadot;
using Crypto.Net.Solana;
using Crypto.Net.Wallet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

bool send = args.Length >= 2 && args[0] == "--send";
using var wallet = send ? HdWallet.FromMnemonic(args[1]) : HdWallet.Generate(wordCount: 12);

// ---------------------------------------------------------------- accounts
using var eth  = wallet.GetEvmAccount(0, EvmChain.Sepolia);
using var btc  = wallet.GetBitcoinAccount(BitcoinAddressType.TaprootP2TR, 0, BitcoinNetwork.Testnet);
using var sol  = wallet.GetSolanaAccount(0, SolanaChain.Devnet);
using var dot  = wallet.GetPolkadotAccount("", PolkadotChain.Westend);
using var atom = wallet.GetCosmosAccount(0, CosmosChain.CosmosHubTestnet);

Console.WriteLine($"Ethereum  {eth.Address}");
Console.WriteLine($"Bitcoin   {btc.Address}");
Console.WriteLine($"Solana    {sol.Address}");
Console.WriteLine($"Polkadot  {dot.Address}");
Console.WriteLine($"Cosmos    {atom.Address}");

// ---------------------------------------------------------------- offline signing
var tx = new EvmTransaction
{
    ChainId = EvmChain.Sepolia.NumericChainId,
    Nonce = 0,
    MaxPriorityFeePerGas = 1_000_000_000,
    MaxFeePerGas = 30_000_000_000,
    To = eth.Address.Value,
    Value = Amount.FromEther(0.001m).BaseUnits,
};
var signed = eth.SignTransaction(tx);
Console.WriteLine($"\nSigned EIP-1559 tx {signed.Hash} ({signed.RawBytes.Length} bytes), sender {signed.RecoverSender()}");
Console.WriteLine($"personal_sign: {HexUtil.Encode(eth.SignMessage("hello from Crypto.Net"))[..26]}…");

// ---------------------------------------------------------------- dependency injection + live reads
var host = Host.CreateApplicationBuilder();
host.Logging.SetMinimumLevel(LogLevel.Warning);
host.Services.AddCryptoNet(c => c
    .AddEvm("eth", o => o.Network = EvmChain.Sepolia)
    .AddSolana("sol", o => o.Cluster = SolanaChain.Devnet)
    .AddBitcoin("btc")
    .AddPolkadot("dot")
    .AddCosmos("atom")
    .WithResilience());
using var app = host.Build();

var registry = app.Services.GetRequiredService<IChainRegistry>();
foreach (var reg in registry.Registrations)
{
    try
    {
        var client = registry.GetClient(reg.Name);
        Console.WriteLine($"{reg.Chain.Name,-20} height {await client.GetBlockNumberAsync(),12:N0}");
    }
    catch (Exception ex) when (ex is HttpRequestException or RpcException or TaskCanceledException)
    {
        Console.WriteLine($"{reg.Chain.Name,-20} unavailable: {ex.Message}");
    }
}

if (!send)
{
    Console.WriteLine("\nRead-only run finished. Pass --send \"<funded testnet phrase>\" to broadcast self-transfers.");
    return;
}

// ---------------------------------------------------------------- sending (testnets only)
var evm = app.Services.GetRequiredKeyedService<EvmRpcClient>("eth");
await using var signer = eth.CreateSigner();
TxHash h1 = await evm.TransferAsync(signer, eth.Address.Value, Amount.FromEther(0.0001m));
var receipt = await evm.WaitForReceiptAsync(h1);
Console.WriteLine($"EVM      {h1} in block {receipt.BlockNumber}");

var esplora = app.Services.GetRequiredKeyedService<EsploraClient>("btc");
Console.WriteLine($"Bitcoin  {await esplora.TransferAsync(btc, btc.Address.Value, amountSats: 1_000)}");

var solana = app.Services.GetRequiredKeyedService<SolanaRpcClient>("sol");
Console.WriteLine($"Solana   {await solana.TransferAsync(sol, sol.Address.Value, Amount.FromSol(0.001m))}");

var westend = app.Services.GetRequiredKeyedService<PolkadotRpcClient>("dot");
Console.WriteLine($"Polkadot {await westend.TransferAsync(dot, dot.Address.Value, Amount.Parse("0.01", 12))}");

var cosmos = app.Services.GetRequiredKeyedService<CosmosRestClient>("atom");
Console.WriteLine($"Cosmos   {await cosmos.TransferAsync(atom, atom.Address.Value, Amount.FromAtom(0.001m))}");
