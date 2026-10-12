using Crypto.Net.Evm;
using Crypto.Net.Testing;
using Crypto.Net.Trezor;
using Crypto.Net.Wallet;

namespace Crypto.Net.Tests;

/// <summary>Trezor messages cross-checked with trezorlib 0.13.10, plus scripted device conversations.</summary>
public class TrezorTests
{
    private const string Path = "m/44'/60'/0'/0/0";
    private const string Abandon = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
    private const string PathHex = "08ac8080800808bc80808008088080808008080008";
    private static readonly byte[] Data = Enumerable.Range(0, 1280).Select(i => (byte)i).ToArray();

    private static byte[] Msg(ProtoWriter w) => w.ToArray();
    private static byte[] H(string hex) => Convert.FromHexString(hex);

    [Fact]
    public void Framing_MatchesTrezorlib()
    {
        var reports = TrezorFraming.Wrap(55, Enumerable.Range(0, 100).Select(i => (byte)i).ToArray());
        Assert.Equal(2, reports.Count);
        Assert.Equal("3f2323003700000064000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f30313233343536", Convert.ToHexStringLower(reports[0]));
        Assert.Equal("3f3738393a3b3c3d3e3f404142434445464748494a4b4c4d4e4f505152535455565758595a5b5c5d5e5f60616263000000000000000000000000000000000000", Convert.ToHexStringLower(reports[1]));
    }

    [Fact]
    public async Task Framing_RoundTrips()
    {
        byte[] payload = Enumerable.Range(0, 300).Select(i => (byte)(i * 7)).ToArray();
        var queue = new Queue<byte[]>(TrezorFraming.Wrap(452, payload));
        var (type, back) = await TrezorFraming.UnwrapAsync(_ => Task.FromResult(queue.Dequeue()), default);
        Assert.Equal(452, type);
        Assert.Equal(payload, back);
    }

    [Fact]
    public async Task Address_MatchesTrezorlibEncoding()
    {
        using var t = new ReplayTrezorTransport([
            (TrezorMessageType.EthereumGetAddress, H(PathHex + "001001"), TrezorMessageType.ButtonRequest, H("0808")),
            (TrezorMessageType.ButtonAck, [], TrezorMessageType.EthereumAddress, Msg(new ProtoWriter().String(2, TestVectors.AbandonEthereum.ToLowerInvariant()))),
        ]);
        int buttons = 0;
        var client = new TrezorClient(t) { OnButtonRequest = _ => { buttons++; return Task.CompletedTask; } };
        Assert.Equal(TestVectors.AbandonEthereum, await client.GetEthereumAddressAsync(Path, display: true));
        Assert.Equal(1, buttons);
        Assert.True(t.Completed);
    }

    [Fact]
    public async Task Eip1559_SendsChunksAndAssemblesTheSignature()
    {
        var tx = new EvmTransaction
        {
            Type = EvmTransactionType.DynamicFee, ChainId = 11155111, Nonce = 7, MaxFeePerGas = 30_000_000_000, MaxPriorityFeePerGas = 1_000_000_000,
            GasLimit = 100_000, To = "0x000000000000000000000000000000000000dEaD", Value = 1_000_000_000_000_000, Data = Data,
        };
        using var wallet = HdWallet.FromMnemonic(Abandon);
        using var account = wallet.GetEvmAccount(0, EvmChain.Sepolia);
        var reference = account.SignTransaction(tx);
        string first = "08ac8080800808bc80808008088080808008080008001201071a0506fc23ac0022043b9aca002a030186a0322a3078303030303030303030303030303030303030303030303030303030303030303030303030644561443a07038d7ea4c68000428008"
            + Convert.ToHexStringLower(Data.AsSpan(0, 1024)) + "48800a50a7eda805";

        using var t = new ReplayTrezorTransport([
            (TrezorMessageType.EthereumSignTxEip1559, H(first), TrezorMessageType.EthereumTxRequest, Msg(new ProtoWriter().Varint(1, 256))),
            (TrezorMessageType.EthereumTxAck, H("0a8002" + Convert.ToHexStringLower(Data.AsSpan(1024))), TrezorMessageType.ButtonRequest, H("0813")),
            (TrezorMessageType.ButtonAck, [], TrezorMessageType.EthereumTxRequest,
                Msg(new ProtoWriter().Varint(2, reference.RecoveryId).Bytes(3, reference.R.ToByteArray(true, true)).Bytes(4, reference.S.ToByteArray(true, true)))),
        ]);
        var signed = await new TrezorClient(t).SignEthereumTransactionAsync(Path, tx);
        Assert.Equal(reference.RawBytes, signed.RawBytes);
        Assert.Equal(TestVectors.AbandonEthereum, EvmTransaction.DecodeSigned(signed.RawBytes).RecoverSender());
        Assert.True(t.Completed);
    }

    [Fact]
    public async Task Legacy_UsesTheLegacyMessageAndEip155V()
    {
        var tx = new EvmTransaction { Type = EvmTransactionType.Legacy, ChainId = 1, Nonce = 0, GasPrice = 20_000_000_000, GasLimit = 21000, To = "0x000000000000000000000000000000000000dEaD", Value = 5 };
        using var wallet = HdWallet.FromMnemonic(Abandon);
        using var account = wallet.GetEvmAccount(0, EvmChain.Ethereum);
        var reference = account.SignTransaction(tx);
        using var t = new ReplayTrezorTransport([
            (TrezorMessageType.EthereumSignTx, H("08ac8080800808bc808080080880808080080800080012001a0504a817c800220252085a2a3078303030303030303030303030303030303030303030303030303030303030303030303030644561443201053a0040004801"),
                TrezorMessageType.EthereumTxRequest, Msg(new ProtoWriter().Varint(2, (ulong)(37 + reference.RecoveryId)).Bytes(3, reference.R.ToByteArray(true, true)).Bytes(4, reference.S.ToByteArray(true, true)))),
        ]);
        var signed = await new TrezorClient(t).SignEthereumTransactionAsync(Path, tx);
        Assert.Equal(reference.RawBytes, signed.RawBytes);
    }

    [Fact]
    public async Task PersonalMessage_AndFailures()
    {
        using var t = new ReplayTrezorTransport([
            (TrezorMessageType.EthereumSignMessage, H(PathHex + "00120568656c6c6f"), TrezorMessageType.PassphraseRequest, []),
            (TrezorMessageType.PassphraseAck, Msg(new ProtoWriter().String(1, "")), TrezorMessageType.EthereumMessageSignature, Msg(new ProtoWriter().Bytes(2, new byte[65]))),
            (TrezorMessageType.EthereumSignMessage, H(PathHex + "00120568656c6c6f"), TrezorMessageType.Failure, Msg(new ProtoWriter().Varint(1, 4).String(2, "Cancelled"))),
        ]);
        var client = new TrezorClient(t);
        Assert.Equal(65, (await client.SignEthereumMessageAsync(Path, "hello"u8.ToArray())).Length);
        var ex = await Assert.ThrowsAsync<TrezorException>(() => client.SignEthereumMessageAsync(Path, "hello"u8.ToArray()));
        Assert.True(ex.IsUserRejection);
    }

    [Fact]
    public void ProtoMessage_ParsesWhatProtoWriterWrites()
    {
        var m = ProtoMessage.Parse(new ProtoWriter().Varint(1, 300).String(2, "hi").Bool(3, true).ToArray());
        Assert.Equal((300UL, "hi", 1UL), (m.Varint(1), m.String(2), m.Varint(3)));
        Assert.Throws<FormatException>(() => ProtoMessage.Parse([0x0a, 0x05, 0x01]));
    }
}
