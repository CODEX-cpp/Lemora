// Lemora: in web pages. Finds login fields, puts the Lemora icon in the field, shows the entries saved for this address and fills them
// on the user's click (never on its own). After a submit with a password it offers to save it. The UI lives in a closed shadow DOM: the page cannot touch it.
'use strict';
(() => {
  // "already loaded" marker as a Symbol: a page element with id="__lemoraLoaded" cannot fake it (named access on window)
  const LOADED = Symbol.for('lemora.loaded');
  if (window[LOADED]) return;
  Object.defineProperty(window, LOADED, { value: true });
  if (location.protocol !== 'http:' && location.protocol !== 'https:') return;
  // inside a cross-origin frame we do nothing: a site could frame a device's admin panel
  // and get the password filled with a deceptive click (clickjacking). The background checks again anyway
  const sameOriginTop = (() => { if (window.top === window) return true; try { return window.top.location.origin === location.origin; } catch { return false; } })();
  if (!sameOriginTop) return;
  const api = globalThis.browser ?? globalThis.chrome;
  const send = (msg) => { try { return Promise.resolve(api.runtime.sendMessage(msg)).then((r) => r || { ok: false }, () => ({ ok: false })); } catch { return Promise.resolve({ ok: false }); } };   // promise: in Firefox the callback does not exist
  const tr = (key, ...subs) => api.i18n.getMessage(key, subs.map(String));   // texts from _locales (Italian or English)

  // ---- style: dark islands, light blue accent, 4px corners; the user's theme comes from Lemora and replaces the colors
  // UI_CSS, not CSS: a constant named CSS hides the browser's CSS object (CSS.escape) in this whole file
  const UI_CSS = `
    :host { all: initial !important; }
    * { box-sizing: border-box; font-family: "Segoe UI Variable Text", "Segoe UI", "Noto Sans", system-ui, sans-serif; }
    svg { display: block; flex: none; }
    /* the Lemora logo in its dark version (always dark, whatever the theme: it is the logo, recognizable on every page) */
    .icon { position: fixed; width: 24px; height: 24px; border-radius: 5px; border: 1px solid rgba(255,255,255,.22); padding: 0; cursor: pointer; z-index: 2147483646;
      background: #1c1c1c; color: #f2f2f2; display: grid; place-items: center; }
    .icon > svg { width: 72%; height: 72%; }
    /* no shadow on the icons: the browser counts the shadow as covering the icon of the field below (which would no longer be clickable).
       For the same reason the small counter must not stick out above or below: it would cover the nearby field's icon, which would then count as "covered" and not clickable */
    .icon.small .bdg { display: none; }
    .icon:focus-visible { outline: 2px solid var(--accent, #60CDFF); outline-offset: 2px; }
    .bdg { position: absolute; right: -6px; bottom: -2px; min-width: 13px; height: 13px; padding: 0 2px; border-radius: 7px; background: var(--bg, #1e1e1e);
      border: 1px solid var(--accent, #60CDFF); color: var(--accent, #60CDFF); font: 700 9px/1 "Segoe UI", sans-serif; display: grid; place-items: center; }
    .dd { position: fixed; z-index: 2147483647; background: var(--bg, #1e1e1e); color: var(--text, #F0F0F0); border: 1px solid var(--line-2, #4a4a4a); border-radius: 4px;
      box-shadow: 0 8px 24px rgba(0,0,0,.4); font-size: 13px; line-height: 1.35; }
    .menu { width: 312px; max-height: 360px; overflow-y: auto; }
    .mhead { height: 34px; display: flex; align-items: center; gap: 8px; padding: 0 12px 0 14px; font-size: 12.5px; color: var(--text-2, #b0b0b0); border-bottom: 1px solid var(--line, #2e2e2e); }
    .mhead span:first-child { flex: 1; }
    .mhost { max-width: 50%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--text-3, #8a8a8a); }
    button.item { all: unset; position: relative; display: flex; align-items: center; gap: 10px; width: 100%; height: 52px; padding: 0 12px 0 14px; cursor: pointer; box-sizing: border-box; }
    button.item:hover, button.item:focus-visible { background: var(--raised, #33383c); }
    button.item:hover::before, button.item:focus-visible::before { content: ""; position: absolute; left: 0; top: 8px; bottom: 8px; width: 3px; border-radius: 2px; background: var(--accent, #60CDFF); }
    .av { width: 32px; height: 32px; flex: none; border-radius: 4px; background: var(--field, #2b2b2b); color: var(--text-2, #d0d0d0); display: grid; place-items: center; }
    .txt { display: flex; flex-direction: column; min-width: 0; }
    .name { font-size: 14px; font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .sub { font-size: 12px; color: var(--text-2, #b8b8b8); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .box { padding: 14px; display: flex; flex-direction: column; gap: 10px; }
    .box + .box { border-top: 1px solid var(--line, #2e2e2e); }
    .boxhead { display: flex; align-items: center; gap: 10px; }
    .tile { width: 32px; height: 32px; flex: none; border-radius: 4px; background: var(--accent, #60CDFF); color: var(--accent-ink, #00283b); display: grid; place-items: center; }
    .tile.s { width: 28px; height: 28px; }
    .tile.warn { background: var(--field, #2b2b2b); color: var(--warn, #FDD663); }
    .mt { font-size: 14.5px; font-weight: 600; }
    .ms { font-size: 12.5px; color: var(--text-2, #b0b0b0); line-height: 1.4; }
    .alert { margin: 8px 14px 0; padding: 6px 8px; border-radius: 4px; background: var(--warn-bg, rgba(253,214,99,.12)); color: var(--warn, #FDD663); font-size: 12px; line-height: 1.35; }
    .acts { display: flex; gap: 8px; justify-content: flex-end; }
    .act { all: unset; height: 32px; padding: 0 16px; border-radius: 4px; background: var(--accent, #60CDFF); color: var(--accent-ink, #00283b); font-weight: 700; font-size: 13px;
      display: inline-flex; align-items: center; cursor: pointer; box-sizing: border-box; white-space: nowrap; }
    .act.sec { background: transparent; color: var(--text, #F0F0F0); border: 1px solid var(--line-2, #5a5a5a); font-weight: 400; padding: 0 14px; }
    .act:focus-visible, .ib:focus-visible { outline: 2px solid var(--accent, #60CDFF); outline-offset: 2px; }
    .ib { all: unset; width: 28px; height: 28px; flex: none; border-radius: 4px; display: grid; place-items: center; cursor: pointer; color: var(--text-2, #b0b0b0); }
    .ib:hover { background: rgba(240,240,240,.07); }
    .fl { font-size: 12px; color: var(--text-2, #b8b8b8); margin: 0 0 4px; }
    .genrow { height: 38px; display: flex; align-items: center; gap: 8px; padding: 0 5px 0 10px; background: var(--card, #171717); border: 1px solid var(--line, #2e2e2e); border-radius: 4px; }
    .genpw { flex: 1; min-width: 0; font: 13.5px "Cascadia Mono", Consolas, monospace; color: var(--text, #F0F0F0); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .toast { position: fixed; left: 50%; bottom: 16px; transform: translateX(-50%); z-index: 2147483647; display: flex; align-items: center; gap: 10px; max-width: calc(100vw - 32px);
      padding: 9px 14px; background: var(--surface, #262626); color: var(--text, #F0F0F0); border: 1px solid var(--line-2, #4a4a4a); border-left: 3px solid var(--ok, #6CCB5F); border-radius: 4px;
      font-size: 13px; pointer-events: none; box-shadow: 0 8px 24px rgba(0,0,0,.4); }
    .ok { width: 18px; height: 18px; flex: none; border-radius: 50%; background: var(--ok, #6CCB5F); color: var(--bg, #101010); display: grid; place-items: center; font-size: 10px; font-weight: 800; }
    .bar { top: 12px; right: 12px; width: 300px; padding: 14px; display: flex; flex-direction: column; gap: 10px; }
    .bar .mt { flex: 1; }
    .bar select, .bar input, .fld { width: 100%; height: 34px; padding: 0 10px; border-radius: 4px; border: 1px solid var(--line-2, #3a3a3a); background: var(--card, #171717); color: var(--text, #F0F0F0);
      font-size: 13px; display: flex; align-items: center; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .bar input:focus, .bar select:focus { outline: none; border-bottom: 2px solid var(--accent, #60CDFF); }
    .bar select.invalid, .bar input.invalid { border: 1px solid var(--accent, #60CDFF); box-shadow: 0 0 0 1px var(--accent, #60CDFF); }
    .err { color: var(--danger, #FF9B94); margin: 0; font-size: 12.5px; }
    @media (prefers-reduced-motion: reduce) { * { transition: none !important; } }`;
  const icon = (name, size) => lemoraIcon(name, size);   // icons.js, loaded before this file (same isolated world, the page cannot see it)

  // ---- container: closed shadow DOM, in the top layer (popover) when possible, with locked style.
  // The page can only touch the <lemora-ui> element: if it makes it invisible or transparent, clicks do not count (see genuine)
  const host = document.createElement('lemora-ui');
  const root = host.attachShadow({ mode: 'closed' });
  const style = document.createElement('style'); style.textContent = UI_CSS; root.appendChild(style);
  const HOST_STYLE = 'all: initial !important; position: fixed !important; inset: 0 !important; width: 0 !important; height: 0 !important; margin: 0 !important; padding: 0 !important; border: 0 !important; opacity: 1 !important; visibility: visible !important; display: block !important; overflow: visible !important; filter: none !important; transform: none !important; clip-path: none !important; mask: none !important; mix-blend-mode: normal !important; pointer-events: auto !important; z-index: 2147483647 !important; background: transparent !important;';
  const canPopover = typeof host.showPopover === 'function';
  let attached = false;
  const lockHost = () => { if (host.getAttribute('style') !== HOST_STYLE) host.setAttribute('style', HOST_STYLE); };
  const attach = () => {
    if (!attached || !host.isConnected) {
      lockHost();
      if (canPopover) host.setAttribute('popover', 'manual');
      document.documentElement.appendChild(host);
      attached = true;
    }
    // always on top of the top layer: reopening it puts it back above the page's other popovers
    if (canPopover) { try { if (host.matches(':popover-open')) host.hidePopover(); host.showPopover(); } catch { /* not supported */ } }
  };
  new MutationObserver(lockHost).observe(host, { attributes: true, attributeFilter: ['style', 'class', 'hidden', 'inert'] });

  // real visibility (Chrome/Edge: IntersectionObserver v2): false if the element is covered by anything, even by an overlay
  // that lets clicks through (pointer-events: none), or if it is transparent, filtered or transformed. Firefox lacks it: the other checks remain
  const ioV2 = typeof IntersectionObserverEntry !== 'undefined' && 'isVisible' in IntersectionObserverEntry.prototype;
  const shown = new WeakMap();
  const io = ioV2 ? new IntersectionObserver((list) => { for (const x of list) shown.set(x.target, x.isVisible); }, { trackVisibility: true, delay: 100, threshold: [1.0] }) : null;
  const watch = (el) => { if (io) { shown.set(el, false); io.observe(el); } return el; };
  // is a page field really visible right now (not covered by an overlay, not transparent)? Without IntersectionObserver v2: yes
  const reallyVisible = (el) => !ioV2 ? Promise.resolve(true) : new Promise((res) => {
    const o = new IntersectionObserver((list) => { o.disconnect(); res(list.some((x) => x.isVisible)); }, { trackVisibility: true, delay: 100, threshold: [0.5] });
    o.observe(el);
    setTimeout(() => { o.disconnect(); res(false); }, 1500);
  });
  // the field's own decorations (floating label "Password", lock icon, show-password eye) count as covering it for IntersectionObserver v2
  // (VTENext login). They are the field's labels and small icons over less than half of it: anything bigger still counts as a cover
  function decorationsOver(field) {
    const r = field.getBoundingClientRect(), labels = new Set(field.labels || []), out = [];
    const scope = FORM_OF.call(field) || field.parentElement?.parentElement?.parentElement || document.body;
    let covered = 0;
    for (const e of scope.querySelectorAll('*')) {
      if (e === field || e.contains(field) || field.contains(e)) continue;
      const b = e.getBoundingClientRect();
      const w = Math.min(b.right, r.right) - Math.max(b.left, r.left), h = Math.min(b.bottom, r.bottom) - Math.max(b.top, r.top);
      if (w <= 0 || h <= 0) continue;
      if (!labels.has(e) && (b.width > 64 || b.height > r.height * 1.6)) continue;
      covered += w * h; out.push(e);
    }
    return covered <= (r.width * r.height) / 2 ? out : [];
  }
  // hides them at once (no transition) and returns how to put back their exact style attribute
  const hideNow = (els) => els.map((e) => {
    const s = e.getAttribute('style');
    e.style.setProperty('transition', 'none', 'important'); e.style.setProperty('visibility', 'hidden', 'important');
    return () => (s === null ? e.removeAttribute('style') : e.setAttribute('style', s));
  });

  // the click counts only if it comes from the user, the UI is really visible at that point and has been for at least half a second
  const MIN_SHOWN_MS = 500;
  function genuine(e, shownAt, box) {
    if (!e.isTrusted) return false;
    if (shownAt && performance.now() - shownAt < MIN_SHOWN_MS) return false;
    if (io && box && shown.get(box) !== true) return false;
    for (let n = host; n && n.nodeType === 1; n = n.parentElement) {
      const cs = getComputedStyle(n);
      if (cs.opacity !== '1' || cs.visibility !== 'visible' || (cs.filter && cs.filter !== 'none')) return false;
    }
    if (host.hasAttribute('inert') || document.documentElement.hasAttribute('inert')) return false;
    // the page can cover the UI only by opening something in the top layer after it (popover, modal dialog, fullscreen):
    // in that case clicks do not count. Needed mostly where IntersectionObserver v2 is missing (Firefox)
    if (document.fullscreenElement) return false;
    try { if ([...document.querySelectorAll(':popover-open, dialog:modal')].some((x) => x !== host)) return false; } catch { /* selectors not supported */ }
    const top = document.elementsFromPoint(e.clientX, e.clientY)[0];
    return top === host;
  }

  let status = null;
  let items = null;
  let menu = null;
  let menuShownAt = 0;
  const icons = new Map();   // field -> button

  // Lemora theme (light or dark, like the app): the variables go into the shadow stylesheet (the <lemora-ui> style stays locked).
  // lemoraThemeVars (icons.js) accepts only #rrggbb colors and outputs only colors: no page or app text ends up in the CSS
  function applyTheme(t) {
    if (!t) return;
    const { scheme, ...v } = lemoraThemeVars(t);
    style.textContent = UI_CSS + `
:host { color-scheme: ${scheme}; ${Object.entries(v).map(([k, x]) => `${k}: ${x};`).join(' ')} }`;
  }

  async function load() {
    status = await send({ cmd: 'status' });
    applyTheme(status.theme);
    items = status.state === 'ready' ? ((await send({ cmd: 'match' })).items || []) : [];
  }

  // ---- login fields: only those the user really sees (no transparent, tiny, off-screen fields or fields hidden by an ancestor)
  function visible(el) {
    const r = el.getBoundingClientRect();
    if (r.width < 30 || r.height < 12) return false;
    if (r.bottom < 0 || r.right < 0 || r.top > innerHeight + 2000 || r.left > innerWidth) return false;
    for (let n = el, i = 0; n && n.nodeType === 1 && i < 25; n = n.parentElement, i++) {
      const cs = getComputedStyle(n);
      if (cs.visibility === 'hidden' || cs.display === 'none' || parseFloat(cs.opacity) < 0.5) return false;
    }
    return true;
  }
  function userFieldFor(pw) {
    const scope = pw.form || document;
    const all = [...scope.querySelectorAll('input')].filter((i) => visible(i) && !i.disabled && !i.readOnly);
    const idx = all.indexOf(pw);
    for (let i = idx - 1; i >= 0; i--) if (/^(text|email|tel|)$/i.test(all[i].type)) return all[i];
    return null;
  }
  function passwordFields() { return [...document.querySelectorAll('input[type="password"]')].filter((i) => visible(i) && !i.disabled); }

  // does the form send data to another origin? Then no filling (a tampered page could ship the password elsewhere).
  // Original getters (a field named "action" would shadow form.action), plus the formaction of every submit button of the form
  const ACTION = Object.getOwnPropertyDescriptor(HTMLFormElement.prototype, 'action').get;
  const BTN_ACTION = Object.getOwnPropertyDescriptor(HTMLButtonElement.prototype, 'formAction').get;
  const INPUT_ACTION = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'formAction').get;
  const FORM_OF = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'form').get;
  // same origin; the only exception allowed is http to https on the same host (safer, not less safe)
  const sameOrigin = (u) => {
    try {
      const t = new URL(u || location.href, location.href);
      return t.origin === location.origin || (location.protocol === 'http:' && t.protocol === 'https:' && t.hostname === location.hostname);
    } catch { return false; }
  };
  function formTargetOk(field) {
    const f = field ? FORM_OF.call(field) : null;
    if (!f) return true;
    if (!sameOrigin(ACTION.call(f))) return false;
    const submitters = [...f.querySelectorAll('button, input[type="submit"], input[type="image"]')];
    if (f.id) submitters.push(...document.querySelectorAll(`button[form="${CSS.escape(f.id)}"], input[form="${CSS.escape(f.id)}"]`));
    for (const b of submitters) {
      if (!b.hasAttribute('formaction')) continue;
      const a = b instanceof HTMLButtonElement ? BTN_ACTION.call(b) : INPUT_ACTION.call(b);
      if (!sameOrigin(a)) return false;
    }
    return true;
  }

  function place(btn, field) {
    const r = field.getBoundingClientRect();
    // inside the field: on short fields the icon shrinks, otherwise two nearby fields would have overlapping (hence "covered") icons
    const z = Math.max(14, Math.min(24, Math.round(r.height - 4)));
    btn.style.width = btn.style.height = `${z}px`;
    btn.classList.toggle('small', z < 20);
    const right = Math.min(r.right - 7, iconsInside(field, r) - 4);   // to the left of the page's own icons (show-password eye, other managers)
    btn.style.left = `${Math.round(right - z)}px`;
    btn.style.top = `${Math.round(r.top + (r.height - z) / 2)}px`;
    btn.style.display = r.bottom < 0 || r.top > innerHeight ? 'none' : 'grid';   // grid: with flex the logo was not centered (place-items)
  }

  // left edge of the leftmost small element drawn OVER the right end of the field (eye button, icons of other extensions);
  // Infinity if there is none. Looks along the middle line of the field, only at what is stacked above the field itself
  function iconsInside(field, r) {
    let min = Infinity;
    const y = r.top + r.height / 2, stop = Math.max(r.left + r.width / 2, r.right - 150);
    for (let x = r.right - 3; x > stop; x -= 6) {
      for (const e of document.elementsFromPoint(x, y)) {
        if (e === field) break;                                            // what follows is under the field
        if (e === host || e.contains(field) || field.contains(e)) continue;   // Lemora itself, containers of the field
        const b = e.getBoundingClientRect();
        if (b.width === 0 || b.width > 64 || b.height > r.height * 1.6) continue;   // not an icon (label, overlay, wrapper)
        min = Math.min(min, b.left);
        break;
      }
    }
    return min > r.left + 40 ? min : Infinity;   // something covering most of the field is not an icon to avoid
  }

  // the UI enters the page only when the user touches (or tabs into) a login field: before that, a page could
  // detect Lemora just by looking for <lemora-ui> (browser fingerprinting). Once touched, it stays for the whole page
  let armed = false, lastKey = -Infinity;   // "never": at page start no key has been pressed
  const loginFields = () => passwordFields().flatMap((pw) => [pw, userFieldFor(pw)].filter(Boolean));
  const arm = (t) => { if (!armed && t instanceof HTMLInputElement && loginFields().includes(t)) { armed = true; refresh(); } };
  document.addEventListener('pointerdown', (e) => { if (e.isTrusted) arm(e.target); }, true);
  // clicking the field's label (e.g. "Password" drawn over the field) does not touch the field: the browser sends the field a click
  document.addEventListener('click', (e) => { if (e.isTrusted) arm(e.target); }, true);
  document.addEventListener('keydown', (e) => { if (e.isTrusted) lastKey = performance.now(); }, true);
  document.addEventListener('focusin', (e) => { if (e.isTrusted && performance.now() - lastKey < 600) arm(e.target); }, true);

  function decorate() {
    if (!armed) return;
    if (!status || status.state === 'nohost' || status.state === 'notallowed') return;
    for (const pw of passwordFields()) {
      for (const f of [pw, userFieldFor(pw)].filter(Boolean)) {
        if (icons.has(f)) continue;
        attach();
        const b = document.createElement('button');
        b.className = 'icon'; b.type = 'button';
        b.append(icon('logo', 16));
        iconBadge(b);
        b.addEventListener('click', (e) => { e.preventDefault(); if (!genuine(e, 0, b)) return; toggleMenu(f); });
        root.appendChild(b);
        watch(b);
        icons.set(f, b);
        place(b, f);
      }
    }
  }

  // number of entries for this address; locked / to confirm / not paired only in the tooltip (no lock badge)
  function iconBadge(b) {
    b.querySelector('.bdg')?.remove();
    const st = status?.state, n = items?.length || 0;
    let tip = 'Lemora';
    if (st === 'locked' || st === 'unpaired' || st === 'confirm') {
      tip = tr(st === 'locked' ? 'lemora_locked' : st === 'confirm' ? 'page_tip_confirm' : 'page_tip_unpaired');
    } else if (st === 'ready' && n) {
      b.append(el('span', 'bdg', n > 9 ? '9+' : String(n)));
      tip = tr(n === 1 ? 'page_tip_count_one' : 'page_tip_count_many', n);
    } else if (st === 'closed') tip = tr('lemora_not_open');
    b.title = tip;
    b.setAttribute('aria-label', tip);
  }

  function reposition() { for (const [f, b] of icons) { if (!f.isConnected || !visible(f)) { b.remove(); icons.delete(f); } else place(b, f); } if (menu) positionMenu(); }

  // ---- entries menu
  let menuField = null;
  function closeMenu() { menu?.remove(); menu = null; menuField = null; }
  function positionMenu() {
    if (!menu || !menuField) return;
    const r = menuField.getBoundingClientRect();
    // beside the field when there is room (right first, then left): below it, it would cover the next fields and the form buttons
    const right = Math.min(360, innerWidth - r.right - 20), left = Math.min(360, r.left - 20);
    const side = right >= 312 ? right : left >= 312 ? left : 0;   // same minimum width as the menu below the field
    if (side) {
      menu.style.width = `${side}px`;
      menu.style.left = `${Math.round(right >= 312 ? r.right + 10 : r.left - 10 - side)}px`;
      const h = menu.offsetHeight;
      menu.style.top = `${Math.round(Math.max(8, Math.min(r.top, innerHeight - h - 8)))}px`;
      return;
    }
    const w = Math.min(400, Math.max(312, Math.round(r.width)), innerWidth - 16);
    menu.style.width = `${w}px`;
    menu.style.left = `${Math.max(8, Math.min(Math.round(r.left), innerWidth - w - 8))}px`;
    menu.style.top = `${Math.round(r.bottom + 4)}px`;
  }
  function el(tag, cls, text) { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
  function showMenu(field, build) {
    closeMenu();
    menuField = field;
    menu = el('div', 'dd menu');
    menu.setAttribute('role', 'menu');
    menu.setAttribute('aria-label', 'Lemora');
    build(menu);
    attach();
    root.appendChild(menu);
    positionMenu();
    watch(menu);
    menuShownAt = performance.now();
    menu.querySelector('button')?.focus();
  }

  // panel with title and text (states, warnings); returns the container to add buttons to
  function box(m, iconName, warn, title, text) {
    const b = el('div', 'box');
    const h = el('div', 'boxhead');
    const t = el('span', 'tile' + (warn ? ' warn' : '')); t.append(icon(iconName, 16));
    const tx = el('div', 'txt'); tx.append(el('span', 'mt', title));
    h.append(t, tx);
    b.append(h);
    if (text) b.append(el('span', 'ms', text));
    m.appendChild(b);
    return b;
  }
  function action(parent, label, fn, secondary) {
    let row = parent.querySelector(':scope > .acts');
    if (!row) { row = el('div', 'acts'); parent.appendChild(row); }
    const b = el('button', 'act' + (secondary ? ' sec' : ''), label);
    b.addEventListener('click', (e) => { if (genuine(e, menuShownAt, menu)) fn(); });
    row.appendChild(b);
    return b;
  }

  // random password: 16 characters, at least one of each kind, without look-alike characters (0/O, 1/l/I)
  function generate(n = 16) {
    const sets = ['ABCDEFGHJKLMNPQRSTUVWXYZ', 'abcdefghijkmnopqrstuvwxyz', '23456789', '!#$%&*+-=?@^_'];
    const pick = (cs) => { const lim = 256 - (256 % cs.length); for (;;) { const v = crypto.getRandomValues(new Uint8Array(1))[0]; if (v < lim) return cs[v % cs.length]; } };
    const out = sets.map(pick);
    while (out.length < n) out.push(pick(sets.join('')));
    for (let i = out.length - 1; i > 0; i--) { const j = crypto.getRandomValues(new Uint32Array(1))[0] % (i + 1); [out[i], out[j]] = [out[j], out[i]]; }
    return out.join('');
  }
  // new-password fields in the same form: this one, those marked new-password and the empty ones after it (the confirmation).
  // The current password in a "change password" form comes before and is not touched
  function newPasswordFields(field) {
    const form = FORM_OF.call(field);
    const list = [...(form || document).querySelectorAll('input[type="password"]')].filter((i) => visible(i) && !i.disabled && !i.readOnly);
    const at = list.indexOf(field);
    return list.filter((i, k) => i === field || /new-password/i.test(i.getAttribute('autocomplete') || '') || (k > at && !i.value));
  }
  function genSection(m, field) {
    const b = el('div', 'box');
    b.append(el('span', 'ms', tr('page_suggested')));
    let pw = generate();
    const row = el('div', 'genrow');
    const shownPw = el('span', 'genpw', pw);
    const again = el('button', 'ib'); again.type = 'button'; again.title = tr('page_generate_another'); again.setAttribute('aria-label', tr('page_generate_another_pw')); again.append(icon('refresh', 14));
    again.addEventListener('click', (e) => { if (genuine(e, menuShownAt, menu)) { pw = generate(); shownPw.textContent = pw; } });
    row.append(shownPw, again);
    b.append(row);
    m.appendChild(b);
    action(b, tr('page_use_password'), () => {
      for (const f of newPasswordFields(field)) setValue(f, pw);
      pw = ''; closeMenu();
      toast(tr('page_pw_inserted'));
    });
  }

  async function toggleMenu(field) {
    if (menu && menuField === field) { closeMenu(); return; }
    await load();
    for (const b of icons.values()) iconBadge(b);
    showMenu(field, (m) => {
      const st = status?.state;
      if (st === 'closed' || st === 'nohost') {
        const b = box(m, 'warn', true, tr(st === 'closed' ? 'lemora_not_open' : 'lemora_not_responding'), tr(st === 'closed' ? 'page_closed_text' : 'page_nohost_text'));
        if (st === 'closed') action(b, tr('open_lemora'), async () => { await send({ cmd: 'open' }); closeMenu(); });
      } else if (st === 'notallowed') {
        box(m, 'warn', true, tr('link_refused'), tr('link_refused_text'));
      } else if (st === 'unpaired') {
        const b = box(m, 'key', false, tr('link_extension'), tr('page_unpaired_text'));
        action(b, tr('link_to_lemora'), async () => { closeMenu(); await send({ cmd: 'pair' }); await load(); decorate(); for (const x of icons.values()) iconBadge(x); });
      } else if (st === 'confirm') {
        // without Windows Hello the password goes only in the extension's own window (never in a page): the menu points there
        if (!status?.hello) { box(m, 'lock', false, tr('unlock_db'), tr('page_unlock_text')); return; }
        const b = box(m, 'lock', false, tr('confirm_its_you'), tr('page_confirm_text'));
        action(b, tr('page_confirm_hello'), async () => { closeMenu(); const r = await send({ cmd: 'confirm' }); await load(); for (const x of icons.values()) iconBadge(x); if (r.ok) toggleMenu(field); });
      } else if (st === 'locked') {
        const b = box(m, 'lock', false, tr('lemora_locked'), tr('page_locked_text'));
        action(b, tr('unlock_in_lemora'), async () => { await send({ cmd: 'open' }); closeMenu(); });
      } else {
        const isNew = field.type === 'password' && /new-password/i.test(field.getAttribute('autocomplete') || '');
        if (isNew) genSection(m, field);
        if (!items.length) {
          if (!isNew) box(m, 'key', false, tr('page_no_pw_site'), tr('page_after_login'));
          return;
        }
        if (!formTargetOk(field)) { box(m, 'warn', true, tr('page_other_target'), tr('page_other_target_text')); return; }
        const head = el('div', 'mhead'); head.append(el('span', null, tr('page_sign_in_as')), el('span', 'mhost', location.host)); m.appendChild(head);
        if (location.protocol === 'http:') m.appendChild(el('div', 'alert', tr('page_http_warning')));
        for (const e of items) for (const u of e.users) {
          const b = el('button', 'item'); b.setAttribute('role', 'menuitem');
          const av = el('span', 'av'); av.append(icon('user', 16));
          const tx = el('span', 'txt'); tx.append(el('span', 'name', lemoraLabel(e)), el('span', 'sub', u));
          b.append(av, tx);
          b.addEventListener('click', (ev) => { if (genuine(ev, menuShownAt, menu)) fill(field, e.id, u); });
          m.appendChild(b);
        }
      }
    });
  }

  // short notice at the bottom of the page (does not receive clicks)
  let toastEl = null;
  function toast(text) {
    toastEl?.remove();
    attach();
    toastEl = el('div', 'toast'); toastEl.setAttribute('role', 'status');
    toastEl.append(el('span', 'ok', '✓'), el('span', null, text));
    root.appendChild(toastEl);
    const mine = toastEl;
    setTimeout(() => { mine.remove(); if (toastEl === mine) toastEl = null; }, 3000);
  }

  function setValue(input, value) {
    const proto = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value');
    input.focus();
    proto.set.call(input, value);   // works with React/Vue too, which watch the original setter
    input.dispatchEvent(new Event('input', { bubbles: true }));
    input.dispatchEvent(new Event('change', { bubbles: true }));
  }

  async function fill(field, id, user) {
    const pw = field.type === 'password' ? field : passwordFields().find((p) => userFieldFor(p) === field) || passwordFields()[0];
    if (!pw || !visible(pw) || !formTargetOk(pw)) { closeMenu(); return false; }
    const uf0 = userFieldFor(pw);
    // our UI (menu, icon inside the field) would cover the fields: remove it for the duration of the check, then put it back
    closeMenu();
    host.remove(); attached = false;
    const restore = ioV2 ? hideNow([...new Set([pw, uf0].filter(Boolean).flatMap(decorationsOver))]) : [];   // Set: a label can float over both fields
    let fieldsOk;
    try { fieldsOk = (await reallyVisible(pw)) && (!uf0 || (await reallyVisible(uf0))); } finally { restore.forEach((f) => f()); attach(); }
    if (!fieldsOk) {
      // fields covered by something else: whoever looks at the page cannot tell where the password would end up
      showMenu(field, (m) => box(m, 'warn', true, tr('page_covered'), tr('page_covered_text')));
      return false;
    }
    const r = await send({ cmd: 'secret', id, user });
    closeMenu();
    if (!r.ok) {
      // never silently: the user must know it was not filled
      showMenu(field, (m) => box(m, 'warn', true, tr('page_not_filled'), r.error === 'notallowed' ? tr('page_not_for_address') : (r.error || tr('page_not_responding_dot'))));
      return false;
    }
    if (!formTargetOk(pw)) return false;   // the form may have changed while Lemora was replying
    const uf = userFieldFor(pw);
    if (uf && r.user) setValue(uf, r.user);
    setValue(pw, r.password);
    const name = items?.find((e) => e.id === id)?.name;
    toast(name ? tr('page_filled_name', name) : tr('page_filled'));
    return true;
  }

  // ---- save prompt after submit
  // only pages reached by IP (devices, routers, panels): with a domain in the address bar Lemora does not offer to save.
  // The background checks it again: this only avoids sending the password there for nothing
  const ipPage = /^\d{1,3}(\.\d{1,3}){3}$/.test(location.hostname) || location.hostname.startsWith('[');
  let lastCapture = 0;
  function captureFrom(form) {
    if (!ipPage) return;
    const pw = [...(form || document).querySelectorAll('input[type="password"]')].find((p) => p.value && visible(p));
    if (!pw) return;
    const uf = userFieldFor(pw);
    const at = lastCapture = performance.now();
    send({ cmd: 'capture', user: uf ? uf.value.trim() : '', password: pw.value }).then((r) => {
      if (!r.ok || r.known) return;
      // many device pages log in without loading a new page: the question would never come. It comes when the password field is gone
      // (a failed login usually leaves it there, so a wrong password is not offered)
      for (const ms of [1500, 4000]) setTimeout(() => { if (at === lastCapture && (!document.contains(pw) || !visible(pw))) askSave(); }, ms);
    });
  }
  document.addEventListener('submit', (e) => { if (e.isTrusted) captureFrom(e.target); }, true);
  document.addEventListener('click', (e) => {
    if (!e.isTrusted) return;
    if (!e.composedPath().includes(host)) {
      const b = e.target.closest?.('button, input[type="submit"], [role="button"]');
      if (b && passwordFields().some((p) => p.value)) captureFrom(b.form || b.closest('form'));
      if (menu) closeMenu();
    }
  }, true);
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') closeMenu();
    if (e.isTrusted && e.key === 'Enter' && e.target?.type === 'password') captureFrom(e.target.form);
  }, true);

  // nobody touches the save prompt: it closes by itself and the captured password is dropped.
  // A click, a key or focus inside keeps it open for good; the pointer over it pauses the countdown (the person is reading)
  const SAVE_IDLE_MS = 15000;
  function autoClose(bar) {
    let t = 0;
    const arm = () => { clearTimeout(t); t = setTimeout(() => { if (bar.isConnected) { bar.remove(); send({ cmd: 'savePending', dismiss: true }); } }, SAVE_IDLE_MS); };
    const keep = () => { clearTimeout(t); for (const [ev, f] of evs) bar.removeEventListener(ev, f, true); };
    const evs = [['pointerdown', keep], ['keydown', keep], ['focusin', keep], ['pointerenter', () => clearTimeout(t)], ['pointerleave', arm]];
    for (const [ev, f] of evs) bar.addEventListener(ev, f, true);
    arm();
  }

  let saveBar = null, asking = false;
  async function askSave() {
    if (window.top !== window || !ipPage) return;   // a single prompt, in the main page; only on IP addresses
    if (asking || saveBar?.isConnected) return;     // already asked (page load and "login without a new page" can both call it)
    asking = true;
    try { await showSave(); } finally { asking = false; }
  }
  async function showSave() {
    // the previous page's capture may reach the background after this page has already started: retry for about 2 s
    let p = null;
    for (const wait of [0, 300, 700, 1200]) {
      if (wait) await new Promise((res) => setTimeout(res, wait));
      const r = await send({ cmd: 'pending' });
      p = r.ok && r.pending;
      if (p) break;
    }
    if (!p) return;
    attach();
    const bar = saveBar = el('div', 'dd bar'); bar.setAttribute('role', 'dialog'); bar.setAttribute('aria-label', tr('page_save_pw_q'));
    const shownAt = performance.now();
    const head = el('div', 'boxhead');
    const t = el('span', 'tile s'); t.append(icon('key', 15));
    const close = el('button', 'ib'); close.type = 'button'; close.setAttribute('aria-label', tr('page_close_no_save')); close.textContent = '✕';
    head.append(t, el('span', 'mt', tr('page_save_q')), close);
    bar.append(head);
    const dismiss = async (e) => { if (!genuine(e, 0, bar)) return; await send({ cmd: 'savePending', dismiss: true }); bar.remove(); };
    close.addEventListener('click', dismiss);
    if (p.confirm) {
      // the 20 minutes have passed: before, the typed password was silently dropped and nothing was asked.
      // First "it's you", then the real prompt (folders and existing entries are shown only after the confirmation)
      bar.append(el('p', 'ms', tr(p.hello ? 'page_confirm_save_hello' : 'page_confirm_save', p.user || tr('page_no_user'), p.host)));
      const row0 = el('div', 'acts');
      const no0 = el('button', 'act sec', tr('page_not_now'));
      const go = el('button', 'act', tr(p.hello ? 'page_confirm' : 'retry'));
      no0.addEventListener('click', dismiss);
      go.addEventListener('click', async (e) => {
        if (!genuine(e, shownAt, bar)) return;
        const r = p.hello ? await send({ cmd: 'confirm' }) : { ok: true };
        if (!r.ok) { if (!bar.querySelector('.err')) bar.insertBefore(el('p', 'err'), row0); bar.querySelector('.err').textContent = tr(p.hello ? 'page_confirm_failed' : 'page_confirm_from_icon'); return; }
        bar.remove(); await load(); askSave();
      });
      if (!p.hello) bar.append(el('p', 'ms', tr('page_click_icon_retry')));
      row0.append(no0, go); bar.append(row0);
      root.appendChild(bar); watch(bar); autoClose(bar);
      return;
    }
    const field = (label, node) => { const w = el('div'); w.append(el('p', 'fl', label), node); bar.append(w); return node; };
    const name = el('input'); name.value = p.host; name.maxLength = 200; name.setAttribute('aria-label', tr('title'));
    field(tr('title'), name);
    // the captured user name can be wrong (a page that fills it with its own text, a field found by guess): it can be corrected here
    const user = el('input'); user.value = p.user || ''; user.maxLength = 512; user.autocomplete = 'off'; user.spellcheck = false;
    user.placeholder = tr('page_no_user'); user.setAttribute('aria-label', tr('page_email_user'));
    field(tr('page_email_user'), user);
    let target;
    if (p.matches.length) {
      // default: new entry. Updating an existing entry (maybe shared with the team) is an explicit choice
      target = el('select'); target.setAttribute('aria-label', tr('page_update_entry'));
      const fresh = el('option', null, tr('new_entry')); fresh.value = ''; target.appendChild(fresh);
      // entries without a folder cannot be updated (Lemora rejects them): they are not offered
      for (const m of p.matches.filter((x) => x.folder)) { const o = el('option', null, lemoraLabel(m)); o.value = m.id; target.appendChild(o); }
      field(tr('page_save_as'), target);
    }
    const folder = el('select'); folder.setAttribute('aria-label', tr('folder'));
    // the folder is required: in Lemora there are no passwords that belong to "nobody"
    const none = el('option', null, tr('choose_folder')); none.value = ''; folder.appendChild(none);
    // the folder list (customer names) arrives only when the user opens the picker, not with the prompt
    let foldersLoaded = false;
    const loadFolders = async (e) => {
      if (foldersLoaded || !e.isTrusted) return;
      foldersLoaded = true;
      const r = await send({ cmd: 'pendingFolders' });
      for (const f of (r.ok ? r.folders : [])) { const o = el('option', null, f); o.value = f; folder.appendChild(o); }
    };
    folder.addEventListener('pointerdown', loadFolders);
    folder.addEventListener('keydown', loadFolders);
    const fw = el('div'); fw.append(el('p', 'fl', tr('page_folder_new')), folder); bar.append(fw);
    if (target) target.addEventListener('change', () => { fw.hidden = !!target.value; });
    const row = el('div', 'acts');
    const no = el('button', 'act sec', tr('page_not_now')); const yes = el('button', 'act', tr('save'));
    no.addEventListener('click', dismiss);
    yes.addEventListener('click', async (e) => {
      if (!genuine(e, shownAt, bar)) return;
      const err = () => bar.querySelector('.err') || bar.insertBefore(el('p', 'err'), row);
      const needFolder = !target?.value && !folder.value;
      folder.classList.toggle('invalid', needFolder);
      if (needFolder) { err().textContent = tr('page_choose_folder'); folder.focus(); return; }
      const u = user.value.trim();
      const res = await send({ cmd: 'savePending', updateId: target?.value || '', folder: folder.value, name: name.value.trim(), user: u });
      if (res.ok) { bar.remove(); await load(); for (const b of icons.values()) iconBadge(b); toast(tr('page_saved_user', u || tr('page_no_user'))); }
      else err().textContent = tr('not_saved', res.error || tr('error_generic'));
    });
    row.append(no, yes); bar.appendChild(row);
    root.appendChild(bar);
    watch(bar);
    autoClose(bar);
  }

  // ---- "Fill in" from the popup: only the frame with a password field answers (the background requests the password for this frame's address)
  api.runtime.onMessage.addListener((msg, _sender, reply) => {
    if (msg?.cmd !== 'fillFromPopup') return false;
    const pw = passwordFields()[0];
    if (!pw) return false;
    fill(pw, String(msg.id || ''), String(msg.user || '')).then((ok) => reply({ ok }), () => reply({ ok: false }));
    return true;
  });

  // ---- startup: right away if there are password fields, then on every page change
  let timer = null;
  const refresh = () => { clearTimeout(timer); timer = setTimeout(async () => { if (passwordFields().length) { if (!status) await load(); decorate(); } reposition(); }, 300); };
  new MutationObserver(refresh).observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['type', 'style', 'class'] });
  addEventListener('scroll', reposition, true);
  addEventListener('resize', reposition);
  refresh();
  askSave();
})();
