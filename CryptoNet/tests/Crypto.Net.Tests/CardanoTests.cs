using Crypto.Net.Cardano;
using Crypto.Net.Native;
using Crypto.Net.Wallet;

namespace Crypto.Net.Tests;

/// <summary>Cardano keys, addresses and transactions cross-checked with @emurgo/cardano-serialization-lib 13.2.</summary>
public class CardanoTests
{
    private const string Abandon = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
    private const string TestEnterprise = "addr_test1vq8ac7qqy0vtulyl7wntmsxc6wex80gvcyjy33qffrhm7ss9hjl0y";
    private const string TestBase = "addr_test1qq8ac7qqy0vtulyl7wntmsxc6wex80gvcyjy33qffrhm7sh927ysx5sftuw0dlft05dz3c7revpf7jx0xnlcjz3g69mqkt5dmn";
    private static string Hex(byte[] b) => Convert.ToHexStringLower(b);

    [Fact]
    public void IcarusRoot_MatchesCsl()
    {
        Assert.Equal("60ce7dbec3616e9fc17e0c32578b3f380337b1b61a1f3cb9651aee30670e6f53970419a23a2e4e4082d12bf78faa8645dfc882cee2ae7179e2b07fe88098abb2072310084784c7308182dbbdb1449b2706586f1ff5cbf13d15e9b6e78c15f067", Hex(Mnemonic.ToIcarusRootKey(Abandon)));
        Assert.Equal("48e2488a8fdb4566b31f76964e16276fc24c085cc8970d6ee38f147ccce3754f7c3a3e69f8162ef540392507317c60b50d27d0eaf510f7a43211d2d3dbf28263a4057463fa4d8998b61bf36203171c51292804c9f1bb11cbbb7105649efe3865", Hex(Mnemonic.ToIcarusRootKey(Abandon, "foo")));
    }

    [Fact]
    public void Account_AddressesMatchCsl()
    {
        using var mainnet = CardanoAccount.FromMnemonic(Abandon);
        Assert.Equal("7ea09a34aebb13c9841c71397b1cabfec5ddf950405293dee496cac2f437480a", Hex(mainnet.PublicKey.ToArray()));
        Assert.Equal("012f5dc3115b8a07981e6e50f5a671e2c6fbb26c3ffde1cd1dcaf40a7fe8f160", Hex(mainnet.StakePublicKey!));
        Assert.Equal("0fdc780023d8be7c9ff3a6bdc0d8d3b263bd0cc12448c40948efbf42", Hex(mainnet.PaymentKeyHash));
        Assert.Equal("addr1qy8ac7qqy0vtulyl7wntmsxc6wex80gvcyjy33qffrhm7sh927ysx5sftuw0dlft05dz3c7revpf7jx0xnlcjz3g69mq4afdhv", mainnet.Address.Value);
        Assert.Equal("addr1vy8ac7qqy0vtulyl7wntmsxc6wex80gvcyjy33qffrhm7ss7lxrqp", mainnet.EnterpriseAddress);
        Assert.Equal("stake1u8j40zgr2gy4788kl54h6x3gu0pukq5lfr8nflufpg5dzaskqlx2l", mainnet.RewardAddress);

        using var preprod = CardanoAccount.FromMnemonic(Abandon, network: CardanoChain.Preprod);
        Assert.Equal(TestBase, preprod.Address.Value);
        Assert.Equal(TestEnterprise, preprod.EnterpriseAddress);
        Assert.Equal("stake_test1urj40zgr2gy4788kl54h6x3gu0pukq5lfr8nflufpg5dzas324ywz", preprod.RewardAddress);
        Assert.Equal("217635a4afcf319ca670c1eeeab1378e16bb84c36c983f55f03b1f317e2bfa11d980ad8c4a60bf288dbf65304ef8495b7a19725be63d1a25996940402ab4c704", Hex(preprod.Sign("hello cardano"u8)));
    }

    [Fact]
    public void AccountXpub_DerivesTheSameAddressesWithoutPrivateKeys()
    {
        using var wallet = HdWallet.FromMnemonic(Abandon);
        byte[] xpub = wallet.GetCardanoAccountXpub();
        Assert.Equal("beb7e770b3d0f1932b0a2f3a63285bf9ef7d3e461d55446d6a3911d8f0ee55c0b0e2df16538508046649d0e6d5b32969555a23f2f1ebf2db2819359b0d88bd16", Hex(xpub));
        Assert.Equal(TestBase, CardanoWalletExtensions.DeriveCardanoAddress(xpub, 0, CardanoChain.Preprod));
        using var second = wallet.GetCardanoAccount(index: 1, network: CardanoChain.Preprod);
        Assert.Equal(second.Address.Value, CardanoWalletExtensions.DeriveCardanoAddress(xpub, 1, CardanoChain.Preprod));
    }

    [Fact]
    public void Transaction_MatchesCsl()
    {
        using var account = CardanoAccount.FromMnemonic(Abandon, network: CardanoChain.Preprod);
        var tx = new CardanoTransaction
        {
            Inputs = [new(new string('1', 64), 1), new(string.Concat(Enumerable.Repeat("0a", 32)), 0)],
            Outputs = [new(TestEnterprise, 1_500_000), new(TestBase, 8_300_000)],
            Fee = 170_000,
            Ttl = 90_000_000,
        };
        Assert.Equal("a400d90102828258201111111111111111111111111111111111111111111111111111111111111111018258200a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a00018282581d600fdc780023d8be7c9ff3a6bdc0d8d3b263bd0cc12448c40948efbf421a0016e360825839000fdc780023d8be7c9ff3a6bdc0d8d3b263bd0cc12448c40948efbf42e557890352095f1cf6fd2b7d1a28e3c3cb029f48cf34ff890a28d1761a007ea5e0021a00029810031a055d4a80", Hex(tx.SerializeBody()));
        Assert.Equal("30db60a00656e19c8ba3a1bd3d148121e80cdd808f209127a9b470cef3d79a25", tx.Id);
        byte[] signed = tx.Sign(account);
        Assert.Equal("84a400d90102828258201111111111111111111111111111111111111111111111111111111111111111018258200a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a00018282581d600fdc780023d8be7c9ff3a6bdc0d8d3b263bd0cc12448c40948efbf421a0016e360825839000fdc780023d8be7c9ff3a6bdc0d8d3b263bd0cc12448c40948efbf42e557890352095f1cf6fd2b7d1a28e3c3cb029f48cf34ff890a28d1761a007ea5e0021a00029810031a055d4a80a100d90102818258207ea09a34aebb13c9841c71397b1cabfec5ddf950405293dee496cac2f437480a5840016330b04c3500e42197be94c32e2d0b38210c0904d68a09ac3c6d77de59948c23a803bb7d87476b41d652adc2791d42a9764c32cd5dd0591ec9ff31a061f102f5f6", Hex(signed));
        Assert.Equal(tx.Id, CardanoTransaction.IdOf(signed));
    }

    [Fact]
    public void MultiAssetsAndMessage_MatchCsl()
    {
        using var account = CardanoAccount.FromMnemonic(Abandon, network: CardanoChain.Preprod);
        string p1 = string.Concat(Enumerable.Repeat("01", 28)), p2 = string.Concat(Enumerable.Repeat("02", 28));
        var output = new CardanoOutput(TestEnterprise, 2_000_000,
        [
            new(p2, Convert.ToHexString("zz"u8), 7), new(p2, Convert.ToHexString("a"u8), 9), new(p2, "", 1),
            new(p1, Convert.ToHexString("TOKEN"u8), 1_000_000_000_000),
        ]);
        var tx = new CardanoTransaction
        {
            Inputs = [new(new string('1', 64), 1)],
            Outputs = [output],
            Fee = 200_000,
            Ttl = 90_000_000,
        };
        // The reference message has two chunks, so build its auxiliary data explicitly.
        var w = new System.Formats.Cbor.CborWriter();
        w.WriteStartMap(1); w.WriteUInt64(674); w.WriteStartMap(1); w.WriteTextString("msg");
        w.WriteStartArray(2); w.WriteTextString("hello from Crypto.Net"); w.WriteTextString("xxxxxxxxxx"); w.WriteEndArray(); w.WriteEndMap(); w.WriteEndMap();
        tx.AuxiliaryData = w.Encode();
        Assert.Equal("a11902a2a1636d7367827568656c6c6f2066726f6d2043727970746f2e4e65746a78787878787878787878", Hex(tx.AuxiliaryData));
        Assert.Equal("65b9b9ead62b277ae9a6b5ea724359d5911eba3c4658118a23d8a3b7d038a719", tx.Id);
        byte[] signed = tx.Sign(account);
        Assert.Equal(368, signed.Length);
        Assert.Equal(171_573UL, new CardanoProtocolParameters(44, 155_381, 4310, 16384).MinFee(signed.Length));
        Assert.Equal(1_228_350UL, output.MinLovelace(4310));
        Assert.Equal("hello from Crypto.Netxxxxxxxxxx", CardanoMetadata.ReadMessage(tx.AuxiliaryData));
    }

    [Fact]
    public void Message_SplitsInto64ByteChunks()
    {
        string text = new string('é', 40); // 80 UTF-8 bytes
        var aux = CardanoMetadata.Message(text);
        Assert.Equal(text, CardanoMetadata.ReadMessage(aux));
        var r = new System.Formats.Cbor.CborReader(aux);
        r.ReadStartMap(); r.ReadUInt64(); r.ReadStartMap(); r.ReadTextString();
        Assert.Equal(2, r.ReadStartArray());
    }

    [Fact]
    public void BuildTransfer_SelectsUtxosReturnsAssetsAndPaysExactFee()
    {
        using var account = CardanoAccount.FromMnemonic(Abandon, network: CardanoChain.Preprod);
        var pp = new CardanoProtocolParameters(44, 155_381, 4310, 16384);
        string policy = string.Concat(Enumerable.Repeat("ab", 28));
        var utxos = new List<CardanoUtxo>
        {
            new(new(new string('1', 64), 0), 3_000_000, []),
            new(new(new string('2', 64), 1), 10_000_000, [new(policy, "4e4654", 1)]),
            new(new(new string('3', 64), 0), 1_200_000, []),
        };
        var tx = KoiosClient.BuildTransfer(account, utxos, [new CardanoOutput(TestEnterprise, 5_000_000)], pp, 1000, "thanks");
        Assert.Single(tx.Inputs);
        Assert.Equal(new string('2', 64), tx.Inputs[0].TxHash);
        var change = tx.Outputs[1];
        Assert.Equal(account.Address.Value, change.Address);
        Assert.Equal("4e4654", Assert.Single(change.Assets!).AssetNameHex);
        Assert.Equal(pp.MinFee(tx.Sign(account).Length), tx.Fee);
        Assert.Equal(10_000_000UL, tx.Outputs.Aggregate(0UL, (s, o) => s + o.Lovelace) + tx.Fee);

        Assert.Throws<InvalidOperationException>(() => KoiosClient.BuildTransfer(account, utxos, [new CardanoOutput(TestEnterprise, 20_000_000)], pp, 1000));
    }

    [Theory]
    [InlineData(TestBase, true)]
    [InlineData("stake1u8j40zgr2gy4788kl54h6x3gu0pukq5lfr8nflufpg5dzaskqlx2l", true)]
    [InlineData("Ae2tdPwUPEZFRbyhz3cpfC2CumGzNkFBN2L42rcUc2yjQpEkxDbkPodpMAi", true)]
    [InlineData("addr1qy8ac7qqy0vtulyl7wntmsxc6wex80gvcyjy33qffrhm7sh927ysx5sftuw0dlft05dz3c7revpf7jx0xnlcjz3g69mqkt5dmn", false)]
    [InlineData("Ae2tdPwUPEZFRbyhz3cpfC2CumGzNkFBN2L42rcUc2yjQpEkxDbkPodpMAj", false)]
    public void Addresses(string address, bool valid) => Assert.Equal(valid, CardanoAddress.IsValid(address));
}
