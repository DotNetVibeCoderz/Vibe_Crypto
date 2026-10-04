# CLI `cnet`

[← Dokumentasi](index.md) · [English](../en/cli.md)

```bash
dotnet tool install -g Crypto.Net.Cli
cnet --help
```

Setiap perintah menerima `--json` untuk scripting. Rahasia bisa diberikan lewat opsi, variabel lingkungan
(`CNET_MNEMONIC`, `CNET_PRIVATE_KEY`) atau prompt tersembunyi — utamakan dua cara terakhir agar tidak tersimpan di
riwayat shell.

| Perintah | Fungsinya |
| --- | --- |
| `cnet doctor` | Status library native, ABI, RID, OS, .NET, penyedia RPC yang dikonfigurasi |
| `cnet networks` | Semua kunci jaringan bawaan (dipakai dengan `-N`) |
| `cnet wallet new [--words 24] [--passphrase …] [--testnet]` | Frasa baru plus alamat di semua chain |
| `cnet wallet derive -m "<frasa>" [-n 5] [--show-keys]` | Alamat (dan opsional kunci) untuk indeks 0..n-1 |
| `cnet wallet xpub -m "<frasa>"` | xpub, zpub, tpub… akun BIP-44/84/86 |
| `cnet keystore encrypt -k <hex> [--kdf standard\|light\|pbkdf2] [-o berkas]` | Keystore Web3 v3 |
| `cnet keystore decrypt <berkas> [--show-key]` | Memeriksa kata sandi dan menampilkan alamat |
| `cnet address validate <alamat> [-N jaringan]` | Mendeteksi chain, memverifikasi checksum |
| `cnet convert <jumlah> <dari> <ke>` | Konversi satuan yang pasti (wei/gwei/ether, sat/btc, lamports/sol, planck/dot, uatom/atom) |
| `cnet balance <alamat> -N <jaringan> [--rpc url]` | Saldo native secara live |
| `cnet block -N <jaringan>` | Tinggi blok atau slot terbaru |
| `cnet tx <hash> -N <jaringan>` | Receipt (kode keluar 2 selama pending) |
| `cnet sign message "<teks>" (-k <hex> \| -m "<frasa>" [-i indeks])` | Tanda tangan EIP-191 |
| `cnet sign typed-data <berkas.json> …` | Digest dan tanda tangan EIP-712 |
| `cnet verify "<teks>" -s <tanda-tangan>` | Memulihkan alamat penanda tangan |
| `cnet bench [--scale 1.0]` | Inti Rust vs C# managed di mesin ini |

![cnet bench](../assets/screenshots/cli-bench.png)

![cnet address validate](../assets/screenshots/cli-address.png)

Variabel lingkungan: `CRYPTONET_ANKR_KEY`, `CRYPTONET_DRPC_KEY` (+ `_NETWORKS`) mengarahkan panggilan RPC lewat
penyedia ber-key; `CRYPTONET_BACKEND=native|managed` memaksa satu mesin; `CRYPTONET_NATIVE_PATH` menunjuk build
library native kustom.
