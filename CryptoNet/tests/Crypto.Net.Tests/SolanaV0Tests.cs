using System.Buffers.Binary;
using Crypto.Net.Native;
using Crypto.Net.Solana;

namespace Crypto.Net.Tests;

/// <summary>Versioned (v0) messages and address lookup tables, cross-checked against @solana/web3.js 1.98.0.</summary>
public class SolanaV0Tests
{
    private static byte[] Seed(byte n) => Enumerable.Repeat(n, 32).ToArray();
    private static string Key(byte n) => CryptoNative.Base58Encode(CryptoNative.Ed25519GetPublicKey(Seed(n)));

    // Produced by web3.js: TransactionMessage.compileToV0Message([table]) and VersionedTransaction.sign([payer, other]).
    private const string ExpectedTx =
        "02868112f76391a65dc16a9d0f449a60913a7d296b1c9b72f300394e95e404b3f5ce853425c0c66abab2ace9bf92421f004f5c2f7e4fc4fcac20acd089b2bf2903" +
        "a7478517bab1526f88bcf487917a38f380438467850e409977455724bda58cf01aa6c143184062eda4f4420e0f5f81a1ab781b0942e7311ec1b56545723efc05" +
        "80020103058a88e3dd7409f195fd52db2d3cba5d72ca6709bf1d94121bf3748801b40f6f5c8139770ea87d175f56a35466c34c7ecccb8d8a91b4ee37a25df60f" +
        "5b8fc9b3940306466fe5211732ffecadba72c39be7bc8ce5bbc5f7126b2c439b3a40000000000000000000000000000000000000000000000000000000000000" +
        "0000000000054a535a992921064d24e87160da387c7c35b5ddbc92bb81e41fa8404105448dfd1724385aa0c75b64fb78cd602fa1d991fdebf76b13c58ed702ea" +
        "c835e9f6180402000903e803000000000000030200050c020000008813000000000000030200060c020000007017000000000000040207010268690120828bf5" +
        "c5bdcacb684863336c202fb5599da48be5596615742170705beca9f70201020103";

    private static (SolanaTransaction Tx, AddressLookupTableAccount Table) Build()
    {
        string payer = Key(1), other = Key(2);
        var table = new AddressLookupTableAccount(Key(20), [Key(30), Key(3), Key(4), Key(5), SolanaAddress.SystemProgram]);
        var tx = new SolanaTransaction(payer, Key(9))
            .Add(ComputeBudgetProgram.SetComputeUnitPrice(1000))
            .Add(SystemProgram.Transfer(payer, Key(3), 5000))
            .Add(SystemProgram.Transfer(payer, Key(4), 6000))
            .Add(new SolanaInstruction(SolanaAddress.MemoProgram, [AccountMeta.ReadOnly(Key(5)), AccountMeta.ReadOnly(other, true)], "hi"u8.ToArray()))
            .WithLookupTables(table);
        return (tx, table);
    }

    [Fact]
    public void V0_Message_MatchesWeb3Js()
    {
        var (tx, _) = Build();
        tx.Sign(Seed(1), Seed(2));
        Assert.Equal(ExpectedTx, Convert.ToHexStringLower(tx.Serialize()));
        Assert.Equal(SolanaMessageVersion.V0, tx.Version);
    }

    [Fact]
    public void V0_Message_ParsesAndResolves()
    {
        var (tx, table) = Build();
        tx.Sign(Seed(1), Seed(2));
        var (signatures, message) = SolanaMessage.ParseTransaction(tx.Serialize());
        Assert.Equal(2, signatures.Count);
        Assert.Equal(SolanaMessageVersion.V0, message.Version);
        Assert.Equal(5, message.StaticAccountKeys.Count);
        var lookup = Assert.Single(message.AddressTableLookups);
        Assert.Equal(table.Key, lookup.TableKey);
        Assert.Equal([1, 2], lookup.WritableIndexes);
        Assert.Equal([3], lookup.ReadonlyIndexes);

        var keys = message.ResolveAccountKeys([table]);
        Assert.Equal(8, keys.Count);
        var transfer = message.Instructions[1];
        Assert.Equal(SolanaAddress.SystemProgram, keys[transfer.ProgramIdIndex]);
        Assert.Equal(Key(3), keys[transfer.AccountIndexes[1]]);
        Assert.True(CryptoNative.Ed25519Verify(CryptoNative.Ed25519GetPublicKey(Seed(1)), tx.SerializeMessage(), signatures[0]));
    }

    [Fact]
    public void Legacy_Message_ParsesBack()
    {
        var tx = new SolanaTransaction(Key(1), Key(9)).Add(SystemProgram.Transfer(Key(1), Key(3), 42));
        tx.Sign(Seed(1));
        var (sigs, message) = SolanaMessage.ParseTransaction(tx.Serialize());
        Assert.Single(sigs);
        Assert.Equal(SolanaMessageVersion.Legacy, message.Version);
        Assert.Equal([Key(1), Key(3), SolanaAddress.SystemProgram], message.StaticAccountKeys);
        Assert.Empty(message.AddressTableLookups);
    }

    [Fact]
    public void LookupTableProgram_MatchesWeb3Js()
    {
        var (create, table) = AddressLookupTableProgram.Create(Key(1), Key(1), 123456);
        Assert.Equal("G4Pt2AMjL8aLsTB3ypumteyATmZQqKQgwzQi82MDM4im", table);
        Assert.Equal("0000000040e2010000000000fe", Convert.ToHexStringLower(create.Data));
        var extend = AddressLookupTableProgram.Extend(table, Key(1), [Key(3), Key(4)], Key(1));
        Assert.Equal("020000000200000000000000ed4928c628d1c2c6eae90338905995612959273a5c63f93636c14614ac8737d1ca93ac1705187071d67b83c7ff0efe8108e8ec4530575d7726879333dbdabe7c",
            Convert.ToHexStringLower(extend.Data));
    }

    [Fact]
    public void LookupTableAccount_Decodes()
    {
        byte[] data = new byte[AddressLookupTableAccount.MetaSize + 64];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 1);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(4), ulong.MaxValue);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(12), 777);
        data[21] = 1;
        SolanaAddress.Decode(Key(1)).CopyTo(data, 22);
        SolanaAddress.Decode(Key(3)).CopyTo(data, 56);
        SolanaAddress.Decode(Key(4)).CopyTo(data, 88);
        var table = AddressLookupTableAccount.Parse(Key(20), data);
        Assert.True(table.IsActive);
        Assert.Equal(777ul, table.LastExtendedSlot);
        Assert.Equal(Key(1), table.Authority);
        Assert.Equal([Key(3), Key(4)], table.Addresses);
        Assert.Throws<FormatException>(() => AddressLookupTableAccount.Parse(Key(20), data.AsSpan(0, 60)));
    }
}
