namespace Crypto.Net.Core;

/// <summary>An error returned by a node (JSON-RPC error object or REST error payload).</summary>
public sealed class RpcException : Exception
{
    public RpcException(long code, string message, string? errorData = null, string? method = null)
        : base(method is null ? $"RPC error {code}: {message}" : $"RPC error {code} in {method}: {message}")
    {
        Code = code;
        RpcMessage = message;
        ErrorData = errorData;
        Method = method;
    }

    /// <summary>JSON-RPC error code (or HTTP / ABCI code for REST APIs).</summary>
    public long Code { get; }

    /// <summary>The node's error message.</summary>
    public string RpcMessage { get; }

    /// <summary>Raw <c>data</c> field of the error, when present (e.g. EVM revert data).</summary>
    public string? ErrorData { get; }

    /// <summary>The RPC method that failed.</summary>
    public string? Method { get; }
}
