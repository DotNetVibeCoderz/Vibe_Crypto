using System.Security.Cryptography;
using Crypto.Net.Bitcoin;
using Crypto.Net.Cosmos;
using Crypto.Net.Evm;
using Crypto.Net.Native;
using Crypto.Net.Solana;
using Crypto.Net.Testing;
using Crypto.Net.Wallet;

namespace Crypto.Net.Tests;

public class WalletTests
{
    [Theory]
    [InlineData("00000000000000000000000000000000", "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about")]
    [InlineData("7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f", "legal winner thank year wave sausage worth useful legal winner thank yellow")]
    [InlineData("80808080808080808080808080808080", "letter advice cage absurd amount doctor acoustic avoid letter advice cage above")]
    [InlineData("ffffffffffffffffffffffffffffffff", "zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo wrong")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000", "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon art")]
    public void Bip39_EntropyVectors_RoundTrip(string entropyHex, string mnemonic)
    {
        Assert.Equal(mnemonic, Mnemonic.FromEntropy(Convert.FromHexString(entropyHex)));
        Assert.Equal(entropyHex, Convert.ToHexStringLower(Mnemonic.ToEntropy(mnemonic)));
        Assert.True(Mnemonic.Validate(mnemonic));
    }

    [Fact]
    public void Bip39_SeedVectors()
    {
        Assert.Equal(TestVectors.Bip39ExpectedSeedHex, Convert.ToHexStringLower(Mnemonic.ToSeed(TestVectors.Bip39TestMnemonic)));
        Assert.Equal(TestVectors.Bip39TrezorSeedHex, Convert.ToHexStringLower(Mnemonic.ToSeed(TestVectors.Bip39TestMnemonic, "TREZOR")));
    }

    [Fact]
    public void Bip39_RejectsBadInput_AndNormalizesWhitespace()
    {
        Assert.False(Mnemonic.Validate("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon"));
        Assert.False(Mnemonic.Validate("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon notaword"));
        Assert.False(Mnemonic.Validate(""));
        Assert.True(Mnemonic.Validate("  Abandon abandon\tabandon abandon abandon abandon abandon abandon abandon abandon abandon   about \n"));
    }

    [Theory]
    [InlineData(12)]
    [InlineData(15)]
    [InlineData(18)]
    [InlineData(21)]
    [InlineData(24)]
    public void Bip39_Generate_ProducesValidPhrases(int words)
    {
        string m = Mnemonic.Generate(words);
        Assert.Equal(words, m.Split(' ').Length);
        Assert.True(Mnemonic.Validate(m));
        Assert.NotEqual(m, Mnemonic.Generate(words));
    }

    [Fact]
    public void Bip32_Vector1_ExtendedKeys()
    {
        using var master = HdKey.FromSeed(Convert.FromHexString(TestVectors.Bip32Vector1Seed));
        Assert.Equal(TestVectors.Bip32Vector1MasterXprv, master.ToExtendedPrivateKey());
        Assert.Equal(TestVectors.Bip32Vector1MasterXpub, master.ToExtendedPublicKey());

        using var leaf = master.Derive(TestVectors.Bip32Vector1LeafPath);
        Assert.Equal(TestVectors.Bip32Vector1LeafXprv, leaf.ToExtendedPrivateKey());
        Assert.Equal(5, leaf.Depth);

        using var parsed = HdKey.ParseExtendedPrivateKey(TestVectors.Bip32Vector1LeafXprv);
        Assert.True(parsed.PrivateKey.ContentEquals(leaf.PrivateKey.Span));
        Assert.Equal(TestVectors.Bip32Vector1LeafXprv, parsed.ToExtendedPrivateKey());
    }

    [Fact]
    public void Slip10_Vector1()
    {
        using var master = HdKey.FromSeed(Convert.FromHexString(TestVectors.Bip32Vector1Seed), HdCurve.Ed25519);
        using var leaf = master.Derive("m/0'/1'/2'/2'/1000000000'");
        Assert.Equal(TestVectors.Slip10Vector1LeafKey, Convert.ToHexStringLower(leaf.PrivateKey.Span));
    }

    [Fact]
    public void DerivationPath_ParseAndFormat()
    {
        uint[] p = DerivationPath.Parse("m/44'/60h/0H/0/7");
        Assert.Equal([0x8000002Cu, 0x8000003Cu, 0x80000000u, 0u, 7u], p);
        Assert.Equal("m/44'/60'/0'/0/7", DerivationPath.Format(p));
        Assert.Throws<FormatException>(() => DerivationPath.Parse("m/44'/x"));
        Assert.Throws<FormatException>(() => DerivationPath.Parse("m/2147483648"));
    }

    [Fact]
    public void HdWallet_AbandonMnemonic_MatchesReferenceWallets()
    {
        using var wallet = HdWallet.FromMnemonic(TestVectors.Bip39TestMnemonic);

        using var eth = wallet.GetEvmAccount();
        Assert.Equal(TestVectors.AbandonEthereum, eth.Address.Value);

        using var legacy = wallet.GetBitcoinAccount(BitcoinAddressType.LegacyP2PKH);
        Assert.Equal(TestVectors.AbandonBitcoinLegacy, legacy.Address.Value);

        using var segwit = wallet.GetBitcoinAccount(BitcoinAddressType.SegWitP2WPKH);
        Assert.Equal(TestVectors.AbandonBitcoinSegwit, segwit.Address.Value);

        using var taproot = wallet.GetBitcoinAccount(BitcoinAddressType.TaprootP2TR);
        Assert.Equal(TestVectors.AbandonBitcoinTaproot, taproot.Address.Value);

        Assert.Equal(TestVectors.AbandonBip84Zpub, wallet.GetBitcoinAccountXpub(BitcoinAddressType.SegWitP2WPKH));

        using var cosmos = wallet.GetCosmosAccount();
        Assert.Equal(TestVectors.AbandonCosmos, cosmos.Address.Value);

        using var sol = wallet.GetSolanaAccount();
        Assert.Equal(TestVectors.AbandonSolana, sol.Address.Value);
        Assert.Equal("m/44'/501'/0'/0'", sol.DerivationPath);
    }

    [Fact]
    public void HdWallet_RevealMnemonic_AndDispose()
    {
        var wallet = HdWallet.Generate(24);
        string phrase = wallet.RevealMnemonic();
        Assert.Equal(24, phrase.Split(' ').Length);
        wallet.Dispose();
        Assert.Throws<ObjectDisposedException>(() => wallet.RevealMnemonic());
    }

    [Fact]
    public void KeyStore_Pbkdf2OfficialVector_Decrypts()
    {
        using var key = KeyStore.Decrypt(TestVectors.KeystorePbkdf2Json, TestVectors.KeystorePassword);
        Assert.Equal(TestVectors.KeystorePrivateKey, Convert.ToHexStringLower(key.Span));
    }

    [Fact]
    public void KeyStore_ScryptOfficialVector_Decrypts()
    {
        using var key = KeyStore.Decrypt(TestVectors.KeystoreScryptJson, TestVectors.KeystorePassword);
        Assert.Equal(TestVectors.KeystorePrivateKey, Convert.ToHexStringLower(key.Span));
    }

    [Fact]
    public void KeyStore_RoundTrip_AndWrongPassword()
    {
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        string json = KeyStore.Encrypt(secret, "correct horse", "0xAbC0000000000000000000000000000000000001", KeyStoreKdf.Light);
        Assert.Contains("\"aes-128-ctr\"", json);
        Assert.Equal("abc0000000000000000000000000000000000001", KeyStore.ReadAddress(json));

        using var decrypted = KeyStore.Decrypt(json, "correct horse");
        Assert.Equal(secret, decrypted.ToArray());
        Assert.Throws<CryptographicException>(() => KeyStore.Decrypt(json, "wrong"));

        string pbkdf2 = KeyStore.Encrypt(secret, "pw", kdf: new KeyStoreKdf.Pbkdf2Kdf(1000));
        using var d2 = KeyStore.Decrypt(pbkdf2, "pw");
        Assert.Equal(secret, d2.ToArray());
    }

    [Fact]
    public void SecureBuffer_ZeroesOnDispose()
    {
        var buffer = SecureBuffer.FromHex("0x0102030405");
        Assert.Equal(5, buffer.Length);
        Assert.True(buffer.ContentEquals([1, 2, 3, 4, 5]));
        buffer.Dispose();
        Assert.True(buffer.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => buffer.Span.Length);
    }
}
