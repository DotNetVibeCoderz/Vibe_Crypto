using Crypto.Net.Core;
using Crypto.Net.Evm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Crypto.Net.Indexer;

/// <summary>What an <see cref="EvmLogIndexer"/> watches and how it pages through history.</summary>
public sealed class EvmLogIndexerOptions
{
    /// <summary>Checkpoint key; use a distinct name per indexer.</summary>
    public string Name { get; set; } = "evm-logs";

    /// <summary>Contract addresses to watch (empty = every contract).</summary>
    public List<string> Addresses { get; set; } = [];

    /// <summary>Events to decode. Their topic-0 values also filter the logs unless <see cref="Topics"/> is set.</summary>
    public List<EvmEvent> Events { get; set; } = [];

    /// <summary>
    /// Raw topic filter, one entry per position (null = any, several values = OR). Overrides the topic-0 filter derived
    /// from <see cref="Events"/>.
    /// </summary>
    public List<IReadOnlyList<string>?>? Topics { get; set; }

    /// <summary>First block to index when there is no checkpoint (default: the current safe head).</summary>
    public ulong? StartBlock { get; set; }

    /// <summary>Blocks behind the head that are considered final; protects against reorgs (default 12).</summary>
    public ulong Confirmations { get; set; } = 12;

    /// <summary>Largest block range per <c>eth_getLogs</c> call; halved automatically when a provider refuses it.</summary>
    public ulong MaxBlockRange { get; set; } = 2_000;

    /// <summary>Delay between polls once the indexer has caught up.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(12);
}

/// <summary>A processed block range with its raw logs and the logs that decoded as one of the configured events.</summary>
public sealed record EvmLogBatch(ulong FromBlock, ulong ToBlock, IReadOnlyList<EvmLog> Logs, IReadOnlyList<EvmDecodedEvent> Events);

/// <summary>Receives log batches in block order. A batch is redelivered if the handler throws.</summary>
public interface IEvmLogHandler
{
    Task HandleAsync(EvmLogBatch batch, CancellationToken ct);
}

/// <summary>Adapts a delegate to <see cref="IEvmLogHandler"/>.</summary>
public sealed class DelegateLogHandler(Func<EvmLogBatch, CancellationToken, Task> handle) : IEvmLogHandler
{
    public Task HandleAsync(EvmLogBatch batch, CancellationToken ct) => handle(batch, ct);
}

/// <summary>
/// Pages through <c>eth_getLogs</c> from the checkpoint to the confirmed head, hands each range to an
/// <see cref="IEvmLogHandler"/>, then advances the checkpoint. It adapts the range size when a provider limits
/// results ("query returned more than 10000 results", "block range too large", …) and keeps following new blocks.
/// </summary>
public sealed class EvmLogIndexer
{
    private readonly EvmRpcClient _client;
    private readonly ICheckpointStore _checkpoints;
    private readonly IEvmLogHandler _handler;
    private readonly EvmLogIndexerOptions _options;
    private readonly ILogger _logger;
    private ulong _range;

    public EvmLogIndexer(EvmRpcClient client, ICheckpointStore checkpoints, IEvmLogHandler handler, EvmLogIndexerOptions options, ILogger<EvmLogIndexer>? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (options.MaxBlockRange == 0) throw new ArgumentException("MaxBlockRange must be positive", nameof(options));
        _logger = logger ?? NullLogger<EvmLogIndexer>.Instance;
        _range = options.MaxBlockRange;
    }

    public EvmLogIndexerOptions Options => _options;

    /// <summary>Indexes until cancelled, polling for new blocks once caught up.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ulong? processed = null;
            try
            {
                processed = await RunOnceAsync(ct).ConfigureAwait(false);
            }
            catch (RpcException ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("{Indexer}: RPC error {Message}; retrying after {Delay}", _options.Name, ex.RpcMessage, _options.PollInterval);
            }
            catch (HttpRequestException ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("{Indexer}: network error {Message}; retrying after {Delay}", _options.Name, ex.Message, _options.PollInterval);
            }
            if (processed is null) await Task.Delay(_options.PollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Processes every confirmed block after the checkpoint. Returns the last block processed, or null when there was
    /// nothing new.
    /// </summary>
    public async Task<ulong?> RunOnceAsync(CancellationToken ct = default)
    {
        ulong head = await _client.GetBlockNumberAsync(ct).ConfigureAwait(false);
        if (head < _options.Confirmations) return null;
        ulong safe = head - _options.Confirmations;

        ulong? checkpoint = await _checkpoints.GetAsync(_options.Name, ct).ConfigureAwait(false);
        ulong from = checkpoint is { } c ? c + 1 : _options.StartBlock ?? safe;
        if (from > safe) return null;

        ulong last = from - 1;
        while (from <= safe)
        {
            ct.ThrowIfCancellationRequested();
            ulong to = Math.Min(safe, from + _range - 1);
            IReadOnlyList<EvmLog> logs;
            try
            {
                logs = await _client.GetLogsAsync(Filter(from, to), ct).ConfigureAwait(false);
            }
            catch (RpcException ex) when (IsRangeTooLarge(ex) && to > from)
            {
                _range = Math.Max(1, (to - from + 1) / 2);
                _logger.LogDebug("{Indexer}: provider refused {Count} blocks ({Message}); trying {Range}", _options.Name, to - from + 1, ex.RpcMessage, _range);
                continue;
            }

            var ordered = logs.OrderBy(l => l.BlockNumber).ThenBy(l => l.LogIndex).ToList();
            var decoded = new List<EvmDecodedEvent>();
            foreach (var log in ordered)
                foreach (var e in _options.Events)
                    if (e.TryDecode(log, out var d))
                    {
                        decoded.Add(d!);
                        break;
                    }

            await _handler.HandleAsync(new EvmLogBatch(from, to, ordered, decoded), ct).ConfigureAwait(false);
            await _checkpoints.SetAsync(_options.Name, to, ct).ConfigureAwait(false);
            _logger.LogDebug("{Indexer}: blocks {From}-{To}, {Logs} logs", _options.Name, from, to, ordered.Count);

            last = to;
            from = to + 1;
            if (_range < _options.MaxBlockRange) _range = Math.Min(_options.MaxBlockRange, _range * 2);
        }
        return last;
    }

    private EvmLogFilter Filter(ulong from, ulong to)
    {
        IReadOnlyList<IReadOnlyList<string>?> topics = _options.Topics ?? [];
        if (_options.Topics is null && _options.Events.Count > 0 && _options.Events.All(e => !e.Anonymous))
            topics = [_options.Events.Select(e => e.Topic0).Distinct().ToList()];
        return new EvmLogFilter(from, to) { Addresses = _options.Addresses, Topics = topics };
    }

    /// <summary>Errors providers use when a log query spans too many blocks or returns too many results.</summary>
    public static bool IsRangeTooLarge(RpcException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        string m = ex.RpcMessage;
        return ex.Code is -32005 or -32602 && (m.Contains("range", StringComparison.OrdinalIgnoreCase) || m.Contains("results", StringComparison.OrdinalIgnoreCase) || m.Contains("limit", StringComparison.OrdinalIgnoreCase))
            || m.Contains("block range", StringComparison.OrdinalIgnoreCase)
            || m.Contains("more than", StringComparison.OrdinalIgnoreCase)
            || m.Contains("too many", StringComparison.OrdinalIgnoreCase)
            || m.Contains("response size", StringComparison.OrdinalIgnoreCase)
            || m.Contains("exceed", StringComparison.OrdinalIgnoreCase);
    }
}
