# Chain

[← Dokumentasi](index.md) · [English](../en/chains.md)

- [Ethereum dan EVM](#ethereum-dan-evm)
- [Bitcoin](#bitcoin)
- [Solana](#solana)
- [Polkadot dan Substrate](#polkadot-dan-substrate)
- [Cosmos SDK](#cosmos-sdk)

---

## Ethereum dan EVM

Paket `Crypto.Net.Evm`. Jaringan: `EvmChain.Ethereum`, `Sepolia`, `Hoodi`, `Polygon`, `Arbitrum`, `Optimism`,
`Base`, `BnbChain`, `Avalanche`, `Localhost` (Anvil/Hardhat, chain id 31337).

### Transaksi

```csharp
var tx = new EvmTransaction                       // EIP-1559 secara default
{
    ChainId = EvmChain.Sepolia.NumericChainId,
    Nonce = 7,
    MaxPriorityFeePerGas = 1_500_000_000,
    MaxFeePerGas = 30_000_000_000,
    GasLimit = 21_000,
    To = "0x…",
    Value = Amount.FromEther(0.01m).BaseUnits,
};
SignedEvmTransaction signed = tx.Sign(privateKey);   // atau await tx.SignAsync(signer)
Console.WriteLine(signed.RawHex);                     // payload eth_sendRawTransaction
Console.WriteLine(signed.Hash);
```

`Type = EvmTransactionType.Legacy` menandatangani dengan proteksi replay EIP-155 (atau pra-155 jika `ChainId = 0`);
`AccessList` didukung untuk EIP-2930 dan EIP-1559. `EvmTransaction.DecodeSigned(raw)` mem-parse transaksi bertanda
tangan apa pun dan `RecoverSender()` mengembalikan pengirimnya. Tanda tangan bersifat low-S (EIP-2).

`EvmRpcClient.PrepareTransactionAsync(from, to, value, data)` mengisi nonce, fee (2 × base fee + tip) dan gas
(+20 %), sedangkan `TransferAsync` / `SendTransactionAsync` menandatangani dan mem-broadcast.

### ABI

```csharp
byte[] call = EvmAbi.EncodeFunctionCall("transfer(address to, uint256 amount)", recipient, 1_000_000);
object?[] outputs = await client.CallFunctionAsync(token, "balanceOf(address)", "uint256", [owner]);
byte[] topic0 = EvmAbi.GetEventTopic("Transfer(address,address,uint256)");
object?[] values = EvmAbi.Decode("address,uint256,(uint16,bool)[]", data);
```

Tipe yang didukung: `uintN`, `intN`, `address`, `bool`, `bytesN`, `bytes`, `string`, array tetap dan dinamis, serta
tuple bersarang.

### Tanda tangan pesan

```csharp
byte[] sig = EvmMessageSigner.SignPersonalMessage(key, "halo");                 // EIP-191, v = 27/28
string who = EvmMessageSigner.RecoverPersonalMessageSigner("halo", HexUtil.Encode(sig));
byte[] digest = EvmMessageSigner.HashTypedData(typedDataJson);                   // EIP-712 (eth_signTypedData_v4)
byte[] typedSig = EvmMessageSigner.SignTypedData(key, typedDataJson);
```

### ERC-20

```csharp
var usdc = new Erc20Client(client, "0x…");
Amount bal = await usdc.GetBalanceAsync(owner);                 // diskalakan dengan decimals()
TxHash h = await usdc.TransferAsync(signer, recipient, Amount.Parse("12.5", 6));
```

---

## Bitcoin

Paket `Crypto.Net.Bitcoin`. Jaringan: `BitcoinNetwork.Mainnet`, `Testnet`, `Signet`, `Regtest`.

### Alamat

```csharp
BitcoinAddress.FromPublicKey(pub33, BitcoinAddressType.TaprootP2TR, BitcoinNetwork.Mainnet); // bc1p… (BIP-86)
BitcoinAddressInfo info = BitcoinAddress.Parse("bc1q…");     // Type, Payload, ScriptPubKey
BitcoinAddress.FromScript(redeemScript, BitcoinAddressType.SegWitP2WSH);
string wif = BitcoinAddress.WifFromPrivateKey(key);
```

Alamat Taproot memakai tweak BIP-341 tanpa script tree (BIP-86), sama dengan Sparrow, descriptor Bitcoin Core
`tr(KEY)` dan hardware wallet.

### Membangun dan menandatangani

```csharp
using var account = wallet.GetBitcoinAccount(BitcoinAddressType.SegWitP2WPKH, 0, BitcoinNetwork.Testnet);
await using var esplora = new EsploraClient(BitcoinNetwork.Testnet);

// Satu panggilan: UTXO → coin selection → kembalian → tanda tangan → broadcast
TxHash id = await esplora.TransferAsync(account, "tb1q…", amountSats: 50_000, feeRateSatPerVb: 3);

// Atau manual
var utxos = await esplora.GetUtxosAsync(account.Address.Value);
var pick = CoinSelector.Select(utxos, 50_000, feeRateSatPerVb: 3m, account.Type);
var tx = new BitcoinTransaction();
foreach (var u in pick.Inputs) tx.AddInput(u.TxId, u.Vout);
tx.AddOutput("tb1q…", 50_000, BitcoinNetwork.Testnet);
if (pick.ChangeSats > 0) tx.AddOutput(account.ScriptPubKey, pick.ChangeSats);
account.Sign(tx, pick.Inputs);
Console.WriteLine($"{tx.TxId} {tx.VirtualSize} vB");
```

Input yang membelanjakan P2PKH (sighash legacy), P2WPKH (BIP-143) dan P2TR key-path (BIP-341, SIGHASH_DEFAULT,
Schnorr) ditandatangani otomatis sesuai script output yang dibelanjakan. Input memakai sequence `0xFFFFFFFD` (RBF).
`EsploraClient.GetFeeRateAsync(targetBlocks)` membaca `/fee-estimates`.

---

## Solana

Paket `Crypto.Net.Solana`. Cluster: `SolanaChain.MainnetBeta`, `Devnet`, `Testnet`, `Localnet`.

```csharp
using var sol = wallet.GetSolanaAccount(0, SolanaChain.Devnet);          // m/44'/501'/0'/0' (Phantom)
await using var rpc = new SolanaRpcClient(SolanaChain.Devnet);
await rpc.RequestAirdropAsync(sol.Address.Value, 1_000_000_000);         // hanya devnet
TxHash sig = await rpc.TransferAsync(sol, "9xQe…", Amount.FromSol(0.25m), priorityFeeMicroLamports: 1_000);
TxHash spl = await rpc.TransferTokenAsync(sol, mint, recipient, Amount.Parse("5", 6));   // membuat ATA bila perlu
```

Membangun transaksi apa pun:

```csharp
var (blockhash, _) = await rpc.GetLatestBlockhashAsync();
var tx = new SolanaTransaction(sol.Address.Value, blockhash)
    .Add(ComputeBudgetProgram.SetComputeUnitPrice(1_000))
    .Add(SystemProgram.Transfer(sol.Address.Value, other, 5_000))
    .Add(MemoProgram.Memo("dibayar dengan Crypto.Net", sol.Address.Value));
sol.Sign(tx);
var (error, logs) = await rpc.SimulateTransactionAsync(tx.Serialize());
```

Akun dideduplikasi dan diurutkan (fee payer, signer writable, signer read-only, writable, read-only).
`SolanaAddress.FindProgramAddress(seeds, programId)` dan `GetAssociatedTokenAddress(owner, mint)` menurunkan PDA.
Kunci bisa diimpor/ekspor dalam format Phantom (Base58, 64 byte) dan solana-cli (array JSON).

---

## Polkadot dan Substrate

Paket `Crypto.Net.Polkadot`. Jaringan: `PolkadotChain.Polkadot`, `PolkadotAssetHub`, `Kusama`, `KusamaAssetHub`,
`Westend`, `WestendAssetHub`, `Paseo`, `Local`.

### Akun

Dompet Substrate menurunkan kunci dari **entropi** mnemonic (substrate-bip39), bukan dari seed BIP-39, dan memakai
junction `//hard` dan `/soft`. Crypto.Net mengikuti Polkadot.js persis:

```csharp
using var root  = PolkadotAccount.FromMnemonic(phrase);                  // sama dengan Polkadot.js/Talisman/SubWallet
using var child = PolkadotAccount.FromMnemonic(phrase, "//polkadot//0");
using var alice = PolkadotAccount.FromSuri("//Alice", chain: PolkadotChain.Westend);   // akun dev
using var ed    = PolkadotAccount.FromMnemonic(phrase, "//0", SignatureScheme.Ed25519);
string kusamaAddress = root.AddressFor(PolkadotChain.Kusama);           // kunci sama, prefix 2
```

### Transfer dan call apa pun

```csharp
await using var rpc = new PolkadotRpcClient(PolkadotChain.WestendAssetHub);
SubstrateAccountInfo info = await rpc.GetAccountInfoAsync(root.Address.Value);   // free, reserved, frozen, nonce
TxHash h = await rpc.TransferAsync(root, dest, Amount.Parse("1.5", 12));          // Balances.transfer_keep_alive

RuntimeMetadata md = await rpc.GetMetadataAsync();
byte[] call = Extrinsic.Remark(md, "halo"u8);
TxHash r = await rpc.SubmitCallAsync(root, call);
```

Indeks pallet dan call serta daftar signed extension diambil dari metadata runtime live (V14/V15), sehingga transfer
tetap berfungsi setelah upgrade runtime. Extrinsic bersifat mortal (64 blok) secara default. Extension yang membawa
data yang tidak bisa diisi Crypto.Net melempar `NotSupportedException` alih-alih menghasilkan extrinsic tidak valid.
`GetReceiptAsync` melaporkan *inklusi* (ditemukan di salah satu dari 50 blok terakhir); keberhasilan dispatch
memerlukan pembacaan `System.Events` — periksa explorer seperti Subscan.

---

## Cosmos SDK

Paket `Crypto.Net.Cosmos`. Jaringan: `CosmosChain.CosmosHub` (+ `CosmosHubTestnet`), `Osmosis`
(+ `OsmosisTestnet`), `Local`.

```csharp
using var atom = wallet.GetCosmosAccount(0, CosmosChain.CosmosHubTestnet);    // m/44'/118'/0'/0/0
await using var rest = new CosmosRestClient(CosmosChain.CosmosHubTestnet);
TxHash h = await rest.TransferAsync(atom, "cosmos1…", Amount.FromAtom(0.5m), memo: "terima kasih");

var builder = new CosmosTxBuilder { Memo = "stake" }
    .Add(CosmosMessage.Delegate(atom.Address.Value, "cosmosvaloper1…", new Coin("uatom", 1_000_000)));
TxHash d = await rest.SignAndBroadcastAsync(atom, builder);       // simulasi gas (×1,3), membayar GasPrice
```

Transaksi berupa protobuf `TxRaw` yang ditandatangani dalam `SIGN_MODE_DIRECT`. `CosmosAddress.ConvertPrefix(address, "osmo")`
mengodekan ulang akun untuk chain lain. Untuk chain dengan coin type berbeda (mis. 60 untuk kunci gaya Evmos), isi
`coinType` pada `GetCosmosAccount`.
