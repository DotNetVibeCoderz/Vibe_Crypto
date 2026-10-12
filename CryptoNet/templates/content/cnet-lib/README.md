    # CnetLib

    A class library that exposes `PortfolioService` through DI, with an xUnit test project that never touches the network (Crypto.Net.Testing mocks).

```bash
dotnet test
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

    Class library yang menyediakan `PortfolioService` lewat DI, dengan project test xUnit yang tidak pernah menyentuh jaringan (mock Crypto.Net.Testing).

```bash
dotnet test
```

    `appsettings.json` berisi profil testnet. Ubah nilai apa pun lewat environment variable (`CNET_Network=solana-devnet`),
    argumen command line (`--Network solana-devnet`) atau user secrets. Jangan menaruh mnemonic di source:

    ```bash
    dotnet user-secrets set Mnemonic "<frasa test Anda>"
    ```

    Dokumentasi: https://github.com/DotNetVibeCoderz/Vibe_Crypto/tree/main/CryptoNet/docs/id

    *Built by Gravicode Studios, led by Kang Fadhil. · Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.*
