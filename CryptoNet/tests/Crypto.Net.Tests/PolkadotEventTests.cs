using System.IO.Compression;
using System.Numerics;
using System.Text;
using Crypto.Net.Polkadot;

namespace Crypto.Net.Tests;

/// <summary>
/// System.Events decoding against real Polkadot Asset Hub data: runtime metadata plus the events of block 21723176
/// (extrinsic 2 failed with <c>Token(Frozen)</c>) and block 21723168 (a successful transfer).
/// </summary>
public class PolkadotEventTests
{
    private static readonly Lazy<RuntimeMetadata> Metadata = new(() => RuntimeMetadata.Parse(Fixture("asset-hub-metadata")));

    private static byte[] Fixture(string name)
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Vectors", name + ".bin.gz"));
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Twox_StorageKeys_MatchKnownPrefixes()
    {
        Assert.Equal("26aa394eea5630e07c48ae0c9558cef780d41e5e16056765bc8461851072c9d7", Convert.ToHexStringLower(SubstrateEvents.StorageKey));
        Assert.Equal("26aa394eea5630e07c48ae0c9558cef7b99d880ec681799c0cf30e8886371da9", Convert.ToHexStringLower(StorageKeys.Prefix("System", "Account")));
        // xxHash64 reference values (seed 0): "" and "abc"
        Assert.Equal(0xEF46DB3751D8E999UL, StorageKeys.XxHash64([], 0));
        Assert.Equal(0x44BC2CF5AD770999UL, StorageKeys.XxHash64("abc"u8, 0));
        byte[] longInput = Encoding.ASCII.GetBytes(new string('a', 100));
        Assert.Equal(StorageKeys.XxHash64(longInput, 0), StorageKeys.XxHash64(longInput.AsSpan(), 0));
    }

    [Fact]
    public void Metadata_ExposesRegistryAndStorage()
    {
        var md = Metadata.Value;
        Assert.Equal(14, md.Version);
        var events = md.GetStorageEntry("System", "Events");
        Assert.Empty(events.Hashers);
        var account = md.GetStorageEntry("System", "Account");
        Assert.Equal([StorageHasher.Blake2_128Concat], account.Hashers);
        Assert.NotNull(md.Pallets["Balances"].ErrorsType);
        Assert.NotNull(md.Pallets["Balances"].EventsType);

        byte[] accountId = new byte[32];
        Assert.Equal(StorageKeys.Prefix("System", "Account").Length + 16 + 32, md.StorageKey("System", "Account", accountId).Length);
    }

    [Fact]
    public void FailedExtrinsic_ReportsDispatchError()
    {
        var md = Metadata.Value;
        var events = SubstrateEvents.Decode(md, Fixture("asset-hub-events-failed"));
        Assert.Equal(8, events.Count);
        Assert.Equal((false, "Token.Frozen"), SubstrateEvents.Outcome(md, events, 2));
        Assert.Equal((true, null), SubstrateEvents.Outcome(md, events, 0));
        Assert.Null(SubstrateEvents.Outcome(md, events, 9));

        var fee = events.Single(e => e.Is("TransactionPayment", "TransactionFeePaid"));
        Assert.Equal(new BigInteger(8970082), fee["actual_fee"]);
        Assert.Equal(EventPhase.Finalization, events[^1].Phase);
    }

    [Fact]
    public void SuccessfulTransfer_DecodesFields()
    {
        var md = Metadata.Value;
        var events = SubstrateEvents.Decode(md, Fixture("asset-hub-events-ok"));
        var transfer = events.First(e => e.Is("Balances", "Transfer") && e.ExtrinsicIndex == 2);
        Assert.Equal(new BigInteger(71397840829918), transfer["amount"]);
        Assert.Equal("b9a3a1678a7b4b754c5d924bf43ed1c1e8d97a4e53be9023212f90eceba7f06f", Convert.ToHexStringLower((byte[])transfer["to"]!));
        string address = Ss58Address.Encode((byte[])transfer["to"]!, 0);
        Assert.Equal((byte[])transfer["to"]!, Ss58Address.Decode(address).PublicKey);
        Assert.Equal((true, null), SubstrateEvents.Outcome(md, events, 2));
        Assert.StartsWith("Balances.Transfer {from: 0x", transfer.ToString());
    }

    [Fact]
    public void ModuleError_ResolvesPalletErrorName()
    {
        var md = Metadata.Value;
        var balances = md.Pallets["Balances"];
        var errors = md.Types[balances.ErrorsType!.Value].Variants;
        var target = errors.First(v => v.Name == "InsufficientBalance");
        var error = new ScaleVariant("Module", 3, new Dictionary<string, object?>
        {
            ["index"] = balances.Index,
            ["error"] = new byte[] { target.Index, 0, 0, 0 },
        });
        Assert.Equal("Balances.InsufficientBalance", SubstrateEvents.DescribeDispatchError(md, error));
        Assert.Equal("BadOrigin", SubstrateEvents.DescribeDispatchError(md, new ScaleVariant("BadOrigin", 2, null)));
    }

    [Fact]
    public void Decoder_RejectsTruncatedData()
    {
        var md = Metadata.Value;
        byte[] raw = Fixture("asset-hub-events-ok");
        Assert.Throws<FormatException>(() => SubstrateEvents.Decode(md, raw.AsSpan(0, raw.Length - 3)));
        Assert.Throws<FormatException>(() => SubstrateEvents.Decode(md, [.. raw, 0]));
    }
}
