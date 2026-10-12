using Crypto.Net.Core;
using Crypto.Net.Native;
using Crypto.Net.Sui;
using Crypto.Net.Wallet;

namespace Crypto.Net.Tests;

/// <summary>Sui encoding cross-checked with @mysten/sui 1.21.0.</summary>
public class SuiTests
{
    private const string Sender = "0xa0ccc8bcc83f6c628340134f8546a21e0618fd1aaa02432bba454c4a2c2233da";
    private static SuiAccount Key7() => new(Enumerable.Repeat((byte)7, 32).ToArray(), SuiChain.Testnet);

    private static readonly SuiObjectRef GasCoin = new("0x" + string.Concat(Enumerable.Repeat("11", 32)), 5, CryptoNative.Base58Encode(Enumerable.Repeat((byte)0x22, 32).ToArray()));

    [Fact]
    public void Keys_MatchSuiSdk()
    {
        using var account = Key7();
        Assert.Equal(Sender, account.Address.Value);
        Assert.Equal("suiprivkey1qqrswpc8qurswpc8qurswpc8qurswpc8qurswpc8qurswpc8qurswxzszc4", account.ExportPrivateKey());
        using var imported = SuiAccount.FromPrivateKey(account.ExportPrivateKey());
        Assert.Equal(Sender, imported.Address.Value);
    }

    [Fact]
    public void HdWallet_MatchesSuiSdk()
    {
        using var wallet = HdWallet.FromMnemonic("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about");
        using var a0 = wallet.GetSuiAccount(0);
        using var a1 = wallet.GetSuiAccount(1);
        Assert.Equal("0x5e93a736d04fbb25737aa40bee40171ef79f65fae833749e3c089fe7cc2161f1", a0.Address.Value);
        Assert.Equal("0x082d099250999ab8450a9ef3a962edf9e2449e1045be32ba5a0f2c6117ff7167", a1.Address.Value);
    }

    [Fact]
    public void SuiTransfer_MatchesSuiSdk()
    {
        using var account = Key7();
        var data = new SuiTransactionBuilder(Sender)
            .TransferSui("0x" + string.Concat(Enumerable.Repeat("ab", 32)), 123456789)
            .Build([GasCoin], 1000, 5_000_000);
        const string bcs = "000002000815cd5b07000000000020abababababababababababababababababababababababababababababababab0202000101000001010300000000010100a0ccc8bcc83f6c628340134f8546a21e0618fd1aaa02432bba454c4a2c2233da0111111111111111111111111111111111111111111111111111111111111111110500000000000000202222222222222222222222222222222222222222222222222222222222222222a0ccc8bcc83f6c628340134f8546a21e0618fd1aaa02432bba454c4a2c2233dae803000000000000404b4c000000000000";
        Assert.Equal(bcs, Convert.ToHexStringLower(data.Serialize()));
        Assert.Equal("8b69oQWyU22KAsT56DgpgtoNWhB9fnH4sK4yezHkRApH", data.Digest);
        Assert.Equal("AKelRewDV6Vdoah1vcioS3Awqx7mNR4drwD/j6aGvT4HjafJxqOz6uvDsba3hTii8xpSlwdk6I+Twc/flGhmRA/qSmxj4pxSCr71UHsTLsX5lUd2rr6+e5JCHuppFEbSLA==",
            Convert.ToBase64String(account.SignTransaction(data.Serialize())));
    }

    [Fact]
    public void MoveCalls_MatchSuiSdk()
    {
        using var account = Key7();
        var tx = new SuiTransactionBuilder(Sender);
        var coin = tx.Object(new SuiObjectRef("0x" + string.Concat(Enumerable.Repeat("33", 32)), 9, CryptoNative.Base58Encode(Enumerable.Repeat((byte)0x44, 32).ToArray())));
        var clock = tx.SharedObject("0x6", 1, mutable: false);
        tx.MoveCall("0x2::pay::split", [MoveTypeTag.Parse("0x2::sui::SUI")], coin, tx.PureU64(5));
        tx.MoveCall("0xabc::m::f", null, clock, tx.PureString("hi"), tx.Pure(MoveArg.Bytes([1, 2])), tx.PureBool(true));
        tx.MergeCoins(tx.Gas, coin);
        var data = tx.Build([GasCoin], 1000, 5_000_000);

        Assert.Equal("BkvGjQMevJK8aEx2xvMyCqee6A4fXRCSPqEVXUBkKwb5", data.Digest);
        Assert.Equal("AAXWtwA0/evgYh7OBNUU69w8pBWV3pEC7lmZoG+Sq1d/80GeT2jFvTMzSMTUAV3NZuHHUoMmgvEHIfF6ukJtOQDqSmxj4pxSCr71UHsTLsX5lUd2rr6+e5JCHuppFEbSLA==",
            Convert.ToBase64String(account.SignTransaction(data.Serialize())));
        Assert.Equal(6, data.Inputs.Count);
    }

    [Fact]
    public void TransactionData_RoundTripsThroughParser()
    {
        var tx = new SuiTransactionBuilder(Sender);
        var v = tx.MakeMoveVec(MoveTypeTag.U64, tx.PureU64(1), tx.PureU64(2));
        tx.MoveCall("0x2::m::f", [MoveTypeTag.Parse("vector<0x2::coin::Coin<0x2::sui::SUI>>")], v, tx.Receiving(GasCoin));
        tx.TransferObjects([tx.SplitCoins(tx.Gas, tx.PureU64(7))[0]], tx.PureAddress("0x5"));
        var data = tx.Build([GasCoin], 750, 10_000_000, gasOwner: "0x" + string.Concat(Enumerable.Repeat("cd", 32)), expirationEpoch: 1234);
        byte[] bcs = data.Serialize();

        var parsed = SuiTransactionData.Parse(bcs);
        Assert.Equal(bcs, parsed.Serialize());
        Assert.Equal(1234UL, parsed.ExpirationEpoch);
        Assert.IsType<SuiCommand.MakeMoveVec>(parsed.Commands[0]);
        Assert.Equal(new SuiArgument(SuiArgumentKind.NestedResult, 2, 0), ((SuiCommand.TransferObjects)parsed.Commands[3]).Objects[0]);

        using var account = Key7();
        byte[] signed = data.Sign(account);
        var (decoded, sigs) = SuiSignedTransaction.Decode(signed);
        Assert.Equal(bcs, decoded);
        Assert.True(SuiAccount.Verify(Assert.Single(sigs), SuiIntent.TransactionData, decoded, out string signer));
        Assert.Equal(Sender, signer);
    }

    [Fact]
    public void PersonalMessage_MatchesSuiSdk()
    {
        using var account = Key7();
        byte[] sig = account.SignPersonalMessage("hello sui"u8);
        Assert.Equal("AB4YhLqz7/KxSXe/61Ss8pNJyAHY7/G7XRSrVL4h1y4umvbJVzN1p+w3jZ5evcv6wjY4HVVHKsIpmKUtd0zf5w7qSmxj4pxSCr71UHsTLsX5lUd2rr6+e5JCHuppFEbSLA==", Convert.ToBase64String(sig));
        Assert.True(SuiAccount.Verify(sig, SuiIntent.PersonalMessage, new BcsWriter().Bytes("hello sui"u8).ToArray(), out _));
    }

    [Theory]
    [InlineData("0x2", true)]
    [InlineData("0xa0ccc8bcc83f6c628340134f8546a21e0618fd1aaa02432bba454c4a2c2233da", true)]
    [InlineData("a0ccc8bcc83f6c628340134f8546a21e0618fd1aaa02432bba454c4a2c2233da", false)]
    [InlineData("0xzz", false)]
    public void Addresses(string address, bool valid) => Assert.Equal(valid, SuiAddress.IsValid(address));
}
