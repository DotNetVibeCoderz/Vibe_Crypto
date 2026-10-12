using System.Numerics;
using Crypto.Net.Ton;
using Crypto.Net.Wallet;

namespace Crypto.Net.Tests;

/// <summary>TON encoding cross-checked with @ton/core 0.60.1, @ton/crypto 3.3.0 and @ton/ton 15.2.1.</summary>
public class TonTests
{
    private static readonly byte[] Seed7 = Enumerable.Repeat((byte)7, 32).ToArray();
    private static readonly TonAddress Dest = TonAddress.Parse("0:" + string.Concat(Enumerable.Repeat("ab", 32)));
    private static string Hex(byte[] b) => Convert.ToHexStringLower(b);

    [Fact]
    public void Boc_MatchesTonCore()
    {
        var cell = new TonCellBuilder().StoreUInt(5, 8).StoreRef(new TonCellBuilder().StoreUInt(1, 3).EndCell()).EndCell();
        Assert.Equal("b5ee9c72c10102010007000407010205010001307d4e2ff2", Hex(cell.ToBoc(withIndex: true, withCrc: true)));
        Assert.Equal("b5ee9c720101020100070001020501000130", Hex(cell.ToBoc(withIndex: false, withCrc: false)));
        Assert.Equal(cell, TonCell.FromBoc(Convert.FromHexString("b5ee9c72c10102010007000407010205010001307d4e2ff2")));
        Assert.Equal("x{05}", cell.ToString());
        Assert.Equal("x{3_}", cell.Refs[0].ToString());
    }

    [Fact]
    public void Boc_RejectsTreesDeeperThan1024()
    {
        // A 5000-cell chain: rejected while parsing instead of overflowing the stack when hashed.
        var b = new List<byte>(Convert.FromHexString("b5ee9c7202021388000100004e1e0000"));
        for (int i = 0; i < 5000; i++) b.AddRange(i < 4999 ? [1, 0, (byte)((i + 1) >> 8), (byte)(i + 1)] : [0, 0]);
        Assert.Throws<FormatException>(() => TonBoc.Deserialize(b.ToArray()));
    }

    [Fact]
    public void StringTail_SnakesAcrossCells()
    {
        var cell = new TonCellBuilder().StoreStringTail(new string('x', 200)).EndCell();
        Assert.Equal("93d3f9d95ae931c19307b0548ee6a2582dd34f68c2148709a7754d5bf753d418", Hex(cell.Hash()));
        Assert.Equal(new string('x', 200), cell.BeginParse().LoadStringTail());
        Assert.Equal(cell, TonCell.FromBoc(cell.ToBoc()));
    }

    [Theory]
    [InlineData(TonWalletVersion.V4R2, false, "0:2a6ee6b7ff41bfecafe383386325c7a895f4fe4ce346b18eb9c14a0152d6629c", "UQAqbua3_0G_7K_jgzhjJceolfT-TONGsY65wUoBUtZinP1w", "EQAqbua3_0G_7K_jgzhjJceolfT-TONGsY65wUoBUtZinKC1")]
    [InlineData(TonWalletVersion.V4R2, true, "0:2a6ee6b7ff41bfecafe383386325c7a895f4fe4ce346b18eb9c14a0152d6629c", "0QAqbua3_0G_7K_jgzhjJceolfT-TONGsY65wUoBUtZinEb6", "kQAqbua3_0G_7K_jgzhjJceolfT-TONGsY65wUoBUtZinBs_")]
    [InlineData(TonWalletVersion.V5R1, false, "0:949bc8af02b9eb02b449c4e11d594f4befd433c8c6b665967b60a9f9dbfff3d0", "UQCUm8ivArnrArRJxOEdWU9L79QzyMa2ZZZ7YKn52__z0N1Q", "EQCUm8ivArnrArRJxOEdWU9L79QzyMa2ZZZ7YKn52__z0ICV")]
    [InlineData(TonWalletVersion.V5R1, true, "0:f4353ed65c960a639e4abc7f605cd8cebae5e841cbdc2fad2c3d0806eabf4c75", "0QD0NT7WXJYKY55KvH9gXNjOuuXoQcvcL60sPQgG6r9MdYxq", "kQD0NT7WXJYKY55KvH9gXNjOuuXoQcvcL60sPQgG6r9MddGv")]
    public void WalletAddresses_MatchTonTon(TonWalletVersion version, bool testnet, string raw, string nonBounceable, string bounceable)
    {
        using var account = new TonAccount(Seed7, testnet ? TonChain.Testnet : TonChain.Mainnet, version);
        Assert.Equal(raw, account.WalletAddress.ToRawString());
        Assert.Equal(nonBounceable, account.Address.Value);
        Assert.Equal(bounceable, account.WalletAddress.ToString(bounceable: true, testOnly: testnet));
        var (parsed, b, t) = TonAddress.ParseWithFlags(bounceable);
        Assert.Equal((account.WalletAddress, true, testnet), (parsed, b!.Value, t!.Value));
    }

    [Fact]
    public void WalletCode_HashesMatch()
    {
        Assert.Equal("feb5ff6820e2ff0d9483e7e0d62c817d846789fb4ae580c878866d959dabd5c0", Hex(TonWalletContract.Code(TonWalletVersion.V4R2).Hash()));
        Assert.Equal("20834b7b72b112147e1b2fb457b84e74d1a30f04f737d4f62a668e9552d2b72f", Hex(TonWalletContract.Code(TonWalletVersion.V5R1).Hash()));
        using var v5 = new TonAccount(Seed7, TonChain.Testnet);
        Assert.Equal("b5ee9c7241010101002b000051800000003ffffffef5253631f14e29055f7aa83d899762fccaa3bb575f5f3dc9210f75348a23691620746dd1ee",
            Hex(TonWalletContract.InitialData(TonWalletVersion.V5R1, v5.PublicKey.Span, v5.WalletId).ToBoc()));
    }

    [Theory]
    [InlineData(TonWalletVersion.V4R2, 0u, "c5328eb856fc17aff8c1c5e92d9109de527161d6a497b38452141a14613cefa4")]
    [InlineData(TonWalletVersion.V4R2, 5u, "bddcbed674a66a9327fe9b98b0c0c1b1ef95613b979eb12aaa93f046384ad59a")]
    [InlineData(TonWalletVersion.V5R1, 0u, "f2799164cae92540456637edf2ae14e0a93255d8b9ac44f06a606f07dde73b4d")]
    [InlineData(TonWalletVersion.V5R1, 5u, "e4b5987650601266f0e9ab8881790e50ddb8a2292872147a38d0f58dd9fa98df")]
    public void TransferBodies_MatchTonTon(TonWalletVersion version, uint seqno, string hash)
    {
        // Same transfer as the reference: a commented 0.123 TON message and a bounceable 5-nanoton message.
        using var account = new TonAccount(Seed7, TonChain.Testnet, version);
        var body = account.CreateTransferBody(seqno, Messages(), seqno == 0 ? uint.MaxValue : 1791900000);
        Assert.Equal(hash, Hex(body.Hash()));
    }

    [Fact]
    public void ExternalMessages_MatchTonTon()
    {
        using var account = new TonAccount(Seed7, TonChain.Mainnet, TonWalletVersion.V4R2);
        var withInit = account.CreateTransfer(0, Messages());
        Assert.Equal("690ecb973b715d7be77ecb6f37cf8a43610e166d50af2fe9f227672d9ef41283", Hex(withInit.Hash()));
        var body = account.CreateTransferBody(0, Messages());
        var plain = TonMessage.External(account.WalletAddress, body);
        Assert.Equal("438d6529156789e1ac5320fa50e3433362616a8167b8a7b235520b1315f70ac3", Hex(plain.Hash()));
        Assert.Equal(TonCell.FromBocBase64("te6cckEBAwEA7AAC44gAVN3Nb/6Df9lfxwZwxkuPUSvp/JnGjWMdc4KUAqWsxTgGTSQ+L0xgjJFJis72lbGt30L7FHlG1ue2txTNY62LwGx4d+y0+wBCyc1iiaI7cpIPFzmRQcV7dZXhfcR8UdgYUU1NGL/////4AAAAAAAYHAECAIJCAFXV1dXV1dXV1dXV1dXV1dXV1dXV1dXV1dXV1dXV1dXVoDqmpgAAAAAAAAAAAAAAAAAAAAAAAGhlbGxvIHRvbgBiYgBV1dXV1dXV1dXV1dXV1dXV1dXV1dXV1dXV1dXV1dXV1YgoAAAAAAAAAAAAAAAAAOKZjAI="), plain);
        // The normalized hash ignores the state init and where the body is stored.
        Assert.Equal(Hex(TonMessage.NormalizedHash(withInit)), Hex(TonMessage.NormalizedHash(plain)));
    }

    [Fact]
    public void Mnemonic_MatchesTonCrypto()
    {
        const string m = "logic family swap airport quality hunt float mechanic adapt frame behave cream agree spoil street buzz invite all horse census glory forest milk firm";
        Assert.True(TonMnemonic.Validate(m));
        Assert.False(TonMnemonic.Validate(m, "secret"));
        Assert.Equal("ffb86a431b8dbc1f3594e5b919732880028f5dbe3c00ffa0d92f405e8a321072", Hex(TonMnemonic.ToPrivateKey(m)));

        const string pw = "position poem hawk reason ensure join robust civil segment advice west cheese vote bonus vicious pottery issue apart you zero clog mouse stick equip";
        Assert.False(TonMnemonic.Validate(pw));
        Assert.True(TonMnemonic.IsPasswordNeeded(pw));
        Assert.Equal("4d749de0e827fdc8b6c6e3f4c08728ee78c3da14dac7ee76e2bf9308f70ecbb9", Hex(TonMnemonic.ToPrivateKey(pw, "secret")));

        Assert.False(TonMnemonic.Validate("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon art"));
        string generated = TonMnemonic.Generate();
        Assert.True(TonMnemonic.Validate(generated));
        using var account = TonAccount.FromMnemonic(generated);
        Assert.StartsWith("UQ", account.Address.Value);
    }

    [Fact]
    public void JettonTransfer_MatchesTonCore()
    {
        var body = TonJetton.TransferBody(1_000_000, Dest, Dest, BigInteger.One, "hi");
        Assert.Equal("03e891eaf88461ad45034c6db5b719b182610048ff39d3c200251e23ef577d88", Hex(body.Hash()));
    }

    [Theory]
    [InlineData("EQAqbua3_0G_7K_jgzhjJceolfT-TONGsY65wUoBUtZinKC1", true)]
    [InlineData("EQAqbua3/0G/7K/jgzhjJceolfT+TONGsY65wUoBUtZinKC1", true)]
    [InlineData("EQAqbua3_0G_7K_jgzhjJceolfT-TONGsY65wUoBUtZinKC2", false)]
    [InlineData("-1:" + "0000000000000000000000000000000000000000000000000000000000000000", true)]
    [InlineData("0:abc", false)]
    public void Addresses(string address, bool valid) => Assert.Equal(valid, TonAddress.IsValid(address));

    [Fact]
    public void HdWallet_UsesSlip10Path()
    {
        using var wallet = HdWallet.FromMnemonic("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about");
        using var a = wallet.GetTonAccount();
        Assert.Equal("m/44'/607'/0'", a.DerivationPath);
        Assert.Equal(48, a.Address.Value.Length);
    }

    private static TonMessage[] Messages() =>
    [
        TonMessage.WithComment(Dest, 123_000_000, "hello ton"),
        new TonMessage(Dest, 5, Bounce: true),
    ];
}
