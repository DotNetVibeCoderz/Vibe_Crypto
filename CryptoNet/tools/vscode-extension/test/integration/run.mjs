// Launches a downloaded, isolated VS Code with the extension loaded and runs suite.cjs inside it.
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { runTests } from '@vscode/test-electron';

const here = path.dirname(fileURLToPath(import.meta.url));
const extensionDevelopmentPath = path.resolve(here, '../..');
const workspace = path.resolve(here, 'workspace');

await runTests({
  extensionDevelopmentPath,
  extensionTestsPath: path.resolve(here, 'suite.cjs'),
  launchArgs: [workspace, '--disable-extensions', '--disable-workspace-trust', '--skip-welcome', '--skip-release-notes'],
});
