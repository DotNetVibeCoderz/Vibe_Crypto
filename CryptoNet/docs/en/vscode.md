# VS Code extension

> Built by Gravicode Studios, led by Kang Fadhil. · [Bahasa Indonesia](../id/vscode.md)

**Crypto.Net Tools** brings Crypto.Net into the editor. It is a thin UI over the `cnet` CLI, so every operation runs
the same tested .NET code as your app.

## Install

1. `dotnet tool install -g Crypto.Net.Cli`
2. Download `crypto-net-tools.vsix` from the [GitHub release](https://github.com/DotNetVibeCoderz/Vibe_Crypto/releases),
   then run **Extensions: Install from VSIX…**, or `code --install-extension crypto-net-tools.vsix`.

## What it does

| Feature | How |
| --- | --- |
| Networks view | Activity bar → Crypto.Net. Click a network to check a balance. |
| New testnet wallet | Shows the phrase and the addresses on all chains in an **unsaved** document. |
| Check balance, validate address, convert units | Command palette, `Crypto.Net: …`. Unit conversion is exact. |
| Generate contract client | Right-click `*.abi.json` / `*.idl.json`. Writes `<Name>.g.cs` (same generator as `cnet gen`). |
| New project | Pick one of the six [templates](templates.md), a folder and a default network. |
| Hovers | EVM addresses show EIP-55 status and explorer links. Long integers show wei/gwei/ETH, lamports/SOL, sat/BTC, planck/DOT, uatom/ATOM. |
| Diagnostics | `CNETVS001` flags an address whose mixed case does not match its EIP-55 checksum, a likely typo. |
| Snippets | `cnet-transfer`, `cnet-contract`, `cnet-event`, `cnet-psbt`, `cnet-wallet`, `cnet-sol`, `cnet-di` |
| Local nodes | Status bar → Anvil, solana-test-validator, bitcoind regtest, polkadot --dev, or docker compose |
| Notebooks | Opens the [notebooks](../../samples/notebooks/README.md) in your language |

The UI follows VS Code's display language (English or Bahasa Indonesia). The [analyzers](analyzers.md) complement the
extension with compile-time rules.

## Development

```bash
cd tools/vscode-extension
npm ci
npm test                  # unit tests (EIP-55 vectors, exact unit math, scanning)
npm run test:integration  # launches an isolated VS Code and checks activation, commands, hovers and diagnostics
npm run package           # crypto-net-tools.vsix
```

CI runs all of the above (integration under `xvfb`) and attaches the `.vsix` to every release. Publishing to the
Marketplace additionally needs a `VSCE_PAT` repository secret.
