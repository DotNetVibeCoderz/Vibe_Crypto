"""Generates the Crypto.Net Polyglot Notebooks (*.en.ipynb and *.id.ipynb).

Code cells are shared between languages so both editions always run the same code; only the prose differs.
Edit this file, then run:

    python samples/notebooks/_generate.py

Built by Gravicode Studios, led by Kang Fadhil.
"""
import json
from pathlib import Path

VERSION = "1.0.0"
HERE = Path(__file__).parent

ATTR = {
    "en": "*Built by Gravicode Studios, led by Kang Fadhil.*",
    "id": "*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.*",
}
SAFETY = {
    "en": "> **Safe to run.** This notebook only reads public networks and signs offline; it never broadcasts a transaction. "
          "Keys created here are demo keys — never send real funds to them.",
    "id": "> **Aman dijalankan.** Notebook ini hanya membaca jaringan publik dan menandatangani secara offline; tidak pernah "
          "mem-broadcast transaksi. Kunci yang dibuat di sini adalah kunci demo — jangan pernah mengirim dana sungguhan ke sana.",
}
RUN_HINT = {
    "en": "Open in VS Code with the **Polyglot Notebooks** extension (or Jupyter with .NET Interactive) and run the cells in order.",
    "id": "Buka di VS Code dengan ekstensi **Polyglot Notebooks** (atau Jupyter dengan .NET Interactive) lalu jalankan sel secara berurutan.",
}

INSTALL = f'#r "nuget: Crypto.Net.Extensions, {VERSION}"'


def md(en, id_):
    return ("md", {"en": en, "id": id_})


def code(src):
    return ("code", src.strip("\n"))


# ----------------------------------------------------------------------------------------------- notebooks

NOTEBOOKS = []

# ============================================================================== 01 wallet
NOTEBOOKS.append(("01-wallet-and-keys", {
    "en": "Crypto.Net · 01 — Wallets and keys", "id": "Crypto.Net · 01 — Dompet dan kunci"}, [
    md("What you will do: generate a BIP-39 recovery phrase, derive accounts on five chains from it, export extended "
       "public keys, encrypt a key into a Web3 keystore, and compare the Rust core with the managed C# engine.",
       "Yang akan Anda lakukan: membuat frasa pemulihan BIP-39, menurunkan akun di lima chain darinya, mengekspor extended "
       "public key, mengenkripsi kunci ke keystore Web3, dan membandingkan inti Rust dengan mesin C# managed."),
    code(INSTALL),
    code(r'''
using Crypto.Net.Core;
using Crypto.Net.Native;
using Crypto.Net.Wallet;
using Crypto.Net.Evm;
using Crypto.Net.Bitcoin;
using Crypto.Net.Solana;
using Crypto.Net.Polkadot;
using Crypto.Net.Cosmos;
using Crypto.Net.Extensions;

$"Rust core loaded: {NativeLoader.IsNativeAvailable} · ABI 0x{NativeLoader.AbiVersion:X8} · {NativeLoader.RuntimeIdentifier}"
'''),
    md("## Recovery phrase (BIP-39)\n\n12–24 words encode 128–256 bits of entropy plus a checksum. "
       "`Mnemonic.Validate` checks both the wordlist and the checksum.",
       "## Frasa pemulihan (BIP-39)\n\n12–24 kata mengodekan 128–256 bit entropi ditambah checksum. "
       "`Mnemonic.Validate` memeriksa daftar kata sekaligus checksum-nya."),
    code(r'''
var phrase = Mnemonic.Generate(12);
Console.WriteLine(phrase);
Console.WriteLine($"valid: {Mnemonic.Validate(phrase)}");
Console.WriteLine($"one wrong word: {Mnemonic.Validate(phrase.Replace(phrase.Split(' ')[0], "zoo"))}");
'''),
    md("The rest of this notebook uses the public BIP-39 test phrase so the results are reproducible — and so you can "
       "compare them with MetaMask, Phantom, Polkadot.js or Keplr.",
       "Bagian selanjutnya memakai frasa uji BIP-39 yang publik agar hasilnya bisa direproduksi — dan bisa Anda bandingkan "
       "dengan MetaMask, Phantom, Polkadot.js atau Keplr."),
    code(r'''
const string TestPhrase = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
byte[] seed = Mnemonic.ToSeed(TestPhrase);
Convert.ToHexStringLower(seed)
'''),
    md("## One phrase, every chain\n\n`HdWallet` holds three roots: BIP-32 (secp256k1) for EVM, Bitcoin and Cosmos, "
       "SLIP-10 (ed25519) for Solana, and substrate-bip39 for Polkadot.",
       "## Satu frasa, semua chain\n\n`HdWallet` menyimpan tiga root: BIP-32 (secp256k1) untuk EVM, Bitcoin dan Cosmos, "
       "SLIP-10 (ed25519) untuk Solana, dan substrate-bip39 untuk Polkadot."),
    code(r'''
var wallet = HdWallet.FromMnemonic(TestPhrase);

var eth  = wallet.GetEvmAccount(0, EvmChain.Ethereum);
var btc  = wallet.GetBitcoinAccount(BitcoinAddressType.SegWitP2WPKH);
var tap  = wallet.GetBitcoinAccount(BitcoinAddressType.TaprootP2TR);
var sol  = wallet.GetSolanaAccount(0);
var dot  = wallet.GetPolkadotAccount("", PolkadotChain.Polkadot);
var atom = wallet.GetCosmosAccount(0);

new[]
{
    new { Chain = "Ethereum",          Path = eth.DerivationPath,  Address = eth.Address.Value },
    new { Chain = "Bitcoin (SegWit)",  Path = btc.DerivationPath,  Address = btc.Address.Value },
    new { Chain = "Bitcoin (Taproot)", Path = tap.DerivationPath,  Address = tap.Address.Value },
    new { Chain = "Solana",            Path = sol.DerivationPath,  Address = sol.Address.Value },
    new { Chain = "Polkadot",          Path = "(root, sr25519)",   Address = dot.Address.Value },
    new { Chain = "Cosmos Hub",        Path = atom.DerivationPath, Address = atom.Address.Value },
}
'''),
    md("## Extended keys and custom paths\n\nAccount-level `zpub`/`xpub` let a watch-only wallet derive receive "
       "addresses without the private keys.",
       "## Extended key dan path kustom\n\n`zpub`/`xpub` tingkat akun memungkinkan dompet watch-only menurunkan alamat "
       "penerimaan tanpa private key."),
    code(r'''
Console.WriteLine($"BIP-84 zpub: {wallet.GetBitcoinAccountXpub(BitcoinAddressType.SegWitP2WPKH)}");

var key = wallet.DerivePath("m/44'/60'/0'/0/7");
Console.WriteLine($"m/44'/60'/0'/0/7 → {EvmAddress.FromPrivateKey(key.PrivateKey.Span)}");
Console.WriteLine($"depth {key.Depth}, parent fingerprint {key.ParentFingerprint:x8}");
key.Dispose();
'''),
    md("## Encrypted keystore (Web3 Secret Storage v3)\n\nThe same format geth and MetaMask import. "
       "`KeyStoreKdf.Light` keeps this demo fast; use `KeyStoreKdf.Standard` in real applications.",
       "## Keystore terenkripsi (Web3 Secret Storage v3)\n\nFormat yang sama dengan yang diimpor geth dan MetaMask. "
       "`KeyStoreKdf.Light` membuat demo ini cepat; gunakan `KeyStoreKdf.Standard` di aplikasi sungguhan."),
    code(r'''
string json = eth.ExportKeyStore("correct horse battery staple", KeyStoreKdf.Light);
Console.WriteLine(json);

var restored = EvmAccount.FromKeyStore(json, "correct horse battery staple");
Console.WriteLine($"restored {restored.Address} — same account: {restored.Address == eth.Address}");
try { KeyStore.Decrypt(json, "wrong password"); }
catch (System.Security.Cryptography.CryptographicException e) { Console.WriteLine($"wrong password → {e.Message}"); }
'''),
    md("## Two engines, identical bytes\n\nEvery primitive runs in Rust or in managed C#. `UseBackend` switches for the "
       "current flow only; the outputs are byte-identical.",
       "## Dua mesin, byte identik\n\nSetiap primitif berjalan di Rust atau C# managed. `UseBackend` mengganti mesin hanya "
       "untuk alur saat ini; keluarannya identik per byte."),
    code(r'''
byte[] data = "Crypto.Net"u8.ToArray();
string rust, managed;
using (CryptoNative.UseBackend(CryptoBackend.Native))  rust    = Convert.ToHexStringLower(CryptoNative.Keccak256(data));
using (CryptoNative.UseBackend(CryptoBackend.Managed)) managed = Convert.ToHexStringLower(CryptoNative.Keccak256(data));
Console.WriteLine($"{rust}\n{managed}\nidentical: {rust == managed}");

CryptoBenchmark.Run(scale: 0.1)
    .Select(r => new { r.Name, Rust = Math.Round(r.NativeOpsPerSec), Managed = Math.Round(r.ManagedOpsPerSec), SpeedUp = Math.Round(r.Speedup, 1) })
'''),
    md("Dispose secrets when you are done.", "Dispose rahasia setelah selesai."),
    code(r'''
foreach (IDisposable d in new IDisposable[] { eth, btc, tap, sol, dot, atom, restored, wallet }) d.Dispose();
"disposed"
'''),
]))

# ============================================================================== 02 bitcoin
NOTEBOOKS.append(("02-bitcoin", {"en": "Crypto.Net · 02 — Bitcoin", "id": "Crypto.Net · 02 — Bitcoin"}, [
    md("Addresses (legacy, SegWit, Taproot), WIF keys, live data from Esplora, coin selection and offline signing of a "
       "transaction that spends SegWit and Taproot outputs.",
       "Alamat (legacy, SegWit, Taproot), kunci WIF, data live dari Esplora, coin selection dan penandatanganan offline "
       "transaksi yang membelanjakan output SegWit dan Taproot."),
    code(INSTALL),
    code(r'''
using Crypto.Net.Core;
using Crypto.Net.Native;
using Crypto.Net.Wallet;
using Crypto.Net.Bitcoin;

var network = BitcoinNetwork.Testnet;
var wallet  = HdWallet.Generate();
'''),
    md("## Addresses\n\nBIP-44 (P2PKH), BIP-84 (P2WPKH) and BIP-86 (P2TR, Taproot with the BIP-341 tweak).",
       "## Alamat\n\nBIP-44 (P2PKH), BIP-84 (P2WPKH) dan BIP-86 (P2TR, Taproot dengan tweak BIP-341)."),
    code(r'''
var legacy  = wallet.GetBitcoinAccount(BitcoinAddressType.LegacyP2PKH, 0, network);
var segwit  = wallet.GetBitcoinAccount(BitcoinAddressType.SegWitP2WPKH, 0, network);
var taproot = wallet.GetBitcoinAccount(BitcoinAddressType.TaprootP2TR, 0, network);

new[] { legacy, segwit, taproot }.Select(a => new
{
    a.Type,
    a.DerivationPath,
    Address = a.Address.Value,
    ScriptPubKey = Convert.ToHexStringLower(a.ScriptPubKey),
})
'''),
    md("`BitcoinAddress.Parse` validates checksums (Base58Check, Bech32 vs Bech32m per BIP-350) and returns the output script.",
       "`BitcoinAddress.Parse` memvalidasi checksum (Base58Check, Bech32 vs Bech32m sesuai BIP-350) dan mengembalikan script output."),
    code(r'''
var info = BitcoinAddress.Parse("bc1p5cyxnuxmeuwuvkwfem96lqzszd02n6xdcjrs20cac6yqjjwudpxqkedrcr");
Console.WriteLine($"{info.Type} · program {Convert.ToHexStringLower(info.Payload)}");
Console.WriteLine($"typo detected: {!BitcoinAddress.IsValid("bc1p5cyxnuxmeuwuvkwfem96lqzszd02n6xdcjrs20cac6yqjjwudpxqkedrcs")}");
Console.WriteLine($"wrong network detected: {!BitcoinAddress.IsValid(segwit.Address.Value, BitcoinNetwork.Mainnet)}");
'''),
    md("## WIF\n\nWallet Import Format — what Bitcoin Core's `dumpprivkey` prints.",
       "## WIF\n\nWallet Import Format — yang ditampilkan `dumpprivkey` di Bitcoin Core."),
    code(r'''
string wif = segwit.ExportWif();
var imported = BitcoinAccount.FromWif(wif, BitcoinAddressType.SegWitP2WPKH, network);
Console.WriteLine($"{wif[..6]}… → {imported.Address} (same: {imported.Address == segwit.Address})");
'''),
    md("## Live network (Esplora)", "## Jaringan live (Esplora)"),
    code(r'''
var esplora = new EsploraClient(network);
try
{
    Console.WriteLine($"testnet tip: {await esplora.GetBlockNumberAsync():N0}");
    Console.WriteLine($"fee for ~6 blocks: {await esplora.GetFeeRateAsync(6)} sat/vB");
    Console.WriteLine($"balance of {segwit.Address}: {await esplora.GetBalanceAsync(segwit.Address)} tBTC");
}
catch (Exception e) { Console.WriteLine($"Esplora unavailable right now: {e.Message}"); }
'''),
    md("## Build and sign offline\n\nTwo pretend UTXOs — one SegWit, one Taproot — are spent in a single transaction. "
       "Each input is signed according to its script: BIP-143 ECDSA for P2WPKH, BIP-341 Schnorr for P2TR. "
       "Both outputs belong to the same wallet, so `SignAllInputs` can sign them with their own keys.",
       "## Membangun dan menandatangani offline\n\nDua UTXO tiruan — satu SegWit, satu Taproot — dibelanjakan dalam satu "
       "transaksi. Setiap input ditandatangani sesuai script-nya: ECDSA BIP-143 untuk P2WPKH, Schnorr BIP-341 untuk P2TR."),
    code(r'''
var utxos = new List<BitcoinUtxo>
{
    new(new string('a', 64), 0, 60_000, segwit.ScriptPubKey),
    new(new string('b', 64), 1, 40_000, taproot.ScriptPubKey),
};

var pick = CoinSelector.Select(utxos, targetSats: 80_000, feeRateSatPerVb: 2m);
Console.WriteLine($"inputs {pick.Inputs.Count}, fee {pick.FeeSats} sats, change {pick.ChangeSats} sats");

var tx = new BitcoinTransaction();
foreach (var u in pick.Inputs) tx.AddInput(u.TxId, u.Vout);
tx.AddOutput("tb1qw508d6qejxtdg4y5r3zarvary0c5xw7kxpjzsx", 80_000, network);
if (pick.ChangeSats > 0) tx.AddOutput(segwit.ScriptPubKey, pick.ChangeSats);

// Each input is signed by the account that owns it.
for (int i = 0; i < pick.Inputs.Count; i++)
{
    var owner = pick.Inputs[i].Type == BitcoinAddressType.TaprootP2TR ? taproot : segwit;
    using var k = wallet.DerivePath(owner.DerivationPath!);
    tx.SignInput(i, k.PrivateKey.Span, pick.Inputs);
}

Console.WriteLine($"txid  {tx.TxId}");
Console.WriteLine($"size  {tx.Serialize().Length} bytes, {tx.VirtualSize} vB, weight {tx.Weight}");
Console.WriteLine(tx.ToHex());
'''),
    md("Verify the Taproot signature against the tweaked output key, exactly as a node would.",
       "Verifikasi tanda tangan Taproot terhadap output key yang sudah di-tweak, persis seperti yang dilakukan node."),
    code(r'''
int tapIndex = pick.Inputs.ToList().FindIndex(u => u.Type == BitcoinAddressType.TaprootP2TR);
byte[] sighash   = tx.GetTaprootSigHash(tapIndex, pick.Inputs);
byte[] outputKey = taproot.ScriptPubKey[2..];
CryptoNative.SchnorrVerify(outputKey, sighash, tx.Inputs[tapIndex].Witness[0])
'''),
    md("## Sending for real\n\nWith a funded testnet account, one call fetches UTXOs, selects coins, adds change, signs "
       "and broadcasts:\n\n```csharp\nTxHash id = await esplora.TransferAsync(segwit, \"tb1q…\", amountSats: 10_000);\n"
       "var receipt = await esplora.WaitForReceiptAsync(id, timeout: TimeSpan.FromMinutes(30));\n```",
       "## Mengirim sungguhan\n\nDengan akun testnet yang memiliki saldo, satu panggilan mengambil UTXO, memilih koin, "
       "menambah kembalian, menandatangani dan mem-broadcast:\n\n```csharp\nTxHash id = await esplora.TransferAsync(segwit, \"tb1q…\", amountSats: 10_000);\n"
       "var receipt = await esplora.WaitForReceiptAsync(id, timeout: TimeSpan.FromMinutes(30));\n```"),
    code(r'''
foreach (IDisposable d in new IDisposable[] { legacy, segwit, taproot, imported, esplora, wallet }) d.Dispose();
"disposed"
'''),
]))

# ============================================================================== 03 evm
NOTEBOOKS.append(("03-ethereum-evm", {"en": "Crypto.Net · 03 — Ethereum and EVM", "id": "Crypto.Net · 03 — Ethereum dan EVM"}, [
    md("EIP-55 addresses, live reads from Sepolia, an ERC-20 contract, EIP-1559 transactions, EIP-191 and EIP-712 "
       "signatures, and the ABI codec.",
       "Alamat EIP-55, pembacaan live dari Sepolia, kontrak ERC-20, transaksi EIP-1559, tanda tangan EIP-191 dan EIP-712, "
       "serta codec ABI."),
    code(INSTALL),
    code(r'''
using System.Numerics;
using Crypto.Net.Core;
using Crypto.Net.Wallet;
using Crypto.Net.Evm;

var wallet  = HdWallet.Generate();
var account = wallet.GetEvmAccount(0, EvmChain.Sepolia);   // m/44'/60'/0'/0/0, like MetaMask
account.Address.Value
'''),
    md("## Addresses (EIP-55)\n\nMixed-case addresses carry a checksum; a single wrong letter is caught.",
       "## Alamat (EIP-55)\n\nAlamat huruf campuran membawa checksum; satu huruf yang salah langsung terdeteksi."),
    code(r'''
string good = "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed";
string bad  = "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAeD";
Console.WriteLine($"{good} valid: {EvmAddress.IsValid(good)}");
Console.WriteLine($"{bad} valid: {EvmAddress.IsValid(bad)}");
Console.WriteLine($"checksummed: {EvmAddress.ToChecksumAddress(good.ToLowerInvariant())}");
'''),
    md("## Live data from Sepolia", "## Data live dari Sepolia"),
    code(r'''
var client = new EvmRpcClient(EvmChain.Sepolia);
Console.WriteLine($"chain id {await client.GetChainIdAsync()}, block {await client.GetBlockNumberAsync():N0}");
var fees = await client.EstimateFeesAsync();
Console.WriteLine($"base fee {Amount.FromWei(fees.BaseFee).WithDecimals(18)} ETH, suggested max {Amount.FromWei(fees.MaxFeePerGas)} ETH per gas");
Console.WriteLine($"your balance: {await client.GetBalanceAsync(account.Address)} ETH");
'''),
    md("## ERC-20\n\n`Erc20Client` reads metadata and scales balances by `decimals()`. This is Sepolia WETH.",
       "## ERC-20\n\n`Erc20Client` membaca metadata dan menskalakan saldo dengan `decimals()`. Ini WETH di Sepolia."),
    code(r'''
var weth = new Erc20Client(client, "0xfFf9976782d46CC05630D1f6eBAb18b2324d6B14");
Console.WriteLine($"{await weth.GetNameAsync()} ({await weth.GetSymbolAsync()}), {await weth.GetDecimalsAsync()} decimals");
Console.WriteLine($"total supply {await weth.GetTotalSupplyAsync()}");
'''),
    md("## EIP-1559 transaction\n\nBuilt and signed offline, then decoded again to recover the sender.",
       "## Transaksi EIP-1559\n\nDibangun dan ditandatangani offline, lalu di-decode lagi untuk memulihkan pengirimnya."),
    code(r'''
var tx = new EvmTransaction
{
    ChainId = EvmChain.Sepolia.NumericChainId,
    Nonce = 0,
    MaxPriorityFeePerGas = 1_000_000_000,
    MaxFeePerGas = 20_000_000_000,
    GasLimit = 21_000,
    To = "0xfFf9976782d46CC05630D1f6eBAb18b2324d6B14",
    Value = Amount.FromEther(0.001m).BaseUnits,
};
var signed  = account.SignTransaction(tx);
var decoded = EvmTransaction.DecodeSigned(signed.RawBytes);

Console.WriteLine($"hash   {signed.Hash}");
Console.WriteLine($"raw    {signed.RawHex}");
Console.WriteLine($"type   {decoded.Transaction.Type}, nonce {decoded.Transaction.Nonce}, value {Amount.FromWei(decoded.Transaction.Value)} ETH");
Console.WriteLine($"sender {decoded.RecoverSender()} (ours: {decoded.RecoverSender() == account.Address.Value})");
'''),
    md("## Message signatures\n\nEIP-191 `personal_sign` (\"Sign in with Ethereum\") and EIP-712 typed data.",
       "## Tanda tangan pesan\n\nEIP-191 `personal_sign` (\"Sign in with Ethereum\") dan typed data EIP-712."),
    code(r'''
byte[] sig = account.SignMessage("Sign in to Crypto.Net");
Console.WriteLine($"signature {HexUtil.Encode(sig)}");
Console.WriteLine($"recovered {EvmMessageSigner.RecoverPersonalMessageSigner("Sign in to Crypto.Net", HexUtil.Encode(sig))}");

string typedData = """
{
  "types": {
    "EIP712Domain": [ {"name":"name","type":"string"}, {"name":"version","type":"string"}, {"name":"chainId","type":"uint256"} ],
    "Order": [ {"name":"maker","type":"address"}, {"name":"amount","type":"uint256"}, {"name":"note","type":"string"} ]
  },
  "primaryType": "Order",
  "domain": { "name": "Crypto.Net Demo", "version": "1", "chainId": 11155111 },
  "message": { "maker": "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed", "amount": "1000000", "note": "hello" }
}
""";
byte[] typedSig = account.SignTypedData(typedData);
Console.WriteLine($"EIP-712 digest {HexUtil.Encode(EvmMessageSigner.HashTypedData(typedData))}");
Console.WriteLine($"EIP-712 signer {EvmMessageSigner.RecoverTypedDataSigner(typedData, typedSig)}");
'''),
    md("## ABI\n\nEncode calls by signature, decode return data and logs.",
       "## ABI\n\nMengodekan panggilan dari signature, men-decode data balikan dan log."),
    code(r'''
byte[] call = EvmAbi.EncodeFunctionCall("transfer(address to, uint256 amount)", account.Address.Value, new BigInteger(1_000_000));
Console.WriteLine(HexUtil.Encode(call));

byte[] encoded = EvmAbi.Encode("string,uint256[],(address,bool)", "Crypto.Net", new object[] { 1, 2, 3 }, new object[] { account.Address.Value, true });
var values = EvmAbi.Decode("string,uint256[],(address,bool)", encoded);
Console.WriteLine($"{values[0]} · [{string.Join(", ", (object?[])values[1]!)}] · {string.Join(", ", (object?[])values[2]!)}");
Console.WriteLine($"Transfer topic {Erc20Client.TransferEventTopic}");
'''),
    md("## Sending for real\n\nWith Sepolia ETH in the account:\n\n```csharp\nawait using var signer = account.CreateSigner();\n"
       "TxHash hash = await client.TransferAsync(signer, \"0x…\", Amount.FromEther(0.001m)); // nonce, fees, gas, simulation\n"
       "TxReceipt receipt = await client.WaitForReceiptAsync(hash);\n```",
       "## Mengirim sungguhan\n\nDengan ETH Sepolia di akun:\n\n```csharp\nawait using var signer = account.CreateSigner();\n"
       "TxHash hash = await client.TransferAsync(signer, \"0x…\", Amount.FromEther(0.001m)); // nonce, fee, gas, simulasi\n"
       "TxReceipt receipt = await client.WaitForReceiptAsync(hash);\n```"),
    code(r'''
account.Dispose(); client.Dispose(); wallet.Dispose();
"disposed"
'''),
]))

# ============================================================================== 04 solana
NOTEBOOKS.append(("04-solana", {"en": "Crypto.Net · 04 — Solana", "id": "Crypto.Net · 04 — Solana"}, [
    md("Phantom-compatible accounts, key formats, live devnet reads, program-derived addresses and a simulated "
       "transaction with Compute Budget, System and Memo instructions.",
       "Akun yang kompatibel dengan Phantom, format kunci, pembacaan live devnet, program-derived address dan transaksi "
       "yang disimulasikan dengan instruksi Compute Budget, System dan Memo."),
    code(INSTALL),
    code(r'''
using Crypto.Net.Core;
using Crypto.Net.Wallet;
using Crypto.Net.Solana;

var wallet  = HdWallet.Generate();
var account = wallet.GetSolanaAccount(0, SolanaChain.Devnet);   // m/44'/501'/0'/0' (Phantom, Solflare)
Console.WriteLine($"{account.Address} ({account.DerivationPath})");
Console.WriteLine($"Phantom secret key format: {account.ExportBase58SecretKey()[..12]}… (64 bytes)");
Console.WriteLine($"solana-cli keypair JSON:   {account.ExportKeypairJson()[..40]}…");
'''),
    md("## Live devnet", "## Devnet live"),
    code(r'''
var rpc = new SolanaRpcClient(SolanaChain.Devnet);
var (blockhash, lastValid) = await rpc.GetLatestBlockhashAsync();
Console.WriteLine($"slot {await rpc.GetBlockNumberAsync():N0}, blockhash {blockhash} (valid until height {lastValid:N0})");
Console.WriteLine($"balance {await rpc.GetBalanceAsync(account.Address)} SOL");
Console.WriteLine($"rent-exempt minimum for 165 bytes: {Amount.FromLamports(await rpc.GetMinimumBalanceForRentExemptionAsync(165))} SOL");
'''),
    md("## Program-derived addresses\n\nPDAs are off the ed25519 curve, so no private key exists for them. The associated "
       "token account (ATA) is the PDA every wallet uses for a given mint (here: devnet USDC).",
       "## Program-derived address\n\nPDA berada di luar kurva ed25519, sehingga tidak ada private key untuknya. Associated "
       "token account (ATA) adalah PDA yang dipakai setiap dompet untuk mint tertentu (di sini: USDC devnet)."),
    code(r'''
const string DevnetUsdc = "4zMMC9srt5Ri5X14GAgXhaHii3GnPAEERYPJgZJDncDU";
string ata = SolanaAddress.GetAssociatedTokenAddress(account.Address.Value, DevnetUsdc);
var (vault, bump) = SolanaAddress.FindProgramAddress([SolanaAddress.Utf8Seed("vault"), SolanaAddress.Decode(account.Address.Value)], SolanaAddress.MemoProgram);

new[]
{
    new { What = "wallet", Address = account.Address.Value, OnCurve = SolanaAddress.IsOnCurve(account.Address.Value) },
    new { What = "USDC ATA", Address = ata, OnCurve = SolanaAddress.IsOnCurve(ata) },
    new { What = $"PDA (bump {bump})", Address = vault, OnCurve = SolanaAddress.IsOnCurve(vault) },
}
'''),
    md("## Build, sign and simulate\n\nA fresh account has no SOL, so the simulation reports an error — that is the "
       "node validating our signed transaction. Fund it with `RequestAirdropAsync` (devnet) to see it succeed.",
       "## Membangun, menandatangani dan mensimulasikan\n\nAkun baru belum punya SOL, sehingga simulasi melaporkan error — "
       "itu adalah node yang memvalidasi transaksi bertanda tangan kita. Isi saldo dengan `RequestAirdropAsync` (devnet) "
       "untuk melihatnya berhasil."),
    code(r'''
var tx = new SolanaTransaction(account.Address.Value, blockhash)
    .Add(ComputeBudgetProgram.SetComputeUnitPrice(1_000))
    .Add(SystemProgram.Transfer(account.Address.Value, "11111111111111111111111111111112", 5_000))
    .Add(MemoProgram.Memo("Hello from Crypto.Net", account.Address.Value));
account.Sign(tx);

byte[] wire = tx.Serialize();
Console.WriteLine($"signature (tx id) {tx.Signature}");
Console.WriteLine($"{wire.Length} bytes · accounts: {string.Join(", ", tx.CompileAccounts().Select(a => a.PublicKey[..6] + (a.IsSigner ? "(s)" : "") + (a.IsWritable ? "(w)" : "")))}");

var (error, logs) = await rpc.SimulateTransactionAsync(wire);
Console.WriteLine(error is null ? "simulation succeeded" : $"simulation error: {error}");
foreach (var line in logs) Console.WriteLine($"  {line}");
'''),
    md("## Sending for real\n\n```csharp\nawait rpc.RequestAirdropAsync(account.Address.Value, 1_000_000_000); // devnet, rate-limited\n"
       "TxHash sig = await rpc.TransferAsync(account, \"<recipient>\", Amount.FromSol(0.1m), priorityFeeMicroLamports: 1_000);\n"
       "TxHash spl = await rpc.TransferTokenAsync(account, DevnetUsdc, \"<recipient>\", Amount.Parse(\"1\", 6));\n```",
       "## Mengirim sungguhan\n\n```csharp\nawait rpc.RequestAirdropAsync(account.Address.Value, 1_000_000_000); // devnet, dibatasi laju\n"
       "TxHash sig = await rpc.TransferAsync(account, \"<penerima>\", Amount.FromSol(0.1m), priorityFeeMicroLamports: 1_000);\n"
       "TxHash spl = await rpc.TransferTokenAsync(account, DevnetUsdc, \"<penerima>\", Amount.Parse(\"1\", 6));\n```"),
    code(r'''
account.Dispose(); rpc.Dispose(); wallet.Dispose();
"disposed"
'''),
]))

# ============================================================================== 05 polkadot
NOTEBOOKS.append(("05-polkadot", {"en": "Crypto.Net · 05 — Polkadot and Substrate", "id": "Crypto.Net · 05 — Polkadot dan Substrate"}, [
    md("sr25519 and ed25519 accounts with Substrate derivation paths, SS58 addresses, message signatures, SCALE, and an "
       "extrinsic built from live runtime metadata whose fee is quoted by the chain.",
       "Akun sr25519 dan ed25519 dengan path derivasi Substrate, alamat SS58, tanda tangan pesan, SCALE, dan extrinsic yang "
       "dibangun dari metadata runtime live dan fee-nya dihitung oleh chain."),
    code(INSTALL),
    code(r'''
using Crypto.Net.Core;
using Crypto.Net.Native;
using Crypto.Net.Wallet;
using Crypto.Net.Polkadot;

$"sr25519 needs the Rust core — loaded: {NativeLoader.IsNativeAvailable}"
'''),
    md("## Development accounts\n\n`//Alice` is derived from Substrate's public development phrase — the same account every "
       "local node pre-funds.",
       "## Akun pengembangan\n\n`//Alice` diturunkan dari frasa pengembangan publik Substrate — akun yang sama yang diberi "
       "saldo oleh setiap node lokal."),
    code(r'''
var alice   = PolkadotAccount.FromSuri("//Alice", chain: PolkadotChain.Westend);
var aliceEd = PolkadotAccount.FromSuri("//Alice", SignatureScheme.Ed25519, PolkadotChain.Westend);

new[] { PolkadotChain.Polkadot, PolkadotChain.Kusama, PolkadotChain.Westend }
    .Select(c => new { Network = c.Name, c.Ss58Prefix, Sr25519 = alice.AddressFor(c), Ed25519 = aliceEd.AddressFor(c) })
'''),
    md("## Your accounts\n\nThe empty path is the root account Polkadot.js, Talisman and SubWallet show for a phrase; "
       "`//hard` and `/soft` junctions derive more.",
       "## Akun Anda\n\nPath kosong adalah akun root yang ditampilkan Polkadot.js, Talisman dan SubWallet untuk sebuah frasa; "
       "junction `//hard` dan `/soft` menurunkan akun lainnya."),
    code(r'''
var wallet = HdWallet.Generate();
var root   = wallet.GetPolkadotAccount("", PolkadotChain.Polkadot);
var child  = wallet.GetPolkadotAccount("//polkadot//0", PolkadotChain.Polkadot);
var (prefix, publicKey) = Ss58Address.Decode(root.Address.Value);
Console.WriteLine($"root  {root.Address}  (prefix {prefix}, key {HexUtil.Encode(publicKey)})");
Console.WriteLine($"child {child.Address}  ({child.DerivationPath})");
'''),
    md("## Signing messages\n\n`SignMessage` wraps the text in `<Bytes>…</Bytes>` like the Polkadot.js extension's "
       "`signRaw`. sr25519 signatures are randomized, so each run differs, but they always verify.",
       "## Menandatangani pesan\n\n`SignMessage` membungkus teks dengan `<Bytes>…</Bytes>` seperti `signRaw` di ekstensi "
       "Polkadot.js. Tanda tangan sr25519 bersifat acak sehingga berbeda tiap kali, tetapi selalu terverifikasi."),
    code(r'''
byte[] sig = root.SignMessage("Hello Polkadot");
Console.WriteLine(HexUtil.Encode(sig));
Console.WriteLine($"valid: {PolkadotAccount.VerifyMessage("Hello Polkadot", sig, root.PublicKey.Span)}");
Console.WriteLine($"tampered: {PolkadotAccount.VerifyMessage("Hello Kusama", sig, root.PublicKey.Span)}");
'''),
    md("## SCALE codec", "## Codec SCALE"),
    code(r'''
var encoded = new ScaleWriter().Compact(1_000_000_000_000).U32(42).String("Crypto.Net").ToArray();
{
    // ScaleReader is a ref struct, so it lives inside a block rather than as a notebook-level variable.
    var reader = new ScaleReader(encoded);
    Console.WriteLine($"{HexUtil.Encode(encoded)} → {reader.Compact()}, {reader.U32()}, {reader.String()}");
}
'''),
    md("## Live runtime metadata (Westend Asset Hub)\n\nPallet indices, call indices and the signed-extension list are "
       "read from the chain, so extrinsics keep working after runtime upgrades.",
       "## Metadata runtime live (Westend Asset Hub)\n\nIndeks pallet, indeks call dan daftar signed extension dibaca dari "
       "chain, sehingga extrinsic tetap berfungsi setelah upgrade runtime."),
    code(r'''
var rpc = new PolkadotRpcClient(PolkadotChain.WestendAssetHub);
var (spec, txVersion, specName) = await rpc.GetRuntimeVersionAsync();
var metadata = await rpc.GetMetadataAsync();
var (pallet, callIndex) = metadata.GetCallIndex("Balances", "transfer_keep_alive");

Console.WriteLine($"{await rpc.GetChainNameAsync()} · {specName} v{spec} · metadata V{metadata.Version} · {metadata.Pallets.Count} pallets");
Console.WriteLine($"Balances.transfer_keep_alive = ({pallet}, {callIndex})");
Console.WriteLine($"signed extensions: {string.Join(", ", metadata.SignedExtensions.Select(e => e.Identifier))}");
var aliceInfo = await rpc.GetAccountInfoAsync(alice.AddressFor(PolkadotChain.WestendAssetHub));
Console.WriteLine($"//Alice on Westend Asset Hub: nonce {aliceInfo.Nonce}, free {new Amount(aliceInfo.Free, 12)} WND");
'''),
    md("## A signed extrinsic, priced by the chain\n\nThe runtime must decode an extrinsic to quote its fee, so a fee "
       "estimate proves the encoding is valid. Nothing is submitted.",
       "## Extrinsic bertanda tangan, dihargai oleh chain\n\nRuntime harus men-decode extrinsic untuk menghitung fee-nya, "
       "sehingga estimasi fee membuktikan encoding-nya valid. Tidak ada yang dikirim."),
    code(r'''
var ctx = await rpc.GetExtrinsicContextAsync(alice.Address.Value);
byte[] call = Extrinsic.TransferKeepAlive(metadata, root.AddressFor(PolkadotChain.WestendAssetHub), 1_000_000_000_000);
byte[] extrinsic = Extrinsic.Sign(metadata, call, alice, ctx);

Console.WriteLine($"{extrinsic.Length} bytes, hash {Extrinsic.Hash(extrinsic)}");
Console.WriteLine($"mortal for {ctx.MortalPeriod} blocks from #{ctx.BlockNumber:N0}, nonce {ctx.Nonce}");
Console.WriteLine($"fee quoted by the runtime: {await rpc.EstimateFeeAsync(extrinsic)} WND");
'''),
    md("## Sending for real\n\n```csharp\nTxHash hash = await rpc.TransferAsync(root, \"5Grw…\", Amount.Parse(\"0.5\", 12));\n"
       "TxHash remark = await rpc.SubmitCallAsync(root, Extrinsic.Remark(metadata, \"hello\"u8));\n```",
       "## Mengirim sungguhan\n\n```csharp\nTxHash hash = await rpc.TransferAsync(root, \"5Grw…\", Amount.Parse(\"0.5\", 12));\n"
       "TxHash remark = await rpc.SubmitCallAsync(root, Extrinsic.Remark(metadata, \"hello\"u8));\n```"),
    code(r'''
foreach (IDisposable d in new IDisposable[] { alice, aliceEd, root, child, rpc, wallet }) d.Dispose();
"disposed"
'''),
]))

# ============================================================================== 06 cosmos
NOTEBOOKS.append(("06-cosmos", {"en": "Crypto.Net · 06 — Cosmos SDK", "id": "Crypto.Net · 06 — Cosmos SDK"}, [
    md("Bech32 accounts across Cosmos chains, live REST reads, protobuf transactions in `SIGN_MODE_DIRECT` and a gas "
       "simulation by a real node.",
       "Akun Bech32 lintas chain Cosmos, pembacaan REST live, transaksi protobuf dalam `SIGN_MODE_DIRECT` dan simulasi gas "
       "oleh node sungguhan."),
    code(INSTALL),
    code(r'''
using Crypto.Net.Core;
using Crypto.Net.Native;
using Crypto.Net.Wallet;
using Crypto.Net.Cosmos;

// The public BIP-39 test phrase has an account on Cosmos Hub, which lets us run a real simulation below.
const string TestPhrase = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
var wallet  = HdWallet.FromMnemonic(TestPhrase);
var account = wallet.GetCosmosAccount(0, CosmosChain.CosmosHub);   // m/44'/118'/0'/0/0, like Keplr
account.Address.Value
'''),
    md("## One key, many chains\n\nThe same key has an address on every Cosmos chain that uses coin type 118; only the "
       "Bech32 prefix changes.",
       "## Satu kunci, banyak chain\n\nKunci yang sama punya alamat di setiap chain Cosmos yang memakai coin type 118; "
       "hanya prefix Bech32 yang berubah."),
    code(r'''
new[] { "cosmos", "osmo", "celestia", "juno" }
    .Select(hrp => new { Prefix = hrp, Address = CosmosAddress.ConvertPrefix(account.Address.Value, hrp) })
'''),
    md("## Live data", "## Data live"),
    code(r'''
var rest = new CosmosRestClient(CosmosChain.CosmosHub);
var (accountNumber, sequence) = await rest.GetAccountAsync(account.Address.Value);
Console.WriteLine($"Cosmos Hub height {await rest.GetBlockNumberAsync():N0}");
Console.WriteLine($"account number {accountNumber}, sequence {sequence}");
Console.WriteLine($"balance {await rest.GetBalanceAsync(account.Address)} ATOM");
'''),
    md("## Build and sign (SIGN_MODE_DIRECT)\n\nA bank send and a delegation in one transaction. The signature covers "
       "SHA-256 of the protobuf `SignDoc` (body, auth info, chain id, account number).",
       "## Membangun dan menandatangani (SIGN_MODE_DIRECT)\n\nKirim bank dan delegasi dalam satu transaksi. Tanda tangan "
       "mencakup SHA-256 dari `SignDoc` protobuf (body, auth info, chain id, account number)."),
    code(r'''
var builder = new CosmosTxBuilder { Memo = "Crypto.Net notebook", GasLimit = 250_000 }
    .Add(CosmosMessage.BankSend(account.Address.Value, account.Address.Value, new Coin("uatom", 1)))
    .Add(CosmosMessage.Delegate(account.Address.Value, "cosmosvaloper1tflk30mq5vgqjdly92kkhhq3raev2hnz6eete3", new Coin("uatom", 1)));
builder.Fee.Add(new Coin("uatom", 6_250));

byte[] txRaw = account.Sign(builder, "cosmoshub-4", accountNumber, sequence);
byte[] digest = CryptoNative.Sha256(CosmosTxBuilder.EncodeSignDoc(builder.EncodeBody(), builder.EncodeAuthInfo(account.PublicKey.Span, sequence), "cosmoshub-4", accountNumber));
Console.WriteLine($"{txRaw.Length} bytes, tx hash {CosmosTxBuilder.Hash(txRaw)}");
Console.WriteLine($"signature verifies: {CryptoNative.Secp256k1Verify(account.PublicKey.Span, digest, txRaw[^64..])}");
'''),
    md("## Gas simulation by a real node\n\nThe node decodes and executes the transaction without committing it. A "
       "result proves the protobuf encoding is correct. Nothing is broadcast.",
       "## Simulasi gas oleh node sungguhan\n\nNode men-decode dan mengeksekusi transaksi tanpa menyimpannya. Hasilnya "
       "membuktikan encoding protobuf sudah benar. Tidak ada yang di-broadcast."),
    code(r'''
try
{
    ulong gas = await rest.SimulateAsync(txRaw);
    Console.WriteLine($"gas used {gas:N0} → a safe limit is {(ulong)(gas * 1.3):N0}");
}
catch (RpcException e) { Console.WriteLine($"node rejected the simulation: {e.RpcMessage}"); }
'''),
    md("## Sending for real\n\n`SignAndBroadcastAsync` simulates, sizes gas (×1.3), pays the chain's gas price, signs and "
       "broadcasts:\n\n```csharp\nvar testnet = wallet.GetCosmosAccount(0, CosmosChain.CosmosHubTestnet);\n"
       "await using var client = new CosmosRestClient(CosmosChain.CosmosHubTestnet);\n"
       "TxHash hash = await client.TransferAsync(testnet, \"cosmos1…\", Amount.FromAtom(0.1m), memo: \"thanks\");\n```",
       "## Mengirim sungguhan\n\n`SignAndBroadcastAsync` mensimulasikan, menentukan gas (×1,3), membayar gas price chain, "
       "menandatangani dan mem-broadcast:\n\n```csharp\nvar testnet = wallet.GetCosmosAccount(0, CosmosChain.CosmosHubTestnet);\n"
       "await using var client = new CosmosRestClient(CosmosChain.CosmosHubTestnet);\n"
       "TxHash hash = await client.TransferAsync(testnet, \"cosmos1…\", Amount.FromAtom(0.1m), memo: \"terima kasih\");\n```"),
    code(r'''
account.Dispose(); rest.Dispose(); wallet.Dispose();
"disposed"
'''),
]))

# ----------------------------------------------------------------------------------------------- writer


def lines(text):
    parts = text.split("\n")
    return [p + "\n" for p in parts[:-1]] + [parts[-1]]


def build(lang, title, cells):
    other = "id" if lang == "en" else "en"
    header = (f"# {title[lang]}\n\n{ATTR[lang]}\n\n{SAFETY[lang]}\n\n{RUN_HINT[lang]} "
              + ("[Versi Bahasa Indonesia]" if lang == "en" else "[English version]") + "(./{other_file})")
    nb_cells = [{"cell_type": "markdown", "metadata": {}, "source": lines(header)}]
    for kind, content in cells:
        if kind == "md":
            nb_cells.append({"cell_type": "markdown", "metadata": {}, "source": lines(content[lang])})
        else:
            nb_cells.append({
                "cell_type": "code",
                "execution_count": None,
                "metadata": {"dotnet_interactive": {"language": "csharp"}, "polyglot_notebook": {"kernelName": "csharp"}},
                "outputs": [],
                "source": lines(content),
            })
    return {
        "cells": nb_cells,
        "metadata": {
            "kernelspec": {"display_name": ".NET (C#)", "language": "C#", "name": ".net-csharp"},
            "language_info": {"name": "polyglot-notebook"},
            "polyglot_notebook": {"kernelInfo": {"defaultKernelName": "csharp", "items": [{"aliases": [], "name": "csharp"}]}},
        },
        "nbformat": 4,
        "nbformat_minor": 5,
    }


def main():
    for name, title, cells in NOTEBOOKS:
        for lang in ("en", "id"):
            other = "id" if lang == "en" else "en"
            nb = build(lang, title, cells)
            first = nb["cells"][0]
            first["source"] = [s.replace("{other_file}", f"{name}.{other}.ipynb") for s in first["source"]]
            path = HERE / f"{name}.{lang}.ipynb"
            path.write_text(json.dumps(nb, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
            print("wrote", path.name)


if __name__ == "__main__":
    main()
