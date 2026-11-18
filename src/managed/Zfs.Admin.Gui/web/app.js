// The ZfsWinGui web app.  Everything it reads or changes goes through one
// allow-listed command table in the window (WebCommands.cs); this page never
// talks to the network.  A refresh updates the page in place (morph), so
// scroll position, focus, open sections and half-made choices survive the
// 5-second refresh.
'use strict';
(() => {
  const webview = window.chrome && window.chrome.webview;
  const $ = (s, el = document) => el.querySelector(s);

  // --- markup ---------------------------------------------------------------
  class Raw { constructor(s) { this.s = s; } toString() { return this.s; } }
  const ESC = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' };
  const esc = (v) => String(v).replace(/[&<>"']/g, (c) => ESC[c]);
  function fmt(v) {
    if (v instanceof Raw) return v.s;
    if (Array.isArray(v)) return v.map(fmt).join('');
    if (v === null || v === undefined || v === false) return '';
    return esc(v);
  }
  function html(strings, ...values) {
    let out = strings[0];
    for (let i = 0; i < values.length; i++) out += fmt(values[i]) + strings[i + 1];
    return new Raw(out);
  }
  const attr = (on, name) => (on ? new Raw(' ' + name) : '');

  // --- formatting -----------------------------------------------------------
  const UNITS = ['B', 'KiB', 'MiB', 'GiB', 'TiB', 'PiB'];
  function bytes(n) {
    if (n === null || n === undefined) return '–';
    let v = Number(n), i = 0;
    while (Math.abs(v) >= 1024 && i < UNITS.length - 1) { v /= 1024; i++; }
    return i === 0 ? `${v.toFixed(0)} B` : `${v.toFixed(v < 10 ? 1 : 0)} ${UNITS[i]}`;
  }
  const num = (n) => (n === null || n === undefined ? '–' : Number(n).toLocaleString());
  const pct = (f, digits) => (f === null || f === undefined || !isFinite(f) ? '–' : `${(f * 100).toFixed(digits ?? (f < 0.1 ? 1 : 0))}%`);
  const when = (iso) => (iso ? new Date(iso).toLocaleString(undefined, { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }) : '–');
  function ago(iso) {
    const s = (Date.now() - new Date(iso)) / 1000;
    if (s < 60) return 'just now';
    if (s < 3600) return `${Math.floor(s / 60)} min ago`;
    if (s < 86400) return `${Math.floor(s / 3600)} h ago`;
    return `${Math.floor(s / 86400)} d ago`;
  }
  const tone = (state) => (state === null || state === undefined || state === 'ONLINE' ? 'ok' : state === 'DEGRADED' ? 'warn' : 'bad');
  const chip = (text, t) => html`<span class="chip ${t}">${text}</span>`;
  const shortSnap = (name) => name.slice(name.indexOf('@') + 1);
  const stamp = () => { const d = new Date(), p = (n) => String(n).padStart(2, '0'); return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}-${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`; };
  function scanText(scan) {
    if (!scan || !scan.state) return 'no scrub has run';
    const what = (scan.function || 'scan').toLowerCase();
    if (scan.state === 'SCANNING') {
      const progress = scan.toExamine > 0 ? `, ${pct((scan.examined || 0) / scan.toExamine, 1)}` : '';
      return `${what} ${scan.paused ? 'paused' : 'running'}${progress}`;
    }
    if (scan.state === 'FINISHED') return `${what} finished, ${scan.errors ?? 0} errors`;
    return `${what} ${scan.state.toLowerCase()}`;
  }
  function windowsTone(text) {
    if (!text) return 'plain';
    if (text.startsWith('Healthy')) return 'ok';
    if (text.startsWith('Warning')) return 'warn';
    if (text.startsWith('Unhealthy')) return 'bad';
    return 'plain';
  }

  // --- the window -------------------------------------------------------------
  class HostError extends Error {
    constructor(e) { super(e.message || 'The operation failed.'); this.code = e.code; this.detail = e.detail; }
  }
  let sequence = 0;
  const pending = new Map();
  function call(cmd, args) {
    return new Promise((resolve, reject) => {
      if (!webview) { reject(new HostError({ code: 'internal', message: 'This page only runs inside ZfsWinGui.' })); return; }
      const id = 'r' + (++sequence);
      pending.set(id, { resolve, reject });
      webview.postMessage(JSON.stringify({ id, cmd, args: args || {} }));
    });
  }
  if (webview) {
    webview.addEventListener('message', (e) => {
      const m = e.data;
      if (!m || typeof m !== 'object') return;
      if (m.type === 'result') {
        const p = pending.get(m.id);
        if (!p) return;
        pending.delete(m.id);
        if (m.ok) p.resolve(m.data); else p.reject(new HostError(m.error || {}));
      } else if (m.type === 'show' && VIEWS[m.view]) {
        show(m.view);
      } else if (m.type === 'window') {
        S.minimized = !!m.minimized;
        if (!S.minimized) refresh();
        scheduleRates();
      }
    });
  }

  // --- state ------------------------------------------------------------------
  const S = {
    view: 'overview', pool: null, app: null, minimized: false, busy: false,
    data: {}, errors: {}, loading: {},
    sel: { dataset: null, snapshot: null, disk: null, mount: null },
    snapDataset: null, pendingProps: {}, propFilter: '',
    rates: { pool: null, every: 5, paused: false, series: [], last: null },
  };
  const poolDatasets = () => (S.app?.datasets || []).filter((d) => d === S.pool || d.startsWith(S.pool + '/'));
  const appPool = () => (S.app?.pools || []).find((p) => p.name === S.pool);

  // --- rendering ----------------------------------------------------------------
  const main = $('#view');
  function applyWidths(root) {
    for (const el of root.querySelectorAll('[data-w]')) el.style.width = `${Math.max(0, Math.min(100, Number(el.dataset.w) || 0))}%`;
  }
  // A new view replaces the page; a refresh of the same view updates it in
  // place.  morph() keeps every element that is still there and changes only
  // the text and attributes that differ, so focus, hover, scrolled tables,
  // opened help sections and the page's scroll position survive.
  function render(markup) {
    const tpl = document.createElement('template');
    tpl.innerHTML = markup.toString();
    if (main.dataset.view !== S.view) {
      main.replaceChildren(tpl.content);
      main.dataset.view = S.view;
      main.scrollTop = 0;
    } else {
      morph(main, tpl.content);
    }
    applyWidths(main);
  }
  const sameKind = (a, b) => a.nodeType === b.nodeType && a.nodeName === b.nodeName && (a.nodeType !== 1 || (a.dataset?.key ?? null) === (b.dataset?.key ?? null));
  function morph(target, source) {
    const before = [...target.childNodes], after = [...source.childNodes];
    after.forEach((to, i) => {
      const from = before[i];
      if (!from) { target.appendChild(to); return; }
      if (!sameKind(from, to)) { target.replaceChild(to, from); return; }
      if (from.nodeType !== 1) { if (from.nodeValue !== to.nodeValue) from.nodeValue = to.nodeValue; return; }
      morphAttributes(from, to);
      morph(from, to);
      morphState(from);
    });
    for (let i = before.length - 1; i >= after.length; i--) before[i].remove();
  }
  function morphAttributes(from, to) {
    // style comes from data-w (applyWidths); a <details> stays as the user left it.
    const keep = (name) => name === 'style' || (name === 'open' && from.nodeName === 'DETAILS');
    for (const { name } of [...from.attributes]) if (!keep(name) && !to.hasAttribute(name)) from.removeAttribute(name);
    for (const { name, value } of [...to.attributes]) if (!keep(name) && from.getAttribute(name) !== value) from.setAttribute(name, value);
  }
  // What a control shows once the user touched it no longer follows its
  // attributes; every select and checkbox here records its change at once,
  // so the markup is the truth.  Typed text is left alone.
  function morphState(el) {
    if (el.nodeName === 'SELECT') {
      const i = [...el.options].findIndex((o) => o.hasAttribute('selected'));
      el.selectedIndex = i >= 0 ? i : 0;
    } else if (el.nodeName === 'INPUT' && (el.type === 'checkbox' || el.type === 'radio')) {
      el.checked = el.hasAttribute('checked');
    }
  }
  const emptyState = (title, text, extra) => html`<div class="panel empty"><strong>${title}</strong><span>${text}</span>${extra || ''}</div>`;

  function drawFrame() {
    const app = S.app;
    $('#edition').textContent = app?.edition || '';
    $('#versions').textContent = app?.brokerAvailable ? `ZfsWinBroker ${app.broker ?? '?'}\nOpenZFS ${app.openzfs ?? '?'}` : 'ZfsWinBroker not running';
    const select = $('#pool-select');
    const names = (app?.pools || []).map((p) => p.name);
    if (!names.includes(S.pool)) S.pool = names[0] ?? null;
    const options = names.map((n) => html`<option${attr(n === S.pool, 'selected')}>${n}</option>`);
    const markup = fmt(options);
    if (select.dataset.src !== markup) { select.innerHTML = markup; select.dataset.src = markup; }
    select.value = S.pool ?? '';
    $('#context').hidden = names.length === 0;
    const p = appPool();
    const chipEl = $('#pool-chip');
    const chipMarkup = p ? fmt(chip(p.deviceProblems > 0 && p.health === 'ONLINE' ? `ONLINE · ${p.deviceProblems} device issue${p.deviceProblems === 1 ? '' : 's'}` : p.health, p.deviceProblems > 0 && p.health === 'ONLINE' ? 'warn' : tone(p.health))) : '';
    if (chipEl.dataset.src !== chipMarkup) { chipEl.innerHTML = chipMarkup; chipEl.dataset.src = chipMarkup; }
    setBadge('devices', p?.deviceProblems || 0, 'warn');
    setBadge('health', p?.errorCount || 0, 'bad');
    for (const b of document.querySelectorAll('nav.side button[data-view]')) {
      b.toggleAttribute('aria-current', false);
      if (b.dataset.view === S.view) b.setAttribute('aria-current', 'page');
      const off = !app?.brokerAvailable && VIEWS[b.dataset.view].broker;
      b.setAttribute('aria-disabled', off ? 'true' : 'false');
    }
    const live = $('#live');
    live.className = 'live' + (S.minimized ? ' off' : app && !app.brokerAvailable ? ' down' : '');
    live.textContent = app ? `${app.brokerAvailable ? 'Live' : 'Broker not running'} · updated ${new Date().toLocaleTimeString()}` : 'Connecting…';
  }
  function setBadge(name, count, kind) {
    const b = document.querySelector(`[data-badge="${name}"]`);
    b.hidden = count === 0;
    b.textContent = String(count);
    b.className = 'badge' + (kind === 'bad' ? ' bad' : '');
  }

  function drawView() {
    const view = VIEWS[S.view];
    if (view.broker && S.app && !S.app.brokerAvailable) { render(brokerDown()); return; }
    if (view.pool && S.app && !S.pool) { render(emptyState('No pool is imported.', 'Import or create a pool with zpool; it appears here within a few seconds.')); return; }
    const error = S.errors[S.view];
    if (!S.data[S.view] && error) { render(emptyState(`${view.title} could not be loaded.`, error.message)); return; }
    if (!S.data[S.view]) { render(html`<div class="vhead"><h1>${view.title}</h1><span class="sub">Loading…</span></div>`); return; }
    render(view.draw());
  }
  function brokerDown() {
    const service = S.app.service;
    return html`<div class="vhead"><h1>${VIEWS[S.view].title}</h1></div>
      <div class="banner"><strong>ZfsWinBroker is not running.</strong><span>${S.app.brokerError || ''} Start it with <span class="mono">net start ZfsWinBroker</span>; this window reconnects by itself.</span></div>
      ${service !== 'NotInstalled' ? html`<p class="hint">The user-mode edition's service is ${service}. Pool administration needs it running.</p><div><button type="button" class="btn" data-act="goto" data-view="service">Open User-mode service</button></div>` : ''}`;
  }

  // --- loading ----------------------------------------------------------------
  async function loadApp() {
    try {
      S.app = await call('app.state');
    } catch (e) {
      setStatus(`Status: ${e.message}`);
    }
    drawFrame();
  }
  async function loadView(name) {
    const view = VIEWS[name];
    if (S.loading[name] || !S.app || (view.broker && !S.app.brokerAvailable) || (view.pool && !S.pool)) { if (S.view === name) drawView(); return; }
    S.loading[name] = true;
    try {
      await view.load();
      S.errors[name] = null;
    } catch (e) {
      S.errors[name] = e;
      if (S.data[name]) setStatus(`${view.title}: ${e.message}`);
    } finally {
      S.loading[name] = false;
    }
    if (S.view === name) drawView();
  }
  let refreshing = false;
  async function refresh() {
    if (refreshing) return;
    refreshing = true;
    try {
      await loadApp();
      if (S.view !== 'performance') await loadView(S.view);
      else drawView();
    } finally {
      refreshing = false;
    }
  }
  function show(name) {
    if (!VIEWS[name]) return;
    S.view = name;
    main.dataset.view = '';
    main.scrollTop = 0;
    drawFrame();
    drawView();
    scheduleRates(name === 'performance' || (name === 'overview' && S.rates.series.length === 0));
    if (name !== 'performance') loadView(name);
  }

  // --- feedback ---------------------------------------------------------------
  function setStatus(text) { $('#status').textContent = text; }
  function toast(text, kind) {
    const t = document.createElement('div');
    t.className = 'toast' + (kind === 'bad' ? ' bad' : '');
    t.textContent = text;
    $('#toasts').append(t);
    setTimeout(() => t.remove(), kind === 'bad' ? 7000 : 4000);
  }

  // A modal <dialog>; onSubmit runs the change, and its error stays in the
  // dialog so the user can correct and retry.
  function openDialog(markup, { onSubmit, init } = {}) {
    return new Promise((resolve) => {
      const d = document.createElement('dialog');
      d.innerHTML = markup.toString();
      document.body.append(d);
      applyWidths(d);
      const form = d.querySelector('form');
      const error = d.querySelector('.error');
      let result = null;
      d.addEventListener('close', () => { d.remove(); resolve(result); });
      for (const c of d.querySelectorAll('[data-cancel]')) c.addEventListener('click', () => d.close());
      form.addEventListener('submit', async (ev) => {
        ev.preventDefault();
        const ok = form.querySelector('[type=submit]');
        ok.disabled = true;
        try {
          result = onSubmit ? (await onSubmit(new FormData(form), d)) ?? true : true;
          d.close();
        } catch (e) {
          if (error) { error.textContent = e.detail ? `${e.message} ${e.detail}` : e.message; error.hidden = false; }
          ok.disabled = false;
        }
      });
      if (init) init(d);
      d.showModal();
      (d.querySelector('[autofocus]') || d.querySelector('input, select, [type=submit]'))?.focus();
    });
  }
  function errorDialog(label, e) {
    return openDialog(html`<form><div class="dh"><span class="eyebrow danger">Failed</span><h2>${label}</h2></div>
      <div class="db"><p>${e.message}</p>${e.detail ? html`<details class="help" open><summary>Details from ZfsWinBroker</summary><pre>${e.detail}</pre></details>` : ''}</div>
      <div class="df"><button type="submit" class="btn primary">Close</button></div></form>`);
  }
  // Destructive changes: what happens, then the target's name typed again.
  // The window checks the typed text against the target before it acts.
  async function confirmChange({ eyebrow, title, items, token, ok, label, action }) {
    if (S.busy) { toast('Another change is still running.', 'bad'); return false; }
    const done = await openDialog(html`<form>
        <div class="dh"><span class="eyebrow danger">${eyebrow}</span><h2>${title}</h2></div>
        <div class="db"><ul>${items.map((i) => html`<li>${i}</li>`)}</ul>
          ${token !== null ? html`<label class="field"><span>Type <b class="mono">${token}</b> to confirm</span><input type="text" name="confirm" autocomplete="off" spellcheck="false" autofocus></label>` : ''}
          <p class="error" hidden></p></div>
        <div class="df"><button type="button" class="btn" data-cancel>Cancel</button><button type="submit" class="btn danger-solid"${attr(token !== null, 'disabled')}>${ok}</button></div></form>`, {
      init: (d) => {
        if (token === null) return;
        const input = d.querySelector('[name=confirm]'), button = d.querySelector('[type=submit]');
        input.addEventListener('input', () => { button.disabled = input.value.trim() !== token; });
      },
      onSubmit: async (form) => {
        S.busy = true;
        setStatus(`${label}…`);
        try { await action(token === null ? undefined : String(form.get('confirm')).trim()); } finally { S.busy = false; }
      },
    });
    if (done) { setStatus(`${label}: done.`); toast(`${label}: done.`); await refresh(); }
    return !!done;
  }
  // A change without a confirmation step.
  async function run(label, action) {
    if (S.busy) { toast('Another change is still running.', 'bad'); return undefined; }
    S.busy = true;
    setStatus(`${label}…`);
    try {
      const result = await action();
      setStatus(`${label}: done.`);
      toast(`${label}: done.`);
      return result;
    } catch (e) {
      setStatus(`${label}: failed.`);
      errorDialog(`${label} failed`, e);
      return undefined;
    } finally {
      S.busy = false;
      await refresh();
    }
  }

  // --- Overview -----------------------------------------------------------------
  function drawOverview() {
    const d = S.data.overview;
    const p = d.pools.find((x) => x.pool.name === S.pool);
    if (!p) return emptyState('Loading pool…', S.pool);
    const pool = p.pool, status = p.status, health = pool.health || status?.state || 'UNKNOWN';
    const used = pool.allocated ?? 0, free = pool.free ?? 0, size = pool.size ?? used + free;
    const fraction = size > 0 ? used / size : 0;
    // used and logicalused include children: count only the top-most datasets.
    const names = new Set(p.datasets.map((x) => x.name));
    const topMost = p.datasets.filter((x) => !x.name.split('/').slice(0, -1).some((_, i, parts) => names.has(parts.slice(0, i + 1).join('/'))));
    const compression = topMost.reduce((s, x) => s + (x.saved || 0), 0);
    const ratio = parseFloat(String(pool.dedupRatio || '1').replace(/x$/i, '')) || 1;
    const dedup = ratio > 1 ? Math.round(used * (ratio - 1)) : 0;
    const clone = p.blockCloneSaved || 0;
    const saved = compression + dedup + clone;
    const scale = Math.max(1, saved);
    const rootRatio = p.datasets.find((x) => x.name === pool.name)?.compressRatio;
    const a = appPool();
    const attention = [];
    if (health !== 'ONLINE') attention.push(html`<div class="item bad"><span><b>${pool.name} is ${health}.</b> ${status?.status || ''} ${status?.action || ''}</span><button type="button" class="btn" data-act="goto" data-view="devices">Open Devices</button></div>`);
    if (p.damagedFiles > 0) attention.push(html`<div class="item bad"><span><b>${num(p.damagedFiles)} data error${p.damagedFiles === 1 ? '' : 's'}.</b> Files with blocks that failed their checksum.</span><button type="button" class="btn" data-act="goto" data-view="health">Review files</button></div>`);
    if (a?.deviceProblems > 0 && health === 'ONLINE') attention.push(html`<div class="item"><span><b>${a.deviceProblems} device${a.deviceProblems === 1 ? ' reports' : 's report'} errors or is not ONLINE.</b></span><button type="button" class="btn" data-act="goto" data-view="devices">Review devices</button></div>`);
    if (p.redundancy?.warning) attention.push(html`<div class="item"><span><b>No spare copy.</b> ${p.redundancy.text}</span><button type="button" class="btn" data-act="goto" data-view="devices">Add a mirror</button></div>`);
    if (fraction > 0.8) attention.push(html`<div class="item${fraction > 0.9 ? ' bad' : ''}"><span><b>${pct(fraction)} full.</b> ZFS slows down and fragments as a pool fills; keep it below 80%.</span></div>`);
    const scanning = status?.scan?.state === 'SCANNING';
    const counted = Object.values(p.counts).some((c) => c.files !== null && c.files !== undefined);
    const files = Object.values(p.counts).reduce((s, c) => s + (c.files || 0), 0);
    const folders = Object.values(p.counts).reduce((s, c) => s + (c.folders || 0), 0);
    const countedAt = Object.values(p.counts).filter((c) => c.files !== null && c.files !== undefined).map((c) => c.countedAt).sort()[0];
    return html`
      <div class="vhead"><h1 class="mono-title">${pool.name}</h1>
        <span class="sub" data-region="sub">${p.redundancy ? `${p.redundancy.layout} · ` : ''}last scrub: ${scanText(status?.scan)}</span>
        <div class="actions" data-region="actions">${scanning
          ? html`<button type="button" class="btn" data-act="scrub" data-action="${status.scan.paused ? 'start' : 'pause'}">${status.scan.paused ? 'Resume scrub' : 'Pause scrub'}</button><button type="button" class="btn" data-act="scrub" data-action="stop">Stop scrub</button>`
          : html`<button type="button" class="btn primary" data-act="scrub" data-action="start">Start scrub</button>`}</div></div>
      <div class="attn" data-region="attention">${attention}</div>
      <div class="grid3" data-region="cards">
        <section class="panel"><h2>Capacity</h2>
          <div class="big">${bytes(used)} <small>of ${bytes(size)}</small></div>
          <div class="bar" role="img" aria-label="${pct(fraction)} used"><i class="${fraction > 0.9 ? 'fill-bad' : fraction > 0.8 ? 'fill-warn' : 'fill-accent'}" data-w="${fraction * 100}"></i></div>
          <div class="legend"><span>${pct(fraction)} used</span><span>${bytes(free)} free</span><span>${pool.fragmentationPercent ?? '–'}% fragmented</span></div></section>
        <section class="panel"><h2>Space saved</h2>
          <div class="big">${bytes(saved)} <small>saved in total</small></div>
          <div class="bar" role="img" aria-label="Saved by compression, deduplication and block cloning"><i class="fill-c1" data-w="${compression / scale * 100}"></i><i class="fill-c2" data-w="${dedup / scale * 100}"></i><i class="fill-c3" data-w="${clone / scale * 100}"></i></div>
          <div class="legend"><span><b class="fill-c1"></b>compression ${bytes(compression)}${rootRatio ? ` (${rootRatio}x)` : ''}</span><span><b class="fill-c2"></b>dedup about ${bytes(dedup)} (${ratio.toFixed(2)}x)</span><span><b class="fill-c3"></b>cloning ${bytes(clone)}</span></div></section>
        <section class="panel"><h2>Copies of your data</h2>
          ${p.redundancy ? html`<div class="big">${p.redundancy.healthyCopies} cop${p.redundancy.healthyCopies === 1 ? 'y' : 'ies'}</div><p class="hint">${p.redundancy.text}</p>` : html`<p class="hint">Not reported.</p>`}</section>
      </div>
      <div class="charts">${chartPanel('mb', true)}${chartPanel('iops', true)}</div>
      <section class="panel flush"><h2>Datasets</h2>
        <div class="row pad-x" data-region="count">
          ${counted ? html`<span><b>${num(files)}</b> files · <b>${num(folders)}</b> folders</span>${countedAt ? html`<span class="mut">counted ${ago(countedAt)}</span>` : ''}` : html`<span class="mut">Files not counted yet.</span>`}
          <span class="mut">${d.countText || ''}</span>
          ${d.counting ? html`<button type="button" class="btn small" data-act="count-cancel">Cancel count</button>` : html`<button type="button" class="btn small" data-act="count-start">Count files now</button>`}
        </div>
        <div class="table-wrap" data-region="datasets"><table class="t"><thead><tr><th>Dataset</th><th>Mounted at</th><th class="num">Logical</th><th class="num">On disk</th><th>Saved</th><th class="num">Files</th><th class="num">Folders</th></tr></thead><tbody>
          ${p.datasets.map((x) => {
            const logical = x.logicalUsed ?? x.used ?? 0, c = p.counts[x.name];
            return html`<tr><td class="mono">${x.name}</td><td>${x.mounted ? x.driveLetter || x.mountpoint || '' : html`<span class="mut">not mounted</span>`}</td><td class="num">${bytes(logical)}</td><td class="num">${bytes(x.used)}</td>
              <td><span class="bar thin"><i class="fill-c1" data-w="${logical > 0 ? x.saved / logical * 100 : 0}"></i></span> <span class="mut">${x.compressRatio ? `${x.compressRatio}x` : ''}</span></td>
              <td class="num">${num(c?.files)}</td><td class="num">${num(c?.folders)}</td></tr>`;
          })}</tbody></table></div>
      </section>
      <section class="panel" data-region="memory">${memoryPanel(d.memory)}</section>
      ${allPools()}`;
  }
  function allPools() {
    const pools = S.app?.pools || [];
    return html`<section class="panel flush"><h2>All pools</h2><div class="table-wrap" data-region="pools"><table class="t"><thead><tr><th>Pool</th><th>Health</th><th class="num">Size</th><th class="num">Allocated</th><th class="num">Free</th><th>Capacity</th><th class="num">Fragmentation</th><th class="num">Dedup</th><th>Last scrub</th><th class="num">Device issues</th></tr></thead><tbody>
      ${pools.map((p) => html`<tr tabindex="0" data-act="select-pool" data-key="${p.name}" aria-selected="${p.name === S.pool}" class="${tone(p.health) === 'ok' ? '' : tone(p.health)}"><td class="mono">${p.name}</td><td>${chip(p.health || 'UNKNOWN', tone(p.health))}</td><td class="num">${bytes(p.size)}</td><td class="num">${bytes(p.allocated)}</td><td class="num">${bytes(p.free)}</td>
        <td><span class="bar thin"><i class="${p.capacityPercent > 90 ? 'fill-bad' : p.capacityPercent > 80 ? 'fill-warn' : 'fill-accent'}" data-w="${p.capacityPercent ?? 0}"></i></span> ${p.capacityPercent ?? '–'}%</td>
        <td class="num">${p.fragmentationPercent ?? '–'}%</td><td class="num">${p.dedupRatio || '–'}</td><td>${scanText(p.scan)}</td><td class="num">${p.deviceProblems || 0}</td></tr>`)}
    </tbody></table></div></section>`;
  }
  function memoryPanel(m) {
    const arc = m?.arc;
    if (!arc) return html`<h2>ZFS memory</h2><p class="hint">ARC statistics are not available: neither the kernel driver (kstat) nor the user-mode service answered.</p>`;
    const mru = arc.mruSize || 0, mfu = arc.mfuSize || 0, all = Math.max(1, mru + mfu);
    const ddt = (m.dedupTables || []).reduce((s, t) => s + (t.item2 || 0), 0);
    return html`<h2>ZFS memory (ARC)</h2>
      <div class="row wide-gap">
        <div><div class="big">${bytes(arc.size)}</div><span class="mut">target ${bytes(arc.target)} · min ${bytes(arc.min)} · max ${bytes(arc.max)}</span></div>
        <div><div class="big">${pct(arc.hitRatio, 1)}</div><span class="mut">hits (${num(arc.hits)} / ${num(arc.misses)} misses)</span></div>
        ${arc.l2Size > 0 ? html`<div><div class="big">${bytes(arc.l2Size)}</div><span class="mut">L2ARC, ${pct(arc.l2HitRatio, 1)} hits</span></div>` : ''}
        ${ddt > 0 ? html`<div><div class="big">${bytes(ddt)}</div><span class="mut">dedup tables</span></div>` : ''}
        ${m.userModeServicePrivateBytes ? html`<div><div class="big">${bytes(m.userModeServicePrivateBytes)}</div><span class="mut">ZfsWinUserMode process</span></div>` : ''}
      </div>
      <div class="bar"><i class="fill-accent" data-w="${mru / all * 100}"></i><i class="fill-c2" data-w="${mfu / all * 100}"></i></div>
      <div class="legend"><span><b class="fill-accent"></b>recently used ${bytes(mru)}</span><span><b class="fill-c2"></b>frequently used ${bytes(mfu)}</span></div>`;
  }

  // --- Datasets -----------------------------------------------------------------
  async function loadDatasets() {
    const list = await call('datasets.get', { pool: S.pool });
    if (!list.some((x) => x.name === S.sel.dataset)) S.sel.dataset = list[0]?.name ?? null;
    const properties = S.sel.dataset ? await call('dataset.properties', { dataset: S.sel.dataset }) : [];
    S.data.datasets = { pool: S.pool, list, dataset: S.sel.dataset, properties };
  }
  const FEATURES = [
    ['compression', 'Compression', ['off', 'on', 'lz4', 'gzip', 'gzip-1', 'gzip-9', 'zle', 'lzjb', 'zstd', 'zstd-1', 'zstd-3', 'zstd-9', 'zstd-19', 'zstd-fast']],
    ['dedup', 'Deduplication', ['off', 'on', 'verify', 'sha256', 'sha256,verify', 'blake3', 'edonr,verify']],
    ['sync', 'Sync writes', ['standard', 'always', 'disabled']],
  ];
  const OTHER = '__other__';
  // What zfs accepts as compression, dedup or sync values: zstd-5, gzip-4, skein,verify…
  const FEATURE_VALUE = /^[a-z0-9][a-z0-9,-]{0,31}$/;
  function drawDatasets() {
    const d = S.data.datasets;
    const ds = d.list.find((x) => x.name === S.sel.dataset);
    const properties = d.dataset === S.sel.dataset ? d.properties : [];
    const prop = (name) => properties.find((p) => p.name === name)?.value ?? '';
    const changes = S.pendingProps[S.sel.dataset] || {};
    const changeCount = Object.keys(changes).length;
    const filter = S.propFilter.toLowerCase();
    return html`
      <div class="vhead"><h1>Datasets</h1><span class="sub">${d.list.length} in ${d.pool}</span></div>
      <div class="split">
        <section class="panel flush"><div class="table-wrap" data-region="list"><table class="t"><thead><tr><th>Dataset</th><th class="num">Used</th><th class="num">Available</th><th>Compression</th><th>Mounted at</th></tr></thead><tbody>
          ${d.list.map((x) => html`<tr tabindex="0" data-act="select-dataset" data-key="${x.name}" aria-selected="${x.name === S.sel.dataset}"><td class="mono">${x.name}</td><td class="num">${bytes(x.used)}</td><td class="num">${bytes(x.available)}</td><td>${x.compression || ''}</td><td>${x.mounted ? x.driveLetter || x.mountpoint || '' : html`<span class="mut">not mounted</span>`}</td></tr>`)}
        </tbody></table></div></section>
        <aside class="panel stack">
          <div class="stack" data-region="detail">${ds ? html`
            <h2>Selected</h2><div class="mono"><b>${ds.name}</b></div>
            <dl class="kv"><dt>Used</dt><dd>${bytes(ds.used)}</dd><dt>Available</dt><dd>${bytes(ds.available)}</dd><dt>Compress ratio</dt><dd>${ds.compressRatio ? `${ds.compressRatio}x` : '–'}</dd><dt>Copies</dt><dd>${ds.copies}</dd><dt>Checksum</dt><dd>${ds.checksum || prop('checksum') || '–'}</dd></dl>
            <h2>Features for new writes</h2>
            <div class="features">${FEATURES.map(([name, label, values]) => {
              const current = prop(name), chosen = changes[name] ?? current;
              const list = [...new Set([current, chosen, ...values].filter(Boolean))];
              return html`<label for="f-${name}">${label}</label><select id="f-${name}" data-change="prop" data-prop="${name}" class="${changes[name] !== undefined ? 'changed' : ''}">${list.map((v) => html`<option${attr(v === chosen, 'selected')}>${v}</option>`)}<option value="${OTHER}">Other value…</option></select>`;
            })}</div>
            ${changeCount > 0 ? html`<div class="pending"><span>${changeCount} change${changeCount === 1 ? '' : 's'} not applied</span><button type="button" class="btn small" data-act="props-undo">Undo</button><button type="button" class="btn small primary" data-act="props-apply">Apply to new writes</button></div>` : ''}
            <details class="help"><summary>What these change</summary><p>Changes apply to data written afterwards; existing blocks keep their compression and dedup state. Dedup needs RAM for its table and pays off only for genuinely duplicate data. sync=disabled is faster but loses the last seconds of writes on a power cut.</p></details>`
            : html`<p class="hint">Select a dataset.</p>`}</div>
          <h2>All properties</h2>
          <input type="text" class="filter-input" placeholder="Filter properties" aria-label="Filter properties" data-input="prop-filter">
          <div class="table-wrap" data-region="props"><table class="t"><thead><tr><th>Property</th><th>Value</th><th>Source</th></tr></thead><tbody>
            ${properties.filter((p) => !filter || p.name.includes(filter) || String(p.value).toLowerCase().includes(filter)).map((p) => html`<tr><td>${p.name}</td><td class="mono">${p.value ?? ''}</td><td class="mut">${p.source ?? ''}</td></tr>`)}
          </tbody></table></div>
        </aside>
      </div>`;
  }

  // --- Snapshots ------------------------------------------------------------------
  async function loadSnapshots() {
    const datasets = poolDatasets();
    if (!datasets.includes(S.snapDataset)) S.snapDataset = datasets[0] ?? null;
    if (!S.snapDataset) { S.data.snapshots = { dataset: null, snapshots: [], mounts: {} }; return; }
    const d = await call('snapshots.get', { dataset: S.snapDataset });
    if (!d.snapshots.some((s) => s.name === S.sel.snapshot)) S.sel.snapshot = d.snapshots[0]?.name ?? null;
    S.data.snapshots = d;
  }
  function drawSnapshots() {
    const d = S.data.snapshots;
    const s = d.snapshots.find((x) => x.name === S.sel.snapshot);
    const mount = s ? d.mounts[s.name] : null;
    const held = d.snapshots.reduce((n, x) => n + (x.used || 0), 0);
    return html`
      <div class="vhead"><h1>Snapshots</h1>
        <label for="snap-dataset" class="mut">of</label>
        <select id="snap-dataset" data-change="snap-dataset">${poolDatasets().map((n) => html`<option${attr(n === S.snapDataset, 'selected')}>${n}</option>`)}</select>
        <span class="sub" data-region="sum">${d.snapshots.length} snapshot${d.snapshots.length === 1 ? '' : 's'} · ${bytes(held)} held only by snapshots</span>
        <div class="actions"><button type="button" class="btn primary" data-act="snapshot-create"${attr(!d.dataset, 'disabled')}>Create snapshot</button></div></div>
      <div class="split">
        <section class="panel flush"><div class="table-wrap" data-region="list">${d.snapshots.length === 0
          ? html`<div class="empty"><strong>No snapshots of ${d.dataset || 'this pool'} yet.</strong><span>A snapshot keeps the dataset's state; it costs nothing until files change.</span></div>`
          : html`<table class="t"><thead><tr><th>Snapshot</th><th>Created</th><th class="num">Used</th><th class="num">Referenced</th><th>Mount</th></tr></thead><tbody>
            ${d.snapshots.map((x) => html`<tr tabindex="0" data-act="select-snapshot" data-key="${x.name}" aria-selected="${x.name === S.sel.snapshot}"><td class="mono">@${x.snapshot}</td><td>${when(x.creation)}</td><td class="num">${bytes(x.used)}</td><td class="num">${bytes(x.referenced)}</td><td>${d.mounts[x.name] ? chip(`${d.mounts[x.name]} read-only`, 'ok') : ''}</td></tr>`)}
          </tbody></table>`}</div></section>
        <aside class="panel stack">
          <div class="stack" data-region="detail">${s ? html`
            <h2>Selected</h2><div class="mono"><b>@${s.snapshot}</b></div>
            <dl class="kv"><dt>Created</dt><dd>${when(s.creation)}</dd><dt>Held only here</dt><dd>${bytes(s.used)}</dd><dt>Referenced</dt><dd>${bytes(s.referenced)}</dd><dt>Mounted</dt><dd>${mount ? `${mount} (temporary, read-only)` : 'no'}</dd></dl>
            <div class="stack">${mount
              ? html`<button type="button" class="btn" data-act="snapshot-open">Open ${mount} in Explorer</button><button type="button" class="btn" data-act="snapshot-unmount">Unmount ${mount}</button>`
              : html`<button type="button" class="btn" data-act="snapshot-mount">Mount read-only and browse…</button>`}</div>
            <details class="help"><summary>Restoring single files</summary><p>Mount the snapshot, then copy files back in Explorer. The mount is a read-only clone that uses no space; it is removed when you unmount it, when this window closes, or the next time the broker starts.</p></details>
            <div class="zone"><span class="label">Changes data</span>
              <button type="button" class="btn danger" data-act="snapshot-rollback">Roll back dataset…</button>
              <button type="button" class="btn danger" data-act="snapshot-destroy">Destroy snapshot…</button></div>`
            : html`<p class="hint">Select a snapshot.</p>`}
            <label class="check"><input type="checkbox" data-change="keep-mounted"${attr(d.keepMounted, 'checked')}>Keep temporary mounts when this window closes</label></div>
        </aside>
      </div>`;
  }

  // --- Devices ------------------------------------------------------------------
  function vdevLabel(v) {
    if (v.type === 'mirror') return `${v.children.length}-way mirror`;
    if (/^raidz/.test(v.name) || v.type === 'raidz' || v.type === 'draid') return `${v.name.replace(/-\d+$/, '')} of ${v.children.length}`;
    return v.type || '';
  }
  const leavesOf = (v) => (v.isLeaf ? [v] : v.children.flatMap(leavesOf));
  function drawDevices() {
    const d = S.data.devices;
    const st = d.status;
    const health = new Map(d.leaves.map((l) => [l.device, l]));
    const groups = [
      ['data', 'data', st.vdevs.flatMap((r) => (r.type === 'root' ? r.children : [r]))],
      ['log', 'write log (SLOG)', st.logs], ['cache', 'read cache (L2ARC)', st.caches],
      ['spare', 'spare', st.spares], ['special', 'special', st.special], ['dedup', 'dedup', st.dedup],
    ];
    const sel = S.sel.disk;
    const disk = (leaf, role, parent) => {
      const dev = leaf.path ?? leaf.name, h = health.get(dev) || {};
      const errors = leaf.readErrors + leaf.writeErrors + leaf.checksumErrors;
      const t = h.problem ? (tone(leaf.state) === 'bad' || h.windows?.startsWith('Unhealthy') ? 'bad' : 'warn') : '';
      return html`<button type="button" class="disk ${t}" data-act="select-disk" data-key="${dev}" data-role="${role}" data-parent="${parent?.name ?? ''}" data-parent-type="${parent?.type ?? ''}" aria-pressed="${sel?.dev === dev}">
        <span class="name">${h.name || dev.replace(/^\\\\[?.]\\/, '')}</span>
        <span class="meta">${leaf.state || ''} · errors ${leaf.readErrors}/${leaf.writeErrors}/${leaf.checksumErrors}${h.temperatureC ? ` · ${h.temperatureC} °C` : ''}${h.wearPercent !== null && h.wearPercent !== undefined ? ` · ${h.wearPercent}% wear` : ''}</span>
        ${h.windows ? chip(h.windows, windowsTone(h.windows)) : ''}${errors > 0 && !h.windows ? chip(`${errors} errors`, 'warn') : ''}</button>`;
    };
    const topology = groups.filter(([, , list]) => list.length > 0).map(([role, title, list]) => list.map((v) => html`
      <div class="vdev"><div class="vh"><b>${v.isLeaf ? (role === 'data' ? 'single disk' : role) : v.name}</b><span class="mut">${title}${v.isLeaf ? '' : ` · ${vdevLabel(v)}`}</span>${chip(v.state || 'ONLINE', tone(v.state))}
        ${role === 'log' && !v.isLeaf ? html`<button type="button" class="btn small danger end" data-act="device-remove" data-device="${v.name}" data-what="the write log mirror ${v.name}">Remove log mirror…</button>` : ''}</div>
        <div class="disks">${leavesOf(v).map((leaf) => disk(leaf, role, v.isLeaf ? null : v))}</div></div>`));
    const h = sel ? health.get(sel.dev) : null;
    const scan = st.scan;
    const resilvering = scan?.function === 'RESILVER' && scan?.state === 'SCANNING';
    return html`
      <div class="vhead"><h1>Devices</h1><span class="sub">${st.name}</span>
        <div class="actions"><button type="button" class="btn" data-act="device-add" data-role="cache">Add read cache…</button><button type="button" class="btn" data-act="device-add" data-role="log">Add write log…</button></div></div>
      <div class="stack" data-region="copies">
        <div class="copies${d.redundancy.warning ? ' warn' : ''}">${d.redundancy.text}</div>
        ${resilvering ? html`<div class="row"><span>Resilvering: ${pct((scan.examined || 0) / Math.max(1, scan.toExamine || 1), 1)} (${bytes(scan.examined)} of ${bytes(scan.toExamine)})</span><progress max="100" value="${Math.round((scan.examined || 0) / Math.max(1, scan.toExamine || 1) * 100)}"></progress></div>` : ''}
      </div>
      <div class="split">
        <div class="topo" data-region="topology">${topology}</div>
        <aside class="panel stack" data-region="detail">${sel && h ? html`
          <h2>Selected device</h2><div><b>${h.name || sel.dev}</b></div>
          <dl class="kv"><dt>Device</dt><dd class="mono">${h.device}</dd><dt>Role</dt><dd>${sel.role}${sel.parent ? ` in ${sel.parent}` : ''}</dd><dt>ZFS state</dt><dd>${h.state || '–'}</dd><dt>Errors r/w/c</dt><dd>${h.readErrors}/${h.writeErrors}/${h.checksumErrors}</dd>
            <dt>Windows</dt><dd>${h.windows}</dd><dt>Media</dt><dd>${h.media || '–'}</dd><dt>Temperature</dt><dd>${h.temperatureC ? `${h.temperatureC} °C` : '–'}</dd><dt>Wear</dt><dd>${h.wearPercent ?? '–'}${h.wearPercent !== null && h.wearPercent !== undefined ? '%' : ''}</dd>
            <dt>Power-on hours</dt><dd>${num(h.powerOnHours)}</dd><dt>Uncorrected reads</dt><dd>${h.note || num(h.readErrorsUncorrected)}</dd></dl>
          ${h.problem ? html`<p class="hint warn-text">This device reports a problem. Plan to replace it: add a mirror member first, then detach this one.</p>` : ''}
          ${deviceActions(sel)}`
          : html`<p class="hint">Select a device for its details and actions.</p><details class="help"><summary>Mirrors, read cache and write log</summary><p>A mirror keeps a full copy on every member; ZFS resilvers a new member automatically. A read cache (L2ARC) is a fast device that holds data evicted from RAM. A write log (SLOG) speeds up synchronous writes; mirror it, because it holds writes not yet on the pool.</p></details>`}</aside>
      </div>
      <section class="panel flush"><h2>Drive health</h2><div class="table-wrap" data-region="health"><table class="t"><thead><tr><th>Device</th><th>Role</th><th>ZFS state</th><th class="num">Errors r/w/c</th><th>Windows</th><th>Media</th><th class="num">Temperature</th><th class="num">Wear</th><th class="num">Power-on hours</th><th class="num">Uncorrected reads</th></tr></thead><tbody>
        ${d.leaves.map((l) => html`<tr class="${l.problem ? (tone(l.state) === 'bad' ? 'bad' : 'warn') : ''}"><td class="mono">${l.name ? `${l.device} (${l.name})` : l.device}</td><td>${l.role}</td><td>${l.state || ''}</td><td class="num">${l.readErrors}/${l.writeErrors}/${l.checksumErrors}</td><td>${l.windows}</td><td>${l.media || '–'}</td><td class="num">${l.temperatureC ? `${l.temperatureC} °C` : '–'}</td><td class="num">${l.wearPercent !== null && l.wearPercent !== undefined ? `${l.wearPercent}%` : '–'}</td><td class="num">${num(l.powerOnHours)}</td><td class="num">${l.note || num(l.readErrorsUncorrected)}</td></tr>`)}
      </tbody></table></div></section>
      <section class="panel flush"><h2>Copies of your data, per dataset</h2><div class="table-wrap" data-region="per-dataset"><table class="t"><thead><tr><th>Dataset</th><th class="num">copies</th><th>Copies of the data</th></tr></thead><tbody>
        ${d.copies.map((c) => html`<tr class="${c.redundancy.warning ? 'warn' : ''}"><td class="mono">${c.name}</td><td class="num">${c.copies}</td><td class="wrap">${c.redundancy.text}</td></tr>`)}
      </tbody></table></div></section>`;
  }
  function deviceActions(sel) {
    if (sel.role === 'data') {
      return html`<div class="stack"><button type="button" class="btn primary" data-act="device-attach">Add a mirror member…</button></div>
        ${sel.parentType === 'mirror' ? html`<div class="zone"><span class="label">Reduces redundancy</span><button type="button" class="btn danger" data-act="device-detach">Detach from mirror…</button></div>` : ''}`;
    }
    if (sel.role === 'cache') return html`<div class="zone"><span class="label">Removes device</span><button type="button" class="btn danger" data-act="device-remove" data-device="${sel.dev}" data-what="the read cache ${sel.dev}">Remove read cache…</button></div>`;
    if (sel.role === 'log' && !sel.parent) return html`<div class="zone"><span class="label">Removes device</span><button type="button" class="btn danger" data-act="device-remove" data-device="${sel.dev}" data-what="the write log ${sel.dev}">Remove write log…</button></div>`;
    if (sel.role === 'log') return html`<p class="hint">This disk belongs to the log mirror ${sel.parent}; remove the whole mirror from its card.</p>`;
    return html`<p class="hint">No actions for ${sel.role} devices here; use zpool.</p>`;
  }

  // --- Health ---------------------------------------------------------------------
  function drawHealth() {
    const d = S.data.health, e = d.errors, scan = e.scan;
    const scanning = scan?.state === 'SCANNING';
    const progress = scanning && scan.toExamine > 0 ? (scan.examined || 0) / scan.toExamine : null;
    return html`
      <div class="vhead"><h1>Health &amp; scrub</h1><span class="sub">${e.pool} · ${chip(e.state || 'ONLINE', tone(e.state))}</span>
        <div class="actions" data-region="actions">${scanning
          ? html`<button type="button" class="btn" data-act="scrub" data-action="${scan.paused ? 'start' : 'pause'}">${scan.paused ? 'Resume scrub' : 'Pause scrub'}</button><button type="button" class="btn" data-act="scrub" data-action="stop">Stop scrub</button>`
          : html`<button type="button" class="btn primary" data-act="scrub" data-action="start">Start scrub</button>`}</div></div>
      <section class="panel stack" data-region="scrub"><h2>Scrub</h2>
        <div class="row"><b>${scanning ? `${(scan.function || 'scan').toLowerCase()} ${scan.paused ? 'paused' : 'running'}` : scan ? `Last ${(scan.function || 'scan').toLowerCase()}: ${String(scan.state || '').toLowerCase()}, ${scan.errors ?? 0} errors` : 'No scrub has run.'}</b>
          ${progress !== null ? html`<progress max="100" value="${Math.round(progress * 100)}"></progress><span class="mut">${bytes(scan.examined)} of ${bytes(scan.toExamine)} (${pct(progress, 1)})</span>` : ''}</div>
        <details class="help"><summary>How ZFS finds damaged data</summary><p>ZFS checks every block it reads against its checksum (${d.checksum} on ${e.pool}), not a CRC. With a second copy (mirror, raidz or copies=2) a bad block is repaired from it; otherwise the file is listed below. The list covers the blocks read since the last clear; a scrub reads every block.</p></details>
        ${e.logError ? html`<p class="hint bad-text">The error log could not be read: ${e.logError}</p>` : ''}</section>
      <section class="panel flush" data-region="files"><div class="row panel-head"><h2>Damaged files (permanent errors)</h2>${d.files.length > 0 ? html`<button type="button" class="btn small push" data-act="files-copy">Copy list</button>` : ''}</div>
        ${d.files.length === 0
          ? html`<div class="empty"><strong>${e.errorCount > 0 ? `${num(e.errorCount)} errors are recorded.` : 'No known data errors.'}</strong><span>${e.errorCount > 0 ? 'Their files are listed after the next scrub.' : 'Every block read so far matched its checksum.'}</span></div>`
          : html`<div class="table-wrap"><table class="t"><thead><tr><th>File or object</th><th>Dataset</th><th>Snapshot</th><th>Kind</th><th></th></tr></thead><tbody>
            ${d.files.map((f) => html`<tr class="bad"><td class="mono wrap">${f.display}</td><td>${f.dataset || ''}</td><td>${f.snapshot || ''}</td><td>${f.kind === 'metadata' ? 'pool metadata' : f.kind}</td><td>${f.canReveal ? html`<button type="button" class="btn ghost small" data-act="file-reveal" data-index="${f.index}">Show in Explorer</button>` : ''}</td></tr>`)}
          </tbody></table></div>`}</section>
      <section class="panel flush"><h2>Errors per device</h2><div class="table-wrap" data-region="devices"><table class="t"><thead><tr><th>Device</th><th>Group</th><th>State</th><th class="num">READ</th><th class="num">WRITE</th><th class="num">CKSUM</th></tr></thead><tbody>
        ${e.devices.map((x) => html`<tr class="${x.readErrors + x.writeErrors + x.checksumErrors > 0 || tone(x.state) !== 'ok' ? 'bad' : ''}"><td class="mono">${x.path || x.name}</td><td>${x.group}</td><td>${x.state || ''}</td><td class="num">${x.readErrors}</td><td class="num">${x.writeErrors}</td><td class="num">${x.checksumErrors}</td></tr>`)}
      </tbody></table></div></section>
      <div class="zone"><span class="label">Resets counters</span><div><button type="button" class="btn danger" data-act="pool-clear">Clear error counters…</button></div>
        <p class="hint">Clear them after you have replaced or fixed what caused them. The list of damaged files stays until a scrub no longer finds them.</p></div>`;
  }

  // --- Performance ------------------------------------------------------------------
  // One sampler for the Performance view and the Overview's activity panel:
  // it runs while either is on screen and keeps the last five minutes.
  const WINDOW_SECONDS = 300;
  let rateTimer = null;
  function scheduleRates(now) {
    clearTimeout(rateTimer);
    rateTimer = null;
    if (!['performance', 'overview'].includes(S.view) || S.rates.paused || S.minimized || !S.app?.brokerAvailable || !S.pool) return;
    rateTimer = setTimeout(sampleRates, now ? 0 : S.rates.every * 1000);
  }
  async function sampleRates() {
    const r = S.rates;
    if (r.pool !== S.pool) { r.pool = S.pool; r.series = []; r.last = null; }
    try {
      const d = await call('rates.sample', { pool: S.pool });
      if (!d.pending && r.pool === S.pool) {
        const t = Date.now();
        r.series.push({ t, rb: d.total.readBytesPerSecond, wb: d.total.writeBytesPerSecond, ri: d.total.readIops, wi: d.total.writeIops });
        // One sample older than the window keeps the line reaching its left edge.
        while (r.series.length > 2 && r.series[1].t < t - WINDOW_SECONDS * 1000) r.series.shift();
      }
      r.last = d;
      S.errors.performance = null;
    } catch (e) {
      S.errors.performance = e;
      setStatus(`Performance: ${e.message}`);
    }
    if (S.view === 'performance' || S.view === 'overview') drawView();
    scheduleRates();
  }
  function niceMax(m) {
    if (m <= 0) return 1;
    const p = 10 ** Math.floor(Math.log10(m)), f = m / p;
    return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10) * p;
  }
  const MEASURES = {
    mb: { title: 'Throughput', unit: 'MB/s', read: (s) => s.rb / 1048576, write: (s) => s.wb / 1048576, text: (v) => v.toFixed(1) },
    iops: { title: 'Operations', unit: 'IOPS', read: (s) => s.ri, write: (s) => s.wi, text: (v) => num(Math.round(v)) },
  };
  // Read and write over the last five minutes, on a time axis.
  function chart(series, kind, small) {
    const m = MEASURES[kind];
    // A narrower drawing for the small charts keeps their labels readable.
    const W = small ? 420 : 520, H = small ? 160 : 230, L = 48, R = 14, T = 10, B = 26;
    const end = series.length ? series[series.length - 1].t : Date.now(), start = end - WINDOW_SECONDS * 1000;
    const read = series.map(m.read), write = series.map(m.write);
    // Four gridline steps, each a round number.
    const max = niceMax(Math.max(0, ...read, ...write) * 1.05 / 4) * 4;
    const x = (t) => L + (W - L - R) * Math.max(0, (t - start) / (WINDOW_SECONDS * 1000));
    const y = (v) => T + (H - T - B) * (1 - v / max);
    const label = (v) => (v >= 10000 ? `${(v / 1000).toFixed(0)}k` : v >= 10 ? v.toFixed(0) : v.toFixed(1));
    const grid = [0, 1, 2, 3, 4].map((i) => { const v = max * i / 4, yy = y(v); return html`<line x1="${L}" x2="${W - R}" y1="${yy}" y2="${yy}"></line><text x="${L - 8}" y="${yy + 4}" text-anchor="end">${label(v)}</text>`; });
    const ticks = [5, 4, 3, 2, 1, 0].map((min) => html`<text x="${x(end - min * 60000)}" y="${H - 6}" text-anchor="${min === 5 ? 'start' : min === 0 ? 'end' : 'middle'}">${min === 0 ? 'now' : `−${min} min`}</text>`);
    const line = (values) => values.map((v, i) => `${x(series[i].t).toFixed(1)},${y(v).toFixed(1)}`).join(' ');
    const area = (values) => `${x(series[0].t).toFixed(1)},${H - B} ${line(values)} ${x(end).toFixed(1)},${H - B}`;
    const dot = (values, cls) => (values.length ? html`<circle class="${cls}" cx="${x(end)}" cy="${y(values[values.length - 1])}" r="4"></circle>` : '');
    return html`<svg viewBox="0 0 ${W} ${H}" role="img" aria-label="Read and write ${m.unit} over the last five minutes">
      <g class="grid">${grid}${ticks}</g>
      ${series.length > 1 ? html`<polygon class="read-area" points="${area(read)}"></polygon><polygon class="write-area" points="${area(write)}"></polygon><polyline class="read" points="${line(read)}"></polyline><polyline class="write" points="${line(write)}"></polyline>` : ''}
      ${dot(read, 'read-dot')}${dot(write, 'write-dot')}
      ${series.length < 2 ? html`<text x="${(L + W - R) / 2}" y="${(T + H - B) / 2}" text-anchor="middle">Collecting samples…</text>` : ''}</svg>`;
  }
  function chartPanel(kind, small) {
    const r = S.rates, m = MEASURES[kind], last = r.series[r.series.length - 1];
    return html`<section class="panel chart"><h2>${m.title}, last 5 minutes</h2>
      <div data-region="chart-${kind}"><div class="legend chart-legend"><span><b class="read-key"></b>Read <strong>${last ? `${m.text(m.read(last))} ${m.unit}` : '–'}</strong></span><span><b class="write-key"></b>Write <strong>${last ? `${m.text(m.write(last))} ${m.unit}` : '–'}</strong></span></div>
      ${chart(r.series, kind, small)}</div></section>`;
  }
  function drawPerformance() {
    const r = S.rates, d = r.last;
    return html`
      <div class="vhead"><h1>Performance</h1><span class="sub">${S.pool}</span>
        <div class="actions">
          <label for="rate-every" class="mut">Sample every</label><select id="rate-every" data-change="rate-every">${[1, 2, 5, 10, 30, 60].map((s) => html`<option value="${s}"${attr(s === r.every, 'selected')}>${s} s</option>`)}</select>
          <button type="button" class="btn" data-act="rate-pause">${r.paused ? 'Resume' : 'Pause'}</button></div></div>
      <div class="charts">${chartPanel('mb')}${chartPanel('iops')}</div>
      <div class="split">
        <section class="panel flush"><h2>Devices</h2><div class="table-wrap" data-region="devices">${d && !d.pending ? html`<table class="t"><thead><tr><th>Device</th><th class="num">Read MB/s</th><th class="num">Write MB/s</th><th class="num">Read IOPS</th><th class="num">Write IOPS</th></tr></thead><tbody>
          <tr><td class="mono"><b>${S.pool} (pool)</b></td><td class="num">${(d.total.readBytesPerSecond / 1048576).toFixed(1)}</td><td class="num">${(d.total.writeBytesPerSecond / 1048576).toFixed(1)}</td><td class="num">${num(Math.round(d.total.readIops))}</td><td class="num">${num(Math.round(d.total.writeIops))}</td></tr>
          ${d.devices.map((x) => html`<tr><td class="mono">${x.device}</td><td class="num">${(x.rates.readBytesPerSecond / 1048576).toFixed(1)}</td><td class="num">${(x.rates.writeBytesPerSecond / 1048576).toFixed(1)}</td><td class="num">${num(Math.round(x.rates.readIops))}</td><td class="num">${num(Math.round(x.rates.writeIops))}</td></tr>`)}
        </tbody></table>` : html`<p class="hint pad">Two samples make a rate; the first arrives in ${r.every} s.</p>`}</div></section>
        <section class="panel" data-region="memory">${memoryPanel(d?.memory)}</section>
      </div>`;
  }

  // --- User-mode service ------------------------------------------------------------
  function drawService() {
    const d = S.data.service;
    if (d.state === 'NotInstalled') return html`<div class="vhead"><h1>User-mode service</h1></div>${emptyState('The user-mode edition is not installed.', 'The ZfsWinUserMode service and its mounts appear here when it is. The kernel-driver edition does not need it.')}`;
    const m = d.mounts.find((x) => x.name === S.sel.mount);
    const running = d.state === 'Running';
    return html`
      <div class="vhead"><h1>User-mode service</h1><span data-region="state">${chip(`ZfsWinUserMode: ${d.state}`, running ? 'ok' : d.state === 'Stopped' ? 'bad' : 'warn')}</span>
        <div class="actions" data-region="actions">${running
          ? html`<button type="button" class="btn" data-act="service" data-action="restart">Restart</button><button type="button" class="btn danger" data-act="service" data-action="stop">Stop (unmount and export)</button>`
          : html`<button type="button" class="btn primary" data-act="service" data-action="start">Start</button>`}</div></div>
      <section class="panel flush">
        <div class="row panel-head"><h2>Mounts</h2><span class="push"></span>
          <button type="button" class="btn primary" data-act="mount-add">Add mount…</button>
          <span data-region="mount-actions"><button type="button" class="btn" data-act="mount-edit"${attr(!m, 'disabled')}>Edit…</button> <button type="button" class="btn danger" data-act="mount-remove"${attr(!m, 'disabled')}>Remove…</button></span></div>
        <div class="table-wrap" data-region="list">${d.mounts.length === 0 ? html`<div class="empty"><strong>No mounts.</strong><span>Add one to mount a pool's dataset when the service starts.</span></div>` : html`<table class="t"><thead><tr><th>Name</th><th>Source</th><th>Pool</th><th>Dataset</th><th>Mount point</th><th>Read-only</th></tr></thead><tbody>
          ${d.mounts.map((x) => html`<tr tabindex="0" data-act="select-mount" data-key="${x.name}" aria-selected="${x.name === S.sel.mount}"><td>${x.name}</td><td class="mono">${x.source || html`<span class="mut">(imported pool)</span>`}</td><td class="mono">${x.pool}</td><td class="mono">${x.dataset}</td><td class="mono">${x.mountPoint}</td><td>${x.readOnly ? 'yes' : 'no'}</td></tr>`)}
        </tbody></table>`}</div></section>
      <p class="hint">Mount changes take effect when the service restarts. Mounts made with zfs mount (or zpool import) are listed too, as auto-&lt;dataset&gt;; pools are administered in the other sections, as in the kernel-driver edition.</p>`;
  }
  function mountDialog(existing) {
    const m = existing || { name: '', source: '', pool: S.pool || '', dataset: '', mountPoint: 'Z:', readOnly: false };
    return openDialog(html`<form>
      <div class="dh"><span class="eyebrow">User-mode mount</span><h2>${existing ? `Edit ${existing.name}` : 'Add mount'}</h2></div>
      <div class="db">
        <label class="field">Name<input type="text" name="name" value="${m.name}"${attr(!!existing, 'readonly')} autofocus></label>
        <label class="field">Source (folder with the pool's image files; empty for a pool imported with zpool)<span class="row"><input type="text" name="source" value="${m.source}"><button type="button" class="btn" data-browse="source">Browse…</button></span></label>
        <label class="field">Pool<input type="text" name="pool" value="${m.pool}"></label>
        <label class="field">Dataset (empty: the pool's root)<input type="text" name="dataset" value="${m.dataset === m.pool ? '' : m.dataset}"></label>
        <label class="field">Mount point (a free drive letter such as Z:, or a folder path that does not exist yet)<input type="text" name="mountPoint" value="${m.mountPoint}"></label>
        <label class="check"><input type="checkbox" name="readOnly"${attr(m.readOnly, 'checked')}>Read-only (nothing on the pool changes)</label>
        <p class="error" hidden></p></div>
      <div class="df"><button type="button" class="btn" data-cancel>Cancel</button><button type="submit" class="btn primary">Save</button></div></form>`, {
      init: (d) => d.querySelector('[data-browse]').addEventListener('click', async () => {
        const folder = await call('pick.folder', { description: 'Folder that contains the pool\'s image files' }).catch(() => null);
        if (folder) d.querySelector('[name=source]').value = folder;
      }),
      onSubmit: (f) => call('mount.save', {
        name: String(f.get('name')), source: String(f.get('source')), pool: String(f.get('pool')), dataset: String(f.get('dataset')),
        mountPoint: String(f.get('mountPoint')), readOnly: f.get('readOnly') === 'on', replace: !!existing,
      }),
    });
  }

  // --- actions --------------------------------------------------------------------------
  const ACT = {
    goto: (ds) => show(ds.view),
    refresh: async () => {
      await refresh();
      if (S.view === 'performance' || S.view === 'overview') scheduleRates(true);
      setStatus(`Refreshed at ${new Date().toLocaleTimeString()}.`);
    },
    'select-pool': (ds) => { CHANGE.pool({ value: ds.key }); },
    'report-save': async () => {
      try { const file = await call('report.save'); if (file) toast(`Saved ${file}.`); } catch (e) { errorDialog('Save report failed', e); }
    },
    scrub: (ds) => run(`${ds.action === 'start' ? 'Start' : ds.action === 'pause' ? 'Pause' : 'Stop'} scrub on ${S.pool}`, () => call('pool.scrub', { pool: S.pool, action: ds.action })),
    'count-start': async () => { await call('count.start'); loadView('overview'); },
    'count-cancel': async () => { await call('count.cancel'); loadView('overview'); },

    'select-dataset': (ds) => { S.sel.dataset = ds.key; loadView('datasets'); drawView(); },
    'props-undo': () => { delete S.pendingProps[S.sel.dataset]; drawView(); },
    'props-apply': () => {
      const dataset = S.sel.dataset, changes = S.pendingProps[dataset] || {};
      return run(`Apply ${Object.entries(changes).map(([k, v]) => `${k}=${v}`).join(', ')} to ${dataset}`, async () => {
        for (const [property, value] of Object.entries(changes)) {
          await call('dataset.set', { dataset, property, value });
          delete changes[property];
        }
        delete S.pendingProps[dataset];
      });
    },

    'select-snapshot': (ds) => { S.sel.snapshot = ds.key; drawView(); },
    'snapshot-create': () => {
      const dataset = S.snapDataset;
      openDialog(html`<form><div class="dh"><span class="eyebrow">New snapshot</span><h2>Snapshot ${dataset}</h2></div>
        <div class="db"><label class="field">Name<span class="row"><span class="mono">${dataset}@</span><input type="text" name="name" value="gui-${stamp()}" autofocus spellcheck="false"></span></label>
          <label class="check"><input type="checkbox" name="recursive">Include child datasets</label><p class="error" hidden></p></div>
        <div class="df"><button type="button" class="btn" data-cancel>Cancel</button><button type="submit" class="btn primary">Create</button></div></form>`, {
        onSubmit: (f) => call('snapshot.create', { dataset, name: String(f.get('name')).trim(), recursive: f.get('recursive') === 'on' }),
      }).then((done) => { if (done) { toast(`Created a snapshot of ${dataset}.`); refresh(); } });
    },
    'snapshot-mount': () => {
      const snapshot = S.sel.snapshot, letters = S.data.snapshots.freeLetters || [];
      openDialog(html`<form><div class="dh"><span class="eyebrow">Read-only</span><h2>Mount @${shortSnap(snapshot)}</h2></div>
        <div class="db"><p class="hint">A temporary, read-only clone that uses no space. Explorer opens on it.</p>
          <label class="check"><input type="radio" name="where" value="letter" checked>Drive letter <select name="letter">${letters.map((l) => html`<option>${l}</option>`)}</select></label>
          <label class="check"><input type="radio" name="where" value="folder">Empty folder</label>
          <span class="row"><input type="text" name="folder" placeholder="D:\\restore" aria-label="Empty folder"><button type="button" class="btn" data-browse="folder">Browse…</button></span>
          <p class="error" hidden></p></div>
        <div class="df"><button type="button" class="btn" data-cancel>Cancel</button><button type="submit" class="btn primary">Mount and open</button></div></form>`, {
        init: (d) => d.querySelector('[data-browse]').addEventListener('click', async () => {
          const folder = await call('pick.folder', { description: 'An empty folder for the snapshot' }).catch(() => null);
          if (folder) { d.querySelector('[name=folder]').value = folder; d.querySelector('[value=folder]').checked = true; }
        }),
        onSubmit: (f) => call('snapshot.mount', { snapshot, mountPoint: f.get('where') === 'letter' ? String(f.get('letter') || '') : String(f.get('folder')).trim() }),
      }).then((done) => { if (done) { toast(`Mounted ${snapshot} read-only.`); refresh(); } });
    },
    'snapshot-open': () => call('snapshot.open', { snapshot: S.sel.snapshot }).catch((e) => errorDialog('Open in Explorer failed', e)),
    'snapshot-unmount': () => run(`Unmount ${S.sel.snapshot}`, () => call('snapshot.unmount', { snapshot: S.sel.snapshot })),
    'snapshot-rollback': () => {
      const d = S.data.snapshots, s = d.snapshots.find((x) => x.name === S.sel.snapshot);
      const newer = d.snapshots.filter((x) => new Date(x.creation) > new Date(s.creation));
      const mountedNewer = newer.filter((x) => d.mounts[x.name]);
      if (mountedNewer.length) { toast(`Unmount the newer snapshots first: ${mountedNewer.map((x) => '@' + x.snapshot).join(', ')}.`, 'bad'); return; }
      confirmChange({
        eyebrow: 'Changes data', title: `Roll ${s.dataset} back to @${s.snapshot}?`, ok: 'Roll back', label: `Roll back ${s.dataset} to @${s.snapshot}`,
        items: [`${s.dataset} returns to its state of ${when(s.creation)}.`, 'Every change made since then is lost. The dataset is unmounted for a moment and mounted again at the same place.',
          ...(newer.length ? [`These ${newer.length} newer snapshot${newer.length === 1 ? ' is' : 's are'} destroyed as well: ${newer.map((x) => '@' + x.snapshot).join(', ')}.`] : [])],
        token: s.snapshot, action: (confirm) => call('snapshot.rollback', { snapshot: s.name, confirm }),
      });
    },
    'snapshot-destroy': () => {
      const d = S.data.snapshots, s = d.snapshots.find((x) => x.name === S.sel.snapshot);
      if (d.mounts[s.name]) { toast(`@${s.snapshot} is mounted; unmount it first.`, 'bad'); return; }
      confirmChange({
        eyebrow: 'Permanent', title: `Destroy @${s.snapshot}?`, ok: 'Destroy', label: `Destroy ${s.name}`,
        items: [`The ${bytes(s.used)} held only by this snapshot are freed.`, 'Files in it can no longer be restored. This cannot be undone.'],
        token: s.snapshot, action: (confirm) => call('snapshot.destroy', { snapshot: s.name, confirm }),
      });
    },

    'select-disk': (ds) => { S.sel.disk = { dev: ds.key, role: ds.role, parent: ds.parent || null, parentType: ds.parentType || null }; drawView(); },
    'device-attach': async () => {
      const dev = S.sel.disk.dev, pool = S.pool;
      let pick;
      try { pick = await call('devices.pick', { pool, purpose: 'mirror', device: dev }); } catch (e) { errorDialog('Device list failed', e); return; }
      if (!pick) return;
      confirmChange({
        eyebrow: 'Overwrites the new device', title: `Mirror ${dev}?`, ok: 'Attach', label: `Attach ${pick.devices[0]} to ${dev}`,
        items: [`New member: ${pick.summary}`, 'Everything on the new device is overwritten.', 'ZFS then copies the data to it (resilver); the pool stays usable.'],
        token: 'attach', action: (confirm) => call('device.attach', { pool, device: dev, pick: pick.token, confirm }),
      });
    },
    'device-detach': () => {
      const sel = S.sel.disk, pool = S.pool;
      confirmChange({
        eyebrow: 'Reduces redundancy', title: `Detach ${sel.dev}?`, ok: 'Detach', label: `Detach ${sel.dev}`,
        items: [`${sel.parent} loses one copy.`, 'ZFS refuses if this disk holds the only valid copy of some data.'],
        token: 'detach', action: (confirm) => call('device.detach', { pool, device: sel.dev, confirm }),
      });
    },
    'device-add': async (ds) => {
      const pool = S.pool, role = ds.role, what = role === 'cache' ? 'read cache (L2ARC)' : 'write log (SLOG)';
      let pick;
      try { pick = await call('devices.pick', { pool, purpose: role }); } catch (e) { errorDialog('Device list failed', e); return; }
      if (!pick) return;
      confirmChange({
        eyebrow: 'Overwrites the devices', title: `Add a ${what} to ${pool}?`, ok: 'Add', label: `Add ${what} to ${pool}`,
        items: [...pick.summary.split(/\r?\n\s*/).filter(Boolean), ...(pick.mirror ? ['Mirrored across the selected devices.'] : []), 'Everything on these devices is overwritten.'],
        token: 'add', action: (confirm) => call('device.add', { pool, role, pick: pick.token, confirm }),
      });
    },
    'device-remove': (ds) => {
      const pool = S.pool;
      confirmChange({
        eyebrow: 'Removes device', title: `Remove ${ds.what}?`, ok: 'Remove', label: `Remove ${ds.device} from ${pool}`,
        items: ['Pool data is not affected; a write log is flushed to the pool first.', 'A read cache starts cold if you add it again.'],
        token: 'remove', action: (confirm) => call('device.remove', { pool, device: ds.device, confirm }),
      });
    },

    'file-reveal': (ds) => call('file.reveal', { pool: S.pool, index: Number(ds.index) }).catch((e) => errorDialog('Show in Explorer', e)),
    'files-copy': () => call('files.copy', { pool: S.pool }).then(() => toast('Copied the list of damaged files.')).catch((e) => errorDialog('Copy list failed', e)),
    'pool-clear': () => confirmChange({
      eyebrow: 'Resets counters', title: `Clear the error counters of ${S.pool}?`, ok: 'Clear counters', label: `Clear errors on ${S.pool}`,
      items: ['READ, WRITE and CKSUM go back to 0 on every device (zpool clear).', 'The damaged-file list stays until a scrub no longer finds them.'],
      token: S.pool, action: (confirm) => call('pool.clear', { pool: S.pool, confirm }),
    }),

    'rate-pause': () => { S.rates.paused = !S.rates.paused; drawView(); scheduleRates(true); },

    service: (ds) => {
      if (ds.action === 'start') { run('Start ZfsWinUserMode', () => call('service.control', { action: 'start' })); return; }
      confirmChange({
        eyebrow: 'Unmounts every user-mode pool', title: ds.action === 'stop' ? 'Stop ZfsWinUserMode?' : 'Restart ZfsWinUserMode?', ok: ds.action === 'stop' ? 'Stop' : 'Restart',
        label: `${ds.action === 'stop' ? 'Stop' : 'Restart'} ZfsWinUserMode`,
        items: ['Every user-mode pool is unmounted and exported first; open files on them are closed.', ...(ds.action === 'restart' ? ['The mounts come back when the service has started.'] : [])],
        token: null, action: () => call('service.control', { action: ds.action, confirm: ds.action }),
      });
    },
    'select-mount': (ds) => { S.sel.mount = ds.key; drawView(); },
    'mount-add': () => afterMount(mountDialog(null)),
    'mount-edit': () => afterMount(mountDialog(S.data.service.mounts.find((x) => x.name === S.sel.mount))),
    'mount-remove': () => {
      const name = S.sel.mount;
      confirmChange({
        eyebrow: 'Removes configuration', title: `Remove the mount ${name}?`, ok: 'Remove', label: `Remove mount ${name}`,
        items: ['The pool is not touched.', 'Restart the service to unmount it.'],
        token: name, action: (confirm) => call('mount.remove', { name, confirm }),
      });
    },
  };
  async function afterMount(dialog) {
    const result = await dialog;
    if (!result) return;
    toast('Saved the mount.');
    await refresh();
    if (result.restartSuggested) ACT.service({ action: 'restart' });
  }
  const CHANGE = {
    pool: (el) => { S.pool = el.value; S.sel.disk = null; S.sel.snapshot = null; S.data = { performance: S.rates }; drawFrame(); show(S.view); },
    'snap-dataset': (el) => { S.snapDataset = el.value; S.sel.snapshot = null; S.data.snapshots = null; loadView('snapshots'); },
    'keep-mounted': (el) => call('snapshots.keepMounted', { value: el.checked }).catch((e) => errorDialog('Keep mounts', e)),
    prop: (el) => {
      const dataset = S.sel.dataset, name = el.dataset.prop;
      if (el.value === OTHER) { otherValue(dataset, name); return; }
      const current = S.data.datasets.properties.find((p) => p.name === name)?.value;
      const changes = (S.pendingProps[dataset] ||= {});
      if (el.value === current) delete changes[name]; else changes[name] = el.value;
      drawView();
    },
    'rate-every': (el) => { S.rates.every = Number(el.value); drawView(); scheduleRates(); },
  };
  function otherValue(dataset, name) {
    const label = FEATURES.find((f) => f[0] === name)[1];
    const current = S.pendingProps[dataset]?.[name] ?? S.data.datasets.properties.find((p) => p.name === name)?.value ?? '';
    openDialog(html`<form><div class="dh"><span class="eyebrow">${dataset}</span><h2>Another ${label.toLowerCase()} value</h2></div>
      <div class="db"><label class="field">Value, as zfs set takes it (for example ${name === 'compression' ? 'zstd-5 or gzip-4' : name === 'dedup' ? 'skein,verify' : 'standard'})<input type="text" name="value" value="${current}" autofocus spellcheck="false" autocomplete="off"></label>
        <p class="hint">It is applied with the other changes; zfs refuses a value it does not know.</p><p class="error" hidden></p></div>
      <div class="df"><button type="button" class="btn" data-cancel>Cancel</button><button type="submit" class="btn primary">Use this value</button></div></form>`, {
      onSubmit: (f) => {
        const value = String(f.get('value')).trim().toLowerCase();
        if (!FEATURE_VALUE.test(value)) throw new HostError({ message: 'Use lowercase letters, digits, commas and hyphens, as zfs does.' });
        const changes = (S.pendingProps[dataset] ||= {});
        if (value === S.data.datasets.properties.find((p) => p.name === name)?.value) delete changes[name]; else changes[name] = value;
        return true;
      },
    }).then(() => drawView());
  }
  const INPUT = {
    'prop-filter': (el) => { S.propFilter = el.value; drawView(); },
  };

  document.addEventListener('click', (e) => {
    const nav = e.target.closest('nav.side button[data-view]');
    if (nav) { if (nav.getAttribute('aria-disabled') !== 'true') show(nav.dataset.view); return; }
    const a = e.target.closest('[data-act]');
    if (a && !a.disabled && (main.contains(a) || a.closest('.titlebar'))) { const fn = ACT[a.dataset.act]; if (fn) { e.preventDefault(); fn(a.dataset, a); } }
  });
  document.addEventListener('keydown', (e) => {
    if ((e.key === 'Enter' || e.key === ' ') && e.target.matches && e.target.matches('tr[data-act]')) { e.preventDefault(); e.target.click(); }
  });
  document.addEventListener('change', (e) => { const c = e.target.closest('[data-change]'); if (c && CHANGE[c.dataset.change]) CHANGE[c.dataset.change](c); });
  document.addEventListener('input', (e) => { const c = e.target.closest('[data-input]'); if (c && INPUT[c.dataset.input]) INPUT[c.dataset.input](c); });

  // --- views ------------------------------------------------------------------------------
  const VIEWS = {
    overview: { title: 'Overview', broker: true, pool: true, load: async () => { S.data.overview = await call('overview.get'); }, draw: drawOverview },
    datasets: { title: 'Datasets', broker: true, pool: true, load: loadDatasets, draw: drawDatasets },
    snapshots: { title: 'Snapshots', broker: true, pool: true, load: loadSnapshots, draw: drawSnapshots },
    devices: {
      title: 'Devices', broker: true, pool: true, draw: drawDevices,
      load: async () => {
        const d = await call('devices.get', { pool: S.pool });
        if (S.sel.disk && !d.leaves.some((l) => l.device === S.sel.disk.dev)) S.sel.disk = null;
        if (!S.sel.disk) {
          const first = d.leaves.find((l) => l.problem) || null;
          if (first) {
            const parent = [...d.status.vdevs.flatMap((r) => (r.type === 'root' ? r.children : [r])), ...d.status.logs].find((v) => !v.isLeaf && leavesOf(v).some((x) => (x.path ?? x.name) === first.device));
            S.sel.disk = { dev: first.device, role: first.role, parent: parent?.name ?? null, parentType: parent?.type ?? null };
          }
        }
        S.data.devices = d;
      },
    },
    health: { title: 'Health & scrub', broker: true, pool: true, load: async () => { S.data.health = await call('health.get', { pool: S.pool }); }, draw: drawHealth },
    performance: { title: 'Performance', broker: true, pool: true, load: async () => {}, draw: drawPerformance },
    service: { title: 'User-mode service', broker: false, pool: false, load: async () => { S.data.service = await call('service.get'); }, draw: drawService },
  };
  // Performance draws from its own samples, even before the first one.
  S.data.performance = S.rates;

  // --- start --------------------------------------------------------------------------------
  (async () => {
    drawFrame();
    await loadApp();
    if (S.app && !S.app.brokerAvailable && S.app.service !== 'NotInstalled') S.view = 'service';
    show(S.view);
    await loadView(S.view);
    if (webview) webview.postMessage(JSON.stringify({ type: 'ready' }));
    setInterval(() => { if (!S.minimized && !document.hidden) refresh(); }, 5000);
  })();
})();
