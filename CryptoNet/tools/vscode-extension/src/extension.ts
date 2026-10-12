// Crypto.Net Tools for VS Code. Built by Gravicode Studios, led by Kang Fadhil.
// The extension is a thin UI over the `cnet` CLI, so all crypto logic lives (and is tested) in .NET.
import * as path from 'node:path';
import * as vscode from 'vscode';
import { cnetJson, cnetTerminal, CliError, NetworkInfo, WalletInfo, ValidateInfo, BalanceInfo, ConvertInfo, GenInfo } from './cli';
import { checksumState, describeBaseUnits, EXPLORERS, scanLine, toChecksumAddress } from './core';

const t = vscode.l10n.t;

export function activate(context: vscode.ExtensionContext): void {
  const networks = new NetworksProvider();
  context.subscriptions.push(
    vscode.window.registerTreeDataProvider('cryptonet.networks', networks),
    vscode.window.registerTreeDataProvider('cryptonet.actions', new ActionsProvider()),
    vscode.commands.registerCommand('cryptonet.refreshNetworks', () => networks.refresh()),
    vscode.commands.registerCommand('cryptonet.newWallet', newWallet),
    vscode.commands.registerCommand('cryptonet.checkBalance', (network?: string) => checkBalance(typeof network === 'string' ? network : undefined)),
    vscode.commands.registerCommand('cryptonet.validateAddress', validateAddress),
    vscode.commands.registerCommand('cryptonet.convertUnits', convertUnits),
    vscode.commands.registerCommand('cryptonet.generateClient', generateClient),
    vscode.commands.registerCommand('cryptonet.newProject', newProject),
    vscode.commands.registerCommand('cryptonet.doctor', doctor),
    vscode.commands.registerCommand('cryptonet.startNode', startNode),
    vscode.commands.registerCommand('cryptonet.openNotebook', openNotebook),
    vscode.languages.registerHoverProvider({ language: 'csharp' }, new CryptoHoverProvider()),
  );

  const diagnostics = vscode.languages.createDiagnosticCollection('cryptonet');
  context.subscriptions.push(diagnostics);
  const refresh = (doc: vscode.TextDocument) => updateDiagnostics(doc, diagnostics);
  vscode.workspace.textDocuments.forEach(refresh);
  context.subscriptions.push(
    vscode.workspace.onDidOpenTextDocument(refresh),
    vscode.workspace.onDidChangeTextDocument(e => refresh(e.document)),
    vscode.workspace.onDidCloseTextDocument(d => diagnostics.delete(d.uri)),
  );

  const status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 50);
  status.text = '$(server-environment) Crypto.Net';
  status.tooltip = t('Start a local blockchain node');
  status.command = 'cryptonet.startNode';
  status.show();
  context.subscriptions.push(status);
}

export function deactivate(): void { /* nothing to clean up */ }

// ---------------------------------------------------------------- commands

async function withProgress<T>(title: string, work: (token: vscode.CancellationToken) => Promise<T>): Promise<T | undefined> {
  try {
    return await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title, cancellable: true }, (_, token) => work(token));
  } catch (e) {
    vscode.window.showErrorMessage(e instanceof CliError ? e.message : String(e));
    return undefined;
  }
}

async function pickNetwork(placeHolder: string, testnetsOnly = false): Promise<string | undefined> {
  const list = await withProgress(t('Loading networks…'), tk => cnetJson<NetworkInfo[]>(['networks'], tk));
  if (!list) return undefined;
  const preferred = vscode.workspace.getConfiguration('cryptonet').get<string>('defaultNetwork');
  const items = list.filter(n => !testnetsOnly || n.testnet)
    .sort((a, b) => Number(b.key === preferred) - Number(a.key === preferred) || Number(b.testnet) - Number(a.testnet))
    .map(n => ({ label: n.key, description: `${n.name} · ${n.symbol}`, detail: n.testnet ? t('testnet') : t('mainnet') }));
  return (await vscode.window.showQuickPick(items, { placeHolder }))?.label;
}

async function newWallet(): Promise<void> {
  const wallet = await withProgress(t('Generating a testnet wallet…'), tk => cnetJson<WalletInfo>(['wallet', 'new', '--testnet'], tk));
  if (!wallet) return;
  const doc = await vscode.workspace.openTextDocument({
    language: 'markdown',
    content: `# ${t('New testnet wallet')}\n\n> ${t('This document is not saved. Copy the recovery phrase somewhere safe and offline, then close it without saving. Never fund a phrase that has been on a screen with real money.')}\n\n` +
      (wallet.mnemonic ? `**${t('Recovery phrase')}**\n\n\`\`\`\n${wallet.mnemonic}\n\`\`\`\n\n` : '') +
      `| ${t('Chain')} | ${t('Path')} | ${t('Address')} |\n|---|---|---|\n` +
      wallet.accounts.map(a => `| ${a.chain} | \`${a.path}\` | \`${a.address}\` |`).join('\n') + '\n',
  });
  await vscode.window.showTextDocument(doc, { preview: true });
}

async function checkBalance(network?: string): Promise<void> {
  network ??= await pickNetwork(t('Network'));
  if (!network) return;
  const selection = vscode.window.activeTextEditor?.document.getText(vscode.window.activeTextEditor.selection).trim();
  const address = await vscode.window.showInputBox({ prompt: t('Address on {0}', network), value: selection && selection.length < 120 ? selection : '' });
  if (!address) return;
  const result = await withProgress(t('Reading balance…'), tk => cnetJson<BalanceInfo>(['balance', address, '-N', network!], tk));
  if (result) vscode.window.showInformationMessage(`${result.balance} ${result.symbol} — ${address} (${network})`);
}

async function validateAddress(): Promise<void> {
  const selection = vscode.window.activeTextEditor?.document.getText(vscode.window.activeTextEditor.selection).trim();
  const address = await vscode.window.showInputBox({ prompt: t('Address to validate'), value: selection ?? '' });
  if (!address) return;
  // cnet exits with 1 for an invalid address but still prints JSON, which cnetJson returns.
  const r = await withProgress(t('Validating…'), tk => cnetJson<ValidateInfo>(['address', 'validate', address], tk));
  if (!r) return;
  if (r.valid) vscode.window.showInformationMessage(t('Valid: {0}', r.matches.map(m => `${m.family} (${m.format})`).join(', ')));
  else vscode.window.showWarningMessage(t('Not a valid address on any supported chain'));
}

async function convertUnits(): Promise<void> {
  const input = await vscode.window.showInputBox({ prompt: t('Amount and units, e.g. "1.5 ether gwei" or "250000 lamports sol"') });
  const parts = input?.trim().split(/\s+/);
  if (!parts || parts.length !== 3) return;
  const r = await withProgress(t('Converting…'), tk => cnetJson<ConvertInfo>(['convert', parts[0], parts[1], parts[2]], tk));
  if (!r) return;
  const choice = await vscode.window.showInformationMessage(`${parts[0]} ${parts[1]} = ${r.output} ${parts[2]}`, t('Copy'));
  if (choice) await vscode.env.clipboard.writeText(r.output);
}

async function generateClient(uri?: vscode.Uri): Promise<void> {
  uri ??= (await vscode.window.showOpenDialog({ filters: { 'ABI / IDL': ['json'] }, canSelectMany: false }))?.[0];
  if (!uri) return;
  const base = path.basename(uri.fsPath).replace(/\.(abi|idl)?\.?json$/i, '');
  const name = await vscode.window.showInputBox({ prompt: t('Class name'), value: base.charAt(0).toUpperCase() + base.slice(1) });
  if (!name) return;
  const ns = await vscode.window.showInputBox({ prompt: t('Namespace (optional)'), value: '' });
  const output = path.join(path.dirname(uri.fsPath), `${name}.g.cs`);
  const args = ['gen', uri.fsPath, '-n', name, '-o', output, ...(ns ? ['--namespace', ns] : [])];
  const r = await withProgress(t('Generating {0}…', name), tk => cnetJson<GenInfo>(args, tk));
  if (!r) return;
  await vscode.window.showTextDocument(vscode.Uri.file(r.output ?? output));
  vscode.window.showInformationMessage(t('Generated {0}. Tip: add the Crypto.Net.Generators package to regenerate on every build instead.', path.basename(output)));
}

async function newProject(): Promise<void> {
  const templates = [
    { label: 'console', detail: t('Console app: account, balance and head on a testnet') },
    { label: 'worker', detail: t('Worker Service: block watcher and ERC-20 indexer into SQLite') },
    { label: 'webapi', detail: t('Minimal API with OpenAPI: networks, heads, balances') },
    { label: 'lib', detail: t('Class library with DI and an offline test project') },
    { label: 'test', detail: t('xUnit project with mocks, vectors and local nodes') },
    { label: 'contract-client', detail: t('Typed clients generated from an ABI and an Anchor IDL') },
  ];
  const template = await vscode.window.showQuickPick(templates, { placeHolder: t('Template') });
  if (!template) return;
  const folder = (await vscode.window.showOpenDialog({ canSelectFolders: true, canSelectFiles: false, openLabel: t('Create here') }))?.[0];
  if (!folder) return;
  const name = await vscode.window.showInputBox({ prompt: t('Project name'), value: 'MyCryptoApp', validateInput: v => /^[A-Za-z_][\w.]*$/.test(v) ? null : t('Use letters, digits, dots and underscores') });
  if (!name) return;
  const network = await pickNetwork(t('Default network'), true);
  cnetTerminal('cnet new', ['new', template.label, '-n', name, '-o', path.join(folder.fsPath, name), ...(network ? ['--network', network] : [])]);
}

async function doctor(): Promise<void> {
  const r = await withProgress(t('Inspecting the runtime…'), tk => cnetJson<Record<string, unknown>>(['doctor'], tk));
  if (!r) return;
  const doc = await vscode.workspace.openTextDocument({ language: 'json', content: JSON.stringify(r, null, 2) });
  await vscode.window.showTextDocument(doc, { preview: true });
}

const NODES = [
  { label: 'Anvil', detail: 'EVM · chain 31337 · http://127.0.0.1:8545', command: 'anvil' },
  { label: 'solana-test-validator', detail: 'Solana · http://127.0.0.1:8899', command: 'solana-test-validator' },
  { label: 'bitcoind -regtest', detail: 'Bitcoin regtest · RPC 18443', command: 'bitcoind -regtest -server -rpcuser=cnet -rpcpassword=cnet' },
  { label: 'polkadot --dev', detail: 'Substrate dev chain · ws://127.0.0.1:9944', command: 'polkadot --dev --rpc-cors all' },
  { label: 'docker compose up', detail: t('All nodes from the cnet-test template docker-compose.yml'), command: 'docker compose up' },
];

async function startNode(): Promise<void> {
  const node = await vscode.window.showQuickPick(NODES, { placeHolder: t('Start a local node (stop it with Ctrl+C in its terminal)') });
  if (!node) return;
  const terminal = vscode.window.createTerminal({ name: node.label, cwd: vscode.workspace.workspaceFolders?.[0]?.uri.fsPath });
  terminal.show();
  terminal.sendText(node.command);
}

async function openNotebook(): Promise<void> {
  const notebooks = [
    ['01-wallet-and-keys', t('Wallets and keys')], ['02-bitcoin', 'Bitcoin'], ['03-ethereum-evm', 'Ethereum / EVM'], ['04-solana', 'Solana'],
    ['05-polkadot', 'Polkadot'], ['06-cosmos', 'Cosmos SDK'], ['07-indexer', t('Indexing events')], ['08-benchmarks', t('Rust vs managed')],
    ['09-security', t('Security practices')],
  ];
  const pick = await vscode.window.showQuickPick(notebooks.map(([file, label]) => ({ label, description: file })), { placeHolder: t('Notebook') });
  if (!pick) return;
  const lang = vscode.env.language.startsWith('id') ? 'id' : 'en';
  vscode.env.openExternal(vscode.Uri.parse(`https://github.com/DotNetVibeCoderz/Vibe_Crypto/blob/main/CryptoNet/samples/notebooks/${pick.description}.${lang}.ipynb`));
}

// ---------------------------------------------------------------- views

class NetworksProvider implements vscode.TreeDataProvider<vscode.TreeItem> {
  private readonly changed = new vscode.EventEmitter<void>();
  readonly onDidChangeTreeData = this.changed.event;
  private cache?: NetworkInfo[];

  refresh(): void { this.cache = undefined; this.changed.fire(); }

  getTreeItem(item: vscode.TreeItem): vscode.TreeItem { return item; }

  async getChildren(parent?: vscode.TreeItem): Promise<vscode.TreeItem[]> {
    try {
      this.cache ??= await cnetJson<NetworkInfo[]>(['networks']);
    } catch (e) {
      const item = new vscode.TreeItem(e instanceof CliError ? e.message : String(e));
      item.iconPath = new vscode.ThemeIcon('warning');
      return [item];
    }
    if (!parent) {
      return [...new Set(this.cache.map(n => n.family))].map(f => {
        const item = new vscode.TreeItem(f, vscode.TreeItemCollapsibleState.Collapsed);
        item.contextValue = 'family';
        item.iconPath = new vscode.ThemeIcon('globe');
        return item;
      });
    }
    return this.cache.filter(n => n.family === parent.label).map(n => {
      const item = new vscode.TreeItem(n.key);
      item.description = `${n.name} · ${n.symbol}${n.testnet ? '' : ' · mainnet'}`;
      item.iconPath = new vscode.ThemeIcon(n.testnet ? 'beaker' : 'star-full');
      item.tooltip = t('Check a balance on {0}', n.name);
      item.command = { command: 'cryptonet.checkBalance', title: '', arguments: [n.key] };
      return item;
    });
  }
}

class ActionsProvider implements vscode.TreeDataProvider<vscode.TreeItem> {
  getTreeItem(item: vscode.TreeItem): vscode.TreeItem { return item; }
  getChildren(): vscode.TreeItem[] {
    const actions: [string, string, string][] = [
      ['cryptonet.newWallet', t('New testnet wallet'), 'key'],
      ['cryptonet.checkBalance', t('Check balance'), 'credit-card'],
      ['cryptonet.validateAddress', t('Validate address'), 'pass'],
      ['cryptonet.convertUnits', t('Convert units'), 'arrow-swap'],
      ['cryptonet.generateClient', t('Generate contract client'), 'file-code'],
      ['cryptonet.newProject', t('New project from template'), 'new-folder'],
      ['cryptonet.startNode', t('Start local node'), 'server-environment'],
      ['cryptonet.openNotebook', t('Open a notebook'), 'notebook'],
      ['cryptonet.doctor', t('Run cnet doctor'), 'pulse'],
    ];
    return actions.map(([command, label, icon]) => {
      const item = new vscode.TreeItem(label);
      item.command = { command, title: label };
      item.iconPath = new vscode.ThemeIcon(icon);
      return item;
    });
  }
}

// ---------------------------------------------------------------- editor integration

class CryptoHoverProvider implements vscode.HoverProvider {
  provideHover(document: vscode.TextDocument, position: vscode.Position): vscode.Hover | undefined {
    if (!vscode.workspace.getConfiguration('cryptonet').get<boolean>('hover.enabled', true)) return undefined;
    const line = document.lineAt(position.line).text;
    const token = scanLine(line).find(tk => position.character >= tk.start && position.character <= tk.end);
    if (!token) return undefined;
    const range = new vscode.Range(position.line, token.start, position.line, token.end);
    const md = new vscode.MarkdownString();
    if (token.kind === 'evmAddress') {
      const state = checksumState(token.text);
      const checksummed = toChecksumAddress(token.text);
      md.appendMarkdown(`**${t('EVM address')}** — ${state === 'valid' ? t('EIP-55 checksum valid') : state === 'invalid' ? '⚠️ ' + t('EIP-55 checksum INVALID (possible typo)') : t('no checksum (single case)')}\n\n`);
      md.appendCodeblock(checksummed, 'text');
      md.appendMarkdown(Object.entries(EXPLORERS).map(([k, url]) => `[${k}](${url}${checksummed})`).join(' · '));
    } else {
      const value = BigInt(token.text.replace(/_/g, ''));
      md.appendMarkdown(`**${t('If these are base units')}**\n\n`);
      for (const l of describeBaseUnits(value)) md.appendMarkdown(`- ${l}\n`);
    }
    return new vscode.Hover(md, range);
  }
}

function updateDiagnostics(document: vscode.TextDocument, collection: vscode.DiagnosticCollection): void {
  if (document.languageId !== 'csharp' || !vscode.workspace.getConfiguration('cryptonet').get<boolean>('diagnostics.enabled', true)) {
    collection.delete(document.uri);
    return;
  }
  const diagnostics: vscode.Diagnostic[] = [];
  for (let i = 0; i < document.lineCount && i < 20_000; i++) {
    for (const token of scanLine(document.lineAt(i).text)) {
      if (token.kind === 'evmAddress' && checksumState(token.text) === 'invalid') {
        const d = new vscode.Diagnostic(new vscode.Range(i, token.start, i, token.end),
          t('EIP-55 checksum mismatch: expected {0}. A wrong character means funds would go to a different address.', toChecksumAddress(token.text)),
          vscode.DiagnosticSeverity.Warning);
        d.source = 'Crypto.Net';
        d.code = 'CNETVS001';
        diagnostics.push(d);
      }
    }
  }
  collection.set(document.uri, diagnostics);
}
