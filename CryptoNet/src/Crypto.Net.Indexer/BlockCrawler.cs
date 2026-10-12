using Crypto.Net.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Crypto.Net.Indexer;

public sealed class BlockCrawlerOptions
{
    /// <summary>Checkpoint key.</summary>
    public string Name { get; set; } = "blocks";

    /// <summary>First block when there is no checkpoint (default: the current safe head).</summary>
    public ulong? StartBlock { get; set; }

    /// <summary>Blocks behind the head treated as final (0 for chains with instant finality).</summary>
    public ulong Confirmations { get; set; }

    /// <summary>Upper bound of blocks handled per poll, so a long backlog still checkpoints regularly.</summary>
    public ulong MaxBlocksPerPoll { get; set; } = 500;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(6);
}

/// <summary>
/// Chain-agnostic crawler: calls a handler for every block height (or Solana slot) in order, from the checkpoint to
/// the confirmed head, and checkpoints after each block. The handler fetches whatever it needs for that height using
/// the chain-specific client (EVM receipts, Substrate events, Solana blocks, Bitcoin transactions…).
/// </summary>
public sealed class BlockCrawler
{
    private readonly IChainClient _client;
    private readonly ICheckpointStore _checkpoints;
    private readonly Func<ulong, CancellationToken, Task> _handleBlock;
    private readonly BlockCrawlerOptions _options;
    private readonly ILogger _logger;

    public BlockCrawler(IChainClient client, ICheckpointStore checkpoints, Func<ulong, CancellationToken, Task> handleBlock, BlockCrawlerOptions options, ILogger<BlockCrawler>? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
        _handleBlock = handleBlock ?? throw new ArgumentNullException(nameof(handleBlock));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<BlockCrawler>.Instance;
    }

    public BlockCrawlerOptions Options => _options;

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ulong? processed = null;
            try
            {
                processed = await RunOnceAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is RpcException or HttpRequestException && !ct.IsCancellationRequested)
            {
                _logger.LogWarning("{Crawler}: {Message}; retrying after {Delay}", _options.Name, ex.Message, _options.PollInterval);
            }
            if (processed is null) await Task.Delay(_options.PollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Handles up to <see cref="BlockCrawlerOptions.MaxBlocksPerPoll"/> new blocks; returns the last one or null.</summary>
    public async Task<ulong?> RunOnceAsync(CancellationToken ct = default)
    {
        ulong head = await _client.GetBlockNumberAsync(ct).ConfigureAwait(false);
        if (head < _options.Confirmations) return null;
        ulong safe = head - _options.Confirmations;
        ulong? checkpoint = await _checkpoints.GetAsync(_options.Name, ct).ConfigureAwait(false);
        ulong from = checkpoint is { } c ? c + 1 : _options.StartBlock ?? safe;
        if (from > safe) return null;
        ulong to = Math.Min(safe, from + Math.Max(1, _options.MaxBlocksPerPoll) - 1);
        for (ulong n = from; n <= to; n++)
        {
            ct.ThrowIfCancellationRequested();
            await _handleBlock(n, ct).ConfigureAwait(false);
            await _checkpoints.SetAsync(_options.Name, n, ct).ConfigureAwait(false);
        }
        return to;
    }
}
