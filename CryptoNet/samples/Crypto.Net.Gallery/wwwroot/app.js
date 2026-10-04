// Crypto.Net Gallery front-end. Built by Gravicode Studios, led by Kang Fadhil.
(() => {
  "use strict";

  const $ = (sel, root = document) => root.querySelector(sel);
  const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];
  const store = {
    get(k, d) { try { return localStorage.getItem(k) ?? d; } catch { return d; } },
    set(k, v) { try { localStorage.setItem(k, v); } catch { /* private mode */ } },
  };
  const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches
    || new URLSearchParams(location.search).get("still") === "1"; // still=1: static render for screenshots

  const state = {
    lang: store.get("cn.lang", (navigator.language || "en").toLowerCase().startsWith("id") ? "id" : "en"),
    mode: store.get("cn.mode", "testnet"),
    scheme: "eip191",
    wallet: null,
    bench: null,
    status: null,
  };

  // ------------------------------------------------------------------ i18n
  const t = (key, vars = {}) => {
    const s = (I18N[state.lang] && I18N[state.lang][key]) ?? I18N.en[key] ?? key;
    return s.replace(/\{(\w+)\}/g, (_, k) => vars[k] ?? "");
  };

  function applyI18n() {
    document.documentElement.lang = state.lang;
    $$("[data-i18n]").forEach(el => {
      const v = t(el.dataset.i18n);
      if (v.includes("<em>")) el.innerHTML = v; else el.textContent = v;
    });
    $$("[data-i18n-placeholder]").forEach(el => (el.placeholder = t(el.dataset.i18nPlaceholder)));
    $$("[data-i18n-aria]").forEach(el => el.setAttribute("aria-label", t(el.dataset.i18nAria)));
    $$("[data-lang]").forEach(b => b.setAttribute("aria-pressed", String(b.dataset.lang === state.lang)));
    renderEngine();
    if (state.wallet) renderWallet(state.wallet, false);
    if (state.bench) renderBench(state.bench);
    $("#reveal").textContent = $("#phrase").classList.contains("blurred") ? t("reveal") : t("hide");
  }

  // ------------------------------------------------------------------ helpers
  async function api(path, body) {
    const res = await fetch("/api" + path, body === undefined
      ? {}
      : { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(data.error || res.statusText);
    return data;
  }

  let toastTimer;
  function toast(msg) {
    const el = $("#toast");
    el.textContent = msg;
    el.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => (el.hidden = true), 2200);
  }

  async function copy(text) {
    try { await navigator.clipboard.writeText(text); toast(t("copied")); } catch { /* clipboard unavailable */ }
  }

  const esc = s => String(s).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const fmt = n => n >= 1e6 ? (n / 1e6).toFixed(n >= 1e7 ? 0 : 1) + "M" : n >= 1e3 ? (n / 1e3).toFixed(n >= 1e4 ? 0 : 1) + "k" : n.toFixed(n >= 100 ? 0 : 1);

  function busy(btn, on, label) {
    if (!btn) return;
    if (on) { btn.dataset.label = btn.textContent; btn.textContent = label || "…"; btn.disabled = true; }
    else { btn.textContent = btn.dataset.label || btn.textContent; btn.disabled = false; }
  }

  // ------------------------------------------------------------------ router
  const autorun = new URLSearchParams(location.search).get("autorun") === "1"; // used for documentation screenshots
  const autoran = new Set();
  const pages = ["wallet", "inspect", "convert", "sign", "keystore", "networks", "bench"];
  function route() {
    const page = pages.includes(location.hash.slice(1)) ? location.hash.slice(1) : "wallet";
    $$(".page").forEach(p => (p.hidden = p.dataset.page !== page));
    $$(".rail a").forEach(a => a.dataset.page === page ? a.setAttribute("aria-current", "page") : a.removeAttribute("aria-current"));
    if (page === "networks" && !$("#net-grid").children.length) loadNetworks();
    if (page === "convert" && !$("#conv-from").options.length) loadUnits();
    if (page === "inspect" && !$("#inspect-result").children.length) inspect();
    if (autorun && !autoran.has(page)) {
      autoran.add(page);
      if (page === "sign") sign();
      if (page === "keystore") encryptKeystore();
      if (page === "bench") runBench();
    }
    $("#main").focus({ preventScroll: true });
    window.scrollTo(0, 0);
  }

  // ------------------------------------------------------------------ engine status + mode + theme
  function renderEngine() {
    const s = state.status;
    if (!s) return;
    const el = $("#engine");
    el.classList.toggle("native", s.nativeLoaded);
    el.classList.toggle("managed", !s.nativeLoaded);
    $("#engine-label").textContent = s.nativeLoaded ? t("engineNative", { abi: s.abiVersion }) : t("engineManaged");
    el.title = `${s.rid} · ${s.runtime}`;
  }

  function setMode(mode) {
    state.mode = mode;
    store.set("cn.mode", mode);
    $$("[data-mode]").forEach(b => b.setAttribute("aria-pressed", String(b.dataset.mode === mode)));
    $("#mainnet-banner").hidden = mode !== "mainnet";
    if (state.wallet) restoreWallet(state.wallet.mnemonic, true);
    if ($("#net-grid").children.length) loadNetworks();
  }

  function setTheme(theme) {
    if (theme) document.documentElement.dataset.theme = theme; else delete document.documentElement.dataset.theme;
    store.set("cn.theme", theme || "");
  }

  // ------------------------------------------------------------------ rosette (signature element)
  function bytesOf(hex) {
    const out = [];
    for (let i = 0; i < hex.length; i += 2) out.push(parseInt(hex.substr(i, 2), 16));
    return out;
  }

  function ringPath(R, k, amp, k2, amp2, phase) {
    const steps = Math.max(480, k * 30);
    let d = "";
    for (let i = 0; i <= steps; i++) {
      const th = (i / steps) * Math.PI * 2;
      const r = R + amp * Math.sin(k * th + phase) + amp2 * Math.sin(k2 * th - phase * 2);
      const x = (r * Math.cos(th)).toFixed(2), y = (r * Math.sin(th)).toFixed(2);
      d += (i ? "L" : "M") + x + " " + y;
    }
    return d + "Z";
  }

  function drawRosette(hex) {
    const b = bytesOf(hex.padEnd(64, "0"));
    const svg = $("#rosette");
    const ns = "http://www.w3.org/2000/svg";
    $$("g, defs", svg).forEach(n => n.remove());
    const g = document.createElementNS(ns, "g");

    const rings = [
      { R: 168, k: 14 + (b[0] % 10), amp: 9 + (b[1] % 8), copies: 34 + (b[2] % 12), cls: "ring-ink" },
      { R: 126, k: 9 + (b[3] % 9), amp: 16 + (b[4] % 10), copies: 18 + (b[5] % 8), cls: "ring-ovi" },
      { R: 84, k: 7 + (b[6] % 7), amp: 11 + (b[7] % 7), copies: 16 + (b[8] % 8), cls: "ring-ink" },
      { R: 40, k: 5 + (b[9] % 5), amp: 9 + (b[10] % 5), copies: 12 + (b[11] % 6), cls: "ring-fine" },
    ];
    rings.forEach((ring, ri) => {
      const k2 = ring.k * 2 + 1 + (b[12 + ri] % 3);
      const amp2 = 1.5 + (b[16 + ri] % 4);
      for (let c = 0; c < ring.copies; c++) {
        const p = document.createElementNS(ns, "path");
        p.setAttribute("d", ringPath(ring.R, ring.k, ring.amp, k2, amp2, (2 * Math.PI * c) / (ring.copies * ring.k)));
        p.setAttribute("class", ring.cls);
        g.appendChild(p);
      }
    });

    // Microprint, as on a banknote: legible only up close.
    const defs = document.createElementNS(ns, "defs");
    const circle = document.createElementNS(ns, "path");
    circle.setAttribute("id", "micro-path");
    circle.setAttribute("d", "M 0 -191 A 191 191 0 1 1 -0.01 -191");
    defs.appendChild(circle);
    const text = document.createElementNS(ns, "text");
    const tp = document.createElementNS(ns, "textPath");
    tp.setAttribute("href", "#micro-path");
    tp.textContent = ("CRYPTO.NET · GRAVICODE STUDIOS · KANG FADHIL · " + hex.slice(0, 16).toUpperCase() + " · ").repeat(3);
    text.appendChild(tp);
    g.appendChild(text);
    svg.appendChild(defs);
    svg.appendChild(g);

    if (!reduceMotion) {
      $$("path", g).forEach(p => p.style.setProperty("--len", Math.ceil(p.getTotalLength())));
      svg.classList.remove("drawing");
      void svg.getBoundingClientRect();
      svg.classList.add("drawing");
    }
    $("#fp").textContent = hex.slice(0, 8) + "…" + hex.slice(-8);
  }

  // ------------------------------------------------------------------ wallet
  const chainLabels = {
    "evm": "Ethereum · EVM", "btc-segwit": "Bitcoin · SegWit", "btc-taproot": "Bitcoin · Taproot",
    "sol": "Solana", "dot": "Polkadot", "atom": "Cosmos Hub",
  };

  function renderWallet(w, animate = true) {
    state.wallet = w;
    if (animate) drawRosette(w.fingerprint);
    const phrase = $("#phrase");
    phrase.innerHTML = w.mnemonic.split(" ").map(word => `<li><span>${esc(word)}</span></li>`).join("");
    $("#accounts").innerHTML = w.accounts.map(a => `
      <div class="acct">
        <div class="acct-chain">${esc(chainLabels[a.id] || a.chain)}<small>${esc(a.chain)} · ${esc(a.curve)}</small></div>
        <div><span class="acct-addr">${esc(a.address)}</span><span class="acct-path">${esc(a.path)}</span></div>
        <button class="copy" type="button" data-copy="${esc(a.address)}">${t("copy")}</button>
      </div>`).join("");
    $("#wallet-meta").textContent = t("walletMeta", { fp: w.masterFingerprint, xpub: w.xpub });
  }

  async function generateWallet() {
    const btn = $("#wallet-form .btn.primary");
    busy(btn, true);
    try {
      const w = await api("/wallet/generate", { words: +$("#words").value, passphrase: $("#passphrase").value || null, testnet: state.mode === "testnet" });
      $("#phrase").classList.add("blurred");
      $("#reveal").textContent = t("reveal");
      $("#reveal").setAttribute("aria-pressed", "false");
      renderWallet(w);
    } catch (e) { toast(t("requestFailed", { msg: e.message })); }
    finally { busy(btn, false); }
  }

  async function restoreWallet(mnemonic, quiet) {
    try {
      const w = await api("/wallet/restore", { mnemonic, passphrase: $("#passphrase").value || null, testnet: state.mode === "testnet" });
      renderWallet(w, !quiet);
    } catch (e) { toast(e.message); }
  }

  // ------------------------------------------------------------------ inspect
  async function inspect() {
    const out = $("#inspect-result");
    try {
      const r = await api("/address/inspect", { address: $("#inspect-input").value });
      if (!r.valid) {
        out.innerHTML = `<div class="verdict bad">${iconX()}<span>${esc(t("invalidAddress"))}</span></div>`;
        return;
      }
      out.innerHTML = `
        <div class="verdict ok">${iconCheck()}<span>${esc(t("validFor"))}</span></div>
        <div>${r.matches.map(m => `<span class="tag">${esc(m.family)} · ${esc(m.format)}</span>`).join("")}</div>
        ${r.details.length ? `<dl class="kv">${r.details.map(d => `<dt>${esc(d.label)}</dt><dd>${esc(d.value)}</dd>`).join("")}</dl>` : ""}`;
    } catch (e) { out.innerHTML = `<p class="error">${esc(e.message)}</p>`; }
  }

  const iconCheck = () => `<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="10" fill="none" stroke="currentColor" stroke-width="2"/><path d="M7 12.5l3.2 3.2L17 9" fill="none" stroke="currentColor" stroke-width="2"/></svg>`;
  const iconX = () => `<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="10" fill="none" stroke="currentColor" stroke-width="2"/><path d="M8 8l8 8M16 8l-8 8" stroke="currentColor" stroke-width="2"/></svg>`;

  // ------------------------------------------------------------------ convert
  let units = [];
  async function loadUnits() {
    units = await api("/units");
    const opts = units.map(g => `<optgroup label="${esc(g.family.toUpperCase())}">${g.units.map(u => `<option value="${esc(u.name)}">${esc(u.name)}</option>`).join("")}</optgroup>`).join("");
    $("#conv-from").innerHTML = opts;
    $("#conv-from").value = "ether";
    syncToUnits("gwei");
    convert();
  }

  function syncToUnits(preferred) {
    const from = $("#conv-from").value;
    const fam = units.find(g => g.units.some(u => u.name === from));
    $("#conv-to").innerHTML = fam.units.filter(u => u.name !== from).map(u => `<option value="${esc(u.name)}">${esc(u.name)}</option>`).join("");
    if (preferred && fam.units.some(u => u.name === preferred)) $("#conv-to").value = preferred;
  }

  let convTimer;
  function convert() {
    clearTimeout(convTimer);
    convTimer = setTimeout(async () => {
      const out = $("#conv-out");
      try {
        const r = await api("/convert", { value: $("#conv-value").value, from: $("#conv-from").value, to: $("#conv-to").value });
        out.innerHTML = `${esc(r.output)}<span class="unit">${esc($("#conv-to").value)}</span>`;
      } catch (e) { out.innerHTML = `<span class="error">${esc(e.message)}</span>`; }
    }, 120);
  }

  // ------------------------------------------------------------------ sign
  const sampleMessages = {
    eip191: "Sign in to Crypto.Net Gallery\nNonce: 8f2c1a",
    ed25519: "Hello Solana from .NET 10",
    sr25519: "Hello Polkadot from .NET 10",
    schnorr: "Taproot says hello",
    eip712: JSON.stringify({
      types: {
        EIP712Domain: [{ name: "name", type: "string" }, { name: "version", type: "string" }, { name: "chainId", type: "uint256" }, { name: "verifyingContract", type: "address" }],
        Person: [{ name: "name", type: "string" }, { name: "wallet", type: "address" }],
        Mail: [{ name: "from", type: "Person" }, { name: "to", type: "Person" }, { name: "contents", type: "string" }],
      },
      primaryType: "Mail",
      domain: { name: "Ether Mail", version: "1", chainId: 1, verifyingContract: "0xCcCCccccCCCCcCCCCCCcCcCccCcCCCcCcccccccC" },
      message: {
        from: { name: "Cow", wallet: "0xCD2a3d9F938E13CD947Ec05AbC7FE734Df8DD826" },
        to: { name: "Bob", wallet: "0xbBbBBBBbbBBBbbbBbbBbbbbBBbBbbbbBbBbbBBbB" },
        contents: "Hello, Bob!",
      },
    }, null, 2),
  };

  function setScheme(s) {
    state.scheme = s;
    $$("[data-scheme]").forEach(b => b.setAttribute("aria-pressed", String(b.dataset.scheme === s)));
    $("#sign-message").value = sampleMessages[s];
    $("#sign-result").innerHTML = "";
  }

  async function sign() {
    const out = $("#sign-result");
    const btn = $("#sign-form .btn.primary");
    busy(btn, true);
    try {
      const r = await api("/sign", { scheme: state.scheme, message: $("#sign-message").value, mnemonic: state.wallet?.mnemonic });
      out.innerHTML = `
        <div class="verdict ${r.verified ? "ok" : "bad"}">${r.verified ? iconCheck() : iconX()}<span>${esc(r.verified ? t("verified") : t("notVerified"))}</span></div>
        <dl class="kv">
          <dt>${t("scheme")}</dt><dd>${esc(r.scheme)}</dd>
          <dt>${t("signer")}</dt><dd>${esc(r.signer)}</dd>
          ${r.digest ? `<dt>${t("digest")}</dt><dd>${esc(r.digest)}</dd>` : ""}
          <dt>${t("signature")}</dt><dd>${esc(r.signature)}</dd>
          <dt></dt><dd>${esc(r.note)}</dd>
        </dl>`;
    } catch (e) { out.innerHTML = `<p class="error">${esc(e.message)}</p>`; }
    finally { busy(btn, false); }
  }

  // ------------------------------------------------------------------ keystore
  let lastKeystore = null;
  async function encryptKeystore() {
    const out = $("#ks-result");
    const btn = $("#ks-form .btn.primary");
    busy(btn, true);
    try {
      const r = await api("/keystore/encrypt", { privateKey: $("#ks-key").value || null, password: $("#ks-password").value, kdf: $("#ks-kdf").value });
      lastKeystore = r.keystore;
      out.innerHTML = `<div class="verdict ok">${iconCheck()}<span>${esc(t("encrypted", { ms: r.milliseconds, address: r.address }))}</span></div>
        <button class="copy" type="button" data-copy-keystore>${t("copy")}</button>
        <pre class="keystore mono">${esc(r.keystore)}</pre>`;
      $("#ks-decrypt").hidden = false;
      $("#ks-dresult").textContent = "";
    } catch (e) { out.innerHTML = `<p class="error">${esc(e.message)}</p>`; }
    finally { busy(btn, false); }
  }

  async function decryptKeystore() {
    const out = $("#ks-dresult");
    try {
      const r = await api("/keystore/decrypt", { keystore: lastKeystore, password: $("#ks-dpass").value });
      out.className = "note";
      out.textContent = t("decrypted", { address: r.address });
    } catch (e) { out.className = "note error"; out.textContent = e.message; }
  }

  // ------------------------------------------------------------------ networks
  let networks = [];
  async function loadNetworks() {
    if (!networks.length) networks = await api("/networks");
    const visible = networks.filter(n => !n.local && (state.mode === "mainnet" || n.testnet));
    const grid = $("#net-grid");
    grid.innerHTML = visible.map(n => `
      <article class="net" data-key="${esc(n.key)}" data-mainnet="${!n.testnet}">
        <span class="net-family">${esc(n.family)}${n.testnet ? " · testnet" : ""}</span>
        <span class="net-name">${esc(n.name)}</span>
        <span class="net-height pending">${t("loading")}</span>
        <span class="net-meta"></span>
      </article>`).join("");
    $("#bal-net").innerHTML = visible.map(n => `<option value="${esc(n.key)}">${esc(n.name)}</option>`).join("");
    await Promise.all(visible.map(async n => {
      const card = $(`.net[data-key="${CSS.escape(n.key)}"]`, grid);
      try {
        const r = await api(`/networks/${encodeURIComponent(n.key)}/head`);
        const h = $(".net-height", card);
        if (r.error) {
          h.textContent = "—";
          $(".net-meta", card).innerHTML = `<span class="net-err">${esc(r.error.slice(0, 90))}</span>`;
        } else {
          h.classList.remove("pending");
          h.textContent = Number(r.height).toLocaleString(state.lang === "id" ? "id-ID" : "en-US");
          $(".net-meta", card).textContent = `${n.family === "Solana" ? t("slot") : t("block")} · ${r.milliseconds} ms`;
        }
      } catch (e) { $(".net-meta", card).textContent = e.message; }
    }));
  }

  async function checkBalance() {
    const out = $("#bal-out");
    out.textContent = t("loading");
    try {
      const r = await api(`/networks/${encodeURIComponent($("#bal-net").value)}/balance/${encodeURIComponent($("#bal-address").value.trim())}`);
      out.innerHTML = `${esc(r.balance)}<span class="unit">${esc(r.symbol)}</span>`;
    } catch (e) { out.innerHTML = `<span class="error">${esc(e.message)}</span>`; }
  }

  // ------------------------------------------------------------------ benchmark chart
  async function runBench() {
    const btn = $("#bench-run");
    busy(btn, true, t("running"));
    try {
      state.bench = await api("/benchmark", { scale: 0.6 });
      renderBench(state.bench);
    } catch (e) { toast(t("requestFailed", { msg: e.message })); }
    finally { busy(btn, false); }
  }

  function renderBench(data) {
    const rows = data.results;
    const W = 860, labelW = 210, speedW = 84, rowH = 38, top = 26, barH = 10, gap = 2;
    const plotW = W - labelW - speedW;
    const values = rows.flatMap(r => [r.nativeOpsPerSec, r.managedOpsPerSec]).filter(v => v > 0);
    const lo = Math.pow(10, Math.floor(Math.log10(Math.min(...values))));
    const hi = Math.pow(10, Math.ceil(Math.log10(Math.max(...values))));
    const x = v => labelW + (Math.log10(Math.max(v, lo)) - Math.log10(lo)) / (Math.log10(hi) - Math.log10(lo)) * plotW;
    const H = top + rows.length * rowH + 8;
    const bar = (x0, x1, y, cls) => {
      const w = Math.max(x1 - x0, 2), r = Math.min(4, w / 2, barH / 2);
      return `<path class="${cls}" d="M${x0} ${y}H${x0 + w - r}a${r} ${r} 0 0 1 ${r} ${r}V${y + barH - r}a${r} ${r} 0 0 1 -${r} ${r}H${x0}Z"/>`;
    };

    let svg = `<svg viewBox="0 0 ${W} ${H}" role="img" aria-label="${esc(t("benchTitle"))}">`;
    for (let p = Math.log10(lo); p <= Math.log10(hi); p++) {
      const gx = x(Math.pow(10, p));
      svg += `<line class="grid" x1="${gx}" x2="${gx}" y1="${top - 8}" y2="${H - 6}"/>`;
      svg += `<text class="axis-label" x="${gx}" y="${top - 12}" text-anchor="middle">${fmt(Math.pow(10, p))}</text>`;
    }
    rows.forEach((r, i) => {
      const y = top + i * rowH + 6;
      svg += `<text class="row-label" x="0" y="${y + 9}">${esc(r.name)}</text>`;
      svg += `<text class="row-cat" x="0" y="${y + 23}">${esc(r.category)}</text>`;
      if (r.nativeOpsPerSec > 0) svg += bar(labelW, x(r.nativeOpsPerSec), y, "bar-rust");
      svg += bar(labelW, x(r.managedOpsPerSec), y + barH + gap, "bar-managed");
      const sp = r.speedup;
      svg += `<text class="speed ${sp < 1 ? "slow" : ""}" x="${W - 4}" y="${y + barH + 4}" text-anchor="end">${sp > 0 ? sp.toFixed(sp >= 10 ? 0 : 1) + "×" : "—"}</text>`;
      svg += `<rect class="hit" x="0" y="${y - 6}" width="${W}" height="${rowH}" data-row="${i}"/>`;
    });
    svg += `</svg>`;
    $("#bench-chart").innerHTML = svg + (data.nativeAvailable ? "" : `<p class="note">${esc(t("nativeMissing"))}</p>`);

    const tip = $("#bench-tip");
    $$(".hit", $("#bench-chart")).forEach(h => {
      h.addEventListener("mousemove", ev => {
        const r = rows[+h.dataset.row];
        tip.innerHTML = `<strong>${esc(r.name)}</strong><br>${t("rust")}: ${r.nativeOpsPerSec ? Math.round(r.nativeOpsPerSec).toLocaleString() : "—"} ${t("opsPerSec")}<br>${t("managed")}: ${Math.round(r.managedOpsPerSec).toLocaleString()} ${t("opsPerSec")}`;
        tip.hidden = false;
        tip.style.left = Math.min(ev.clientX + 14, window.innerWidth - 240) + "px";
        tip.style.top = ev.clientY + 14 + "px";
      });
      h.addEventListener("mouseleave", () => (tip.hidden = true));
    });

    $("#bench-table").innerHTML = `<table class="data"><thead><tr><th>${t("operation")}</th><th>${t("rust")} ${t("opsPerSec")}</th><th>${t("managed")} ${t("opsPerSec")}</th><th>${t("speedup")}</th></tr></thead><tbody>${
      rows.map(r => `<tr><td>${esc(r.name)}</td><td>${r.nativeOpsPerSec ? Math.round(r.nativeOpsPerSec).toLocaleString() : "—"}</td><td>${Math.round(r.managedOpsPerSec).toLocaleString()}</td><td>${r.speedup ? r.speedup.toFixed(1) + "×" : "—"}</td></tr>`).join("")
    }</tbody></table>`;
  }

  // ------------------------------------------------------------------ code samples
  function highlight(code) {
    return esc(code)
      .replace(/(\/\/.*)$/gm, '<span class="com">$1</span>')
      .replace(/(&quot;[^&]*?&quot;|\$&quot;.*?&quot;)/g, '<span class="str">$1</span>')
      .replace(/\b(using|var|await|foreach|in|new|string|bool|byte|ulong|return|static)\b/g, '<span class="kw">$1</span>')
      .replace(/\b(HdWallet|EvmChain|BitcoinNetwork|BitcoinAddressType|SolanaChain|PolkadotChain|CosmosChain|Amount|UnitConverter|EvmMessageSigner|PolkadotAccount|EvmAccount|KeyStoreKdf|ChainCatalog|EvmAddress|BitcoinAddress|CryptoNative|CryptoBackend|CryptoBenchmark|Console|File|IChainClient|Address|ChainId)\b/g, '<span class="typ">$1</span>');
  }

  // ------------------------------------------------------------------ wiring
  function wire() {
    window.addEventListener("hashchange", route);
    $$("[data-lang]").forEach(b => b.addEventListener("click", () => { state.lang = b.dataset.lang; store.set("cn.lang", state.lang); applyI18n(); }));
    $$("[data-mode]").forEach(b => b.addEventListener("click", () => setMode(b.dataset.mode)));
    $("#theme-toggle").addEventListener("click", () => {
      const dark = document.documentElement.dataset.theme === "dark" ||
        (!document.documentElement.dataset.theme && matchMedia("(prefers-color-scheme: dark)").matches);
      setTheme(dark ? "light" : "dark");
    });

    $("#wallet-form").addEventListener("submit", e => { e.preventDefault(); generateWallet(); });
    $("#restore-form").addEventListener("submit", e => { e.preventDefault(); restoreWallet($("#restore-input").value); });
    $("#reveal").addEventListener("click", () => {
      const blurred = $("#phrase").classList.toggle("blurred");
      $("#reveal").textContent = blurred ? t("reveal") : t("hide");
      $("#reveal").setAttribute("aria-pressed", String(!blurred));
    });
    $$(".tab").forEach(tab => tab.addEventListener("click", () => {
      $$(".tab", tab.parentElement).forEach(x => x.setAttribute("aria-selected", String(x === tab)));
      ["wallet-demo", "wallet-code"].forEach(id => ($("#" + id).hidden = id !== tab.dataset.tab));
    }));
    document.addEventListener("click", e => {
      const c = e.target.closest("[data-copy]");
      if (c) copy(c.dataset.copy);
      if (e.target.closest("[data-copy-keystore]") && lastKeystore) copy(lastKeystore);
    });

    $("#inspect-form").addEventListener("submit", e => { e.preventDefault(); inspect(); });
    $$("[data-example]").forEach(c => c.addEventListener("click", () => { $("#inspect-input").value = c.dataset.example; inspect(); }));

    $("#conv-value").addEventListener("input", convert);
    $("#conv-from").addEventListener("change", () => { syncToUnits(); convert(); });
    $("#conv-to").addEventListener("change", convert);
    $("#convert-form").addEventListener("submit", e => e.preventDefault());

    $$("[data-scheme]").forEach(b => b.addEventListener("click", () => setScheme(b.dataset.scheme)));
    $("#sign-form").addEventListener("submit", e => { e.preventDefault(); sign(); });

    $("#ks-form").addEventListener("submit", e => { e.preventDefault(); encryptKeystore(); });
    $("#ks-decrypt").addEventListener("submit", e => { e.preventDefault(); decryptKeystore(); });

    $("#net-refresh").addEventListener("click", loadNetworks);
    $("#bal-form").addEventListener("submit", e => { e.preventDefault(); checkBalance(); });

    $("#bench-run").addEventListener("click", runBench);
    $("#bench-table-toggle").addEventListener("click", e => {
      const showTable = $("#bench-table").hidden;
      $("#bench-table").hidden = !showTable;
      $("#bench-chart").hidden = showTable;
      e.currentTarget.setAttribute("aria-pressed", String(showTable));
      e.currentTarget.textContent = showTable ? t("showChart") : t("showTable");
    });

    $$("pre.code[data-code]").forEach(pre => (pre.innerHTML = highlight(CODE_SAMPLES[pre.dataset.code] || "")));
  }

  async function init() {
    const params = new URLSearchParams(location.search);
    if (params.get("lang") === "id" || params.get("lang") === "en") state.lang = params.get("lang");
    const theme = params.get("theme") || store.get("cn.theme", "");
    if (theme) setTheme(theme);
    wire();
    setScheme("eip191");
    $$("[data-mode]").forEach(b => b.setAttribute("aria-pressed", String(b.dataset.mode === state.mode)));
    $("#mainnet-banner").hidden = state.mode !== "mainnet";
    drawRosette("6a3ce02e7d640f2a24e9eeeac2410c0f8a6300e5a0ffb0206a3ce02e7d640f2a");
    applyI18n();
    route();
    try { state.status = await api("/status"); renderEngine(); } catch { /* backend offline */ }
    generateWallet();
  }

  init();
})();
