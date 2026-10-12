// Pure helpers (no VS Code API) so they can be unit-tested with `node --test`.
import { keccak_256 } from '@noble/hashes/sha3';

const ADDRESS = /^0x[0-9a-fA-F]{40}$/;

/** EIP-55 mixed-case checksum of a 20-byte hex address. */
export function toChecksumAddress(address: string): string {
  if (!ADDRESS.test(address)) throw new Error(`Not an EVM address: ${address}`);
  const lower = address.slice(2).toLowerCase();
  const hash = Buffer.from(keccak_256(new TextEncoder().encode(lower))).toString('hex');
  let out = '0x';
  for (let i = 0; i < 40; i++) out += parseInt(hash[i], 16) >= 8 ? lower[i].toUpperCase() : lower[i];
  return out;
}

export type ChecksumState = 'valid' | 'invalid' | 'unchecked';

/** All-lower or all-upper addresses carry no checksum; mixed case must match EIP-55 exactly. */
export function checksumState(address: string): ChecksumState {
  const body = address.slice(2);
  if (body === body.toLowerCase() || body === body.toUpperCase()) return 'unchecked';
  return toChecksumAddress(address) === address ? 'valid' : 'invalid';
}

/** Units by family: decimals relative to the base unit. */
export const UNITS: { family: string; base: string; units: [string, number][] }[] = [
  { family: 'EVM', base: 'wei', units: [['gwei', 9], ['ETH', 18]] },
  { family: 'Solana', base: 'lamports', units: [['SOL', 9]] },
  { family: 'Bitcoin', base: 'sat', units: [['BTC', 8]] },
  { family: 'Polkadot', base: 'planck', units: [['DOT', 10], ['KSM', 12]] },
  { family: 'Cosmos', base: 'uatom', units: [['ATOM', 6]] },
];

/** Exact decimal string of `value / 10^decimals` (no floating point), trailing zeros trimmed. */
export function formatUnits(value: bigint, decimals: number): string {
  const negative = value < 0n;
  let v = negative ? -value : value;
  const scale = 10n ** BigInt(decimals);
  const whole = v / scale;
  const frac = (v % scale).toString().padStart(decimals, '0').replace(/0+$/, '');
  return (negative ? '-' : '') + whole.toString() + (frac ? '.' + frac : '');
}

/** Exact base units of a decimal string with `decimals` places; throws when precision would be lost. */
export function parseUnits(text: string, decimals: number): bigint {
  const m = /^(-?)(\d*)(?:\.(\d*))?$/.exec(text.trim().replace(/_/g, ''));
  if (!m || (m[2] === '' && !m[3])) throw new Error(`Not a number: ${text}`);
  const frac = (m[3] ?? '').replace(/0+$/, '');
  if (frac.length > decimals) throw new Error(`${text} has more than ${decimals} decimals`);
  const v = BigInt((m[2] || '0') + frac.padEnd(decimals, '0'));
  return m[1] ? -v : v;
}

/** Conversions shown when hovering a large integer literal (assumed to be base units). */
export function describeBaseUnits(value: bigint): string[] {
  const lines: string[] = [];
  for (const f of UNITS) {
    const parts = f.units.map(([name, d]) => `${formatUnits(value, d)} ${name}`);
    lines.push(`${f.family}: ${value} ${f.base} = ${parts.join(' = ')}`);
  }
  return lines;
}

export interface Token { kind: 'evmAddress' | 'integer'; text: string; start: number; end: number }

/** Finds EVM addresses and long integer literals (≥ 7 digits, underscores allowed) in a line of code. */
export function scanLine(line: string): Token[] {
  const tokens: Token[] = [];
  const re = /0x[0-9a-fA-F]{40}(?![0-9a-fA-F])|(?<![\w.])\d[\d_]{6,}(?![\w.])/g;
  let m: RegExpExecArray | null;
  while ((m = re.exec(line)) !== null) {
    const kind = m[0].startsWith('0x') ? 'evmAddress' : 'integer';
    tokens.push({ kind, text: m[0], start: m.index, end: m.index + m[0].length });
  }
  return tokens;
}

/** Network keys for explorer links on EVM addresses. */
export const EXPLORERS: Record<string, string> = {
  ethereum: 'https://etherscan.io/address/',
  sepolia: 'https://sepolia.etherscan.io/address/',
  polygon: 'https://polygonscan.com/address/',
  base: 'https://basescan.org/address/',
  arbitrum: 'https://arbiscan.io/address/',
  optimism: 'https://optimistic.etherscan.io/address/',
};
