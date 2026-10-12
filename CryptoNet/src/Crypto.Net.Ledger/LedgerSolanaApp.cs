using System.Text;
using Crypto.Net.Native;
using Crypto.Net.Solana;

namespace Crypto.Net.Ledger;

/// <summary>Version and settings of the Solana app.</summary>
public sealed record LedgerSolanaConfiguration(string Version, bool BlindSigningEnabled, int PublicKeyDisplayMode);

/// <summary>
/// Client for the Ledger Solana app. Paths are fully hardened (<c>44'/501'/account'/change'</c>); the device shows each
/// transaction and message and signs only after the user confirms.
/// </summary>
public sealed class LedgerSolanaApp(ILedgerTransport transport)
{
    private const byte Cla = 0xe0;
    private const byte InsGetVersion = 0x04, InsGetAddress = 0x05, InsSign = 0x06, InsSignOffchain = 0x07;
    private const byte P2Extend = 0x01, P2More = 0x02;
    private const int MaxApduData = 255;

    /// <summary>Phantom / Solflare layout: <c>44'/501'/index'/0'</c>.</summary>
    public static string PhantomPath(uint index = 0) => $"44'/501'/{index}'/0'";

    public async Task<LedgerSolanaConfiguration> GetConfigurationAsync(CancellationToken ct = default)
    {
        byte[] r = await SendAsync(InsGetVersion, 0, [], ct).ConfigureAwait(false);
        if (r.Length < 5) throw new LedgerException(0, "Unexpected configuration response");
        return new LedgerSolanaConfiguration($"{r[2]}.{r[3]}.{r[4]}", r[0] != 0, r[1]);
    }

    /// <summary>The Base58 address at <paramref name="path"/>; with <paramref name="display"/> the device shows it for comparison.</summary>
    public async Task<string> GetAddressAsync(string path, bool display = false, CancellationToken ct = default)
    {
        byte[] r = await SendAsync(InsGetAddress, display ? (byte)1 : (byte)0, SerializePath(path), ct).ConfigureAwait(false);
        if (r.Length != 32) throw new LedgerException(0, "Unexpected public key length");
        return CryptoNative.Base58Encode(r);
    }

    /// <summary>Signs a serialized message (legacy or v0); returns the 64-byte ed25519 signature.</summary>
    public Task<byte[]> SignMessageBytesAsync(string path, ReadOnlyMemory<byte> message, CancellationToken ct = default) =>
        SendAsync(InsSign, 1, [1, .. SerializePath(path), .. message.Span], ct);

    /// <summary>Signs <paramref name="transaction"/> on the device and attaches the signature for the key at <paramref name="path"/>.</summary>
    public async Task<SolanaTransaction> SignTransactionAsync(string path, SolanaTransaction transaction, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        string address = await GetAddressAsync(path, ct: ct).ConfigureAwait(false);
        byte[] signature = await SignMessageBytesAsync(path, transaction.SerializeMessage(), ct).ConfigureAwait(false);
        return transaction.AddSignature(address, signature);
    }

    /// <summary>Signs an already formatted off-chain message (see <see cref="FormatOffchainMessage"/>).</summary>
    public Task<byte[]> SignOffchainMessageAsync(string path, ReadOnlyMemory<byte> formattedMessage, CancellationToken ct = default) =>
        SendAsync(InsSignOffchain, 1, [1, .. SerializePath(path), .. formattedMessage.Span], ct);

    /// <summary>
    /// Off-chain message, version 0: <c>"\xffsolana offchain" ‖ version ‖ format ‖ length (u16 LE) ‖ text</c>. Format 0
    /// is printable ASCII, 1 is UTF-8 (up to 1212 bytes).
    /// </summary>
    public static byte[] FormatOffchainMessage(string text)
    {
        byte[] body = Encoding.UTF8.GetBytes(text);
        if (body.Length is 0 or > 1212) throw new ArgumentException("Off-chain messages must be 1 to 1212 bytes", nameof(text));
        byte format = body.All(b => b is >= 0x20 and <= 0x7e) ? (byte)0 : (byte)1;
        return [0xff, .. "solana offchain"u8, 0, format, (byte)body.Length, (byte)(body.Length >> 8), .. body];
    }

    /// <summary>Paths as the app expects them: every level hardened.</summary>
    public static byte[] SerializePath(string path)
    {
        string trimmed = path.StartsWith("m/", StringComparison.Ordinal) ? path[2..] : path;
        string hardened = string.Join('/', trimmed.Split('/').Select(s => s.EndsWith('\'') || s.EndsWith('h') ? s : s + "'"));
        return LedgerEthereumApp.SerializePath(hardened);
    }

    // Payloads over 255 bytes go in chunks: P2_MORE on every chunk but the last, P2_EXTEND on every chunk but the first.
    private async Task<byte[]> SendAsync(byte ins, byte p1, byte[] payload, CancellationToken ct)
    {
        byte p2 = 0;
        int offset = 0;
        while (payload.Length - offset > MaxApduData)
        {
            await transport.ExchangeAsync(Cla, ins, p1, (byte)(p2 | P2More), payload.AsMemory(offset, MaxApduData), ct).ConfigureAwait(false);
            offset += MaxApduData;
            p2 |= P2Extend;
        }
        return await transport.ExchangeAsync(Cla, ins, p1, p2, payload.AsMemory(offset), ct).ConfigureAwait(false);
    }
}
