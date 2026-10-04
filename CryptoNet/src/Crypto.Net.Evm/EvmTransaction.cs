using System.Numerics;
using Crypto.Net.Core;
using Crypto.Net.Native;

namespace Crypto.Net.Evm;

/// <summary>EVM transaction envelope types.</summary>
public enum EvmTransactionType : byte
{
    /// <summary>Pre-typed transaction, signed with EIP-155 replay protection when <see cref="EvmTransaction.ChainId"/> &gt; 0.</summary>
    Legacy = 0,
    /// <summary>EIP-2930 access-list transaction.</summary>
    AccessList = 1,
    /// <summary>EIP-1559 dynamic-fee transaction (the default).</summary>
    DynamicFee = 2,
}

/// <summary>EIP-2930 access-list entry.</summary>
public sealed record AccessListEntry(string Address, IReadOnlyList<byte[]> StorageKeys);

/// <summary>
/// Unsigned EVM transaction (Legacy/EIP-155, EIP-2930 or EIP-1559). Build it, then call
/// <see cref="Sign(ReadOnlySpan{byte})"/> or <see cref="SignAsync"/> to obtain the raw bytes to broadcast.
/// </summary>
public sealed class EvmTransaction
{
    public EvmTransactionType Type { get; set; } = EvmTransactionType.DynamicFee;
    public ulong ChainId { get; set; } = 1;
    public ulong Nonce { get; set; }

    /// <summary>Gas price in wei (Legacy and EIP-2930 only).</summary>
    public BigInteger GasPrice { get; set; }

    /// <summary>Priority fee (tip) in wei (EIP-1559 only).</summary>
    public BigInteger MaxPriorityFeePerGas { get; set; }

    /// <summary>Fee cap in wei (EIP-1559 only).</summary>
    public BigInteger MaxFeePerGas { get; set; }

    public ulong GasLimit { get; set; } = 21_000;

    /// <summary>Recipient, or <c>null</c> for contract creation.</summary>
    public string? To { get; set; }

    /// <summary>Value in wei.</summary>
    public BigInteger Value { get; set; }

    public byte[] Data { get; set; } = [];

    public List<AccessListEntry> AccessList { get; set; } = [];

    /// <summary>Upper bound of the fee this transaction can pay, in wei.</summary>
    public BigInteger MaxCost => Value + GasLimit * (Type == EvmTransactionType.DynamicFee ? MaxFeePerGas : GasPrice);

    private byte[][] CommonFields()
    {
        byte[] to = To is null ? EvmRlp.EncodeBytes([]) : EvmRlp.EncodeBytes(ToAddressBytes(To));
        byte[] value = EvmRlp.EncodeUInt(Value);
        byte[] data = EvmRlp.EncodeBytes(Data);
        byte[] gas = EvmRlp.EncodeUInt(GasLimit);
        byte[] nonce = EvmRlp.EncodeUInt(Nonce);

        return Type switch
        {
            EvmTransactionType.Legacy => [nonce, EvmRlp.EncodeUInt(GasPrice), gas, to, value, data],
            EvmTransactionType.AccessList => [EvmRlp.EncodeUInt(ChainId), nonce, EvmRlp.EncodeUInt(GasPrice), gas, to, value, data, EncodeAccessList()],
            EvmTransactionType.DynamicFee => [EvmRlp.EncodeUInt(ChainId), nonce, EvmRlp.EncodeUInt(MaxPriorityFeePerGas), EvmRlp.EncodeUInt(MaxFeePerGas), gas, to, value, data, EncodeAccessList()],
            _ => throw new NotSupportedException($"Transaction type {Type}"),
        };
    }

    private byte[] EncodeAccessList() =>
        EvmRlp.EncodeList(AccessList.Select(e => EvmRlp.EncodeList(
            EvmRlp.EncodeBytes(ToAddressBytes(e.Address)),
            EvmRlp.EncodeList(e.StorageKeys.Select(k => k.Length == 32 ? EvmRlp.EncodeBytes(k) : throw new ArgumentException("Storage keys must be 32 bytes")))))
        .ToArray());

    private static byte[] ToAddressBytes(string address)
    {
        if (!EvmAddress.IsValid(address)) throw new FormatException($"Invalid EVM address '{address}'");
        return HexUtil.Decode(address);
    }

    private byte[] Typed(byte[] rlp) => Type == EvmTransactionType.Legacy ? rlp : [(byte)Type, .. rlp];

    /// <summary>The exact bytes that are hashed for signing.</summary>
    public byte[] GetSigningPayload()
    {
        var fields = CommonFields();
        if (Type == EvmTransactionType.Legacy && ChainId > 0)
            fields = [.. fields, EvmRlp.EncodeUInt(ChainId), EvmRlp.EncodeUInt(0), EvmRlp.EncodeUInt(0)];
        return Typed(EvmRlp.EncodeList(fields));
    }

    /// <summary>keccak256 of <see cref="GetSigningPayload"/>.</summary>
    public byte[] GetSigningHash() => CryptoNative.Keccak256(GetSigningPayload());

    /// <summary>Signs with a raw 32-byte private key.</summary>
    public SignedEvmTransaction Sign(ReadOnlySpan<byte> privateKey)
    {
        var (sig, recId) = CryptoNative.Secp256k1SignRecoverable(privateKey, GetSigningHash());
        return WithSignature(sig, recId);
    }

    /// <summary>Signs with any secp256k1 <see cref="ISigner"/> (in-memory key, HSM, remote signer…).</summary>
    public async ValueTask<SignedEvmTransaction> SignAsync(ISigner signer, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signer);
        if (signer.Scheme != SignatureScheme.Secp256k1Ecdsa) throw new ArgumentException("EVM transactions require a secp256k1 ECDSA signer");
        var signature = await signer.SignAsync(GetSigningHash(), ct).ConfigureAwait(false);
        return WithSignature(signature.Bytes.Span, signature.RecoveryId ?? throw new InvalidOperationException("Signer did not return a recovery id"));
    }

    /// <summary>Attaches a 64-byte r||s signature and recovery id.</summary>
    public SignedEvmTransaction WithSignature(ReadOnlySpan<byte> rs, byte recoveryId)
    {
        if (rs.Length != 64) throw new ArgumentException("Signature must be 64 bytes (r||s)");
        if (recoveryId > 1) throw new ArgumentException("Recovery id must be 0 or 1");
        BigInteger r = new(rs[..32], isUnsigned: true, isBigEndian: true);
        BigInteger s = new(rs[32..], isUnsigned: true, isBigEndian: true);

        BigInteger v = Type switch
        {
            EvmTransactionType.Legacy when ChainId > 0 => recoveryId + 35 + 2 * (BigInteger)ChainId,
            EvmTransactionType.Legacy => recoveryId + 27,
            _ => recoveryId,
        };

        byte[] raw = Typed(EvmRlp.EncodeList([.. CommonFields(), EvmRlp.EncodeUInt(v), EvmRlp.EncodeUInt(r), EvmRlp.EncodeUInt(s)]));
        return new SignedEvmTransaction(this, raw, r, s, v, recoveryId);
    }

    /// <summary>Decodes a signed raw transaction (any supported type).</summary>
    public static SignedEvmTransaction DecodeSigned(ReadOnlySpan<byte> raw)
    {
        if (raw.IsEmpty) throw new FormatException("Empty transaction");
        var tx = new EvmTransaction();
        RlpItem list;
        if (raw[0] >= 0xc0)
        {
            tx.Type = EvmTransactionType.Legacy;
            list = EvmRlp.Decode(raw);
            if (list.Items!.Count != 9) throw new FormatException("Legacy transaction must have 9 fields");
            tx.Nonce = list[0].ToUInt64();
            tx.GasPrice = list[1].ToBigInteger();
            tx.GasLimit = list[2].ToUInt64();
            tx.To = ReadTo(list[3]);
            tx.Value = list[4].ToBigInteger();
            tx.Data = list[5].Bytes!;
            BigInteger v = list[6].ToBigInteger();
            byte rec;
            if (v >= 35)
            {
                tx.ChainId = (ulong)((v - 35) / 2);
                rec = (byte)((v - 35) % 2);
            }
            else
            {
                tx.ChainId = 0;
                rec = (byte)(v - 27);
            }
            return new SignedEvmTransaction(tx, raw.ToArray(), list[7].ToBigInteger(), list[8].ToBigInteger(), v, rec);
        }

        tx.Type = (EvmTransactionType)raw[0];
        list = EvmRlp.Decode(raw[1..]);
        int i = 0;
        tx.ChainId = list[i++].ToUInt64();
        tx.Nonce = list[i++].ToUInt64();
        if (tx.Type == EvmTransactionType.DynamicFee)
        {
            tx.MaxPriorityFeePerGas = list[i++].ToBigInteger();
            tx.MaxFeePerGas = list[i++].ToBigInteger();
        }
        else if (tx.Type == EvmTransactionType.AccessList)
        {
            tx.GasPrice = list[i++].ToBigInteger();
        }
        else
        {
            throw new NotSupportedException($"Transaction type 0x{raw[0]:x2} is not supported");
        }
        tx.GasLimit = list[i++].ToUInt64();
        tx.To = ReadTo(list[i++]);
        tx.Value = list[i++].ToBigInteger();
        tx.Data = list[i++].Bytes!;
        tx.AccessList = list[i++].Items!.Select(e => new AccessListEntry(
            EvmAddress.ToChecksumAddress(e[0].Bytes!),
            e[1].Items!.Select(k => k.Bytes!).ToList())).ToList();
        BigInteger yParity = list[i++].ToBigInteger();
        return new SignedEvmTransaction(tx, raw.ToArray(), list[i++].ToBigInteger(), list[i].ToBigInteger(), yParity, (byte)yParity);
    }

    private static string? ReadTo(RlpItem item) => item.Bytes is { Length: 20 } b ? EvmAddress.ToChecksumAddress(b) : null;
}

/// <summary>A signed transaction ready to broadcast.</summary>
public sealed record SignedEvmTransaction(EvmTransaction Transaction, byte[] RawBytes, BigInteger R, BigInteger S, BigInteger V, byte RecoveryId)
{
    /// <summary>Transaction hash (keccak256 of the raw bytes).</summary>
    public TxHash Hash => TxHash.FromBytes(CryptoNative.Keccak256(RawBytes));

    /// <summary>Raw bytes as 0x-prefixed hex, the format expected by <c>eth_sendRawTransaction</c>.</summary>
    public string RawHex => HexUtil.Encode(RawBytes);

    /// <summary>Recovers the sender address from the signature.</summary>
    public string RecoverSender()
    {
        byte[] rs = new byte[64];
        R.TryWriteBytes(rs.AsSpan(32 - R.GetByteCount(true), R.GetByteCount(true)), out _, true, true);
        S.TryWriteBytes(rs.AsSpan(64 - S.GetByteCount(true), S.GetByteCount(true)), out _, true, true);
        byte[] pub = CryptoNative.Secp256k1RecoverPublicKey(Transaction.GetSigningHash(), rs, RecoveryId, compressed: false);
        return EvmAddress.FromPublicKey(pub).Value;
    }
}
