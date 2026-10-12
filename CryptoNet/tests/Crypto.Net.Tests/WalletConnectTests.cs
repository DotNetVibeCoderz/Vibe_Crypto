using System.Text.Json.Nodes;
using Crypto.Net.Evm;
using Crypto.Net.Testing;
using Crypto.Net.WalletConnect;
using Crypto.Net.Wallet;

namespace Crypto.Net.Tests;

/// <summary>WalletConnect cryptography cross-checked with @walletconnect/utils and relay-auth (2.21).</summary>
public class WalletConnectTests
{
    private static readonly byte[] A = Enumerable.Repeat((byte)0x11, 32).ToArray();
    private static readonly byte[] B = Enumerable.Repeat((byte)0x22, 32).ToArray();
    private const string Sym = "dc5774cb6f12055709d3faed4fbe6b36f192c9a610a9fa0ad748c9f89975083f";

    [Fact]
    public void KeyAgreement_MatchesSdk()
    {
        byte[] pubB = Crypto.Net.Native.CryptoNative.X25519GetPublicKey(B);
        Assert.Equal("0faa684ed28867b97f4a6a2dee5df8ce974e76b7018e3f22a1c4cf2678570f20", Convert.ToHexStringLower(pubB));
        byte[] sym = WalletConnectCrypto.DeriveSymKey(A, pubB);
        Assert.Equal(Sym, Convert.ToHexStringLower(sym));
        Assert.Equal(sym, WalletConnectCrypto.DeriveSymKey(B, Crypto.Net.Native.CryptoNative.X25519GetPublicKey(A)));
        Assert.Equal("b2bfbfd11c76cffd30bfd6c29163d24eeb5612ad7280ef7fea544d406a81f084", WalletConnectCrypto.Topic(sym));
    }

    [Fact]
    public void Envelopes_MatchSdk()
    {
        byte[] sym = Convert.FromHexString(Sym);
        const string message = "{\"id\":1,\"jsonrpc\":\"2.0\",\"method\":\"wc_sessionPing\",\"params\":{}}";
        string type0 = WalletConnectCrypto.Encrypt(sym, message, Enumerable.Repeat((byte)0x33, 12).ToArray());
        Assert.Equal("ADMzMzMzMzMzMzMzMzcHIJWfCkDTSehVep8Y047ES2rUGgsWOPF1iPNsgtJ/MxQk4o2qsw/IMmaDkRYAELIzwp0BEEJC4BD2H2ahGJJhtlL5bAj74rl0MJxUeg==", type0);
        Assert.Equal(message, WalletConnectCrypto.Decrypt(sym, type0).Message);

        byte[] pubA = Convert.FromHexString("7b4e909bbe7ffe44c465a220037d608ee35897d31ef972f07f74892cb0f73f13");
        string type1 = WalletConnectCrypto.Encrypt(sym, "hi", Enumerable.Repeat((byte)0x44, 12).ToArray(), pubA);
        Assert.Equal("AXtOkJu+f/5ExGWiIAN9YI7jWJfTHvly8H90iSyw9z8TREREREREREREREREbFWDKkFlVXyTNxbA3r/6qrsQ", type1);
        var (text, sender) = WalletConnectCrypto.Decrypt(sym, type1);
        Assert.Equal("hi", text);
        Assert.Equal(pubA, sender);

        byte[] tampered = Convert.FromBase64String(type0);
        tampered[^1] ^= 1;
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => WalletConnectCrypto.Decrypt(sym, Convert.ToBase64String(tampered)));
    }

    [Fact]
    public void RelayJwt_MatchesRelayAuth()
    {
        string jwt = WalletConnectCrypto.RelayJwt(Enumerable.Repeat((byte)0x55, 32).ToArray(), new string('6', 64), "wss://relay.walletconnect.org", 1791900000, 86400);
        Assert.Equal("eyJhbGciOiJFZERTQSIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJkaWQ6a2V5Ono2TWtzcDlzZlZLVnBXQWk0M25pSExYZkdRNU5kQ1RFb2l5Y0xtckxQZWhxdVZxSyIsInN1YiI6IjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjYiLCJhdWQiOiJ3c3M6Ly9yZWxheS53YWxsZXRjb25uZWN0Lm9yZyIsImlhdCI6MTc5MTkwMDAwMCwiZXhwIjoxNzkxOTg2NDAwfQ.Q3q86RMonRHDt_LM-hcTdTv9ROgA8nlkaLX9YunZv-HCJs0Dif0oWrVYucPk_07GBGUzkNmWMK2QYi8Px-uNAA", jwt);
    }

    [Fact]
    public void PairingUri_MatchesSdkAndParses()
    {
        byte[] sym = Convert.FromHexString(Sym);
        string uri = WalletConnectCrypto.PairingUri(WalletConnectCrypto.Topic(sym), sym, 1791900300);
        Assert.Equal("wc:b2bfbfd11c76cffd30bfd6c29163d24eeb5612ad7280ef7fea544d406a81f084@2?relay-protocol=irn&symKey=dc5774cb6f12055709d3faed4fbe6b36f192c9a610a9fa0ad748c9f89975083f&expiryTimestamp=1791900300", uri);
        var (topic, key) = WalletConnectCrypto.ParsePairingUri(uri);
        Assert.Equal((WalletConnectCrypto.Topic(sym), Sym), (topic, Convert.ToHexStringLower(key)));
        Assert.Throws<FormatException>(() => WalletConnectCrypto.ParsePairingUri(uri.Replace("b2bf", "b3bf")));
        Assert.Throws<FormatException>(() => WalletConnectCrypto.ParsePairingUri("wc:abc@1?symKey=00"));
    }

    [Fact]
    public void EthereumNamespace()
    {
        var ns = WalletConnectEthereum.Namespace(1, 11155111)["eip155"];
        Assert.Equal(["eip155:1", "eip155:11155111"], ns.Chains);
        Assert.Contains("personal_sign", ns.Methods);
    }
}

/// <summary>Runs only when CRYPTONET_WALLETCONNECT_PROJECT_ID is set: a full session through Reown's relay.</summary>
public sealed class WalletConnectFactAttribute : FactAttribute
{
    public WalletConnectFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CRYPTONET_WALLETCONNECT_PROJECT_ID")))
            Skip = "Set CRYPTONET_WALLETCONNECT_PROJECT_ID to run against the WalletConnect relay";
    }
}

public class WalletConnectRelayTests
{
    private const string Abandon = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    [WalletConnectFact]
    public async Task AppAndWallet_PairApproveSignAndDisconnect()
    {
        string projectId = Environment.GetEnvironmentVariable("CRYPTONET_WALLETCONNECT_PROJECT_ID")!;
        await using var app = await WalletConnectClient.CreateAsync(new() { ProjectId = projectId, Metadata = new("Crypto.Net test app", "dapp side", "https://github.com/DotNetVibeCoderz/Vibe_Crypto") });
        await using var walletSide = await WalletConnectWallet.CreateAsync(new() { ProjectId = projectId, Metadata = new("Crypto.Net test wallet", "wallet side", "https://github.com/DotNetVibeCoderz/Vibe_Crypto") });

        using var hd = HdWallet.FromMnemonic(Abandon);
        using var account = hd.GetEvmAccount(0, EvmChain.Sepolia);
        var signer = account.CreateSigner();
        walletSide.RequestHandler = async request =>
        {
            if (request.Method != "personal_sign") throw new WalletConnectException(4001, "User rejected the request.");
            byte[] message = Convert.FromHexString(request.Params![0]!.GetValue<string>()[2..]);
            return JsonValue.Create("0x" + Convert.ToHexStringLower(await EvmMessageSigner.SignPersonalMessageAsync(signer, message)));
        };

        var pairing = await app.ConnectAsync(WalletConnectEthereum.Namespace(11155111));
        var proposal = await walletSide.PairAsync(pairing.Uri);
        Assert.Equal("Crypto.Net test app", proposal.Proposer.Name);
        await walletSide.ApproveAsync(proposal, new Dictionary<string, WalletConnectNamespace>
        {
            ["eip155"] = new(["eip155:11155111"], WalletConnectEthereum.DefaultMethods, WalletConnectEthereum.DefaultEvents, [$"eip155:11155111:{account.Address.Value}"]),
        });
        var session = await pairing.Approval;
        Assert.Equal("Crypto.Net test wallet", session.Peer.Name);

        var eth = new WalletConnectEthereum(session, 11155111);
        Assert.Equal(TestVectors.AbandonEthereum, eth.Address);
        string signature = await eth.PersonalSignAsync("Crypto.Net over WalletConnect");
        Assert.Equal(TestVectors.AbandonEthereum, EvmMessageSigner.RecoverPersonalMessageSigner("Crypto.Net over WalletConnect", signature));

        var ex = await Assert.ThrowsAsync<WalletConnectException>(() => eth.SendTransactionAsync("0x000000000000000000000000000000000000dEaD", 1));
        Assert.True(ex.IsUserRejection);
        await session.PingAsync();
        await session.DisconnectAsync();
    }
}
