using System.Numerics;
using Crypto.Net.Core;
using Crypto.Net.Native;

namespace Crypto.Net.Polkadot;

/// <summary>Chain state required to sign an extrinsic.</summary>
public sealed record ExtrinsicContext(
    uint SpecVersion,
    uint TransactionVersion,
    byte[] GenesisHash,
    byte[] BlockHash,
    ulong BlockNumber,
    uint Nonce,
    BigInteger Tip,
    ulong? MortalPeriod = 64);

/// <summary>Builds and signs v4 extrinsics using the signed-extension list from runtime metadata.</summary>
public static class Extrinsic
{
    /// <summary>Encodes a call: pallet index, call index, then SCALE-encoded arguments.</summary>
    public static byte[] Call(RuntimeMetadata metadata, string pallet, string call, ReadOnlySpan<byte> encodedArgs)
    {
        var (p, c) = metadata.GetCallIndex(pallet, call);
        return [p, c, .. encodedArgs];
    }

    /// <summary><c>Balances.transfer_keep_alive(MultiAddress::Id(dest), Compact&lt;u128&gt; value)</c>.</summary>
    public static byte[] TransferKeepAlive(RuntimeMetadata metadata, string destination, BigInteger planck)
    {
        var (_, destKey) = Ss58Address.Decode(destination);
        byte[] args = new ScaleWriter().U8(0x00).Bytes(destKey).Compact(planck).ToArray();
        return Call(metadata, "Balances", "transfer_keep_alive", args);
    }

    /// <summary><c>System.remark(Vec&lt;u8&gt;)</c>.</summary>
    public static byte[] Remark(RuntimeMetadata metadata, ReadOnlySpan<byte> remark) =>
        Call(metadata, "System", "remark", new ScaleWriter().VecU8(remark).ToArray());

    /// <summary>Mortal era encoding (period rounded to a power of two in [4, 65536]).</summary>
    public static byte[] EncodeMortalEra(ulong period, ulong currentBlock)
    {
        ulong p = 4;
        while (p < period && p < 65536) p <<= 1;
        ulong phase = currentBlock % p;
        ulong quantizeFactor = Math.Max(p >> 12, 1);
        ulong quantizedPhase = phase / quantizeFactor * quantizeFactor;
        int trailing = BitOperations.TrailingZeroCount(p);
        ushort encoded = (ushort)(Math.Min(15, Math.Max(1, trailing - 1)) | (int)((quantizedPhase / quantizeFactor) << 4));
        return [(byte)encoded, (byte)(encoded >> 8)];
    }

    /// <summary>
    /// Produces the (extra, additional-signed) bytes for every signed extension in metadata order.
    /// Unknown extensions are accepted only when they carry no data.
    /// </summary>
    public static (byte[] Extra, byte[] Additional) EncodeSignedExtensions(RuntimeMetadata metadata, ExtrinsicContext ctx)
    {
        var extra = new ScaleWriter();
        var additional = new ScaleWriter();
        bool mortal = ctx.MortalPeriod is > 0;

        foreach (var ext in metadata.SignedExtensions)
        {
            switch (ext.Identifier)
            {
                case "CheckSpecVersion":
                    additional.U32(ctx.SpecVersion);
                    break;
                case "CheckTxVersion":
                    additional.U32(ctx.TransactionVersion);
                    break;
                case "CheckGenesis":
                    additional.Bytes(ctx.GenesisHash);
                    break;
                case "CheckMortality":
                case "CheckEra":
                    if (mortal)
                    {
                        extra.Bytes(EncodeMortalEra(ctx.MortalPeriod!.Value, ctx.BlockNumber));
                        additional.Bytes(ctx.BlockHash);
                    }
                    else
                    {
                        extra.U8(0x00);
                        additional.Bytes(ctx.GenesisHash);
                    }
                    break;
                case "CheckNonce":
                    extra.Compact(ctx.Nonce);
                    break;
                case "ChargeTransactionPayment":
                    extra.Compact(ctx.Tip);
                    break;
                case "ChargeAssetTxPayment":
                    extra.Compact(ctx.Tip).U8(0x00); // asset_id: None (pay fees in the native token)
                    break;
                case "CheckMetadataHash":
                    extra.U8(0x00); // mode: disabled
                    additional.U8(0x00); // Option<H256>::None
                    break;
                default:
                    if (!metadata.IsZeroSized(ext.ExtraType) || !metadata.IsZeroSized(ext.AdditionalType))
                        throw new NotSupportedException($"Signed extension '{ext.Identifier}' carries data that Crypto.Net cannot fill");
                    break;
            }
        }
        return (extra.ToArray(), additional.ToArray());
    }

    /// <summary>Signs <paramref name="call"/> and returns the length-prefixed extrinsic ready for <c>author_submitExtrinsic</c>.</summary>
    public static byte[] Sign(RuntimeMetadata metadata, byte[] call, PolkadotAccount signer, ExtrinsicContext ctx)
    {
        ArgumentNullException.ThrowIfNull(signer);
        var (extra, additional) = EncodeSignedExtensions(metadata, ctx);

        byte[] payload = [.. call, .. extra, .. additional];
        if (payload.Length > 256) payload = CryptoNative.Blake2b256(payload);
        byte[] signature = signer.Sign(payload);

        byte[] body = new ScaleWriter()
            .U8(0x84)                                     // signed, extrinsic format v4
            .U8(0x00).Bytes(signer.PublicKey.Span)        // MultiAddress::Id
            .U8(signer.Scheme == SignatureScheme.Sr25519 ? (byte)0x01 : (byte)0x00).Bytes(signature) // MultiSignature
            .Bytes(extra)
            .Bytes(call)
            .ToArray();
        return [.. Scale.EncodeCompact(body.Length), .. body];
    }

    /// <summary>Extrinsic hash as reported by explorers: blake2_256 of the full encoded extrinsic.</summary>
    public static TxHash Hash(ReadOnlySpan<byte> extrinsic) => TxHash.FromBytes(CryptoNative.Blake2b256(extrinsic));
}
