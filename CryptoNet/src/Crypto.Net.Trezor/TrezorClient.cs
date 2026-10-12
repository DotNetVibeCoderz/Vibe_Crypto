using System.Numerics;
using Crypto.Net.Evm;
using Crypto.Net.Wallet;

namespace Crypto.Net.Trezor;

/// <summary>Model, firmware and state reported by <c>Initialize</c>.</summary>
public sealed record TrezorFeatures(string Vendor, string Version, string? Model, string? Label, bool Initialized, bool PinProtection, bool PassphraseProtection);

/// <summary>
/// Trezor client (Model One, Model T, Safe 3/5) for Ethereum and every EVM chain. Each operation is confirmed on the
/// device. Button requests are acknowledged automatically; supply <see cref="PinProvider"/> for blind PIN entry on
/// Model One and <see cref="Passphrase"/> for hidden wallets.
/// </summary>
public sealed class TrezorClient(ITrezorTransport transport) : IDisposable
{
    private const int MaxDataChunk = 1024;

    /// <summary>
    /// Called with the scrambled-matrix request type on Model One; return the positions the user typed. Newer models
    /// enter the PIN on the device and never ask.
    /// </summary>
    public Func<int, Task<string>>? PinProvider { get; set; }

    /// <summary>Passphrase for a hidden wallet; null (the default) asks the user to enter it on the device.</summary>
    public string? Passphrase { get; set; } = "";

    /// <summary>Raised after each <c>ButtonRequest</c> is acknowledged, while the device waits for the user (or a debug link).</summary>
    public Func<int, Task>? OnButtonRequest { get; set; }

    public async Task<TrezorFeatures> InitializeAsync(CancellationToken ct = default)
    {
        var (type, payload) = await transport.CallAsync(TrezorMessageType.Initialize, [], ct).ConfigureAwait(false);
        Expect(type, payload, TrezorMessageType.Features);
        var f = ProtoMessage.Parse(payload);
        return new TrezorFeatures(
            f.String(1) ?? "",
            $"{f.Varint(2) ?? 0}.{f.Varint(3) ?? 0}.{f.Varint(4) ?? 0}",
            f.String(21) ?? f.String(44),
            f.String(10),
            f.Varint(12) == 1,
            f.Varint(7) == 1,
            f.Varint(8) == 1);
    }

    /// <summary>Default Trezor Suite / MetaMask path: <c>m/44'/60'/0'/0/index</c>.</summary>
    public static string EthereumPath(uint index = 0) => $"m/44'/60'/0'/0/{index}";

    /// <summary>The EIP-55 address at <paramref name="path"/>; with <paramref name="display"/> the device shows it.</summary>
    public async Task<string> GetEthereumAddressAsync(string path, bool display = false, CancellationToken ct = default)
    {
        var w = new ProtoWriter().RepeatedUInt32(1, DerivationPath.Parse(path)).Bool(2, display);
        var reply = await CallAsync(TrezorMessageType.EthereumGetAddress, w.ToArray(), TrezorMessageType.EthereumAddress, ct).ConfigureAwait(false);
        string address = reply.String(2) ?? (reply.Bytes(1) is { } old ? "0x" + System.Convert.ToHexString(old) : throw new FormatException("No address in reply"));
        return EvmAddress.ToChecksumAddress(System.Convert.FromHexString(address[2..]));
    }

    /// <summary>Signs a legacy or EIP-1559 transaction and returns the signed transaction.</summary>
    public async Task<SignedEvmTransaction> SignEthereumTransactionAsync(string path, EvmTransaction tx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        byte[] data = tx.Data;
        var w = new ProtoWriter().RepeatedUInt32(1, DerivationPath.Parse(path));
        ushort type;
        if (tx.Type == EvmTransactionType.DynamicFee)
        {
            if (tx.AccessList.Count > 0) throw new NotSupportedException("Access lists are not supported by this client");
            w.Bytes(2, Be(tx.Nonce)).Bytes(3, Be(tx.MaxFeePerGas)).Bytes(4, Be(tx.MaxPriorityFeePerGas)).Bytes(5, Be(tx.GasLimit));
            if (tx.To is not null) w.String(6, tx.To);
            w.Bytes(7, Be(tx.Value)).Bytes(8, data.AsSpan(0, Math.Min(MaxDataChunk, data.Length))).Varint(9, (ulong)data.Length).Varint(10, tx.ChainId);
            type = TrezorMessageType.EthereumSignTxEip1559;
        }
        else if (tx.Type == EvmTransactionType.Legacy)
        {
            w.Bytes(2, Be(tx.Nonce)).Bytes(3, Be(tx.GasPrice)).Bytes(4, Be(tx.GasLimit));
            if (tx.To is not null) w.String(11, tx.To);
            w.Bytes(6, Be(tx.Value)).Bytes(7, data.AsSpan(0, Math.Min(MaxDataChunk, data.Length))).Varint(8, (ulong)data.Length).Varint(9, tx.ChainId);
            type = TrezorMessageType.EthereumSignTx;
        }
        else
        {
            throw new NotSupportedException($"Trezor signs legacy and EIP-1559 transactions, not {tx.Type}");
        }

        int sent = Math.Min(MaxDataChunk, data.Length);
        var reply = await CallAsync(type, w.ToArray(), TrezorMessageType.EthereumTxRequest, ct).ConfigureAwait(false);
        while (reply.Varint(1) is > 0 and var requested)
        {
            int n = (int)Math.Min(requested, (ulong)(data.Length - sent));
            byte[] chunk = data.AsSpan(sent, n).ToArray();
            sent += n;
            reply = await CallAsync(TrezorMessageType.EthereumTxAck, new ProtoWriter().Bytes(1, chunk).ToArray(), TrezorMessageType.EthereumTxRequest, ct).ConfigureAwait(false);
        }
        ulong v = reply.Varint(2) ?? throw new FormatException("No signature in reply");
        byte[] r = Pad32(reply.Bytes(3)), s = Pad32(reply.Bytes(4));
        byte parity = (byte)(v <= 1 ? v : v is 27 or 28 ? v - 27 : (v - 35) % 2);
        return tx.WithSignature([.. r, .. s], parity);
    }

    /// <summary>EIP-191 personal message; returns the 65-byte <c>r ‖ s ‖ v</c> signature.</summary>
    public async Task<byte[]> SignEthereumMessageAsync(string path, ReadOnlyMemory<byte> message, CancellationToken ct = default)
    {
        var w = new ProtoWriter().RepeatedUInt32(1, DerivationPath.Parse(path)).Bytes(2, message.Span);
        var reply = await CallAsync(TrezorMessageType.EthereumSignMessage, w.ToArray(), TrezorMessageType.EthereumMessageSignature, ct).ConfigureAwait(false);
        return reply.Bytes(2) ?? throw new FormatException("No signature in reply");
    }

    /// <summary>EIP-712 by hashes (domain separator and struct hash), shown on the device as hashes.</summary>
    public async Task<byte[]> SignEthereumTypedHashAsync(string path, byte[] domainSeparatorHash, byte[]? messageHash, CancellationToken ct = default)
    {
        var w = new ProtoWriter().RepeatedUInt32(1, DerivationPath.Parse(path)).Bytes(2, domainSeparatorHash);
        if (messageHash is not null) w.Bytes(3, messageHash);
        var reply = await CallAsync(TrezorMessageType.EthereumSignTypedHash, w.ToArray(), TrezorMessageType.EthereumTypedDataSignature, ct).ConfigureAwait(false);
        return reply.Bytes(1) ?? throw new FormatException("No signature in reply");
    }

    // Sends a request and answers the device's interjections (buttons, PIN, passphrase) until the expected reply arrives.
    private async Task<ProtoMessage> CallAsync(ushort type, byte[] payload, ushort expected, CancellationToken ct)
    {
        var (t, p) = await transport.CallAsync(type, payload, ct).ConfigureAwait(false);
        for (int guard = 0; guard < 64; guard++)
        {
            switch (t)
            {
                case TrezorMessageType.ButtonRequest:
                {
                    int code = (int)(ProtoMessage.Parse(p).Varint(1) ?? 0);
                    var pending = transport.CallAsync(TrezorMessageType.ButtonAck, [], ct);
                    if (OnButtonRequest is not null) await OnButtonRequest(code).ConfigureAwait(false);
                    (t, p) = await pending.ConfigureAwait(false);
                    continue;
                }
                case TrezorMessageType.PinMatrixRequest:
                {
                    if (PinProvider is null) throw new TrezorException(0, "The device asks for a PIN; set PinProvider");
                    string pin = await PinProvider((int)(ProtoMessage.Parse(p).Varint(1) ?? 0)).ConfigureAwait(false);
                    (t, p) = await transport.CallAsync(TrezorMessageType.PinMatrixAck, new ProtoWriter().String(1, pin).ToArray(), ct).ConfigureAwait(false);
                    continue;
                }
                case TrezorMessageType.PassphraseRequest:
                {
                    var ack = Passphrase is null ? new ProtoWriter().Bool(3, true) : new ProtoWriter().String(1, Passphrase);
                    (t, p) = await transport.CallAsync(TrezorMessageType.PassphraseAck, ack.ToArray(), ct).ConfigureAwait(false);
                    continue;
                }
            }
            Expect(t, p, expected);
            return ProtoMessage.Parse(p);
        }
        throw new TrezorException(0, "Too many device prompts");
    }

    private static void Expect(ushort type, byte[] payload, ushort expected)
    {
        if (type == TrezorMessageType.Failure)
        {
            var f = ProtoMessage.Parse(payload);
            throw new TrezorException((int)(f.Varint(1) ?? 0), f.String(2) ?? "Failure");
        }
        if (type != expected) throw new TrezorException(0, $"Unexpected reply type {type} (expected {expected})");
    }

    private static byte[] Be(BigInteger value)
    {
        if (value.Sign < 0) throw new ArgumentOutOfRangeException(nameof(value));
        return value.IsZero ? [] : value.ToByteArray(isUnsigned: true, isBigEndian: true);
    }

    private static byte[] Pad32(byte[]? b)
    {
        if (b is null || b.Length > 32) throw new FormatException("Invalid signature component");
        byte[] o = new byte[32];
        b.CopyTo(o, 32 - b.Length);
        return o;
    }

    public void Dispose() => transport.Dispose();
}
