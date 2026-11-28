// Lemora: extension popup. List on the left (entries for this page, then the others; folder filter; recent), detail on the right:
// fill, copy (with Lemora's protected clipboard), edit, new, delete. The password never reaches the popup: Lemora does the copying.
'use strict';
const api = globalThis.browser ?? globalThis.chrome;
const send = (msg) => { try { return Promise.resolve(api.runtime.sendMessage(msg)).then((r) => r || { ok: false }, () => ({ ok: false })); } catch { return Promise.resolve({ ok: false }); } };   // promise: in Firefox the callback does not exist
const $ = (id) => document.getElementById(id);
const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; };
// texts from _locales (Italian or English, as the browser language; English for every other language)
const tr = (key, ...subs) => api.i18n.getMessage(key, subs.map(String));
const LOCALE = tr('locale');
document.documentElement.lang = LOCALE;
for (const n of document.querySelectorAll('[data-i18n]')) n.textContent = tr(n.dataset.i18n);
for (const n of document.querySelectorAll('[data-i18n-placeholder]')) n.placeholder = tr(n.dataset.i18nPlaceholder);
for (const n of document.querySelectorAll('[data-i18n-label]')) n.setAttribute('aria-label', tr(n.dataset.i18nLabel));
for (const n of document.querySelectorAll('[data-i18n-title]')) n.title = tr(n.dataset.i18nTitle);

let tab = null;
let folders = [];
let all = [];          // entries (without passwords)
let found = null;      // search results, null = no search
let here = new Set();  // ids of the entries matching the open page
let recent = {};       // id -> last use from this browser (ms)
let folder = '';       // folder filter, '' = all
let mode = 'all';      // 'all' | 'recent'
let sel = null;        // { id, user }
let editing = null;

lemoraIconsIn(document);

function toast(text, bad) {
  const t = $('toast');
  $('toast-text').textContent = text;
  t.classList.toggle('bad', !!bad);
  t.hidden = false;
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => { t.hidden = true; }, 3000);
}

// same theme as the app (light or dark): colors derived in icons.js
function applyTheme(t) {
  if (!t) return;
  const { scheme, ...vars } = lemoraThemeVars(t);
  const s = document.documentElement.style;
  for (const [k, v] of Object.entries(vars)) s.setProperty(k, v);
  s.colorScheme = scheme;
}

// tab host only for web pages (not for edge://, about:, extension pages)
function pageHost() {
  try { const u = new URL(tab?.url || ''); return /^https?:$/.test(u.protocol) ? u.host : ''; } catch { return ''; }
}

// ---- states (narrow window)
function showState({ icon = 'key', warn = false, center = false, title, text = '', wait = '', pri, sec, conn }) {
  document.body.className = 'is-state';
  $('app').hidden = true; $('unlock').hidden = true; $('state').hidden = false;
  const tile = $('state-tile');
  // linking screens (no specific icon): the Lemora logo, large and centered, instead of the key
  const logo = icon === 'key';
  if (logo) { const img = document.createElement('img'); img.src = 'icons/icon128.png'; img.alt = 'Lemora'; img.width = 72; img.height = 72; tile.replaceChildren(img); }
  else tile.replaceChildren(lemoraIcon(icon, 38));   // warning, lock...: large and centered too
  tile.classList.toggle('tile-logo', logo);
  tile.classList.toggle('tile-warn', warn);
  $('state-body').classList.toggle('center', center);
  $('state-title').textContent = title;
  $('state-text').textContent = text;
  $('state-wait').hidden = !wait; $('state-wait-text').textContent = wait;
  for (const [id, b] of [['state-pri', pri], ['state-sec', sec]]) {
    const n = $(id);
    n.hidden = !b; n.textContent = b ? b.label : ''; n.onclick = b ? b.run : null; n.disabled = false;
  }
  $('conn').hidden = !conn;
  if (conn) { $('conn-dot').className = 'dot ' + (conn.ok ? 'dot-ok' : 'dot-off'); $('conn-text').textContent = tr(conn.ok ? 'popup_conn_ok' : 'popup_conn_off'); }
}

// opened by itself after Windows Hello (small window instead of the toolbar popup): the page is the tab the popup was opened on,
// and the window closes when you click elsewhere, like the popup
const P = new URLSearchParams(location.search);
const AS_WINDOW = P.get('win') === '1';
if (AS_WINDOW) {
  document.documentElement.classList.add('as-window');
  setTimeout(() => window.addEventListener('blur', () => window.close()), 800);   // after it gets the focus
}

async function start() {
  const tabId = Number(P.get('tab')) || 0;
  if (AS_WINDOW && tabId) tab = await api.tabs.get(tabId).catch(() => null);
  else [tab] = await api.tabs.query({ active: true, currentWindow: true });
  const st = await send({ cmd: 'status' });
  applyTheme(st.theme);
  const retry = { label: tr('retry'), run: start };
  if (st.state === 'nohost') return showState({ icon: 'warn', warn: true, title: tr('lemora_not_responding'), text: tr('popup_nohost_text'), sec: retry, conn: { ok: false } });
  if (st.state === 'notallowed') return showState({ icon: 'warn', warn: true, title: tr('link_refused'), text: tr('link_refused_text'), sec: retry, conn: { ok: false } });
  if (st.state === 'closed') return showState({ icon: 'warn', warn: true, title: tr('lemora_not_open'), text: tr('popup_closed_text'), conn: { ok: false },
    pri: { label: tr('open_lemora'), run: async () => { $('state-pri').disabled = true; await send({ cmd: 'open' }); setTimeout(start, 4000); } }, sec: retry });
  if (st.state === 'unpaired') return showState({ title: tr('link_extension'), text: tr('popup_unpaired_text'),
    pri: { label: tr('link_to_lemora'), run: pair } });
  if (st.state === 'locked') return showState({ icon: 'lock', center: true, title: tr('lemora_locked'), text: tr('popup_locked_text'), conn: { ok: true },
    pri: { label: tr('unlock_in_lemora'), run: async () => { await send({ cmd: 'open' }); window.close(); } }, sec: retry });
  // every 20 minutes: Windows Hello starts by itself as soon as the popup opens, no click and no Lemora window
  // without Windows Hello: the master password right here, in a small box
  if (st.state === 'confirm') return st.hello ? confirmMe() : showUnlock();
  // only a real "ready" opens the list: any other answer (an error of the browser, of the channel) was shown as an empty list,
  // as if the extension were connected. The detail says what went wrong
  if (st.state !== 'ready') return showState({ icon: 'warn', warn: true, title: tr('lemora_not_responding'), text: st.error || st.detail ? tr('popup_link_failed_detail', st.detail || st.error) : tr('popup_link_failed'), sec: retry, conn: { ok: false } });
  await openApp();
}

async function confirmMe() {
  showState({ icon: 'lock', center: true, title: tr('confirm_its_you'), text: tr('popup_confirm_text'), wait: tr('popup_waiting_hello') });
  const r = await send({ cmd: 'confirm', tab: tab?.id || 0 });
  if (r.ok) return start();
  showUnlock();   // Windows Hello cancelled or not available: the password box
}

// ---- master password in the popup (every 20 minutes): only the title, the box and the button
function showUnlock(error = '') {
  document.body.className = 'is-unlock';
  $('state').hidden = true; $('app').hidden = true; $('unlock').hidden = false;
  const err = $('unlock-err');
  err.hidden = !error; err.textContent = error;
  const pw = $('unlock-pw');
  pw.value = ''; pw.disabled = false; $('unlock-ok').disabled = false;
  setTimeout(() => pw.focus(), 50);
}
$('unlock').onsubmit = async (ev) => {
  ev.preventDefault();
  const pw = $('unlock-pw');
  if (!pw.value) return showUnlock(tr('popup_type_master'));
  pw.disabled = true; $('unlock-ok').disabled = true;
  const r = await send({ cmd: 'confirmpw', password: pw.value });
  pw.value = '';
  if (r.ok) { $('unlock').hidden = true; return start(); }
  showUnlock(r.error === 'wrong' ? tr('popup_wrong_password') : r.error === 'wait' ? tr('popup_too_many') : r.error === 'locked' ? tr('popup_locked_unlock_app') : tr('not_confirmed', r.error || tr('no_answer')));
};

async function pair() {
  showState({ title: tr('link_extension'), text: tr('popup_pair_text'), wait: tr('popup_waiting_approval') });
  const r = await send({ cmd: 'pair', tab: tab?.id || 0 });
  if (r.ok) return start();
  showState({ title: tr('popup_not_linked'), text: r.error === 'refused' ? tr('popup_link_refused_in_lemora') : tr('not_confirmed', r.error || tr('no_answer')), pri: { label: tr('retry'), run: pair } });
}

// ---- list and detail (wide window)
async function openApp() {
  document.body.className = 'is-app';
  $('state').hidden = true; $('unlock').hidden = true; $('app').hidden = false;
  const [f, l, m, rc] = await Promise.all([send({ cmd: 'folders' }), send({ cmd: 'list', q: '' }), send({ cmd: 'match', url: tab?.url || '' }), send({ cmd: 'recent' })]);
  // Lemora said no (confirmation expired, unpaired, closed...): the state screen says why, never an empty list that looks connected
  if (!l.ok) {
    const st = await send({ cmd: 'status' });
    if (st.state !== 'ready') return start();
    return showState({ icon: 'warn', warn: true, title: tr('lemora_not_responding'), text: tr('popup_list_unavailable', l.error || tr('no_answer')), sec: { label: tr('retry'), run: start }, conn: { ok: false } });
  }
  folders = f.ok ? f.folders : [];
  all = l.ok ? l.items : [];
  here = new Set(m.ok ? m.items.map((e) => e.id) : []);
  // the page's entries are there even if the full list was truncated (Lemora sends at most 300)
  if (m.ok) for (const e of m.items) if (!all.some((x) => x.id === e.id)) all.push(e);
  recent = rc.ok ? rc.recent : {};
  $('menu-count').textContent = `${tr(all.length === 1 ? 'popup_count_entry_one' : 'popup_count_entry_many', all.length)} · ${tr(folders.length === 1 ? 'popup_count_folder_one' : 'popup_count_folder_many', folders.length)}`;
  buildFolderMenu();
  render();
  $('q').focus();
}

const inFolder = (e) => !folder || e.folder === folder || e.folder.startsWith(folder + ' / ');
// one row per entry (name and folder); its users are all shown in the detail
const rowsOf = (list) => list.filter(inFolder).map((e) => ({ e }));
const usersOf = (e) => (e.users.length ? e.users : ['']);
// every address of the entry, the main one first (an older Lemora sends only "ip")
const ipsOf = (e) => (e.ips?.length ? e.ips : e.ip ? [e.ip] : []);
const pageOrigin = () => (pageHost() ? new URL(tab.url).origin : '');
const sameSel = (r) => sel && sel.id === r.e.id;

function groups() {
  if (found) return [[tr('popup_results'), rowsOf(found)]];
  if (mode === 'recent') {
    const now = Date.now(), day = 864e5;
    const used = rowsOf(all.filter((e) => recent[e.id])).sort((a, b) => recent[b.e.id] - recent[a.e.id]);
    const today = new Date(); today.setHours(0, 0, 0, 0);
    return [[tr('popup_today'), used.filter((r) => recent[r.e.id] >= +today)],
      [tr('popup_last7'), used.filter((r) => recent[r.e.id] < +today && now - recent[r.e.id] < 7 * day)],
      [tr('popup_earlier'), used.filter((r) => now - recent[r.e.id] >= 7 * day)]];
  }
  return [[tr('popup_match_site'), rowsOf(all.filter((e) => here.has(e.id)))], [tr(here.size ? 'popup_other_entries' : 'popup_all_entries'), rowsOf(all.filter((e) => !here.has(e.id)))]];
}

function render() {
  const gs = groups();
  const rows = gs.flatMap(([, r]) => r);
  $('chip-all').textContent = tr('popup_all_count', rowsOf(found || all).length);
  $('chip-all').setAttribute('aria-pressed', String(mode === 'all' || !!found));
  $('chip-recent').setAttribute('aria-pressed', String(mode === 'recent' && !found));
  if (!rows.some(sameSel)) sel = rows[0] ? { id: rows[0].e.id, user: usersOf(rows[0].e)[0] } : null;
  const list = $('list');
  list.replaceChildren();
  for (const [title, rs] of gs) {
    if (!rs.length) continue;
    list.append(el('div', 'group', title));
    for (const r of rs) list.append(row(r));
  }
  const empty = $('empty');
  empty.hidden = rows.length > 0;
  empty.textContent = tr(found ? 'popup_none_found' : mode === 'recent' ? 'popup_none_used' : folder ? 'popup_none_folder' : 'popup_none');
  showDetail();
}

function row(r) {
  const b = el('button', 'row');
  b.type = 'button';
  if (sameSel(r)) b.setAttribute('aria-current', 'true');
  const av = el('span', 'avatar'); av.append(lemoraIcon('user', 17));
  const main = el('span', 'row-main');
  main.append(el('span', 'row-title', r.e.name || r.e.ip || ''), el('span', 'row-sub', r.e.folder || tr('popup_no_folder')));
  b.append(av, main);
  b.onclick = () => { sel = { id: r.e.id, user: usersOf(r.e)[0] }; render(); };
  return b;
}

function when(ms) {
  if (!ms) return tr('popup_never');
  const d = new Date(ms), t = new Date(); t.setHours(0, 0, 0, 0);
  const hm = d.toLocaleTimeString(LOCALE, { hour: '2-digit', minute: '2-digit' });
  if (ms >= +t) return tr('popup_today_at', hm);
  if (ms >= +t - 864e5) return tr('popup_yesterday_at', hm);
  return d.toLocaleDateString(LOCALE, { day: 'numeric', month: 'long' });
}

function current() { return sel && ((found || []).find((e) => e.id === sel.id) || all.find((e) => e.id === sel.id)); }

function showDetail() {
  if (editing !== null) return;
  const e = current();
  $('detail-empty').hidden = !!e; $('detail-body').hidden = !e; $('form').hidden = true;
  closeMenus();
  disarmDelete();
  if (!e) return;
  $('d-title').textContent = lemoraLabel(e);
  // every saved user, one card each (user and password, with copy). One user: "Fill in" at the top; several: one per user
  const canFill = here.has(e.id) && !!pageHost();
  const box = $('d-users');
  box.replaceChildren(...usersOf(e).map((u) => userCard(e, u, canFill && e.users.length > 1)));
  lemoraIconsIn(box);
  const ips = ipsOf(e);
  $('d-url-label').textContent = tr(ips.length > 1 ? 'addresses' : 'address');
  $('d-urls').replaceChildren(...ips.map((ip) => el('div', 'url', ip)));
  // the open page is not among the entry's addresses: one click adds it (e.g. the NAS's second network card)
  const origin = pageOrigin();
  const add = $('d-add-ip');
  add.hidden = !origin || here.has(e.id) || !e.folder || ips.some((x) => x.toLowerCase() === origin.toLowerCase());
  add.textContent = '+ ' + tr('popup_add_this_address');
  add.title = tr('popup_add_this_address_tip', origin);
  $('d-folder').textContent = e.folder || tr('popup_no_folder');
  $('d-shared').hidden = !e.shared;
  $('d-last').textContent = when(recent[e.id]);
  $('d-fill').hidden = !(canFill && e.users.length === 1);
  // "nobody's" password: without a folder it cannot be edited or deleted from the extension (the folder is assigned in the app)
  const orphan = !e.folder;
  $('d-edit').hidden = $('d-more').hidden = orphan;
  $('d-orphan').hidden = !orphan;
}

function userCard(e, u, fillHere) {
  const field = (icon, label, value) => {
    const f = el('div', 'field'), i = el('span'), m = el('div', 'field-main'), v = el('span', 'field-value', value);
    i.dataset.icon = icon;
    m.append(el('span', 'field-label', label), v);
    f.append(i, m);
    return [f, v];
  };
  const ghost = (label, tip, run) => { const b = el('button', 'btn-ghost'); b.type = 'button'; b.dataset.icon = 'copy'; b.setAttribute('aria-label', label); b.title = tip; b.onclick = run; return b; };
  const [fu] = field('user', tr('user'), u || tr('popup_no_user'));
  const [fp, dots] = field('key', 'Password', '••••••••••••');
  dots.classList.add('field-dots'); dots.setAttribute('aria-label', tr('popup_password_hidden'));
  if (fillHere) { const b = el('button', 'btn btn-pri btn-s', tr('popup_fill')); b.type = 'button'; b.onclick = () => fillUser(e, u); fu.append(b); }
  if (u) {
    fu.append(ghost(tr('popup_copy_user'), tr('popup_copy_user'), () => copyUser(e, u)));
    fp.append(ghost(tr('popup_copy_password'), tr('popup_copy_password_tip'), () => copyPw(e, u)));
  }
  const card = el('div', 'card');
  card.append(fu, fp);
  return card;
}

async function fillUser(e, u) {
  const r = await api.tabs.sendMessage(tab.id, { cmd: 'fillFromPopup', id: e.id, user: u }).catch(() => null);
  if (r?.ok) window.close(); else toast(tr('popup_no_login_field'), true);
}
async function copyUser(e, u) {
  try { await navigator.clipboard.writeText(u); toast(tr('popup_user_copied', e.name || '')); } catch { toast(tr('popup_not_copied'), true); }
}
async function copyPw(e, u) {
  const r = await send({ cmd: 'copy', id: e.id, user: u });
  if (r.ok) { recent[e.id] = Date.now(); $('d-last').textContent = when(recent[e.id]); }
  toast(r.ok ? tr('popup_pw_copied') : tr('popup_pw_not_copied', r.error || tr('error_generic')), !r.ok);
}
$('d-fill').onclick = () => { const e = current(); if (e) fillUser(e, usersOf(e)[0]); };
$('d-add-ip').onclick = async () => {
  const e = current(), origin = pageOrigin();
  if (!e || !origin) return;
  const ips = [...ipsOf(e), origin];
  // no "users": Lemora keeps them as they are. "ip" too, for a Lemora that does not know "ips" yet
  const r = await send({ cmd: 'save', entry: { id: e.id, name: e.name, folder: e.folder, ip: ips[0], ips } });
  if (!r.ok) return toast(tr('not_saved', r.error || tr('lemora_not_responding')), true);
  toast(tr('popup_address_added', origin));
  const keep = sel;
  await openApp();
  sel = keep; render();
};
$('d-open').onclick = () => send({ cmd: 'open' });
$('d-edit').onclick = () => openEdit(current());

// ---- pop-up menus (menu, folders, more actions)
const MENUS = [['menu-btn', 'menu'], ['folder-btn', 'folder-menu'], ['d-more', 'd-menu']];
function closeMenus(except) { for (const [b, m] of MENUS) if (m !== except) { $(m).hidden = true; $(b).setAttribute('aria-expanded', 'false'); } }
for (const [b, m] of MENUS) $(b).onclick = (ev) => {
  ev.stopPropagation();
  const open = $(m).hidden;
  closeMenus(m);
  $(m).hidden = !open; $(b).setAttribute('aria-expanded', String(open));
  if (open) $(m).querySelector('button')?.focus();
};
document.addEventListener('click', (ev) => { if (!ev.target.closest('.pop')) closeMenus(); });
document.addEventListener('keydown', (ev) => { if (ev.key === 'Escape' && MENUS.some(([, m]) => !$(m).hidden)) { ev.preventDefault(); closeMenus(); } });
$('menu-open').onclick = () => { closeMenus(); send({ cmd: 'open' }); };
$('menu-reload').onclick = () => { closeMenus(); openApp(); };

function buildFolderMenu() {
  const m = $('folder-menu');
  m.replaceChildren();
  for (const f of ['', ...folders]) {
    const b = el('button', 'pop-item');
    b.type = 'button'; b.setAttribute('role', 'menuitemradio'); b.setAttribute('aria-checked', String(f === folder));
    b.append(lemoraIcon('folder', 15), el('span', null, f || tr('all_folders')));
    b.onclick = () => { folder = f; $('folder-label').textContent = f ? f.split(' / ').pop() : tr('all_folders'); $('folder-btn').title = f; $('q').placeholder = f ? tr('popup_search_in', f.split(' / ').pop()) : tr('popup_search_all'); closeMenus(); buildFolderMenu(); render(); };
    m.append(b);
  }
}

$('chip-all').onclick = () => { mode = 'all'; render(); };
$('chip-recent').onclick = () => { mode = 'recent'; if (found) { $('q').value = ''; found = null; } render(); };
$('q').addEventListener('input', () => {
  clearTimeout(render.t);
  render.t = setTimeout(async () => {
    const q = $('q').value.trim();
    if (!q) { found = null; return render(); }
    const r = await send({ cmd: 'list', q });
    if ($('q').value.trim() !== q) return;   // meanwhile the user typed something else
    found = r.ok ? r.items : [];
    render();
  }, 150);
});

// ---- delete: confirm with a second click (confirm() does not exist in Firefox popups)
function disarmDelete() { const b = $('d-delete'); delete b.dataset.armed; $('d-delete-text').textContent = tr('popup_delete_entry'); }
$('d-delete').onclick = async (ev) => {
  ev.stopPropagation();
  const e = current(); if (!e) return;
  const b = $('d-delete');
  if (!b.dataset.armed) {
    b.dataset.armed = '1';
    $('d-delete-text').textContent = tr('popup_click_again_delete');
    setTimeout(disarmDelete, 8000);
    return;
  }
  disarmDelete(); closeMenus();
  const r = await send({ cmd: 'delete', id: e.id });
  if (!r.ok) return toast(tr('popup_not_deleted', r.error || tr('error_generic')), true);
  toast(tr('popup_deleted', e.name || e.ip));
  await openApp();
};

// ---- edit and new entry
// random password: 16 characters, at least one of each kind, without easily confused characters (0/O, 1/l/I)
function generate(n = 16) {
  const sets = ['ABCDEFGHJKLMNPQRSTUVWXYZ', 'abcdefghijkmnopqrstuvwxyz', '23456789', '!#$%&*+-=?@^_'];
  const pick = (s) => { const lim = 256 - (256 % s.length); for (;;) { const b = crypto.getRandomValues(new Uint8Array(1))[0]; if (b < lim) return s[b % s.length]; } };
  const out = sets.map(pick);
  while (out.length < n) out.push(pick(sets.join('')));
  for (let i = out.length - 1; i > 0; i--) { const j = crypto.getRandomValues(new Uint32Array(1))[0] % (i + 1); [out[i], out[j]] = [out[j], out[i]]; }
  return out.join('');
}

function userRow(user = '', isNew = true) {
  const r = el('div', 'user-row');
  const u = el('input', 'in'); u.type = 'text'; u.value = user; u.placeholder = tr('user'); u.setAttribute('aria-label', tr('user')); u.dataset.was = user; u.autocomplete = 'off'; u.spellcheck = false;
  const p = el('input', 'in'); p.type = 'password'; p.placeholder = isNew ? 'Password' : tr('popup_unchanged'); p.setAttribute('aria-label', 'Password'); p.autocomplete = 'new-password';
  const g = el('button', 'btn btn-sec btn-square-s'); g.type = 'button'; g.title = tr('popup_generate'); g.setAttribute('aria-label', tr('popup_generate')); g.append(lemoraIcon('refresh', 15));
  g.onclick = () => { p.value = generate(); p.type = 'text'; toast(tr('popup_generated')); };
  const x = el('button', 'btn btn-sec btn-square-s', '✕'); x.type = 'button'; x.setAttribute('aria-label', tr('popup_remove_user')); x.onclick = () => r.remove();
  r.append(u, p, g, x);
  return r;
}

function ipRow(value = '') {
  const r = el('div', 'ip-row');
  const i = el('input', 'in'); i.type = 'text'; i.value = value; i.placeholder = 'https://'; i.setAttribute('aria-label', tr('address')); i.autocomplete = 'off'; i.spellcheck = false;
  const x = el('button', 'btn btn-sec btn-square-s', '✕'); x.type = 'button'; x.setAttribute('aria-label', tr('popup_remove_address')); x.title = tr('popup_remove_address'); x.onclick = () => r.remove();
  r.append(i, x);
  return r;
}

function openEdit(e) {
  editing = e || false;
  closeMenus();
  $('app').classList.add('editing');
  $('edit-title').textContent = tr(e ? 'popup_edit_entry' : 'new_entry');
  const host = pageHost();
  $('f-name').value = e ? e.name : host;
  const ipList = e ? ipsOf(e) : [pageOrigin()];
  $('f-ips').replaceChildren(...(ipList.length ? ipList : ['']).map((x) => ipRow(x)));
  const s = $('f-folder');
  // folder mandatory: in Lemora there are no "nobody's" passwords
  s.replaceChildren(Object.assign(el('option', null, tr('choose_folder')), { value: '' }));
  for (const f of folders) s.append(Object.assign(el('option', null, f), { value: f }));
  s.value = e ? e.folder : folder;
  const users = $('f-users');
  users.replaceChildren();
  if (e) for (const u of e.users) users.append(userRow(u, false));
  else users.append(userRow('', true));
  clearInvalid();
  $('detail-empty').hidden = true; $('detail-body').hidden = true; $('form').hidden = false;
  $('f-name').focus();
}

function closeEdit() { editing = null; $('app').classList.remove('editing'); $('form').hidden = true; showDetail(); }

$('f-add-user').onclick = () => { $('f-users').append(userRow('', true)); $('f-users').lastElementChild.querySelector('input').focus(); };
$('f-add-ip').onclick = () => { $('f-ips').append(ipRow()); $('f-ips').lastElementChild.querySelector('input').focus(); };
$('f-cancel').onclick = closeEdit;
$('add').onclick = () => openEdit(null);
$('form').addEventListener('keydown', (ev) => { if (ev.key === 'Escape') { ev.preventDefault(); closeEdit(); } });

// missing fields: blue border and message under the bar (the error used to end up at the bottom of the form, out of sight: Save seemed to do nothing)
function clearInvalid() {
  for (const n of $('form').querySelectorAll('.invalid')) n.classList.remove('invalid');
  $('f-error').hidden = true;
}
function showError(text, fields = []) {
  for (const n of fields) n.classList.add('invalid');
  $('f-error').textContent = text; $('f-error').hidden = false;
  (fields[0] || $('f-error')).focus?.();
}
$('form').addEventListener('input', (ev) => ev.target.classList?.remove('invalid'));
$('form').addEventListener('change', (ev) => ev.target.classList?.remove('invalid'));

$('form').addEventListener('submit', async (ev) => {
  ev.preventDefault();
  clearInvalid();
  const bad = [];
  const rows = [...$('f-users').querySelectorAll('.user-row')];
  const users = [];
  for (const r of rows) {
    const [u, p] = r.querySelectorAll('input');
    const name = u.value.trim();
    if (!name) { if (p.value) bad.push(u); continue; }   // empty row: ignored; password without user: the user is missing
    if (!u.dataset.was && !p.value) bad.push(p);          // new user: the password is required
    const o = { user: name, was: u.dataset.was || name };
    if (p.value) o.password = p.value;   // empty on an existing user = the previous one is kept
    users.push(o);
  }
  if (!users.length && !bad.length) bad.push(rows[0]?.querySelector('input') || $('f-add-user'));
  if (!$('f-name').value.trim()) bad.unshift($('f-name'));
  const ipInputs = [...$('f-ips').querySelectorAll('input')];
  const ips = [...new Set(ipInputs.map((i) => i.value.trim()).filter(Boolean))];
  if (!ips.length) bad.push(ipInputs[0] || $('f-add-ip'));
  if (!$('f-folder').value) bad.push($('f-folder'));
  if (bad.length) return showError(tr('popup_fill_fields'), bad);
  const entry = { id: editing?.id || '', name: $('f-name').value.trim(), ip: ips[0], ips, folder: $('f-folder').value, users };
  const r = await send({ cmd: 'save', entry });
  if (!r.ok) return showError(tr('not_saved', r.error || tr('lemora_not_responding')));
  for (const i of $('f-users').querySelectorAll('input[type="text"], input[type="password"]')) i.value = '';   // the password does not stay in the popup
  toast(tr('popup_saved', lemoraLabel(entry)));
  editing = null; $('app').classList.remove('editing'); $('form').hidden = true;
  await openApp();
  const saved = all.find((x) => x.name === entry.name && x.ip === entry.ip);
  if (saved) { sel = { id: saved.id, user: users[0]?.user || '' }; render(); }
});

start();
