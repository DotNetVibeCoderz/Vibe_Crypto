using System.Numerics;
using Crypto.Net.Core;
using Crypto.Net.Native;
using Crypto.Net.Polkadot;
using Crypto.Net.Testing;
using Crypto.Net.Wallet;

namespace Crypto.Net.Tests;

[Collection(BackendCollection.Name)] // sr25519 is native-only; keep away from tests that force the managed backend
public class PolkadotTests
{
    [Fact]
    public void Ss58_AliceVectors()
    {
        byte[] alice = Convert.FromHexString(TestVectors.AliceSr25519PublicKey);
        Assert.Equal(TestVectors.AliceSs58Generic, Ss58Address.Encode(alice, 42));
        Assert.Equal(TestVectors.AliceSs58Polkadot, Ss58Address.Encode(alice, 0));
        var (prefix, key) = Ss58Address.Decode(TestVectors.AliceSs58Polkadot);
        Assert.Equal(0, prefix);
        Assert.Equal(alice, key);
        Assert.Equal(TestVectors.AliceSs58Generic, Ss58Address.Convert(TestVectors.AliceSs58Polkadot, 42));
        Assert.True(Ss58Address.IsValid(TestVectors.AliceSs58Generic, 42));
        Assert.False(Ss58Address.IsValid(TestVectors.AliceSs58Generic, 0));
        Assert.False(Ss58Address.IsValid(TestVectors.AliceSs58Generic[..^1] + "Z"));
    }

    [Fact]
    public void Ss58_TwoBytePrefix_RoundTrips()
    {
        byte[] key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        foreach (ushort prefix in new ushort[] { 64, 255, 1284, 16383 })
        {
            string address = Ss58Address.Encode(key, prefix);
            var (p, k) = Ss58Address.Decode(address);
            Assert.Equal(prefix, p);
            Assert.Equal(key, k);
        }
    }

    [Fact]
    public void Suri_DevAccounts()
    {
        using var alice = PolkadotAccount.FromSuri("//Alice", chain: PolkadotChain.Westend);
        Assert.Equal(TestVectors.AliceSs58Generic, alice.Address.Value);
        Assert.Equal(TestVectors.AliceSs58Polkadot, alice.AddressFor(PolkadotChain.Polkadot));

        using var aliceEd = PolkadotAccount.FromSuri("//Alice", SignatureScheme.Ed25519, PolkadotChain.Westend);
        Assert.Equal(TestVectors.AliceEd25519Ss58Generic, aliceEd.Address.Value);

        byte[] sig = alice.SignMessage("hello polkadot");
        Assert.True(PolkadotAccount.VerifyMessage("hello polkadot", sig, alice.PublicKey.Span));
        Assert.False(PolkadotAccount.VerifyMessage("hello kusama", sig, alice.PublicKey.Span));
    }

    [Fact]
    public void HdWallet_SubstrateAccounts_MatchMnemonicDerivation()
    {
        using var wallet = HdWallet.FromMnemonic(PolkadotAccount.DevPhrase);
        using var viaWallet = wallet.GetPolkadotAccount("//Alice", PolkadotChain.Westend);
        Assert.Equal(TestVectors.AliceSs58Generic, viaWallet.Address.Value);

        using var root = wallet.GetPolkadotAccount();
        using var rootDirect = PolkadotAccount.FromMnemonic(PolkadotAccount.DevPhrase);
        Assert.Equal(rootDirect.Address, root.Address);
    }

    [Fact]
    public void Scale_CompactVectors()
    {
        Assert.Equal("00", Convert.ToHexStringLower(Scale.EncodeCompact(0)));
        Assert.Equal("fc", Convert.ToHexStringLower(Scale.EncodeCompact(63)));
        Assert.Equal("0101", Convert.ToHexStringLower(Scale.EncodeCompact(64)));
        Assert.Equal("feff0300", Convert.ToHexStringLower(Scale.EncodeCompact(65535)));
        Assert.Equal("0300000040", Convert.ToHexStringLower(Scale.EncodeCompact(1073741824)));
        foreach (BigInteger v in new BigInteger[] { 0, 1, 63, 64, 16383, 16384, 1 << 30, BigInteger.Pow(2, 64), BigInteger.Pow(2, 128) - 1 })
        {
            var r = new ScaleReader(Scale.EncodeCompact(v));
            Assert.Equal(v, r.Compact());
        }
    }

    [Fact]
    public void MortalEra_Encoding()
    {
        // Era::mortal(64, 42): low nibble = log2(64) - 1 = 5, phase 42 in the upper bits => 0x02a5 little-endian

        Assert.Equal(new byte[] { 0xa5, 0x02 }, Extrinsic.EncodeMortalEra(64, 42));
    }
}
