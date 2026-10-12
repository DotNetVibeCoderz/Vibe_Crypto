    # CnetWebApi

    An ASP.NET Core Minimal API built on Crypto.Net. It serves only testnets unless `CryptoNet:AllowMainnet` is true.

```bash
dotnet run
curl http://localhost:5000/networks
curl http://localhost:5000/networks/sepolia/head
```

The OpenAPI document is at `/openapi/v1.json`.

    ## Configuration

    `appsettings.json` holds a testnet profile. Override any value with an environment variable (`CNET_Network=solana-devnet`),
    a command-line switch (`--Network solana-devnet`) or user secrets. Keep mnemonics out of source:

    ```bash
    dotnet user-secrets set Mnemonic "<your test phrase>"
    export CRYPTONET_DRPC_KEY=...   # optional: dRPC / Ankr keys raise rate limits
    ```

    Documentation: https://github.com/DotNetVibeCoderz/Vibe_Crypto/tree/main/CryptoNet/docs/en

    ---

    ## Bahasa Indonesia

    Minimal API ASP.NET Core yang dibangun di atas Crypto.Net. API ini hanya melayani testnet kecuali `CryptoNet:AllowMainnet` bernilai true.

```bash
dotnet run
curl http://localhost:5000/networks
```

Dokumen OpenAPI ada di `/openapi/v1.json`.

    `appsettings.json` berisi profil testnet. Ubah nilai apa pun lewat environment variable (`CNET_Network=solana-devnet`),
    argumen command line (`--Network solana-devnet`) atau user secrets. Jangan menaruh mnemonic di source:

    ```bash
    dotnet user-secrets set Mnemonic "<frasa test Anda>"
    ```

    Dokumentasi: https://github.com/DotNetVibeCoderz/Vibe_Crypto/tree/main/CryptoNet/docs/id

    *Built by Gravicode Studios, led by Kang Fadhil. · Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.*
