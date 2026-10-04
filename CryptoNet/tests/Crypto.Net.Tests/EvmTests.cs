using System.Numerics;
using System.Text;
using Crypto.Net.Core;
using Crypto.Net.Evm;
using Crypto.Net.Native;
using Crypto.Net.Testing;

namespace Crypto.Net.Tests;

public class EvmTests
{
    [Theory]
    [InlineData(TestVectors.EthValidChecksum1)]
    [InlineData(TestVectors.EthValidChecksum2)]
    [InlineData(TestVectors.EthValidChecksum3)]
    public void Eip55_ChecksumVectors(string address)
    {
        Assert.True(EvmAddress.IsValid(address));
        Assert.Equal(address, EvmAddress.ToChecksumAddress(address.ToLowerInvariant()));
        Assert.True(EvmAddress.IsValid(address.ToLowerInvariant()));
        // Flipping the case of one letter breaks the checksum.
        string broken = address[..2] + (char.IsUpper(address[2]) ? char.ToLowerInvariant(address[2]) : char.ToUpperInvariant(address[2])) + address[3..];
        if (broken != address && char.IsLetter(address[2])) Assert.False(EvmAddress.IsValid(broken));
    }

    [Fact]
    public void ContractAddress_Create()
    {
        Assert.Equal("0xcd234a471b72ba2f1ccf0a70fcaba648a5eecd8d", EvmAddress.GetContractAddress("0x6ac7ea33f8831ea9dcc53393aaa88b25a785dbf0", 0).ToLowerInvariant());
    }

    [Fact]
    public void Eip155_LegacyTransaction_OfficialVector()
    {
        var tx = new EvmTransaction
        {
            Type = EvmTransactionType.Legacy,
            ChainId = 1,
            Nonce = 9,
            GasPrice = 20_000_000_000,
            GasLimit = 21_000,
            To = "0x3535353535353535353535353535353535353535",
            Value = BigInteger.Parse("1000000000000000000"),
        };
        Assert.Equal("daf5a779ae972f972197303d7b574746c7ef83eadac0f2791ad23db92e4c8e53", Convert.ToHexStringLower(tx.GetSigningHash()));

        var signed = tx.Sign(Convert.FromHexString(TestVectors.Eip155PrivateKey));
        Assert.Equal(TestVectors.Eip155SignedTx, Convert.ToHexStringLower(signed.RawBytes));
        Assert.Equal(37, (int)signed.V);
        Assert.Equal("0x9d8A62f656a8d1615C1294fd71e9CFb3E4855A4F", signed.RecoverSender());
    }

    [Fact]
    public void Eip1559_SignDecodeRecover_RoundTrip()
    {
        byte[] key = Convert.FromHexString(TestVectors.Eip155PrivateKey);
        var tx = new EvmTransaction
        {
            ChainId = 11155111,
            Nonce = 42,
            MaxPriorityFeePerGas = 1_500_000_000,
            MaxFeePerGas = 30_000_000_000,
            GasLimit = 65_000,
            To = TestVectors.EthValidChecksum1,
            Value = 123_456_789,
            Data = Erc20Client.BuildTransferData(TestVectors.EthValidChecksum2, 1_000_000),
            AccessList = [new AccessListEntry(TestVectors.EthValidChecksum3, [new byte[32]])],
        };
        var signed = tx.Sign(key);
        Assert.Equal(0x02, signed.RawBytes[0]);

        var decoded = EvmTransaction.DecodeSigned(signed.RawBytes);
        Assert.Equal(EvmTransactionType.DynamicFee, decoded.Transaction.Type);
        Assert.Equal(42UL, decoded.Transaction.Nonce);
        Assert.Equal(tx.MaxFeePerGas, decoded.Transaction.MaxFeePerGas);
        Assert.Equal(tx.Data, decoded.Transaction.Data);
        Assert.Single(decoded.Transaction.AccessList);
        Assert.Equal(EvmAddress.FromPrivateKey(key).Value, decoded.RecoverSender());
        Assert.Equal(signed.Hash, decoded.Hash);
    }

    [Fact]
    public void Eip2930_AccessListTransaction_RoundTrip()
    {
        byte[] key = Convert.FromHexString(TestVectors.Eip155PrivateKey);
        var tx = new EvmTransaction { Type = EvmTransactionType.AccessList, ChainId = 1, Nonce = 1, GasPrice = 1, GasLimit = 30_000, To = null, Data = [0x60, 0x00] };
        var decoded = EvmTransaction.DecodeSigned(tx.Sign(key).RawBytes);
        Assert.Equal(EvmTransactionType.AccessList, decoded.Transaction.Type);
        Assert.Null(decoded.Transaction.To);
        Assert.Equal(EvmAddress.FromPrivateKey(key).Value, decoded.RecoverSender());
    }

    [Fact]
    public void PersonalSign_Web3Vector()
    {
        byte[] key = Convert.FromHexString(TestVectors.PersonalSignPrivateKey);
        Assert.Equal("1da44b586eb0729ff70a73c326926f6ed5a25f5b056e7f47fbc6e58d86871655", Convert.ToHexStringLower(EvmMessageSigner.HashPersonalMessage("Some data")));
        byte[] sig = EvmMessageSigner.SignPersonalMessage(key, "Some data");
        Assert.Equal(TestVectors.PersonalSignSignature, HexUtil.Encode(sig));
        Assert.Equal(TestVectors.PersonalSignAddress, EvmMessageSigner.RecoverPersonalMessageSigner("Some data", TestVectors.PersonalSignSignature));
    }

    [Fact]
    public void Eip712_MailExample()
    {
        Assert.Equal("Mail(Person from,Person to,string contents)Person(string name,address wallet)", EvmMessageSigner.EncodeType(TestVectors.Eip712MailJson, "Mail"));
        Assert.Equal(TestVectors.Eip712MailDigest, Convert.ToHexStringLower(EvmMessageSigner.HashTypedData(TestVectors.Eip712MailJson)));

        byte[] cowKey = CryptoNative.Keccak256("cow"u8);
        Assert.Equal(TestVectors.Eip712MailSignerAddress, EvmAddress.FromPrivateKey(cowKey).Value);
        byte[] sig = EvmMessageSigner.SignTypedData(cowKey, TestVectors.Eip712MailJson);
        Assert.Equal("4355c47d63924e8a72e509b65029052eb6c299d53a04e167c5775fd466751c9d07299936d304c153f6443dfa05f40ff007d72911b6f72307f996231605b915621c",
            Convert.ToHexStringLower(sig));
        Assert.Equal(TestVectors.Eip712MailSignerAddress, EvmMessageSigner.RecoverTypedDataSigner(TestVectors.Eip712MailJson, sig));
    }

    [Fact]
    public void Abi_SolidityDocExamples()
    {
        // baz(uint32,bool) with (69, true)
        byte[] baz = EvmAbi.EncodeFunctionCall("baz(uint32 x, bool y)", 69, true);
        Assert.Equal("cdcd77c0" +
            "0000000000000000000000000000000000000000000000000000000000000045" +
            "0000000000000000000000000000000000000000000000000000000000000001", Convert.ToHexStringLower(baz));

        // sam(bytes,bool,uint256[]) with ("dave", true, [1,2,3])
        byte[] sam = EvmAbi.EncodeFunctionCall("sam(bytes,bool,uint256[])", Encoding.ASCII.GetBytes("dave"), true, new object[] { 1, 2, 3 });
        Assert.Equal("a5643bf2" +
            "0000000000000000000000000000000000000000000000000000000000000060" +
            "0000000000000000000000000000000000000000000000000000000000000001" +
            "00000000000000000000000000000000000000000000000000000000000000a0" +
            "0000000000000000000000000000000000000000000000000000000000000004" +
            "6461766500000000000000000000000000000000000000000000000000000000" +
            "0000000000000000000000000000000000000000000000000000000000000003" +
            "0000000000000000000000000000000000000000000000000000000000000001" +
            "0000000000000000000000000000000000000000000000000000000000000002" +
            "0000000000000000000000000000000000000000000000000000000000000003", Convert.ToHexStringLower(sam));
    }

    [Fact]
    public void Abi_DecodeRoundTrip_WithNestedTypes()
    {
        const string types = "address,uint256,string,bytes,int8,(uint16,bool)[],bytes4[2]";
        object?[] values =
        [
            TestVectors.EthValidChecksum1, BigInteger.Pow(2, 200), "Crypto.Net ✓", new byte[] { 1, 2, 3 }, -5,
            new object[] { new object[] { 7, true }, new object[] { 65535, false } },
            new object[] { new byte[] { 1, 2, 3, 4 }, new byte[] { 5, 6, 7, 8 } },
        ];
        byte[] encoded = EvmAbi.Encode(types, values);
        object?[] decoded = EvmAbi.Decode(types, encoded);

        Assert.Equal(TestVectors.EthValidChecksum1, decoded[0]);
        Assert.Equal(BigInteger.Pow(2, 200), decoded[1]);
        Assert.Equal("Crypto.Net ✓", decoded[2]);
        Assert.Equal(new byte[] { 1, 2, 3 }, decoded[3]);
        Assert.Equal(new BigInteger(-5), decoded[4]);
        var tuples = (object?[])decoded[5]!;
        Assert.Equal(new BigInteger(65535), ((object?[])tuples[1]!)[0]);
        Assert.Equal(false, ((object?[])tuples[1]!)[1]);
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, ((object?[])decoded[6]!)[1]);

        Assert.Throws<ArgumentOutOfRangeException>(() => EvmAbi.Encode("uint8", 256));
        Assert.Equal("a9059cbb", Convert.ToHexStringLower(EvmAbi.GetFunctionSelector("transfer(address,uint256)")));
        Assert.Equal("0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef", Erc20Client.TransferEventTopic);
    }

    [Fact]
    public void Rlp_DecodeRejectsNonCanonical()
    {
        Assert.Throws<FormatException>(() => EvmRlp.Decode([0x81, 0x05]));
        Assert.Throws<FormatException>(() => EvmRlp.Decode([0x83, 0x01]));
        var item = EvmRlp.Decode(EvmRlp.EncodeList(EvmRlp.EncodeBytes("cat"u8), EvmRlp.EncodeUInt(1024)));
        Assert.Equal(1024UL, item[1].ToUInt64());
    }

    [Fact]
    public async Task KeySigner_SignsTransactionsLikeRawKey()
    {
        using var account = EvmAccount.FromPrivateKeyHex(TestVectors.Eip155PrivateKey, EvmChain.Sepolia);
        await using var signer = account.CreateSigner();
        var tx = new EvmTransaction { ChainId = EvmChain.Sepolia.NumericChainId, MaxFeePerGas = 2, MaxPriorityFeePerGas = 1, To = TestVectors.EthValidChecksum1 };
        var viaSigner = await tx.SignAsync(signer);
        var viaKey = account.SignTransaction(tx);
        Assert.Equal(viaKey.RawBytes, viaSigner.RawBytes);
    }
}
