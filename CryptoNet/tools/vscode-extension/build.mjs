// Bundles the extension (and the pure helper modules used by the tests) with esbuild.
import * as esbuild from 'esbuild';

const production = process.argv.includes('--production');
const watch = process.argv.includes('--watch');
const common = { bundle: true, platform: 'node', target: 'node18', format: 'cjs', sourcemap: !production, minify: production, logLevel: 'info' };

const contexts = await Promise.all([
  esbuild.context({ ...common, entryPoints: ['src/extension.ts'], outfile: 'dist/extension.js', external: ['vscode'] }),
  esbuild.context({ ...common, entryPoints: ['src/core.ts'], outfile: 'dist/core.js' }),
]);
if (watch) await Promise.all(contexts.map(c => c.watch()));
else { await Promise.all(contexts.map(c => c.rebuild())); await Promise.all(contexts.map(c => c.dispose())); }
