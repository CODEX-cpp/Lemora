// Lemora: extension icons (same design as the mock: stroke, 24x24). Built with createElementNS, never with innerHTML.
// Used by the popup (data-icon attribute) and, with a copy of this list, by the content script.
'use strict';
const LEMORA_ICONS = {
  key: [['circle', { cx: 8, cy: 15, r: 4 }], ['path', { d: 'm11 12 9-9M16 7l3 3' }]],
  menu: [['path', { d: 'M4 7h16M4 12h16M4 17h16' }]],
  folder: [['path', { d: 'M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2Z' }]],
  chevron: [['path', { d: 'm6 9 6 6 6-6' }]],
  search: [['circle', { cx: 11, cy: 11, r: 7 }], ['path', { d: 'm20 20-3.5-3.5' }]],
  plus: [['path', { d: 'M12 5v14M5 12h14' }]],
  user: [['circle', { cx: 12, cy: 8, r: 4 }], ['path', { d: 'M4 21a8 8 0 0 1 16 0' }]],
  copy: [['rect', { x: 9, y: 9, width: 11, height: 11, rx: 2 }], ['path', { d: 'M5 15V6a2 2 0 0 1 2-2h9' }]],
  pen: [['path', { d: 'M12 20h9' }], ['path', { d: 'M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4Z' }]],
  external: [['path', { d: 'M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5' }]],
  dots: [['circle', { cx: 12, cy: 5, r: 1.6 }], ['circle', { cx: 12, cy: 12, r: 1.6 }], ['circle', { cx: 12, cy: 19, r: 1.6 }]],
  globe: [['circle', { cx: 12, cy: 12, r: 9 }], ['path', { d: 'M3 12h18M12 3c3 3 3 15 0 18M12 3c-3 3-3 15 0 18' }]],
  bolt: [['path', { d: 'm13 2-9 12h7l-1 8 9-12h-7Z' }]],
  lock: [['rect', { x: 5, y: 11, width: 14, height: 9, rx: 2 }], ['path', { d: 'M8 11V8a4 4 0 0 1 8 0v3' }]],
  warn: [['path', { d: 'M12 3 2 20h20Z' }], ['path', { d: 'M12 10v4M12 17.5v.1' }]],
  refresh: [['path', { d: 'M20 12a8 8 0 1 1-2.5-5.8' }], ['path', { d: 'M20 4v5h-5' }]],
  trash: [['path', { d: 'M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13' }]],
  home: [['path', { d: 'M3 11 12 4l9 7v9H3Z' }]],
  // Lemora logo (like icons/icon128.png): a ring open at the top right and a separate stroke that closes it
  logo: [['path', { d: 'M17.6 13.4A6.1 6.1 0 1 1 11.2 7', 'stroke-width': 3.2, 'stroke-linecap': 'butt' }],
    ['path', { d: 'M13.2 5.1A6.2 6.2 0 0 1 19.2 11', 'stroke-width': 3.2, 'stroke-linecap': 'butt' }]],
};

function lemoraIcon(name, size = 16) {
  const NS = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(NS, 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('width', size); svg.setAttribute('height', size);
  svg.setAttribute('fill', 'none'); svg.setAttribute('stroke', 'currentColor');
  svg.setAttribute('stroke-width', '2'); svg.setAttribute('stroke-linecap', 'round'); svg.setAttribute('stroke-linejoin', 'round');
  svg.setAttribute('aria-hidden', 'true');
  for (const [tag, attrs] of LEMORA_ICONS[name] || []) {
    const n = document.createElementNS(NS, tag);
    for (const [k, v] of Object.entries(attrs)) n.setAttribute(k, v);
    if (name === 'dots') n.setAttribute('fill', 'currentColor');
    svg.appendChild(n);
  }
  return svg;
}

// Lemora theme -> extension CSS variables (popup and in-page UI). From the few colors the app sends we derive
// lines, secondary text, panel backgrounds and status colors; a light theme wants them dark, a dark one light. Only #rrggbb colors
function lemoraThemeVars(t) {
  const hex = (v, d) => (/^#[0-9a-f]{6}$/i.test(v || '') ? v : d);
  const rgb = (h) => [1, 3, 5].map((i) => parseInt(h.slice(i, i + 2), 16));
  const mix = (a, b, w) => '#' + rgb(a).map((x, i) => Math.round(x * w + rgb(b)[i] * (1 - w)).toString(16).padStart(2, '0')).join('');
  const lum = (h) => { const [r, g, b] = rgb(h).map((x) => { x /= 255; return x <= 0.03928 ? x / 12.92 : ((x + 0.055) / 1.055) ** 2.4; }); return 0.2126 * r + 0.7152 * g + 0.0722 * b; };
  t = t || {};
  const bg = hex(t.background, '#1e1e1e'), text = hex(t.text, '#f0f0f0'), accent = hex(t.accent, '#60cdff');
  const dark = typeof t.dark === 'boolean' ? t.dark : lum(bg) < 0.3;
  return {
    scheme: dark ? 'dark' : 'light',
    '--bg': bg, '--text': text, '--accent': accent,
    '--surface': hex(t.surface, dark ? '#262626' : mix(bg, '#000000', 0.96)),
    '--raised': hex(t.raised, dark ? '#33383c' : mix(bg, '#ffffff', 0.5)),
    '--card': dark ? mix(bg, '#000000', 0.78) : mix(bg, '#ffffff', 0.45),
    '--field': mix(text, bg, 0.07),
    '--text-2': mix(text, bg, 0.72), '--text-3': mix(text, bg, 0.55),
    '--line': mix(text, bg, 0.09), '--line-2': mix(text, bg, 0.22), '--hover': mix(text, bg, 0.06),
    '--accent-ink': hex(t.accentText, lum(accent) > 0.35 ? '#101010' : '#ffffff'),
    '--ok': dark ? '#6ccb5f' : '#1e7b34', '--warn': dark ? '#fdd663' : '#8a5a00', '--danger': dark ? '#ff9b94' : '#b42318',
    '--warn-bg': dark ? 'rgba(253,214,99,.12)' : 'rgba(138,90,0,.10)',
  };
}

// entry name as "[folder] Name" (only the folder's last level is shown: "Clients / Acme Ltd" -> "Acme Ltd")
function lemoraLabel(e) {
  const name = e.name || e.ip || '';
  const f = (e.folder || '').split(' / ').pop();
  return f ? `[${f}] ${name}` : name;
}

// fills every element that has data-icon (before the text it contains)
function lemoraIconsIn(root) {
  for (const el of root.querySelectorAll('[data-icon]')) {
    if (el.querySelector(':scope > svg')) continue;
    el.prepend(lemoraIcon(el.dataset.icon, Number(el.dataset.size) || (el.classList.contains('tile') ? 22 : 15)));
  }
}
