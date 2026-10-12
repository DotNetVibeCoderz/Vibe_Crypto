// WebAssembly host: starts .NET in the page and hands app.js an in-process API (see wasm-boot.js).
window.cryptonetReady = new Promise((resolve, reject) => {
  window.__cnetResolve = resolve;
  window.__cnetReject = reject;
});
(() => {
  const s = document.createElement("script");
  s.type = "module";
  s.src = "wasm-boot.js";
  document.head.appendChild(s);
})();
