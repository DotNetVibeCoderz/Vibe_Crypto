using System.Numerics;
using Crypto.Net.Core;

namespace Crypto.Net.Evm;

/// <summary>ERC-165 interface ids of common standards.</summary>
public static class InterfaceIds
{
    public const string Erc165 = "0x01ffc9a7";
    public const string Erc721 = "0x80ac58cd";
    public const string Erc721Metadata = "0x5b5e139f";
    public const string Erc721Enumerable = "0x780e9d63";
    public const string Erc1155 = "0xd9b67a26";
    public const string Erc1155MetadataUri = "0x0e89341c";
    public const string Erc2981Royalties = "0x2a55205a";
    public const string Erc4906MetadataUpdate = "0x49064906";
}

/// <summary>Shared ERC-165 support.</summary>
public abstract class Erc165Contract(EvmRpcClient rpc, string address) : EvmContract(rpc, address)
{
    /// <summary><c>supportsInterface(bytes4)</c>; false when the contract does not implement ERC-165 or reverts.</summary>
    public async ValueTask<bool> SupportsInterfaceAsync(string interfaceId, CancellationToken ct = default)
    {
        try
        {
            var r = await CallAsync("supportsInterface(bytes4)", "bool", [HexUtil.Decode(interfaceId)], "latest", ct).ConfigureAwait(false);
            return (bool)r[0]!;
        }
        catch (Exception ex) when (ex is RpcException or FormatException or IndexOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>EIP-2981 royalty for a sale; null when the contract does not implement it.</summary>
    public async ValueTask<(string Receiver, BigInteger Amount)?> GetRoyaltyAsync(BigInteger tokenId, BigInteger salePrice, CancellationToken ct = default)
    {
        if (!await SupportsInterfaceAsync(InterfaceIds.Erc2981Royalties, ct).ConfigureAwait(false)) return null;
        var r = await CallAsync("royaltyInfo(uint256,uint256)", "address,uint256", [tokenId, salePrice], "latest", ct).ConfigureAwait(false);
        return ((string)r[0]!, (BigInteger)r[1]!);
    }
}

/// <summary>ERC-721 non-fungible token client (with the optional metadata and enumerable extensions).</summary>
public sealed class Erc721Client(EvmRpcClient rpc, string address) : Erc165Contract(rpc, address)
{
    /// <summary>ERC-721 <c>Transfer</c>: the token id is indexed (four topics), unlike ERC-20.</summary>
    public static EvmEvent TransferEvent { get; } = EvmEvent.Parse("Transfer(address indexed from, address indexed to, uint256 indexed tokenId)");
    public static EvmEvent ApprovalEvent { get; } = EvmEvent.Parse("Approval(address indexed owner, address indexed approved, uint256 indexed tokenId)");
    public static EvmEvent ApprovalForAllEvent { get; } = EvmEvent.Parse("ApprovalForAll(address indexed owner, address indexed operator, bool approved)");

    public async ValueTask<string> GetNameAsync(CancellationToken ct = default) => (string)(await CallAsync("name()", "string", [], "latest", ct).ConfigureAwait(false))[0]!;
    public async ValueTask<string> GetSymbolAsync(CancellationToken ct = default) => (string)(await CallAsync("symbol()", "string", [], "latest", ct).ConfigureAwait(false))[0]!;
    public async ValueTask<BigInteger> GetBalanceAsync(string owner, CancellationToken ct = default) => (BigInteger)(await CallAsync("balanceOf(address)", "uint256", [owner], "latest", ct).ConfigureAwait(false))[0]!;
    public async ValueTask<string> GetOwnerAsync(BigInteger tokenId, CancellationToken ct = default) => (string)(await CallAsync("ownerOf(uint256)", "address", [tokenId], "latest", ct).ConfigureAwait(false))[0]!;
    public async ValueTask<string> GetTokenUriAsync(BigInteger tokenId, CancellationToken ct = default) => (string)(await CallAsync("tokenURI(uint256)", "string", [tokenId], "latest", ct).ConfigureAwait(false))[0]!;
    public async ValueTask<string> GetApprovedAsync(BigInteger tokenId, CancellationToken ct = default) => (string)(await CallAsync("getApproved(uint256)", "address", [tokenId], "latest", ct).ConfigureAwait(false))[0]!;
    public async ValueTask<bool> IsApprovedForAllAsync(string owner, string @operator, CancellationToken ct = default) => (bool)(await CallAsync("isApprovedForAll(address,address)", "bool", [owner, @operator], "latest", ct).ConfigureAwait(false))[0]!;
    public async ValueTask<BigInteger> GetTotalSupplyAsync(CancellationToken ct = default) => (BigInteger)(await CallAsync("totalSupply()", "uint256", [], "latest", ct).ConfigureAwait(false))[0]!;
    public async ValueTask<BigInteger> GetTokenOfOwnerByIndexAsync(string owner, BigInteger index, CancellationToken ct = default) => (BigInteger)(await CallAsync("tokenOfOwnerByIndex(address,uint256)", "uint256", [owner, index], "latest", ct).ConfigureAwait(false))[0]!;

    /// <summary><c>safeTransferFrom</c>: reverts if <paramref name="to"/> is a contract that does not accept ERC-721 tokens.</summary>
    public ValueTask<TxHash> SafeTransferFromAsync(ISigner signer, string from, string to, BigInteger tokenId, byte[]? data = null, CancellationToken ct = default) =>
        data is null
            ? SendAsync(signer, "safeTransferFrom(address,address,uint256)", [from, to, tokenId], BigInteger.Zero, ct)
            : SendAsync(signer, "safeTransferFrom(address,address,uint256,bytes)", [from, to, tokenId, data], BigInteger.Zero, ct);

    public ValueTask<TxHash> TransferFromAsync(ISigner signer, string from, string to, BigInteger tokenId, CancellationToken ct = default) =>
        SendAsync(signer, "transferFrom(address,address,uint256)", [from, to, tokenId], BigInteger.Zero, ct);

    public ValueTask<TxHash> ApproveAsync(ISigner signer, string approved, BigInteger tokenId, CancellationToken ct = default) =>
        SendAsync(signer, "approve(address,uint256)", [approved, tokenId], BigInteger.Zero, ct);

    public ValueTask<TxHash> SetApprovalForAllAsync(ISigner signer, string @operator, bool approved, CancellationToken ct = default) =>
        SendAsync(signer, "setApprovalForAll(address,bool)", [@operator, approved], BigInteger.Zero, ct);

    public static byte[] EncodeSafeTransferFrom(string from, string to, BigInteger tokenId) =>
        EvmAbi.EncodeFunctionCall("safeTransferFrom(address,address,uint256)", from, to, tokenId);
}

/// <summary>ERC-1155 multi-token client.</summary>
public sealed class Erc1155Client(EvmRpcClient rpc, string address) : Erc165Contract(rpc, address)
{
    public static EvmEvent TransferSingleEvent { get; } = EvmEvent.Parse("TransferSingle(address indexed operator, address indexed from, address indexed to, uint256 id, uint256 value)");
    public static EvmEvent TransferBatchEvent { get; } = EvmEvent.Parse("TransferBatch(address indexed operator, address indexed from, address indexed to, uint256[] ids, uint256[] values)");
    public static EvmEvent ApprovalForAllEvent { get; } = EvmEvent.Parse("ApprovalForAll(address indexed account, address indexed operator, bool approved)");
    public static EvmEvent UriEvent { get; } = EvmEvent.Parse("URI(string value, uint256 indexed id)");

    public async ValueTask<BigInteger> GetBalanceAsync(string account, BigInteger id, CancellationToken ct = default) =>
        (BigInteger)(await CallAsync("balanceOf(address,uint256)", "uint256", [account, id], "latest", ct).ConfigureAwait(false))[0]!;

    public async ValueTask<BigInteger[]> GetBalanceBatchAsync(IReadOnlyList<string> accounts, IReadOnlyList<BigInteger> ids, CancellationToken ct = default)
    {
        if (accounts.Count != ids.Count) throw new ArgumentException("accounts and ids must have the same length");
        var r = await CallAsync("balanceOfBatch(address[],uint256[])", "uint256[]", [accounts, ids], "latest", ct).ConfigureAwait(false);
        return ToArray(r[0], x => (BigInteger)x!);
    }

    /// <summary>Metadata URI with the <c>{id}</c> placeholder replaced by the 64-hex-digit id, as ERC-1155 specifies.</summary>
    public async ValueTask<string> GetUriAsync(BigInteger id, CancellationToken ct = default)
    {
        string uri = (string)(await CallAsync("uri(uint256)", "string", [id], "latest", ct).ConfigureAwait(false))[0]!;
        return uri.Replace("{id}", id.ToString("x64", System.Globalization.CultureInfo.InvariantCulture)[^64..], StringComparison.Ordinal);
    }

    public async ValueTask<bool> IsApprovedForAllAsync(string account, string @operator, CancellationToken ct = default) =>
        (bool)(await CallAsync("isApprovedForAll(address,address)", "bool", [account, @operator], "latest", ct).ConfigureAwait(false))[0]!;

    public ValueTask<TxHash> SafeTransferFromAsync(ISigner signer, string from, string to, BigInteger id, BigInteger amount, byte[]? data = null, CancellationToken ct = default) =>
        SendAsync(signer, "safeTransferFrom(address,address,uint256,uint256,bytes)", [from, to, id, amount, data ?? []], BigInteger.Zero, ct);

    public ValueTask<TxHash> SafeBatchTransferFromAsync(ISigner signer, string from, string to, IReadOnlyList<BigInteger> ids, IReadOnlyList<BigInteger> amounts, byte[]? data = null, CancellationToken ct = default) =>
        SendAsync(signer, "safeBatchTransferFrom(address,address,uint256[],uint256[],bytes)", [from, to, ids, amounts, data ?? []], BigInteger.Zero, ct);

    public ValueTask<TxHash> SetApprovalForAllAsync(ISigner signer, string @operator, bool approved, CancellationToken ct = default) =>
        SendAsync(signer, "setApprovalForAll(address,bool)", [@operator, approved], BigInteger.Zero, ct);
}
