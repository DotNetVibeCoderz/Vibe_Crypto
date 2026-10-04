using System.Text.Json;
using Crypto.Net.Core;

namespace Crypto.Net.Solana;

/// <summary>An SPL token account owned by a wallet.</summary>
public sealed record SolanaTokenAccount(string Address, string Mint, Amount Balance);

/// <summary>Solana JSON-RPC client.</summary>
public sealed class SolanaRpcClient : IChainClient
{
    private readonly JsonRpcClient _rpc;

    public SolanaRpcClient(SolanaChain chain, string? rpcUrl = null, HttpClient? httpClient = null)
        : this(chain, [new RpcEndpoint(rpcUrl ?? chain.DefaultRpcUrl)], httpClient)
    {
    }

    /// <summary>Uses <paramref name="endpoints"/> in order, failing over when a provider denies access.</summary>
    public SolanaRpcClient(SolanaChain chain, IReadOnlyList<RpcEndpoint> endpoints, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(chain);
        Cluster = chain;
        _rpc = new JsonRpcClient(endpoints, httpClient);
    }

    public SolanaChain Cluster { get; }
    public IChain Chain => Cluster;
    public JsonRpcClient Rpc => _rpc;

    private static readonly object Confirmed = new Dictionary<string, string> { ["commitment"] = "confirmed" };

    public async ValueTask<Amount> GetBalanceAsync(Address address, CancellationToken ct = default) =>
        Amount.FromLamports(await GetBalanceLamportsAsync(address.Value, ct));

    public async ValueTask<ulong> GetBalanceLamportsAsync(string address, CancellationToken ct = default)
    {
        var r = await _rpc.CallAsync("getBalance", [address, Confirmed], ct);
        return r.GetProperty("value").GetUInt64();
    }

    /// <summary>Current slot.</summary>
    public async ValueTask<ulong> GetBlockNumberAsync(CancellationToken ct = default) =>
        (await _rpc.CallAsync("getSlot", [Confirmed], ct)).GetUInt64();

    public async ValueTask<(string Blockhash, ulong LastValidBlockHeight)> GetLatestBlockhashAsync(CancellationToken ct = default)
    {
        var r = await _rpc.CallAsync("getLatestBlockhash", [Confirmed], ct);
        var v = r.GetProperty("value");
        return (v.GetProperty("blockhash").GetString()!, v.GetProperty("lastValidBlockHeight").GetUInt64());
    }

    public async ValueTask<ulong> GetMinimumBalanceForRentExemptionAsync(ulong dataLength, CancellationToken ct = default) =>
        (await _rpc.CallAsync("getMinimumBalanceForRentExemption", [dataLength], ct)).GetUInt64();

    /// <summary>Fee in lamports the network charges for a serialized message.</summary>
    public async ValueTask<ulong?> GetFeeForMessageAsync(byte[] message, CancellationToken ct = default)
    {
        var r = await _rpc.CallAsync("getFeeForMessage", [Convert.ToBase64String(message), Confirmed], ct);
        var v = r.GetProperty("value");
        return v.ValueKind == JsonValueKind.Null ? null : v.GetUInt64();
    }

    public async ValueTask<TxHash> SendRawTransactionAsync(ReadOnlyMemory<byte> signedTxBytes, CancellationToken ct = default)
    {
        var options = new Dictionary<string, object> { ["encoding"] = "base64", ["preflightCommitment"] = "confirmed" };
        var r = await _rpc.CallAsync("sendTransaction", [Convert.ToBase64String(signedTxBytes.Span), options], ct);
        return new TxHash(r.GetString()!);
    }

    /// <summary>Simulates a signed transaction; returns the error (null on success) and program logs.</summary>
    public async ValueTask<(string? Error, IReadOnlyList<string> Logs)> SimulateTransactionAsync(byte[] signedTx, CancellationToken ct = default)
    {
        var options = new Dictionary<string, object> { ["encoding"] = "base64", ["commitment"] = "confirmed" };
        var r = await _rpc.CallAsync("simulateTransaction", [Convert.ToBase64String(signedTx), options], ct);
        var v = r.GetProperty("value");
        string? err = v.TryGetProperty("err", out var e) && e.ValueKind != JsonValueKind.Null ? e.GetRawText() : null;
        var logs = v.TryGetProperty("logs", out var l) && l.ValueKind == JsonValueKind.Array ? l.EnumerateArray().Select(x => x.GetString()!).ToList() : [];
        return (err, logs);
    }

    public async ValueTask<TxReceipt?> GetReceiptAsync(TxHash hash, CancellationToken ct = default)
    {
        var options = new Dictionary<string, bool> { ["searchTransactionHistory"] = true };
        var r = await _rpc.CallAsync("getSignatureStatuses", [new[] { hash.Value }, options], ct);
        var status = r.GetProperty("value")[0];
        if (status.ValueKind == JsonValueKind.Null) return null;
        string? confirmation = status.TryGetProperty("confirmationStatus", out var c) ? c.GetString() : null;
        if (confirmation is not ("confirmed" or "finalized")) return null;
        bool ok = !status.TryGetProperty("err", out var err) || err.ValueKind == JsonValueKind.Null;
        return new TxReceipt(hash, status.GetProperty("slot").GetUInt64(), ok, 0, ok ? null : err.GetRawText());
    }

    /// <summary>Requests an airdrop (devnet/testnet/localnet only).</summary>
    public async ValueTask<TxHash> RequestAirdropAsync(string address, ulong lamports, CancellationToken ct = default)
    {
        if (!Cluster.IsTestnet) throw new InvalidOperationException("Airdrops are only available on test clusters");
        var r = await _rpc.CallAsync("requestAirdrop", [address, lamports], ct);
        return new TxHash(r.GetString()!);
    }

    /// <summary>SPL token accounts of <paramref name="owner"/> (classic Token program).</summary>
    public async ValueTask<IReadOnlyList<SolanaTokenAccount>> GetTokenAccountsAsync(string owner, string tokenProgram = SolanaAddress.TokenProgram, CancellationToken ct = default)
    {
        var filter = new Dictionary<string, string> { ["programId"] = tokenProgram };
        var options = new Dictionary<string, string> { ["encoding"] = "jsonParsed", ["commitment"] = "confirmed" };
        var r = await _rpc.CallAsync("getTokenAccountsByOwner", [owner, filter, options], ct);
        return r.GetProperty("value").EnumerateArray().Select(a =>
        {
            var info = a.GetProperty("account").GetProperty("data").GetProperty("parsed").GetProperty("info");
            var amount = info.GetProperty("tokenAmount");
            return new SolanaTokenAccount(
                a.GetProperty("pubkey").GetString()!,
                info.GetProperty("mint").GetString()!,
                new Amount(System.Numerics.BigInteger.Parse(amount.GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture), amount.GetProperty("decimals").GetInt32()));
        }).ToList();
    }

    /// <summary>Decimals of an SPL mint (byte 44 of the mint account).</summary>
    public async ValueTask<byte> GetMintDecimalsAsync(string mint, CancellationToken ct = default)
    {
        var options = new Dictionary<string, string> { ["encoding"] = "base64" };
        var r = await _rpc.CallAsync("getAccountInfo", [mint, options], ct);
        var value = r.GetProperty("value");
        if (value.ValueKind == JsonValueKind.Null) throw new InvalidOperationException($"Mint {mint} not found");
        byte[] data = Convert.FromBase64String(value.GetProperty("data")[0].GetString()!);
        return data.Length >= 45 ? data[44] : throw new InvalidOperationException($"{mint} is not a mint account");
    }

    /// <summary>Signs, simulates and sends a SOL transfer from <paramref name="account"/>.</summary>
    public async ValueTask<TxHash> TransferAsync(SolanaAccount account, string to, Amount amount, ulong priorityFeeMicroLamports = 0, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        SolanaAddress.Decode(to);
        var (blockhash, _) = await GetLatestBlockhashAsync(ct);
        var tx = new SolanaTransaction(account.Address.Value, blockhash);
        if (priorityFeeMicroLamports > 0) tx.Add(ComputeBudgetProgram.SetComputeUnitPrice(priorityFeeMicroLamports));
        tx.Add(SystemProgram.Transfer(account.Address.Value, to, (ulong)amount.WithDecimals(9).BaseUnits));
        return await SendAsync(account, tx, ct);
    }

    /// <summary>Transfers SPL tokens, creating the recipient's associated token account when needed.</summary>
    public async ValueTask<TxHash> TransferTokenAsync(SolanaAccount account, string mint, string to, Amount amount, CancellationToken ct = default)
    {
        byte decimals = await GetMintDecimalsAsync(mint, ct);
        string owner = account.Address.Value;
        string source = SolanaAddress.GetAssociatedTokenAddress(owner, mint);
        string destination = SolanaAddress.GetAssociatedTokenAddress(to, mint);
        var (blockhash, _) = await GetLatestBlockhashAsync(ct);
        var tx = new SolanaTransaction(owner, blockhash)
            .Add(TokenProgram.CreateAssociatedTokenAccountIdempotent(owner, to, mint))
            .Add(TokenProgram.TransferChecked(source, mint, destination, owner, (ulong)amount.WithDecimals(decimals).BaseUnits, decimals));
        return await SendAsync(account, tx, ct);
    }

    private async ValueTask<TxHash> SendAsync(SolanaAccount account, SolanaTransaction tx, CancellationToken ct)
    {
        account.Sign(tx);
        byte[] raw = tx.Serialize();
        var (error, logs) = await SimulateTransactionAsync(raw, ct);
        if (error is not null) throw new RpcException(-32002, $"Simulation failed: {error}", string.Join('\n', logs), "simulateTransaction");
        return await SendRawTransactionAsync(raw, ct);
    }

    public void Dispose() => _rpc.Dispose();

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
