using System.IO.Compression;
using System.Numerics;
using Crypto.Net.Polkadot;

namespace Crypto.Net.Tests;

/// <summary>Metadata-driven SCALE encoding against real Polkadot Asset Hub metadata.</summary>
public class ScaleEncoderTests
{
    private static readonly Lazy<RuntimeMetadata> Metadata = new(() =>
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Vectors", "asset-hub-metadata.bin.gz"));
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return RuntimeMetadata.Parse(ms.ToArray());
    });

    private const string Alice = "15oF4uVJwmo4TdGW7VfQxNLavjCXviqxT9S1MgbjMNHr6Sp5";
    private const string Bob = "14E5nqKAp3oAJcmzgZhUD2RcptBeUBScxKHgJKU4HPNcKVf3";

    private static ScaleVariant DecodeCall(byte[] call, string pallet)
    {
        var md = Metadata.Value;
        var p = md.Pallets[pallet];
        Assert.Equal(p.Index, call[0]);
        return (ScaleVariant)ScaleDecoder.Decode(md, p.CallsType!.Value, call.AsSpan(1))!;
    }

    [Fact]
    public void GenericCall_MatchesHandWrittenTransfer()
    {
        var md = Metadata.Value;
        byte[] manual = Extrinsic.TransferKeepAlive(md, Bob, 12_345_678_900);
        byte[] generic = ScaleEncoder.EncodeCall(md, "Balances", "transfer_keep_alive", new { dest = Bob, value = new BigInteger(12_345_678_900) });
        Assert.Equal(manual, generic);
        // Explicit enum form gives the same bytes.
        byte[] explicitForm = ScaleEncoder.EncodeCall(md, "Balances", "transfer_keep_alive",
            new Dictionary<string, object?> { ["dest"] = new ScaleVariant("Id", 0, Ss58Address.Decode(Bob).PublicKey), ["value"] = 12_345_678_900L });
        Assert.Equal(manual, explicitForm);
    }

    [Fact]
    public void Staking_CallsRoundTrip()
    {
        var md = Metadata.Value;
        Assert.True(md.Pallets.ContainsKey("Staking"), "fixture runtime has no Staking pallet");

        var nominate = DecodeCall(PolkadotStaking.Nominate(md, [Alice, Bob]), "Staking");
        Assert.Equal("nominate", nominate.Name);
        var targets = (object?[])((IReadOnlyDictionary<string, object?>)nominate.Value!)["targets"]!;
        Assert.Equal(2, targets.Length);
        Assert.Equal(Ss58Address.Decode(Bob).PublicKey, (byte[])((ScaleVariant)targets[1]!).Value!);

        var bond = DecodeCall(PolkadotStaking.Bond(md, 10_000_000_000, "Account", Alice), "Staking");
        var bondArgs = (IReadOnlyDictionary<string, object?>)bond.Value!;
        Assert.Equal(new BigInteger(10_000_000_000), bondArgs["value"]);
        Assert.Equal("Account", ((ScaleVariant)bondArgs["payee"]!).Name);

        Assert.Equal("chill", DecodeCall(PolkadotStaking.Chill(md), "Staking").Name);
        Assert.Equal("withdraw_unbonded", DecodeCall(PolkadotStaking.WithdrawUnbonded(md, 3), "Staking").Name);

        var join = DecodeCall(PolkadotStaking.JoinPool(md, 5_000_000_000, 12), "NominationPools");
        Assert.Equal(12u, ((IReadOnlyDictionary<string, object?>)join.Value!)["pool_id"]);
        var extra = DecodeCall(PolkadotStaking.PoolBondExtra(md, 7), "NominationPools");
        Assert.Equal("FreeBalance", ((ScaleVariant)((IReadOnlyDictionary<string, object?>)extra.Value!)["extra"]!).Name);
    }

    [Fact]
    public void Encoder_ValidatesInput()
    {
        var md = Metadata.Value;
        Assert.Throws<ArgumentException>(() => ScaleEncoder.EncodeCall(md, "Balances", "transfer_keep_alive", new { dest = Bob }));
        Assert.Throws<ArgumentException>(() => ScaleEncoder.EncodeCall(md, "Balances", "transfer_keep_alive", new { dest = Bob, value = 1.5 }));
        Assert.Throws<KeyNotFoundException>(() => ScaleEncoder.EncodeCall(md, "Balances", "no_such_call"));
        Assert.Throws<ArgumentException>(() => ScaleEncoder.EncodeCall(md, "System", "remark_with_event", new { remark = (object?)null! }));
    }

    [Fact]
    public void RemarkAndBatch_EncodeNestedCalls()
    {
        var md = Metadata.Value;
        byte[] remark = ScaleEncoder.EncodeCall(md, "System", "remark", new { remark = "hello"u8.ToArray() });
        Assert.Equal(Extrinsic.Remark(md, "hello"u8), remark);

        // Utility.batch_all takes Vec<RuntimeCall>: nested calls are ScaleVariants (pallet) of ScaleVariants (call).
        var inner = new ScaleVariant("System", 0, new ScaleVariant("remark", 0, new Dictionary<string, object?> { ["remark"] = "hi"u8.ToArray() }));
        byte[] batch = ScaleEncoder.EncodeCall(md, "Utility", "batch_all", new { calls = new[] { inner, inner } });
        var decoded = DecodeCall(batch, "Utility");
        Assert.Equal(2, ((object?[])((IReadOnlyDictionary<string, object?>)decoded.Value!)["calls"]!).Length);
    }
}
