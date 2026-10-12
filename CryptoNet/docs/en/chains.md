# Chains

[← Documentation](index.md) · [Bahasa Indonesia](../id/chain.md)

- [Ethereum and EVM](#ethereum-and-evm)
- [Bitcoin](#bitcoin)
- [Solana](#solana)
- [Polkadot and Substrate](#polkadot-and-substrate)
- [Cosmos SDK](#cosmos-sdk)

---

## Ethereum and EVM

Package `Crypto.Net.Evm`. Networks: `EvmChain.Ethereum`, `Sepolia`, `Hoodi`, `Polygon`, `Arbitrum`, `Optimism`,
`Base`, `BnbChain`, `Avalanche`, `Localhost` (Anvil/Hardhat, chain id 31337).

### Transactions

```csharp
var tx = new EvmTransaction                       // EIP-1559 by default
{
    ChainId = EvmChain.Sepolia.NumericChainId,
    Nonce = 7,
    MaxPriorityFeePerGas = 1_500_000_000,
    MaxFeePerGas = 30_000_000_000,
    GasLimit = 21_000,
    To = "0x…",
    Value = Amount.FromEther(0.01m).BaseUnits,
};
SignedEvmTransaction signed = tx.Sign(privateKey);   // or await tx.SignAsync(signer)
Console.WriteLine(signed.RawHex);                     // eth_sendRawTransaction payload
Console.WriteLine(signed.Hash);
```

`Type = EvmTransactionType.Legacy` signs with EIP-155 replay protection (or pre-155 when `ChainId = 0`);
`AccessList` is supported for EIP-2930 and EIP-1559. `EvmTransaction.DecodeSigned(raw)` parses any signed
transaction and `RecoverSender()` returns its sender. Signatures are low-S (EIP-2).

`EvmRpcClient.PrepareTransactionAsync(from, to, value, data)` fills nonce, fees (2 × base fee + tip) and gas
(+20 %), and `TransferAsync` / `SendTransactionAsync` sign and broadcast.

### ABI

```csharp
byte[] call = EvmAbi.EncodeFunctionCall("transfer(address to, uint256 amount)", recipient, 1_000_000);
object?[] outputs = await client.CallFunctionAsync(token, "balanceOf(address)", "uint256", [owner]);
byte[] topic0 = EvmAbi.GetEventTopic("Transfer(address,address,uint256)");
object?[] values = EvmAbi.Decode("address,uint256,(uint16,bool)[]", data);
```

Supported types: `uintN`, `intN`, `address`, `bool`, `bytesN`, `bytes`, `string`, fixed and dynamic arrays, and
nested tuples.

### Message signing

```csharp
byte[] sig = EvmMessageSigner.SignPersonalMessage(key, "hello");                // EIP-191, v = 27/28
string who = EvmMessageSigner.RecoverPersonalMessageSigner("hello", HexUtil.Encode(sig));
byte[] digest = EvmMessageSigner.HashTypedData(typedDataJson);                   // EIP-712 (eth_signTypedData_v4)
byte[] typedSig = EvmMessageSigner.SignTypedData(key, typedDataJson);
```

### ERC-20

```csharp
var usdc = new Erc20Client(client, "0x…");
Amount bal = await usdc.GetBalanceAsync(owner);                 // scaled with decimals()
TxHash h = await usdc.TransferAsync(signer, recipient, Amount.Parse("12.5", 6));
```

### NFTs (ERC-721 / ERC-1155)

```csharp
var nft = new Erc721Client(client, "0x…");
if (await nft.SupportsInterfaceAsync(InterfaceIds.Erc721)) Console.WriteLine(await nft.GetOwnerAsync(tokenId));
string uri = await new Erc1155Client(client, "0x…").GetUriAsync(id);     // {id} replaced as ERC-1155 requires
var royalty = await nft.GetRoyaltyAsync(tokenId, salePrice);            // EIP-2981, null when unsupported
```

`Erc721Client.TransferEvent` (token id indexed) and `Erc1155Client.TransferSingleEvent`/`TransferBatchEvent` plug into
the [indexer](indexer.md).

### ENS

```csharp
var ens = new EnsClient(mainnet);
string? address = await ens.ResolveAddressAsync("vitalik.eth");
string? name = await ens.LookupAddressAsync(address!);          // reverse record, verified forward
string? avatar = await ens.GetTextAsync("vitalik.eth", "avatar");
```

`EnsClient.NameHash` implements EIP-137. Normalization lower-cases and NFC-normalizes names. Names that need full
ENSIP-15 handling (emoji, confusables), and off-chain CCIP-read resolvers, are outside its scope.

### Account abstraction (ERC-4337)

```csharp
var op = new UserOperation
{
    Sender = await SimpleAccount.GetAddressAsync(client, owner, salt: 0),
    Nonce = await SimpleAccount.GetNonceAsync(client, sender, EntryPointVersion.V07),
    CallData = SimpleAccount.EncodeExecute(recipient, value, []),
    /* gas fields from bundler.EstimateGasAsync(op, …), fees from client.EstimateFeesAsync() */
};
SimpleAccount.Sign(op, ownerKey, EntryPointVersion.V07, chainId: 11155111);
using var bundler = new BundlerClient("https://…bundler…");
string hash = await bundler.SendAsync(op, EntryPointVersion.V07);
var receipt = await bundler.GetReceiptAsync(hash);
```

`UserOperation.GetHash` supports EntryPoint v0.6, v0.7 (packed gas fields) and v0.8 (EIP-712). It matches viem's
`getUserOperationHash`.

---

## Bitcoin

Package `Crypto.Net.Bitcoin`. Networks: `BitcoinNetwork.Mainnet`, `Testnet`, `Signet`, `Regtest`.

### Addresses

```csharp
BitcoinAddress.FromPublicKey(pub33, BitcoinAddressType.TaprootP2TR, BitcoinNetwork.Mainnet); // bc1p… (BIP-86)
BitcoinAddressInfo info = BitcoinAddress.Parse("bc1q…");     // Type, Payload, ScriptPubKey
BitcoinAddress.FromScript(redeemScript, BitcoinAddressType.SegWitP2WSH);
string wif = BitcoinAddress.WifFromPrivateKey(key);
```

Taproot addresses use the BIP-341 tweak with no script tree (BIP-86), matching Sparrow, Bitcoin Core descriptors
`tr(KEY)` and hardware wallets.

### Building and signing

```csharp
using var account = wallet.GetBitcoinAccount(BitcoinAddressType.SegWitP2WPKH, 0, BitcoinNetwork.Testnet);
await using var esplora = new EsploraClient(BitcoinNetwork.Testnet);

// One call: UTXOs → coin selection → change → signatures → broadcast
TxHash id = await esplora.TransferAsync(account, "tb1q…", amountSats: 50_000, feeRateSatPerVb: 3);

// Or by hand
var utxos = await esplora.GetUtxosAsync(account.Address.Value);
var pick = CoinSelector.Select(utxos, 50_000, feeRateSatPerVb: 3m, account.Type);
var tx = new BitcoinTransaction();
foreach (var u in pick.Inputs) tx.AddInput(u.TxId, u.Vout);
tx.AddOutput("tb1q…", 50_000, BitcoinNetwork.Testnet);
if (pick.ChangeSats > 0) tx.AddOutput(account.ScriptPubKey, pick.ChangeSats);
account.Sign(tx, pick.Inputs);
Console.WriteLine($"{tx.TxId} {tx.VirtualSize} vB");
```

Inputs spending P2PKH (legacy sighash), P2WPKH (BIP-143) and P2TR key-path (BIP-341, SIGHASH_DEFAULT, Schnorr)
are signed automatically according to the spent output's script. Inputs use sequence `0xFFFFFFFD` (RBF).
`EsploraClient.GetFeeRateAsync(targetBlocks)` reads `/fee-estimates`.

### Taproot script trees

```csharp
var tree = new TaprootNode.Branch(new TaprootNode.Leaf(scriptA), new TaprootNode.Leaf(scriptB));
string address = Taproot.Address(internalXOnlyKey, tree, BitcoinNetwork.Testnet);
var spends = Taproot.LeafSpends(internalXOnlyKey, tree);      // leaf hash + control block per leaf
byte[] msg = tx.GetTaprootSigMessage(0, utxos, SigHashType.SingleAnyoneCanPay, leafHash: spends[0].LeafHash);
```

`GetTaprootSigHash` covers every BIP-341 variant (ANYONECANPAY, NONE, SINGLE, script path, annex), and
`GetLegacySigHash`/`GetSegwitV0SigHash` accept every sighash type. They are checked against the official BIP-341
wallet vectors and Bitcoin Core's 500 `sighash.json` cases. `BitcoinTransaction.Parse` reads raw transactions.

### PSBT and multisig

`Psbt` implements BIP-174 (v0) and BIP-370 (v2) with the BIP-371 Taproot fields, covering every role.
Unknown and proprietary records are preserved, and an unmodified PSBT re-serializes byte for byte.

```csharp
byte[] script = BitcoinScript.Multisig(2, [pubA, pubB, pubC]);              // sortedmulti (BIP-67)
string addr = BitcoinAddress.FromScript(script, BitcoinAddressType.SegWitP2WSH, BitcoinNetwork.Testnet).Value;

var psbt = Psbt.Create(unsignedTx)                                          // creator
    .Update(witnessScripts: [script], previousTransactions: [fundingTx],     // updater
            keyOrigins: [PsbtKeyOrigin.Create(pubA, fingerprint, "m/48'/1'/0'/2'/0/0"), ...]);
string forCosigner = psbt.ToBase64();

var mine = Psbt.Parse(forCosigner);
mine.Sign(wallet.MasterKey);                     // signs every input whose BIP-32 origin matches this wallet
var all = Psbt.Combine(mine, Psbt.Parse(theirs)); // combiner
all.Finalize();                                  // finalizer
BitcoinTransaction signed = all.ExtractTransaction();
```

The signer handles P2PKH, P2WPKH, P2SH-P2WPKH, bare/P2SH/P2WSH/P2SH-P2WSH multisig, and Taproot key and script
paths. It enforces the BIP-174 signer checks: the UTXO must match the txid, scripts must hash to the spent output,
and a witness UTXO is refused on a non-witness input. `Psbt.CreateV2()` with `AddInput`/`AddOutput` builds v2
PSBTs, including the BIP-370 locktime rules. `ConvertTo(0|2)` converts between versions.
`BitcoinAddress.FromScriptNestedSegwit` produces `sh(wsh(...))` addresses.
All official BIP-174 and BIP-370 vectors pass.

---

## Solana

Package `Crypto.Net.Solana`. Clusters: `SolanaChain.MainnetBeta`, `Devnet`, `Testnet`, `Localnet`.

```csharp
using var sol = wallet.GetSolanaAccount(0, SolanaChain.Devnet);          // m/44'/501'/0'/0' (Phantom)
await using var rpc = new SolanaRpcClient(SolanaChain.Devnet);
await rpc.RequestAirdropAsync(sol.Address.Value, 1_000_000_000);         // devnet only
TxHash sig = await rpc.TransferAsync(sol, "9xQe…", Amount.FromSol(0.25m), priorityFeeMicroLamports: 1_000);
TxHash spl = await rpc.TransferTokenAsync(sol, mint, recipient, Amount.Parse("5", 6));   // creates the ATA if needed
```

Build arbitrary transactions:

```csharp
var (blockhash, _) = await rpc.GetLatestBlockhashAsync();
var tx = new SolanaTransaction(sol.Address.Value, blockhash)
    .Add(ComputeBudgetProgram.SetComputeUnitPrice(1_000))
    .Add(SystemProgram.Transfer(sol.Address.Value, other, 5_000))
    .Add(MemoProgram.Memo("paid with Crypto.Net", sol.Address.Value));
sol.Sign(tx);
var (error, logs) = await rpc.SimulateTransactionAsync(tx.Serialize());
```

Accounts are de-duplicated and ordered (fee payer, writable signers, read-only signers, writable, read-only).
`SolanaAddress.FindProgramAddress(seeds, programId)` and `GetAssociatedTokenAddress(owner, mint)` derive PDAs.
Keys import/export in Phantom (Base58, 64 bytes) and solana-cli (JSON array) formats.

### Versioned transactions and lookup tables

```csharp
// Create a table, then extend it with the accounts you reuse
ulong slot = await rpc.GetBlockNumberAsync();
var (create, tableAddress) = AddressLookupTableProgram.Create(sol.Address.Value, sol.Address.Value, slot);
var extend = AddressLookupTableProgram.Extend(tableAddress, sol.Address.Value, recipients, sol.Address.Value);

// Use it: non-signer accounts found in the table are loaded by index (v0 message)
var table = await rpc.GetAddressLookupTableAsync(tableAddress);
var tx = new SolanaTransaction(sol.Address.Value, blockhash).WithLookupTables(table!);
foreach (var r in recipients) tx.Add(SystemProgram.Transfer(sol.Address.Value, r, 1_000));
sol.Sign(tx);

var (signatures, message) = SolanaMessage.ParseTransaction(tx.Serialize());   // decode legacy or v0
IReadOnlyList<string> keys = message.ResolveAccountKeys([table!]);
```

v0 messages compile exactly as `@solana/web3.js` `compileToV0Message` does. Signers and program ids stay static.
`Freeze`, `Deactivate` and `Close` complete the lookup table program.

### Token-2022

```csharp
TokenMint? mint = await rpc.GetMintAsync("2b1kV6DkPAnxd5ixfnxCpjxmKwqjjaYmCZfHsFu24GXo");   // PYUSD
var fee = mint!.TransferFeeConfig?.ForEpoch(currentEpoch).Calculate(amount) ?? 0;
Console.WriteLine($"{mint.Metadata?.Name} ({mint.Metadata?.Symbol}), fee {fee}");
tx.Add(Token2022.TransferCheckedWithFee(sourceAta, mintAddress, destinationAta, owner, amount, mint.Decimals, fee));
```

`TokenMint` and `TokenAccountData` parse both token programs. All TLV extensions are listed. These have typed
accessors: transfer fee, metadata pointer, on-chain token metadata, permanent delegate, close authority, transfer
hook, interest rate and non-transferable.

The following builders produce bytes identical to `@solana/spl-token`: `InitializeMint2`,
`InitializeTransferFeeConfig`, `InitializeMetadataPointer`, `InitializePermanentDelegate`,
`InitializeNonTransferableMint` and `InitializeMetadata`. `Token2022.MintSpace(...)` sizes the account.

---

## Polkadot and Substrate

Package `Crypto.Net.Polkadot`. Networks: `PolkadotChain.Polkadot`, `PolkadotAssetHub`, `Kusama`, `KusamaAssetHub`,
`Westend`, `WestendAssetHub`, `Paseo`, `Local`.

### Accounts

Substrate wallets derive keys from the mnemonic's **entropy** (substrate-bip39), not the BIP-39 seed, and use
`//hard` and `/soft` junctions. Crypto.Net follows Polkadot.js exactly:

```csharp
using var root  = PolkadotAccount.FromMnemonic(phrase);                  // same as Polkadot.js/Talisman/SubWallet
using var child = PolkadotAccount.FromMnemonic(phrase, "//polkadot//0");
using var alice = PolkadotAccount.FromSuri("//Alice", chain: PolkadotChain.Westend);   // dev account
using var ed    = PolkadotAccount.FromMnemonic(phrase, "//0", SignatureScheme.Ed25519);
string kusamaAddress = root.AddressFor(PolkadotChain.Kusama);           // same key, prefix 2
```

### Transfers and arbitrary calls

```csharp
await using var rpc = new PolkadotRpcClient(PolkadotChain.WestendAssetHub);
SubstrateAccountInfo info = await rpc.GetAccountInfoAsync(root.Address.Value);   // free, reserved, frozen, nonce
TxHash h = await rpc.TransferAsync(root, dest, Amount.Parse("1.5", 12));          // Balances.transfer_keep_alive

RuntimeMetadata md = await rpc.GetMetadataAsync();
byte[] call = Extrinsic.Remark(md, "hello"u8);
TxHash r = await rpc.SubmitCallAsync(root, call);
```

Pallet and call indices and the signed-extension list come from the live runtime metadata (V14/V15), so
transfers keep working across runtime upgrades. Extrinsics are mortal (64 blocks) by default. Extensions that carry
data Crypto.Net cannot fill raise `NotSupportedException` instead of producing an invalid extrinsic.
`GetReceiptAsync` searches the last 50 blocks. It decodes `System.Events` at that block, so `IsSuccess` reflects
dispatch, and `ErrorMessage` names the error (`Balances.InsufficientBalance`, `Token.Frozen`, ...).

### Events and storage

```csharp
var events = await client.GetEventsAsync(blockHash);              // every event in the block
foreach (var e in events.Where(e => e.Is("Balances", "Transfer")))
    Console.WriteLine($"{e["from"]} -> {e["to"]}: {e["amount"]}");

object? now = await client.GetStorageAsync("Timestamp", "Now");   // any storage item, decoded from metadata
byte[] key = metadata.StorageKey("System", "Account", accountId); // twox/blake2 hashers applied per key
```

`RuntimeMetadata` keeps the full type registry, storage entries and each pallet's event/error enums.
`ScaleDecoder.Decode(metadata, typeId, bytes)` turns any SCALE value into dictionaries, `ScaleVariant`s, arrays and
numbers. Metadata is fetched per block and cached per spec version, so historical blocks decode with their own runtime.

### Any call by name, staking and pools

```csharp
byte[] call = ScaleEncoder.EncodeCall(md, "Balances", "transfer_keep_alive", new { dest = "15oF4…", value = planck });
byte[] nominate = PolkadotStaking.Nominate(md, ["1zugca…", "14Ns6…"]);       // Staking lives on Asset Hub now
byte[] join = PolkadotStaking.JoinPool(md, amount, poolId: 12);
var ctx = await client.GetExtrinsicContextAsync(account.Address.Value);
await client.SendRawTransactionAsync(Extrinsic.Sign(md, nominate, account, ctx));
```

`ScaleEncoder` encodes any value against the runtime's type registry:

- **Composites** accept dictionaries or objects with matching properties.
- **Enums** accept a `ScaleVariant`, or the variant name for variants without fields.
- **Account ids and `MultiAddress`** accept SS58 strings.
- **Nested `RuntimeCall`s** (for `Utility.batch_all` or a proxy) are written as a pallet `ScaleVariant` that wraps
  a call `ScaleVariant`.

`PolkadotStaking` wraps these calls: bond, bond_extra, nominate, unbond, withdraw_unbonded, chill and set_payee.
For nomination pools it wraps join, bond_extra, claim_payout, unbond and withdraw_unbonded.

---

## Cosmos SDK

Package `Crypto.Net.Cosmos`. Networks: `CosmosChain.CosmosHub` (+ `CosmosHubTestnet`), `Osmosis`
(+ `OsmosisTestnet`), `Local`.

```csharp
using var atom = wallet.GetCosmosAccount(0, CosmosChain.CosmosHubTestnet);    // m/44'/118'/0'/0/0
await using var rest = new CosmosRestClient(CosmosChain.CosmosHubTestnet);
TxHash h = await rest.TransferAsync(atom, "cosmos1…", Amount.FromAtom(0.5m), memo: "thanks");

var builder = new CosmosTxBuilder { Memo = "stake" }
    .Add(CosmosMessage.Delegate(atom.Address.Value, "cosmosvaloper1…", new Coin("uatom", 1_000_000)));
TxHash d = await rest.SignAndBroadcastAsync(atom, builder);       // simulates for gas (×1.3), pays GasPrice
```

Transactions are protobuf `TxRaw` signed in `SIGN_MODE_DIRECT`. `CosmosAddress.ConvertPrefix(address, "osmo")`
re-encodes an account for another chain. For chains with a different coin type (e.g. 60 for Evmos-style keys) pass
`coinType` to `GetCosmosAccount`.

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

`MsgExecuteContract`, `MsgInstantiateContract`, `MsgMigrateContract` and `MsgUpdateAdmin` encode exactly as
cosmjs-types does. Contract messages can be objects (serialized to compact JSON) or raw JSON strings. Use
`QueryContractRawAsync` for raw storage reads, and `GetContractInfoAsync` / `GetContractsByCodeAsync` for code
metadata.
