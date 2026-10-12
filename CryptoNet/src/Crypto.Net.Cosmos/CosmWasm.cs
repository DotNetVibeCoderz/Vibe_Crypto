using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Crypto.Net.Cosmos;

/// <summary>CosmWasm (<c>cosmwasm.wasm.v1</c>) messages. Contract messages are JSON, passed as an object or a raw string.</summary>
public static class CosmWasm
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = null };

    /// <summary>Compact UTF-8 JSON of <paramref name="message"/> (a string is used verbatim).</summary>
    public static byte[] ToJsonBytes(object message) => message switch
    {
        string s => Encoding.UTF8.GetBytes(s),
        byte[] b => b,
        JsonElement e => Encoding.UTF8.GetBytes(e.GetRawText()),
        _ => JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), Json),
    };

    /// <summary><c>/cosmwasm.wasm.v1.MsgExecuteContract</c>.</summary>
    public static CosmosMessage Execute(string sender, string contract, object message, params Coin[] funds)
    {
        var w = new ProtoWriter().String(1, sender).String(2, contract).Bytes(3, ToJsonBytes(message));
        foreach (var c in funds) w.Message(5, c.Encode());
        return new CosmosMessage("/cosmwasm.wasm.v1.MsgExecuteContract", w.ToArray());
    }

    /// <summary><c>/cosmwasm.wasm.v1.MsgInstantiateContract</c>.</summary>
    public static CosmosMessage Instantiate(string sender, ulong codeId, string label, object message, string? admin = null, params Coin[] funds)
    {
        var w = new ProtoWriter().String(1, sender).String(2, admin).UInt64(3, codeId).String(4, label).Bytes(5, ToJsonBytes(message));
        foreach (var c in funds) w.Message(6, c.Encode());
        return new CosmosMessage("/cosmwasm.wasm.v1.MsgInstantiateContract", w.ToArray());
    }

    /// <summary><c>/cosmwasm.wasm.v1.MsgMigrateContract</c> (admin only).</summary>
    public static CosmosMessage Migrate(string sender, string contract, ulong codeId, object message) =>
        new("/cosmwasm.wasm.v1.MsgMigrateContract", new ProtoWriter().String(1, sender).String(2, contract).UInt64(3, codeId).Bytes(4, ToJsonBytes(message)).ToArray());

    /// <summary><c>/cosmwasm.wasm.v1.MsgUpdateAdmin</c>.</summary>
    public static CosmosMessage UpdateAdmin(string sender, string contract, string newAdmin) =>
        new("/cosmwasm.wasm.v1.MsgUpdateAdmin", new ProtoWriter().String(1, sender).String(2, newAdmin).String(3, contract).ToArray());
}

/// <summary>Information about a deployed contract.</summary>
public sealed record WasmContractInfo(string Address, ulong CodeId, string Creator, string? Admin, string Label);

public sealed partial class CosmosRestClient
{
    /// <summary>Runs a smart query and returns the contract's JSON response (<c>data</c>).</summary>
    public async ValueTask<JsonElement> QueryContractAsync(string contract, object query, CancellationToken ct = default)
    {
        string encoded = Uri.EscapeDataString(Convert.ToBase64String(CosmWasm.ToJsonBytes(query)));
        var r = await SendAsync(HttpMethod.Get, $"/cosmwasm/wasm/v1/contract/{contract}/smart/{encoded}", null, ct).ConfigureAwait(false);
        return r.GetProperty("data");
    }

    /// <summary>Smart query deserialized into <typeparamref name="T"/>.</summary>
    public async ValueTask<T?> QueryContractAsync<T>(string contract, object query, CancellationToken ct = default) =>
        (await QueryContractAsync(contract, query, ct).ConfigureAwait(false)).Deserialize<T>();

    /// <summary>Raw storage read by key; null when the key is absent.</summary>
    public async ValueTask<byte[]?> QueryContractRawAsync(string contract, byte[] key, CancellationToken ct = default)
    {
        string encoded = Uri.EscapeDataString(Convert.ToBase64String(key));
        var r = await SendAsync(HttpMethod.Get, $"/cosmwasm/wasm/v1/contract/{contract}/raw/{encoded}", null, ct).ConfigureAwait(false);
        return r.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String && d.GetString() is { Length: > 0 } b64 ? Convert.FromBase64String(b64) : null;
    }

    public async ValueTask<WasmContractInfo> GetContractInfoAsync(string contract, CancellationToken ct = default)
    {
        var r = await SendAsync(HttpMethod.Get, $"/cosmwasm/wasm/v1/contract/{contract}", null, ct).ConfigureAwait(false);
        var info = r.GetProperty("contract_info");
        string? admin = info.TryGetProperty("admin", out var a) ? a.GetString() : null;
        return new WasmContractInfo(r.GetProperty("address").GetString()!, ulong.Parse(info.GetProperty("code_id").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
            info.GetProperty("creator").GetString()!, string.IsNullOrEmpty(admin) ? null : admin, info.GetProperty("label").GetString() ?? "");
    }

    /// <summary>Contracts instantiated from <paramref name="codeId"/> (first page).</summary>
    public async ValueTask<IReadOnlyList<string>> GetContractsByCodeAsync(ulong codeId, int limit = 50, CancellationToken ct = default)
    {
        var r = await SendAsync(HttpMethod.Get, $"/cosmwasm/wasm/v1/code/{codeId}/contracts?pagination.limit={limit}", null, ct).ConfigureAwait(false);
        return [.. r.GetProperty("contracts").EnumerateArray().Select(c => c.GetString()!)];
    }
}

/// <summary>CW20 fungible-token contract client.</summary>
public sealed class Cw20Client(CosmosRestClient rest, string contract)
{
    public string Contract { get; } = contract;

    public sealed record TokenInfo(string Name, string Symbol, int Decimals, BigInteger TotalSupply);

    public async ValueTask<BigInteger> GetBalanceAsync(string address, CancellationToken ct = default)
    {
        var r = await rest.QueryContractAsync(Contract, new Dictionary<string, object> { ["balance"] = new Dictionary<string, string> { ["address"] = address } }, ct).ConfigureAwait(false);
        return BigInteger.Parse(r.GetProperty("balance").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async ValueTask<TokenInfo> GetTokenInfoAsync(CancellationToken ct = default)
    {
        var r = await rest.QueryContractAsync(Contract, new Dictionary<string, object> { ["token_info"] = new Dictionary<string, object>() }, ct).ConfigureAwait(false);
        return new TokenInfo(r.GetProperty("name").GetString()!, r.GetProperty("symbol").GetString()!, r.GetProperty("decimals").GetInt32(),
            BigInteger.Parse(r.GetProperty("total_supply").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary><c>{"transfer":{"recipient","amount"}}</c> as a message for <see cref="CosmosTxBuilder"/>.</summary>
    public CosmosMessage Transfer(string sender, string recipient, BigInteger amount) =>
        CosmWasm.Execute(sender, Contract, new Dictionary<string, object>
        {
            ["transfer"] = new Dictionary<string, string> { ["recipient"] = recipient, ["amount"] = amount.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        });

    /// <summary><c>{"send":{"contract","amount","msg"}}</c>: transfer to a contract with a base64 hook message.</summary>
    public CosmosMessage Send(string sender, string targetContract, BigInteger amount, object hookMessage) =>
        CosmWasm.Execute(sender, Contract, new Dictionary<string, object>
        {
            ["send"] = new Dictionary<string, string>
            {
                ["contract"] = targetContract,
                ["amount"] = amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["msg"] = Convert.ToBase64String(CosmWasm.ToJsonBytes(hookMessage)),
            },
        });
}
