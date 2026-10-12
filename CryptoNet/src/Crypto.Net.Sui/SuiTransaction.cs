using Crypto.Net.Core;
using Crypto.Net.Native;

namespace Crypto.Net.Sui;

/// <summary>A reference to a specific version of an owned object: id, version and Base58 digest.</summary>
public sealed record SuiObjectRef(string ObjectId, ulong Version, string Digest)
{
    internal void Write(BcsWriter w)
    {
        byte[] digest = CryptoNative.Base58Decode(Digest);
        if (digest.Length != 32) throw new FormatException("Object digests are 32 bytes");
        w.Fixed(SuiAddress.Parse(ObjectId)).U64(Version).Bytes(digest);
    }

    internal static SuiObjectRef Read(ref BcsReader r) =>
        new(SuiAddress.Format(r.Fixed(32)), r.U64(), CryptoNative.Base58Encode(r.Bytes()));
}

public enum SuiArgumentKind : byte { GasCoin = 0, Input = 1, Result = 2, NestedResult = 3 }

/// <summary>An argument of a programmable transaction command: the gas coin, an input, or a command result.</summary>
public readonly record struct SuiArgument(SuiArgumentKind Kind, ushort Index = 0, ushort SubIndex = 0)
{
    public static SuiArgument GasCoin => new(SuiArgumentKind.GasCoin);

    /// <summary>The <paramref name="i"/>-th value returned by this command result (for commands that return several values).</summary>
    public SuiArgument this[int i] => Kind == SuiArgumentKind.Result
        ? new(SuiArgumentKind.NestedResult, Index, checked((ushort)i))
        : throw new InvalidOperationException("Only a command result can be indexed");

    internal void Write(BcsWriter w)
    {
        w.Variant((byte)Kind);
        if (Kind == SuiArgumentKind.GasCoin) return;
        w.U16(Index);
        if (Kind == SuiArgumentKind.NestedResult) w.U16(SubIndex);
    }

    internal static SuiArgument Read(ref BcsReader r) => r.Uleb128() switch
    {
        0 => GasCoin,
        1 => new(SuiArgumentKind.Input, r.U16()),
        2 => new(SuiArgumentKind.Result, r.U16()),
        3 => new(SuiArgumentKind.NestedResult, r.U16(), r.U16()),
        var k => throw new FormatException($"Unknown argument kind {k}"),
    };
}

/// <summary>An input of a programmable transaction.</summary>
public abstract record SuiCallArg
{
    internal abstract void Write(BcsWriter w);

    /// <summary>A BCS-encoded pure value (see <see cref="MoveArg"/>).</summary>
    public sealed record Pure(byte[] Bcs) : SuiCallArg
    {
        internal override void Write(BcsWriter w) => w.Variant(0).Bytes(Bcs);
    }

    public sealed record OwnedObject(SuiObjectRef Ref) : SuiCallArg
    {
        internal override void Write(BcsWriter w)
        {
            w.Variant(1).Variant(0);
            Ref.Write(w);
        }
    }

    public sealed record SharedObject(string ObjectId, ulong InitialSharedVersion, bool Mutable) : SuiCallArg
    {
        internal override void Write(BcsWriter w) => w.Variant(1).Variant(1).Fixed(SuiAddress.Parse(ObjectId)).U64(InitialSharedVersion).Bool(Mutable);
    }

    public sealed record Receiving(SuiObjectRef Ref) : SuiCallArg
    {
        internal override void Write(BcsWriter w)
        {
            w.Variant(1).Variant(2);
            Ref.Write(w);
        }
    }

    internal static SuiCallArg Read(ref BcsReader r)
    {
        ulong kind = r.Uleb128();
        if (kind == 0) return new Pure(r.Bytes());
        if (kind != 1) throw new FormatException($"Unsupported call argument kind {kind}");
        return r.Uleb128() switch
        {
            0 => new OwnedObject(SuiObjectRef.Read(ref r)),
            1 => new SharedObject(SuiAddress.Format(r.Fixed(32)), r.U64(), r.Bool()),
            2 => new Receiving(SuiObjectRef.Read(ref r)),
            var k => throw new FormatException($"Unknown object argument kind {k}"),
        };
    }
}

/// <summary>A command of a programmable transaction.</summary>
public abstract record SuiCommand
{
    internal abstract void Write(BcsWriter w);

    public sealed record MoveCall(string Package, string Module, string Function, IReadOnlyList<MoveTypeTag> TypeArgs, IReadOnlyList<SuiArgument> Args) : SuiCommand
    {
        internal override void Write(BcsWriter w)
        {
            w.Variant(0).Fixed(SuiAddress.Parse(Package)).String(Module).String(Function);
            w.Vector(TypeArgs, (bw, t) => t.Write(bw));
            w.Vector(Args, (bw, a) => a.Write(bw));
        }
    }

    public sealed record TransferObjects(IReadOnlyList<SuiArgument> Objects, SuiArgument Address) : SuiCommand
    {
        internal override void Write(BcsWriter w)
        {
            w.Variant(1).Vector(Objects, (bw, a) => a.Write(bw));
            Address.Write(w);
        }
    }

    public sealed record SplitCoins(SuiArgument Coin, IReadOnlyList<SuiArgument> Amounts) : SuiCommand
    {
        internal override void Write(BcsWriter w)
        {
            w.Variant(2);
            Coin.Write(w);
            w.Vector(Amounts, (bw, a) => a.Write(bw));
        }
    }

    public sealed record MergeCoins(SuiArgument Destination, IReadOnlyList<SuiArgument> Sources) : SuiCommand
    {
        internal override void Write(BcsWriter w)
        {
            w.Variant(3);
            Destination.Write(w);
            w.Vector(Sources, (bw, a) => a.Write(bw));
        }
    }

    public sealed record Publish(IReadOnlyList<byte[]> Modules, IReadOnlyList<string> Dependencies) : SuiCommand
    {
        internal override void Write(BcsWriter w) =>
            w.Variant(4).Vector(Modules, (bw, m) => bw.Bytes(m)).Vector(Dependencies, (bw, d) => bw.Fixed(SuiAddress.Parse(d)));
    }

    public sealed record MakeMoveVec(MoveTypeTag? ElementType, IReadOnlyList<SuiArgument> Elements) : SuiCommand
    {
        internal override void Write(BcsWriter w)
        {
            w.Variant(5).Option(ElementType, (bw, t) => t.Write(bw));
            w.Vector(Elements, (bw, a) => a.Write(bw));
        }
    }

    public sealed record Upgrade(IReadOnlyList<byte[]> Modules, IReadOnlyList<string> Dependencies, string Package, SuiArgument Ticket) : SuiCommand
    {
        internal override void Write(BcsWriter w)
        {
            w.Variant(6).Vector(Modules, (bw, m) => bw.Bytes(m)).Vector(Dependencies, (bw, d) => bw.Fixed(SuiAddress.Parse(d))).Fixed(SuiAddress.Parse(Package));
            Ticket.Write(w);
        }
    }

    internal static SuiCommand Read(ref BcsReader r)
    {
        ulong kind = r.Uleb128();
        switch (kind)
        {
            case 0:
            {
                string package = SuiAddress.Format(r.Fixed(32));
                string module = r.String();
                string function = r.String();
                var typeArgs = ReadList(ref r, static (ref BcsReader x) => MoveTypeTag.Read(ref x));
                return new MoveCall(package, module, function, typeArgs, ReadArgs(ref r));
            }
            case 1:
            {
                var objects = ReadArgs(ref r);
                return new TransferObjects(objects, SuiArgument.Read(ref r));
            }
            case 2:
            {
                var coin = SuiArgument.Read(ref r);
                return new SplitCoins(coin, ReadArgs(ref r));
            }
            case 3:
            {
                var destination = SuiArgument.Read(ref r);
                return new MergeCoins(destination, ReadArgs(ref r));
            }
            case 4:
            {
                var modules = ReadList(ref r, static (ref BcsReader x) => x.Bytes());
                return new Publish(modules, ReadList(ref r, static (ref BcsReader x) => SuiAddress.Format(x.Fixed(32))));
            }
            case 5:
            {
                MoveTypeTag? type = r.U8() switch { 0 => null, 1 => MoveTypeTag.Read(ref r), _ => throw new FormatException("Invalid option tag") };
                return new MakeMoveVec(type, ReadArgs(ref r));
            }
            case 6:
            {
                var modules = ReadList(ref r, static (ref BcsReader x) => x.Bytes());
                var deps = ReadList(ref r, static (ref BcsReader x) => SuiAddress.Format(x.Fixed(32)));
                string package = SuiAddress.Format(r.Fixed(32));
                return new Upgrade(modules, deps, package, SuiArgument.Read(ref r));
            }
            default:
                throw new FormatException($"Unknown command kind {kind}");
        }
    }

    internal delegate T ItemReader<T>(ref BcsReader r);

    internal static List<T> ReadList<T>(ref BcsReader r, ItemReader<T> read)
    {
        int n = r.Length();
        var list = new List<T>(n);
        for (int i = 0; i < n; i++) list.Add(read(ref r));
        return list;
    }

    private static List<SuiArgument> ReadArgs(ref BcsReader r) => ReadList(ref r, static (ref BcsReader x) => SuiArgument.Read(ref x));
}

/// <summary>Sui <c>TransactionData</c> (V1) holding a programmable transaction.</summary>
public sealed class SuiTransactionData
{
    private static readonly byte[] DigestPrefix = "TransactionData::"u8.ToArray();

    public required string Sender { get; init; }
    public List<SuiCallArg> Inputs { get; init; } = [];
    public List<SuiCommand> Commands { get; init; } = [];
    public List<SuiObjectRef> GasPayment { get; init; } = [];

    /// <summary>Who pays for gas; the sender unless the transaction is sponsored.</summary>
    public string? GasOwner { get; init; }
    public required ulong GasPrice { get; init; }
    public required ulong GasBudget { get; init; }

    /// <summary>Epoch after which the transaction expires, or null for none.</summary>
    public ulong? ExpirationEpoch { get; init; }

    public byte[] Serialize()
    {
        var w = new BcsWriter().Variant(0).Variant(0);
        w.Vector(Inputs, (bw, i) => i.Write(bw));
        w.Vector(Commands, (bw, c) => c.Write(bw));
        w.Fixed(SuiAddress.Parse(Sender));
        w.Vector(GasPayment, (bw, g) => g.Write(bw));
        w.Fixed(SuiAddress.Parse(GasOwner ?? Sender)).U64(GasPrice).U64(GasBudget);
        if (ExpirationEpoch is { } epoch) w.Variant(1).U64(epoch);
        else w.Variant(0);
        return w.ToArray();
    }

    /// <summary>Parses transaction data BCS (V1 programmable transactions).</summary>
    public static SuiTransactionData Parse(ReadOnlySpan<byte> bcs)
    {
        var r = new BcsReader(bcs);
        var data = Read(ref r);
        if (r.Remaining != 0) throw new FormatException("Trailing bytes after transaction data");
        return data;
    }

    internal static SuiTransactionData Read(ref BcsReader r)
    {
        if (r.Uleb128() != 0) throw new FormatException("Only TransactionData V1 is supported");
        if (r.Uleb128() != 0) throw new FormatException("Only programmable transactions are supported");
        var inputs = SuiCommand.ReadList(ref r, static (ref BcsReader x) => SuiCallArg.Read(ref x));
        var commands = SuiCommand.ReadList(ref r, static (ref BcsReader x) => SuiCommand.Read(ref x));
        string sender = SuiAddress.Format(r.Fixed(32));
        var payment = SuiCommand.ReadList(ref r, static (ref BcsReader x) => SuiObjectRef.Read(ref x));
        string owner = SuiAddress.Format(r.Fixed(32));
        ulong price = r.U64();
        ulong budget = r.U64();
        ulong? epoch = r.Uleb128() switch { 0 => null, 1 => r.U64(), var k => throw new FormatException($"Unsupported expiration kind {k}") };
        return new SuiTransactionData
        {
            Sender = sender, Inputs = inputs, Commands = commands, GasPayment = payment,
            GasOwner = owner == sender ? null : owner, GasPrice = price, GasBudget = budget, ExpirationEpoch = epoch,
        };
    }

    /// <summary>The transaction digest (Base58): <c>blake2b-256("TransactionData::" ‖ BCS)</c>.</summary>
    public string Digest => DigestOf(Serialize());

    public static string DigestOf(ReadOnlySpan<byte> transactionDataBcs) =>
        CryptoNative.Base58Encode(CryptoNative.Blake2b256([.. DigestPrefix, .. transactionDataBcs]));

    /// <summary>Signs with <paramref name="account"/> and returns the BCS <c>SenderSignedData</c> to submit.</summary>
    public byte[] Sign(SuiAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        byte[] bcs = Serialize();
        return SuiSignedTransaction.Encode(bcs, [account.SignTransaction(bcs)]);
    }
}

/// <summary>
/// BCS <c>SenderSignedData</c>: the transaction data with its intent and the serialized signatures (the sender's, plus
/// the sponsor's for sponsored transactions). This is the byte form <see cref="IChainClient.SendRawTransactionAsync"/> takes.
/// </summary>
public static class SuiSignedTransaction
{
    public static byte[] Encode(ReadOnlySpan<byte> transactionData, IReadOnlyList<byte[]> signatures)
    {
        var w = new BcsWriter().Uleb128(1).U8(SuiIntent.TransactionData).U8(0).U8(0).Fixed(transactionData);
        return w.Vector(signatures, (bw, s) => bw.Bytes(s)).ToArray();
    }

    /// <summary>Splits signed data into the transaction data BCS and its serialized signatures.</summary>
    public static (byte[] TransactionData, IReadOnlyList<byte[]> Signatures) Decode(ReadOnlySpan<byte> signed)
    {
        var r = new BcsReader(signed);
        if (r.Uleb128() != 1) throw new FormatException("Expected one signed transaction");
        if (r.U8() != SuiIntent.TransactionData || r.U8() != 0 || r.U8() != 0) throw new FormatException("Not a transaction-data intent");
        int start = r.Position;
        SuiTransactionData.Read(ref r);
        byte[] data = signed[start..r.Position].ToArray();
        var sigs = SuiCommand.ReadList(ref r, static (ref BcsReader x) => x.Bytes());
        if (r.Remaining != 0) throw new FormatException("Trailing bytes after the signatures");
        return (data, sigs);
    }
}

/// <summary>Builds programmable transactions command by command, in the style of the TypeScript SDK.</summary>
public sealed class SuiTransactionBuilder(string sender)
{
    private readonly List<SuiCallArg> _inputs = [];
    private readonly List<SuiCommand> _commands = [];
    private readonly Dictionary<string, ushort> _objectInputs = new(StringComparer.Ordinal);

    public string Sender { get; } = SuiAddress.Normalize(sender);

    /// <summary>The coin that pays for gas; split it to send SUI.</summary>
    public SuiArgument Gas => SuiArgument.GasCoin;

    public SuiArgument Pure(byte[] bcs) => AddInput(new SuiCallArg.Pure(bcs));
    public SuiArgument PureU64(ulong value) => Pure(MoveArg.U64(value));
    public SuiArgument PureAddress(string address) => Pure(SuiAddress.Parse(address));
    public SuiArgument PureBool(bool value) => Pure(MoveArg.Bool(value));
    public SuiArgument PureString(string value) => Pure(MoveArg.String(value));

    /// <summary>An owned or immutable object at a specific version (the same object is added once).</summary>
    public SuiArgument Object(SuiObjectRef objectRef) => ObjectInput(objectRef.ObjectId, () => new SuiCallArg.OwnedObject(objectRef));

    /// <summary>A shared object (such as the clock <c>0x6</c>), by id and the version at which it became shared.</summary>
    public SuiArgument SharedObject(string objectId, ulong initialSharedVersion, bool mutable) =>
        ObjectInput(objectId, () => new SuiCallArg.SharedObject(SuiAddress.Normalize(objectId), initialSharedVersion, mutable));

    public SuiArgument Receiving(SuiObjectRef objectRef) => ObjectInput(objectRef.ObjectId, () => new SuiCallArg.Receiving(objectRef));

    /// <summary>Splits <paramref name="coin"/>; index the result (<c>result[0]</c>, …) for each new coin.</summary>
    public SuiArgument SplitCoins(SuiArgument coin, params SuiArgument[] amounts) => AddCommand(new SuiCommand.SplitCoins(coin, amounts));

    public SuiArgument MergeCoins(SuiArgument destination, params SuiArgument[] sources) => AddCommand(new SuiCommand.MergeCoins(destination, sources));

    public SuiArgument TransferObjects(IReadOnlyList<SuiArgument> objects, SuiArgument recipient) => AddCommand(new SuiCommand.TransferObjects(objects, recipient));

    /// <summary>Calls <c>package::module::function</c>.</summary>
    public SuiArgument MoveCall(string target, IReadOnlyList<MoveTypeTag>? typeArgs = null, params SuiArgument[] args)
    {
        string[] parts = target.Split("::");
        if (parts.Length != 3) throw new FormatException($"Expected package::module::function, got '{target}'");
        return AddCommand(new SuiCommand.MoveCall(SuiAddress.Normalize(parts[0]), parts[1], parts[2], typeArgs ?? [], args));
    }

    public SuiArgument MakeMoveVec(MoveTypeTag? elementType, params SuiArgument[] elements) => AddCommand(new SuiCommand.MakeMoveVec(elementType, elements));

    /// <summary>Splits <paramref name="mist"/> off the gas coin and sends it to <paramref name="recipient"/>.</summary>
    public SuiTransactionBuilder TransferSui(string recipient, ulong mist)
    {
        var coin = SplitCoins(Gas, PureU64(mist));
        TransferObjects([coin[0]], PureAddress(recipient));
        return this;
    }

    public SuiTransactionData Build(IReadOnlyList<SuiObjectRef> gasPayment, ulong gasPrice, ulong gasBudget, string? gasOwner = null, ulong? expirationEpoch = null) => new()
    {
        Sender = Sender,
        Inputs = [.. _inputs],
        Commands = [.. _commands],
        GasPayment = [.. gasPayment],
        GasOwner = gasOwner is null ? null : SuiAddress.Normalize(gasOwner),
        GasPrice = gasPrice,
        GasBudget = gasBudget,
        ExpirationEpoch = expirationEpoch,
    };

    private SuiArgument AddInput(SuiCallArg arg)
    {
        _inputs.Add(arg);
        return new SuiArgument(SuiArgumentKind.Input, checked((ushort)(_inputs.Count - 1)));
    }

    private SuiArgument ObjectInput(string objectId, Func<SuiCallArg> create)
    {
        string id = SuiAddress.Normalize(objectId);
        if (_objectInputs.TryGetValue(id, out ushort existing)) return new SuiArgument(SuiArgumentKind.Input, existing);
        var arg = AddInput(create());
        _objectInputs[id] = arg.Index;
        return arg;
    }

    private SuiArgument AddCommand(SuiCommand command)
    {
        _commands.Add(command);
        return new SuiArgument(SuiArgumentKind.Result, checked((ushort)(_commands.Count - 1)));
    }
}
