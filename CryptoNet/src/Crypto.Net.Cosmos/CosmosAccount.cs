using Crypto.Net.Core;
using Crypto.Net.Native;
using Crypto.Net.Wallet;

namespace Crypto.Net.Cosmos;

/// <summary>A Cosmos SDK account (secp256k1).</summary>
public sealed class CosmosAccount : IAccount, IDisposable
{
    private readonly SecureBuffer _privateKey;

    public CosmosAccount(ReadOnlySpan<byte> privateKey, CosmosChain? chain = null, string? derivationPath = null)
    {
        _privateKey = SecureBuffer.FromBytes(privateKey);
        Network = chain ?? CosmosChain.CosmosHub;
        PublicKey = CryptoNative.Secp256k1GetPublicKey(privateKey, compressed: true);
        Address = CosmosAddress.FromPublicKey(PublicKey.Span, Network.Bech32Prefix, Network.Id);
        DerivationPath = derivationPath;
    }

    public CosmosChain Network { get; }
    public IChain Chain => Network;
    public Address Address { get; }
    public ReadOnlyMemory<byte> PublicKey { get; }
    public string? DerivationPath { get; }

    public byte[] Sign(CosmosTxBuilder builder, string chainId, ulong accountNumber, ulong sequence) =>
        builder.Sign(_privateKey.Span, chainId, accountNumber, sequence);

    public KeySigner CreateSigner() => new(Network, Address, _privateKey.Clone(), SignatureScheme.Secp256k1Ecdsa);

    public void Dispose() => _privateKey.Dispose();
}

/// <summary>HD wallet helpers (BIP-44 coin type 118 unless the chain uses another).</summary>
public static class CosmosWalletExtensions
{
    public static CosmosAccount GetCosmosAccount(this HdWallet wallet, uint index = 0, CosmosChain? chain = null, uint coinType = 118, uint account = 0)
    {
        string path = DerivationPath.Standard.Bip44(coinType, account, 0, index);
        using var key = wallet.DerivePath(path);
        return new CosmosAccount(key.PrivateKey.Span, chain, path);
    }
}
