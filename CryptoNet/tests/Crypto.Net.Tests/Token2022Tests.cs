using System.Buffers.Binary;
using Crypto.Net.Native;
using Crypto.Net.Solana;

namespace Crypto.Net.Tests;

/// <summary>Token-2022 instructions cross-checked with @solana/spl-token 0.4.9; layouts with hand-built accounts.</summary>
public class Token2022Tests
{
    private static string Key(byte n) => CryptoNative.Base58Encode(CryptoNative.Ed25519GetPublicKey(Enumerable.Repeat(n, 32).ToArray()));

    // spl-token zero-pads optional pubkeys to a fixed size; the program reads the compact form, which is what we emit.
    private static void AssertPrefixThenZeros(string expectedHex, byte[] actual)
    {
        byte[] expected = Convert.FromHexString(expectedHex);
        Assert.Equal(expected[..actual.Length], actual);
        Assert.All(expected[actual.Length..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Instructions_MatchSplToken()
    {
        var transfer = Token2022.TransferCheckedWithFee(Key(1), Key(2), Key(3), Key(4), 1_000_000, 6, 5000);
        Assert.Equal("1a0140420f0000000000068813000000000000", Convert.ToHexStringLower(transfer.Data));
        Assert.Equal([(Key(1), false, true), (Key(2), false, false), (Key(3), false, true), (Key(4), true, false)],
            transfer.Accounts.Select(a => (a.PublicKey, a.IsSigner, a.IsWritable)));

        Assert.Equal("1406ca93ac1705187071d67b83c7ff0efe8108e8ec4530575d7726879333dbdabe7c016e7a1cdd29b0b78fd13af4c5598feff4ef2a97166e3ca6f2e4fbfccd80505bf1",
            Convert.ToHexStringLower(Token2022.InitializeMint2(Key(2), 6, Key(4), Key(5)).Data));
        AssertPrefixThenZeros("1409ca93ac1705187071d67b83c7ff0efe8108e8ec4530575d7726879333dbdabe7c000000000000000000000000000000000000000000000000000000000000000000",
            Token2022.InitializeMint2(Key(2), 9, Key(4)).Data);
        AssertPrefixThenZeros("1a0001ca93ac1705187071d67b83c7ff0efe8108e8ec4530575d7726879333dbdabe7c00fa0040420f00000000000000000000000000000000000000000000000000000000000000000000000000",
            Token2022.InitializeTransferFeeConfig(Key(2), Key(4), null, 250, 1_000_000).Data);
        Assert.Equal("2700ca93ac1705187071d67b83c7ff0efe8108e8ec4530575d7726879333dbdabe7c8139770ea87d175f56a35466c34c7ecccb8d8a91b4ee37a25df60f5b8fc9b394",
            Convert.ToHexStringLower(Token2022.InitializeMetadataPointer(Key(2), Key(4), Key(2)).Data));
        Assert.Equal(346, Token2022.MintSpace(TokenExtensionType.TransferFeeConfig, TokenExtensionType.MetadataPointer));
    }

    [Fact]
    public void TransferFee_RoundsUpAndCaps()
    {
        var fee = new TransferFee(0, 5_000, 250);   // 2.5 %, max 5000
        Assert.Equal(25ul, fee.Calculate(1000));
        Assert.Equal(1ul, fee.Calculate(1));        // ceil
        Assert.Equal(5000ul, fee.Calculate(10_000_000));
        Assert.Equal(0ul, new TransferFee(0, 10, 0).Calculate(1_000_000));
    }

    [Fact]
    public void Mint_WithExtensions_Parses()
    {
        var data = new List<byte>();
        data.AddRange([1, 0, 0, 0, .. SolanaAddress.Decode(Key(4))]);              // mint authority
        data.AddRange(BitConverter.GetBytes(123_456_789UL));                       // supply
        data.AddRange([6, 1]);                                                     // decimals, initialized
        data.AddRange([0, 0, 0, 0, .. new byte[32]]);                              // no freeze authority
        data.AddRange(new byte[165 - 82]);                                         // padding to the account size
        data.Add(1);                                                               // AccountType::Mint

        void Tlv(TokenExtensionType t, byte[] v)
        {
            var h = new byte[4];
            BinaryPrimitives.WriteUInt16LittleEndian(h, (ushort)t);
            BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(2), (ushort)v.Length);
            data.AddRange(h);
            data.AddRange(v);
        }
        var fee = new BorshWriter().PublicKey(Key(4)).Bytes(new byte[32]).U64(77)
            .U64(10).U64(1000).U16(100)       // older
            .U64(20).U64(5000).U16(250)       // newer
            .ToArray();
        Tlv(TokenExtensionType.TransferFeeConfig, fee);
        Tlv(TokenExtensionType.MetadataPointer, [.. SolanaAddress.Decode(Key(4)), .. SolanaAddress.Decode(Key(2))]);
        var meta = new BorshWriter().PublicKey(Key(4)).PublicKey(Key(2)).String("Crypto Dollar").String("CDOL").String("https://example.com/cdol.json")
            .U32(1).String("issuer").String("Gravicode").ToArray();
        Tlv(TokenExtensionType.TokenMetadata, meta);

        var mint = TokenMint.Parse(data.ToArray());
        Assert.Equal((Key(4), 123_456_789UL, (byte)6, true, (string?)null), (mint.MintAuthority, mint.Supply, mint.Decimals, mint.IsInitialized, mint.FreezeAuthority));
        Assert.Equal(3, mint.Extensions.Count);
        var cfg = mint.TransferFeeConfig!;
        Assert.Equal((Key(4), (string?)null, 77UL), (cfg.ConfigAuthority, cfg.WithdrawWithheldAuthority, cfg.WithheldAmount));
        Assert.Equal(100, cfg.ForEpoch(15).BasisPoints);
        Assert.Equal(250, cfg.ForEpoch(20).BasisPoints);
        Assert.Equal((Key(4), Key(2)), mint.MetadataPointer!.Value);
        var md = mint.Metadata!;
        Assert.Equal(("Crypto Dollar", "CDOL", "Gravicode"), (md.Name, md.Symbol, md.AdditionalMetadata[0].Value));

        // A classic SPL mint (82 bytes) has no extensions.
        Assert.Empty(TokenMint.Parse(data.Take(82).ToArray()).Extensions);
        Assert.Throws<FormatException>(() => TokenMint.Parse(data.Take(170).Concat(new byte[] { 0xff, 0xff }).ToArray()));
    }

    [Fact]
    public void TokenAccount_Parses()
    {
        var data = new byte[165];
        SolanaAddress.Decode(Key(2)).CopyTo(data, 0);
        SolanaAddress.Decode(Key(4)).CopyTo(data, 32);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(64), 42);
        data[108] = 1;
        var account = TokenAccountData.Parse(data);
        Assert.Equal((Key(2), Key(4), 42UL, false), (account.Mint, account.Owner, account.Amount, account.IsFrozen));
        Assert.Null(account.Delegate);
    }
}
