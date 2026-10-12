using Crypto.Net.Bitcoin;
using Crypto.Net.Evm;
using Crypto.Net.Solana;
using Crypto.Net.Testing;
using Crypto.Net.Wallet;

namespace CnetTests;

/// <summary>Offline tests against the official vectors shipped in Crypto.Net.Testing.</summary>
public class WalletTests
{
#pragma warning disable CNET001 // public test vector, not a real wallet
    private static HdWallet Abandon() => HdWallet.FromMnemonic(TestVectors.Bip39TestMnemonic);
#pragma warning restore CNET001

    [Fact]
    public void DerivesTheReferenceAddresses()
    {
        using var wallet = Abandon();
        using var eth = wallet.GetEvmAccount();
        using var btc = wallet.GetBitcoinAccount();
        using var sol = wallet.GetSolanaAccount();
        Assert.Equal(TestVectors.AbandonEthereum, eth.Address.Value);
        Assert.Equal(TestVectors.AbandonBitcoinSegwit, btc.Address.Value);
        Assert.Equal(TestVectors.AbandonSolana, sol.Address.Value);
    }

    [Fact]
    public async Task MockedRpcReturnsHead()
    {
        var mock = new MockHttpMessageHandler().OnRpc("eth_blockNumber", "\"0x10\"");
        await using var client = new EvmRpcClient(EvmChain.Sepolia, httpClient: mock.CreateClient());
        Assert.Equal(16ul, await client.GetBlockNumberAsync());
    }
}
