using Crypto.Net.Evm;

namespace CnetTests;

/// <summary>Runs against the nodes in docker-compose.yml: <c>docker compose up -d</c> then <c>CNET_LOCAL_NODES=1 dotnet test</c>.</summary>
public class LocalNodeTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("CNET_LOCAL_NODES") == "1";

    [Fact]
    public async Task AnvilAnswers()
    {
        if (!Enabled) return;
        await using var client = new EvmRpcClient(EvmChain.Localhost);
        Assert.True(await client.GetBlockNumberAsync() >= 0);
    }
}
