using System.Collections.Concurrent;
using System.Text.Json;

namespace Crypto.Net.Indexer;

/// <summary>
/// Remembers the last fully processed block per indexer, so a restarted indexer resumes where it stopped.
/// Indexers write the checkpoint only after the handler succeeded (at-least-once delivery): handlers should be
/// idempotent, e.g. keyed by transaction hash and log index.
/// </summary>
public interface ICheckpointStore
{
    /// <summary>The last processed block of <paramref name="indexer"/>, or null when it never ran.</summary>
    ValueTask<ulong?> GetAsync(string indexer, CancellationToken ct = default);

    ValueTask SetAsync(string indexer, ulong block, CancellationToken ct = default);
}

/// <summary>Process-local checkpoints (tests, short-lived tools).</summary>
public sealed class InMemoryCheckpointStore : ICheckpointStore
{
    private readonly ConcurrentDictionary<string, ulong> _values = new(StringComparer.Ordinal);

    public ValueTask<ulong?> GetAsync(string indexer, CancellationToken ct = default) =>
        ValueTask.FromResult(_values.TryGetValue(indexer, out ulong v) ? v : (ulong?)null);

    public ValueTask SetAsync(string indexer, ulong block, CancellationToken ct = default)
    {
        _values[indexer] = block;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Checkpoints in a small JSON file, replaced atomically on every write.</summary>
public sealed class FileCheckpointStore(string path) : ICheckpointStore
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public async ValueTask<ulong?> GetAsync(string indexer, CancellationToken ct = default)
    {
        var all = await ReadAsync(ct).ConfigureAwait(false);
        return all.TryGetValue(indexer, out ulong v) ? v : null;
    }

    public async ValueTask SetAsync(string indexer, ulong block, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await ReadAsync(ct).ConfigureAwait(false);
            all[indexer] = block;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            string temp = Path + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(all), ct).ConfigureAwait(false);
            File.Move(temp, Path, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<Dictionary<string, ulong>> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(Path)) return new(StringComparer.Ordinal);
        string json = await File.ReadAllTextAsync(Path, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<Dictionary<string, ulong>>(json) is { } d
            ? new Dictionary<string, ulong>(d, StringComparer.Ordinal)
            : new(StringComparer.Ordinal);
    }
}
