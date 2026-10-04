using System.Buffers.Binary;
using System.Text;
using Crypto.Net.Core;
using Crypto.Net.Native;

namespace Crypto.Net.Solana;

/// <summary>An account referenced by an instruction.</summary>
public sealed record AccountMeta(string PublicKey, bool IsSigner, bool IsWritable)
{
    public static AccountMeta Writable(string key, bool signer = false) => new(key, signer, true);
    public static AccountMeta ReadOnly(string key, bool signer = false) => new(key, signer, false);
}

/// <summary>A program invocation.</summary>
public sealed record SolanaInstruction(string ProgramId, IReadOnlyList<AccountMeta> Accounts, byte[] Data);

/// <summary>
/// Builds, signs and serializes legacy Solana transactions. Accounts are de-duplicated and ordered
/// (writable signers, read-only signers, writable, read-only) with the fee payer first.
/// </summary>
public sealed class SolanaTransaction
{
    public SolanaTransaction(string feePayer, string recentBlockhash)
    {
        FeePayer = feePayer;
        RecentBlockhash = recentBlockhash;
    }

    public string FeePayer { get; }
    public string RecentBlockhash { get; set; }
    public List<SolanaInstruction> Instructions { get; } = [];
    private readonly Dictionary<string, byte[]> _signatures = new(StringComparer.Ordinal);

    public SolanaTransaction Add(SolanaInstruction instruction)
    {
        Instructions.Add(instruction);
        return this;
    }

    /// <summary>Accounts in message order, with signer/writable flags merged.</summary>
    public IReadOnlyList<AccountMeta> CompileAccounts()
    {
        var map = new Dictionary<string, (bool Signer, bool Writable, int Order)>(StringComparer.Ordinal);
        int order = 0;
        void Merge(string key, bool signer, bool writable)
        {
            map[key] = map.TryGetValue(key, out var e) ? (e.Signer || signer, e.Writable || writable, e.Order) : (signer, writable, order++);
        }

        Merge(FeePayer, true, true);
        foreach (var ix in Instructions)
        {
            foreach (var a in ix.Accounts) Merge(a.PublicKey, a.IsSigner, a.IsWritable);
            Merge(ix.ProgramId, false, false);
        }

        return map
            .OrderBy(kv => kv.Key == FeePayer ? 0 : 1)
            .ThenBy(kv => (kv.Value.Signer, kv.Value.Writable) switch { (true, true) => 0, (true, false) => 1, (false, true) => 2, _ => 3 })
            .ThenBy(kv => kv.Value.Order)
            .Select(kv => new AccountMeta(kv.Key, kv.Value.Signer, kv.Value.Writable))
            .ToList();
    }

    /// <summary>Serialized message: the bytes every signer signs.</summary>
    public byte[] SerializeMessage()
    {
        if (Instructions.Count == 0) throw new InvalidOperationException("Transaction has no instructions");
        var accounts = CompileAccounts();
        var index = accounts.Select((a, i) => (a.PublicKey, i)).ToDictionary(x => x.PublicKey, x => (byte)x.i, StringComparer.Ordinal);

        using var ms = new MemoryStream();
        ms.WriteByte((byte)accounts.Count(a => a.IsSigner));
        ms.WriteByte((byte)accounts.Count(a => a.IsSigner && !a.IsWritable));
        ms.WriteByte((byte)accounts.Count(a => !a.IsSigner && !a.IsWritable));
        WriteCompactU16(ms, accounts.Count);
        foreach (var a in accounts) ms.Write(SolanaAddress.Decode(a.PublicKey));
        ms.Write(SolanaAddress.Decode(RecentBlockhash));
        WriteCompactU16(ms, Instructions.Count);
        foreach (var ix in Instructions)
        {
            ms.WriteByte(index[ix.ProgramId]);
            WriteCompactU16(ms, ix.Accounts.Count);
            foreach (var a in ix.Accounts) ms.WriteByte(index[a.PublicKey]);
            WriteCompactU16(ms, ix.Data.Length);
            ms.Write(ix.Data);
        }
        return ms.ToArray();
    }

    /// <summary>Signs with raw 32-byte ed25519 seeds (Solana secret keys' first half).</summary>
    public SolanaTransaction Sign(params byte[][] privateKeys)
    {
        byte[] message = SerializeMessage();
        foreach (var key in privateKeys)
        {
            string pub = CryptoNative.Base58Encode(CryptoNative.Ed25519GetPublicKey(key));
            _signatures[pub] = CryptoNative.Ed25519Sign(key, message);
        }
        return this;
    }

    public async ValueTask<SolanaTransaction> SignAsync(IEnumerable<ISigner> signers, CancellationToken ct = default)
    {
        byte[] message = SerializeMessage();
        foreach (var s in signers)
        {
            if (s.Scheme != SignatureScheme.Ed25519) throw new ArgumentException("Solana requires ed25519 signers");
            _signatures[s.Address.Value] = (await s.SignAsync(message, ct).ConfigureAwait(false)).Bytes.ToArray();
        }
        return this;
    }

    /// <summary>Wire format: compact-array of signatures followed by the message.</summary>
    public byte[] Serialize()
    {
        byte[] message = SerializeMessage();
        var signers = CompileAccounts().Where(a => a.IsSigner).ToList();
        using var ms = new MemoryStream();
        WriteCompactU16(ms, signers.Count);
        foreach (var s in signers)
        {
            if (!_signatures.TryGetValue(s.PublicKey, out var sig)) throw new InvalidOperationException($"Missing signature for {s.PublicKey}");
            if (!CryptoNative.Ed25519Verify(SolanaAddress.Decode(s.PublicKey), message, sig)) throw new InvalidOperationException($"Invalid signature for {s.PublicKey}");
            ms.Write(sig);
        }
        ms.Write(message);
        return ms.ToArray();
    }

    /// <summary>The transaction id: Base58 of the fee payer's signature.</summary>
    public string Signature => _signatures.TryGetValue(FeePayer, out var sig)
        ? CryptoNative.Base58Encode(sig)
        : throw new InvalidOperationException("Transaction is not signed by the fee payer");

    public string ToBase64() => Convert.ToBase64String(Serialize());

    internal static void WriteCompactU16(Stream s, int value)
    {
        if (value is < 0 or > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(value));
        while (value >= 0x80)
        {
            s.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        s.WriteByte((byte)value);
    }
}

/// <summary>Instruction builders for the native System, Compute Budget and Memo programs.</summary>
public static class SystemProgram
{
    /// <summary>Transfers lamports between two system accounts.</summary>
    public static SolanaInstruction Transfer(string from, string to, ulong lamports)
    {
        byte[] data = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 2);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(4), lamports);
        return new SolanaInstruction(SolanaAddress.SystemProgram, [AccountMeta.Writable(from, true), AccountMeta.Writable(to)], data);
    }

    /// <summary>Creates a new account owned by <paramref name="owner"/>.</summary>
    public static SolanaInstruction CreateAccount(string from, string newAccount, ulong lamports, ulong space, string owner)
    {
        byte[] data = new byte[52];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(4), lamports);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(12), space);
        SolanaAddress.Decode(owner).CopyTo(data, 20);
        return new SolanaInstruction(SolanaAddress.SystemProgram, [AccountMeta.Writable(from, true), AccountMeta.Writable(newAccount, true)], data);
    }
}

public static class ComputeBudgetProgram
{
    public static SolanaInstruction SetComputeUnitLimit(uint units)
    {
        byte[] data = new byte[5];
        data[0] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(1), units);
        return new SolanaInstruction(SolanaAddress.ComputeBudgetProgram, [], data);
    }

    /// <summary>Priority fee in micro-lamports per compute unit.</summary>
    public static SolanaInstruction SetComputeUnitPrice(ulong microLamports)
    {
        byte[] data = new byte[9];
        data[0] = 3;
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(1), microLamports);
        return new SolanaInstruction(SolanaAddress.ComputeBudgetProgram, [], data);
    }
}

public static class MemoProgram
{
    public static SolanaInstruction Memo(string text, params string[] signers) =>
        new(SolanaAddress.MemoProgram, signers.Select(s => AccountMeta.ReadOnly(s, true)).ToList(), Encoding.UTF8.GetBytes(text));
}

/// <summary>SPL Token and Associated Token Account instructions.</summary>
public static class TokenProgram
{
    /// <summary><c>TransferChecked</c>: validates mint and decimals on-chain.</summary>
    public static SolanaInstruction TransferChecked(string sourceAta, string mint, string destinationAta, string owner, ulong amount, byte decimals, string tokenProgram = SolanaAddress.TokenProgram)
    {
        byte[] data = new byte[10];
        data[0] = 12;
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(1), amount);
        data[9] = decimals;
        return new SolanaInstruction(tokenProgram,
            [AccountMeta.Writable(sourceAta), AccountMeta.ReadOnly(mint), AccountMeta.Writable(destinationAta), AccountMeta.ReadOnly(owner, true)], data);
    }

    /// <summary>Creates the associated token account if it does not exist (idempotent).</summary>
    public static SolanaInstruction CreateAssociatedTokenAccountIdempotent(string payer, string owner, string mint, string tokenProgram = SolanaAddress.TokenProgram)
    {
        string ata = SolanaAddress.GetAssociatedTokenAddress(owner, mint, tokenProgram);
        return new SolanaInstruction(SolanaAddress.AssociatedTokenProgram,
        [
            AccountMeta.Writable(payer, true),
            AccountMeta.Writable(ata),
            AccountMeta.ReadOnly(owner),
            AccountMeta.ReadOnly(mint),
            AccountMeta.ReadOnly(SolanaAddress.SystemProgram),
            AccountMeta.ReadOnly(tokenProgram),
        ], [1]);
    }
}
