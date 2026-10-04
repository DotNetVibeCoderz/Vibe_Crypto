using System.Numerics;
using System.Text;
using Crypto.Net.Core;
using Crypto.Net.Native;

namespace Crypto.Net.Cosmos;

/// <summary>Minimal protobuf writer for Cosmos SDK messages.</summary>
public sealed class ProtoWriter
{
    private readonly MemoryStream _ms = new();

    private void Tag(int field, int wireType) => Varint((ulong)((field << 3) | wireType));

    public void Varint(ulong value)
    {
        while (value >= 0x80)
        {
            _ms.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        _ms.WriteByte((byte)value);
    }

    public ProtoWriter UInt64(int field, ulong value)
    {
        if (value == 0) return this; // proto3 default values are omitted
        Tag(field, 0);
        Varint(value);
        return this;
    }

    public ProtoWriter Bytes(int field, ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty) return this;
        Tag(field, 2);
        Varint((ulong)value.Length);
        _ms.Write(value);
        return this;
    }

    public ProtoWriter String(int field, string? value) => string.IsNullOrEmpty(value) ? this : Bytes(field, Encoding.UTF8.GetBytes(value));

    /// <summary>Embedded message (written even when empty, as Cosmos requires for some fields).</summary>
    public ProtoWriter Message(int field, byte[] encoded)
    {
        Tag(field, 2);
        Varint((ulong)encoded.Length);
        _ms.Write(encoded);
        return this;
    }

    public byte[] ToArray() => _ms.ToArray();
}

/// <summary>A Cosmos SDK coin.</summary>
public sealed record Coin(string Denom, BigInteger Amount)
{
    public byte[] Encode() => new ProtoWriter().String(1, Denom).String(2, Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
}

/// <summary>A message packed as <c>google.protobuf.Any</c>.</summary>
public sealed record CosmosMessage(string TypeUrl, byte[] Value)
{
    public byte[] EncodeAny() => new ProtoWriter().String(1, TypeUrl).Bytes(2, Value).ToArray();

    /// <summary><c>/cosmos.bank.v1beta1.MsgSend</c>.</summary>
    public static CosmosMessage BankSend(string from, string to, params Coin[] amount)
    {
        var w = new ProtoWriter().String(1, from).String(2, to);
        foreach (var c in amount) w.Message(3, c.Encode());
        return new CosmosMessage("/cosmos.bank.v1beta1.MsgSend", w.ToArray());
    }

    /// <summary><c>/cosmos.staking.v1beta1.MsgDelegate</c>.</summary>
    public static CosmosMessage Delegate(string delegator, string validator, Coin amount) =>
        new("/cosmos.staking.v1beta1.MsgDelegate", new ProtoWriter().String(1, delegator).String(2, validator).Message(3, amount.Encode()).ToArray());

    /// <summary><c>/cosmos.staking.v1beta1.MsgUndelegate</c>.</summary>
    public static CosmosMessage Undelegate(string delegator, string validator, Coin amount) =>
        new("/cosmos.staking.v1beta1.MsgUndelegate", new ProtoWriter().String(1, delegator).String(2, validator).Message(3, amount.Encode()).ToArray());

    /// <summary><c>/cosmos.distribution.v1beta1.MsgWithdrawDelegatorReward</c>.</summary>
    public static CosmosMessage WithdrawRewards(string delegator, string validator) =>
        new("/cosmos.distribution.v1beta1.MsgWithdrawDelegatorReward", new ProtoWriter().String(1, delegator).String(2, validator).ToArray());
}

/// <summary>
/// Builds and signs Cosmos SDK transactions in <c>SIGN_MODE_DIRECT</c>:
/// signature = secp256k1(sha256(SignDoc{body, authInfo, chainId, accountNumber})).
/// </summary>
public sealed class CosmosTxBuilder
{
    public List<CosmosMessage> Messages { get; } = [];
    public string Memo { get; set; } = "";
    public ulong TimeoutHeight { get; set; }
    public ulong GasLimit { get; set; } = 200_000;
    public List<Coin> Fee { get; } = [];

    public CosmosTxBuilder Add(CosmosMessage message)
    {
        Messages.Add(message);
        return this;
    }

    public byte[] EncodeBody()
    {
        var w = new ProtoWriter();
        foreach (var m in Messages) w.Message(1, m.EncodeAny());
        return w.String(2, Memo).UInt64(3, TimeoutHeight).ToArray();
    }

    public byte[] EncodeAuthInfo(ReadOnlySpan<byte> compressedPublicKey, ulong sequence)
    {
        byte[] pubKeyAny = new ProtoWriter()
            .String(1, "/cosmos.crypto.secp256k1.PubKey")
            .Bytes(2, new ProtoWriter().Bytes(1, compressedPublicKey).ToArray())
            .ToArray();
        byte[] modeInfo = new ProtoWriter().Message(1, new ProtoWriter().UInt64(1, 1).ToArray()).ToArray(); // single { mode: SIGN_MODE_DIRECT }
        byte[] signerInfo = new ProtoWriter().Message(1, pubKeyAny).Message(2, modeInfo).UInt64(3, sequence).ToArray();

        var fee = new ProtoWriter();
        foreach (var c in Fee) fee.Message(1, c.Encode());
        fee.UInt64(2, GasLimit);

        return new ProtoWriter().Message(1, signerInfo).Message(2, fee.ToArray()).ToArray();
    }

    public static byte[] EncodeSignDoc(byte[] body, byte[] authInfo, string chainId, ulong accountNumber) =>
        new ProtoWriter().Bytes(1, body).Bytes(2, authInfo).String(3, chainId).UInt64(4, accountNumber).ToArray();

    public static byte[] EncodeTxRaw(byte[] body, byte[] authInfo, params byte[][] signatures)
    {
        var w = new ProtoWriter().Bytes(1, body).Bytes(2, authInfo);
        foreach (var s in signatures) w.Message(3, s);
        return w.ToArray();
    }

    /// <summary>Signs with a raw secp256k1 key and returns TxRaw bytes ready to broadcast.</summary>
    public byte[] Sign(ReadOnlySpan<byte> privateKey, string chainId, ulong accountNumber, ulong sequence)
    {
        byte[] pub = CryptoNative.Secp256k1GetPublicKey(privateKey, compressed: true);
        byte[] body = EncodeBody();
        byte[] authInfo = EncodeAuthInfo(pub, sequence);
        byte[] digest = CryptoNative.Sha256(EncodeSignDoc(body, authInfo, chainId, accountNumber));
        var (sig, _) = CryptoNative.Secp256k1SignRecoverable(privateKey, digest);
        return EncodeTxRaw(body, authInfo, sig);
    }

    public async ValueTask<byte[]> SignAsync(ISigner signer, string chainId, ulong accountNumber, ulong sequence, CancellationToken ct = default)
    {
        if (signer.Scheme != SignatureScheme.Secp256k1Ecdsa) throw new ArgumentException("Cosmos requires a secp256k1 signer");
        byte[] body = EncodeBody();
        byte[] authInfo = EncodeAuthInfo(signer.PublicKey.Span, sequence);
        byte[] digest = CryptoNative.Sha256(EncodeSignDoc(body, authInfo, chainId, accountNumber));
        var sig = await signer.SignAsync(digest, ct).ConfigureAwait(false);
        return EncodeTxRaw(body, authInfo, sig.Bytes.ToArray());
    }

    /// <summary>Cosmos transaction hash: uppercase hex SHA-256 of TxRaw.</summary>
    public static string Hash(ReadOnlySpan<byte> txRaw) => Convert.ToHexString(CryptoNative.Sha256(txRaw));
}
