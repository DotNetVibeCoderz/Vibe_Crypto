using Crypto.Net.Cosmos;
using Crypto.Net.Native;
using Crypto.Net.Solana;
using Crypto.Net.Testing;
using Crypto.Net.Wallet;

namespace Crypto.Net.Tests;

public class SolanaTests
{
    [Fact]
    public void Addresses_AndCurveChecks()
    {
        Assert.True(SolanaAddress.IsValid(TestVectors.SolanaTokenProgram));
        Assert.True(SolanaAddress.IsValid(TestVectors.SolanaSystemProgram));
        Assert.False(SolanaAddress.IsValid("0OIl"));

        using var account = SolanaAccount.Generate();
        Assert.True(SolanaAddress.IsOnCurve(account.Address.Value));

        string mint = "So11111111111111111111111111111111111111112";
        string ata = SolanaAddress.GetAssociatedTokenAddress(account.Address.Value, mint);
        Assert.False(SolanaAddress.IsOnCurve(ata));
        var (pda, bump) = SolanaAddress.FindProgramAddress([SolanaAddress.Utf8Seed("vault")], SolanaAddress.MemoProgram);
        Assert.Equal(pda, SolanaAddress.CreateProgramAddress([SolanaAddress.Utf8Seed("vault"), [bump]], SolanaAddress.MemoProgram));
    }

    [Fact]
    public void Transaction_CompilesSignsAndSerializes()
    {
        using var payer = SolanaAccount.Generate(SolanaChain.Devnet);
        using var other = SolanaAccount.Generate(SolanaChain.Devnet);
        string blockhash = CryptoNative.Base58Encode(new byte[32].Select((_, i) => (byte)i).ToArray());

        var tx = new SolanaTransaction(payer.Address.Value, blockhash)
            .Add(ComputeBudgetProgram.SetComputeUnitPrice(1000))
            .Add(SystemProgram.Transfer(payer.Address.Value, other.Address.Value, 1_000_000))
            .Add(MemoProgram.Memo("Crypto.Net", payer.Address.Value));

        var accounts = tx.CompileAccounts();
        Assert.Equal(payer.Address.Value, accounts[0].PublicKey);
        Assert.True(accounts[0].IsSigner && accounts[0].IsWritable);
        Assert.Equal(other.Address.Value, accounts[1].PublicKey);
        Assert.Equal(5, accounts.Count); // payer, recipient, compute budget, system, memo

        byte[] message = tx.SerializeMessage();
        Assert.Equal(new byte[] { 1, 0, 3 }, message[..3]); // 1 signer, 0 ro signed, 3 ro unsigned programs

        Assert.Throws<InvalidOperationException>(() => tx.Serialize());
        payer.Sign(tx);
        byte[] wire = tx.Serialize();
        Assert.Equal(1, wire[0]);
        Assert.True(CryptoNative.Ed25519Verify(payer.PublicKey.Span, message, wire.AsSpan(1, 64)));
        Assert.Equal(CryptoNative.Base58Encode(wire.AsSpan(1, 64)), tx.Signature);
    }

    [Fact]
    public void Keypair_ImportExport()
    {
        using var a = SolanaAccount.Generate();
        using var b = SolanaAccount.FromBase58SecretKey(a.ExportBase58SecretKey());
        using var c = SolanaAccount.FromKeypairJson(a.ExportKeypairJson());
        Assert.Equal(a.Address, b.Address);
        Assert.Equal(a.Address, c.Address);
    }
}

public class CosmosTests
{
    [Fact]
    public void Address_PrefixConversion()
    {
        Assert.True(CosmosAddress.IsValid(TestVectors.AbandonCosmos));
        Assert.False(CosmosAddress.IsValid(TestVectors.AbandonCosmos, "osmo"));
        string osmo = CosmosAddress.ConvertPrefix(TestVectors.AbandonCosmos, "osmo");
        Assert.StartsWith("osmo1", osmo);
        Assert.Equal(TestVectors.AbandonCosmos, CosmosAddress.ConvertPrefix(osmo, "cosmos"));
    }

    [Fact]
    public void SignDoc_SignatureVerifiesAgainstSha256()
    {
        using var wallet = HdWallet.FromMnemonic(TestVectors.Bip39TestMnemonic);
        using var account = wallet.GetCosmosAccount(chain: CosmosChain.CosmosHubTestnet);
        var builder = new CosmosTxBuilder { Memo = "crypto.net", GasLimit = 100_000 }
            .Add(CosmosMessage.BankSend(account.Address.Value, TestVectors.AbandonCosmos, new Coin("uatom", 12345)));
        builder.Fee.Add(new Coin("uatom", 2500));

        byte[] txRaw = account.Sign(builder, "provider", 7, 3);
        byte[] body = builder.EncodeBody();
        byte[] authInfo = builder.EncodeAuthInfo(account.PublicKey.Span, 3);
        byte[] digest = CryptoNative.Sha256(CosmosTxBuilder.EncodeSignDoc(body, authInfo, "provider", 7));

        // TxRaw = body(1) authInfo(2) signature(3); the signature is the last 64 bytes.
        byte[] sig = txRaw[^64..];
        Assert.True(CryptoNative.Secp256k1Verify(account.PublicKey.Span, digest, sig));
        Assert.Equal(64, CosmosTxBuilder.Hash(txRaw).Length);
        Assert.True(body.AsSpan().IndexOf("/cosmos.bank.v1beta1.MsgSend"u8) >= 0);
    }

    [Fact]
    public void Protobuf_MsgSendEncoding()
    {
        var msg = CosmosMessage.BankSend("a", "b", new Coin("uatom", 5));
        // field1 "a", field2 "b", field3 { field1 "uatom", field2 "5" }
        Assert.Equal("0a01611201621a0a0a057561746f6d120135", Convert.ToHexStringLower(msg.Value));
    }
}
