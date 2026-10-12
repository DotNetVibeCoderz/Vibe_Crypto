using System.Numerics;
using System.Text;
using System.Text.Json;
using Crypto.Net.Cosmos;
using Crypto.Net.Testing;

namespace Crypto.Net.Tests;

/// <summary>CosmWasm messages cross-checked with cosmjs-types 0.9.0.</summary>
public class CosmWasmTests
{
    [Fact]
    public void Messages_MatchCosmjs()
    {
        var exec = CosmWasm.Execute("osmo1sender", "osmo1contract", "{\"increment\":{}}", new Coin("uosmo", 1000));
        Assert.Equal("/cosmwasm.wasm.v1.MsgExecuteContract", exec.TypeUrl);
        Assert.Equal("0a0b6f736d6f3173656e646572120d6f736d6f31636f6e74726163741a107b22696e6372656d656e74223a7b7d7d2a0d0a05756f736d6f120431303030", Convert.ToHexStringLower(exec.Value));

        // Objects serialize to the same compact JSON.
        var execObj = CosmWasm.Execute("osmo1sender", "osmo1contract", new Dictionary<string, object> { ["increment"] = new Dictionary<string, object>() }, new Coin("uosmo", 1000));
        Assert.Equal(exec.Value, execObj.Value);

        Assert.Equal("0a0b6f736d6f3173656e646572120a6f736d6f3161646d696e182a220a636f756e7465722076312a0b7b22636f756e74223a307d",
            Convert.ToHexStringLower(CosmWasm.Instantiate("osmo1sender", 42, "counter v1", "{\"count\":0}", admin: "osmo1admin").Value));
        Assert.Equal("0a0b6f736d6f3173656e646572120d6f736d6f31636f6e7472616374182b22027b7d",
            Convert.ToHexStringLower(CosmWasm.Migrate("osmo1sender", "osmo1contract", 43, "{}").Value));
        Assert.Equal("0a0b6f736d6f3173656e64657212086f736d6f316e65771a0d6f736d6f31636f6e7472616374",
            Convert.ToHexStringLower(CosmWasm.UpdateAdmin("osmo1sender", "osmo1contract", "osmo1new").Value));
    }

    [Fact]
    public async Task SmartQueries_And_Cw20()
    {
        string? lastPath = null;
        var mock = new MockHttpMessageHandler()
            .OnPath("/smart/", "{\"data\":{\"balance\":\"123456\"}}")
            .OnPath("/cosmwasm/wasm/v1/contract/osmo1token", "{\"address\":\"osmo1token\",\"contract_info\":{\"code_id\":\"7\",\"creator\":\"osmo1c\",\"admin\":\"\",\"label\":\"tok\"}}");
        using var rest = new CosmosRestClient(CosmosChain.OsmosisTestnet, "https://lcd.invalid", mock.CreateClient());
        var cw20 = new Cw20Client(rest, "osmo1token");
        Assert.Equal(new BigInteger(123456), await cw20.GetBalanceAsync("osmo1holder"));
        lastPath = mock.Requests[^1].Uri.AbsolutePath;
        string b64 = Uri.UnescapeDataString(lastPath[(lastPath.LastIndexOf('/') + 1)..]);
        Assert.Equal("{\"balance\":{\"address\":\"osmo1holder\"}}", Encoding.UTF8.GetString(Convert.FromBase64String(b64)));

        var info = await rest.GetContractInfoAsync("osmo1token");
        Assert.Equal((7ul, (string?)null, "tok"), (info.CodeId, info.Admin, info.Label));

        var transfer = cw20.Transfer("osmo1me", "osmo1you", 5);
        Assert.Contains("{\"transfer\":{\"recipient\":\"osmo1you\",\"amount\":\"5\"}}", Encoding.UTF8.GetString(transfer.Value));
        var send = cw20.Send("osmo1me", "osmo1pool", 9, new { swap = new { min = "1" } });
        string json = Encoding.UTF8.GetString(send.Value);
        string hook = JsonDocument.Parse(json[json.IndexOf('{')..(json.LastIndexOf('}') + 1)]).RootElement.GetProperty("send").GetProperty("msg").GetString()!;
        Assert.Equal("{\"swap\":{\"min\":\"1\"}}", Encoding.UTF8.GetString(Convert.FromBase64String(hook)));
    }
}
