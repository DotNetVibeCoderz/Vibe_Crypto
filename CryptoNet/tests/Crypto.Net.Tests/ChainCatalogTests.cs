using Crypto.Net.Extensions;
using Crypto.Net.Native;
using Crypto.Net.Wallet;

namespace Crypto.Net.Tests;

public class ChainCatalogTests
{
    private const string Abandon = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    [Fact]
    public void EveryEntry_DerivesAValidAccountAndCreatesAClient()
    {
        using var wallet = HdWallet.FromMnemonic(Abandon);
        foreach (var entry in ChainCatalog.Entries)
        {
            if (entry.Family == ChainFamily.Polkadot && !NativeLoader.IsNativeAvailable) continue;
            var account = ChainCatalog.DeriveAccount(wallet, entry);
            try
            {
                Assert.True(ChainCatalog.IsValidAddress(entry, account.Address.Value), $"{entry.Key}: {account.Address.Value}");
                using var client = ChainCatalog.CreateClient(entry);
                Assert.Same(entry.Chain, client.Chain);
            }
            finally
            {
                (account as IDisposable)?.Dispose();
            }
        }
    }

    [Theory]
    [InlineData("aptos", ChainFamily.Aptos)]
    [InlineData("apt", ChainFamily.Aptos)]
    [InlineData("sui-testnet", ChainFamily.Sui)]
    [InlineData("ton", ChainFamily.Ton)]
    [InlineData("ada", ChainFamily.Cardano)]
    [InlineData("preprod", ChainFamily.Cardano)]
    [InlineData("near-testnet", ChainFamily.Near)]
    public void Find_KnowsTheNewChains(string name, ChainFamily family) => Assert.Equal(family, ChainCatalog.Get(name).Family);

    [Theory]
    [InlineData("0xeb663b681209e7087d681c5d3eed12aaa8e1915e7c87794542c3f96e94b3d3bf", ChainFamily.Aptos)]
    [InlineData("0xeb663b681209e7087d681c5d3eed12aaa8e1915e7c87794542c3f96e94b3d3bf", ChainFamily.Sui)]
    [InlineData("alice.near", ChainFamily.Near)]
    [InlineData("UQAqbua3_0G_7K_jgzhjJceolfT-TONGsY65wUoBUtZinP1w", ChainFamily.Ton)]
    [InlineData("addr1qy8ac7qqy0vtulyl7wntmsxc6wex80gvcyjy33qffrhm7sh927ysx5sftuw0dlft05dz3c7revpf7jx0xnlcjz3g69mq4afdhv", ChainFamily.Cardano)]
    public void DetectAddress_RecognizesTheNewFormats(string address, ChainFamily family) =>
        Assert.Contains(ChainCatalog.DetectAddress(address), m => m.Family == family);

    [Fact]
    public void DetectAddress_DoesNotMistakeCardanoForCosmos() =>
        Assert.DoesNotContain(ChainCatalog.DetectAddress("addr1qy8ac7qqy0vtulyl7wntmsxc6wex80gvcyjy33qffrhm7sh927ysx5sftuw0dlft05dz3c7revpf7jx0xnlcjz3g69mq4afdhv"), m => m.Family == ChainFamily.Cosmos);

    [Fact]
    public void IsValidAddress_ChecksTheNetwork()
    {
        Assert.False(ChainCatalog.IsValidAddress(ChainCatalog.Get("cardano-preprod"), "addr1qy8ac7qqy0vtulyl7wntmsxc6wex80gvcyjy33qffrhm7sh927ysx5sftuw0dlft05dz3c7revpf7jx0xnlcjz3g69mq4afdhv"));
        Assert.False(ChainCatalog.IsValidAddress(ChainCatalog.Get("ton"), "0QAqbua3_0G_7K_jgzhjJceolfT-TONGsY65wUoBUtZinEb6"));
        Assert.True(ChainCatalog.IsValidAddress(ChainCatalog.Get("ton-testnet"), "0QAqbua3_0G_7K_jgzhjJceolfT-TONGsY65wUoBUtZinEb6"));
    }
}
