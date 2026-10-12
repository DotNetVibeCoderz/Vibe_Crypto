# Changelog

All notable changes to Crypto.Net. Format: [Keep a Changelog](https://keepachangelog.com/), versions: SemVer.

## [Unreleased]

### Added
- **Crypto.Net.Indexer**:
  - `EvmLogIndexer` pages from a checkpoint to the confirmed head with adaptive ranges and at-least-once delivery.
  - `BlockCrawler` works on any chain.
  - Checkpoint stores: in-memory and file.
  - Hosted-service registration: `AddEvmLogIndexer`, `AddBlockCrawler`.
- **Crypto.Net.Indexer.EntityFrameworkCore**:
  - `CryptoNetIndexerDbContext` / `ApplyCryptoNetIndexer`.
  - `EfCheckpointStore`.
  - `EfEvmLogStore`: idempotent raw logs and ERC-20 transfers.
- **EVM**:
  - `EvmEvent`: event signatures with `indexed` and anonymous events, plus log decoding (ERC-20/721 ready-made).
  - `EvmLogFilter` for multi-address / OR-topic `eth_getLogs`.
- **Crypto.Net.Generators**: an incremental source generator that turns build inputs into typed clients.
  - Solidity ABI / Hardhat & Foundry artifacts (`*.abi.json`) become `EvmContract` clients with views, sends,
    calldata encoders, struct records and typed events.
  - Anchor IDLs (`*.idl.json`, 0.30+ and older formats) become instruction builders, Borsh account decoders, fetchers
    and error tables.
  - `cnet gen` writes the same code to a file.
  - New `cnet-contract-client` template.
- **Solana**: `BorshWriter`/`BorshReader`, `Anchor` discriminator helpers, `SolanaRpcClient.GetAccountDataAsync`.
- **EVM**: `EvmContract` base class for typed contract clients.
- **EVM standards**:
  - `Erc721Client` / `Erc1155Client` with ERC-165 detection and EIP-2981 royalties.
  - `EnsClient`: EIP-137 namehash, forward/reverse resolution with verification, text and contenthash records.
  - ERC-4337: `UserOperation` hashing for EntryPoint v0.6/v0.7/v0.8 (matches viem), `BundlerClient`, and
    `SimpleAccount` helpers.
- **Solana Token-2022**:
  - `TokenMint` / `TokenAccountData` with TLV extensions: transfer fee, metadata, pointers, hook, interest,
    delegate.
  - Fee calculation.
  - Extension instructions identical to @solana/spl-token.
  - `GetMintAsync`.
- **CosmWasm**:
  - Execute/instantiate/migrate/update-admin messages (identical to cosmjs-types).
  - Smart/raw queries, contract info, and a `Cw20Client`.
- **Polkadot**:
  - `ScaleEncoder`: a metadata-driven encoder with `EncodeCall(pallet, call, args)` and nested calls.
  - `PolkadotStaking`: staking and nomination-pool calls. Verified by a fee quote from Polkadot Asset Hub.
- **Crypto.Net.Aptos**: AIP-40 addresses, AIP-80 keys, `m/44'/637'` accounts, BCS entry-function transactions,
  simulation, view functions and a REST client. Byte-identical to @aptos-labs/ts-sdk.
- **Crypto.Net.Sui**: `suiprivkey` keys, `m/44'/784'` accounts, programmable transactions (BCS build and parse),
  intent signing, personal messages, and a GraphQL client with gas selection and dry runs. Byte-identical to @mysten/sui.
- **Crypto.Net.Ton**:
  - Cells and bags of cells (same order as @ton/core), raw and user-friendly addresses, and TON mnemonics.
  - Wallets V4R2 and W5, with automatic deployment on the first transfer.
  - Comments, jetton transfers, TEP-467 normalized hashes, and a TON Center client.
- **Crypto.Net.Cardano**:
  - CIP-1852 accounts with BIP32-Ed25519 (Icarus) keys, account xpubs, and base/enterprise/reward/Byron addresses.
  - Conway transactions with native assets and CIP-20 messages, UTxO selection with exact fees, and a Koios client.
  - Byte-identical to cardano-serialization-lib.
- **Native core (ABI 0x00010200)**: SHA3-256, BIP32-Ed25519 derivation (private and public), and extended-key
  signing, each in Rust and in the managed fallback.
- **Core**: `BcsWriter`/`BcsReader`, `MoveAddress`, `MoveTypeTag` and `MoveArg`.
- **Wallet**: `Mnemonic.ToIcarusRootKey` and `HdWallet.DeriveBip32Ed25519`.
- `ChainCatalog`, `AddNear`/`AddAptos`/`AddSui`/`AddTon`/`AddCardano`, address detection, `cnet wallet derive` and
  the Gallery wallet all cover the five new chains.
- **Crypto.Net.WalletConnect**:
  - WalletConnect v2 Sign API on Reown's relay, for both apps and wallets.
  - Pairing URIs, session proposal and settlement, requests and events, ping and disconnect.
  - `WalletConnectEthereum` provides `personal_sign`, `eth_signTypedData_v4` and `eth_sendTransaction`.
  - Crypto matches @walletconnect/utils byte for byte. Interop was verified both ways with the official sign-client.
- **Native core**: X25519 (RFC 7748) in Rust and managed C#.
- **Crypto.Net.Trezor**:
  - Protocol v1 with Trezor Bridge, UDP (emulator) and replay transports.
  - Ethereum: addresses, legacy and EIP-1559 transactions with chunked data, personal messages, EIP-712 hashes.
  - Handles PIN and passphrase prompts. Byte-identical to trezorlib, and verified in CI on Trezor's core emulator.
- **Ledger Solana app** (`LedgerSolanaApp`): addresses, chunked legacy/v0 transactions, off-chain messages. Verified
  with hw-app-solana's recorded sessions and, in CI, against the Solana app built from source and run in Speculos.
  `SolanaTransaction.AddSignature` attaches signatures made elsewhere.
- **Crypto.Net.Ledger**:
  - APDU protocol, HID framing, and USB HID, Speculos and replay transports.
  - Ethereum app: addresses, chunked transactions, personal messages, EIP-712.
  - Verified against Ledger's recorded sessions and, in CI, against the real Ethereum app in Speculos.
- **Crypto.Net.Near**:
  - Keys (`m/44'/397'`), implicit and named accounts.
  - Borsh transactions with all eight actions (identical to near-api-js).
  - Function calls, access keys, and an RPC client.
- Borsh moved from Crypto.Net.Solana to Crypto.Net.Core (`Crypto.Net.Core.BorshWriter`/`BorshReader`), since NEAR uses
  it too. `JsonRpcClient.CallWithObjectParamsAsync` sends by-name parameters.
- **Gallery desktop and WebAssembly**:
  - `Crypto.Net.Gallery.Core` makes the Gallery API host-neutral.
  - `Crypto.Net.Gallery.Desktop` is a native window (Photino) with the server in-process on loopback.
  - `Crypto.Net.Gallery.Wasm` runs the whole Gallery in the browser on .NET WebAssembly, installable as a PWA.
    CI checks it in headless Chrome.
- **VS Code extension "Crypto.Net Tools"**:
  - Networks view.
  - Commands for wallet, balance, address, units, client generation, templates, notebooks and doctor.
  - C# hovers for units and addresses, and EIP-55 typo diagnostics.
  - Snippets and local-node launcher.
  - EN/ID localization.
  - Tested in a real VS Code in CI; the `.vsix` is attached to releases.
- Notebooks 07 (indexer to SQLite), 08 (Rust vs managed benchmarks), 09 (security practices) and 10 (NEAR, Aptos,
  Sui, TON and Cardano, with live dry runs) in EN/ID. All ten notebooks target 1.2.0 and were executed against the
  packed packages.
- `MockRpcError` in Crypto.Net.Testing lets an `OnRpc` factory answer with an error.

### Changed
- `cnet address validate --json` reports chain families by name.
- The `cnet-worker` template indexes ERC-20 transfers of `Watcher:Tokens` into SQLite.

## [1.1.0] — 2026-10-12

### Security
- Decoder fuzzing (cargo-fuzz targets and .NET mutation fuzz tests in CI) found and fixed crashes on malformed input:
  - `RuntimeMetadata.Parse`: `OverflowException` on oversized compact type ids.
  - `EvmTransaction.DecodeSigned`: overflow on large nonce, chain id or `v`, and `NullReferenceException`s on
    wrongly shaped RLP.
  - `EvmRlp`: negative 4-byte lengths, and unbounded nesting that could overflow the stack.
  - Rust `schnorr_verify` / `schnorr_sign` panicked inside k256 on keys or signatures of the wrong length. The FFI
    turned this into `CN_ERR_PANIC`; it now returns an ordinary invalid-input result.

  All of these now throw `FormatException`.
- CI runs `cargo audit`, `cargo deny`, a NuGet vulnerability scan and produces a CycloneDX SBOM.

### Changed
- Native library for `linux-musl-x64` (Alpine) is built in CI and shipped in `Crypto.Net.Native`, tested on Alpine.
- Polkadot `GetReceiptAsync` now reports dispatch success or failure from `System.Events`; previously a receipt only
  meant inclusion. `RuntimeMetadata.Types` is public and uses the new `ScaleTypeInfo` records.

### Added
- **Crypto.Net.Templates**: `dotnet new` templates.
  - The templates are `cnet-console`, `cnet-worker`, `cnet-webapi`, `cnet-lib` (with tests) and `cnet-test` (with docker-compose local nodes).
  - `cnet new` is a CLI shortcut for them.
  - `ChainCatalog.DeriveAccount` derives the standard account for any catalog network.
  - CI smoke-tests every template against freshly packed packages.
- **Crypto.Net.Analyzers**: new Roslyn package.
  - CNET001: hard-coded mnemonics, keys, WIF and xprv strings.
  - CNET002: secrets kept in strings.
  - CNET003: `Amount` built from floating point.
  - CNET004: mainnet clients in Debug builds.
  - CNET005: an available `CancellationToken` not passed to RPC calls; comes with a code fix.
  - The Gallery and the CLI now build with these analyzers.
- Polyglot notebooks for wallets, Bitcoin, Ethereum/EVM, Solana, Polkadot and Cosmos in English and
  Bahasa Indonesia (`samples/notebooks`), generated from one source and executed against the published 1.0.0 packages.
- **Bitcoin PSBT**: `Psbt` with BIP-174 (v0) and BIP-370 (v2) plus BIP-371 Taproot fields. Covers every role:
  creator, updater, signer, combiner, finalizer and extractor. Version conversion, unknown records preserved.
  Passes all official BIP-174/370 vectors.
- **Bitcoin multisig**: `BitcoinScript.Multisig` (sortedmulti), `TryParseMultisig`,
  `BitcoinAddress.FromScriptNestedSegwit`. PSBT signing and finalizing for bare, P2SH, P2WSH and P2SH-P2WSH.
- **Taproot**: script trees (`TaprootNode`, `Taproot.LeafSpends` control blocks). Full BIP-341 sighash
  (ANYONECANPAY/NONE/SINGLE, script path, annex). `SignTaprootKeyPath`. Official BIP-341 wallet vectors.
- **WebSocket subscriptions**: `WebSocketRpcClient` in Core multiplexes calls and subscriptions over one socket.
  Typed streams per chain: `SubscribeEvmHeadsAsync`/`SubscribeEvmLogsAsync`/`SubscribeEvmPendingTransactionsAsync`;
  `SubscribeSolanaSlotsAsync`/`SubscribeSolanaAccountAsync`/`SubscribeSolanaLogsAsync`/`WaitForSolanaSignatureAsync`;
  `SubscribeSubstrateHeadsAsync`/`SubscribeSubstrateFinalizedHeadsAsync`/`SubmitAndWatchExtrinsicAsync`.
- **Polkadot events**: `RuntimeMetadata` keeps the full type registry, storage entries and event/error enums.
  `ScaleDecoder` decodes any SCALE value. `SubstrateEvents` decodes `System.Events` and dispatch errors.
  `GetEventsAsync`, `GetExtrinsicEventsAsync` and `GetStorageAsync` are new. `StorageKeys` covers xxHash/twox
  and the BLAKE2 hashers. Metadata is cached per spec version and fetched at the queried block.
- **Solana v0**: versioned messages with address lookup tables. `WithLookupTables` compiles exactly as
  web3.js `compileToV0Message`. Adds the `AddressLookupTableProgram` (create/extend/freeze/deactivate/close),
  `GetAddressLookupTableAsync`, and the `SolanaMessage` decoder for legacy and v0 wire transactions.
- `BitcoinTransaction.Parse`. Legacy and BIP-143 sighash for every hash type, checked against Bitcoin Core's
  `sighash.json`.

## [1.0.0] — 2026-10-05

First public release. Built by Gravicode Studios, led by Kang Fadhil.

### Added
- **Rust core** (`cryptonet`, ABI 1.1): SHA-2, Keccak-256, BLAKE2b, RIPEMD-160, BIP-340 tagged hashes; secp256k1
  ECDSA (RFC 6979, low-S, recovery), BIP-340 Schnorr, BIP-341 Taproot tweaks, BIP-32; Ed25519 and SLIP-10;
  sr25519 and Substrate `//hard/soft` derivation; PBKDF2, scrypt; Base58(Check), Bech32/Bech32m and SegWit, RLP.
  30 Rust tests against official vectors. Panics are caught at the FFI boundary.
- **Managed fallback** for every primitive except sr25519, byte-identical to Rust (backend parity tests);
  `CryptoBackend` Auto/Native/Managed with per-async-flow overrides.
- **Wallet**: BIP-39 (entropy, validation, NFKD seeds), BIP-32/SLIP-10 HD keys, xprv/xpub/zpub, substrate-bip39,
  Web3 Secret Storage v3 keystores (scrypt/PBKDF2, AES-128-CTR), `KeySigner`, pinned zeroizing `SecureBuffer`.
- **EVM**: EIP-55, Legacy/EIP-155, EIP-2930 and EIP-1559 transactions with decoding and sender recovery, full ABI
  codec, EIP-191 and EIP-712, ERC-20 client, JSON-RPC client with fee/gas/nonce preparation.
- **Bitcoin**: P2PKH, P2SH, P2WPKH, P2WSH, P2TR (BIP-86); SegWit serialization; legacy, BIP-143 and BIP-341 signing;
  coin selection; Esplora client with UTXOs, fee estimates and one-call transfers.
- **Solana**: general legacy-message builder, System/SPL Token/ATA/Compute Budget/Memo instructions, PDAs,
  Phantom and solana-cli key formats, RPC client with simulation, airdrops and SPL transfers.
- **Polkadot**: SS58, SCALE, runtime metadata V14/V15 parser, metadata-driven signed extrinsics
  (`transfer_keep_alive`, `remark`, any call), account info, fee estimation.
- **Cosmos**: Bech32 accounts, protobuf `SIGN_MODE_DIRECT` (bank send, delegate, undelegate, withdraw rewards),
  REST client with simulation-based gas.
- **Core**: exact `Amount`, `UnitConverter`, `JsonRpcClient` with access-denied failover, `RpcException`,
  receipt polling and block watching.
- **Extensions**: `AddCryptoNet()` keyed clients, resilience, `ChainCatalog`, Ankr and dRPC providers, benchmark.
- **Tools**: `cnet` CLI (doctor, networks, wallet, keystore, address, convert, balance, block, tx, sign, verify, bench),
  the Gallery web studio (EN/ID, light/dark) and the QuickStart sample.
- Bilingual documentation (English and Bahasa Indonesia), screenshots, NuGet icon and pack/publish scripts.

### Fixed (relative to the pre-release prototype)
- SegWit and Taproot addresses were encoded incorrectly (witness version converted as data) and Taproot keys were
  not tweaked; addresses now match BIP-84/86 vectors.
- SS58 checksums used BLAKE2b-256 instead of BLAKE2b-512.
- The managed RIPEMD-160 and BLAKE2b fallbacks were incorrect; the secp256k1 and Ed25519 fallbacks were missing.
- `panic = "abort"` defeated `catch_unwind` at the FFI boundary.
- Cosmos broadcasts sent hex instead of base64; EVM signatures kept leading zeros in `r`/`s`.
- The keystore claimed version 3 but was not Web3-compatible.
- The BIP-39 test vector constant was wrong.
