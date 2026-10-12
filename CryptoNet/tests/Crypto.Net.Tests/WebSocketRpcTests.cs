using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Crypto.Net.Core;
using Crypto.Net.Evm;
using Crypto.Net.Polkadot;
using Crypto.Net.Solana;

namespace Crypto.Net.Tests;

/// <summary>WebSocket JSON-RPC and subscriptions against an in-process loopback server.</summary>
public class WebSocketRpcTests
{
    /// <summary>
    /// Minimal JSON-RPC WebSocket peer: answers each request with <c>handler</c>; the handler may push notifications
    /// with <c>send</c> before or after returning its result.
    /// </summary>
    private sealed class FakeServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Func<string, JsonNode?, Func<JsonNode, Task>, Task<JsonNode?>> _handler;
        private WebSocket? _server;
        private Task? _loop;
        public List<string> Methods { get; } = [];

        public FakeServer(Func<string, JsonNode?, Func<JsonNode, Task>, Task<JsonNode?>> handler)
        {
            _handler = handler;
            _listener.Start();
        }

        public WebSocketRpcClient CreateClient() => new(async ct =>
        {
            var client = new TcpClient();
            var accept = _listener.AcceptTcpClientAsync(ct).AsTask();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port, ct);
            var serverTcp = await accept;
            _server = WebSocket.CreateFromStream(serverTcp.GetStream(), new WebSocketCreationOptions { IsServer = true });
            _loop = Task.Run(ServeAsync);
            return WebSocket.CreateFromStream(client.GetStream(), new WebSocketCreationOptions { IsServer = false });
        });

        public Task Notify(JsonNode message) => Send(message);

        public async Task CloseAsync() => await _server!.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "going away", default);

        private readonly SemaphoreSlim _sendLock = new(1, 1);

        private async Task Send(JsonNode message)
        {
            await _sendLock.WaitAsync();
            try
            {
                await _server!.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, default);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task ServeAsync()
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (_server!.State == WebSocketState.Open)
                {
                    var r = await _server.ReceiveAsync(buffer, default);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    var req = JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, r.Count))!;
                    string method = req["method"]!.GetValue<string>();
                    lock (Methods) Methods.Add(method);
                    try
                    {
                        var result = await _handler(method, req["params"], Send);
                        await Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = req["id"]!.GetValue<long>(), ["result"] = result });
                    }
                    catch (InvalidOperationException ex)
                    {
                        await Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = req["id"]!.GetValue<long>(), ["error"] = new JsonObject { ["code"] = -32000, ["message"] = ex.Message } });
                    }
                }
            }
            catch (WebSocketException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            if (_loop is not null) await Task.WhenAny(_loop, Task.Delay(2000));
            _server?.Dispose();
        }
    }

    private static JsonObject Notification(string method, JsonNode subscription, JsonNode result) =>
        new() { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = new JsonObject { ["subscription"] = subscription, ["result"] = result } };

    [Fact]
    public async Task Call_ReturnsResult_AndErrorsAsRpcException()
    {
        await using var server = new FakeServer((method, p, _) => method switch
        {
            "echo" => Task.FromResult<JsonNode?>(p![0]!.DeepClone()),
            _ => throw new InvalidOperationException("method not found"),
        });
        await using var ws = server.CreateClient();
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => ws.CallAsync("echo", [i])));
        Assert.Equal(Enumerable.Range(0, 20), results.Select(r => r.GetInt32()));
        var ex = await Assert.ThrowsAsync<RpcException>(() => ws.CallAsync("nope"));
        Assert.Equal(-32000, ex.Code);
        Assert.Equal("nope", ex.Method);
    }

    [Fact]
    public async Task EvmNewHeads_StreamsAndUnsubscribes()
    {
        await using var server = new FakeServer(async (method, p, send) =>
        {
            if (method == "eth_unsubscribe") return true;
            Assert.Equal("newHeads", p![0]!.GetValue<string>());
            // Push notifications right behind the response to exercise the registration race.
            _ = Task.Run(async () =>
            {
                await Task.Yield();
                for (int i = 0; i < 3; i++)
                    await send(Notification("eth_subscription", "0xabc", new JsonObject
                    {
                        ["number"] = $"0x{100 + i:x}",
                        ["hash"] = $"0x{i:x64}",
                        ["parentHash"] = "0x00",
                        ["timestamp"] = "0x6553f100",
                        ["baseFeePerGas"] = "0x3b9aca00",
                        ["gasUsed"] = "0x5208",
                        ["gasLimit"] = "0x1c9c380",
                    }));
            });
            return "0xabc";
        });
        await using var ws = server.CreateClient();
        var heads = new List<EvmBlockHeader>();
        await foreach (var h in ws.SubscribeEvmHeadsAsync())
        {
            heads.Add(h);
            if (heads.Count == 3) break;
        }
        Assert.Equal([100ul, 101ul, 102ul], heads.Select(h => h.Number));
        Assert.Equal(1_000_000_000, heads[0].BaseFeePerGas);
        Assert.Equal(21000ul, heads[0].GasUsed);
        await WaitFor(() => server.Methods.Contains("eth_unsubscribe"));
    }

    [Fact]
    public async Task SolanaSlots_And_SignatureWait()
    {
        await using var server = new FakeServer((method, p, send) =>
        {
            switch (method)
            {
                case "slotSubscribe":
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(20);
                        await send(Notification("slotNotification", 7, new JsonObject { ["parent"] = 41, ["root"] = 10, ["slot"] = 42 }));
                    });
                    return Task.FromResult<JsonNode?>(7);
                case "signatureSubscribe":
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(20);
                        await send(Notification("signatureNotification", 8, new JsonObject
                        {
                            ["context"] = new JsonObject { ["slot"] = 43 },
                            ["value"] = new JsonObject { ["err"] = new JsonObject { ["InstructionError"] = new JsonArray(0, "Custom") } },
                        }));
                    });
                    return Task.FromResult<JsonNode?>(8);
                default:
                    return Task.FromResult<JsonNode?>(true);
            }
        });
        await using var ws = server.CreateClient();
        await foreach (var slot in ws.SubscribeSolanaSlotsAsync())
        {
            Assert.Equal(new SolanaSlotUpdate(42, 41, 10), slot);
            break;
        }
        string? err = await ws.WaitForSolanaSignatureAsync("5sig");
        Assert.Contains("InstructionError", err);
    }

    [Fact]
    public async Task SubstrateSubmitAndWatch_StopsAtFinalized()
    {
        await using var server = new FakeServer((method, p, send) =>
        {
            if (method != "author_submitAndWatchExtrinsic") return Task.FromResult<JsonNode?>(true);
            Assert.Equal("0x0102", p![0]!.GetValue<string>());
            _ = Task.Run(async () =>
            {
                await Task.Delay(10);
                await send(Notification("author_extrinsicUpdate", "s1", "ready"));
                await send(Notification("author_extrinsicUpdate", "s1", new JsonObject { ["inBlock"] = "0xaa" }));
                await send(Notification("author_extrinsicUpdate", "s1", new JsonObject { ["finalized"] = "0xaa" }));
                await send(Notification("author_extrinsicUpdate", "s1", "never-seen"));
            });
            return Task.FromResult<JsonNode?>("s1");
        });
        await using var ws = server.CreateClient();
        var statuses = new List<ExtrinsicStatus>();
        await foreach (var s in ws.SubmitAndWatchExtrinsicAsync([1, 2])) statuses.Add(s);
        Assert.Equal(["ready", "inBlock", "finalized"], statuses.Select(s => s.Status));
        Assert.Equal("0xaa", statuses[^1].BlockHash);
    }

    [Fact]
    public async Task ServerClose_EndsSubscriptionWithError()
    {
        FakeServer? self = null;
        await using var server = self = new FakeServer((method, _, _) =>
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(30);
                await self!.CloseAsync();
            });
            return Task.FromResult<JsonNode?>("sub");
        });
        await using var ws = server.CreateClient();
        await Assert.ThrowsAsync<WebSocketException>(async () =>
        {
            await foreach (var _ in ws.SubscribeEvmHeadsAsync()) { }
        });
        await Assert.ThrowsAsync<WebSocketException>(() => ws.CallAsync("anything"));
    }

    [Fact]
    public void Urls_AreConverted()
    {
        Assert.Equal("wss://rpc.polkadot.io", WebSocketRpcClient.ToWebSocketUrl("https://rpc.polkadot.io"));
        Assert.Equal("ws://127.0.0.1:9944", WebSocketRpcClient.ToWebSocketUrl("http://127.0.0.1:9944"));
        Assert.Equal("wss://x", WebSocketRpcClient.ToWebSocketUrl("wss://x"));
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }
}
