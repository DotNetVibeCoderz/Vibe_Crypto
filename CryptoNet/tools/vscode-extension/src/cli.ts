// Runs the `cnet` CLI (the extension's backend) and parses its --json output.
import { execFile } from 'node:child_process';
import * as vscode from 'vscode';

export class CliError extends Error {
  constructor(message: string, readonly output: string) { super(message); }
}

function cliPath(): string {
  return vscode.workspace.getConfiguration('cryptonet').get<string>('cliPath') || 'cnet';
}

/** Runs `cnet <args> --json` and returns the parsed JSON. */
export function cnetJson<T>(args: string[], token?: vscode.CancellationToken): Promise<T> {
  return new Promise((resolve, reject) => {
    const child = execFile(cliPath(), [...args, '--json'], { timeout: 120_000, maxBuffer: 16 * 1024 * 1024, windowsHide: true }, (error, stdout, stderr) => {
      const text = stdout.trim();
      if (text.startsWith('{') || text.startsWith('[')) {
        try { resolve(JSON.parse(text) as T); return; } catch { /* fall through */ }
      }
      if (error && (error as NodeJS.ErrnoException).code === 'ENOENT') {
        reject(new CliError(vscode.l10n.t('The cnet CLI was not found. Install it with "dotnet tool install -g Crypto.Net.Cli" or set cryptonet.cliPath.'), ''));
        return;
      }
      reject(new CliError(stderr.trim() || text || String(error), stdout + stderr));
    });
    token?.onCancellationRequested(() => child.kill());
  });
}

/** Runs a cnet command in a terminal (for interactive or long-running commands). */
export function cnetTerminal(name: string, args: string[], cwd?: string): vscode.Terminal {
  const terminal = vscode.window.createTerminal({ name, cwd });
  terminal.show();
  terminal.sendText([cliPath(), ...args].map(a => (/\s/.test(a) ? `"${a}"` : a)).join(' '));
  return terminal;
}

export interface NetworkInfo { key: string; family: string; name: string; symbol: string; testnet: boolean }
export interface WalletInfo { mnemonic: string | null; network: string; accounts: { chain: string; path: string; address: string }[] }
export interface ValidateInfo { address: string; valid: boolean; matches: { family: string; format: string }[] }
export interface BalanceInfo { address: string; network: string; balance: string; baseUnits: string; symbol: string }
export interface ConvertInfo { output: string }
export interface GenInfo { output: string; kind: string }
