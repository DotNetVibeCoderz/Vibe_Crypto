using Crypto.Net.Core;
using System.Buffers.Binary;
using System.Text;
using Crypto.Net.Native;

namespace Crypto.Net.Solana;

/// <summary>SPL Token-2022 extension types (TLV type ids).</summary>
public enum TokenExtensionType : ushort
{
    Uninitialized = 0,
    TransferFeeConfig = 1,
    TransferFeeAmount = 2,
    MintCloseAuthority = 3,
    ConfidentialTransferMint = 4,
    ConfidentialTransferAccount = 5,
    DefaultAccountState = 6,
    ImmutableOwner = 7,
    MemoTransfer = 8,
    NonTransferable = 9,
    InterestBearingConfig = 10,
    CpiGuard = 11,
    PermanentDelegate = 12,
    NonTransferableAccount = 13,
    TransferHook = 14,
    TransferHookAccount = 15,
    ConfidentialTransferFeeConfig = 16,
    ConfidentialTransferFeeAmount = 17,
    MetadataPointer = 18,
    TokenMetadata = 19,
    GroupPointer = 20,
    TokenGroup = 21,
    GroupMemberPointer = 22,
    TokenGroupMember = 23,
    ConfidentialMintBurn = 24,
    ScaledUiAmount = 25,
    Pausable = 26,
    PausableAccount = 27,
}

/// <summary>A raw TLV extension entry.</summary>
public sealed record TokenExtension(TokenExtensionType Type, byte[] Data);

/// <summary>Transfer fee for an epoch range: basis points capped at <see cref="MaximumFee"/>.</summary>
public sealed record TransferFee(ulong Epoch, ulong MaximumFee, ushort BasisPoints)
{
    /// <summary>Fee withheld on a transfer of <paramref name="amount"/> base units (rounded up, as the program does).</summary>
    public ulong Calculate(ulong amount)
    {
        if (BasisPoints == 0 || amount == 0) return 0;
        UInt128 fee = ((UInt128)amount * BasisPoints + 9_999) / 10_000;
        return (ulong)UInt128.Min(fee, MaximumFee);
    }
}

public sealed record TransferFeeConfig(string? ConfigAuthority, string? WithdrawWithheldAuthority, ulong WithheldAmount, TransferFee Older, TransferFee Newer)
{
    /// <summary>The fee schedule in force at <paramref name="epoch"/>.</summary>
    public TransferFee ForEpoch(ulong epoch) => epoch >= Newer.Epoch ? Newer : Older;
}

/// <summary>On-chain token metadata (spl-token-metadata-interface), usually stored in the mint itself.</summary>
public sealed record TokenMetadata(string? UpdateAuthority, string Mint, string Name, string Symbol, string Uri, IReadOnlyList<KeyValuePair<string, string>> AdditionalMetadata);

/// <summary>A decoded SPL Token or Token-2022 mint.</summary>
public sealed record TokenMint(string? MintAuthority, ulong Supply, byte Decimals, bool IsInitialized, string? FreezeAuthority, IReadOnlyList<TokenExtension> Extensions)
{
    public const int BaseSize = 82;

    public TokenExtension? GetExtension(TokenExtensionType type) => Extensions.FirstOrDefault(e => e.Type == type);

    public TransferFeeConfig? TransferFeeConfig => GetExtension(TokenExtensionType.TransferFeeConfig) is { } e ? Token2022.ParseTransferFeeConfig(e.Data) : null;

    public TokenMetadata? Metadata => GetExtension(TokenExtensionType.TokenMetadata) is { } e ? Token2022.ParseTokenMetadata(e.Data) : null;

    /// <summary>(Authority, metadata account) of the metadata pointer, if present.</summary>
    public (string? Authority, string? MetadataAddress)? MetadataPointer =>
        GetExtension(TokenExtensionType.MetadataPointer) is { Data.Length: >= 64 } e ? (Token2022.OptionalKey(e.Data.AsSpan(0, 32)), Token2022.OptionalKey(e.Data.AsSpan(32, 32))) : null;

    public string? PermanentDelegate => GetExtension(TokenExtensionType.PermanentDelegate) is { Data.Length: >= 32 } e ? Token2022.OptionalKey(e.Data.AsSpan(0, 32)) : null;

    public string? MintCloseAuthority => GetExtension(TokenExtensionType.MintCloseAuthority) is { Data.Length: >= 32 } e ? Token2022.OptionalKey(e.Data.AsSpan(0, 32)) : null;

    /// <summary>(Authority, hook program) of the transfer hook, if present.</summary>
    public (string? Authority, string? ProgramId)? TransferHook =>
        GetExtension(TokenExtensionType.TransferHook) is { Data.Length: >= 64 } e ? (Token2022.OptionalKey(e.Data.AsSpan(0, 32)), Token2022.OptionalKey(e.Data.AsSpan(32, 32))) : null;

    /// <summary>Current interest rate in basis points, if the mint is interest-bearing.</summary>
    public short? InterestRateBasisPoints =>
        GetExtension(TokenExtensionType.InterestBearingConfig) is { Data.Length: >= 52 } e ? BinaryPrimitives.ReadInt16LittleEndian(e.Data.AsSpan(50)) : null;

    public bool IsNonTransferable => GetExtension(TokenExtensionType.NonTransferable) is not null;

    /// <summary>Decodes mint account data of either token program.</summary>
    public static TokenMint Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < BaseSize) throw new FormatException("Mint data is shorter than 82 bytes");
        var mint = new TokenMint(
            COption(data[..36]),
            BinaryPrimitives.ReadUInt64LittleEndian(data[36..]),
            data[44],
            data[45] != 0,
            COption(data.Slice(46, 36)),
            Token2022.ParseExtensions(data, BaseSize, accountType: 1));
        return mint;
    }

    private static string? COption(ReadOnlySpan<byte> field) =>
        BinaryPrimitives.ReadUInt32LittleEndian(field) == 1 ? CryptoNative.Base58Encode(field.Slice(4, 32)) : null;
}

/// <summary>A decoded SPL Token or Token-2022 token account.</summary>
public sealed record TokenAccountData(string Mint, string Owner, ulong Amount, string? Delegate, byte State, ulong DelegatedAmount, IReadOnlyList<TokenExtension> Extensions)
{
    public const int BaseSize = 165;

    public bool IsFrozen => State == 2;

    /// <summary>Fees withheld in this account by the transfer-fee extension.</summary>
    public ulong WithheldFees => Extensions.FirstOrDefault(e => e.Type == TokenExtensionType.TransferFeeAmount) is { Data.Length: >= 8 } e ? BinaryPrimitives.ReadUInt64LittleEndian(e.Data) : 0;

    public static TokenAccountData Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < BaseSize) throw new FormatException("Token account data is shorter than 165 bytes");
        string? @delegate = BinaryPrimitives.ReadUInt32LittleEndian(data[72..]) == 1 ? CryptoNative.Base58Encode(data.Slice(76, 32)) : null;
        return new TokenAccountData(
            CryptoNative.Base58Encode(data[..32]),
            CryptoNative.Base58Encode(data.Slice(32, 32)),
            BinaryPrimitives.ReadUInt64LittleEndian(data[64..]),
            @delegate,
            data[108],
            BinaryPrimitives.ReadUInt64LittleEndian(data[121..]),
            Token2022.ParseExtensions(data, BaseSize, accountType: 2));
    }
}

/// <summary>Token-2022 extension instructions and decoders (program <see cref="SolanaAddress.Token2022Program"/>).</summary>
public static class Token2022
{
    public const string ProgramId = SolanaAddress.Token2022Program;

    // Extensions start after the base account size (165): one account-type byte, then TLV entries (u16 type, u16 length).
    internal static IReadOnlyList<TokenExtension> ParseExtensions(ReadOnlySpan<byte> data, int baseSize, byte accountType)
    {
        if (data.Length <= baseSize) return [];
        const int TypeOffset = TokenAccountData.BaseSize;
        if (data.Length <= TypeOffset) throw new FormatException("Extended token data is too short");
        if (baseSize == TokenMint.BaseSize && !data[baseSize..TypeOffset].ToArray().All(b => b == 0))
            throw new FormatException("Mint padding must be zero");
        if (data[TypeOffset] != accountType) throw new FormatException($"Unexpected account type {data[TypeOffset]}");
        var list = new List<TokenExtension>();
        int pos = TypeOffset + 1;
        while (pos + 4 <= data.Length)
        {
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
            ushort length = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 2)..]);
            if (type == 0) break; // uninitialized space at the end of the account
            if (pos + 4 + length > data.Length) throw new FormatException("Extension length exceeds the account data");
            list.Add(new TokenExtension((TokenExtensionType)type, data.Slice(pos + 4, length).ToArray()));
            pos += 4 + length;
        }
        return list;
    }

    internal static string? OptionalKey(ReadOnlySpan<byte> key) => key.IndexOfAnyExcept((byte)0) < 0 ? null : CryptoNative.Base58Encode(key);

    public static TransferFeeConfig ParseTransferFeeConfig(ReadOnlySpan<byte> d)
    {
        if (d.Length < 108) throw new FormatException("TransferFeeConfig is 108 bytes");
        static TransferFee Fee(ReadOnlySpan<byte> s) => new(BinaryPrimitives.ReadUInt64LittleEndian(s), BinaryPrimitives.ReadUInt64LittleEndian(s[8..]), BinaryPrimitives.ReadUInt16LittleEndian(s[16..]));
        return new TransferFeeConfig(OptionalKey(d[..32]), OptionalKey(d.Slice(32, 32)), BinaryPrimitives.ReadUInt64LittleEndian(d[64..]), Fee(d[72..]), Fee(d[90..]));
    }

    public static TokenMetadata ParseTokenMetadata(ReadOnlySpan<byte> d)
    {
        var r = new BorshReader(d);
        string? authority = OptionalKey(r.Bytes(32));
        string mint = r.PublicKey();
        string name = r.String(), symbol = r.String(), uri = r.String();
        int n = r.VecLength();
        var extra = new List<KeyValuePair<string, string>>(n);
        for (int i = 0; i < n; i++) extra.Add(new(r.String(), r.String()));
        return new TokenMetadata(authority, mint, name, symbol, uri, extra);
    }

    /// <summary>Space a mint needs for <paramref name="extensions"/> (fixed-size extensions only; add metadata separately).</summary>
    public static int MintSpace(params TokenExtensionType[] extensions)
    {
        if (extensions.Length == 0) return TokenMint.BaseSize;
        int size = TokenAccountData.BaseSize + 1;
        foreach (var e in extensions) size += 4 + ExtensionSize(e);
        return size;
    }

    private static int ExtensionSize(TokenExtensionType type) => type switch
    {
        TokenExtensionType.TransferFeeConfig => 108,
        TokenExtensionType.TransferFeeAmount => 8,
        TokenExtensionType.MintCloseAuthority => 32,
        TokenExtensionType.DefaultAccountState => 1,
        TokenExtensionType.ImmutableOwner => 0,
        TokenExtensionType.MemoTransfer => 1,
        TokenExtensionType.NonTransferable => 0,
        TokenExtensionType.InterestBearingConfig => 52,
        TokenExtensionType.CpiGuard => 1,
        TokenExtensionType.PermanentDelegate => 32,
        TokenExtensionType.NonTransferableAccount => 0,
        TokenExtensionType.TransferHook => 64,
        TokenExtensionType.TransferHookAccount => 1,
        TokenExtensionType.MetadataPointer => 64,
        TokenExtensionType.GroupPointer => 64,
        TokenExtensionType.GroupMemberPointer => 64,
        TokenExtensionType.Pausable => 33,
        TokenExtensionType.PausableAccount => 0,
        _ => throw new NotSupportedException($"Size of {type} is not fixed or not known"),
    };

    private static byte[] PubkeyOption(string? key) => key is null ? [0] : [1, .. SolanaAddress.Decode(key)];

    private static byte[] OptionalNonZero(string? key) => key is null ? new byte[32] : SolanaAddress.Decode(key);

    /// <summary><c>InitializeMint2</c> (run after the extension initializers).</summary>
    public static SolanaInstruction InitializeMint2(string mint, byte decimals, string mintAuthority, string? freezeAuthority = null, string programId = ProgramId) =>
        new(programId, [AccountMeta.Writable(mint)], [20, decimals, .. SolanaAddress.Decode(mintAuthority), .. PubkeyOption(freezeAuthority)]);

    /// <summary><c>TransferFeeExtension::InitializeTransferFeeConfig</c> (before <see cref="InitializeMint2"/>).</summary>
    public static SolanaInstruction InitializeTransferFeeConfig(string mint, string? configAuthority, string? withdrawWithheldAuthority, ushort basisPoints, ulong maximumFee)
    {
        var w = new BorshWriter().U8(26).U8(0).Bytes(PubkeyOption(configAuthority)).Bytes(PubkeyOption(withdrawWithheldAuthority)).U16(basisPoints).U64(maximumFee);
        return new SolanaInstruction(ProgramId, [AccountMeta.Writable(mint)], w.ToArray());
    }

    /// <summary><c>TransferFeeExtension::TransferCheckedWithFee</c>: <paramref name="fee"/> must equal the mint's fee for the amount.</summary>
    public static SolanaInstruction TransferCheckedWithFee(string source, string mint, string destination, string owner, ulong amount, byte decimals, ulong fee)
    {
        var w = new BorshWriter().U8(26).U8(1).U64(amount).U8(decimals).U64(fee);
        return new SolanaInstruction(ProgramId,
            [AccountMeta.Writable(source), AccountMeta.ReadOnly(mint), AccountMeta.Writable(destination), AccountMeta.ReadOnly(owner, true)], w.ToArray());
    }

    /// <summary><c>MetadataPointerExtension::Initialize</c> (before <see cref="InitializeMint2"/>).</summary>
    public static SolanaInstruction InitializeMetadataPointer(string mint, string? authority, string? metadataAddress) =>
        new(ProgramId, [AccountMeta.Writable(mint)], [39, 0, .. OptionalNonZero(authority), .. OptionalNonZero(metadataAddress)]);

    /// <summary><c>InitializePermanentDelegate</c>.</summary>
    public static SolanaInstruction InitializePermanentDelegate(string mint, string @delegate) =>
        new(ProgramId, [AccountMeta.Writable(mint)], [35, .. SolanaAddress.Decode(@delegate)]);

    /// <summary><c>InitializeNonTransferableMint</c> (soul-bound tokens).</summary>
    public static SolanaInstruction InitializeNonTransferableMint(string mint) => new(ProgramId, [AccountMeta.Writable(mint)], [32]);

    /// <summary>spl-token-metadata-interface <c>Initialize</c>: name, symbol and URI stored in the mint (needs a metadata pointer to itself).</summary>
    public static SolanaInstruction InitializeMetadata(string mint, string updateAuthority, string mintAuthority, string name, string symbol, string uri)
    {
        byte[] discriminator = CryptoNative.Sha256(Encoding.UTF8.GetBytes("spl_token_metadata_interface:initialize_account"))[..8];
        var w = new BorshWriter().Bytes(discriminator).String(name).String(symbol).String(uri);
        return new SolanaInstruction(ProgramId,
            [AccountMeta.Writable(mint), AccountMeta.ReadOnly(updateAuthority), AccountMeta.ReadOnly(mint), AccountMeta.ReadOnly(mintAuthority, true)], w.ToArray());
    }
}
