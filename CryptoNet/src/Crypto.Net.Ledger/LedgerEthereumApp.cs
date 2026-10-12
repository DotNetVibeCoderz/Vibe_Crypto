using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Crypto.Net.Core;
using Crypto.Net.Evm;
using Crypto.Net.Wallet;

namespace Crypto.Net.Ledger;

/// <summary>Version and flags of the Ethereum app.</summary>
public sealed record LedgerEthereumConfiguration(string Version, bool ArbitraryDataEnabled, bool Erc20ProvisioningNecessary, bool StarkEnabled);

/// <summary>An address read from the device with its uncompressed public key.</summary>
public sealed record LedgerEthereumAddress(string Address, byte[] PublicKey, byte[]? ChainCode);

/// <summary>
/// Client for the Ledger Ethereum app (also used for every EVM chain). The device shows each transaction and message
/// and signs only after the user confirms.
/// </summary>
public sealed class LedgerEthereumApp(ILedgerTransport transport)
{
    private const byte Cla = 0xe0;
    private const int MaxApduData = 255;

    /// <summary>Default BIP-44 path for <paramref name="index"/> (Ledger Live layout: <c>m/44'/60'/index'/0/0</c>).</summary>
    public static string LedgerLivePath(uint index = 0) => $"m/44'/60'/{index}'/0/0";

    public async Task<LedgerEthereumConfiguration> GetConfigurationAsync(CancellationToken ct = default)
    {
        byte[] r = await transport.ExchangeAsync(Cla, 0x06, 0, 0, Array.Empty<byte>(), ct).ConfigureAwait(false);
        if (r.Length < 4) throw new LedgerException(0, "Unexpected configuration response");
        return new LedgerEthereumConfiguration($"{r[1]}.{r[2]}.{r[3]}", (r[0] & 0x01) != 0, (r[0] & 0x02) != 0, (r[0] & 0x04) != 0);
    }

    /// <summary>
    /// Reads the address at <paramref name="path"/>. With <paramref name="display"/> the device shows it for the user to
    /// compare, the only way to be sure an address belongs to the device. <paramref name="chainId"/> picks the network
    /// name shown on screen.
    /// </summary>
    public async Task<LedgerEthereumAddress> GetAddressAsync(string path, bool display = false, ulong? chainId = null, bool chainCode = false, CancellationToken ct = default)
    {
        byte[] data = SerializePath(path);
        if (chainId is { } id)
        {
            var c = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(c, id);
            data = [.. data, .. c];
        }
        byte[] r = await transport.ExchangeAsync(Cla, 0x02, display ? (byte)1 : (byte)0, chainCode ? (byte)1 : (byte)0, data, ct).ConfigureAwait(false);
        int keyLength = r[0];
        byte[] publicKey = r[1..(1 + keyLength)];
        int addressLength = r[1 + keyLength];
        string address = "0x" + Encoding.ASCII.GetString(r, 2 + keyLength, addressLength);
        byte[]? code = chainCode ? r[(2 + keyLength + addressLength)..(2 + keyLength + addressLength + 32)] : null;
        return new LedgerEthereumAddress(EvmAddress.ToChecksumAddress(Convert.FromHexString(address[2..])), publicKey, code);
    }

    /// <summary>Raw (v, r, s) as returned by the device for <paramref name="unsignedRlp"/> (the EIP-2718 signing payload).</summary>
    public async Task<(byte V, byte[] R, byte[] S)> SignRawTransactionAsync(string path, byte[] unsignedRlp, CancellationToken ct = default)
    {
        byte[] pathBytes = SerializePath(path);
        int endMarker = LegacyEip155MarkerOffset(unsignedRlp);
        byte[] response = [];
        int offset = 0;
        while (offset < unsignedRlp.Length)
        {
            bool first = offset == 0;
            int max = first ? MaxApduData - pathBytes.Length : MaxApduData;
            int size = Math.Min(max, unsignedRlp.Length - offset);
            // The app treats a chunk ending exactly inside the EIP-155 (chainId, 0, 0) tail as malformed: send the rest.
            if (endMarker > 0 && offset + size >= endMarker && unsignedRlp.Length - offset <= max) size = unsignedRlp.Length - offset;
            byte[] chunk = first ? [.. pathBytes, .. unsignedRlp.AsSpan(offset, size)] : unsignedRlp[offset..(offset + size)];
            response = await transport.ExchangeAsync(Cla, 0x04, first ? (byte)0x00 : (byte)0x80, 0x00, chunk, ct).ConfigureAwait(false);
            offset += size;
        }
        if (response.Length < 65) throw new LedgerException(0, "Unexpected signature response");
        return (response[0], response[1..33], response[33..65]);
    }

    /// <summary>Has the device sign <paramref name="tx"/>; returns the signed transaction ready to broadcast.</summary>
    public async Task<SignedEvmTransaction> SignTransactionAsync(string path, EvmTransaction tx, CancellationToken ct = default)
    {
        var (v, r, s) = await SignRawTransactionAsync(path, tx.GetSigningPayload(), ct).ConfigureAwait(false);
        return tx.WithSignature([.. r, .. s], RecoveryId(tx, v));
    }

    /// <summary>EIP-191 <c>personal_sign</c> of <paramref name="message"/>; returns the 65-byte r‖s‖v signature (v = 27/28).</summary>
    public async Task<byte[]> SignPersonalMessageAsync(string path, ReadOnlyMemory<byte> message, CancellationToken ct = default)
    {
        byte[] pathBytes = SerializePath(path);
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)message.Length);
        byte[] payload = [.. length, .. message.Span];
        byte[] response = [];
        int offset = 0;
        do
        {
            bool first = offset == 0;
            int max = first ? MaxApduData - pathBytes.Length : MaxApduData;
            int size = Math.Min(max, payload.Length - offset);
            byte[] chunk = first ? [.. pathBytes, .. payload.AsSpan(0, size)] : payload[offset..(offset + size)];
            response = await transport.ExchangeAsync(Cla, 0x08, first ? (byte)0x00 : (byte)0x80, 0x00, chunk, ct).ConfigureAwait(false);
            offset += size;
        }
        while (offset < payload.Length);
        return VrsToSignature(response);
    }

    public Task<byte[]> SignPersonalMessageAsync(string path, string message, CancellationToken ct = default) =>
        SignPersonalMessageAsync(path, Encoding.UTF8.GetBytes(message), ct);

    /// <summary>
    /// EIP-712 signature from the domain separator and struct hash (as computed by <see cref="EvmMessageSigner"/>).
    /// Returns the 65-byte r‖s‖v signature.
    /// </summary>
    public async Task<byte[]> SignEip712HashedMessageAsync(string path, byte[] domainSeparator, byte[] messageHash, CancellationToken ct = default)
    {
        if (domainSeparator.Length != 32 || messageHash.Length != 32) throw new ArgumentException("Both hashes must be 32 bytes");
        byte[] data = [.. SerializePath(path), .. domainSeparator, .. messageHash];
        return VrsToSignature(await transport.ExchangeAsync(Cla, 0x0c, 0, 0, data, ct).ConfigureAwait(false));
    }

    /// <summary>BIP-32 path as the device expects: count byte, then big-endian 32-bit indices.</summary>
    public static byte[] SerializePath(string path)
    {
        string normalized = path.StartsWith("m/", StringComparison.Ordinal) ? path : "m/" + path;
        uint[] indices = DerivationPath.Parse(normalized);
        if (indices.Length is 0 or > 10) throw new ArgumentException("A path needs 1 to 10 levels", nameof(path));
        var bytes = new byte[1 + 4 * indices.Length];
        bytes[0] = (byte)indices.Length;
        for (int i = 0; i < indices.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(1 + 4 * i), indices[i]);
        return bytes;
    }

    private static byte[] VrsToSignature(byte[] r)
    {
        if (r.Length < 65) throw new LedgerException(0, "Unexpected signature response");
        return [.. r.AsSpan(1, 64), r[0]];
    }

    // The device returns v as one byte: y-parity for typed transactions, (35 + 2·chainId + parity) mod 256 for legacy ones.
    private static byte RecoveryId(EvmTransaction tx, byte v)
    {
        if (tx.Type != EvmTransactionType.Legacy) return (byte)(v & 1);
        if (tx.ChainId == 0) return (byte)(v is 27 or 28 ? v - 27 : (v - 37) & 1);
        int expected = (int)((35 + 2 * (BigInteger)tx.ChainId) % 256);
        int parity = (v - expected + 256) % 256;
        if (parity > 1) throw new LedgerException(0, $"Unexpected v {v} for chain {tx.ChainId}");
        return (byte)parity;
    }

    // Offset of the RLP-encoded (chainId, 0, 0) tail of a legacy EIP-155 payload, or 0.
    private static int LegacyEip155MarkerOffset(byte[] rlp)
    {
        if (rlp.Length == 0 || rlp[0] < 0xc0) return 0;
        var items = EvmRlp.Decode(rlp).AsList();
        if (items.Count != 9) return 0;
        int tail = items.Skip(6).Sum(i => EvmRlp.EncodeBytes(i.AsBytes()).Length);
        return rlp.Length - tail;
    }
}
