using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Crypto.Net.Core;
using Crypto.Net.Evm;
using Crypto.Net.Indexer;
using Crypto.Net.Indexer.EntityFrameworkCore;
using Crypto.Net.Native;
using Crypto.Net.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Crypto.Net.Tests;

public class IndexerTests
{
    private const string Token = "0x1c7D4B196Cb0C7B01d743Fbc6116a902379C7238";
    private static readonly string Alice = EvmAddress.ToChecksumAddress(Enumerable.Repeat((byte)0xaa, 20).ToArray());
    private static readonly string Bob = EvmAddress.ToChecksumAddress(Enumerable.Repeat((byte)0xbb, 20).ToArray());

    private static string Word(string address) => "0x" + new string('0', 24) + address[2..].ToLowerInvariant();

    private static EvmLog TransferLog(ulong block, ulong index, BigInteger value) => new(
        Token,
        [EvmEvent.Erc20Transfer.Topic0, Word(Alice), Word(Bob)],
        HexUtil.Encode(EvmAbi.EncodeUint256(value)),
        block,
        "0x" + block.ToString("x64", CultureInfo.InvariantCulture),
        index);

    [Fact]
    public void EvmEvent_ParsesAndDecodes()
    {
        var transfer = EvmEvent.Erc20Transfer;
        Assert.Equal("Transfer(address,address,uint256)", transfer.Signature);
        Assert.Equal("0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef", transfer.Topic0);

        var decoded = transfer.Decode(TransferLog(5, 0, 1_500_000));
        Assert.Equal("Transfer", decoded.Name);
        Assert.Equal(Alice, decoded["from"]);
        Assert.Equal(Bob, decoded["to"]);
        Assert.Equal(new BigInteger(1_500_000), decoded["value"]);

        // ERC-721 Transfer has the token id indexed: four topics, so it must not match the ERC-20 definition.
        var nft = new EvmLog(Token, [transfer.Topic0, Word(Alice), Word(Bob), HexUtil.Encode(EvmAbi.EncodeUint256(7))], "0x", 1, "0x01", 0);
        Assert.False(transfer.Matches(nft));
        var erc721 = EvmEvent.Parse("event Transfer(address indexed from, address indexed to, uint256 indexed tokenId);");
        Assert.Equal(new BigInteger(7), erc721.Decode(nft)["tokenId"]);
    }

    [Fact]
    public void EvmEvent_DynamicAndIndexedDynamicParameters()
    {
        var ev = EvmEvent.Parse("Posted(address indexed author, string indexed topic, string text, uint256[] scores)");
        Assert.Equal("Posted(address,string,string,uint256[])", ev.Signature);
        byte[] topicHash = CryptoNative.Keccak256("news"u8);
        var log = new EvmLog(Token, [ev.Topic0, Word(Alice), HexUtil.Encode(topicHash)],
            HexUtil.Encode(EvmAbi.Encode("string,uint256[]", "hello", new[] { new BigInteger(1), new BigInteger(2) })), 9, "0x02", 3);
        var d = ev.Decode(log);
        Assert.Equal(Alice, d["author"]);
        Assert.Equal(topicHash, (byte[])d["topic"]!);
        Assert.Equal("hello", d["text"]);
        Assert.Equal(2, ((object?[])d["scores"]!).Length);
        Assert.False(ev.TryDecode(log with { Data = "0x1234" }, out _));
    }

    /// <summary>A chain at height <c>head</c> with one transfer every 7 blocks; refuses ranges wider than 25 blocks.</summary>
    private static (MockHttpMessageHandler Mock, List<(ulong From, ulong To)> Calls) FakeChain(Func<ulong> head)
    {
        var calls = new List<(ulong, ulong)>();
        var mock = new MockHttpMessageHandler()
            .OnRpc("eth_blockNumber", _ => $"\"0x{head():x}\"")
            .OnRpc("eth_getLogs", p =>
            {
                var f = p[0];
                ulong from = (ulong)HexUtil.ParseQuantity(f.GetProperty("fromBlock").GetString());
                ulong to = (ulong)HexUtil.ParseQuantity(f.GetProperty("toBlock").GetString());
                Assert.Equal(EvmEvent.Erc20Transfer.Topic0, f.GetProperty("topics")[0].GetString());
                lock (calls) calls.Add((from, to));
                if (to - from + 1 > 25) throw new MockRpcError(-32005, "query returned more than 10000 results");
                var logs = new List<object>();
                for (ulong b = from; b <= to; b++)
                {
                    if (b % 7 != 0) continue;
                    var l = TransferLog(b, 0, b * 10);
                    logs.Add(new
                    {
                        address = l.Address,
                        topics = l.Topics,
                        data = l.Data,
                        blockNumber = $"0x{b:x}",
                        transactionHash = l.TransactionHash,
                        logIndex = "0x0",
                    });
                }
                return JsonSerializer.Serialize(logs);
            });
        return (mock, calls);
    }

    [Fact]
    public async Task EvmLogIndexer_PagesAdaptsAndResumes()
    {
        ulong head = 120;
        var (mock, calls) = FakeChain(() => head);
        await using var client = new EvmRpcClient(EvmChain.Sepolia, httpClient: mock.CreateClient());
        var checkpoints = new InMemoryCheckpointStore();
        var seen = new List<EvmDecodedEvent>();
        var options = new EvmLogIndexerOptions { Name = "usdc", Addresses = [Token], Events = [EvmEvent.Erc20Transfer], StartBlock = 0, Confirmations = 10, MaxBlockRange = 100 };
        var indexer = new EvmLogIndexer(client, checkpoints, new DelegateLogHandler((b, _) => { seen.AddRange(b.Events); return Task.CompletedTask; }), options);

        Assert.Equal(110ul, await indexer.RunOnceAsync());
        Assert.Equal(110ul, await checkpoints.GetAsync("usdc"));
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (ulong)i * 7).ToList(), seen.Select(e => e.Log.BlockNumber).ToList());
        Assert.Contains(calls, c => c.To - c.From + 1 > 25);                 // refused...
        Assert.True(calls.Where(c => c.To - c.From + 1 <= 25).Sum(c => (long)(c.To - c.From + 1)) == 111); // ...then covered exactly once

        Assert.Null(await indexer.RunOnceAsync());                           // nothing new
        head = 130;
        Assert.Equal(120ul, await indexer.RunOnceAsync());
        Assert.Equal(119ul, seen[^1].Log.BlockNumber);

        // A fresh indexer with the same checkpoint store resumes instead of starting over.
        var resumed = new EvmLogIndexer(client, checkpoints, new DelegateLogHandler((_, _) => Task.CompletedTask), options);
        calls.Clear();
        head = 131;
        Assert.Equal(121ul, await resumed.RunOnceAsync());
        Assert.Equal((121ul, 121ul), Assert.Single(calls));
    }

    [Fact]
    public async Task EvmLogIndexer_RedeliversWhenHandlerFails()
    {
        var (mock, _) = FakeChain(() => 40);
        await using var client = new EvmRpcClient(EvmChain.Sepolia, httpClient: mock.CreateClient());
        var checkpoints = new InMemoryCheckpointStore();
        int attempts = 0;
        var handler = new DelegateLogHandler((_, _) => ++attempts == 1 ? throw new InvalidOperationException("db down") : Task.CompletedTask);
        var indexer = new EvmLogIndexer(client, checkpoints, handler, new EvmLogIndexerOptions { Events = [EvmEvent.Erc20Transfer], StartBlock = 0, Confirmations = 0, MaxBlockRange = 20 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => indexer.RunOnceAsync());
        Assert.Null(await checkpoints.GetAsync("evm-logs"));
        Assert.Equal(40ul, await indexer.RunOnceAsync());
    }

    [Fact]
    public async Task FileCheckpointStore_Persists()
    {
        string path = Path.Combine(Path.GetTempPath(), $"cnet-cp-{Guid.NewGuid():N}.json");
        try
        {
            await new FileCheckpointStore(path).SetAsync("a", 42);
            await new FileCheckpointStore(path).SetAsync("b", 7);
            var store = new FileCheckpointStore(path);
            Assert.Equal(42ul, await store.GetAsync("a"));
            Assert.Equal(7ul, await store.GetAsync("b"));
            Assert.Null(await store.GetAsync("c"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BlockCrawler_VisitsEveryHeightOnce()
    {
        var mock = new MockHttpMessageHandler().OnRpc("eth_blockNumber", "\"0xa\"");
        await using var client = new EvmRpcClient(EvmChain.Sepolia, httpClient: mock.CreateClient());
        var visited = new List<ulong>();
        var crawler = new BlockCrawler(client, new InMemoryCheckpointStore(), (n, _) => { visited.Add(n); return Task.CompletedTask; },
            new BlockCrawlerOptions { StartBlock = 3, Confirmations = 2, MaxBlocksPerPoll = 4 });
        Assert.Equal(6ul, await crawler.RunOnceAsync());
        Assert.Equal(8ul, await crawler.RunOnceAsync());
        Assert.Null(await crawler.RunOnceAsync());
        Assert.Equal([3ul, 4, 5, 6, 7, 8], visited);
    }

    [Fact]
    public async Task EfStore_WritesLogsTransfersAndCheckpointsIdempotently()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CryptoNetIndexerDbContext>().UseSqlite(connection).Options;
        var factory = new PooledDbContextFactory<CryptoNetIndexerDbContext>(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.EnsureCreatedAsync();

        var (mock, _) = FakeChain(() => 60);
        await using var client = new EvmRpcClient(EvmChain.Sepolia, httpClient: mock.CreateClient());
        var store = new EfEvmLogStore<CryptoNetIndexerDbContext>(factory, new EvmLogStoreOptions { ChainId = "sepolia" });
        var checkpoints = new EfCheckpointStore<CryptoNetIndexerDbContext>(factory);
        var indexer = new EvmLogIndexer(client, checkpoints, store, new EvmLogIndexerOptions { Name = "usdc", Events = [EvmEvent.Erc20Transfer], StartBlock = 0, Confirmations = 0, MaxBlockRange = 25 });

        Assert.Equal(60ul, await indexer.RunOnceAsync());
        Assert.Equal(60ul, await checkpoints.GetAsync("usdc"));

        // Redeliver an already stored range: nothing is duplicated.
        var batch = new EvmLogBatch(0, 30, [TransferLog(7, 0, 70), TransferLog(14, 0, 140)], []);
        await store.HandleAsync(batch, default);

        await using var check = await factory.CreateDbContextAsync();
        Assert.Equal(9, await check.EvmLogs.CountAsync());
        Assert.Equal(9, await check.TokenTransfers.CountAsync());
        Assert.Equal(9, await check.EvmLogs.CountAsync(l => l.EventName == "Transfer"));
        var t = await check.TokenTransfers.OrderBy(x => x.BlockNumber).LastAsync();
        Assert.Equal((56L, Alice, Bob, new BigInteger(560)), (t.BlockNumber, t.From, t.To, t.RawValue));
    }
}
