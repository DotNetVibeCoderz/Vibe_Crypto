using System.Runtime.CompilerServices;
using System.Text.Json;
using Crypto.Net.Core;

namespace Crypto.Net.Polkadot;

/// <summary>A Substrate block header (hashes are 0x-hex).</summary>
public sealed record SubstrateHeader(ulong Number, string ParentHash, string StateRoot, string ExtrinsicsRoot);

/// <summary>
/// Status of a watched extrinsic: <c>future</c>, <c>ready</c>, <c>broadcast</c>, <c>inBlock</c>, <c>retracted</c>,
/// <c>finalityTimeout</c>, <c>finalized</c>, <c>usurped</c>, <c>dropped</c> or <c>invalid</c>.
/// </summary>
public sealed record ExtrinsicStatus(string Status, string? BlockHash)
{
    /// <summary>True for statuses after which the node sends no further updates.</summary>
    public bool IsTerminal => Status is "finalized" or "usurped" or "dropped" or "invalid" or "finalityTimeout";
}

/// <summary>Substrate subscriptions over a <see cref="WebSocketRpcClient"/>.</summary>
public static class SubstrateSubscriptions
{
    public static IAsyncEnumerable<SubstrateHeader> SubscribeSubstrateHeadsAsync(this WebSocketRpcClient ws, CancellationToken ct = default) =>
        Headers(ws, "chain_subscribeNewHeads", "chain_unsubscribeNewHeads", ct);

    public static IAsyncEnumerable<SubstrateHeader> SubscribeSubstrateFinalizedHeadsAsync(this WebSocketRpcClient ws, CancellationToken ct = default) =>
        Headers(ws, "chain_subscribeFinalizedHeads", "chain_unsubscribeFinalizedHeads", ct);

    /// <summary>Submits a signed extrinsic and streams its status until a terminal state.</summary>
    public static async IAsyncEnumerable<ExtrinsicStatus> SubmitAndWatchExtrinsicAsync(this WebSocketRpcClient ws, byte[] signedExtrinsic,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var s in ws.SubscribeAsync("author_submitAndWatchExtrinsic", [HexUtil.Encode(signedExtrinsic)], "author_unwatchExtrinsic", ct).ConfigureAwait(false))
        {
            var status = ParseStatus(s);
            yield return status;
            if (status.IsTerminal) yield break;
        }
    }

    internal static ExtrinsicStatus ParseStatus(JsonElement s)
    {
        if (s.ValueKind == JsonValueKind.String) return new ExtrinsicStatus(s.GetString()!, null);
        foreach (var p in s.EnumerateObject())
            return new ExtrinsicStatus(p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null);
        return new ExtrinsicStatus("unknown", null);
    }

    private static async IAsyncEnumerable<SubstrateHeader> Headers(WebSocketRpcClient ws, string method, string unsubscribe, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var h in ws.SubscribeAsync(method, [], unsubscribe, ct).ConfigureAwait(false))
        {
            yield return new SubstrateHeader(
                (ulong)HexUtil.ParseQuantity(h.GetProperty("number").GetString()),
                h.GetProperty("parentHash").GetString()!,
                h.GetProperty("stateRoot").GetString()!,
                h.GetProperty("extrinsicsRoot").GetString()!);
        }
    }
}
