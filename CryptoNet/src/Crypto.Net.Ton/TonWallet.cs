using System.Numerics;

namespace Crypto.Net.Ton;

/// <summary>Send mode flags of an outgoing message.</summary>
public static class TonSendMode
{
    public const byte PayGasSeparately = 1;
    public const byte IgnoreErrors = 2;
    public const byte DestroyAccountIfZero = 32;
    public const byte CarryAllRemainingIncomingValue = 64;
    public const byte CarryAllRemainingBalance = 128;

    /// <summary><see cref="PayGasSeparately"/> | <see cref="IgnoreErrors"/>, what wallets use for ordinary transfers.</summary>
    public const byte Default = PayGasSeparately | IgnoreErrors;
}

/// <summary>An internal message a wallet sends: recipient, value in nanotons, bounce flag, and optional body and state init.</summary>
public sealed record TonMessage(TonAddress To, BigInteger Value, bool Bounce = false, TonCell? Body = null, TonCell? StateInit = null)
{
    /// <summary>A transfer with a text comment (op 0), which wallets and explorers show.</summary>
    public static TonMessage WithComment(TonAddress to, BigInteger value, string comment, bool bounce = false) => new(to, value, bounce, Comment(comment));

    /// <summary>Comment body: 32 zero bits then the UTF-8 text as a snake.</summary>
    public static TonCell Comment(string text) => new TonCellBuilder().StoreUInt(0, 32).StoreStringTail(text).EndCell();

    /// <summary>The message as a <c>MessageRelaxed</c> cell (source left empty for the wallet to fill in).</summary>
    public TonCell ToCell()
    {
        var b = new TonCellBuilder()
            .StoreBit(false)            // int_msg_info$0
            .StoreBit(true)             // ihr_disabled
            .StoreBit(Bounce)
            .StoreBit(false)            // bounced
            .StoreAddress(null)         // src: filled in by the wallet
            .StoreAddress(To)
            .StoreCoins(Value)
            .StoreBit(false)            // no extra currencies
            .StoreCoins(0)              // ihr_fee
            .StoreCoins(0)              // fwd_fee
            .StoreUInt(0, 64)           // created_lt
            .StoreUInt(0, 32);          // created_at
        StoreInitAndBody(b, StateInit, Body ?? TonCell.Empty);
        return b.EndCell();
    }

    /// <summary>An inbound external message to <paramref name="destination"/> (what is broadcast to the network).</summary>
    public static TonCell External(TonAddress destination, TonCell body, TonCell? stateInit = null)
    {
        var b = new TonCellBuilder()
            .StoreUInt(2, 2)            // ext_in_msg_info$10
            .StoreUInt(0, 2)            // src: addr_none
            .StoreAddress(destination)
            .StoreCoins(0);             // import_fee
        StoreInitAndBody(b, stateInit, body);
        return b.EndCell();
    }

    /// <summary>
    /// TEP-467 normalized hash of an external message: no state init and the body always in a reference. Explorers and
    /// TON Center index messages by it, and it is the same however the message was serialized.
    /// </summary>
    public static byte[] NormalizedHash(TonCell externalMessage)
    {
        var s = externalMessage.BeginParse();
        if (s.LoadUInt(2) != 2) throw new FormatException("Not an inbound external message");
        if (s.LoadUInt(2) != 0) throw new NotSupportedException("External source addresses are not supported");
        var dest = s.LoadAddress() ?? throw new FormatException("Missing destination");
        s.LoadCoins();
        if (s.LoadBit())
        {
            if (s.LoadBit()) s.LoadRef();
            else SkipStateInit(s);
        }
        TonCell body = s.LoadBit() ? s.LoadRef() : RemainderCell(s);
        return new TonCellBuilder().StoreUInt(2, 2).StoreUInt(0, 2).StoreAddress(dest).StoreCoins(0).StoreBit(false).StoreBit(true).StoreRef(body).EndCell().Hash();
    }

    /// <summary>The <c>StateInit</c> cell for a contract's code and data; its hash is the contract's address.</summary>
    public static TonCell CreateStateInit(TonCell code, TonCell data) =>
        new TonCellBuilder().StoreBit(false).StoreBit(false).StoreMaybeRef(code).StoreMaybeRef(data).StoreBit(false).EndCell();

    // Same placement rule as @ton/core: inline when it fits, otherwise in a reference.
    private static void StoreInitAndBody(TonCellBuilder b, TonCell? init, TonCell body)
    {
        if (init is not null)
        {
            b.StoreBit(true);
            if (b.RemainingBits - 2 < init.BitLength + body.BitLength)
            {
                b.StoreBit(true).StoreRef(init);
            }
            else
            {
                b.StoreBit(false).StoreSlice(init);
            }
        }
        else
        {
            b.StoreBit(false);
        }
        if (b.RemainingBits - 1 < body.BitLength || b.RefCount + body.Refs.Count > TonCell.MaxRefs)
        {
            b.StoreBit(true).StoreRef(body);
        }
        else
        {
            b.StoreBit(false).StoreSlice(body);
        }
    }

    private static void SkipStateInit(TonCellSlice s)
    {
        if (s.LoadBit()) s.LoadUInt(5);                        // split_depth
        if (s.LoadBit()) { s.LoadBit(); s.LoadBit(); }         // special (tick, tock)
        s.LoadMaybeRef();                                       // code
        s.LoadMaybeRef();                                       // data
        s.LoadMaybeRef();                                       // library
    }

    private static TonCell RemainderCell(TonCellSlice s)
    {
        var b = new TonCellBuilder();
        while (s.RemainingBits > 0) b.StoreBit(s.LoadBit());
        while (s.RemainingRefs > 0) b.StoreRef(s.LoadRef());
        return b.EndCell();
    }
}

/// <summary>The standard wallet contracts: their code, initial data and transfer format.</summary>
public static class TonWalletContract
{
    private const string V4R2Code = "te6cckECFAEAAtQAART/APSkE/S88sgLAQIBIAIPAgFIAwYC5tAB0NMDIXGwkl8E4CLXScEgkl8E4ALTHyGCEHBsdWe9IoIQZHN0cr2wkl8F4AP6QDAg+kQByMoHy//J0O1E0IEBQNch9AQwXIEBCPQKb6Exs5JfB+AF0z/IJYIQcGx1Z7qSODDjDQOCEGRzdHK6kl8G4w0EBQB4AfoA9AQw+CdvIjBQCqEhvvLgUIIQcGx1Z4MesXCAGFAEywUmzxZY+gIZ9ADLaRfLH1Jgyz8gyYBA+wAGAIpQBIEBCPRZMO1E0IEBQNcgyAHPFvQAye1UAXKwjiOCEGRzdHKDHrFwgBhQBcsFUAPPFiP6AhPLassfyz/JgED7AJJfA+ICASAHDgIBIAgNAgFYCQoAPbKd+1E0IEBQNch9AQwAsjKB8v/ydABgQEI9ApvoTGACASALDAAZrc52omhAIGuQ64X/wAAZrx32omhAEGuQ64WPwAARuMl+1E0NcLH4AFm9JCtvaiaECAoGuQ+gIYRw1AgIR6STfSmRDOaQPp/5g3gSgBt4EBSJhxWfMYQE+PKDCNcYINMf0x/THwL4I7vyZO1E0NMf0x/T//QE0VFDuvKhUVG68qIF+QFUEGT5EPKj+AAkpMjLH1JAyx9SMMv/UhD0AMntVPgPAdMHIcAAn2xRkyDXSpbTB9QC+wDoMOAhwAHjACHAAuMAAcADkTDjDQOkyMsfEssfy/8QERITAG7SB/oA1NQi+QAFyMoHFcv/ydB3dIAYyMsFywIizxZQBfoCFMtrEszMyXP7AMhAFIEBCPRR8qcCAHCBAQjXGPoA0z/IVCBHgQEI9FHyp4IQbm90ZXB0gBjIywXLAlAGzxZQBPoCFMtqEssfyz/Jc/sAAgBsgQEI1xj6ANM/MFIkgQEI9Fnyp4IQZHN0cnB0gBjIywXLAlAFzxZQA/oCE8tqyx8Syz/Jc/sAAAr0AMntVAj45Sg=";
    private const string V5R1Code = "te6cckECFAEAAoEAART/APSkE/S88sgLAQIBIAINAgFIAwQC3NAg10nBIJFbj2Mg1wsfIIIQZXh0br0hghBzaW50vbCSXwPgghBleHRuuo60gCDXIQHQdNch+kAw+kT4KPpEMFi9kVvg7UTQgQFB1yH0BYMH9A5voTGRMOGAQNchcH/bPOAxINdJgQKAuZEw4HDiEA8CASAFDAIBIAYJAgFuBwgAGa3OdqJoQCDrkOuF/8AAGa8d9qJoQBDrkOuFj8ACAUgKCwAXsyX7UTQcdch1wsfgABGyYvtRNDXCgCAAGb5fD2omhAgKDrkPoCwBAvIOAR4g1wsfghBzaWduuvLgin8PAeaO8O2i7fshgwjXIgKDCNcjIIAg1yHTH9Mf0x/tRNDSANMfINMf0//XCgAK+QFAzPkQmiiUXwrbMeHywIffArNQB7Dy0IRRJbry4IVQNrry4Ib4I7vy0IgikvgA3gGkf8jKAMsfAc8Wye1UIJL4D95w2zzYEAP27aLt+wL0BCFukmwhjkwCIdc5MHCUIccAs44tAdcoIHYeQ2wg10nACPLgkyDXSsAC8uCTINcdBscSwgBSMLDy0InXTNc5MAGk6GwShAe78uCT10rAAPLgk+1V4tIAAcAAkVvg69csCBQgkXCWAdcsCBwS4lIQseMPINdKERITAJYB+kAB+kT4KPpEMFi68uCR7UTQgQFB1xj0BQSdf8jKAEAEgwf0U/Lgi44UA4MH9Fvy4Iwi1woAIW4Bs7Dy0JDiyFADzxYS9ADJ7VQAcjDXLAgkji0h8uCS0gDtRNDSAFETuvLQj1RQMJExnAGBAUDXIdcKAPLgjuLIygBYzxbJ7VST8sCN4gAQk1vbMeHXTNC01sNe";

    private static readonly Lazy<TonCell> V4R2 = new(() => TonCell.FromBocBase64(V4R2Code));
    private static readonly Lazy<TonCell> V5R1 = new(() => TonCell.FromBocBase64(V5R1Code));

    public static TonCell Code(TonWalletVersion version) => version == TonWalletVersion.V4R2 ? V4R2.Value : V5R1.Value;

    /// <summary>
    /// Default wallet id: <c>698983191 + workchain</c> for V4R2; for W5 the network's global id XOR the client context
    /// (workchain, version 0, subwallet 0).
    /// </summary>
    public static uint DefaultWalletId(TonWalletVersion version, TonChain network, int workchain = 0) => version switch
    {
        TonWalletVersion.V4R2 => unchecked((uint)(698983191 + workchain)),
        _ => unchecked((uint)network.GlobalId ^ (0x8000_0000u | ((uint)(workchain & 0xff) << 23))),
    };

    public static TonCell InitialData(TonWalletVersion version, ReadOnlySpan<byte> publicKey, uint walletId) => version switch
    {
        TonWalletVersion.V4R2 => new TonCellBuilder().StoreUInt(0, 32).StoreUInt(walletId, 32).StoreBytes(publicKey).StoreBit(false).EndCell(),
        _ => new TonCellBuilder().StoreBit(true).StoreUInt(0, 32).StoreUInt(walletId, 32).StoreBytes(publicKey).StoreBit(false).EndCell(),
    };

    public static TonCell StateInit(TonWalletVersion version, ReadOnlySpan<byte> publicKey, uint walletId) =>
        TonMessage.CreateStateInit(Code(version), InitialData(version, publicKey, walletId));

    internal static TonCell CreateTransfer(TonAccount account, uint seqno, IReadOnlyList<TonMessage> messages, uint validUntil, byte sendMode)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (account.Version == TonWalletVersion.V4R2)
        {
            if (messages.Count > 4) throw new ArgumentException("A V4R2 transfer carries at most 4 messages", nameof(messages));
            var signing = new TonCellBuilder().StoreUInt(account.WalletId, 32).StoreUInt(validUntil, 32).StoreUInt(seqno, 32).StoreUInt(0, 8);
            foreach (var m in messages) signing.StoreUInt(sendMode, 8).StoreRef(m.ToCell());
            var signingCell = signing.EndCell();
            return new TonCellBuilder().StoreBytes(account.Sign(signingCell.Hash())).StoreSlice(signingCell).EndCell();
        }

        if (messages.Count > 255) throw new ArgumentException("A W5 transfer carries at most 255 messages", nameof(messages));
        TonCell? actions = null;
        if (messages.Count > 0)
        {
            // out_list: each action references the previous list; same nesting order as @ton/ton.
            actions = TonCell.Empty;
            for (int i = messages.Count - 1; i >= 0; i--)
                actions = new TonCellBuilder().StoreRef(actions).StoreUInt(0x0ec3c86d, 32).StoreUInt(sendMode, 8).StoreRef(messages[i].ToCell()).EndCell();
        }
        var v5 = new TonCellBuilder()
            .StoreUInt(0x7369676e, 32)  // "sign": signed external request
            .StoreUInt(account.WalletId, 32)
            .StoreUInt(validUntil, 32)
            .StoreUInt(seqno, 32)
            .StoreMaybeRef(actions)
            .StoreBit(false)            // no extended actions
            .EndCell();
        return new TonCellBuilder().StoreSlice(v5).StoreBytes(account.Sign(v5.Hash())).EndCell();
    }
}

/// <summary>Jetton (TEP-74 fungible token) messages.</summary>
public static class TonJetton
{
    public const uint TransferOp = 0x0f8a7ea5;

    /// <summary>
    /// Body of a <c>transfer</c> message, sent to the sender's own jetton wallet (not the jetton master). Attach enough
    /// TON for fees (typically 0.05) plus <paramref name="forwardTonAmount"/>.
    /// </summary>
    public static TonCell TransferBody(BigInteger amount, TonAddress destination, TonAddress responseDestination, BigInteger? forwardTonAmount = null, string? forwardComment = null, ulong queryId = 0)
    {
        var b = new TonCellBuilder()
            .StoreUInt(TransferOp, 32)
            .StoreUInt(queryId, 64)
            .StoreCoins(amount)
            .StoreAddress(destination)
            .StoreAddress(responseDestination)
            .StoreBit(false)            // no custom payload
            .StoreCoins(forwardTonAmount ?? BigInteger.Zero);
        if (forwardComment is null) b.StoreBit(false);
        else b.StoreBit(true).StoreRef(TonMessage.Comment(forwardComment));
        return b.EndCell();
    }
}
