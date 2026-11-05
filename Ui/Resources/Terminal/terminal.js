// Terminale integrato di Lemora: xterm.js + blocchi comando (OSC 133) in stile A2, con collasso reale delle righe.
// Dipende da xterm 6.0.0 (il collasso usa il buffer interno: va riprovato a ogni cambio di versione di xterm).
(function () {
  'use strict';
  const post = m => window.chrome.webview.postMessage(m);
  window.addEventListener('error', e => post({ t: 'err', msg: e.message + ' @' + e.lineno + ':' + e.colno }));
  const ACC = '#60CDFF', OK = '#6CCB5F', ERR = '#FF9B94', RUN = '#9aa0a6', GREY = '#9aa0a6';
  const C = { hdr: '#303030', hdrFg: '#ffffff', border: '#6a6a6a', body: '#0b0b0b', input: '#1b1b1b', panel: '#303030' };   // colori (dal tema)
  const S = { autoCollapse: 0, showDuration: true, showButtons: true, shellIntegration: false,                                // interruttori (dalle impostazioni)
    specialKeysBar: false, specialKeys: [], customKeyLabel: '', customKeyText: '', bell: 'visual', copyOnSelect: false, allowServerClipboard: false, historyLimit: 500 };
  let ANSI = ['#0c0c0c', '#c50f1f', '#13a10e', '#c19c00', '#0037da', '#881798', '#3a96dd', '#cccccc', '#767676', '#e74856', '#16c60c', '#f9f1a5', '#3b78ff', '#b4009e', '#61d6d6', '#f2f2f2'];
  const GUT = 10;      // il riquadro esce di GUT px a sinistra e a destra del testo
  const HOFF = 52;     // il testo della testata parte a 52 px dal bordo sinistro del riquadro (prima ci sono pallino e freccia)
  const term = new Terminal({
    fontFamily: '"Cascadia Mono", Consolas, monospace', fontSize: 13.5, lineHeight: 1.15, cursorBlink: true,
    scrollback: 10000, allowProposedApi: true, allowTransparency: true,
    theme: { background: '#00000000', foreground: '#dcdcdc', cursor: ACC, cursorAccent: '#00283B', selectionBackground: '#60CDFF55' },
  });
  const fit = new FitAddon.FitAddon();
  const ser = typeof SerializeAddon !== 'undefined' ? new SerializeAddon.SerializeAddon() : null;
  const search = typeof SearchAddon !== 'undefined' ? new SearchAddon.SearchAddon() : null;
  term.loadAddon(fit);
  if (ser) term.loadAddon(ser);
  if (search) term.loadAddon(search);
  // link cliccabili (Ctrl+clic): il programma decide se chiedere conferma prima di aprirli
  if (typeof WebLinksAddon !== 'undefined') term.loadAddon(new WebLinksAddon.WebLinksAddon((e, uri) => { if (e.ctrlKey) post({ t: 'link', uri }); }));
  term.open(document.getElementById('t'));

  const $ = id => document.getElementById(id);
  const buf = () => term._core._bufferService.buffer;
  const b64 = s => Uint8Array.from(atob(s), c => c.charCodeAt(0));
  const blocks = [];   // un blocco = un comando: {head, start, end, exit, collapsed, saved, n, hh, t0, ms, cmd, bx, bOff, cwd, decos, tmp}
  let cur = null, cwd = '';
  const fmtMs = ms => ms < 1000 ? ms + ' ms' : ms < 60000 ? (ms / 1000).toFixed(1).replace('.', ',') + ' s' : Math.floor(ms / 60000) + ' min ' + Math.round(ms % 60000 / 1000) + ' s';
  const hhmm = t => new Date(t).toTimeString().slice(0, 5);
  const esc = s => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  const shortCwd = p => (p || '').replace(/^\/home\/[^\/]+/, '~').replace(/^\/root/, '~');

  // sfondo dei corpi dei riquadri: uno strato dietro il testo (xterm e' trasparente), sincronizzato a ogni disegno
  const bgl = document.createElement('div'); bgl.id = 'bgl';
  $('t').insertBefore(bgl, $('t').firstChild);

  // ---------- marcatori di shell integration: A = inizio prompt, B = fine prompt, C = inizio output, D;codice = fine comando
  function mk() { return term.registerMarker(0); }
  function mkAt(abs) { return term.registerMarker(abs - (buf().ybase + buf().y)); }
  function drop(b) { undeco(b); [b.head, b.start, b.end].forEach(m => m && m.dispose()); const i = blocks.indexOf(b); if (i >= 0) blocks.splice(i, 1); }

  term.parser.registerOscHandler(7, d => {            // cartella corrente: file://host/percorso
    const m = d.match(/^file:\/\/[^\/]*(\/.*)$/);
    if (m) { try { cwd = decodeURIComponent(m[1]); } catch (e) { cwd = m[1]; } updateStatus(); }
    return true;
  });
  term.parser.registerOscHandler(133, d => {
    const k = d[0];
    if (k === 'A') {
      if (cur && !cur.start) drop(cur);                 // prompt senza comando (Invio a vuoto, Ctrl+C): il riquadro non serve
      cur = { head: mk(), start: null, end: null, exit: null, collapsed: false, saved: null, n: 0, hh: 1, t0: Date.now(), ms: 0, cmd: '', bx: -1, bOff: 0, cwd, decos: [], tmp: [] };
      blocks.push(cur); decorate(cur);
    } else if (k === 'B' && cur) { const B = buf(); cur.bx = B.x; cur.bOff = B.ybase + B.y - cur.head.line; }
    else if (k === 'C' && cur && !cur.start) {
      cur.start = mk(); cur.t0 = Date.now(); cur.hh = Math.max(1, cur.start.line - cur.head.line);
      cur.cmd = readCmd(cur); decorate(cur);
    } else if (k === 'D' && cur && cur.start) {
      cur.end = mk(); cur.exit = parseInt(d.split(';')[1] || '0', 10); cur.ms = Date.now() - cur.t0;
      post({ t: 'block', exit: cur.exit, ms: cur.ms, count: blocks.length });
      decorate(cur); updateStatus(); renderHist(); drawSb();
      if (S.autoCollapse > 0 && cur.end.line - cur.start.line >= S.autoCollapse) { const b = cur; setTimeout(() => { if (!b.head.isDisposed) toggle(b, true); }, 0); }
    }
    return true;
  });
  // il blocco in corso cresce con l'output: il suo riquadro si ridisegna solo se cambia la riga del cursore (non a ogni disegno: sarebbe un ciclo)
  let rafPending = false, lastSig = '';
  const later = redo => { if (rafPending) return; rafPending = true; requestAnimationFrame(() => {
    rafPending = false;
    if (redo && cur && cur.exit === null) { const B = buf(), sig = (B.ybase + B.y) + ':' + (B.x > 0); if (sig !== lastSig) { lastSig = sig; decorate(cur); } }
    paintBg(); drawSb(); updateScrollUi();
  }); };
  term.onWriteParsed(() => later(true)); term.onScroll(() => later(false)); term.onRender(() => later(false));

  // testo del comando = righe dalla fine del prompt (B) fino alla riga prima dell'output
  function lineText(i) { const l = buf().lines.get(i); return l ? l.translateToString(true) : ''; }
  function readCmd(b) {
    if (b.bx < 0 || !b.start) return '';
    const first = b.head.line + b.bOff, last = b.start.line - 1; let s = '';
    for (let i = first; i <= last; i++) { const t = lineText(i); s += i === first ? t.slice(b.bx) : t; }
    return s.trim();
  }
  // testo dell'output (anche se collassato)
  function outText(b) {
    const ls = b.collapsed ? b.saved : (() => { const a = []; for (let i = b.start.line; i < b.end.line; i++) a.push(buf().lines.get(i)); return a; })();
    let s = '';
    ls.forEach((l, i) => { const t = l ? l.translateToString(true) : ''; s += (i > 0 && !l.isWrapped ? '\n' : '') + t; });
    return s.replace(/\s+$/, '');
  }

  // ---------- colori delle celle della testata (prompt colorato, comando in bianco grassetto)
  function xterm256(n) {
    if (n < 16) return ANSI[n];
    if (n >= 232) { const v = 8 + (n - 232) * 10; return 'rgb(' + v + ',' + v + ',' + v + ')'; }
    const i = n - 16, f = x => x === 0 ? 0 : 55 + x * 40;
    return 'rgb(' + f(Math.floor(i / 36)) + ',' + f(Math.floor(i / 6) % 6) + ',' + f(i % 6) + ')';
  }
  function headerHtml(b) {
    const A = term.buffer.active, out = [];
    for (let r = 0; r < b.hh; r++) {
      const line = A.getLine(b.head.line + r); if (!line) { out.push(''); continue; }
      const text = line.translateToString(true), n = text.length;
      let html = '', run = '', runCss = '';
      const flush = () => { if (run) { html += '<span style="' + runCss + '">' + esc(run) + '</span>'; run = ''; } };
      for (let x = 0; x < n; x++) {
        const cell = line.getCell(x); if (!cell) break;
        const ch = cell.getChars() || ' ';
        const isCmd = r > b.bOff || (r === b.bOff && b.bx >= 0 && x >= b.bx);
        let css;
        if (isCmd) css = 'color:' + C.hdrFg + ';font-weight:700';
        else {
          const col = cell.isFgDefault() ? C.hdrFg : cell.isFgRGB() ? '#' + cell.getFgColor().toString(16).padStart(6, '0') : xterm256(cell.getFgColor());
          css = 'color:' + col + (cell.isBold() ? ';font-weight:700' : '');
        }
        if (css !== runCss) { flush(); runCss = css; }
        run += ch;
      }
      flush(); out.push(html);
    }
    return out.join('\n');
  }

  // ---------- collasso: toglie davvero le righe dell'output dal buffer e le sostituisce con una riga-riassunto
  function paintPh(line, txt) {
    const c = buf().getNullCell();
    for (let x = 0; x < term.cols; x++) {
      const ch = x < txt.length ? txt.codePointAt(x) : 32;
      c.content = ch | (1 << 22); c.fg = 0x01000000 | 0x60CDFF; c.bg = 0;
      line.setCell(x, c);
    }
  }
  function doCollapse(b) {
    if (b.collapsed || !b.start || !b.end || term.buffer.active.type !== 'normal') return false;
    const lines = buf().lines, s = b.start.line, n = b.end.line - s;
    if (n < 2) return false;
    const saved = []; for (let i = s; i < s + n; i++) saved.push(lines.get(i));
    const ph = saved[0].clone();
    paintPh(ph, '▸ ' + n + ' righe nascoste · ' + fmtMs(b.ms) + ' · Espandi');
    const B = buf(), c0 = B.ybase + B.y, yd0 = B.ydisp, atBottom = B.ydisp === B.ybase;
    lines.splice(s, n, ph);
    normalize(c0 - (n - 1), atBottom, s < yd0 ? yd0 - (n - 1) : yd0);
    b.saved = saved; b.n = n; b.collapsed = true;
    return true;
  }
  function doExpand(b) {
    if (!b.collapsed) return false;
    const lines = buf().lines, s = b.head.line + b.hh;
    const B = buf(), c0 = B.ybase + B.y, yd0 = B.ydisp, atBottom = B.ydisp === B.ybase;
    lines.splice(s, 1, ...b.saved);
    normalize(c0 + (b.n - 1), atBottom, s < yd0 ? yd0 + (b.n - 1) : yd0);
    b.collapsed = false; b.saved = null;
    return true;
  }
  // il buffer deve avere sempre (righe dello schermo + scorrimento) righe, con il cursore al suo posto:
  // dopo aver tolto o rimesso righe si ricalcolano base, cursore e righe vuote in fondo, come in un terminale appena avviato
  function normalize(c, atBottom, ydisp) {
    const B = buf(), L = B.lines, need = Math.max(c + 1, term.rows);
    while (L.length > need) L.pop();
    while (L.length < need) L.push(B.getBlankLine(term._core._inputHandler._curAttrData));
    B.ybase = need - term.rows; B.y = c - B.ybase;
    B.ydisp = atBottom ? B.ybase : Math.max(0, Math.min(B.ybase, ydisp));
  }
  // i marker interni si sfasano con splice: dopo ogni cambio li ricreo dalle posizioni calcolate prima
  function toggle(b, collapse) {
    const live = blocks.filter(x => !x.head.isDisposed);
    const i = live.indexOf(b);
    const truth = live.map(x => ({ h: x.head.line, s: x.collapsed ? -1 : (x.start ? x.start.line : -1), e: x.end ? x.end.line : -1 }));
    const was = b.collapsed;
    if (!(collapse ? doCollapse(b) : doExpand(b))) return false;
    const d = was ? (b.n - 1) : -(b.n - 1);
    live.forEach((x, j) => {
      if (j > i) { truth[j].h += d; if (truth[j].s >= 0) truth[j].s += d; if (truth[j].e >= 0) truth[j].e += d; }
      else if (j === i) { truth[j].e += d; truth[j].s = x.collapsed ? -1 : truth[j].h + x.hh; }
    });
    live.forEach((x, j) => {
      const t = truth[j];
      undeco(x);
      x.head.dispose(); x.head = mkAt(t.h);
      if (x.start) x.start.dispose();
      x.start = (t.s >= 0 && !x.collapsed) ? mkAt(t.s) : null;
      if (x.end) { x.end.dispose(); x.end = t.e >= 0 ? mkAt(t.e) : null; }
    });
    blocks.length = 0; live.forEach(x => blocks.push(x));
    term._core._renderService.clear(); term.refresh(0, term.rows - 1);
    blocks.forEach(decorate); updateStatus(); drawSb();
    return true;
  }

  // ---------- grafica A2: riquadro per comando, testata scura, barra colorata a sinistra, comandi a destra della testata
  function undeco(b) { b.decos.forEach(d => d.dispose()); b.tmp.forEach(m => m.dispose()); b.decos = []; b.tmp = []; }
  const ICON_COPY = '<svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4"><rect x="5.5" y="5.5" width="8" height="8" rx="1.5"/><path d="M10.5 3.5v-1a1 1 0 0 0-1-1h-6a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1h1"/></svg>';
  const ICON_SAVE = '<svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"><path d="M8 2v8M4.8 7 8 10.2 11.2 7M2.5 13.5h11"/></svg>';
  const ICON_DOWN = '<svg width="12" height="12" viewBox="0 0 16 16" fill="currentColor"><path d="M3 5.5h10L8 11z"/></svg>';
  const ICON_RIGHT = '<svg width="12" height="12" viewBox="0 0 16 16" fill="currentColor"><path d="M5.5 3v10L11 8z"/></svg>';

  // righe del corpo di un blocco (1 se collassato)
  function bodyRows(b) {
    if (b.collapsed) return 1;
    if (!b.start) return 0;
    const B = buf(), cursorAbs = B.ybase + B.y;
    const endLine = b.end && !b.end.isDisposed ? b.end.line : (B.x > 0 ? cursorAbs + 1 : cursorAbs);
    return Math.max(0, endLine - b.start.line);
  }

  function decorate(b) {
    undeco(b);
    if (b.head.isDisposed) return;
    const B = buf(), H = b.head.line, cursorAbs = B.ybase + B.y;
    const deco = o => { const d = term.registerDecoration(o); if (d) b.decos.push(d); return d; };

    if (!b.start && !b.collapsed) { paintBg(); return; }   // riga di scrittura: riquadro con bordo accent, disegnato da paintBg
    const hh = b.hh, done = b.exit !== null, body = bodyRows(b);
    const total = hh + body, color = !done ? RUN : (b.exit === 0 ? OK : ERR);

    // testata: fascia scura (piu' larga del testo) che ridisegna prompt colorato e comando in bianco grassetto
    const hd = deco({ marker: b.head, x: 0, width: term.cols, height: hh });
    hd && hd.onRender(el => {
      const rh = parseFloat(el.style.height) / hh || 18;
      // la fascia sta dentro la barra colorata (4 px) e il bordo (1 px) disegnati da paintBg, che restano visibili
      el.style.marginLeft = '-' + (GUT - 4) + 'px'; el.style.boxSizing = 'content-box'; el.style.padding = '0 ' + (GUT - 1) + 'px 0 ' + (GUT - 4) + 'px';
      el.style.background = C.hdr; el.style.borderRadius = '3px 3px 0 0'; el.style.pointerEvents = 'none'; el.style.overflow = 'hidden'; el.style.clipPath = 'inset(1px 0 0 0)';
      el.innerHTML = '';
      const t = document.createElement('div');
      t.style.cssText = 'position:absolute;left:' + (HOFF - 4) + 'px;top:0;white-space:pre;font-family:' + term.options.fontFamily + ';font-size:' + term.options.fontSize + 'px;line-height:' + rh + 'px;color:' + C.hdrFg;
      t.innerHTML = headerHtml(b);
      el.appendChild(t);
    });
    // a sinistra della testata pallino d'esito (menu) e freccia (collassa/espandi); a destra copia, salva, durata e ora
    const ctl = deco({ marker: b.head, x: 0, width: term.cols, height: 1 });
    ctl && ctl.onRender(el => {
      el.style.pointerEvents = 'none'; el.style.display = 'flex'; el.style.alignItems = 'center'; el.style.justifyContent = 'flex-end';
      el.innerHTML = '';
      const mkBtn = (html, tip, fn, css) => { const x = document.createElement('span'); x.innerHTML = html; x.title = tip; x.style.cssText = 'cursor:pointer;display:inline-flex;align-items:center;justify-content:center;padding:2px 3px;border-radius:3px;color:' + GREY + ';' + (css || ''); x.onmouseenter = () => x.style.background = '#ffffff22'; x.onmouseleave = () => x.style.background = ''; x.onclick = ev => { ev.stopPropagation(); fn(ev); }; return x; };
      const left = document.createElement('div');
      left.style.cssText = 'position:absolute;left:-' + (GUT - 4) + 'px;top:0;bottom:0;display:flex;align-items:center;gap:6px;pointer-events:auto';
      const dot = mkBtn('<span style="width:8px;height:8px;border-radius:50%;background:' + color + '"></span>', 'Menu del comando', ev => openMenu(b, ev.clientX, ev.clientY), 'padding:4px');
      left.appendChild(dot);
      const can = b.collapsed || body >= 3;
      left.appendChild(mkBtn(b.collapsed ? ICON_RIGHT : ICON_DOWN, can ? (b.collapsed ? 'Espandi' : 'Collassa') : '', () => { if (can) toggle(b, !b.collapsed); }, can ? '' : 'opacity:.35;cursor:default'));
      el.appendChild(left);
      if (done) {
        const grp = document.createElement('div');
        grp.style.cssText = 'pointer-events:auto;display:flex;align-items:center;gap:2px;padding:0 6px 0 10px;height:100%;background:' + C.hdr + ';font:12px "Cascadia Mono",Consolas,monospace;margin-right:-' + (GUT - 6) + 'px;border-radius:0 3px 0 0';
        if (S.showButtons) {
          grp.appendChild(mkBtn(ICON_COPY, 'Copia output', () => act(b, 'copyOut')));
          grp.appendChild(mkBtn(ICON_SAVE, 'Salva output come file…', () => act(b, 'save')));
        }
        const parts = [];
        if (b.exit !== 0) parts.push('exit ' + b.exit);
        if (S.showDuration) { parts.push(fmtMs(b.ms)); parts.push(hhmm(b.t0)); }
        if (parts.length) {
          const t = document.createElement('span'); t.textContent = parts.join(' · ');
          t.style.cssText = 'margin-left:8px;white-space:pre;color:' + (b.exit === 0 ? GREY : ERR);
          grp.appendChild(t);
        }
        el.appendChild(grp);
      }
    });
    if (b.collapsed) {                                 // la riga-riassunto si espande con un clic
      const m = mkAt(H + hh); b.tmp.push(m);
      const ph = deco({ marker: m, x: 0, width: term.cols, height: 1 });
      ph && ph.onRender(el => { el.style.pointerEvents = 'auto'; el.style.cursor = 'pointer'; el.title = 'Espandi'; el.onclick = () => toggle(b, false); });
    }
    paintBg();
  }
  // riquadri e sfondi dietro al testo: xterm disegna una decorazione solo se la sua riga di partenza e' visibile,
  // mentre il riquadro di un comando lungo deve restare anche quando la testata e' uscita dallo schermo.
  // Il riquadro esce di GUT pixel a sinistra e a destra del testo (margine di lettura).
  function paintBg() {
    const scr = document.querySelector('.xterm-screen'); if (!scr) return;
    const tb = $('t').getBoundingClientRect(), sr = scr.getBoundingClientRect();
    const rh = sr.height / term.rows, y0 = term.buffer.active.viewportY, left = sr.left - tb.left - GUT, w = sr.width + 2 * GUT, top0 = sr.top - tb.top;
    let html = '';
    const rect = (first, rows, css, inner) => {
      if (rows <= 0) return;
      const top = (first - y0) * rh; if (top > sr.height || top + rows * rh < 0) return;
      html += '<div style="position:absolute;box-sizing:border-box;left:' + left + 'px;width:' + w + 'px;top:' + (top0 + top) + 'px;height:' + (rows * rh) + 'px;' + css + '">' + (inner || '') + '</div>';
    };
    const B = buf(), cursorAbs = B.ybase + B.y;
    for (const b of blocks) {
      if (b.head.isDisposed) continue;
      if (!b.start && !b.collapsed) {                                   // riga di scrittura: sfondo e bordo accent da 2 px
        rect(b.head.line, Math.max(1, cursorAbs - b.head.line + 1), 'background:' + C.input + ';border-radius:4px');
        continue;
      }
      const body = bodyRows(b), done = b.exit !== null, color = !done ? RUN : (b.exit === 0 ? OK : ERR);
      rect(b.head.line + b.hh, body, 'background:' + C.body + ';border-radius:0 0 4px 4px');
      rect(b.head.line, b.hh + body, 'border:1px solid ' + C.border + ';border-left:4px solid ' + color + ';border-radius:4px',
        '<div style="position:absolute;left:0;right:0;top:' + (b.hh * rh - 1) + 'px;height:1px;background:' + C.border + '"></div>');
    }
    bgl.innerHTML = html;
  }

  // ---------- menu del comando
  const menu = document.createElement('div');
  menu.style.cssText = 'position:fixed;z-index:1000;display:none;min-width:230px;background:#262626;border:1px solid #444;border-radius:6px;padding:4px;box-shadow:0 8px 24px #0009;font:13px "Segoe UI",sans-serif;color:#e6e6e6';
  document.body.appendChild(menu);
  const closeMenu = () => { menu.style.display = 'none'; };
  document.addEventListener('mousedown', e => { if (!menu.contains(e.target)) closeMenu(); }, true);
  document.addEventListener('keydown', e => { if (e.key === 'Escape') closeMenu(); }, true);
  function openMenu(b, x, y) {
    const hasOut = b.collapsed || (b.end && b.end.line > b.start.line);
    const items = [
      ['Copia comando', () => act(b, 'copyCmd'), !!b.cmd],
      ['Copia output', () => act(b, 'copyOut'), hasOut],
      ['Salva output come file…', () => act(b, 'save'), hasOut],
      ['Copia output come HTML', () => act(b, 'copyHtml'), hasOut && !!ser],
      null,
      ['Riesegui', () => act(b, 'rerun'), !!b.cmd],
      [b.collapsed ? 'Espandi output' : 'Collassa output', () => toggle(b, !b.collapsed), b.collapsed || (b.end && b.end.line - b.start.line >= 2)],
      ['Seleziona output', () => act(b, 'select'), hasOut],
    ];
    menu.innerHTML = '';
    items.forEach(it => {
      const r = document.createElement('div');
      if (!it) { r.style.cssText = 'height:1px;background:#3a3a3a;margin:4px 2px'; menu.appendChild(r); return; }
      r.textContent = it[0];
      r.style.cssText = 'padding:6px 10px;border-radius:4px;' + (it[2] ? 'cursor:pointer' : 'opacity:.4');
      if (it[2]) { r.onmouseenter = () => r.style.background = '#60CDFF26'; r.onmouseleave = () => r.style.background = ''; r.onclick = () => { closeMenu(); it[1](); }; }
      menu.appendChild(r);
    });
    menu.style.display = 'block';
    menu.style.left = Math.min(x, innerWidth - menu.offsetWidth - 6) + 'px';
    menu.style.top = Math.min(y, innerHeight - menu.offsetHeight - 6) + 'px';
  }
  function safeName(s) { return (s || 'comando').replace(/[\\/:*?"<>|\x00-\x1f]/g, '').replace(/\s+/g, '-').slice(0, 40) || 'comando'; }
  function act(b, what) {
    if (what === 'copyCmd') post({ t: 'copy', text: b.cmd });
    else if (what === 'copyOut') post({ t: 'copy', text: outText(b) });
    else if (what === 'save') post({ t: 'save', name: 'output-' + safeName(b.cmd) + '.txt', text: outText(b) });
    else if (what === 'rerun') post({ t: 'in', d: '\x15' + b.cmd + '\r' });
    else if (what === 'select' || what === 'copyHtml') {
      const was = b.collapsed; if (was) toggle(b, false);
      term.selectLines(b.start.line, Math.max(b.start.line, b.end.line - 1));
      if (what === 'copyHtml') {
        const html = ser.serializeAsHTML({ onlySelection: true, includeGlobalBackground: true });
        post({ t: 'copy', text: outText(b), html });
        term.clearSelection();
      }
      if (was) toggle(b, true);
    }
  }

  // ---------- ridimensionamento: con righe collassate si espande tutto, si adatta, si ricollassa (il reflow rovinerebbe le righe salvate)
  function refit() {
    const was = blocks.filter(b => b.collapsed);
    was.forEach(b => toggle(b, false));
    try { fit.fit(); } catch (e) { }
    was.forEach(b => toggle(b, true));
    blocks.forEach(decorate); drawSb();
    post({ t: 'size', cols: term.cols, rows: term.rows });
  }
  let rt = 0;
  new ResizeObserver(() => { clearTimeout(rt); rt = setTimeout(refit, 80); }).observe($('t'));

  // ---------- barra di scorrimento a destra con un segno per comando (verde riuscito, rosso errore): clic = vai a quel comando
  function drawSb() {
    const sb = $('sb'); if (!sb || sb.style.display === 'none') return;
    const A = term.buffer.active, total = Math.max(1, A.length), h = sb.clientHeight, y0 = A.viewportY;
    let html = '';
    const thumbH = Math.max(24, h * term.rows / total), thumbTop = total > term.rows ? (h - thumbH) * y0 / (total - term.rows) : 0;
    if (total > term.rows) html += '<div id="sbt" style="top:' + thumbTop + 'px;height:' + thumbH + 'px"></div>';
    blocks.forEach((b, i) => {
      if (b.head.isDisposed || b.exit === null) return;
      html += '<div class="mk" data-i="' + i + '" title="' + esc((b.cmd || '') + ' · exit ' + b.exit) + '" style="top:' + Math.min(h - 3, b.head.line / total * h) + 'px;background:' + (b.exit === 0 ? OK : ERR) + '"></div>';
    });
    if (cur && cur.exit === null && !cur.head.isDisposed) html += '<div class="mk" style="top:' + Math.min(h - 4, cur.head.line / total * h) + 'px;height:4px;background:' + ACC + '"></div>';
    sb.innerHTML = html;
  }
  $('sb').addEventListener('mousedown', e => {
    const mkEl = e.target.closest && e.target.closest('.mk');
    if (mkEl && mkEl.dataset.i !== undefined) { const b = blocks[+mkEl.dataset.i]; if (b) term.scrollToLine(Math.max(0, b.head.line - 1)); e.preventDefault(); return; }
    const sb = $('sb'), jump = ev => {
      const r = sb.getBoundingClientRect(), f = Math.min(1, Math.max(0, (ev.clientY - r.top) / r.height)), A = term.buffer.active;
      term.scrollToLine(Math.round(f * Math.max(0, A.length - term.rows)));
    };
    jump(e);
    const mv = ev => jump(ev), up = () => { document.removeEventListener('mousemove', mv); document.removeEventListener('mouseup', up); };
    document.addEventListener('mousemove', mv); document.addEventListener('mouseup', up);
    e.preventDefault();
  });

  // ---------- barra di stato in fondo e pannello Cronologia a destra
  const doneBlocks = () => blocks.filter(b => b.exit !== null && b.cmd).slice(-S.historyLimit);
  function updateStatus() {
    $('st-cwd').textContent = shortCwd(cwd) || '';
    $('st-size').textContent = term.cols + ' × ' + term.rows;
    const n = doneBlocks().length;
    $('st-count').textContent = n + (n === 1 ? ' comando' : ' comandi');
    const si = blocks.some(b => b.start);
    $('st-si').innerHTML = '<i style="background:' + (si ? ACC : '#6a6a6a') + '"></i>' + (si ? 'Shell integration attiva' : (S.shellIntegration ? 'Shell integration: in attesa' : 'Shell integration non attiva'));
    $('st-si').title = si ? 'I comandi sono riconosciuti: riquadri, esito, collasso e cronologia' : 'Senza shell integration il terminale funziona ma non riconosce i singoli comandi. Si attiva in Impostazioni terminale › Blocchi comando';
    const all = blocks.filter(b => b.exit !== null && (b.collapsed || (b.end && b.start && b.end.line - b.start.line >= 2)));
    $('st-collapse').textContent = all.length && all.every(b => b.collapsed) ? 'Espandi tutti' : 'Collassa tutti';
    $('st-collapse').style.display = all.length ? '' : 'none';
  }
  function renderHist() {
    if ($('hist').style.display === 'none') return;
    const q = $('hq').value.trim().toLowerCase(), list = $('hl'); list.innerHTML = '';
    const items = doneBlocks().filter(b => !q || b.cmd.toLowerCase().includes(q)).reverse();
    items.forEach(b => {
      const r = document.createElement('div'); r.className = 'hi'; r.title = b.cmd + '\nClic: inserisce · doppio clic: invia';
      const sub = [shortCwd(b.cwd), b.exit !== 0 ? 'exit ' + b.exit : ''].filter(Boolean).join('  ·  ');
      r.innerHTML = '<span class="dot" style="background:' + (b.exit === 0 ? OK : ERR) + '"></span><div class="cc"><div class="ct"></div><div class="cs"></div></div><span class="tm">' + (Date.now() - b.t0 < 86400000 ? hhmm(b.t0) : 'ieri') + '</span>';
      r.querySelector('.ct').textContent = b.cmd; r.querySelector('.cs').textContent = sub;
      let timer = 0;
      r.onclick = () => { clearTimeout(timer); timer = setTimeout(() => { post({ t: 'in', d: '\x15' + b.cmd }); term.focus(); }, 220); };
      r.ondblclick = () => { clearTimeout(timer); post({ t: 'in', d: '\x15' + b.cmd + '\r' }); term.focus(); };
      list.appendChild(r);
    });
    if (!items.length) { const e = document.createElement('div'); e.className = 'he'; e.textContent = q ? 'Nessun comando trovato' : 'Nessun comando eseguito'; list.appendChild(e); }
  }
  function showHist(on) { $('hist').style.display = on ? 'flex' : 'none'; renderHist(); post({ t: 'histState', on }); }
  $('hx').onclick = () => showHist(false);
  $('hq').oninput = renderHist;
  $('st-collapse').onclick = () => {
    const all = blocks.filter(b => b.exit !== null && (b.collapsed || (b.end && b.start && b.end.line - b.start.line >= 2)));
    const expand = all.length && all.every(b => b.collapsed);
    all.slice().reverse().forEach(b => toggle(b, !expand));
  };
  document.addEventListener('keydown', e => { if (e.ctrlKey && e.shiftKey && e.key === 'H') { e.preventDefault(); showHist($('hist').style.display === 'none'); } });
  term.onResize(updateStatus);

  // ---------- impostazioni (tema, carattere, cursore...) dal programma
  function applySettings(p) {
    const t = p.theme, a = t.Ansi; ANSI = a;
    C.hdr = t.Header; C.hdrFg = t.HeaderText; C.border = t.Border; C.body = t.Body; C.input = t.Input; C.panel = t.Header;
    S.autoCollapse = p.autoCollapse; S.showDuration = p.showDuration; S.showButtons = p.showButtons; S.shellIntegration = p.shellIntegration;
    S.specialKeysBar = p.specialKeysBar; S.specialKeys = p.specialKeys || []; S.customKeyLabel = p.customKeyLabel; S.customKeyText = p.customKeyText;
    S.bell = p.bell; S.copyOnSelect = p.copyOnSelect; S.allowServerClipboard = p.allowServerClipboard; S.historyLimit = p.historyLimit || 500;
    buildKeys();
    const o = term.options;
    o.fontFamily = p.fontFamily; o.fontSize = p.fontSize; o.lineHeight = p.lineHeight; o.cursorStyle = p.cursorStyle; o.cursorBlink = p.cursorBlink; o.scrollback = p.scrollback;
    o.theme = {
      background: '#00000000', foreground: t.Foreground, cursor: t.Cursor, cursorAccent: t.Background, selectionBackground: t.Selection,
      black: a[0], red: a[1], green: a[2], yellow: a[3], blue: a[4], magenta: a[5], cyan: a[6], white: a[7],
      brightBlack: a[8], brightRed: a[9], brightGreen: a[10], brightYellow: a[11], brightBlue: a[12], brightMagenta: a[13], brightCyan: a[14], brightWhite: a[15],
    };
    const r = document.documentElement.style;
    r.setProperty('--bg', t.Background); r.setProperty('--fg', t.Foreground); r.setProperty('--hdr', t.Header); r.setProperty('--bd', t.Border);
    setTimeout(() => { refit(); updateStatus(); }, 30);
  }

  // ---------- ricerca nell'output (Ctrl+F): n di m, precedente/successivo, maiuscole, espressione regolare
  let fcs = false, frx = false;
  const fdec = { matchBackground: '#1a4a5c', matchBorder: '#60CDFF', matchOverviewRuler: '#60CDFF', activeMatchBackground: '#2f7f9f', activeMatchBorder: '#ffffff', activeMatchColorOverviewRuler: '#ffffff' };
  function findOpts(incremental) { return { caseSensitive: fcs, regex: frx, wholeWord: false, incremental, decorations: fdec }; }
  function doFind(dir, incremental) {
    if (!search) return;
    const q = $('fq').value;
    if (!q) { search.clearDecorations(); $('fn').textContent = ''; return; }
    try { dir < 0 ? search.findPrevious(q, findOpts(false)) : search.findNext(q, findOpts(!!incremental)); } catch (e) { $('fn').textContent = 'Non valida'; }
  }
  if (search) search.onDidChangeResults(r => { $('fn').textContent = !$('fq').value ? '' : r && r.resultCount > 0 ? (r.resultIndex + 1) + ' di ' + r.resultCount : 'Nessuno'; });
  function showFind(on) {
    $('find').style.display = on ? 'flex' : 'none';
    if (on) { $('fq').focus(); $('fq').select(); if ($('fq').value) doFind(1, true); }
    else { if (search) search.clearDecorations(); term.focus(); }
  }
  $('fq').addEventListener('input', () => doFind(1, true));
  $('fq').addEventListener('keydown', e => {
    if (e.key === 'Enter') { e.preventDefault(); doFind(e.shiftKey ? -1 : 1, false); }
    else if (e.key === 'Escape') { e.preventDefault(); showFind(false); }
  });
  $('fnext').onclick = () => doFind(1, false); $('fprev').onclick = () => doFind(-1, false);
  $('fcs').onclick = () => { fcs = !fcs; $('fcs').classList.toggle('on', fcs); doFind(1, true); };
  $('frx').onclick = () => { frx = !frx; $('frx').classList.toggle('on', frx); doFind(1, true); };
  $('fx').onclick = () => showFind(false);

  // ---------- "Vai in fondo" e riga del comando fissa in cima (sticky scroll)
  const sticky = document.createElement('div'); sticky.id = 'sticky'; $('t').appendChild(sticky);
  function updateScrollUi() {
    const A = term.buffer.active, away = A.viewportY < A.baseY - 1;
    $('tobottom').style.display = away ? 'block' : 'none';
    const scr = document.querySelector('.xterm-screen'); if (!scr) return;
    const tb = $('t').getBoundingClientRect(), sr = scr.getBoundingClientRect(), rh = sr.height / term.rows, y0 = A.viewportY;
    let st = null;
    for (const b of blocks) {                                       // comando la cui testata e' uscita sopra lo schermo ma il cui output si vede ancora
      if (b.head.isDisposed || (!b.start && !b.collapsed)) continue;
      const last = b.head.line + b.hh + bodyRows(b);
      if (b.head.line < y0 && last > y0 + 1) { st = b; break; }
    }
    if (!st) { sticky.style.display = 'none'; return; }
    const color = st.exit === null ? RUN : st.exit === 0 ? OK : ERR;
    sticky.style.cssText = 'display:block;left:' + (sr.left - tb.left - GUT) + 'px;top:' + (sr.top - tb.top) + 'px;width:' + (sr.width + 2 * GUT) + 'px;height:' + rh + 'px;line-height:' + rh + 'px;background:' + C.hdr + ';border-left-color:' + color + ';border-color:' + C.border + ';border-left:4px solid ' + color + ';padding-left:' + (HOFF - 4) + 'px;font-family:' + term.options.fontFamily + ';font-size:' + term.options.fontSize + 'px;color:' + C.hdrFg;
    sticky.innerHTML = headerHtml(st).split('\n').pop();
    sticky.onclick = () => term.scrollToLine(Math.max(0, st.head.line - 1));
  }
  $('tobottom').onclick = () => { term.scrollToBottom(); term.focus(); };

  // ---------- tempo di risposta: dalla pressione di un tasto all'arrivo della risposta (mediana delle ultime misure)
  let keyAt = 0; const lats = [];
  function noteKey() { if (!keyAt) keyAt = performance.now(); }
  function noteEcho() {
    if (!keyAt) return;
    lats.push(performance.now() - keyAt); keyAt = 0; if (lats.length > 9) lats.shift();
    const s = lats.slice().sort((a, b) => a - b), m = s[Math.floor(s.length / 2)];
    $('st-lat').textContent = Math.max(1, Math.round(m)) + ' ms';
  }

  // ---------- barra dei tasti speciali (si accende dalle impostazioni)
  const KEYSEQ = {
    ctrlc: ['Ctrl C', '\x03'], ctrld: ['Ctrl D', '\x04'], ctrlz: ['Ctrl Z', '\x1a'], esc: ['Esc', '\x1b'], tab: ['Tab', '\t'],
    left: ['←', '\x1b[D'], up: ['↑', '\x1b[A'], down: ['↓', '\x1b[B'], right: ['→', '\x1b[C'],
  };
  const FSEQ = ['\x1bOP', '\x1bOQ', '\x1bOR', '\x1bOS', '\x1b[15~', '\x1b[17~', '\x1b[18~', '\x1b[19~', '\x1b[20~', '\x1b[21~', '\x1b[23~', '\x1b[24~'];
  function buildKeys() {
    const bar = $('keys'); bar.innerHTML = '';
    const on = S.specialKeysBar && !S.preview;
    bar.style.display = on ? 'flex' : 'none';
    if (!on) return;
    const add = (label, seq) => { const b = document.createElement('button'); b.textContent = label; b.onclick = () => { post({ t: 'in', d: seq }); term.focus(); }; bar.appendChild(b); };
    for (const k of S.specialKeys) {
      if (KEYSEQ[k]) add(KEYSEQ[k][0], KEYSEQ[k][1]);
      else if (k === 'arrows') ['left', 'up', 'down', 'right'].forEach(a => add(KEYSEQ[a][0], KEYSEQ[a][1]));
      else if (k === 'fkeys') FSEQ.forEach((s, i) => add('F' + (i + 1), s));
      else if (k === 'custom' && S.customKeyText) add(S.customKeyLabel || 'Tasto', S.customKeyText.replace(/\\r|\\n/g, '\r').replace(/\\t/g, '\t').replace(/\\e/g, '\x1b'));
    }
  }

  // ---------- campanello, copia alla selezione, appunti scritti dal server (OSC 52)
  term.onBell(() => {
    if (S.bell === 'visual') { const p = $('panel'); p.classList.add('bell'); setTimeout(() => p.classList.remove('bell'), 180); }
    else if (S.bell === 'sound') post({ t: 'beep' });
  });
  term.onSelectionChange(() => { if (S.copyOnSelect && term.hasSelection()) post({ t: 'copy', text: term.getSelection() }); });
  term.parser.registerOscHandler(52, d => {
    if (!S.allowServerClipboard) return true;
    const i = d.indexOf(';'); const data = d.slice(i + 1);
    if (!data || data === '?') return true;                // le richieste di LETTURA degli appunti non si accettano mai
    try { post({ t: 'copy', text: new TextDecoder().decode(b64(data)) }); } catch (e) { }
    return true;
  });

  // ---------- copia e incolla come in Windows Terminal: Ctrl+C copia se c'e' una selezione (altrimenti e' l'interruzione ^C),
  // Ctrl+V / Ctrl+Maiusc+V incollano, Ctrl+Maiusc+C copia sempre, tasto destro copia (se selezionato) o incolla
  function copySelection() { if (!term.hasSelection()) return false; post({ t: 'copy', text: term.getSelection() }); term.clearSelection(); return true; }
  term.attachCustomKeyEventHandler(e => {
    if (e.type !== 'keydown' || !e.ctrlKey || e.altKey || e.metaKey) return true;
    const k = e.key.toLowerCase();
    if (k === 'f' && !e.shiftKey) { showFind(true); e.preventDefault(); return false; }
    if (k === 'c') { if (e.shiftKey) { copySelection(); return false; } return copySelection() ? false : true; }
    if (k === 'v') { post({ t: 'paste' }); e.preventDefault(); return false; }
    return true;
  });
  $('t').addEventListener('contextmenu', e => { e.preventDefault(); if (!copySelection()) post({ t: 'paste' }); });
  // evita che il browser incolli da solo (il testo arriva dal programma)
  document.addEventListener('paste', e => e.preventDefault(), true);

  // ---------- ingresso/uscita
  term.onData(d => { if (d.length === 1) noteKey(); post({ t: 'in', d }); });
  term.onBinary(d => post({ t: 'in', d }));
  window.chrome.webview.addEventListener('message', e => {
    const m = e.data;
    if (m.t === 'out') { noteEcho(); term.write(b64(m.d)); }
    else if (m.t === 'msg') term.write('\r\n\x1b[90m' + m.d + '\x1b[0m\r\n');
    else if (m.t === 'focus') term.focus();
    else if (m.t === 'pasteText') { if (m.text) term.paste(m.text); }
    else if (m.t === 'search') showFind(true);
    else if (m.t === 'getSel') post({ t: 'sel', text: term.getSelection() });
    else if (m.t === 'settings') applySettings(m.s);
    else if (m.t === 'preview') { term.options.disableStdin = true; term.options.cursorBlink = false; $('status').style.display = 'none'; $('sb').style.display = 'none'; $('keys').style.display = 'none'; S.preview = true; }
    else if (m.t === 'hist') showHist(!!m.on);
    else if (m.t === 'histToggle') showHist($('hist').style.display === 'none');
    else if (m.t === 'collapseAll') blocks.slice().reverse().forEach(b => toggle(b, true));
    else if (m.t === 'expandAll') blocks.slice().forEach(b => toggle(b, false));
    else if (m.t === 'dump') post({ t: 'dump', blocks: blocks.map(b => ({ exit: b.exit, collapsed: b.collapsed, head: b.head.line, end: b.end ? b.end.line : -1, cmd: b.cmd, cwd: b.cwd })), len: buf().lines.length, text: lastLines(m.n || 30) });
    else if (m.t === 'toggle') { const b = blocks[m.i < 0 ? blocks.length + m.i : m.i]; if (b) toggle(b, !b.collapsed); post({ t: 'toggled', i: m.i }); }
    else if (m.t === 'act') { const b = blocks[m.i < 0 ? blocks.length + m.i : m.i]; if (b) act(b, m.what); }
    else if (m.t === 'menu') { const b = blocks[m.i < 0 ? blocks.length + m.i : m.i]; if (b) openMenu(b, m.x || 300, m.y || 100); }
  });
  function lastLines(n) {
    const a = term.buffer.active, out = [];
    for (let i = Math.max(0, a.length - n); i < a.length; i++) out.push(a.getLine(i).translateToString(true));
    return out;
  }

  fit.fit();
  term.focus();
  updateStatus(); drawSb();
  post({ t: 'ready', cols: term.cols, rows: term.rows });
})();
