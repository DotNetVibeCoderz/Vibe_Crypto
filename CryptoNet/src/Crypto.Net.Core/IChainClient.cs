namespace Crypto.Net.Core;

/// <summary>Outcome of a transaction once it has been included in a block.</summary>
public sealed record TxReceipt(
    TxHash Hash,
    ulong BlockNumber,
    bool IsSuccess,
    ulong GasUsed = 0,
    string? ErrorMessage = null
);

/// <summary>Minimal header information for block notifications.</summary>
public sealed record BlockInfo(ulong Number, string? Hash = null, DateTimeOffset? Timestamp = null);

/// <summary>Common operations every chain client supports.</summary>
public interface IChainClient : IAsyncDisposable, IDisposable
{
    IChain Chain { get; }

    /// <summary>Native-asset balance of <paramref name="address"/>.</summary>
    ValueTask<Amount> GetBalanceAsync(Address address, CancellationToken ct = default);

    /// <summary>Broadcasts a fully signed transaction.</summary>
    ValueTask<TxHash> SendRawTransactionAsync(ReadOnlyMemory<byte> signedTxBytes, CancellationToken ct = default);

    /// <summary>Returns the receipt, or <c>null</c> while the transaction is pending or unknown.</summary>
    ValueTask<TxReceipt?> GetReceiptAsync(TxHash hash, CancellationToken ct = default);

    /// <summary>Latest block height (slot for Solana).</summary>
    ValueTask<ulong> GetBlockNumberAsync(CancellationToken ct = default);
}

/// <summary>Polling helpers available for every <see cref="IChainClient"/>.</summary>
public static class ChainClientExtensions
{
    /// <summary>Polls until a receipt is available or <paramref name="timeout"/> elapses (default 5 minutes).</summary>
    public static async ValueTask<TxReceipt> WaitForReceiptAsync(
        this IChainClient client, TxHash hash, TimeSpan? pollInterval = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var interval = pollInterval ?? TimeSpan.FromSeconds(2);
        var limit = timeout ?? TimeSpan.FromMinutes(5);
        using var timeoutCts = new CancellationTokenSource(limit);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            while (true)
            {
                var receipt = await client.GetReceiptAsync(hash, linked.Token).ConfigureAwait(false);
                if (receipt is not null) return receipt;
                await Task.Delay(interval, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"No receipt for {hash} within {limit}");
        }
    }

    /// <summary>Yields each new block height as it appears (HTTP polling, works with any RPC endpoint).</summary>
    public static async IAsyncEnumerable<BlockInfo> WatchBlocksAsync(
        this IChainClient client, TimeSpan? pollInterval = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var interval = pollInterval ?? TimeSpan.FromSeconds(3);
        ulong last = await client.GetBlockNumberAsync(ct).ConfigureAwait(false);
        yield return new BlockInfo(last);
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(interval, ct).ConfigureAwait(false);
            ulong current = await client.GetBlockNumberAsync(ct).ConfigureAwait(false);
            for (ulong n = last + 1; n <= current; n++)
                yield return new BlockInfo(n);
            last = Math.Max(last, current);
        }
    }
}
