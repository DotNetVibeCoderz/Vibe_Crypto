// The C# behind each demo. Every snippet compiles against the Crypto.Net packages.
window.CODE_SAMPLES = {
  wallet: `using Crypto.Net.Wallet;
using Crypto.Net.Evm;
using Crypto.Net.Bitcoin;
using Crypto.Net.Solana;
using Crypto.Net.Polkadot;
using Crypto.Net.Cosmos;

// 12 words of entropy; back them up before doing anything else
using var wallet = HdWallet.Generate(wordCount: 12);
Console.WriteLine(wallet.RevealMnemonic());

using var eth  = wallet.GetEvmAccount(index: 0, EvmChain.Sepolia);                   // m/44'/60'/0'/0/0
using var btc  = wallet.GetBitcoinAccount(BitcoinAddressType.SegWitP2WPKH, 0, BitcoinNetwork.Testnet); // BIP-84
using var tap  = wallet.GetBitcoinAccount(BitcoinAddressType.TaprootP2TR, 0, BitcoinNetwork.Testnet);  // BIP-86
using var sol  = wallet.GetSolanaAccount(index: 0, SolanaChain.Devnet);               // m/44'/501'/0'/0'
using var dot  = wallet.GetPolkadotAccount(path: "", PolkadotChain.Westend);          // sr25519 root
using var atom = wallet.GetCosmosAccount(index: 0, CosmosChain.CosmosHubTestnet);     // m/44'/118'/0'/0/0

Console.WriteLine($"{eth.Address}\\n{btc.Address}\\n{tap.Address}\\n{sol.Address}\\n{dot.Address}\\n{atom.Address}");`,

  inspect: `using Crypto.Net.Extensions;
using Crypto.Net.Evm;
using Crypto.Net.Bitcoin;

foreach (var match in ChainCatalog.DetectAddress(address))
    Console.WriteLine($"{match.Family}: {match.Format}");

bool ok   = EvmAddress.IsValid("0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed"); // EIP-55 enforced for mixed case
var info  = BitcoinAddress.Parse("bc1q…", BitcoinNetwork.Mainnet);              // type, payload, scriptPubKey
string ss = Crypto.Net.Polkadot.Ss58Address.Convert(polkadotAddress, newPrefix: 2); // same key on Kusama`,

  convert: `using Crypto.Net.Core;

Amount fee = Amount.Parse("0.000021", decimals: 18);        // exact; throws instead of rounding
Console.WriteLine(fee.BaseUnits);                             // 21000000000000
Console.WriteLine(UnitConverter.Convert("1.5", "ether", "gwei")); // 1500000000

Amount total = Amount.FromEther(1.25m) + Amount.FromGwei(21_000);
Console.WriteLine(total.ToString("ETH"));`,

  sign: `using Crypto.Net.Evm;
using Crypto.Net.Wallet;
using Crypto.Net.Polkadot;

using var wallet = HdWallet.Generate();
using var eth = wallet.GetEvmAccount();

// EIP-191 personal_sign (MetaMask compatible), then recover the signer
byte[] sig = eth.SignMessage("Hello from Crypto.Net");
string who = EvmMessageSigner.RecoverPersonalMessageSigner("Hello from Crypto.Net"u8, sig);

// EIP-712 typed data (eth_signTypedData_v4 JSON)
byte[] typed = eth.SignTypedData(typedDataJson);

// sr25519, wrapped like the Polkadot.js extension's signRaw
using var dot = wallet.GetPolkadotAccount();
byte[] srSig = dot.SignMessage("Hello from Crypto.Net");
bool valid = PolkadotAccount.VerifyMessage("Hello from Crypto.Net", srSig, dot.PublicKey.Span);`,

  keystore: `using Crypto.Net.Evm;
using Crypto.Net.Wallet;

using var account = EvmAccount.FromPrivateKeyHex(privateKeyHex);
string json = account.ExportKeyStore("correct horse battery staple", KeyStoreKdf.Standard); // scrypt N=2^18
File.WriteAllText("UTC--keystore.json", json);

// Works with files produced by geth, MetaMask and MyEtherWallet
using var restored = EvmAccount.FromKeyStore(File.ReadAllText("UTC--keystore.json"), "correct horse battery staple");`,

  networks: `using Crypto.Net.Core;
using Crypto.Net.Extensions;
using Microsoft.Extensions.DependencyInjection;

builder.Services.AddCryptoNet(c => c
    .AddEvm("eth", o => o.Network = Crypto.Net.Evm.EvmChain.Sepolia)
    .AddSolana("sol", o => o.Cluster = Crypto.Net.Solana.SolanaChain.Devnet)
    .AddBitcoin("btc")                // testnet by default
    .AddPolkadot("dot")
    .AddCosmos("atom")
    .WithResilience());               // retries, circuit breaker, timeouts

var eth = app.Services.GetRequiredKeyedService<IChainClient>("eth");
ulong height = await eth.GetBlockNumberAsync();
Amount balance = await eth.GetBalanceAsync(new Address("0x…", ChainId.Sepolia));
await foreach (var block in eth.WatchBlocksAsync()) Console.WriteLine(block.Number);`,

  bench: `using Crypto.Net.Native;
using Crypto.Net.Extensions;

// Auto (default) uses Rust when it loads; Native forces it; Managed forces pure C#.
using (CryptoNative.UseBackend(CryptoBackend.Managed))
{
    byte[] hash = CryptoNative.Keccak256(data);   // identical bytes from both engines
}

foreach (var r in CryptoBenchmark.Run(scale: 1.0))
    Console.WriteLine($"{r.Name,-28} {r.Speedup:0.0}x");

// From a terminal:  cnet bench`,
};
