using Crypto.Net.Core;
using Crypto.Net.Evm;
using Crypto.Net.Ledger;
using Crypto.Net.Native;

namespace Crypto.Net.Tests;

/// <summary>
/// APDU transcripts recorded by Ledger's hw-app-eth test suite (Speculos with its default seed), replayed against our
/// implementation. Every APDU we send must match byte for byte.
/// </summary>
public class LedgerTests
{
    private const string Path = "44'/60'/0'/0'/0";
    private const string SpeculosAddress = "0xCbA98362e199c41E1864D0923AF9646d3A648451";

    [Fact]
    public async Task GetConfiguration()
    {
        using var t = new ReplayLedgerTransport("=> e006000000\n<= 010101069000");
        var config = await new LedgerEthereumApp(t).GetConfigurationAsync();
        Assert.Equal(("1.1.6", true), (config.Version, config.ArbitraryDataEnabled));
    }

    [Fact]
    public async Task GetAddress_WithAndWithoutChainId()
    {
        using var t = new ReplayLedgerTransport("""
            => e002000015058000002c8000003c800000008000000000000000
            <= 4104df00ad3869baad7ce54f4d560ba7f268d542df8f2679a5898d78a690c3db8f9833d2973671cb14b088e91bdf7c0ab00029a576473c0e12f84d252e630bb3809b28436241393833363265313939633431453138363444303932334146393634366433413634383435319000
            => e00201001d058000002c8000003c8000000000000000000000000000000000000089
            <= 41047d8d3c470d1cfd8525d9537efdb92319a13a9bc9e336b6621fa5a664d2591b60fcd4f7882b0ff07d5ea0697050c7d23428daa5beaf6268cbac1369c278c6d8ea28366342434437334344386538613432383434363632663041306537364437463739416664393333649000
            """);
        var app = new LedgerEthereumApp(t);
        var a = await app.GetAddressAsync(Path);
        Assert.Equal(SpeculosAddress, a.Address);
        // The public key really belongs to the address.
        Assert.Equal(SpeculosAddress, EvmAddress.FromPublicKey(a.PublicKey).Value);

        var polygon = await app.GetAddressAsync("44'/60'/0'/0/0", display: true, chainId: 137);
        Assert.Equal("0x6cBCD73CD8e8a42844662f0A0e76D7F79Afd933d", polygon.Address);
        Assert.True(t.Completed);
    }

    [Fact]
    public async Task SignPersonalMessage_ApduAndSignatureLayout()
    {
        using var t = new ReplayLedgerTransport("""
            => e00800001d058000002c8000003c8000000080000000000000000000000474657374
            <= 1b8beafdd56521af1213d6d668a2aed262cc840e7174b642215aec013a1c88b2bd3a407b9125f1bfc015df6983ae8b87a34d54be367b4275834c3039622a73ee009000
            """);
        byte[] signature = await new LedgerEthereumApp(t).SignPersonalMessageAsync(Path, "test");
        // The device answers v‖r‖s; we return r‖s‖v (the usual 65-byte form).
        Assert.Equal("8beafdd56521af1213d6d668a2aed262cc840e7174b642215aec013a1c88b2bd", Convert.ToHexStringLower(signature[..32]));
        Assert.Equal(27, signature[64]);
        // hw-app-eth's recordings come from different seeds, so key consistency is checked against Speculos in CI instead.
        Assert.True(EvmAddress.IsValid(EvmMessageSigner.RecoverPersonalMessageSigner("test"u8, signature)));
    }

    [Fact]
    public async Task SignEip712_ApduAndSignatureLayout()
    {
        byte[] domain = Convert.FromHexString("c24f499b8c957196651b13edd64aaccc3980009674b2aea0966c8a56ba81278e");
        byte[] message = Convert.FromHexString("9d96be8a7cca396e711a3ba356bd9878df02a726d753ddb6cda3c507d888bc77");
        using var t = new ReplayLedgerTransport("""
            => e00c000055058000002c8000003c800000008000000000000000c24f499b8c957196651b13edd64aaccc3980009674b2aea0966c8a56ba81278e9d96be8a7cca396e711a3ba356bd9878df02a726d753ddb6cda3c507d888bc77
            <= 1c47937d12e45197f2f4c47fe34e88944ee10c8e9ee1faf7aa4658f5aab8e0d2bb026c0d81290478fbc45d5bc1308c4b7119ab43d986805413e7f85da5d94597e79000
            """);
        byte[] sig = await new LedgerEthereumApp(t).SignEip712HashedMessageAsync(Path, domain, message);
        Assert.Equal("47937d12e45197f2f4c47fe34e88944ee10c8e9ee1faf7aa4658f5aab8e0d2bb", Convert.ToHexStringLower(sig[..32]));
        Assert.Equal(28, sig[64]);
        byte[] digest = CryptoNative.Keccak256([0x19, 0x01, .. domain, .. message]);
        Assert.Equal(65, CryptoNative.Secp256k1RecoverPublicKey(digest, sig[..64], (byte)(sig[64] - 27)).Length);
    }

    [Fact]
    public async Task SignTransaction_Eip1559_MatchesRecordedApdu()
    {
        using var t = new ReplayLedgerTransport("""
            => e004000046058000002c8000003c80000000000000000000000002ef0306843b9aca008504a817c80082520894b2bb2b958afa2e96dab3f3ce7162b87daea39017872386f26fc1000080c0
            <= 003ccff815955e82c416dcf2ace0bf0aa5f479acd47f5152f0c1753cf68fb723746774b8bff4f776f2788bb19c87c22ca8a2933ea4415eed8d25a9ea0500c81ce19000
            """);
        var tx = new EvmTransaction
        {
            Type = EvmTransactionType.DynamicFee, ChainId = 3, Nonce = 6, MaxPriorityFeePerGas = 1_000_000_000, MaxFeePerGas = 20_000_000_000,
            GasLimit = 21000, To = "0xb2bb2b958afa2e96dab3f3ce7162b87daea39017", Value = 10_000_000_000_000_000,
        };
        var signed = await new LedgerEthereumApp(t).SignTransactionAsync("44'/60'/0'/0/0", tx);
        // Our RLP for the same fields matched the recorded APDU byte for byte; the signature attaches with y-parity 0.
        Assert.Equal(0, signed.RecoveryId);
        Assert.True(EvmAddress.IsValid(EvmTransaction.DecodeSigned(signed.RawBytes).RecoverSender()));
    }

    [Fact]
    public async Task SignTransaction_ChunksLongPayloads()
    {
        // From hw-app-eth's ERC-1155 clear-signing transcript: a 241-byte payload split at 255 bytes of APDU data.
        const string first = "058000002c8000003c80000000000000000000000002f8ee01808414dc9380850d54eb0ea883010ab494d07dc4262bcdbf85190c01c996b4c06a461d243080b8c4f242432a0000000000000000000000006cbcd73cd8e8a42844662f0a0e76d7f79afd933d000000000000000000000000b0b5b0106d69fe64545a60a68c014f7570d3f861000000000000000000000000000000000000000000000000000000000004041c000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000a00000000000000000000000000000000000000000000000000000";
        const string rest = "000000000000c0";
        using var t = new ReplayLedgerTransport($"""
            => e0040000ff{first}
            <= 9000
            => e004800007{rest}
            <= 016467047c39c2c3db9dbe8aa03802c80830beb659f0eed7470735098f2c44cfae7b2aa36e2f31c6e0d37a00b93789ef4eef4fd016dd3b92fa893e7e741a152be99000
            """);
        byte[] payload = Convert.FromHexString(first[42..] + rest);
        var (v, r, _) = await new LedgerEthereumApp(t).SignRawTransactionAsync("44'/60'/0'/0/0", payload);
        Assert.Equal(1, v);
        Assert.Equal("6467047c39c2c3db9dbe8aa03802c80830beb659f0eed7470735098f2c44cfae", Convert.ToHexStringLower(r));
        Assert.True(t.Completed);
    }

    [Fact]
    public async Task StatusWords_BecomeLedgerExceptions()
    {
        using var t = new ReplayLedgerTransport("=> e006000000\n<= 6985\n=> e006000000\n<= 6e00");
        var app = new LedgerEthereumApp(t);
        var rejected = await Assert.ThrowsAsync<LedgerException>(() => app.GetConfigurationAsync());
        Assert.True(rejected.IsUserRejection);
        var wrongApp = await Assert.ThrowsAsync<LedgerException>(() => app.GetConfigurationAsync());
        Assert.Contains("app is not open", wrongApp.Message);
    }

    [Fact]
    public void HidFraming_RoundTrips()
    {
        byte[] apdu = [0xe0, 0x04, 0x00, 0x00, 0xc8, .. Enumerable.Range(0, 200).Select(i => (byte)i)];
        var packets = LedgerHidFraming.Wrap(apdu);
        Assert.Equal(4, packets.Count);                                  // 2-byte length + 205 bytes over 59-byte payloads
        Assert.All(packets, p => Assert.Equal(64, p.Length));
        Assert.Equal("0101050000" + "00cd" + "e004", Convert.ToHexStringLower(packets[0][..9]));
        Assert.Equal("0101050001", Convert.ToHexStringLower(packets[1][..5]));
        Assert.Null(LedgerHidFraming.Unwrap(packets.Take(2).ToList()));
        Assert.Equal(apdu, LedgerHidFraming.Unwrap(packets));
        Assert.Equal("058000002c8000003c800000008000000000000000", Convert.ToHexStringLower(LedgerEthereumApp.SerializePath("44'/60'/0'/0'/0")));
    }
}

/// <summary>The Solana app, replaying the APDU sessions recorded in Ledger's hw-app-solana tests.</summary>
public class LedgerSolanaTests
{
    private const string Tx = "010001035eb9862fe23e544a2a0969cc157cb31fd72901cc2824d536a67fb8ee911e02363b9ba3a2ebaf40c1cd672a80a8e1932b982cca8264be33c39359701e113c3da20000000000000000000000000000000000000000000000000000000000000000030303030303030303030303030303030303030303030303030303030303030301020200010c020000002a00000000000000";

    [Fact]
    public async Task Configuration()
    {
        using var t = new ReplayLedgerTransport("=> e004000000\n<= 00000100069000");
        var c = await new LedgerSolanaApp(t).GetConfigurationAsync();
        Assert.Equal(("1.0.6", false, 0), (c.Version, c.BlindSigningEnabled, c.PublicKeyDisplayMode));
    }

    [Fact]
    public async Task Address_WithAndWithoutDisplay()
    {
        using var t = new ReplayLedgerTransport("""
            => e005000015058000002c800001f5800000008000000080000000
            <= 4d65a10662b9759d62bb59048366705454654cf4f9b4b3525cf314429e46c6919000
            => e005010015058000002c800001f5800000008000000080000000
            <= 4d65a10662b9759d62bb59048366705454654cf4f9b4b3525cf314429e46c6919000
            """);
        var app = new LedgerSolanaApp(t);
        string expected = CryptoNative.Base58Encode(Convert.FromHexString("4d65a10662b9759d62bb59048366705454654cf4f9b4b3525cf314429e46c691"));
        Assert.Equal(expected, await app.GetAddressAsync("44'/501'/0'/0'/0'"));
        Assert.Equal(expected, await app.GetAddressAsync("m/44'/501'/0/0/0", display: true));   // levels are hardened for the app
        Assert.True(t.Completed);
    }

    [Fact]
    public async Task SignTransaction_SignatureVerifiesForTheFeePayer()
    {
        using var t = new ReplayLedgerTransport($"""
            => e0060100a001028000002c800001f5{Tx}
            <= 1ad0702faddc8b2072c59547637c10e6affad2f186b69cf3288f2b029de2e309e1d73b73eb925a79f7b0d026ee07203d714e15807267001fbd3914de76a5490e9000
            """);
        byte[] message = Convert.FromHexString(Tx);
        byte[] sig = await new LedgerSolanaApp(t).SignMessageBytesAsync("44'/501'", message);
        Assert.True(CryptoNative.Ed25519Verify(message.AsSpan(4, 32), message, sig));
    }

    [Fact]
    public async Task LongTransaction_IsChunked()
    {
        const string message = "010001035eb9862fe23e544a2a0969cc157cb31fd72901cc2824d536a67fb8ee911e02363b9ba3a2ebaf40c1cd672a80a8e1932b982cca8264be33c39359701e113c3da20000000000000000000000000000000000000000000000000000000000000000030303030303030303030303030303030303030303030303030303030303030307020200010c020000002a00000000000000020200010c020000002a00000000000000020200010c020000002a00000000000000020200010c020000002a00000000000000020200010c020000002a00000000000000020200010c020000002a00000000000000020200010c020000002a00000000000000";
        using var t = new ReplayLedgerTransport("""
            => e0060102ff01028000002c800001f5010001035eb9862fe23e544a2a0969cc157cb31fd72901cc2824d536a67fb8ee911e02363b9ba3a2ebaf40c1cd672a80a8e1932b982cca8264be33c39359701e113c3da20000000000000000000000000000000000000000000000000000000000000000030303030303030303030303030303030303030303030303030303030303030307020200010c020000002a00000000000000020200010c020000002a00000000000000020200010c020000002a00000000000000020200010c020000002a00000000000000020200010c020000002a00000000000000020200010c020000002a00000000000000020200010c020000002a
            <= 9000
            => e00601010700000000000000
            <= d9ed529ab24ab4e796c006cf85e7e51db85825b31d3477dd3bf8350745b7a9cdc29840442d96dbeca73289a06841655d9f5c342bd6f697dcfdb6dadf8a0784049000
            """);
        byte[] m = Convert.FromHexString(message);
        byte[] sig = await new LedgerSolanaApp(t).SignMessageBytesAsync("44'/501'", m);
        Assert.True(CryptoNative.Ed25519Verify(m.AsSpan(4, 32), m, sig));
        Assert.True(t.Completed);
    }

    [Fact]
    public async Task OffchainMessage()
    {
        byte[] formatted = LedgerSolanaApp.FormatOffchainMessage("Long Off-Chain Test Message.");
        Assert.Equal("ff736f6c616e61206f6666636861696e00001c004c6f6e67204f66662d436861696e2054657374204d6573736167652e", Convert.ToHexStringLower(formatted));
        using var t = new ReplayLedgerTransport("""
            => e00701003a01028000002c800001f5ff736f6c616e61206f6666636861696e00001c004c6f6e67204f66662d436861696e2054657374204d6573736167652e
            <= dd23d8f798aeb0085f97f3aae20e52866bf529e0d880959784ff0e91beaf6f2eb73b7b0ef484801399066b92561f6c523b27f9579e17d0f8106b33edc5f45b079000
            """);
        byte[] sig = await new LedgerSolanaApp(t).SignOffchainMessageAsync("44'/501'", formatted);
        Assert.Equal(64, sig.Length);
        Assert.Equal(1, LedgerSolanaApp.FormatOffchainMessage("héllo")[17]);
        byte[] current = LedgerSolanaApp.FormatOffchainMessage("hi", ["11111111111111111111111111111112"]);
        Assert.Equal(16 + 1 + 32 + 1 + 1 + 32 + 2 + 2, current.Length);
        Assert.Equal((0, 0, 1, 2), (current[16], current[49], current[50], current[83]));
    }

    [Fact]
    public async Task BlindSigningRequired_IsReported()
    {
        using var t = new ReplayLedgerTransport("=> e004000000\n<= 6808");
        var ex = await Assert.ThrowsAsync<LedgerException>(() => new LedgerSolanaApp(t).GetConfigurationAsync());
        Assert.Equal(0x6808, ex.StatusWord);
    }
}

