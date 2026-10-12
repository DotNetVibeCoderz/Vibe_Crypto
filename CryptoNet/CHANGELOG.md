# Changelog

All notable changes to Crypto.Net. Format: [Keep a Changelog](https://keepachangelog.com/), versions: SemVer.

## [Unreleased]

### Changed
- Polkadot `GetReceiptAsync` now reports dispatch success or failure from `System.Events`; previously a receipt only
  meant inclusion. `RuntimeMetadata.Types` is public and uses the new `ScaleTypeInfo` records.

### Added
- Polyglot notebooks for wallets, Bitcoin, Ethereum/EVM, Solana, Polkadot and Cosmos in English and
  Bahasa Indonesia (`samples/notebooks`), generated from one source and executed against the published 1.0.0 packages.
- **Bitcoin PSBT**: `Psbt` with BIP-174 (v0) and BIP-370 (v2) plus BIP-371 Taproot fields. Covers every role:
  creator, updater, signer, combiner, finalizer and extractor. Version conversion, unknown records preserved.
  Passes all official BIP-174/370 vectors.
- **Bitcoin multisig**: `BitcoinScript.Multisig` (sortedmulti), `TryParseMultisig`,
  `BitcoinAddress.FromScriptNestedSegwit`. PSBT signing and finalizing for bare, P2SH, P2WSH and P2SH-P2WSH.
- **Taproot**: script trees (`TaprootNode`, `Taproot.LeafSpends` control blocks). Full BIP-341 sighash
  (ANYONECANPAY/NONE/SINGLE, script path, annex). `SignTaprootKeyPath`. Official BIP-341 wallet vectors.
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
