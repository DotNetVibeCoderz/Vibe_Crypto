// Runs inside the VS Code extension host.
const assert = require('node:assert/strict');
const path = require('node:path');
const vscode = require('vscode');

async function waitFor(check, ms = 10000) {
  const end = Date.now() + ms;
  while (Date.now() < end) {
    const value = await check();
    if (value) return value;
    await new Promise(r => setTimeout(r, 200));
  }
  throw new Error('timed out');
}

exports.run = async function run() {
  const ext = vscode.extensions.getExtension('gravicode.crypto-net-tools');
  assert.ok(ext, 'extension is installed');

  const file = path.join(vscode.workspace.workspaceFolders[0].uri.fsPath, 'Sample.cs');
  const doc = await vscode.workspace.openTextDocument(file);
  await vscode.window.showTextDocument(doc);
  await waitFor(() => ext.isActive);

  const commands = await vscode.commands.getCommands(true);
  for (const c of ['cryptonet.newWallet', 'cryptonet.checkBalance', 'cryptonet.generateClient', 'cryptonet.newProject', 'cryptonet.startNode'])
    assert.ok(commands.includes(c), `command ${c} is registered`);

  // Hover over the long integer on line 3 and the address on line 2.
  const text = doc.getText().split('\n');
  const amountLine = text.findIndex(l => l.includes('1_500_000_000_000_000_000'));
  const amountHover = await vscode.commands.executeCommand('vscode.executeHoverProvider', doc.uri,
    new vscode.Position(amountLine, text[amountLine].indexOf('1_500') + 3));
  const amountText = amountHover.flatMap(h => h.contents.map(c => c.value ?? String(c))).join('\n');
  assert.match(amountText, /1\.5 ETH/, 'hover converts wei to ETH');

  const badLine = text.findIndex(l => l.includes('BadChecksum'));
  const diagnostics = await waitFor(() => {
    const d = vscode.languages.getDiagnostics(doc.uri).filter(x => x.source === 'Crypto.Net');
    return d.length ? d : null;
  });
  assert.equal(diagnostics.length, 1, 'only the mistyped address is flagged');
  assert.equal(diagnostics[0].range.start.line, badLine);
  assert.match(diagnostics[0].message, /0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed/);
  console.log('CRYPTONET_INTEGRATION_OK');
};
