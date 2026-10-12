using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Crypto.Net.Core;

namespace Crypto.Net.Evm;

/// <summary>A block header pushed by <c>eth_subscribe("newHeads")</c>.</summary>
public sealed record EvmBlockHeader(ulong Number, string Hash, string ParentHash, DateTimeOffset Timestamp, BigInteger? BaseFeePerGas, ulong GasUsed, ulong GasLimit);

/// <summary><c>eth_subscribe</c> streams over a <see cref="WebSocketRpcClient"/>.</summary>
public static class EvmSubscriptions
{
    /// <summary>New block headers as they are produced.</summary>
    public static async IAsyncEnumerable<EvmBlockHeader> SubscribeEvmHeadsAsync(this WebSocketRpcClient ws, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var h in ws.SubscribeAsync("eth_subscribe", ["newHeads"], "eth_unsubscribe", ct).ConfigureAwait(false))
            yield return ParseHeader(h);
    }

    /// <summary>Logs matching a contract address and optional topic filter (<c>null</c> topic = wildcard).</summary>
    public static async IAsyncEnumerable<EvmLog> SubscribeEvmLogsAsync(this WebSocketRpcClient ws, string? address, IReadOnlyList<string?>? topics = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var filter = new Dictionary<string, object?>();
        if (address is not null) filter["address"] = address;
        if (topics is not null) filter["topics"] = topics;
        await foreach (var l in ws.SubscribeAsync("eth_subscribe", ["logs", filter], "eth_unsubscribe", ct).ConfigureAwait(false))
            yield return EvmRpcClient.ParseLog(l);
    }

    /// <summary>Hashes of transactions entering the node's mempool (not every provider offers this).</summary>
    public static async IAsyncEnumerable<string> SubscribeEvmPendingTransactionsAsync(this WebSocketRpcClient ws, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var h in ws.SubscribeAsync("eth_subscribe", ["newPendingTransactions"], "eth_unsubscribe", ct).ConfigureAwait(false))
            yield return h.GetString()!;
    }

    internal static EvmBlockHeader ParseHeader(JsonElement h)
    {
        static string S(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        BigInteger? baseFee = h.TryGetProperty("baseFeePerGas", out var b) && b.ValueKind == JsonValueKind.String ? HexUtil.ParseQuantity(b.GetString()) : null;
        return new EvmBlockHeader(
            (ulong)HexUtil.ParseQuantity(S(h, "number")),
            S(h, "hash"),
            S(h, "parentHash"),
            DateTimeOffset.FromUnixTimeSeconds((long)HexUtil.ParseQuantity(S(h, "timestamp"))),
            baseFee,
            (ulong)HexUtil.ParseQuantity(S(h, "gasUsed")),
            (ulong)HexUtil.ParseQuantity(S(h, "gasLimit")));
    }
}
