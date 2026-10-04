using Crypto.Net.Extensions;
using Crypto.Net.Polkadot;

namespace Crypto.Net.Tests;

/// <summary>Runs only when CRYPTONET_LIVE_TESTS=1 (needs internet). Read-only: nothing is broadcast.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CRYPTONET_LIVE_TESTS") != "1")
            Skip = "Set CRYPTONET_LIVE_TESTS=1 to run live network tests";
    }
}

[Collection(BackendCollection.Name)]
public class LiveNetworkTests
{
    public static IEnumerable<object[]> PublicNetworks() =>
        ChainCatalog.Entries.Where(e => !e.Key.Contains("local") && !e.Key.Contains("regtest")).Select(e => new object[] { e.Key });

    [LiveFact]
    public async Task EveryBuiltInEndpoint_ReturnsAHead()
    {
        var failures = new List<string>();
        foreach (var entry in ChainCatalog.Entries.Where(e => !e.Key.Contains("local") && !e.Key.Contains("regtest")))
        {
            try
            {
                using var client = ChainCatalog.CreateClient(entry);
                if (await client.GetBlockNumberAsync() == 0) failures.Add($"{entry.Key}: height 0");
            }
            catch (Exception ex)
            {
                failures.Add($"{entry.Key}: {ex.Message}");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [LiveFact]
    public async Task PolkadotMetadata_BuildsDecodableTransfer()
    {
        foreach (var chain in new[] { PolkadotChain.Polkadot, PolkadotChain.WestendAssetHub, PolkadotChain.Kusama })
        {
            await using var client = new PolkadotRpcClient(chain);
            var md = await client.GetMetadataAsync();
            using var alice = PolkadotAccount.FromSuri("//Alice", chain: chain);
            var ctx = await client.GetExtrinsicContextAsync(alice.Address.Value);
            byte[] ext = Extrinsic.Sign(md, Extrinsic.TransferKeepAlive(md, alice.Address.Value, 1), alice, ctx);
            var fee = await client.EstimateFeeAsync(ext); // the runtime must decode the extrinsic to price it
            Assert.False(fee.IsZero);
        }
    }
}

public class RpcProviderTests
{
    [Fact]
    public void Endpoints_KeepKeysOutOfDisplayNames()
    {
        var drpc = RpcProviders.Drpc.GetEndpoint(Crypto.Net.Evm.EvmChain.BnbChain, "secret-key")!;
        Assert.Equal("https://lb.drpc.live/bsc", drpc.Url);
        Assert.Equal("secret-key", drpc.Headers!["Drpc-Key"]);
        var ankr = RpcProviders.Ankr.GetEndpoint(Crypto.Net.Evm.EvmChain.Sepolia, "secret-key")!;
        Assert.Equal("https://rpc.ankr.com/eth_sepolia/secret-key", ankr.Url);
        Assert.DoesNotContain("secret", ankr.ToString());
        Assert.DoesNotContain("secret", drpc.ToString());
        Assert.Null(RpcProviders.Ankr.GetEndpoint(Crypto.Net.Bitcoin.BitcoinNetwork.Mainnet, "k"));
    }

    [Fact]
    public async Task JsonRpcClient_FailsOverOnAccessDenied_ButNotOnExecutionErrors()
    {
        var denied = new Crypto.Net.Testing.MockHttpMessageHandler();
        var paid = new Crypto.Net.Testing.MockHttpMessageHandler();
        var router = new RoutingHandler(new() { ["denied.test"] = denied, ["ok.test"] = paid });
        denied.OnRpcError("eth_blockNumber", -32052, "API key is not allowed to access blockchain");
        paid.OnRpc("eth_blockNumber", "\"0x2a\"");
        paid.OnRpcError("eth_sendRawTransaction", -32000, "nonce too low");

        using var http = new HttpClient(router);
        await using var client = new Crypto.Net.Evm.EvmRpcClient(Crypto.Net.Evm.EvmChain.Ethereum,
            [new Crypto.Net.Core.RpcEndpoint("http://denied.test"), new Crypto.Net.Core.RpcEndpoint("http://ok.test")], http);

        Assert.Equal(42UL, await client.GetBlockNumberAsync());
        Assert.Equal("http://ok.test", client.Rpc.CurrentEndpoint.Url);
        var ex = await Assert.ThrowsAsync<Crypto.Net.Core.RpcException>(() => client.SendRawTransactionAsync(new byte[] { 1 }).AsTask());
        Assert.Equal(-32000, ex.Code);
        Assert.Single(denied.Requests); // never retried elsewhere after the switch
    }

    private sealed class RoutingHandler(Dictionary<string, Crypto.Net.Testing.MockHttpMessageHandler> routes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var invoker = new HttpMessageInvoker(routes[request.RequestUri!.Host], disposeHandler: false);
            return invoker.SendAsync(request, ct);
        }
    }

    [LiveFact]
    public async Task ConfiguredProviders_ServeEthereum()
    {
        var endpoints = RpcProviders.FromEnvironment(Crypto.Net.Evm.EvmChain.Ethereum, Crypto.Net.Evm.EvmChain.Ethereum.DefaultRpcUrl);
        await using var client = new Crypto.Net.Evm.EvmRpcClient(Crypto.Net.Evm.EvmChain.Ethereum, endpoints);
        Assert.True(await client.GetBlockNumberAsync() > 20_000_000);
    }
}
