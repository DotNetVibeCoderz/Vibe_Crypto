using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Crypto.Net.Core;
using Crypto.Net.Native;
using Crypto.Net.Wallet;

namespace Crypto.Net.Near;

/// <summary>NEAR network descriptor. Amounts are in yoctoNEAR (24 decimals).</summary>
public sealed record NearChain(ChainId Id, string Name, string DefaultRpcUrl, bool IsTestnet = false, string? ExplorerUrl = null) : IChain
{
    public string Symbol => "NEAR";
    public int Decimals => 24;

    public static readonly NearChain Mainnet = new(new ChainId("near"), "NEAR", "https://rpc.mainnet.fastnear.com", ExplorerUrl: "https://nearblocks.io");
    public static readonly NearChain Testnet = new(new ChainId("near-testnet"), "NEAR Testnet", "https://rpc.testnet.fastnear.com", true, "https://testnet.nearblocks.io");
    public static readonly NearChain Localnet = new(new ChainId("near-local"), "NEAR Localnet", "http://127.0.0.1:3030", true);

    public static IReadOnlyList<NearChain> All { get; } = [Mainnet, Testnet, Localnet];
}

/// <summary>NEAR account ids, public keys and units.</summary>
public static partial class NearAddress
{
    [GeneratedRegex("^(([a-z\\d]+[-_])*[a-z\\d]+\\.)*([a-z\\d]+[-_])*[a-z\\d]+$")]
    private static partial Regex AccountIdPattern();

    /// <summary>
    /// Valid account id: 2–64 characters of lowercase letters, digits, <c>-</c>, <c>_</c> and dots (named), or a
    /// 64-hex-character implicit account, or a 40-hex-character <c>0x…</c> ETH-implicit account.
    /// </summary>
    public static bool IsValidAccountId(string? accountId) =>
        accountId is { Length: >= 2 and <= 64 } && AccountIdPattern().IsMatch(accountId);

    public static bool IsImplicit(string accountId) => accountId.Length == 64 && accountId.All(Uri.IsHexDigit) && accountId == accountId.ToLowerInvariant();

    /// <summary>The implicit account id of an ed25519 public key (lowercase hex).</summary>
    public static string ImplicitAccountId(ReadOnlySpan<byte> publicKey) => System.Convert.ToHexStringLower(publicKey);

    /// <summary><c>ed25519:&lt;base58&gt;</c>.</summary>
    public static string FormatPublicKey(ReadOnlySpan<byte> publicKey) => "ed25519:" + CryptoNative.Base58Encode(publicKey);

    public static byte[] ParsePublicKey(string key)
    {
        string body = key.StartsWith("ed25519:", StringComparison.Ordinal) ? key[8..] : key;
        byte[] raw = CryptoNative.Base58Decode(body);
        return raw.Length == 32 ? raw : throw new FormatException("Only 32-byte ed25519 NEAR keys are supported");
    }

    /// <summary>1 NEAR = 10^24 yoctoNEAR.</summary>
    public static Amount Near(decimal near) => Amount.FromDecimal(near, 24);
    public static Amount Yocto(BigInteger yocto) => new(yocto, 24);

    /// <summary>One TGas = 10^12 gas units; 30 TGas is a common budget for a contract call.</summary>
    public const ulong TGas = 1_000_000_000_000;
}

/// <summary>A NEAR ed25519 key with its account id (implicit by default, or a named account it controls).</summary>
public sealed class NearAccount : IAccount, IDisposable
{
    private readonly SecureBuffer _seed;

    public NearAccount(ReadOnlySpan<byte> seed32, string? accountId = null, NearChain? network = null, string? derivationPath = null)
    {
        if (seed32.Length != 32) throw new ArgumentException("NEAR keys are 32-byte ed25519 seeds", nameof(seed32));
        _seed = SecureBuffer.FromBytes(seed32);
        PublicKey = CryptoNative.Ed25519GetPublicKey(seed32);
        AccountId = accountId ?? NearAddress.ImplicitAccountId(PublicKey.Span);
        if (!NearAddress.IsValidAccountId(AccountId)) throw new FormatException($"Invalid NEAR account id '{AccountId}'");
        Network = network ?? NearChain.Mainnet;
        Address = new Address(AccountId, Network.Id);
        DerivationPath = derivationPath;
    }

    /// <summary>Imports <c>ed25519:&lt;base58 64-byte secret&gt;</c> (NEAR CLI / wallet export format).</summary>
    public static NearAccount FromSecretKey(string secretKey, string? accountId = null, NearChain? network = null)
    {
        string body = secretKey.StartsWith("ed25519:", StringComparison.Ordinal) ? secretKey[8..] : secretKey;
        byte[] raw = CryptoNative.Base58Decode(body);
        try
        {
            if (raw.Length is not (32 or 64)) throw new FormatException("A NEAR secret key is 64 bytes (seed ‖ public key)");
            var account = new NearAccount(raw.AsSpan(0, 32), accountId, network);
            if (raw.Length == 64 && !raw.AsSpan(32).SequenceEqual(account.PublicKey.Span)) throw new FormatException("Secret key does not match its public key");
            return account;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(raw);
        }
    }

    public NearChain Network { get; }
    public IChain Chain => Network;
    public string AccountId { get; }
    public Address Address { get; }
    public ReadOnlyMemory<byte> PublicKey { get; }
    public string? DerivationPath { get; }

    /// <summary><c>ed25519:&lt;base58&gt;</c> public key.</summary>
    public string PublicKeyString => NearAddress.FormatPublicKey(PublicKey.Span);

    /// <summary><c>ed25519:&lt;base58 seed ‖ public key&gt;</c>, the format NEAR CLI stores.</summary>
    public string ExportSecretKey() => "ed25519:" + CryptoNative.Base58Encode([.. _seed.Span, .. PublicKey.Span]);

    /// <summary>The same key acting for another (named) account it is an access key of.</summary>
    public NearAccount ForAccount(string accountId) => new(_seed.Span, accountId, Network, DerivationPath);

    public byte[] Sign(ReadOnlySpan<byte> message) => CryptoNative.Ed25519Sign(_seed.Span, message);

    public KeySigner CreateSigner() => new(Network, Address, _seed.Clone(), SignatureScheme.Ed25519);

    public void Dispose() => _seed.Dispose();
}

public static class NearWalletExtensions
{
    /// <summary>NEAR wallet path <c>m/44'/397'/index'</c> (SLIP-10 ed25519), as used by MyNearWallet, Meteor and Ledger.</summary>
    public static NearAccount GetNearAccount(this HdWallet wallet, uint index = 0, NearChain? network = null, string? accountId = null)
    {
        string path = $"m/44'/397'/{index}'";
        using var key = wallet.DeriveEd25519Path(path);
        return new NearAccount(key.PrivateKey.Span, accountId, network, path);
    }
}

/// <summary>A NEAR transaction action.</summary>
public abstract record NearAction
{
    internal abstract void Write(BorshWriter w);

    public sealed record CreateAccount : NearAction
    {
        internal override void Write(BorshWriter w) => w.U8(0);
    }

    public sealed record DeployContract(byte[] Code) : NearAction
    {
        internal override void Write(BorshWriter w) => w.U8(1).VecU8(Code);
    }

    /// <summary>Calls <paramref name="MethodName"/> with raw <paramref name="Args"/> (usually UTF-8 JSON).</summary>
    public sealed record FunctionCall(string MethodName, byte[] Args, ulong Gas, BigInteger Deposit) : NearAction
    {
        internal override void Write(BorshWriter w) => w.U8(2).String(MethodName).VecU8(Args).U64(Gas).U128(Deposit);

        /// <summary>Function call with JSON arguments serialized from <paramref name="args"/>.</summary>
        public static FunctionCall Json(string method, object args, ulong gas = 30 * NearAddress.TGas, BigInteger? deposit = null) =>
            new(method, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(args, args.GetType()), gas, deposit ?? BigInteger.Zero);
    }

    public sealed record Transfer(BigInteger Deposit) : NearAction
    {
        internal override void Write(BorshWriter w) => w.U8(3).U128(Deposit);
    }

    public sealed record Stake(BigInteger Amount, byte[] PublicKey) : NearAction
    {
        internal override void Write(BorshWriter w) => w.U8(4).U128(Amount).U8(0).Bytes(PublicKey);
    }

    /// <summary>Adds a full-access key, or a function-call key when <paramref name="ReceiverId"/> is set.</summary>
    public sealed record AddKey(byte[] PublicKey, string? ReceiverId = null, IReadOnlyList<string>? MethodNames = null, BigInteger? Allowance = null) : NearAction
    {
        internal override void Write(BorshWriter w)
        {
            w.U8(5).U8(0).Bytes(PublicKey).U64(0);
            if (ReceiverId is null)
            {
                w.U8(1);
                return;
            }
            w.U8(0);
            if (Allowance is { } a) w.U8(1).U128(a);
            else w.U8(0);
            w.String(ReceiverId).Vec(MethodNames ?? [], (bw, m) => bw.String(m));
        }
    }

    public sealed record DeleteKey(byte[] PublicKey) : NearAction
    {
        internal override void Write(BorshWriter w) => w.U8(6).U8(0).Bytes(PublicKey);
    }

    public sealed record DeleteAccount(string BeneficiaryId) : NearAction
    {
        internal override void Write(BorshWriter w) => w.U8(7).String(BeneficiaryId);
    }
}

/// <summary>A NEAR transaction: Borsh-encoded, signed as ed25519(sha256(bytes)).</summary>
public sealed class NearTransaction
{
    public required string SignerId { get; init; }
    public required byte[] PublicKey { get; init; }
    public required ulong Nonce { get; init; }
    public required string ReceiverId { get; init; }
    public required byte[] BlockHash { get; init; }
    public List<NearAction> Actions { get; init; } = [];

    public byte[] Serialize()
    {
        if (BlockHash.Length != 32) throw new InvalidOperationException("Block hash must be 32 bytes");
        var w = new BorshWriter().String(SignerId).U8(0).Bytes(PublicKey).U64(Nonce).String(ReceiverId).Bytes(BlockHash);
        w.U32((uint)Actions.Count);
        foreach (var a in Actions) a.Write(w);
        return w.ToArray();
    }

    /// <summary>sha256 of the serialized transaction: what is signed, and (Base58) the transaction hash.</summary>
    public byte[] Hash() => CryptoNative.Sha256(Serialize());

    public string HashBase58 => CryptoNative.Base58Encode(Hash());

    /// <summary>Signs with <paramref name="account"/> and returns the Borsh <c>SignedTransaction</c>.</summary>
    public byte[] Sign(NearAccount account)
    {
        if (!account.PublicKey.Span.SequenceEqual(PublicKey)) throw new InvalidOperationException("The account key does not match the transaction public key");
        byte[] body = Serialize();
        byte[] signature = account.Sign(CryptoNative.Sha256(body));
        return [.. body, 0, .. signature];
    }
}
