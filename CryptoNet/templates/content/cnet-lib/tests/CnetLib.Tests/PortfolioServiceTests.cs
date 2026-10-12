using Crypto.Net.Evm;
using Crypto.Net.Extensions;
using Crypto.Net.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace CnetLib.Tests;

public class PortfolioServiceTests
{
    [Fact]
    public async Task ReadsBalancesThroughRegisteredClients()
    {
        // No network access: the mock answers eth_getBalance with 1.5 ETH.
        var mock = new MockHttpMessageHandler().OnRpc("eth_getBalance", "\"0x14d1120d7b160000\"");
        var services = new ServiceCollection();
        services.AddCryptoNet(b => b.AddEvm("eth", o => o.Network = EvmChain.Sepolia));
        // AddCryptoNet names its HttpClient "Crypto.Net:<name>"; swap in the mock handler.
        services.AddHttpClient("Crypto.Net:eth").ConfigurePrimaryHttpMessageHandler(() => mock);
        services.AddCnetLib();
        using var provider = services.BuildServiceProvider();

        var holdings = await provider.GetRequiredService<PortfolioService>()
            .GetHoldingsAsync([("eth", TestVectors.AbandonEthereum)]);

        var holding = Assert.Single(holdings);
        Assert.Equal("1.5", holding.Balance.ToString());
        Assert.Equal("eth_getBalance", mock.Requests[0].RpcMethod);
    }
}
