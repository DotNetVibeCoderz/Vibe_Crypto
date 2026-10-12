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

### NFT (ERC-721 / ERC-1155)

```csharp
var nft = new Erc721Client(client, "0x…");
if (await nft.SupportsInterfaceAsync(InterfaceIds.Erc721)) Console.WriteLine(await nft.GetOwnerAsync(tokenId));
string uri = await new Erc1155Client(client, "0x…").GetUriAsync(id);     // {id} diganti sesuai ERC-1155
var royalty = await nft.GetRoyaltyAsync(tokenId, salePrice);            // EIP-2981, null bila tidak didukung
```

`Erc721Client.TransferEvent` (token id indexed) dan `Erc1155Client.TransferSingleEvent`/`TransferBatchEvent` bisa
langsung dipakai dengan [indexer](indexer.md).

### ENS

```csharp
var ens = new EnsClient(mainnet);
string? address = await ens.ResolveAddressAsync("vitalik.eth");
string? name = await ens.LookupAddressAsync(address!);          // reverse record, diverifikasi ke arah forward
string? avatar = await ens.GetTextAsync("vitalik.eth", "avatar");
```

`EnsClient.NameHash` mengimplementasikan EIP-137. Normalisasi mengecilkan huruf dan menerapkan NFC. Nama yang memerlukan
ENSIP-15 penuh (emoji, karakter mirip), dan resolver off-chain CCIP-read, berada di luar cakupannya.

### Account abstraction (ERC-4337)

```csharp
var op = new UserOperation
{
    Sender = await SimpleAccount.GetAddressAsync(client, owner, salt: 0),
    Nonce = await SimpleAccount.GetNonceAsync(client, sender, EntryPointVersion.V07),
    CallData = SimpleAccount.EncodeExecute(recipient, value, []),
    /* field gas dari bundler.EstimateGasAsync(op, …), fee dari client.EstimateFeesAsync() */
};
SimpleAccount.Sign(op, ownerKey, EntryPointVersion.V07, chainId: 11155111);
using var bundler = new BundlerClient("https://…bundler…");
string hash = await bundler.SendAsync(op, EntryPointVersion.V07);
var receipt = await bundler.GetReceiptAsync(hash);
```

`UserOperation.GetHash` mendukung EntryPoint v0.6, v0.7 (field gas packed) dan v0.8 (EIP-712). Hasilnya sama dengan
`getUserOperationHash` dari viem.

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

### Script tree Taproot

```csharp
var tree = new TaprootNode.Branch(new TaprootNode.Leaf(scriptA), new TaprootNode.Leaf(scriptB));
string address = Taproot.Address(internalXOnlyKey, tree, BitcoinNetwork.Testnet);
var spends = Taproot.LeafSpends(internalXOnlyKey, tree);      // leaf hash + control block per leaf
byte[] msg = tx.GetTaprootSigMessage(0, utxos, SigHashType.SingleAnyoneCanPay, leafHash: spends[0].LeafHash);
```

`GetTaprootSigHash` mencakup semua varian BIP-341 (ANYONECANPAY, NONE, SINGLE, script path, annex), dan
`GetLegacySigHash`/`GetSegwitV0SigHash` menerima semua tipe sighash. Semuanya diuji dengan vektor resmi BIP-341
dan 500 kasus `sighash.json` dari Bitcoin Core. `BitcoinTransaction.Parse` membaca transaksi mentah.

### PSBT dan multisig

`Psbt` mengimplementasikan BIP-174 (v0) dan BIP-370 (v2) beserta field Taproot BIP-371, mencakup semua peran.
Record unknown dan proprietary dipertahankan, dan PSBT yang tidak diubah diserialisasi ulang identik byte demi byte.

```csharp
byte[] script = BitcoinScript.Multisig(2, [pubA, pubB, pubC]);              // sortedmulti (BIP-67)
string addr = BitcoinAddress.FromScript(script, BitcoinAddressType.SegWitP2WSH, BitcoinNetwork.Testnet).Value;

var psbt = Psbt.Create(unsignedTx)                                          // creator
    .Update(witnessScripts: [script], previousTransactions: [fundingTx],     // updater
            keyOrigins: [PsbtKeyOrigin.Create(pubA, fingerprint, "m/48'/1'/0'/2'/0/0"), ...]);
string untukCosigner = psbt.ToBase64();

var milikSaya = Psbt.Parse(untukCosigner);
milikSaya.Sign(wallet.MasterKey);                     // menandatangani input yang origin BIP-32-nya cocok
var semua = Psbt.Combine(milikSaya, Psbt.Parse(milikMereka)); // combiner
semua.Finalize();                                     // finalizer
BitcoinTransaction signed = semua.ExtractTransaction();
```

Signer menangani P2PKH, P2WPKH, P2SH-P2WPKH, multisig bare/P2SH/P2WSH/P2SH-P2WSH, serta Taproot key path dan
script path. Signer menerapkan pemeriksaan BIP-174: UTXO harus cocok dengan txid, script harus ber-hash sama dengan
output yang dibelanjakan, dan witness UTXO ditolak pada input non-witness. `Psbt.CreateV2()` dengan
`AddInput`/`AddOutput` membangun PSBT v2, termasuk aturan locktime BIP-370. `ConvertTo(0|2)` mengonversi antarversi.
`BitcoinAddress.FromScriptNestedSegwit` menghasilkan alamat `sh(wsh(...))`.
Semua vektor resmi BIP-174 dan BIP-370 lolos.

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

### Transaksi versioned dan lookup table

```csharp
// Buat tabel, lalu isi dengan akun yang sering dipakai
ulong slot = await rpc.GetBlockNumberAsync();
var (create, tableAddress) = AddressLookupTableProgram.Create(sol.Address.Value, sol.Address.Value, slot);
var extend = AddressLookupTableProgram.Extend(tableAddress, sol.Address.Value, recipients, sol.Address.Value);

// Pakai: akun non-signer yang ada di tabel dimuat lewat indeks (message v0)
var table = await rpc.GetAddressLookupTableAsync(tableAddress);
var tx = new SolanaTransaction(sol.Address.Value, blockhash).WithLookupTables(table!);
foreach (var r in recipients) tx.Add(SystemProgram.Transfer(sol.Address.Value, r, 1_000));
sol.Sign(tx);

var (signatures, message) = SolanaMessage.ParseTransaction(tx.Serialize());   // decode legacy atau v0
IReadOnlyList<string> keys = message.ResolveAccountKeys([table!]);
```

Message v0 dikompilasi persis seperti `compileToV0Message` di `@solana/web3.js`. Signer dan program id tetap statis.
`Freeze`, `Deactivate` dan `Close` melengkapi program lookup table.

### Token-2022

```csharp
TokenMint? mint = await rpc.GetMintAsync("2b1kV6DkPAnxd5ixfnxCpjxmKwqjjaYmCZfHsFu24GXo");   // PYUSD
var fee = mint!.TransferFeeConfig?.ForEpoch(currentEpoch).Calculate(amount) ?? 0;
Console.WriteLine($"{mint.Metadata?.Name} ({mint.Metadata?.Symbol}), fee {fee}");
tx.Add(Token2022.TransferCheckedWithFee(sourceAta, mintAddress, destinationAta, owner, amount, mint.Decimals, fee));
```

`TokenMint` dan `TokenAccountData` mem-parse kedua program token. Semua extension TLV didaftar. Yang punya accessor
bertipe: transfer fee, metadata pointer, metadata token on-chain, permanent delegate, close authority, transfer hook,
suku bunga dan non-transferable.

Builder berikut menghasilkan byte yang identik dengan `@solana/spl-token`: `InitializeMint2`,
`InitializeTransferFeeConfig`, `InitializeMetadataPointer`, `InitializePermanentDelegate`,
`InitializeNonTransferableMint` dan `InitializeMetadata`. `Token2022.MintSpace(...)` menghitung ukuran akun.

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
`GetReceiptAsync` mencari di 50 blok terakhir. Method ini men-decode `System.Events` pada blok tersebut, sehingga
`IsSuccess` mencerminkan hasil dispatch, dan `ErrorMessage` menyebut error-nya (`Balances.InsufficientBalance`,
`Token.Frozen`, ...).

### Event dan storage

```csharp
var events = await client.GetEventsAsync(blockHash);              // semua event dalam blok
foreach (var e in events.Where(e => e.Is("Balances", "Transfer")))
    Console.WriteLine($"{e["from"]} -> {e["to"]}: {e["amount"]}");

object? now = await client.GetStorageAsync("Timestamp", "Now");   // item storage apa pun, di-decode dari metadata
byte[] key = metadata.StorageKey("System", "Account", accountId); // hasher twox/blake2 diterapkan per key
```

`RuntimeMetadata` menyimpan type registry lengkap, entri storage, serta enum event/error tiap pallet.
`ScaleDecoder.Decode(metadata, typeId, bytes)` mengubah nilai SCALE apa pun menjadi dictionary, `ScaleVariant`, array
dan angka. Metadata diambil per blok dan di-cache per spec version, sehingga blok lama di-decode dengan runtime-nya sendiri.

### Call apa pun lewat nama, staking dan pool

```csharp
byte[] call = ScaleEncoder.EncodeCall(md, "Balances", "transfer_keep_alive", new { dest = "15oF4…", value = planck });
byte[] nominate = PolkadotStaking.Nominate(md, ["1zugca…", "14Ns6…"]);       // Staking kini ada di Asset Hub
byte[] join = PolkadotStaking.JoinPool(md, amount, poolId: 12);
var ctx = await client.GetExtrinsicContextAsync(account.Address.Value);
await client.SendRawTransactionAsync(Extrinsic.Sign(md, nominate, account, ctx));
```

`ScaleEncoder` meng-encode nilai apa pun berdasarkan type registry runtime:

- **Composite** menerima dictionary atau objek dengan property yang cocok.
- **Enum** menerima `ScaleVariant`, atau nama variant untuk variant tanpa field.
- **Account id dan `MultiAddress`** menerima string SS58.
- **`RuntimeCall` bersarang** (untuk `Utility.batch_all` atau proxy) ditulis sebagai `ScaleVariant` pallet yang
  membungkus `ScaleVariant` call.

`PolkadotStaking` membungkus call berikut: bond, bond_extra, nominate, unbond, withdraw_unbonded, chill dan set_payee.
Untuk nomination pool ia membungkus join, bond_extra, claim_payout, unbond dan withdraw_unbonded.

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

### CosmWasm

```csharp
await using var rest = new CosmosRestClient(CosmosChain.Osmosis);
JsonElement state = await rest.QueryContractAsync(contract, new { get_count = new { } });
var cw20 = new Cw20Client(rest, tokenContract);
BigInteger balance = await cw20.GetBalanceAsync(osmo.Address.Value);
var tx = new CosmosTxBuilder()
    .Add(CosmWasm.Execute(osmo.Address.Value, contract, new { increment = new { } }, new Coin("uosmo", 1000)))
    .Add(cw20.Transfer(osmo.Address.Value, recipient, 5_000));
await rest.SignAndBroadcastAsync(osmo, tx);
```

`MsgExecuteContract`, `MsgInstantiateContract`, `MsgMigrateContract` dan `MsgUpdateAdmin` di-encode persis seperti
cosmjs-types. Pesan kontrak bisa berupa objek (diserialisasi menjadi JSON ringkas) atau string JSON mentah. Pakai
`QueryContractRawAsync` untuk membaca storage mentah, dan `GetContractInfoAsync` / `GetContractsByCodeAsync` untuk
metadata kode.
