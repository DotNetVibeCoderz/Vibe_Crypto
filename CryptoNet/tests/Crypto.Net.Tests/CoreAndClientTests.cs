using System.Net;
using System.Numerics;
using Crypto.Net.Bitcoin;
using Crypto.Net.Core;
using Crypto.Net.Cosmos;
using Crypto.Net.Evm;
using Crypto.Net.Extensions;
using Crypto.Net.Polkadot;
using Crypto.Net.Solana;
using Crypto.Net.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Crypto.Net.Tests;

public class AmountTests
{
    [Theory]
    [InlineData("1.5", 18, "1500000000000000000")]
    [InlineData("0.000000000000000001", 18, "1")]
    [InlineData("-2", 8, "-200000000")]
    [InlineData("123456789012345678901234567890", 18, "123456789012345678901234567890000000000000000000")]
    [InlineData(".5", 9, "500000000")]
    public void Parse_IsExact(string input, int decimals, string expectedUnits)
    {
        var a = Amount.Parse(input, decimals);
        Assert.Equal(BigInteger.Parse(expectedUnits), a.BaseUnits);
    }

    [Fact]
    public void Parse_RejectsExcessPrecisionAndGarbage()
    {
        Assert.Throws<FormatException>(() => Amount.Parse("0.123456789", 8));
        Assert.Throws<FormatException>(() => Amount.Parse("1,5", 8));
        Assert.Throws<FormatException>(() => Amount.Parse("", 8));
        Assert.False(Amount.TryParse("abc", 8, out _));
    }

    [Fact]
    public void Formatting_AndArithmetic()
    {
        Assert.Equal("1.5", Amount.FromEther(1.5m).ToString());
        Assert.Equal("0.00000001", Amount.FromSatoshi(1).ToString());
        Assert.Equal("-0.5", Amount.Parse("-0.5", 9).ToString());
        Assert.Equal("0", Amount.Zero(6).ToString());
        Assert.Equal("1 SOL", Amount.FromSol(1).ToString("SOL"));
        Assert.Equal(Amount.FromWei(1_000_000_000), Amount.FromGwei(1));
        Assert.Equal(Amount.FromEther(3), Amount.FromEther(1) + Amount.FromEther(2));
        Assert.True(Amount.Parse("1", 6) == Amount.Parse("1", 18)); // value equality across scales
        Assert.Equal(Amount.Parse("1", 6).GetHashCode(), Amount.Parse("1", 18).GetHashCode());
        Assert.Throws<InvalidOperationException>(() => Amount.Parse("1.5", 9).WithDecimals(0));
    }

    [Theory]
    [InlineData("1.5", "ether", "gwei", "1500000000")]
    [InlineData("1", "btc", "sats", "100000000")]
    [InlineData("2500", "lamports", "sol", "0.0000025")]
    [InlineData("1", "dot", "planck", "10000000000")]
    public void UnitConverter_Convert(string value, string from, string to, string expected)
    {
        Assert.Equal(expected, UnitConverter.Convert(value, from, to));
    }

    [Fact]
    public void UnitConverter_RejectsCrossAssetConversion()
    {
        Assert.Throws<ArgumentException>(() => UnitConverter.Convert("1", "btc", "eth"));
    }

    [Fact]
    public void Address_EqualitySemantics()
    {
        Assert.Equal(new Address("0xABCD", ChainId.Ethereum), new Address("0xabcd", ChainId.Ethereum));
        Assert.NotEqual(new Address("AbCd", ChainId.Solana), new Address("abcd", ChainId.Solana));
        Assert.NotEqual(new Address("0xabcd", ChainId.Ethereum), new Address("0xabcd", ChainId.Polygon));
    }
}

public class RpcClientTests
{
    [Fact]
    public async Task Evm_Queries_AndErrorMapping()
    {
        var mock = new MockHttpMessageHandler()
            .OnRpc("eth_getBalance", "\"0xde0b6b3a7640000\"")
            .OnRpc("eth_blockNumber", "\"0x10\"")
            .OnRpc("eth_chainId", "\"0xaa36a7\"")
            .OnRpcError("eth_sendRawTransaction", -32000, "insufficient funds for gas * price + value");
        await using var client = new EvmRpcClient(EvmChain.Sepolia, "http://mock", mock.CreateClient());

        Assert.Equal(Amount.FromEther(1), await client.GetBalanceAsync(new Address(TestVectors.EthValidChecksum1, ChainId.Sepolia)));
        Assert.Equal(16UL, await client.GetBlockNumberAsync());
        Assert.Equal(11155111UL, await client.GetChainIdAsync());

        var ex = await Assert.ThrowsAsync<RpcException>(() => client.SendRawTransactionAsync(new byte[] { 1 }).AsTask());
        Assert.Equal(-32000, ex.Code);
        Assert.Contains("insufficient funds", ex.Message);
        Assert.Equal("eth_sendRawTransaction", mock.Requests[^1].RpcMethod);
    }

    [Fact]
    public async Task NonStandardErrorPayload_RaisesRpcException()
    {
        var mock = new MockHttpMessageHandler().QueueResponse("{\"error\":\"API key disabled\"}", HttpStatusCode.Forbidden);
        await using var client = new EvmRpcClient(EvmChain.Polygon, "http://mock", mock.CreateClient());
        var ex = await Assert.ThrowsAsync<RpcException>(() => client.GetBlockNumberAsync().AsTask());
        Assert.Equal(403, ex.Code);
        Assert.Contains("API key disabled", ex.Message);
    }

    [Fact]
    public async Task Evm_PrepareTransaction_FillsNonceFeesAndGas()
    {
        var mock = new MockHttpMessageHandler()
            .OnRpc("eth_getTransactionCount", "\"0x7\"")
            .OnRpc("eth_getBlockByNumber", "{\"baseFeePerGas\":\"0x3b9aca00\"}")
            .OnRpc("eth_maxPriorityFeePerGas", "\"0x59682f00\"")
            .OnRpc("eth_estimateGas", "\"0x5208\"");
        await using var client = new EvmRpcClient(EvmChain.Sepolia, "http://mock", mock.CreateClient());

        var tx = await client.PrepareTransactionAsync(TestVectors.EthValidChecksum1, TestVectors.EthValidChecksum2, 1);
        Assert.Equal(7UL, tx.Nonce);
        Assert.Equal(EvmTransactionType.DynamicFee, tx.Type);
        Assert.Equal(new BigInteger(1_500_000_000), tx.MaxPriorityFeePerGas);
        Assert.Equal(new BigInteger(2 * 1_000_000_000L + 1_500_000_000L), tx.MaxFeePerGas);
        Assert.Equal(25_200UL, tx.GasLimit);
        Assert.Equal(EvmChain.Sepolia.NumericChainId, tx.ChainId);
    }

    [Fact]
    public async Task Erc20_ReadsDecimalsAndBalance()
    {
        var mock = new MockHttpMessageHandler().OnRpc("eth_call", p =>
        {
            string data = p[0].GetProperty("data").GetString()!;
            return data.StartsWith("0x313ce567") // decimals()
                ? "\"0x0000000000000000000000000000000000000000000000000000000000000006\""
                : "\"0x00000000000000000000000000000000000000000000000000000000000f4240\""; // 1,000,000
        });
        await using var client = new EvmRpcClient(EvmChain.Sepolia, "http://mock", mock.CreateClient());
        var token = new Erc20Client(client, TestVectors.EthValidChecksum1);
        var balance = await token.GetBalanceAsync(TestVectors.EthValidChecksum2);
        Assert.Equal("1", balance.ToString());
        Assert.Equal(6, balance.Decimals);
    }

    [Fact]
    public async Task Solana_BalanceAndBlockhash()
    {
        var mock = new MockHttpMessageHandler()
            .OnRpc("getBalance", "{\"context\":{\"slot\":1},\"value\":2500000000}")
            .OnRpc("getLatestBlockhash", "{\"context\":{\"slot\":1},\"value\":{\"blockhash\":\"EkSnNWid2cvwEVnVx9aBqawnmiCNiDgp3gUdkDPTKN1N\",\"lastValidBlockHeight\":99}}");
        await using var client = new SolanaRpcClient(SolanaChain.Devnet, "http://mock", mock.CreateClient());
        Assert.Equal("2.5", (await client.GetBalanceAsync(new Address(TestVectors.SolanaTokenProgram, ChainId.SolanaDevnet))).ToString());
        var (hash, height) = await client.GetLatestBlockhashAsync();
        Assert.Equal(99UL, height);
        Assert.True(SolanaAddress.IsValid(hash));
    }

    [Fact]
    public async Task Esplora_UtxosBalanceAndFees()
    {
        var mock = new MockHttpMessageHandler()
            .OnPath("/utxo", "[{\"txid\":\"" + new string('a', 64) + "\",\"vout\":1,\"value\":15000,\"status\":{\"confirmed\":true}}]")
            .OnPath("/fee-estimates", "{\"1\":20.5,\"6\":8.25,\"144\":1.1}")
            .OnPath("/address/", "{\"chain_stats\":{\"funded_txo_sum\":20000,\"spent_txo_sum\":5000},\"mempool_stats\":{\"funded_txo_sum\":0,\"spent_txo_sum\":0}}");
        await using var client = new EsploraClient(BitcoinNetwork.Mainnet, "http://mock", mock.CreateClient());

        var utxos = await client.GetUtxosAsync(TestVectors.BtcMainnetSegwit);
        Assert.Single(utxos);
        Assert.Equal(BitcoinAddressType.SegWitP2WPKH, utxos[0].Type);
        Assert.Equal(15000, await client.GetBalanceSatsAsync(TestVectors.BtcMainnetSegwit));
        Assert.Equal(8.25m, await client.GetFeeRateAsync(6));
    }

    [Fact]
    public async Task Polkadot_AccountInfoDecoding()
    {
        // nonce=3, consumers/providers/sufficients, free=1 DOT, reserved=0, frozen=0.25 DOT, flags
        var w = new ScaleWriter().U32(3).U32(0).U32(1).U32(0)
            .U128(10_000_000_000).U128(0).U128(2_500_000_000).U128(0);
        var mock = new MockHttpMessageHandler().OnRpc("state_getStorage", $"\"0x{Convert.ToHexStringLower(w.ToArray())}\"");
        await using var client = new PolkadotRpcClient(PolkadotChain.Polkadot, "http://mock", mock.CreateClient());

        var info = await client.GetAccountInfoAsync(TestVectors.AliceSs58Polkadot);
        Assert.Equal(3u, info.Nonce);
        Assert.Equal("0.75", (await client.GetBalanceAsync(new Address(TestVectors.AliceSs58Polkadot, ChainId.Polkadot))).ToString());

        string key = System.Text.Json.JsonDocument.Parse(mock.Requests[0].Body).RootElement.GetProperty("params")[0].GetString()!;
        Assert.StartsWith("0x26aa394eea5630e07c48ae0c9558cef7b99d880ec681799c0cf30e8886371da9", key);
        Assert.EndsWith(TestVectors.AliceSr25519PublicKey, key);
    }

    [Fact]
    public async Task Cosmos_AccountAndBalance()
    {
        var mock = new MockHttpMessageHandler()
            .OnPath("/cosmos/auth/v1beta1/accounts/", "{\"account\":{\"@type\":\"/cosmos.auth.v1beta1.BaseAccount\",\"account_number\":\"42\",\"sequence\":\"7\"}}")
            .OnPath("/by_denom", "{\"balance\":{\"denom\":\"uatom\",\"amount\":\"1234567\"}}");
        await using var client = new CosmosRestClient(CosmosChain.CosmosHub, "http://mock", mock.CreateClient());
        Assert.Equal((42UL, 7UL), await client.GetAccountAsync(TestVectors.AbandonCosmos));
        Assert.Equal("1.234567", (await client.GetBalanceAsync(new Address(TestVectors.AbandonCosmos, ChainId.Cosmos))).ToString());
    }

    [Fact]
    public async Task WaitForReceipt_PollsUntilAvailable()
    {
        int calls = 0;
        var mock = new MockHttpMessageHandler().OnRpc("eth_getTransactionReceipt", _ => ++calls < 3
            ? "null"
            : "{\"transactionHash\":\"0x01\",\"blockNumber\":\"0x5\",\"status\":\"0x1\",\"gasUsed\":\"0x5208\",\"effectiveGasPrice\":\"0x1\",\"from\":\"0x0\",\"logs\":[]}");
        await using var client = new EvmRpcClient(EvmChain.Sepolia, "http://mock", mock.CreateClient());
        var receipt = await client.WaitForReceiptAsync(new TxHash("0x01"), TimeSpan.FromMilliseconds(10));
        Assert.True(receipt.IsSuccess);
        Assert.Equal(5UL, receipt.BlockNumber);
        Assert.Equal(3, calls);
    }
}

public class ExtensionsTests
{
    [Fact]
    public void AddCryptoNet_RegistersKeyedClients()
    {
        var services = new ServiceCollection();
        services.AddCryptoNet(c => c
            .AddEvm("eth", o => o.Network = EvmChain.Sepolia)
            .AddBitcoin("btc")
            .AddSolana("sol", o => o.Cluster = SolanaChain.Devnet)
            .AddPolkadot("dot")
            .AddCosmos("atom")
            .WithResilience());
        using var provider = services.BuildServiceProvider();

        var eth = provider.GetRequiredKeyedService<EvmRpcClient>("eth");
        Assert.Same(EvmChain.Sepolia, eth.Chain);
        Assert.Same(eth, provider.GetRequiredKeyedService<IChainClient>("eth"));
        Assert.IsType<EsploraClient>(provider.GetRequiredKeyedService<IChainClient>("btc"));

        var registry = provider.GetRequiredService<IChainRegistry>();
        Assert.Equal(["eth", "btc", "sol", "dot", "atom"], registry.Registrations.Select(r => r.Name));
        Assert.IsType<SolanaRpcClient>(registry.GetClient("sol"));
    }
}
