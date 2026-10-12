# Hardware wallets (Ledger and Trezor)

> Built by Gravicode Studios, led by Kang Fadhil. · [Bahasa Indonesia](../id/hardware-wallet.md)

`Crypto.Net.Ledger` signs with a Ledger device. Keys never leave the device, and the user confirms every
transaction and message on its screen.

```bash
dotnet add package Crypto.Net.Ledger
```

```csharp
using var transport = HidLedgerTransport.Open();               // USB; unlock the device and open the Ethereum app
var eth = new LedgerEthereumApp(transport);
string path = LedgerEthereumApp.LedgerLivePath(0);              // m/44'/60'/0'/0/0

var account = await eth.GetAddressAsync(path, display: true);   // show it on the device and compare
var tx = await rpc.PrepareTransactionAsync(account.Address, "0x…", Amount.FromEther(0.01m).BaseUnits);
SignedEvmTransaction signed = await eth.SignTransactionAsync(path, tx);   // user approves on the device
await rpc.SendRawTransactionAsync(signed.RawBytes);

byte[] sig = await eth.SignPersonalMessageAsync(path, "Sign in to example.org");
byte[] typed = await eth.SignEip712HashedMessageAsync(path, domainSeparator, structHash);
```

| Piece | Purpose |
| --- | --- |
| `HidLedgerTransport` | USB HID on Windows, macOS and Linux (HidSharp). Linux needs Ledger's udev rules. |
| `SpeculosTransport` | The Speculos emulator's APDU port, for development and CI |
| `ReplayLedgerTransport` | Replays recorded `=>`/`<=` APDU sessions in unit tests |
| `LedgerEthereumApp` | Configuration, address (optionally shown on screen, with chain id), transactions, personal messages, EIP-712 |
| `LedgerSolanaApp` | Configuration, address (optionally shown on screen), legacy and v0 transactions, off-chain messages |
| `LedgerException` | Status words with meaning: `IsUserRejection`, locked device, app not open, blind signing disabled |

Transactions of any length are split into 255-byte APDUs. The split respects the EIP-155 tail of legacy
transactions. Legacy `v` values for chain ids above 109 are reconstructed correctly.

## Solana

```csharp
var sol = new LedgerSolanaApp(transport);                       // open the Solana app on the device
string path = LedgerSolanaApp.PhantomPath(0);                   // 44'/501'/0'/0'
string address = await sol.GetAddressAsync(path, display: true);
var tx = new SolanaTransaction(address, blockhash);
tx.Instructions.Add(SystemProgram.Transfer(address, "…", 1_000_000));
await sol.SignTransactionAsync(path, tx);                       // the signature is verified and attached
await rpc.SendRawTransactionAsync(tx.Serialize());

byte[] sig = await sol.SignOffchainMessageAsync(path, LedgerSolanaApp.FormatOffchainMessage("Sign in to example.org", [address]));
```

Long transactions are sent in 255-byte chunks, and every path level is hardened as the app requires.

## Trezor

`Crypto.Net.Trezor` talks to Trezor Model One, Model T and Safe 3/5 through Trezor Bridge (bundled with Trezor Suite) or
to the firmware emulator over UDP.

```bash
dotnet add package Crypto.Net.Trezor
```

```csharp
using var transport = await TrezorBridgeTransport.OpenAsync();  // Trezor Suite running, device plugged in
using var trezor = new TrezorClient(transport) { Passphrase = "" };   // null = enter the passphrase on the device
await trezor.InitializeAsync();
string path = TrezorClient.EthereumPath(0);                      // m/44'/60'/0'/0/0

string address = await trezor.GetEthereumAddressAsync(path, display: true);
SignedEvmTransaction signed = await trezor.SignEthereumTransactionAsync(path, tx);   // legacy or EIP-1559, data chunked
byte[] sig = await trezor.SignEthereumMessageAsync(path, "Sign in to example.org"u8.ToArray());
byte[] typed = await trezor.SignEthereumTypedHashAsync(path, domainSeparator, structHash);
```

Button prompts are acknowledged automatically; the user confirms on the device. Model One asks for the PIN on the
computer with a scrambled matrix: set `PinProvider`. `TrezorException.IsUserRejection` tells a cancel from an error.

## How it is tested

- Unit tests replay the APDU sessions recorded in Ledger's own `hw-app-eth` test suite, byte for byte.
- CI runs Ledger's released Ethereum app (`app-ethereum` 1.22 for Nano X) in Speculos, seeded with the public BIP-39
  test phrase. A test presses through the review screens via the Speculos API and checks three things against the
  address Crypto.Net derives in software from the same phrase: the device address, and the recovered signers of a
  personal message and of EIP-1559 and legacy transactions.
- The Solana app is replayed from `hw-app-solana`'s recorded sessions; each recorded signature verifies against the
  fee payer. CI also builds Ledger's Solana app from source with Ledger's app builder, runs it in Speculos, and checks
  that the device address equals the Phantom-path address and that the transfer and off-chain signatures verify.
- Trezor messages are byte-identical to trezorlib's protobuf encoding. CI runs Trezor's own firmware (Model T core
  emulator 2.11.0) loaded with the same phrase, presses the buttons through the debug link, and checks the address and
  the recovered signers of a personal message and of EIP-1559 and legacy transactions.

Ledger apps for other chains, Trezor chains beyond Ethereum and WalletConnect v2 are not implemented yet. WalletConnect
needs a project ID from Reown's cloud dashboard.
