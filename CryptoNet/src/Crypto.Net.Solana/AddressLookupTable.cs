using System.Buffers.Binary;
using Crypto.Net.Native;

namespace Crypto.Net.Solana;

/// <summary>An on-chain address lookup table: its key and the addresses it stores.</summary>
public sealed record AddressLookupTableAccount(string Key, IReadOnlyList<string> Addresses)
{
    /// <summary>Size of the lookup table metadata that precedes the address list.</summary>
    public const int MetaSize = 56;

    public ulong DeactivationSlot { get; init; } = ulong.MaxValue;
    public ulong LastExtendedSlot { get; init; }
    public byte LastExtendedSlotStartIndex { get; init; }
    public string? Authority { get; init; }

    /// <summary>True until the table is deactivated (<see cref="DeactivationSlot"/> is <c>u64::MAX</c> while active).</summary>
    public bool IsActive => DeactivationSlot == ulong.MaxValue;

    /// <summary>Decodes the account data of an address lookup table.</summary>
    public static AddressLookupTableAccount Parse(string key, ReadOnlySpan<byte> data)
    {
        if (data.Length < MetaSize || (data.Length - MetaSize) % 32 != 0) throw new FormatException("Invalid address lookup table account size");
        if (BinaryPrimitives.ReadUInt32LittleEndian(data) != 1) throw new FormatException("Account is not an initialized address lookup table");
        string? authority = data[21] == 1 ? CryptoNative.Base58Encode(data.Slice(22, 32)) : null;
        var addresses = new List<string>((data.Length - MetaSize) / 32);
        for (int pos = MetaSize; pos < data.Length; pos += 32) addresses.Add(CryptoNative.Base58Encode(data.Slice(pos, 32)));
        return new AddressLookupTableAccount(key, addresses)
        {
            DeactivationSlot = BinaryPrimitives.ReadUInt64LittleEndian(data[4..]),
            LastExtendedSlot = BinaryPrimitives.ReadUInt64LittleEndian(data[12..]),
            LastExtendedSlotStartIndex = data[20],
            Authority = authority,
        };
    }
}

/// <summary>Accounts a v0 message loads from one lookup table, by index.</summary>
public sealed record MessageAddressTableLookup(string TableKey, IReadOnlyList<byte> WritableIndexes, IReadOnlyList<byte> ReadonlyIndexes);

/// <summary>Instruction builders for the native Address Lookup Table program.</summary>
public static class AddressLookupTableProgram
{
    public const string ProgramId = "AddressLookupTab1e1111111111111111111111111";

    /// <summary>Lookup table address derived from the authority and a recent slot.</summary>
    public static (string Address, byte Bump) DeriveAddress(string authority, ulong recentSlot)
    {
        byte[] slot = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(slot, recentSlot);
        return SolanaAddress.FindProgramAddress([SolanaAddress.Decode(authority), slot], ProgramId);
    }

    /// <summary>Creates a lookup table. <paramref name="recentSlot"/> must be a recent finalized slot.</summary>
    public static (SolanaInstruction Instruction, string TableAddress) Create(string authority, string payer, ulong recentSlot)
    {
        var (table, bump) = DeriveAddress(authority, recentSlot);
        byte[] data = new byte[13];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(4), recentSlot);
        data[12] = bump;
        return (new SolanaInstruction(ProgramId,
        [
            AccountMeta.Writable(table),
            AccountMeta.ReadOnly(authority, true),
            AccountMeta.Writable(payer, true),
            AccountMeta.ReadOnly(SolanaAddress.SystemProgram),
        ], data), table);
    }

    /// <summary>Appends addresses to a table (about 20–30 per transaction fit).</summary>
    public static SolanaInstruction Extend(string table, string authority, IReadOnlyList<string> addresses, string? payer = null)
    {
        byte[] data = new byte[12 + 32 * addresses.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 2);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(4), (ulong)addresses.Count);
        for (int i = 0; i < addresses.Count; i++) SolanaAddress.Decode(addresses[i]).CopyTo(data, 12 + 32 * i);
        var accounts = new List<AccountMeta> { AccountMeta.Writable(table), AccountMeta.ReadOnly(authority, true) };
        if (payer is not null)
        {
            accounts.Add(AccountMeta.Writable(payer, true));
            accounts.Add(AccountMeta.ReadOnly(SolanaAddress.SystemProgram));
        }
        return new SolanaInstruction(ProgramId, accounts, data);
    }

    public static SolanaInstruction Freeze(string table, string authority) => Simple(1, table, authority);

    public static SolanaInstruction Deactivate(string table, string authority) => Simple(3, table, authority);

    /// <summary>Closes a deactivated table and sends its rent to <paramref name="recipient"/>.</summary>
    public static SolanaInstruction Close(string table, string authority, string recipient) =>
        new(ProgramId, [AccountMeta.Writable(table), AccountMeta.ReadOnly(authority, true), AccountMeta.Writable(recipient)], [4, 0, 0, 0]);

    private static SolanaInstruction Simple(byte discriminator, string table, string authority) =>
        new(ProgramId, [AccountMeta.Writable(table), AccountMeta.ReadOnly(authority, true)], [discriminator, 0, 0, 0]);
}

/// <summary>A compiled instruction: program and account indexes into the message's account keys.</summary>
public sealed record CompiledInstruction(byte ProgramIdIndex, IReadOnlyList<byte> AccountIndexes, byte[] Data);

/// <summary>A decoded legacy or v0 message, as it appears on the wire.</summary>
public sealed record SolanaMessage(
    SolanaMessageVersion Version,
    byte RequiredSignatures,
    byte ReadonlySignedAccounts,
    byte ReadonlyUnsignedAccounts,
    IReadOnlyList<string> StaticAccountKeys,
    string RecentBlockhash,
    IReadOnlyList<CompiledInstruction> Instructions,
    IReadOnlyList<MessageAddressTableLookup> AddressTableLookups)
{
    /// <summary>Decodes a serialized message (the bytes signers sign).</summary>
    public static SolanaMessage Parse(ReadOnlySpan<byte> data)
    {
        var r = new Reader(data);
        var version = SolanaMessageVersion.Legacy;
        byte first = r.Byte();
        if ((first & 0x80) != 0)
        {
            if ((first & 0x7F) != 0) throw new FormatException($"Unsupported message version {first & 0x7F}");
            version = SolanaMessageVersion.V0;
            first = r.Byte();
        }
        byte readonlySigned = r.Byte(), readonlyUnsigned = r.Byte();
        var keys = new List<string>();
        int keyCount = r.CompactU16();
        for (int i = 0; i < keyCount; i++) keys.Add(CryptoNative.Base58Encode(r.Bytes(32)));
        if (first > keyCount || readonlySigned > first || readonlyUnsigned > keyCount - first) throw new FormatException("Invalid message header");
        string blockhash = CryptoNative.Base58Encode(r.Bytes(32));
        var instructions = new List<CompiledInstruction>();
        int ixCount = r.CompactU16();
        for (int i = 0; i < ixCount; i++)
        {
            byte program = r.Byte();
            byte[] accounts = r.Bytes(r.CompactU16()).ToArray();
            byte[] ixData = r.Bytes(r.CompactU16()).ToArray();
            instructions.Add(new CompiledInstruction(program, accounts, ixData));
        }
        var lookups = new List<MessageAddressTableLookup>();
        if (version == SolanaMessageVersion.V0)
        {
            int lookupCount = r.CompactU16();
            for (int i = 0; i < lookupCount; i++)
            {
                string table = CryptoNative.Base58Encode(r.Bytes(32));
                byte[] w = r.Bytes(r.CompactU16()).ToArray();
                byte[] ro = r.Bytes(r.CompactU16()).ToArray();
                lookups.Add(new MessageAddressTableLookup(table, w, ro));
            }
        }
        if (r.Remaining != 0) throw new FormatException("Trailing bytes after message");
        return new SolanaMessage(version, first, readonlySigned, readonlyUnsigned, keys, blockhash, instructions, lookups);
    }

    /// <summary>Decodes a signed wire transaction into its signatures and message.</summary>
    public static (IReadOnlyList<byte[]> Signatures, SolanaMessage Message) ParseTransaction(ReadOnlySpan<byte> data)
    {
        var r = new Reader(data);
        int count = r.CompactU16();
        var signatures = new List<byte[]>();
        for (int i = 0; i < count; i++) signatures.Add(r.Bytes(64).ToArray());
        var message = Parse(data[(data.Length - r.Remaining)..]);
        if (message.RequiredSignatures != count) throw new FormatException("Signature count does not match the message header");
        return (signatures, message);
    }

    /// <summary>
    /// All account keys in index order: static keys, then writable and read-only keys resolved from
    /// <paramref name="tables"/>.
    /// </summary>
    public IReadOnlyList<string> ResolveAccountKeys(IEnumerable<AddressLookupTableAccount>? tables = null)
    {
        if (AddressTableLookups.Count == 0) return StaticAccountKeys;
        var byKey = (tables ?? []).ToDictionary(t => t.Key, StringComparer.Ordinal);
        var writable = new List<string>();
        var readOnly = new List<string>();
        foreach (var l in AddressTableLookups)
        {
            if (!byKey.TryGetValue(l.TableKey, out var t)) throw new InvalidOperationException($"Lookup table {l.TableKey} was not provided");
            writable.AddRange(l.WritableIndexes.Select(i => t.Addresses[i]));
            readOnly.AddRange(l.ReadonlyIndexes.Select(i => t.Addresses[i]));
        }
        return [.. StaticAccountKeys, .. writable, .. readOnly];
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _pos;

        public readonly int Remaining => _data.Length - _pos;

        public ReadOnlySpan<byte> Bytes(int n)
        {
            if (n < 0 || _pos + n > _data.Length) throw new FormatException("Message data is truncated");
            var s = _data.Slice(_pos, n);
            _pos += n;
            return s;
        }

        public byte Byte() => Bytes(1)[0];

        public int CompactU16()
        {
            int value = 0;
            for (int shift = 0; shift < 21; shift += 7)
            {
                byte b = Byte();
                value |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    if (value > 0xFFFF) throw new FormatException("compact-u16 overflow");
                    return value;
                }
            }
            throw new FormatException("compact-u16 too long");
        }
    }
}
