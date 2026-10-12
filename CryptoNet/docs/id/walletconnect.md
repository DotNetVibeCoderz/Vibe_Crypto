# WalletConnect v2

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil. · [English](../en/walletconnect.md)

`Crypto.Net.WalletConnect` menghubungkan aplikasi Anda dengan wallet mobile atau browser milik pengguna (MetaMask,
Trust, Rainbow, sisi EVM Phantom, …) lewat relay WalletConnect milik Reown. Kunci tetap di wallet: aplikasi mengirim
permintaan, lalu pengguna menyetujuinya di ponsel. Arah sebaliknya juga bisa: wallet .NET dapat melayani aplikasi.

```bash
dotnet add package Crypto.Net.WalletConnect
```

Anda memerlukan project id dari [dashboard Reown](https://dashboard.reown.com). Simpan di konfigurasi, misalnya
`CRYPTONET_WALLETCONNECT_PROJECT_ID`.

## Sisi aplikasi (dapp)

```csharp
await using var wc = await WalletConnectClient.CreateAsync(new()
{
    ProjectId = projectId,
    Metadata = new("Aplikasi saya", "Membayar tagihan", "https://example.org", ["https://example.org/icon.png"]),
});

var pairing = await wc.ConnectAsync(WalletConnectEthereum.Namespace(1, 11155111));
TampilkanQr(pairing.Uri);                              // wc:…@2?relay-protocol=irn&symKey=…
WalletConnectSession session = await pairing.Approval; // pengguna memindai dan menyetujui

var eth = new WalletConnectEthereum(session, chainId: 11155111);
string signature = await eth.PersonalSignAsync("Masuk ke example.org");
string txHash = await eth.SendTransactionAsync("0x…", Amount.FromEther(0.01m).BaseUnits);
JsonNode? any = await session.RequestAsync("wallet_switchEthereumChain", new JsonArray(new JsonObject { ["chainId"] = "0x1" }), "eip155:1");

session.EventReceived += (name, data, chain) => Console.WriteLine($"{name} di {chain}");   // accountsChanged, chainChanged
await session.DisconnectAsync();
```

Penolakan di wallet melempar `WalletConnectException` dengan `IsUserRejection`.

## Sisi wallet

```csharp
await using var wallet = await WalletConnectWallet.CreateAsync(new() { ProjectId = projectId, Metadata = new("Wallet saya", "…", "https://example.org") });
wallet.RequestHandler = async request => request.Method switch
{
    "personal_sign" => JsonValue.Create(await TandaTanganSetelahPenggunaSetuju(request)),
    _ => throw new WalletConnectException(4001, "User rejected the request."),
};

var proposal = await wallet.PairAsync(uriHasilPindai);
var session = await wallet.ApproveAsync(proposal, new Dictionary<string, WalletConnectNamespace>
{
    ["eip155"] = new(["eip155:1"], ["personal_sign", "eth_sendTransaction"], ["accountsChanged"], [$"eip155:1:{address}"]),
});
```

## Cara kerja dan pengujian

- **Pertukaran kunci dan topic:** X25519 dengan HKDF-SHA256 (X25519 ada di core Rust, dengan fallback managed).
  Topic adalah `sha256(symKey)`.
- **Pesan:** dibungkus envelope ChaCha20-Poly1305 tipe 0 dan 1. Koneksi ke relay diautentikasi dengan JWT EdDSA
  `did:key`.
- **Pemeriksaan byte demi byte:** unit test membandingkan pertukaran kunci, envelope, JWT dan URI pairing dengan
  `@walletconnect/utils` dan `relay-auth`.
- **Tes live:** jika `CRYPTONET_WALLETCONNECT_PROJECT_ID` diisi, sebuah test memasangkan aplikasi Crypto.Net dengan
  wallet Crypto.Net lewat relay sungguhan. Lalu test itu menandatangani, memeriksa penolakan, melakukan ping dan
  memutus sesi.
- **Interop:** sebelum rilis, kedua arah diuji terhadap `@walletconnect/sign-client` resmi dari Reown. Wallet resmi
  menandatangani untuk aplikasi Crypto.Net, dan aplikasi resmi memverifikasi tanda tangan wallet Crypto.Net.

Belum didukung: autentikasi sekali klik (`wc_sessionAuthenticate`), link mode, dan pemulihan sesi setelah restart
(sesi hidup selama objek client ada).
