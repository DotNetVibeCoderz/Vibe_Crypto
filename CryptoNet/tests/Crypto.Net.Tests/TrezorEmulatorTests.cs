using Crypto.Net.Evm;
using Crypto.Net.Testing;
using Crypto.Net.Trezor;

namespace Crypto.Net.Tests;

/// <summary>Runs only when CRYPTONET_TREZOR=1 (CI starts Trezor's core emulator loaded with the public test phrase).</summary>
public sealed class TrezorEmulatorFactAttribute : FactAttribute
{
    public TrezorEmulatorFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CRYPTONET_TREZOR") != "1")
            Skip = "Set CRYPTONET_TREZOR=1 with the Trezor emulator on UDP 21324 (debug link 21325)";
    }
}

/// <summary>
/// End-to-end against Trezor's real firmware in the emulator. Confirmations are pressed through the debug link, and
/// every signature is checked against the address Crypto.Net derives in software from the same phrase.
/// </summary>
public class TrezorEmulatorTests
{
    private static TrezorClient Connect(TrezorUdpTransport debug) => new(new TrezorUdpTransport())
    {
        OnButtonRequest = async _ =>
        {
            await Task.Delay(300);
            await debug.SendAsync(TrezorMessageType.DebugLinkDecision, new ProtoWriter().Varint(1, 1).ToArray());   // button: YES
        },
    };

    [TrezorEmulatorFact]
    public async Task Ethereum_SignsWithTheDeviceKey()
    {
        using var debug = new TrezorUdpTransport(port: 21325);
        using var trezor = Connect(debug);
        var features = await trezor.InitializeAsync();
        Assert.True(features.Initialized);
        string path = TrezorClient.EthereumPath(0);

        Assert.Equal(TestVectors.AbandonEthereum, await trezor.GetEthereumAddressAsync(path));

        byte[] sig = await trezor.SignEthereumMessageAsync(path, "Crypto.Net + Trezor"u8.ToArray());
        Assert.Equal(TestVectors.AbandonEthereum, EvmMessageSigner.RecoverPersonalMessageSigner("Crypto.Net + Trezor", Crypto.Net.Core.HexUtil.Encode(sig)));

        var tx = new EvmTransaction
        {
            Type = EvmTransactionType.DynamicFee, ChainId = 11155111, Nonce = 3, MaxPriorityFeePerGas = 1_000_000_000, MaxFeePerGas = 30_000_000_000,
            GasLimit = 21000, To = "0x000000000000000000000000000000000000dEaD", Value = 1_000_000_000_000_000,
        };
        var signed = await trezor.SignEthereumTransactionAsync(path, tx);
        Assert.Equal(TestVectors.AbandonEthereum, EvmTransaction.DecodeSigned(signed.RawBytes).RecoverSender());

        var legacy = new EvmTransaction { Type = EvmTransactionType.Legacy, ChainId = 1, Nonce = 1, GasPrice = 20_000_000_000, GasLimit = 21000, To = "0x000000000000000000000000000000000000dEaD", Value = 5 };
        var signedLegacy = await trezor.SignEthereumTransactionAsync(path, legacy);
        Assert.Equal(TestVectors.AbandonEthereum, EvmTransaction.DecodeSigned(signedLegacy.RawBytes).RecoverSender());
    }
}
