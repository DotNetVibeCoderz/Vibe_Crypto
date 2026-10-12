using System.Numerics;
using System.Text.Json;
using Crypto.Net.Core;
using Crypto.Net.Evm;
using Crypto.Net.Generators;
using Crypto.Net.Native;
using Crypto.Net.Solana;
using Crypto.Net.Testing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Crypto.Net.Tests;

/// <summary>
/// The clients under test are generated at build time from Contracts/*.abi.json and *.idl.json by the source
/// generator, exactly as in a user project.
/// </summary>
public class GeneratorTests
{
    private const string TokenAddress = "0x1c7D4B196Cb0C7B01d743Fbc6116a902379C7238";
    private static readonly string Alice = EvmAddress.ToChecksumAddress(Enumerable.Repeat((byte)0xaa, 20).ToArray());

    private static (Marketplace Market, Erc20 Token, MockHttpMessageHandler Mock) Clients(Func<string, byte[]> callResult)
    {
        var mock = new MockHttpMessageHandler().OnRpc("eth_call", p =>
        {
            string data = p[0].GetProperty("data").GetString()!;
            return JsonSerializer.Serialize(HexUtil.Encode(callResult(data[..10])));
        });
        var rpc = new EvmRpcClient(EvmChain.Sepolia, httpClient: mock.CreateClient());
        return (new Marketplace(rpc, TokenAddress), new Erc20(rpc, TokenAddress), mock);
    }

    private static string Selector(string signature) => HexUtil.Encode(EvmAbi.GetFunctionSelector(signature));

    [Fact]
    public async Task Erc20_ViewsDecodeTypedValues()
    {
        var (_, token, mock) = Clients(selector =>
            selector == Selector("balanceOf(address)") ? EvmAbi.Encode("uint256", new BigInteger(1234567))
            : selector == Selector("symbol()") ? EvmAbi.Encode("string", "USDC")
            : selector == Selector("decimals()") ? EvmAbi.Encode("uint8", 6)
            : throw new InvalidOperationException(selector));
        Assert.Equal(new BigInteger(1234567), await token.BalanceOfAsync(Alice));
        Assert.Equal("USDC", await token.SymbolAsync());
        Assert.Equal(new BigInteger(6), await token.DecimalsAsync());
        string sent = JsonDocument.Parse(mock.Requests[0].Body).RootElement.GetProperty("params")[0].GetProperty("data").GetString()!;
        Assert.Equal(HexUtil.Encode(EvmAbi.EncodeFunctionCall("balanceOf(address)", Alice)), sent);
        Assert.Equal(EvmAbi.EncodeFunctionCall("transfer(address,uint256)", Alice, new BigInteger(5)), Erc20.EncodeTransfer(Alice, 5));
    }

    [Fact]
    public async Task Structs_ArraysAndMultipleOutputs()
    {
        object?[] listingAbi = [new BigInteger(7), Alice, new BigInteger(990), "Rare book", new object?[] { new byte[32], Enumerable.Repeat((byte)1, 32).ToArray() }];
        var (market, _, _) = Clients(selector =>
            selector == Selector("getListing(uint256)") ? EvmAbi.Encode("(uint256,address,uint96,string,bytes32[])", [listingAbi])
            : selector == Selector("listings(uint256,uint256)") ? EvmAbi.Encode("(uint256,address,uint96,string,bytes32[])[],uint256", new object?[] { listingAbi, listingAbi }, new BigInteger(42))
            : selector == Selector("order(uint256)") ? EvmAbi.Encode("((uint256,address,uint96,string,bytes32[]),address)", [new object?[] { listingAbi, Alice }])
            : selector == Selector("ping()") ? []
            : throw new InvalidOperationException(selector));

        var listing = await market.GetListingAsync(7);
        Assert.Equal((new BigInteger(7), Alice, new BigInteger(990), "Rare book"), (listing.Id, listing.Seller, listing.Price, listing.Title));
        Assert.Equal(2, listing.Tags.Length);

        var page = await market.ListingsAsync(0, 10);
        Assert.Equal(2, page.Items.Length);
        Assert.Equal(new BigInteger(42), page.Total);

        var order = await market.OrderAsync(1);
        Assert.Equal(Alice, order.Buyer);
        Assert.Equal("Rare book", order.Listing.Title);
        await market.PingAsync();

        // Struct inputs encode exactly like hand-written ABI tuples.
        Assert.Equal(EvmAbi.EncodeFunctionCall("create((uint256,address,uint96,string,bytes32[]))", [listingAbi]), Marketplace.EncodeCreate(listing));
        Assert.Equal(EvmAbi.EncodeFunctionCall("batchCreate((uint256,address,uint96,string,bytes32[])[])", [new object?[] { listingAbi }]), Marketplace.EncodeBatchCreate([listing]));
        Assert.NotEqual(Marketplace.EncodeSafeTransferFrom(Alice, Alice, 1), Marketplace.EncodeSafeTransferFrom(Alice, Alice, 1, [1]));
    }

    [Fact]
    public void Events_DecodeTyped()
    {
        string Word(string a) => "0x" + new string('0', 24) + a[2..].ToLowerInvariant();
        var log = new EvmLog(TokenAddress, [Erc20.Events.Transfer.Topic0, Word(Alice), Word(Alice)], HexUtil.Encode(EvmAbi.EncodeUint256(99)), 1, "0x01", 0);
        Assert.True(Erc20.TransferEvent.TryDecode(log, out var transfer));
        Assert.Equal((Alice, new BigInteger(99)), (transfer!.From, transfer.Value));
        Assert.False(Erc20.ApprovalEvent.TryDecode(log, out _));

        byte[] titleHash = CryptoNative.Keccak256("Rare book"u8);
        var listed = new EvmLog(TokenAddress, [Marketplace.Events.Listed.Topic0, HexUtil.Encode(EvmAbi.EncodeUint256(7)), Word(Alice), HexUtil.Encode(titleHash)],
            HexUtil.Encode(EvmAbi.EncodeUint256(990)), 2, "0x02", 1);
        Assert.True(Marketplace.ListedEvent.TryDecode(listed, out var l));
        Assert.Equal(titleHash, l!.Title);
        Assert.Equal(new BigInteger(990), l.Price);
    }

    [Fact]
    public void Anchor_NewFormat_InstructionsAndAccounts()
    {
        string counter = CryptoNative.Base58Encode(Enumerable.Repeat((byte)3, 32).ToArray());
        string user = CryptoNative.Base58Encode(Enumerable.Repeat((byte)4, 32).ToArray());
        var ix = CounterProgram.Initialize(counter, user, 42, "hits", CounterProgram.Mode.Auto);
        Assert.Equal(CounterProgram.ProgramId, ix.ProgramId);
        byte[] expected = [.. Anchor.InstructionDiscriminator("initialize"), .. new BorshWriter().U64(42).String("hits").U8(2).ToArray()];
        Assert.Equal(expected, ix.Data);
        Assert.Equal([(counter, true, true), (user, true, true), (SolanaAddress.SystemProgram, false, false)],
            ix.Accounts.Select(a => (a.PublicKey, a.IsSigner, a.IsWritable)));

        // Optional account omitted: Anchor's convention is to pass the program id, read-only.
        var set = CounterProgram.SetConfig(counter, user, new CounterProgram.Config.Range(5, 9), [1, 2], null, [9, 8, 7, 6]);
        Assert.Equal((CounterProgram.ProgramId, false, false), (set.Accounts[2].PublicKey, set.Accounts[2].IsSigner, set.Accounts[2].IsWritable));
        byte[] args = new BorshWriter().U8(2).U64(5).U64(9).U32(2).U32(1).U32(2).U8(0).Bytes([9, 8, 7, 6]).ToArray();
        Assert.Equal([.. Anchor.InstructionDiscriminator("set_config"), .. args], set.Data);

        // Account round trip with nested option/enum/vec/u128.
        var state = new CounterProgram.Counter(user, 7, "hits", CounterProgram.Mode.On, [-1, 2], new CounterProgram.Config.Fixed(3), BigInteger.Pow(2, 100));
        var w = new BorshWriter().Bytes(Anchor.AccountDiscriminator("Counter"));
        state.Write(w);
        var decoded = CounterProgram.DecodeCounter(w.ToArray());
        Assert.Equal((state.Authority, state.Count, state.Label, state.Mode, state.Total), (decoded.Authority, decoded.Count, decoded.Label, decoded.Mode, decoded.Total));
        Assert.Equal(state.History, decoded.History);
        Assert.Equal(new CounterProgram.Config.Fixed(3), decoded.Config);
        Assert.Throws<FormatException>(() => CounterProgram.DecodeCounter(new byte[16]));
        Assert.Equal("Overflow: Counter overflowed", CounterProgram.ErrorMessage(6000));
    }

    [Fact]
    public void Anchor_LegacyFormat_DerivesDiscriminators()
    {
        string vault = CryptoNative.Base58Encode(Enumerable.Repeat((byte)5, 32).ToArray());
        string owner = CryptoNative.Base58Encode(Enumerable.Repeat((byte)6, 32).ToArray());
        var ix = VaultProgram.DepositSol(vault, owner, SolanaAddress.SystemProgram, 1_000);
        Assert.Equal([.. Anchor.InstructionDiscriminator("deposit_sol"), .. new BorshWriter().U64(1_000).ToArray()], ix.Data);
        Assert.Equal(Anchor.AccountDiscriminator("Vault"), VaultProgram.VaultDiscriminator);
        var data = new BorshWriter().Bytes(Anchor.AccountDiscriminator("Vault")).PublicKey(owner).U64(55).U8(254).ToArray();
        Assert.Equal(new VaultProgram.Vault(owner, 55, 254), VaultProgram.DecodeVault(data));
    }

    [Fact]
    public void InvalidAbi_ReportsDiagnostic()
    {
        var driver = CSharpGeneratorDriver.Create(new CryptoNetSourceGenerator().AsSourceGenerator())
            .AddAdditionalTexts([new InMemoryText("Broken.abi.json", "[{\"type\":\"function\",\"name\":\"f\",\"inputs\":[{\"name\":\"x\",\"type\":\"fixed128x18\"}]}]")]);
        var result = driver.RunGenerators(CSharpCompilation.Create("t")).GetRunResult();
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("CNETGEN001", diagnostic.Id);
    }

    [Fact]
    public void Generators_ProduceSameCodeAsCli()
    {
        string abi = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Contracts", "Erc20.abi.json"));
        string code = EvmContractGenerator.Generate(abi, "Demo", "Erc20");
        Assert.Contains("public partial class Erc20 : EvmContract", code);
        Assert.Contains("BalanceOfAsync(string account", code);
    }

    private sealed class InMemoryText(string path, string text) : AdditionalText
    {
        public override string Path { get; } = path;
        public override Microsoft.CodeAnalysis.Text.SourceText GetText(CancellationToken cancellationToken = default) => Microsoft.CodeAnalysis.Text.SourceText.From(text);
    }
}
