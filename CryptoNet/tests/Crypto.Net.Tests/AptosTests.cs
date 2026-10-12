using Crypto.Net.Aptos;
using Crypto.Net.Core;
using Crypto.Net.Native;
using Crypto.Net.Wallet;

namespace Crypto.Net.Tests;

/// <summary>Aptos encoding cross-checked with @aptos-labs/ts-sdk 1.33.1.</summary>
public class AptosTests
{
    private const string To = "0xabababababababababababababababababababababababababababababababab";
    private static AptosAccount Key7() => new(Enumerable.Repeat((byte)7, 32).ToArray(), AptosChain.Testnet);

    [Fact]
    public void Sha3_256_StandardVectors()
    {
        Assert.Equal("a7ffc6f8bf1ed76651c14756a061d662f580ff4de43b49fa82d80a4b80f8434a", Convert.ToHexStringLower(CryptoNative.Sha3_256([])));
        Assert.Equal("3a985da74fe225b2045c172d6bd390bd855f086e3e9d525b46bfe24511431532", Convert.ToHexStringLower(CryptoNative.Sha3_256("abc"u8)));
    }

    [Fact]
    public void Keys_MatchTsSdk()
    {
        using var account = Key7();
        Assert.Equal("0xcc405722b15c00a19d37e51d9a756de1e61b780ad0935f3363bc7fce64edbdad", account.Address.Value);
        Assert.Equal("ea4a6c63e29c520abef5507b132ec5f9954776aebebe7b92421eea691446d22c", Convert.ToHexStringLower(account.PublicKey.Span));
        Assert.Equal("ed25519-priv-0x" + string.Concat(Enumerable.Repeat("07", 32)), account.ExportPrivateKey());
        using var imported = AptosAccount.FromPrivateKey(account.ExportPrivateKey());
        Assert.Equal(account.Address.Value, imported.Address.Value);
    }

    [Fact]
    public void HdWallet_MatchesTsSdk()
    {
        using var wallet = HdWallet.FromMnemonic("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about");
        using var a0 = wallet.GetAptosAccount(0);
        using var a1 = wallet.GetAptosAccount(1);
        Assert.Equal("0xeb663b681209e7087d681c5d3eed12aaa8e1915e7c87794542c3f96e94b3d3bf", a0.Address.Value);
        Assert.Equal("ed25519-priv-0xcc92c0eaf80206d817f150e21917f797e49cf644a33ac514de3c316baa2f1bf5", a0.ExportPrivateKey());
        Assert.Equal("0xf867372dfec13fb6c0740d4b574363685e10e6f243e9554ffa8f6e698e940efa", a1.Address.Value);
    }

    [Theory]
    [InlineData("0x1", "0x1", true)]
    [InlineData("0xa", "0xa", true)]
    [InlineData("0x0a", "0xa", false)]
    [InlineData("0x10", "0x0000000000000000000000000000000000000000000000000000000000000010", false)]
    [InlineData("0xcc405722b15c00a19d37e51d9a756de1e61b780ad0935f3363bc7fce64edbdad", "0xcc405722b15c00a19d37e51d9a756de1e61b780ad0935f3363bc7fce64edbdad", true)]
    public void Addresses_Aip40(string input, string normalized, bool strictlyValid)
    {
        Assert.Equal(normalized, AptosAddress.Normalize(input));
        Assert.Equal(strictlyValid, AptosAddress.IsValid(input));
    }

    [Theory]
    [InlineData("0x1::coin::CoinStore<0x1::aptos_coin::AptosCoin>", "07000000000000000000000000000000000000000000000000000000000000000104636f696e09436f696e53746f7265010700000000000000000000000000000000000000000000000000000000000000010a6170746f735f636f696e094170746f73436f696e00")]
    [InlineData("vector<0x1::option::Option<u256>>", "06070000000000000000000000000000000000000000000000000000000000000001066f7074696f6e064f7074696f6e010a")]
    public void TypeTags_MatchTsSdk(string text, string bcs)
    {
        var tag = MoveTypeTag.Parse(text);
        Assert.Equal(bcs, Convert.ToHexStringLower(tag.ToBcs()));
        Assert.Equal(text.Replace(",", ", "), tag.ToString());
    }

    [Theory]
    [InlineData("u64<u8>")]
    [InlineData("vector<u8")]
    [InlineData("0x1::coin")]
    [InlineData("vector<u8,u16>")]
    public void TypeTags_RejectMalformed(string text) => Assert.Throws<FormatException>(() => MoveTypeTag.Parse(text));

    [Fact]
    public void AptTransfer_MatchesTsSdk()
    {
        using var account = Key7();
        var tx = Raw(account, AptosEntryFunction.Transfer(To, 123456789));
        const string raw = "cc405722b15c00a19d37e51d9a756de1e61b780ad0935f3363bc7fce64edbdad07000000000000000200000000000000000000000000000000000000000000000000000000000000010d6170746f735f6163636f756e74087472616e73666572000220abababababababababababababababababababababababababababababababab0815cd5b0700000000400d03000000000064000000000000006039ce6a0000000002";
        Assert.Equal(raw, Convert.ToHexStringLower(tx.Serialize()));
        Assert.Equal("b5e97db07fa0bd0e5598aa3643a9bc6f6693bddc1a9fec9e674a461eaa00b193" + raw, Convert.ToHexStringLower(tx.SigningMessage()));

        byte[] signed = tx.Sign(account);
        Assert.Equal(raw + "0020ea4a6c63e29c520abef5507b132ec5f9954776aebebe7b92421eea691446d22c407ec8928a6cf6a0eef5eb98e8b884b6bede50e87e274f6d717f73929e3bdfc8c1d8a801fa537fb7d2427e2031055d4b79e7118c604398738cef001ba5ce6dc106", Convert.ToHexStringLower(signed));
        Assert.Equal("0xc14e573bb41897d65732908da788b8ca8b808db78a008e8c2230c28b0be536d6", AptosRawTransaction.HashOf(signed));
    }

    [Fact]
    public void GenericCoinTransfer_MatchesTsSdk()
    {
        using var account = Key7();
        var payload = AptosEntryFunction.Create("0x1::coin::transfer", [MoveTypeTag.Parse("0x1::aptos_coin::AptosCoin")], MoveArg.Address(To), MoveArg.U64(5));
        byte[] signed = Raw(account, payload).Sign(account);
        Assert.EndsWith("40b394850ee85fcfde5e2167075478fa446d506c517ee1c8169a94b91abec9616a796fa66a89e3813738d282792100873f62a36f0ef7be5980c040f7ebc8395d07", Convert.ToHexStringLower(signed));
        Assert.Equal("0xefa9055ef17f152b78840c741b58032de1cdb8dc6f9057e525cb2bd6d023e700", AptosRawTransaction.HashOf(signed));
        Assert.Equal("0x1::coin::transfer", payload.FunctionId);
    }

    [Fact]
    public void Arguments_EncodeAsBcs()
    {
        Assert.Equal("0568656c6c6f", Convert.ToHexStringLower(MoveArg.String("hello")));
        Assert.Equal("0201000200", Convert.ToHexStringLower(MoveArg.Vector([MoveArg.U16(1), MoveArg.U16(2)])));
        Assert.Equal("012a", Convert.ToHexStringLower(MoveArg.Option(MoveArg.U8(42))));
        Assert.Equal("00", Convert.ToHexStringLower(MoveArg.Option(null)));
    }

    private static AptosRawTransaction Raw(AptosAccount account, AptosEntryFunction payload) => new()
    {
        Sender = account.Address.Value,
        SequenceNumber = 7,
        Payload = payload,
        MaxGasAmount = 200_000,
        GasUnitPrice = 100,
        ExpirationTimestampSecs = 1791900000,
        ChainId = 2,
    };
}
