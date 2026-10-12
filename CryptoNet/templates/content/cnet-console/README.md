    # CnetConsole

    A Crypto.Net console app. It derives an account for the configured network, then prints the chain head and balance.

```bash
dotnet run
dotnet run -- --Network solana-devnet --AccountIndex 1
```

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

    Aplikasi console Crypto.Net. Aplikasi ini menurunkan akun untuk jaringan yang dikonfigurasi, lalu menampilkan head chain dan saldo.

```bash
dotnet run
dotnet run -- --Network solana-devnet --AccountIndex 1
```

    `appsettings.json` berisi profil testnet. Ubah nilai apa pun lewat environment variable (`CNET_Network=solana-devnet`),
    argumen command line (`--Network solana-devnet`) atau user secrets. Jangan menaruh mnemonic di source:

    ```bash
    dotnet user-secrets set Mnemonic "<frasa test Anda>"
    ```

    Dokumentasi: https://github.com/DotNetVibeCoderz/Vibe_Crypto/tree/main/CryptoNet/docs/id

    *Built by Gravicode Studios, led by Kang Fadhil. · Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.*
