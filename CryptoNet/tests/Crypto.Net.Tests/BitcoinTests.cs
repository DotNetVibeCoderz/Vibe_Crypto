using Crypto.Net.Bitcoin;
using Crypto.Net.Native;
using Crypto.Net.Testing;

namespace Crypto.Net.Tests;

public class BitcoinTests
{
    private static string Reverse(string hex)
    {
        byte[] b = Convert.FromHexString(hex);
        Array.Reverse(b);
        return Convert.ToHexStringLower(b);
    }

    [Fact]
    public void Addresses_ParseAndValidate()
    {
        var info = BitcoinAddress.Parse(TestVectors.BtcMainnetSegwit);
        Assert.Equal(BitcoinAddressType.SegWitP2WPKH, info.Type);
        Assert.Equal("751e76e8199196d454941c45d1b3a323f1433bd6", Convert.ToHexStringLower(info.Payload));
        Assert.Equal("0014751e76e8199196d454941c45d1b3a323f1433bd6", Convert.ToHexStringLower(info.ScriptPubKey));

        Assert.Equal(BitcoinAddressType.TaprootP2TR, BitcoinAddress.Parse(TestVectors.AbandonBitcoinTaproot).Type);
        Assert.Equal(BitcoinAddressType.LegacyP2PKH, BitcoinAddress.Parse(TestVectors.AbandonBitcoinLegacy).Type);
        Assert.Equal(BitcoinAddressType.P2SH, BitcoinAddress.Parse("3J98t1WpEZ73CNmQviecrnyiWrnqRhWNLy").Type);

        Assert.False(BitcoinAddress.IsValid(TestVectors.BtcMainnetSegwit, BitcoinNetwork.Testnet));
        Assert.False(BitcoinAddress.IsValid("bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t5"));
        Assert.False(BitcoinAddress.IsValid("1BgGZ9tcN4rm9KBzDn7KprQz87SZ26SAMJ"));
    }

    [Fact]
    public void Generator_KeyAddresses()
    {
        byte[] one = new byte[32];
        one[31] = 1;
        Assert.Equal("1BgGZ9tcN4rm9KBzDn7KprQz87SZ26SAMH", BitcoinAddress.FromPrivateKey(one, BitcoinAddressType.LegacyP2PKH).Value);
        Assert.Equal(TestVectors.BtcMainnetSegwit, BitcoinAddress.FromPrivateKey(one, BitcoinAddressType.SegWitP2WPKH).Value);
        Assert.Equal("tb1qw508d6qejxtdg4y5r3zarvary0c5xw7kxpjzsx", BitcoinAddress.FromPrivateKey(one, BitcoinAddressType.SegWitP2WPKH, BitcoinNetwork.Testnet).Value);
        Assert.StartsWith("bc1p", BitcoinAddress.FromPrivateKey(one, BitcoinAddressType.TaprootP2TR).Value);
    }

    [Fact]
    public void Wif_Vector()
    {
        byte[] key = Convert.FromHexString(TestVectors.WifPrivateKey);
        Assert.Equal(TestVectors.WifCompressed, BitcoinAddress.WifFromPrivateKey(key));
        using var back = BitcoinAddress.PrivateKeyFromWif(TestVectors.WifCompressed, out bool compressed);
        Assert.True(compressed);
        Assert.Equal(key, back.ToArray());
    }

    [Fact]
    public void Bip143_NativeP2wpkh_Example()
    {
        var tx = new BitcoinTransaction { Version = 1, LockTime = 0x11 };
        tx.AddInput(Reverse("fff7f7881a8099afa6940d42d1e7f6362bec38171ea3edf433541db4e4ad969f"), 0, 0xffffffee);
        tx.AddInput(Reverse("ef51e1b804cc89d182d279655c3aa89e815b1b309fe287d9b2b55d57b90ec68a"), 1, 0xffffffff);
        tx.AddOutput(Convert.FromHexString("76a9148280b37df378db99f66f85c95a783a76ac7a6d5988ac"), 112340000);
        tx.AddOutput(Convert.FromHexString("76a9143bde42dbee7e4dbe6a21b2d50ce2f0167faa815988ac"), 223450000);

        Assert.Equal("0100000002fff7f7881a8099afa6940d42d1e7f6362bec38171ea3edf433541db4e4ad969f0000000000eeffffffef51e1b804cc89d182d279655c3aa89e815b1b309fe287d9b2b55d57b90ec68a0100000000ffffffff02202cb206000000001976a9148280b37df378db99f66f85c95a783a76ac7a6d5988ac9093510d000000001976a9143bde42dbee7e4dbe6a21b2d50ce2f0167faa815988ac11000000",
            tx.ToHex());

        byte[] key = Convert.FromHexString("619c335025c7f4012e556c2a58b2506e30b8511b53ade95ea316fd8c3286feb9");
        byte[] pub = CryptoNative.Secp256k1GetPublicKey(key);
        Assert.Equal("025476c2e83188368da1ff3e292e7acafcdb3566bb0ad253f62fc70f07aeee6357", Convert.ToHexStringLower(pub));

        byte[] pkh = CryptoNative.Hash160(pub);
        byte[] sighash = tx.GetSegwitV0SigHash(1, BitcoinScript.P2PKH(pkh), 600000000);
        Assert.Equal("c37af31116d1b27caf68aae9e3ac82f1477929014d5b917657d0eb49478cb670", Convert.ToHexStringLower(sighash));

        byte[] derSig = BitcoinTransaction.EcdsaDer(key, sighash, SigHashType.All);
        Assert.Equal("304402203609e17b84f6a7d30c80bfa610b5b4542f32a8a0d5447a12fb1366d7f01cc44a0220573a954c4518331561406f90300e8f3358f51928d43c212a8caed02de67eebee01",
            Convert.ToHexStringLower(derSig));
    }

    [Fact]
    public void SignAllInputTypes_ProducesVerifiableSignatures()
    {
        byte[] key = Convert.FromHexString(TestVectors.Eip155PrivateKey);
        using var p2wpkh = new BitcoinAccount(key, BitcoinAddressType.SegWitP2WPKH, BitcoinNetwork.Testnet);
        using var p2tr = new BitcoinAccount(key, BitcoinAddressType.TaprootP2TR, BitcoinNetwork.Testnet);
        using var p2pkh = new BitcoinAccount(key, BitcoinAddressType.LegacyP2PKH, BitcoinNetwork.Testnet);

        var utxos = new List<BitcoinUtxo>
        {
            new(new string('a', 64), 0, 50_000, p2wpkh.ScriptPubKey),
            new(new string('b', 64), 1, 70_000, p2tr.ScriptPubKey),
            new(new string('c', 64), 2, 30_000, p2pkh.ScriptPubKey),
        };
        var tx = new BitcoinTransaction();
        foreach (var u in utxos) tx.AddInput(u.TxId, u.Vout);
        tx.AddOutput("tb1qw508d6qejxtdg4y5r3zarvary0c5xw7kxpjzsx", 140_000, BitcoinNetwork.Testnet);
        tx.SignAllInputs(key, utxos);

        // P2WPKH: DER signature over the BIP-143 hash
        byte[] pub = CryptoNative.Secp256k1GetPublicKey(key);
        Assert.Equal(2, tx.Inputs[0].Witness.Count);
        Assert.Equal(pub, tx.Inputs[0].Witness[1]);

        // P2TR: 64-byte Schnorr signature valid for the tweaked output key
        Assert.Single(tx.Inputs[1].Witness);
        byte[] schnorr = tx.Inputs[1].Witness[0];
        Assert.Equal(64, schnorr.Length);
        byte[] outputKey = utxos[1].ScriptPubKey[2..];
        Assert.True(CryptoNative.SchnorrVerify(outputKey, tx.GetTaprootSigHash(1, utxos), schnorr));

        // P2PKH: scriptSig carries <sig> <pubkey>
        Assert.NotEmpty(tx.Inputs[2].ScriptSig);
        Assert.Empty(tx.Inputs[2].Witness);

        Assert.True(tx.HasWitness);
        Assert.True(tx.VirtualSize < tx.Serialize().Length);
        Assert.Equal(64, tx.TxId.Length);
        Assert.NotEqual(tx.TxId, tx.WTxId);
    }

    [Fact]
    public void Signing_RejectsMismatchedKey()
    {
        byte[] key = Convert.FromHexString(TestVectors.Eip155PrivateKey);
        byte[] other = Convert.FromHexString(TestVectors.WifPrivateKey);
        using var account = new BitcoinAccount(other, BitcoinAddressType.SegWitP2WPKH);
        var utxo = new BitcoinUtxo(new string('a', 64), 0, 10_000, account.ScriptPubKey);
        var tx = new BitcoinTransaction().AddInput(utxo.TxId, 0).AddOutput(account.ScriptPubKey, 9_000);
        Assert.Throws<InvalidOperationException>(() => tx.SignAllInputs(key, [utxo]));
    }

    [Fact]
    public void CoinSelection_HandlesChangeDustAndInsufficientFunds()
    {
        byte[] script = BitcoinScript.P2WPKH(new byte[20]);
        var utxos = new[] { new BitcoinUtxo(new string('1', 64), 0, 100_000, script), new BitcoinUtxo(new string('2', 64), 0, 20_000, script) };

        var s = CoinSelector.Select(utxos, 50_000, 2m);
        Assert.Single(s.Inputs);
        Assert.Equal(100_000, s.Inputs.Sum(i => i.ValueSats));
        Assert.Equal(100_000 - 50_000 - s.FeeSats, s.ChangeSats);

        var dust = CoinSelector.Select(utxos, 99_500, 1m);
        Assert.Equal(0, dust.ChangeSats);

        Assert.Throws<InvalidOperationException>(() => CoinSelector.Select(utxos, 500_000, 1m));
    }

    [Fact]
    public void Der_EncodesHighBitAndLeadingZeros()
    {
        byte[] rs = new byte[64];
        rs[0] = 0x80; rs[63] = 1;
        byte[] der = BitcoinTransaction.ToDer(rs);
        Assert.Equal(0x30, der[0]);
        Assert.Equal(33, der[3]); // r padded with 0x00
        Assert.Equal(new byte[] { 0x02, 0x01, 0x01 }, der[^3..]);
    }
}
