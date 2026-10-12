using System.Runtime.CompilerServices;
using System.Text.Json;
using Crypto.Net.Core;

namespace Crypto.Net.Solana;

/// <summary>A slot notification: the slot being processed, its parent and the current root.</summary>
public sealed record SolanaSlotUpdate(ulong Slot, ulong Parent, ulong Root);

/// <summary>An account change: lamports, owner and raw data at <see cref="Slot"/>.</summary>
public sealed record SolanaAccountUpdate(ulong Slot, ulong Lamports, string Owner, byte[] Data, bool Executable);

/// <summary>Program logs of a transaction (<c>Error</c> is null on success).</summary>
public sealed record SolanaLogsUpdate(ulong Slot, string Signature, string? Error, IReadOnlyList<string> Logs);

/// <summary>Solana PubSub streams over a <see cref="WebSocketRpcClient"/> (use the cluster's <c>wss://</c> endpoint).</summary>
public static class SolanaSubscriptions
{
    public static async IAsyncEnumerable<SolanaSlotUpdate> SubscribeSolanaSlotsAsync(this WebSocketRpcClient ws, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var s in ws.SubscribeAsync("slotSubscribe", [], "slotUnsubscribe", ct).ConfigureAwait(false))
            yield return new SolanaSlotUpdate(s.GetProperty("slot").GetUInt64(), s.GetProperty("parent").GetUInt64(), s.GetProperty("root").GetUInt64());
    }

    public static async IAsyncEnumerable<SolanaAccountUpdate> SubscribeSolanaAccountAsync(this WebSocketRpcClient ws, string address, string commitment = "confirmed",
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var options = new Dictionary<string, string> { ["encoding"] = "base64", ["commitment"] = commitment };
        await foreach (var n in ws.SubscribeAsync("accountSubscribe", [address, options], "accountUnsubscribe", ct).ConfigureAwait(false))
        {
            var v = n.GetProperty("value");
            yield return new SolanaAccountUpdate(
                n.GetProperty("context").GetProperty("slot").GetUInt64(),
                v.GetProperty("lamports").GetUInt64(),
                v.GetProperty("owner").GetString()!,
                Convert.FromBase64String(v.GetProperty("data")[0].GetString()!),
                v.TryGetProperty("executable", out var e) && e.GetBoolean());
        }
    }

    /// <summary>Logs of transactions mentioning <paramref name="mentions"/> (an address), or all with <c>"all"</c>.</summary>
    public static async IAsyncEnumerable<SolanaLogsUpdate> SubscribeSolanaLogsAsync(this WebSocketRpcClient ws, string mentions, string commitment = "confirmed",
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        object filter = mentions is "all" or "allWithVotes" ? mentions : new Dictionary<string, string[]> { ["mentions"] = [mentions] };
        var options = new Dictionary<string, string> { ["commitment"] = commitment };
        await foreach (var n in ws.SubscribeAsync("logsSubscribe", [filter, options], "logsUnsubscribe", ct).ConfigureAwait(false))
        {
            var v = n.GetProperty("value");
            yield return new SolanaLogsUpdate(
                n.GetProperty("context").GetProperty("slot").GetUInt64(),
                v.GetProperty("signature").GetString()!,
                v.TryGetProperty("err", out var err) && err.ValueKind != JsonValueKind.Null ? err.GetRawText() : null,
                v.TryGetProperty("logs", out var logs) && logs.ValueKind == JsonValueKind.Array ? [.. logs.EnumerateArray().Select(l => l.GetString()!)] : []);
        }
    }

    /// <summary>
    /// Waits until <paramref name="signature"/> reaches <paramref name="commitment"/>. Returns null on success or the
    /// transaction error as JSON.
    /// </summary>
    public static async Task<string?> WaitForSolanaSignatureAsync(this WebSocketRpcClient ws, string signature, string commitment = "confirmed", CancellationToken ct = default)
    {
        var options = new Dictionary<string, string> { ["commitment"] = commitment };
        // The server removes signature subscriptions after the first notification, so no unsubscribe call is needed.
        await foreach (var n in ws.SubscribeAsync("signatureSubscribe", [signature, options], null, ct).ConfigureAwait(false))
        {
            var v = n.GetProperty("value");
            return v.TryGetProperty("err", out var err) && err.ValueKind != JsonValueKind.Null ? err.GetRawText() : null;
        }
        throw new OperationCanceledException("Subscription ended before the signature was confirmed");
    }
}
