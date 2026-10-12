# Project templates

> Built by Gravicode Studios, led by Kang Fadhil. · [Bahasa Indonesia](../id/template.md)

`Crypto.Net.Templates` adds `dotnet new` templates that start on a testnet and are ready to run.

```bash
dotnet new install Crypto.Net.Templates
dotnet new cnet-console -n Wallet.Demo
cnet new webapi -n Payments.Api --network solana-devnet     # same thing through the cnet CLI
```

| Template | What you get |
| --- | --- |
| `cnet-console` | Console app: derives an account for the configured network, prints the head and the balance |
| `cnet-worker` | Worker Service: follows new blocks and logs balance changes of watched addresses |
| `cnet-webapi` | Minimal API with OpenAPI: networks, heads, balances, address detection; testnets only unless `CryptoNet:AllowMainnet` |
| `cnet-lib` | Class library with a DI-registered `PortfolioService`, plus an xUnit project that uses `Crypto.Net.Testing` mocks |
| `cnet-test` | xUnit project with official vectors, `MockHttpMessageHandler`, and a `docker-compose.yml` for Anvil, a Polkadot dev chain and `solana-test-validator` |

Every template includes:

- a bilingual `README.md`;
- an `.editorconfig` that makes CNET001/CNET003 errors;
- central package management (`Directory.Packages.props`);
- an `appsettings.json` with a testnet profile;
- the [analyzers](analyzers.md).

## Options

| Option | Default | Meaning |
| --- | --- | --- |
| `--Network` | `sepolia` | Default network key (`bitcoin-testnet`, `solana-devnet`, `westend`, `cosmoshub-testnet`, …; see `cnet networks`) |
| `--CryptoNetVersion` | the template package version | Version of the Crypto.Net packages to reference |
| `--skipRestore` | `false` | Skip `dotnet restore` after creation |

## Configuration and secrets

Generated apps read `appsettings.json`, then user secrets, then `CNET_*` environment variables, then the command line.
Put a test phrase in user secrets (`dotnet user-secrets set Mnemonic "…"`), never in source.
`CRYPTONET_DRPC_KEY` / `CRYPTONET_ANKR_KEY` raise rate limits, as in the [DI guide](dependency-injection.md).

CI runs `scripts/test-templates.sh` on every change. The script packs the repository, installs the templates in a
private hive, instantiates each one, builds it with analyzer and vulnerability warnings as errors, and runs the
tests.
