using System.Numerics;
using System.Text.Json;
using Crypto.Net.Core;
using Crypto.Net.Evm;
using Crypto.Net.Native;
using Crypto.Net.Testing;

namespace Crypto.Net.Tests;

public class EvmStandardsTests
{
    [Theory]
    [InlineData("", "0x0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("eth", "0x93cdeb708b7545dc668eb9280176169d1c33cfd8ed6f04690a0bcc88a93fc4ae")]
    [InlineData("foo.eth", "0xde9b09fd7c5f901e23a3f19fecc54828e9c848539801e86591bd9801b019f84f")]
    [InlineData("Foo.ETH", "0xde9b09fd7c5f901e23a3f19fecc54828e9c848539801e86591bd9801b019f84f")]
    public void Ens_NameHash_Eip137(string name, string expected) => Assert.Equal(expected, HexUtil.Encode(EnsClient.NameHash(name)));

    [Theory]
    [InlineData("foo..eth")]
    [InlineData("foo bar.eth")]
    public void Ens_RejectsInvalidNames(string name) => Assert.Throws<FormatException>(() => EnsClient.Normalize(name));

    private static UserOperation SampleOp(bool v06 = false) => new()
    {
        Sender = "0x1306b01bC3e4AD202612D3843387e94737673F53",
        Nonce = 0x1234,
        Factory = "0x91E60e0613810449d098b0b5Ec8b51A0FE8c8985",
        FactoryData = HexUtil.Decode("0x5fbfb9cf000000000000000000000000aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa0000000000000000000000000000000000000000000000000000000000000000"),
        CallData = SimpleAccount.EncodeExecute("0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", BigInteger.Parse("1000000000000000"), []),
        CallGasLimit = 120000,
        VerificationGasLimit = 400000,
        PreVerificationGas = 52000,
        MaxFeePerGas = 30_000_000_000,
        MaxPriorityFeePerGas = 1_500_000_000,
        Paymaster = "0xcccccccccccccccccccccccccccccccccccccccc",
        PaymasterVerificationGasLimit = v06 ? 0 : 60000,
        PaymasterPostOpGasLimit = v06 ? 0 : 30000,
        PaymasterData = HexUtil.Decode(v06 ? "0x1234" : "0xdeadbeef"),
    };

    // Reference values computed with viem 2.31 (account-abstraction getUserOperationHash / toPackedUserOperation), chain 11155111.
    [Fact]
    public void UserOperation_HashesMatchViem()
    {
        var op = SampleOp();
        Assert.Equal("0x00000000000000000000000000061a800000000000000000000000000001d4c0", HexUtil.Encode(op.AccountGasLimits));
        Assert.Equal("0x00000000000000000000000059682f00000000000000000000000006fc23ac00", HexUtil.Encode(op.GasFees));
        Assert.Equal("0xcccccccccccccccccccccccccccccccccccccccc0000000000000000000000000000ea6000000000000000000000000000007530deadbeef", HexUtil.Encode(op.PackedPaymasterAndData));
        Assert.Equal("0x8d541cbb7b721a1aa70fe01fef0a9d4d1958f7cc854c9fa2ca2ed70e00ada136", HexUtil.Encode(op.GetHash(EntryPointVersion.V07, 11155111)));
        Assert.Equal("0x15ad4b4d9e4a325f5e0f7ee2760ece873f83a4d94dd72badacd87a1483324ef5", HexUtil.Encode(op.GetHash(EntryPointVersion.V08, 11155111)));
        Assert.Equal("0x8e2a40448ea7afd8ff8f680b94ceaeefef73b9edbd9aa39c6847e2b417315b14", HexUtil.Encode(SampleOp(v06: true).GetHash(EntryPointVersion.V06, 11155111)));
    }

    [Fact]
    public void SimpleAccount_SignatureRecoversOwner()
    {
        byte[] owner = Convert.FromHexString("4c0883a69102937d6231471b5dbb6204fe5129617082792ae468d01a3f362318");
        string ownerAddress = EvmAddress.FromPrivateKey(owner).Value;
        var op = SampleOp();
        byte[] signature = SimpleAccount.Sign(op, owner, EntryPointVersion.V07, 11155111);
        Assert.Equal(65, signature.Length);
        string recovered = EvmMessageSigner.RecoverPersonalMessageSigner(op.GetHash(EntryPointVersion.V07, 11155111), signature);
        Assert.Equal(ownerAddress, recovered);
        var rpc = op.ToRpc(EntryPointVersion.V07);
        Assert.Equal("0x1234", rpc["nonce"]);
        Assert.Equal("0xea60", rpc["paymasterVerificationGasLimit"]);
        Assert.False(op.ToRpc(EntryPointVersion.V06).ContainsKey("factory"));
    }

    [Fact]
    public async Task BundlerClient_SpeaksErc4337Rpc()
    {
        var mock = new MockHttpMessageHandler()
            .OnRpc("eth_supportedEntryPoints", $"[\"{EntryPoints.V07}\"]")
            .OnRpc("eth_estimateUserOperationGas", "{\"preVerificationGas\":\"0xcb20\",\"verificationGasLimit\":\"0x61a80\",\"callGasLimit\":\"0x1d4c0\"}")
            .OnRpc("eth_sendUserOperation", p =>
            {
                Assert.Equal(EntryPoints.V07, p[1].GetString());
                Assert.Equal("0x1306b01bC3e4AD202612D3843387e94737673F53", p[0].GetProperty("sender").GetString());
                return "\"0xabc\"";
            })
            .OnRpc("eth_getUserOperationReceipt", "{\"userOpHash\":\"0xabc\",\"success\":true,\"actualGasCost\":\"0x10\",\"receipt\":{\"transactionHash\":\"0xdef\",\"blockNumber\":\"0x5\"}}");
        using var bundler = new BundlerClient("https://bundler.invalid", mock.CreateClient());
        Assert.Equal([EntryPoints.V07], await bundler.GetSupportedEntryPointsAsync());
        var gas = await bundler.EstimateGasAsync(SampleOp(), EntryPointVersion.V07);
        Assert.Equal(new BigInteger(400000), gas.VerificationGasLimit);
        Assert.Equal("0xabc", await bundler.SendAsync(SampleOp(), EntryPointVersion.V07));
        var receipt = await bundler.GetReceiptAsync("0xabc");
        Assert.True(receipt!.Success);
        Assert.Equal(5ul, receipt.BlockNumber);
    }

    [Fact]
    public async Task NftClients_DecodeAndSubstituteIds()
    {
        string owner = "0x" + new string('a', 40);
        var mock = new MockHttpMessageHandler().OnRpc("eth_call", p =>
        {
            string data = p[0].GetProperty("data").GetString()!;
            byte[] result = data[..10] switch
            {
                var s when s == HexUtil.Encode(EvmAbi.GetFunctionSelector("ownerOf(uint256)")) => EvmAbi.Encode("address", owner),
                var s when s == HexUtil.Encode(EvmAbi.GetFunctionSelector("supportsInterface(bytes4)")) => EvmAbi.Encode("bool", data[10..18] == InterfaceIds.Erc721[2..]),
                var s when s == HexUtil.Encode(EvmAbi.GetFunctionSelector("uri(uint256)")) => EvmAbi.Encode("string", "ipfs://cid/{id}.json"),
                var s when s == HexUtil.Encode(EvmAbi.GetFunctionSelector("balanceOfBatch(address[],uint256[])")) => EvmAbi.Encode("uint256[]", (object)new object[] { new BigInteger(3), new BigInteger(4) }),
                _ => throw new InvalidOperationException(data),
            };
            return JsonSerializer.Serialize(HexUtil.Encode(result));
        });
        await using var rpc = new EvmRpcClient(EvmChain.Sepolia, httpClient: mock.CreateClient());
        var nft = new Erc721Client(rpc, "0x" + new string('1', 40));
        Assert.Equal(EvmAddress.ToChecksumAddress(HexUtil.Decode(owner)), await nft.GetOwnerAsync(7));
        Assert.True(await nft.SupportsInterfaceAsync(InterfaceIds.Erc721));
        Assert.False(await nft.SupportsInterfaceAsync(InterfaceIds.Erc1155));

        var multi = new Erc1155Client(rpc, "0x" + new string('2', 40));
        Assert.Equal("ipfs://cid/" + new string('0', 62) + "1a.json", await multi.GetUriAsync(26));
        Assert.Equal([new BigInteger(3), new BigInteger(4)], await multi.GetBalanceBatchAsync([owner, owner], [1, 2]));

        var log = new EvmLog("0x1", [Erc721Client.TransferEvent.Topic0, HexUtil.Encode(new byte[32]), HexUtil.Encode(new byte[32]), HexUtil.Encode(EvmAbi.EncodeUint256(7))], "0x", 1, "0x1", 0);
        Assert.Equal(new BigInteger(7), Erc721Client.TransferEvent.Decode(log)["tokenId"]);
    }
}
