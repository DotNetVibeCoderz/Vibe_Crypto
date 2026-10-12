// Run with `npm test` (builds dist/core.js first).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const core = require('../dist/core.js');

test('EIP-55 checksums (vectors from the EIP)', () => {
  for (const a of ['0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed', '0xfB6916095ca1df60bB79Ce92cE3Ea74c37c5d359',
                   '0xdbF03B407c01E7cD3CBea99509d93f8DDDC8C6FB', '0xD1220A0cf47c7B9Be7A2E6BA89F429762e7b9aDb']) {
    assert.equal(core.toChecksumAddress(a.toLowerCase()), a);
    assert.equal(core.checksumState(a), 'valid');
  }
  assert.equal(core.checksumState('0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAeD'), 'invalid');
  assert.equal(core.checksumState('0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed'), 'unchecked');
});

test('exact unit formatting and parsing', () => {
  assert.equal(core.formatUnits(1500000000000000000n, 18), '1.5');
  assert.equal(core.formatUnits(1n, 18), '0.000000000000000001');
  assert.equal(core.formatUnits(-250000000n, 9), '-0.25');
  assert.equal(core.parseUnits('0.1', 18), 100000000000000000n);
  assert.equal(core.parseUnits('1_000.5', 6), 1000500000n);
  assert.throws(() => core.parseUnits('1.0000001', 6), /more than 6 decimals/);
  assert.throws(() => core.parseUnits('abc', 6));
});

test('hover descriptions cover every family', () => {
  const lines = core.describeBaseUnits(1000000000n);
  assert.equal(lines.length, 5);
  assert.match(lines[0], /= 1 gwei/);
  assert.match(lines[1], /= 1 SOL/);
});

test('scanning finds addresses and long integers only', () => {
  const line = 'var tx = Send("0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed", 1_000_000_000, 42, v1.2345678);';
  const tokens = core.scanLine(line);
  assert.deepEqual(tokens.map(t => t.kind), ['evmAddress', 'integer']);
  assert.equal(line.slice(tokens[1].start, tokens[1].end), '1_000_000_000');
  assert.equal(core.scanLine('0x' + 'a'.repeat(64)).length, 0); // tx hashes are not addresses
});
