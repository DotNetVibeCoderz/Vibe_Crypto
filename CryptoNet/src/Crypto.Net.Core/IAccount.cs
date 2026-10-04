namespace Crypto.Net.Core;

/// <summary>
/// Represents a blockchain account identity with associated public address and public key.
/// </summary>
public interface IAccount
{
    IChain Chain { get; }
    Address Address { get; }
    ReadOnlyMemory<byte> PublicKey { get; }
}

public sealed record Account(
    IChain Chain,
    Address Address,
    ReadOnlyMemory<byte> PublicKey
) : IAccount;
