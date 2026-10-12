using System.Numerics;
using System.Text;
using Crypto.Net.Native;
using Crypto.Net.Near;

namespace Crypto.Net.Tests;

/// <summary>NEAR encoding cross-checked with near-api-js 5.0.1.</summary>
public class NearTests
{
    private static NearAccount Key7() => new(Enumerable.Repeat((byte)7, 32).ToArray());

    [Fact]
    public void Keys_MatchNearApiJs()
    {
        using var account = Key7();
        Assert.Equal("ed25519:GmaDrppBC7P5ARKV8g3djiwP89vz1jLK23V2GBjuAEGB", account.PublicKeyString);
        Assert.Equal("ea4a6c63e29c520abef5507b132ec5f9954776aebebe7b92421eea691446d22c", account.AccountId);
        string secret = account.ExportSecretKey();
        Assert.Equal("ed25519:99eUso3aSbE9tqGSTXzo3TLfKb9RkMTURrHKQ1K7Zh3StnzFNUx8FKCPPPPpR479qsw5zv2WNBKmgiz7WqgAJfM", secret);
        using var imported = NearAccount.FromSecretKey(secret, "alice.testnet", NearChain.Testnet);
        Assert.Equal(("alice.testnet", account.PublicKeyString), (imported.AccountId, imported.PublicKeyString));
    }

    [Theory]
    [InlineData("alice.near", true)]
    [InlineData("a-b_c.app.testnet", true)]
    [InlineData("ea4a6c63e29c520abef5507b132ec5f9954776aebebe7b92421eea691446d22c", true)]
    [InlineData("Alice.near", false)]
    [InlineData("a", false)]
    [InlineData("bad..dots", false)]
    [InlineData("trailing-.near", false)]
    public void AccountIds(string id, bool valid) => Assert.Equal(valid, NearAddress.IsValidAccountId(id));

    [Fact]
    public void Transaction_WithEveryAction_MatchesNearApiJs()
    {
        using var account = Key7().ForAccount("alice.testnet");
        byte[] pub = account.PublicKey.ToArray();
        var tx = new NearTransaction
        {
            SignerId = "alice.testnet",
            PublicKey = pub,
            Nonce = 42,
            ReceiverId = "receiver.testnet",
            BlockHash = CryptoNative.Base58Decode("7tkzFg8RHBmMw1ncRJZCCZAizgq4rwCftTKYLce8RU8t"),
            Actions =
            [
                new NearAction.Transfer(BigInteger.Pow(10, 24)),
                new NearAction.FunctionCall("ft_transfer", Encoding.UTF8.GetBytes("{\"receiver_id\":\"bob.testnet\",\"amount\":\"5\"}"), 30 * NearAddress.TGas, 1),
                new NearAction.AddKey(pub),
                new NearAction.AddKey(pub, "app.testnet", ["vote", "claim"], BigInteger.Parse("250000000000000000000000")),
                new NearAction.DeleteKey(pub),
                new NearAction.CreateAccount(),
                new NearAction.Stake(100, pub),
                new NearAction.DeleteAccount("beneficiary.testnet"),
            ],
        };
        const string expected = "0d000000616c6963652e746573746e657400ea4a6c63e29c520abef5507b132ec5f9954776aebebe7b92421eea691446d22c2a000000000000001000000072656365697665722e746573746e657466687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f29250800000003000000a1edccce1bc2d3000000000000020b00000066745f7472616e736665722a0000007b2272656365697665725f6964223a22626f622e746573746e6574222c22616d6f756e74223a2235227d00e057eb481b0000010000000000000000000000000000000500ea4a6c63e29c520abef5507b132ec5f9954776aebebe7b92421eea691446d22c0000000000000000010500ea4a6c63e29c520abef5507b132ec5f9954776aebebe7b92421eea691446d22c00000000000000000001000040683bb3f386f0340000000000000b0000006170702e746573746e65740200000004000000766f746505000000636c61696d0600ea4a6c63e29c520abef5507b132ec5f9954776aebebe7b92421eea691446d22c00046400000000000000000000000000000000ea4a6c63e29c520abef5507b132ec5f9954776aebebe7b92421eea691446d22c071300000062656e65666963696172792e746573746e6574";
        Assert.Equal(expected, Convert.ToHexStringLower(tx.Serialize()));
        Assert.Equal("GemeeVY3AYnDQZo6VM5WG9vETwoMZZ8KbRZ5E8XcpiX9", tx.HashBase58);

        byte[] signed = tx.Sign(account);
        Assert.EndsWith("007970c99acc4061235f88645d251efb88089125771637cb3b5efb078fab2cfffd2fd17ed2137d5177f52163d8f4ce184c75705909ffe6331e3b2a8d511315130b", Convert.ToHexStringLower(signed));
        Assert.Equal(tx.HashBase58, NearRpcClient.TransactionHashOf(signed));
    }
}
