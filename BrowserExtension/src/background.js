// Lemora: the extension's background service. Talks to Lemora (native messaging "com.lemora.passwords") and answers the popup and pages.
// Passwords stay in Lemora: they reach this script only when the user picks an entry to fill, and only for the address of the page asking for it.
'use strict';
const api = globalThis.browser ?? globalThis.chrome;
const HOST = 'com.lemora.passwords';

// ---- channel to Lemora: one request at a time, in order (the bridge replies in the same order)
let port = null;
let queue = [];

function connect() {
  if (port) return port;
  port = api.runtime.connectNative(HOST);
  port.onMessage.addListener((msg) => {
    const p = queue.shift();
    if (p) p.resolve(msg);
  });
  port.onDisconnect.addListener(() => {
    const err = api.runtime.lastError?.message || api.i18n.getMessage('bg_unreachable');
    port = null;
    for (const p of queue.splice(0)) p.resolve({ ok: false, error: 'nohost', detail: err });
  });
  return port;
}

// ---- pairing key: in the extension's IndexedDB, which content scripts (in pages) cannot read.
// storage.local, instead, is readable by them too: if a page's process were compromised, the key would stay out of reach
function idb() {
  return new Promise((res, rej) => {
    const r = indexedDB.open('lemora', 1);
    r.onupgradeneeded = () => r.result.createObjectStore('kv');
    r.onsuccess = () => res(r.result);
    r.onerror = () => rej(r.error);
  });
}
// Firefox with history not kept or site data deleted on close refuses IndexedDB ("A mutation operation was attempted on a database that did not
// allow mutations"): the extension stopped before reaching Lemora and the pairing was never saved. Then the extension storage is used:
// readable by our content scripts too (not by the pages), the weaker but working option
const FALLBACK = 'lemoraKv';
async function fallbackGet(k) { const o = await api.storage.local.get(FALLBACK).catch(() => ({})); return (o[FALLBACK] || {})[k]; }
async function fallbackSet(k, v) { const o = await api.storage.local.get(FALLBACK).catch(() => ({})); const kv = o[FALLBACK] || {}; kv[k] = v; await api.storage.local.set({ [FALLBACK]: kv }).catch(() => {}); }
async function idbGet(k) {
  try {
    const db = await idb();
    const v = await new Promise((res) => { const t = db.transaction('kv').objectStore('kv').get(k); t.onsuccess = () => res(t.result); t.onerror = () => res(undefined); });
    return v !== undefined ? v : fallbackGet(k);
  } catch { return fallbackGet(k); }
}
async function idbSet(k, v) {
  try {
    const db = await idb();
    await new Promise((res, rej) => { const t = db.transaction('kv', 'readwrite'); t.objectStore('kv').put(v, k); t.oncomplete = () => res(); t.onerror = () => rej(t.error); });
  } catch { await fallbackSet(k, v); }
}

let tokenCache = null;
async function token() {
  if (tokenCache !== null) return tokenCache;
  let t = (await idbGet('token')) || '';
  // older versions: the key was in storage.local. Move it and delete it from there
  const old = await api.storage.local.get('lemoraToken').catch(() => ({}));
  if (old && old.lemoraToken) { if (!t) { t = old.lemoraToken; await idbSet('token', t); } await api.storage.local.remove('lemoraToken').catch(() => {}); }
  tokenCache = t;
  return t;
}

async function lemora(req) {
  const full = { ...req, token: await token() };
  return new Promise((resolve) => {
    try {
      const p = connect();
      queue.push({ resolve });
      p.postMessage(full);
    } catch (e) {
      resolve({ ok: false, error: 'nohost', detail: String(e) });
    }
  });
}

// ---- pairing: Lemora asks the user to confirm (showing the real browser, read from the process) and returns a key
async function pair() {
  const r = await lemora({ type: 'pair' });
  if (r.ok && r.token) { await idbSet('token', r.token); tokenCache = r.token; }
  delete r.token;   // the key never leaves the background
  return r;
}

// ---- after Windows Hello: the browser has closed the popup (Hello takes the focus) and does not let the extension reopen it
// (action.openPopup refused without a click). The same list then opens by itself in a small window under the toolbar,
// which closes as soon as you click elsewhere, like the popup (no badge, no extra click)
async function confirmed(r, fromPopup, tabId) {
  if (!r.ok || !fromPopup) return r;
  const popupOpen = api.extension?.getViews ? api.extension.getViews({ type: 'popup' }).length > 0 : false;
  if (!popupOpen) {
    // the real popup, under the icon, once the browser window has the focus back from Windows Hello. No separate window any more
    // (it looked like a detached browser window). If the browser refuses (Firefox without a click), the next click on the icon shows the list
    try {
      let w = null;
      for (let i = 0; i < 20; i++) { w = await api.windows.getLastFocused({ windowTypes: ['normal'] }); if (w.focused) break; await new Promise((res) => setTimeout(res, 150)); }
      // Edge/Chrome refuse the popup on a window without focus ("first call windows.update with focused"): the browser window comes forward first
      if (w?.id && !w.focused) await api.windows.update(w.id, { focused: true });
      await api.action.openPopup(w?.id ? { windowId: w.id } : undefined);
    } catch { /* not allowed here: nothing opens */ }
  }
  return r;
}

// ---- status for the page and the popup
async function status() {
  let r;
  try { r = await lemora({ type: 'hello', version: api.runtime.getManifest().version }); }   // version: Lemora offers the update when it has a newer one
  catch (e) { return { state: 'nohost', detail: String(e) }; }   // e.g. the key storage of the browser not available
  if (!r.ok) return { state: r.error === 'closed' ? 'closed' : r.error === 'notallowed' ? 'notallowed' : 'nohost', detail: r.detail || '' };
  if (r.locked) return { state: 'locked', theme: r.theme };   // checked first: while locked, Lemora does not say whether it is paired
  if (!r.paired) return { state: 'unpaired', theme: r.theme };
  // every 20 minutes Lemora asks Windows Hello or the master password again ("confirmed" missing = Lemora older than this: nothing to confirm)
  if (r.confirmed === false) return { state: 'confirm', hello: !!r.hello, theme: r.theme };
  return { state: 'ready', theme: r.theme };
}

// the page (content script) does not choose the address: the requesting frame's address is used
function senderUrl(sender) {
  return sender.url || '';
}

// a frame inside the page is accepted only if it has the same origin as the main page: a site cannot frame
// a device's admin panel and get it filled with a deceptive click (clickjacking)
function frameAllowed(sender) {
  if (!sender.tab) return false;
  try {
    const f = new URL(senderUrl(sender));
    if (f.protocol !== 'http:' && f.protocol !== 'https:') return false;
    if (sender.frameId === 0) return true;
    return f.origin === new URL(sender.tab.url || '').origin;
  } catch { return false; }
}

// same rule as Lemora (BrowserBridge.EntryMatches): same host; entry with a scheme = same scheme and port (default if missing);
// entry without a scheme = same port if one is given. Only http/https pages
function puny(h) {
  try { return new URL('http://' + (h.includes(':') && !h.startsWith('[') ? `[${h}]` : h)).hostname.replace(/^\[|\]$/g, ''); } catch { return null; }
}
function sameHost(entryIp, url) {
  try {
    const u = new URL(url);
    if (u.protocol !== 'http:' && u.protocol !== 'https:') return false;
    const t = (entryIp || '').trim();
    // entry host and port parsed by hand: with a made-up scheme ("x://") Chromium leaves the host empty
    const e = /^(?:([a-z][a-z0-9+.-]*):\/\/)?(?:[^@/?#]*@)?(\[[^\]]+\]|[^/:?#\s]+)(?::(\d+))?/i.exec(t);
    if (!e || !t) return false;
    const h = (s) => (s || '').replace(/^\[|\]$/g, '').replace(/\.$/, '').toLowerCase();
    const eh = puny(h(e[2]));
    if (!eh || eh !== h(u.hostname)) return false;
    const pagePort = u.port || (u.protocol === 'https:' ? '443' : '80');
    if (!e[1]) return !e[3] || e[3] === pagePort;
    const scheme = e[1].toLowerCase();
    if (scheme !== 'http' && scheme !== 'https') return false;
    if (scheme + ':' !== u.protocol) return false;
    return (e[3] || (scheme === 'https' ? '443' : '80')) === pagePort;
  } catch { return false; }
}

// ---- credentials to save after a form submit (asked on the next page, only on the same site)
const pending = new Map();   // tabId -> { url, origin, user, password, at, matches }
const PENDING_MS = 120000;

function dropPending(tabId) {
  const p = pending.get(tabId);
  if (p) { clearTimeout(p.timer); p.password = ''; pending.delete(tabId); }
}

// saving is offered only for pages reached by IP (devices, panels): with a domain in the address bar, no.
// URL() already normalizes shortened IPv4 forms ("192.168.1" -> "192.168.0.1"); IPv6 arrives in brackets
function ipUrl(url) {
  try {
    const h = new URL(url).hostname;
    if (h.startsWith('[')) return true;
    return /^\d{1,3}(\.\d{1,3}){3}$/.test(h) && h.split('.').every((x) => Number(x) <= 255);
  } catch { return false; }
}

async function capture(sender, user, password) {
  if (!password || password.length > 1024 || user.length > 512) return { ok: false };
  const url = senderUrl(sender);
  if (!ipUrl(url)) return { ok: false };
  // already saved with the same password: Lemora tells us, without the saved password reaching this script
  const k = await lemora({ type: 'known', url, user, password });
  if (k.ok && k.known) { dropPending(sender.tab.id); return { ok: true, known: true }; }
  const m = await lemora({ type: 'match', url });
  dropPending(sender.tab.id);
  // 20 minutes passed: before, the password was dropped here and nothing was asked. It is kept (2 minutes at most, only here)
  // and the page asks to confirm first. Lemora closed, locked or not paired: the password is not kept
  const confirm = !m.ok && m.error === 'confirm';
  if (!m.ok && !confirm) return { ok: false };
  const timer = setTimeout(() => dropPending(sender.tab.id), PENDING_MS);
  pending.set(sender.tab.id, { url, origin: new URL(url).origin, user, password, at: Date.now(), timer, matches: m.ok ? m.items : [], confirm });
  return { ok: true };
}

// the save prompt appears only in the main page and only if it is on the same site (same origin) as the submit
function takePending(sender) {
  const p = pending.get(sender.tab.id);
  if (!p) return null;
  if (Date.now() - p.at > PENDING_MS || sender.frameId !== 0) return null;
  try { if (new URL(senderUrl(sender)).origin !== p.origin) return null; } catch { return null; }
  return p;
}

// ---- last use of entries from this browser (for "Recent" in the popup): only id and time, in IndexedDB like the key
async function touch(id) {
  if (!id) return;
  const r = (await idbGet('recent')) || {};
  r[id] = Date.now();
  const keep = Object.entries(r).sort((a, b) => b[1] - a[1]).slice(0, 200);   // the 200 most recent
  await idbSet('recent', Object.fromEntries(keep));
}
const used = (id) => (r) => { if (r && r.ok) touch(String(id || '')).catch(() => {}); return r; };

// ---- usage log (Lemora sends it to the Teams server, if connected): only the page origin, never path or parameters
function originOf(u) {
  try { const x = new URL(u || ''); return x.protocol === 'http:' || x.protocol === 'https:' ? x.origin : ''; } catch { return ''; }
}
// from the popup: the site of the active tab
async function activeOrigin() {
  // the last normal browser window: with the list opened in its own small window (after Windows Hello) the last focused one is that window
  try { const w = await api.windows.getLastFocused({ windowTypes: ['normal'] }); const [t] = await api.tabs.query({ active: true, windowId: w.id }); return originOf(t?.url); } catch { return ''; }
}

// ---- messages from popup and pages
const POPUP_ONLY = new Set(['list', 'folders', 'save', 'delete', 'copy', 'recent']);
api.runtime.onMessage.addListener((msg, sender, reply) => {
  (async () => {
    if (!msg || typeof msg.cmd !== 'string') return { ok: false, error: 'unknown' };
    // the popup is an extension page; everything else is a web page
    const fromPopup = sender.id === api.runtime.id && (sender.url || '').startsWith(api.runtime.getURL(''));
    const fromPage = !fromPopup;
    if (fromPage && !frameAllowed(sender)) return { ok: false, error: 'notallowed' };
    switch (msg.cmd) {
      case 'status': return status();
      case 'pair': {   // also from the in-page menu: Lemora gives the confirmation
        const r = await pair();
        // right after linking, Windows Hello and then the list by themselves: the popup was closed by the Lemora dialog
        if (r.ok && fromPopup) await confirmed(await lemora({ type: 'confirm' }), true, Number(msg.tab) || 0);
        return r;
      }
      case 'confirm': return confirmed(await lemora({ type: 'confirm' }), fromPopup, Number(msg.tab) || 0);   // Windows Hello, valid 20 minutes
      // master password typed in the popup (never from a page): checked by Lemora, valid 20 minutes, not kept here
      case 'confirmpw': return fromPopup ? lemora({ type: 'confirmpw', password: String(msg.password || '').slice(0, 500) }) : { ok: false, error: 'notallowed' };
      case 'open': return lemora({ type: 'open' });
      case 'match': return lemora({ type: 'match', url: fromPage ? senderUrl(sender) : String(msg.url || '') });
      case 'secret': {
        if (fromPage) {
          // from the page: only entries for that page's address, and only to fill
          const m = await lemora({ type: 'match', url: senderUrl(sender) });
          // any of the entry's addresses (a device can have more than one; an older Lemora sends only "ip")
          if (!m.ok || !m.items.some((e) => e.id === msg.id && (e.ips?.length ? e.ips : [e.ip]).some((ip) => sameHost(ip, senderUrl(sender))))) return { ok: false, error: 'notallowed' };
        }
        return lemora({ type: 'secret', id: String(msg.id || ''), user: String(msg.user || ''), origin: fromPage ? originOf(senderUrl(sender)) : await activeOrigin() }).then(used(msg.id));
      }
      case 'capture': return fromPage ? capture(sender, String(msg.user || ''), String(msg.password || '')) : { ok: false };
      case 'pending': {
        if (!fromPage) return { ok: false };
        const p = takePending(sender);
        if (!p) return { ok: true, pending: null };
        if (p.confirm) {
          // still to confirm? (the user may have just confirmed from the prompt): then the entries of this address are read now
          const m = await lemora({ type: 'match', url: p.url });
          if (m.ok) { p.confirm = false; p.matches = m.items; }
          else if (m.error !== 'confirm') return { ok: true, pending: null };
          else { const h = await lemora({ type: 'hello', version: api.runtime.getManifest().version }); return { ok: true, pending: { confirm: true, hello: !!h.hello, user: p.user, host: new URL(p.url).host } }; }
        }
        return { ok: true, pending: { user: p.user, host: new URL(p.url).host, matches: p.matches.map((e) => ({ id: e.id, name: e.name, folder: e.folder })) } };
      }
      case 'pendingFolders': {
        // folders only for a caller with an open save prompt on this page, and only when the user opens the picker
        if (!fromPage || !takePending(sender)) return { ok: false };
        const f = await lemora({ type: 'folders' });
        return { ok: true, folders: f.ok ? f.folders : [] };
      }
      case 'savePending': {
        if (!fromPage) return { ok: false };
        const p = takePending(sender);
        if (!p) return { ok: false, error: api.i18n.getMessage('bg_expired') };   // shown to the user as "Not saved: expired"
        if (p.confirm && !msg.dismiss) return { ok: false, error: 'confirm' };
        const { password, url } = p;
        // the user name corrected in the prompt (older pages do not send it: the captured one)
        const user = typeof msg.user === 'string' ? msg.user.trim().slice(0, 512) : p.user;
        dropPending(sender.tab.id);
        if (msg.dismiss) return { ok: true };
        const origin = new URL(url).origin;   // with the scheme: the entry will match only http or only https, like the page
        const existing = msg.updateId ? p.matches.find((e) => e.id === msg.updateId) : null;
        const entry = existing
          ? { id: existing.id, folder: existing.folder, name: existing.name, ip: existing.ip, users: [...existing.users.filter((u) => u !== user).map((u) => ({ user: u })), { user, password }] }
          : { folder: String(msg.folder || ''), name: String(msg.name || new URL(url).host).slice(0, 200), ip: origin, users: [{ user, password }] };
        return lemora({ type: 'save', entry, origin, capture: true });
      }
      default:
        // only from the extension popup
        if (!POPUP_ONLY.has(msg.cmd) || fromPage) return { ok: false, error: 'notallowed' };
        if (msg.cmd === 'recent') return { ok: true, recent: (await idbGet('recent')) || {} };
        if (msg.cmd === 'copy') return lemora({ type: 'copy', id: msg.id, user: msg.user, origin: await activeOrigin() }).then(used(msg.id));
        return lemora({ type: msg.cmd, q: msg.q, entry: msg.entry, id: msg.id, user: msg.user, origin: msg.cmd === 'save' || msg.cmd === 'delete' ? await activeOrigin() : undefined });
    }
  })().then(reply, (e) => reply({ ok: false, error: String(e) }));
  return true;   // asynchronous reply
});

// ---- number of entries for the page on the extension icon
async function badge(tabId, url) {
  if (!url || !/^https?:/.test(url)) { api.action.setBadgeText({ tabId, text: '' }); return; }
  const r = await lemora({ type: 'match', url });
  const n = r.ok ? r.items.length : 0;
  api.action.setBadgeBackgroundColor?.({ tabId, color: '#60CDFF' });
  api.action.setBadgeText({ tabId, text: n ? String(n) : '' });
}

api.tabs.onUpdated.addListener((tabId, info, tab) => {
  // the tab moves to another site: the captured password is no longer needed
  if (info.url) { const p = pending.get(tabId); try { if (p && new URL(info.url).origin !== p.origin) dropPending(tabId); } catch { dropPending(tabId); } }
  if (info.status === 'complete') badge(tabId, tab.url).catch(() => {});
});
api.tabs.onActivated.addListener(({ tabId }) => { api.tabs.get(tabId).then((t) => badge(tabId, t.url)).catch(() => {}); });
api.tabs.onRemoved.addListener((tabId) => dropPending(tabId));

// ---- automatic update (Firefox, signed outside the store): the browser checks update_url only once a day; every hour the extension asks for
// a check itself and applies the new version right away. Chrome/Edge from the store update by themselves; loaded from a folder they never do
const UPDATE_ALARM = 'lemora-update';
if (api.alarms && api.runtime.requestUpdateCheck) {
  // created only if missing: the background restarts often (it sleeps when idle) and recreating it would keep moving the next check forward
  Promise.resolve(api.alarms.get(UPDATE_ALARM)).then((a) => { if (!a) api.alarms.create(UPDATE_ALARM, { delayInMinutes: 1, periodInMinutes: 60 }); }).catch(() => {});
  api.alarms.onAlarm.addListener((a) => { if (a.name === UPDATE_ALARM) Promise.resolve(api.runtime.requestUpdateCheck()).catch(() => {}); });
}
// without this the new version would wait for the next browser restart
api.runtime.onUpdateAvailable?.addListener(() => api.runtime.reload());
