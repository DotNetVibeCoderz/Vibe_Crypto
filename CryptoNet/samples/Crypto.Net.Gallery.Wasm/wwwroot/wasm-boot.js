// Boots the .NET runtime and exposes Interop.Invoke to app.js.
import { dotnet } from "./_framework/dotnet.js";

try {
  const runtime = await dotnet.withApplicationArgumentsFromQuery().create();
  const config = runtime.getConfig();
  const exports = await runtime.getAssemblyExports(config.mainAssemblyName);
  await runtime.runMain(config.mainAssemblyName, []);
  window.__cnetResolve((method, path, body) => exports.Interop.Invoke(method, path, body));
} catch (err) {
  console.error("Crypto.Net WebAssembly failed to start", err);
  window.__cnetReject(err);
}

if ("serviceWorker" in navigator && location.protocol === "https:") {
  navigator.serviceWorker.register("sw.js").catch(() => { /* offline support is optional */ });
}
