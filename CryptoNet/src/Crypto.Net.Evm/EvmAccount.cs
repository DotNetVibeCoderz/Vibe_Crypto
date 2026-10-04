using Crypto.Net.Core;
using Crypto.Net.Native;
using Crypto.Net.Wallet;

namespace Crypto.Net.Evm;

/// <summary>An EVM account (address + public key) and a factory for its signer.</summary>
public sealed class EvmAccount : IAccount, IDisposable
{
    private readonly SecureBuffer _privateKey;

    public EvmAccount(ReadOnlySpan<byte> privateKey, EvmChain? chain = null, string? derivationPath = null)
    {
        _privateKey = SecureBuffer.FromBytes(privateKey);
        EvmChain = chain ?? EvmChain.Ethereum;
        PublicKey = CryptoNative.Secp256k1GetPublicKey(privateKey, compressed: false);
        Address = EvmAddress.FromPublicKey(PublicKey.Span, EvmChain.Id);
        DerivationPath = derivationPath;
    }

    public static EvmAccount FromPrivateKeyHex(string hex, EvmChain? chain = null)
    {
        using var key = SecureBuffer.FromHex(hex);
        return new EvmAccount(key.Span, chain);
    }

    public EvmChain EvmChain { get; }
    public IChain Chain => EvmChain;
    public Address Address { get; }
    public ReadOnlyMemory<byte> PublicKey { get; }
    public string? DerivationPath { get; }

    /// <summary>Creates an <see cref="ISigner"/> with its own copy of the key.</summary>
    public KeySigner CreateSigner() => new(EvmChain, Address, _privateKey.Clone(), SignatureScheme.Secp256k1Ecdsa);

    /// <summary>EIP-191 personal_sign.</summary>
    public byte[] SignMessage(string message) => EvmMessageSigner.SignPersonalMessage(_privateKey.Span, message);

    /// <summary>EIP-712 typed data signature.</summary>
    public byte[] SignTypedData(string typedDataJson) => EvmMessageSigner.SignTypedData(_privateKey.Span, typedDataJson);

    public SignedEvmTransaction SignTransaction(EvmTransaction transaction) => transaction.Sign(_privateKey.Span);

    /// <summary>Encrypts the key into a Web3 Secret Storage v3 keystore.</summary>
    public string ExportKeyStore(string password, KeyStoreKdf? kdf = null) =>
        KeyStore.Encrypt(_privateKey.Span, password, Address.Value, kdf);

    public static EvmAccount FromKeyStore(string json, string password, EvmChain? chain = null)
    {
        using var key = KeyStore.Decrypt(json, password);
        return new EvmAccount(key.Span, chain);
    }

    public void Dispose() => _privateKey.Dispose();
}

/// <summary>HD wallet helpers for EVM chains (BIP-44 coin type 60, as used by MetaMask).</summary>
public static class EvmWalletExtensions
{
    public static EvmAccount GetEvmAccount(this HdWallet wallet, uint index = 0, EvmChain? chain = null, uint account = 0)
    {
        string path = DerivationPath.Standard.Ethereum(index, account);
        using var key = wallet.DerivePath(path);
        return new EvmAccount(key.PrivateKey.Span, chain, path);
    }
}
