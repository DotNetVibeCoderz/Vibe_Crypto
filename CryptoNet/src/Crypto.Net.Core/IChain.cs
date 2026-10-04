namespace Crypto.Net.Core;

/// <summary>
/// Interface describing basic parameters of a blockchain.
/// </summary>
public interface IChain
{
    ChainId Id { get; }
    string Name { get; }
    string Symbol { get; }
    int Decimals { get; }
    bool IsTestnet { get; }
}

public sealed record ChainDescriptor(
    ChainId Id,
    string Name,
    string Symbol,
    int Decimals,
    bool IsTestnet = false
) : IChain;
