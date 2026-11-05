'use strict';
// Console web di Lemora Server: creazione guidata (A1-A4), accesso (B1) e console (per ora solo la panoramica).
// Nessuna libreria: tutto il testo inserito passa da textContent, mai da innerHTML.

const I18N = {
  it: {
    setup: 'Creazione del server', console: 'Console di amministrazione', step: (n, name) => `Passo ${n} di 4 · ${name}`,
    next: 'Avanti', back: 'Indietro', retry: 'Riprova', openConsole: 'Apri la console',
    welcome: 'Benvenuto', welcomeT: 'Benvenuto in Lemora Server',
    welcomeS: 'Questo programma custodisce il database delle connessioni RDP, SSH e VNC condivise dai PC del tuo ufficio. Gira in background come servizio e si gestisce da questa pagina web.',
    localOnly: 'Solo rete locale. Il server non va esposto su internet: i client rifiutano indirizzi pubblici. Il traffico è in HTTPS con un certificato creato dal server.',
    vncNote: 'Anche le connessioni VNC si condividono: la password resta cifrata sul server.',
    code: 'Codice di avvio', codeHint: 'Lo trovi nel terminale dove hai avviato il server.',
    codeWrong: n => `Codice non corretto. Tentativi rimasti: ${n}.`, blocked: h => `Troppi tentativi sbagliati. Accesso bloccato per 5 minuti (riprova alle ${h}).`,
    database: 'Database', dbT: 'Dove salvare il database', dbFolder: 'Cartella del database',
    dbDefault: 'Percorso predefinito. Il controllo di scrittura si fa premendo Avanti.', dbOk: 'Controllo di scrittura: riuscito.',
    dbBad: 'La cartella non è scrivibile. Scegline un\'altra o controlla i permessi.', dbPath: 'Indica un percorso completo (per esempio {ex}).',
    freeSpace: 'Spazio libero', backup: 'Fai copie di questa cartella: contiene tutte le credenziali condivise.',
    adminNet: 'Amministratore e rete', adminUser: 'Utente amministratore', password: 'Password', confirm: 'Conferma password', port: 'Porta HTTPS',
    portDefault: 'Predefinita: 25353', portBusy: n => `La porta ${n} è occupata da un altro programma: chiudilo o scegli un'altra porta.`, portRange: 'Scegli una porta tra 1024 e 65535.',
    listen: 'Indirizzo di ascolto', listenAll: 'Tutte le reti locali (consigliato)', listenNote: 'Il server ascolta solo su indirizzi di rete locale: gli indirizzi pubblici non sono mai usati.',
    clientAddr: 'Indirizzo per i client', strength: ['Troppo corta', 'Debole', 'Discreta', 'Buona', 'Ottima'], chars: n => `${n} caratteri`, strengthL: 'Robustezza',
    pwShort: 'La password deve avere almeno 10 caratteri.', pwDiff: 'Le due password non coincidono.', userEmpty: 'Inserisci il nome dell\'amministratore.',
    done: 'Fatto', ready: 'Il server è pronto', readyS: 'Dai ai colleghi l\'indirizzo qui sotto e, dalla console, un codice di abbinamento.',
    failed: 'Non sono riuscito a creare il server', failedS: 'Risolvi i problemi qui sotto e riprova. Nessun dato è stato scritto.',
    fingerprint: 'Impronta del certificato (SHA-256)', fpNote: 'Controlla che l\'impronta mostrata sul primo client sia identica a questa.', dbShort: 'Database',
    restartNote: 'Il server si sta riavviando sulla nuova porta: se la pagina non si apre subito, aspetta qualche secondo e ricarica.',
    consoleT: 'Console Lemora Server', user: 'Utente', signIn: 'Accedi', wrong: n => `Utente o password non corretti. Tentativi rimasti: ${n}.`,
    exit: 'Esci', overview: 'Panoramica', overviewS: 'Stato del server.', serverStatus: 'Stato del server', address: 'Indirizzo',
    soon: 'La console è in costruzione: le altre voci arrivano nei prossimi passi.',
    nav: ['Panoramica', 'PC e utenti', 'Nuovo abbinamento', 'Connessioni condivise', 'Richieste', 'Proposte', 'Conflitti', 'Cestino', 'Registro attività', 'Collegamenti', 'Impostazioni'],
    pcsT: 'PC e utenti', pcsS: 'I PC collegati a questo server, identificati da «PC - utente di Windows».', pcsEmpty: 'Nessun PC ancora. Crea un codice da «Nuovo abbinamento» e inseriscilo sul primo PC.',
    pendingT: 'Richieste in attesa', allPcs: 'PC e utenti', approve: 'Approva', reject: 'Rifiuta', save: 'Salva', disconnect: 'Scollega', cancel: 'Annulla',
    colPc: 'PC - utente', colState: 'Stato', colPerm: 'Permesso', colSeen: 'Ultimo contatto', colAddr: 'Indirizzo', never: 'mai',
    permRead: 'Sola lettura', permWrite: 'Scrittura', noCopy: 'Non copiare le connessioni salvate di questo PC', requestedAt: 'richiesta il',
    states: { pending: 'In attesa', approved: 'Approvato', rejected: 'Rifiutato', revoked: 'Scollegato' },
    confirmT: 'Scollegare questo PC?', confirmS: n => `${n} non potrà più usare il server. Al prossimo contatto la sua copia dei dati condivisi viene cancellata. Per ricollegarlo serve un nuovo abbinamento.`,
    pairT: 'Nuovo abbinamento', pairS: 'Crea un codice, poi inseriscilo sul PC da collegare. Il codice vale per un solo PC.',
    genCode: 'Crea il codice', newCode: 'Crea un nuovo codice', noCode: 'Nessun codice attivo.', expiresIn: m => `Scade tra ${m}`, expired: 'Codice scaduto',
    pairHow: ['Sul PC da collegare apri Lemora e scegli «Collega a Teams».', 'Inserisci l\'indirizzo del server qui sopra e il codice.', 'Torna su «PC e utenti» e approva la richiesta.'],
    pairNew: 'Un nuovo codice annulla quello precedente.', connected: 'PC collegati', waiting: 'Richieste in attesa', sessionEnd: 'Sessione scaduta: accedi di nuovo.',
    netErr: 'Il server non risponde. Riprova tra poco.',
    connsS: 'Le voci RDP, SSH e VNC che arrivano a tutti i PC collegati. Anche le connessioni VNC si condividono: la password resta cifrata sul server.',
    search: 'Cerca per nome, indirizzo, cartella o tag', newConn: 'Nuova connessione', editConn: 'Modifica connessione',
    colName: 'Nome', colProto: 'Protocollo', edit: 'Modifica', pwBtn: 'Password', disable: 'Disabilita', enable: 'Abilita', remove: 'Elimina',
    active: 'Attiva', disabledS: 'Disabilitata',
    connsEmpty: 'Nessuna connessione condivisa. Aggiungi la prima con «Nuova connessione»: comparirà su tutti i PC collegati.',
    connsNone: 'Nessuna connessione corrisponde alla ricerca.',
    fName: 'Nome', fFolder: 'Cartella', folderHint: 'Per esempio Uffici/Sede, con la barra per le sottocartelle.', fProto: 'Protocollo', fHost: 'Indirizzo', fPort: 'Porta',
    passKeep: 'invariata', passClear: 'Togli la password', fTags: 'Tag', tagsHint: 'Separati da virgola.',
    badField: 'Valore non valido.', badPort: 'Indica una porta da 1 a 65535.',
    revealT: 'Mostra password', revealS: n => `La password di ${n} verrà mostrata qui e la lettura verrà registrata nel registro attività.`, revealGo: 'Mostra password',
    noPwd: 'Nessuna password salvata.', close: 'Chiudi', revealErr: 'Non sono riuscito a leggere la password. Riprova.',
    delT: 'Eliminare questa connessione?', delS: n => `${n} sparirà dai PC al prossimo contatto e finirà nel cestino.`,
    permReq: 'Richieste', reqS: 'Modifiche, eliminazioni e nuove voci chieste dai PC con il permesso «Richieste». Approvando, la modifica vale per tutti i PC.',
    reqEmpty: 'Nessuna richiesta in attesa.', reqHistory: 'Già decise', reqHistEmpty: 'Nessuna richiesta decisa.',
    kinds: { update: 'Modifica', delete: 'Eliminazione per tutti', create: 'Nuova voce' },
    reqStates: { pending: 'In attesa', stale: 'Su versione vecchia', approved: 'Approvata', rejected: 'Rifiutata' },
    staleNote: 'La voce è cambiata dopo la richiesta: approvando, i valori richiesti sostituiscono quelli attuali.',
    reqGone: 'La voce non c\'è più sul server: la richiesta si può solo rifiutare.', delAsk: 'Il PC chiede di eliminare questa voce per tutti: approvando finisce nel cestino.',
    colField: 'Campo', colNow: 'Attuale', colAsked: 'Richiesto', pwChanged: 'cambiata', pwSame: 'invariata', reqNote: 'Nota per il PC (facoltativa)',
    colDate: 'Data', colKind: 'Tipo', colEntry: 'Voce', colNote: 'Nota',
    persS: 'Connessioni salvate sui PC e inviate al server: dai PC in sola lettura arrivano sempre, dai PC con «Richieste» se l\'utente ha acceso la condivisione. Approvando, la voce diventa condivisa per tutti; rifiutando resta solo sul suo PC.',
    persEmpty: 'Nessuna proposta in attesa.', persHistEmpty: 'Nessuna proposta decisa.', approveAll: 'Approva tutte', persPending: n => `${n} in attesa`,
    persStates: { pending: 'In attesa', published: 'Pubblicata', rejected: 'Rifiutata' },
    dupShared: n => `Possibile doppione: c'è già la voce condivisa «${n}» con lo stesso indirizzo, porta e utente.`,
    dupProposal: (n, pc) => `Possibile doppione: anche ${pc} propone «${n}» con lo stesso indirizzo, porta e utente.`,
    persNote: 'Nota per il PC (facoltativa)', colWhere: 'Indirizzo',
  },
  en: {
    setup: 'Server setup', console: 'Administration console', step: (n, name) => `Step ${n} of 4 · ${name}`,
    next: 'Next', back: 'Back', retry: 'Retry', openConsole: 'Open the console',
    welcome: 'Welcome', welcomeT: 'Welcome to Lemora Server',
    welcomeS: 'This program keeps the shared RDP, SSH and VNC connections of your office PCs. It runs in the background as a service and is managed from this web page.',
    localOnly: 'Local network only. Never expose the server to the internet: clients refuse public addresses. Traffic is HTTPS with a certificate created by the server.',
    vncNote: 'VNC connections are shared too: the password stays encrypted on the server.',
    code: 'Startup code', codeHint: 'You can find it in the terminal where you started the server.',
    codeWrong: n => `Wrong code. Attempts left: ${n}.`, blocked: h => `Too many failed attempts. Access locked for 5 minutes (retry at ${h}).`,
    database: 'Database', dbT: 'Where to store the database', dbFolder: 'Database folder',
    dbDefault: 'Default path. The write check runs when you press Next.', dbOk: 'Write check: passed.',
    dbBad: 'The folder is not writable. Choose another or check permissions.', dbPath: 'Enter a full path (for example {ex}).',
    freeSpace: 'Free space', backup: 'Back up this folder: it holds all shared credentials.',
    adminNet: 'Administrator and network', adminUser: 'Admin user', password: 'Password', confirm: 'Confirm password', port: 'HTTPS port',
    portDefault: 'Default: 25353', portBusy: n => `Port ${n} is in use by another program: close it or choose another port.`, portRange: 'Choose a port between 1024 and 65535.',
    listen: 'Listen address', listenAll: 'All local networks (recommended)', listenNote: 'The server listens only on local network addresses: public addresses are never used.',
    clientAddr: 'Address for clients', strength: ['Too short', 'Weak', 'Fair', 'Good', 'Excellent'], chars: n => `${n} characters`, strengthL: 'Strength',
    pwShort: 'The password must be at least 10 characters.', pwDiff: 'The two passwords do not match.', userEmpty: 'Enter the administrator name.',
    done: 'Done', ready: 'The server is ready', readyS: 'Give colleagues the address below and, from the console, a pairing code.',
    failed: 'Could not create the server', failedS: 'Fix the issues below and retry. Nothing was written.',
    fingerprint: 'Certificate fingerprint (SHA-256)', fpNote: 'Check that the fingerprint shown on the first client is identical to this one.', dbShort: 'Database',
    restartNote: 'The server is restarting on the new port: if the page does not open right away, wait a few seconds and reload.',
    consoleT: 'Lemora Server console', user: 'User', signIn: 'Sign in', wrong: n => `Wrong user or password. Attempts left: ${n}.`,
    exit: 'Sign out', overview: 'Overview', overviewS: 'Server status.', serverStatus: 'Server status', address: 'Address',
    soon: 'The console is under construction: the other entries arrive in the next steps.',
    nav: ['Overview', 'PCs and users', 'New pairing', 'Shared connections', 'Requests', 'Proposals', 'Conflicts', 'Recycle bin', 'Activity log', 'Sessions', 'Settings'],
    pcsT: 'PCs and users', pcsS: 'The PCs connected to this server, identified as "PC - Windows user".', pcsEmpty: 'No PCs yet. Create a code in "New pairing" and enter it on the first PC.',
    pendingT: 'Pending requests', allPcs: 'PCs and users', approve: 'Approve', reject: 'Reject', save: 'Save', disconnect: 'Disconnect', cancel: 'Cancel',
    colPc: 'PC - user', colState: 'Status', colPerm: 'Permission', colSeen: 'Last contact', colAddr: 'Address', never: 'never',
    permRead: 'Read only', permWrite: 'Write', noCopy: 'Do not copy this PC\'s saved connections', requestedAt: 'requested on',
    states: { pending: 'Pending', approved: 'Approved', rejected: 'Rejected', revoked: 'Disconnected' },
    confirmT: 'Disconnect this PC?', confirmS: n => `${n} will no longer be able to use the server. At its next contact its copy of the shared data is deleted. Reconnecting needs a new pairing.`,
    pairT: 'New pairing', pairS: 'Create a code, then enter it on the PC to connect. The code works for one PC only.',
    genCode: 'Create the code', newCode: 'Create a new code', noCode: 'No active code.', expiresIn: m => `Expires in ${m}`, expired: 'Code expired',
    pairHow: ['On the PC to connect, open Lemora and choose "Connect to Teams".', 'Enter the server address above and the code.', 'Come back to "PCs and users" and approve the request.'],
    pairNew: 'A new code cancels the previous one.', connected: 'Connected PCs', waiting: 'Pending requests', sessionEnd: 'Session expired: sign in again.',
    netErr: 'The server is not responding. Try again shortly.',
    connsS: 'The RDP, SSH and VNC entries that reach every connected PC. VNC connections are shared too: the password stays encrypted on the server.',
    search: 'Search by name, address, folder or tag', newConn: 'New connection', editConn: 'Edit connection',
    colName: 'Name', colProto: 'Protocol', edit: 'Edit', pwBtn: 'Password', disable: 'Disable', enable: 'Enable', remove: 'Delete',
    active: 'Active', disabledS: 'Disabled',
    connsEmpty: 'No shared connections. Add the first one with "New connection": it will appear on every connected PC.',
    connsNone: 'No connection matches the search.',
    fName: 'Name', fFolder: 'Folder', folderHint: 'For example Offices/HQ, with the slash for subfolders.', fProto: 'Protocol', fHost: 'Address', fPort: 'Port',
    passKeep: 'unchanged', passClear: 'Remove the password', fTags: 'Tags', tagsHint: 'Separated by commas.',
    badField: 'Invalid value.', badPort: 'Enter a port from 1 to 65535.',
    revealT: 'Show password', revealS: n => `The password of ${n} will be shown here and the reading will be recorded in the activity log.`, revealGo: 'Show password',
    noPwd: 'No password saved.', close: 'Close', revealErr: 'Could not read the password. Try again.',
    delT: 'Delete this connection?', delS: n => `${n} will disappear from the PCs at their next contact and goes to the recycle bin.`,
    permReq: 'Requests', reqS: 'Changes, deletions and new entries asked by the PCs with the "Requests" permission. Once approved, the change applies to every PC.',
    reqEmpty: 'No pending requests.', reqHistory: 'Already decided', reqHistEmpty: 'No decided requests.',
    kinds: { update: 'Change', delete: 'Delete for everyone', create: 'New entry' },
    reqStates: { pending: 'Pending', stale: 'On an old version', approved: 'Approved', rejected: 'Rejected' },
    staleNote: 'The entry changed after the request: approving replaces the current values with the requested ones.',
    reqGone: 'The entry no longer exists on the server: the request can only be rejected.', delAsk: 'The PC asks to delete this entry for everyone: once approved it goes to the recycle bin.',
    colField: 'Field', colNow: 'Current', colAsked: 'Requested', pwChanged: 'changed', pwSame: 'unchanged', reqNote: 'Note for the PC (optional)',
    colDate: 'Date', colKind: 'Type', colEntry: 'Entry', colNote: 'Note',
    persS: 'Connections saved on the PCs and sent to the server: read-only PCs always send them, PCs with "Requests" only if the user turned sharing on. Once approved, the entry is shared with everyone; if rejected it stays on its PC only.',
    persEmpty: 'No pending proposals.', persHistEmpty: 'No decided proposals.', approveAll: 'Approve all', persPending: n => `${n} pending`,
    persStates: { pending: 'Pending', published: 'Published', rejected: 'Rejected' },
    dupShared: n => `Possible duplicate: the shared entry "${n}" already has the same address, port and user.`,
    dupProposal: (n, pc) => `Possible duplicate: ${pc} also proposes "${n}" with the same address, port and user.`,
    persNote: 'Note for the PC (optional)', colWhere: 'Address',
  },
};

let lang = 'it';
try { lang = localStorage.getItem('lang') || ((navigator.language || 'it').toLowerCase().startsWith('it') ? 'it' : 'en'); } catch { /* nessuna memoria del browser: si resta sull'italiano */ }
const t = k => I18N[lang][k] ?? I18N.it[k] ?? k;
// l'esempio di percorso e' quello del sistema su cui gira il server (Windows o Linux)
const dbPathMsg = () => t('dbPath').replace('{ex}', S.st?.defaults?.dbDir ?? '');

const h = (tag, props, ...kids) => {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(props || {})) {
    if (v == null || v === false) continue;
    if (k === 'class') e.className = v;
    else if (k.startsWith('on')) e.addEventListener(k.slice(2), v);
    else if (k === 'value') e.value = v;
    else e.setAttribute(k, v === true ? '' : v);
  }
  for (const c of kids.flat(Infinity)) if (c != null && c !== false) e.append(c.nodeType ? c : String(c));
  return e;
};

async function api(path, body) {
  try {
    const r = await fetch('/api' + path, body === undefined ? { credentials: 'same-origin' } : {
      method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json', 'X-Lemora': '1' }, body: JSON.stringify(body),
    });
    let data = null;
    try { data = await r.json(); } catch { /* risposta senza corpo */ }
    return { status: r.status, data: data || {} };
  } catch { return { status: 0, data: {} }; }
}

const S = { st: null, screen: 'A1', f: { code: '', dbDir: '', user: 'admin', pwd: '', pwd2: '', port: '25353', addr: 'all' }, err: {}, busy: false, result: null, me: null,
  page: 'overview', pcs: [], sel: {}, code: null, confirm: null, tick: null, poll: null, conns: [], connForm: null, connFilter: '', reveal: null, connDel: null, reqs: [], reqNote: {}, reqErr: {}, pers: [], persNote: {}, persErr: {},
  conflicts: [], cfErr: {}, trash: [], trashConfirm: null, events: { items: [], total: 0, offset: 0 }, evFilter: { kind: '', pc: '' },
  sessions: null, sessView: 'pc' };
const app = document.getElementById('app');
const hhmm = iso => new Date(iso).toLocaleTimeString(lang === 'it' ? 'it-IT' : 'en-GB', { hour: '2-digit', minute: '2-digit' });
const fmtGb = n => `${Math.round(n / 1073741824)} GB`;
const colonFp = s => (s.match(/.{2}/g) || []).join(':');

function go(screen) { S.screen = screen; S.err = {}; render(); }

// ---- componenti -------------------------------------------------------------------------------------------------

const note = (kind, text) => h('div', { class: 'note ' + (kind === 'info' ? '' : kind) }, h('span', { class: 'ic' }, { info: 'i', warn: '!', err: '✕', ok: '✓' }[kind]), h('span', {}, text));
const kv = rows => h('div', { class: 'kv' }, rows.map(([k, v, mono]) => h('div', {}, h('span', {}, k), h('span', { class: mono ? 'mono' : '' }, v))));
const btn = (text, onclick, primary, disabled) => h('button', { class: 'btn' + (primary ? ' primary' : ''), type: 'button', onclick, disabled }, text);

function field(label, id, key, opts = {}) {
  const input = h(opts.select ? 'select' : 'input', {
    id, class: opts.cls, type: opts.type || 'text', autocomplete: opts.type === 'password' ? 'new-password' : 'off', spellcheck: 'false',
    value: opts.select ? null : S.f[key],
    oninput: e => { S.f[key] = e.target.value; opts.onInput?.(e.target.value); },
  });
  if (opts.select) { for (const [v, text] of opts.select) input.append(h('option', { value: v }, text)); input.value = S.f[key]; }
  return h('div', { class: 'field' + (S.err[key] ? ' err' : '') }, h('label', { class: 'label', for: id }, label), h('div', { class: 'row' }, input),
    (S.err[key] || opts.hint) && h('div', { class: 'hint' + (S.err[key] ? '' : opts.hintClass ? ' ' + opts.hintClass : '') }, S.err[key] || opts.hint));
}

// il pulsante Avanti si disattiva a campo vuoto: va riattivato mentre si scrive, senza ridisegnare la pagina (si perderebbe il cursore)
function syncNext(id, value) {
  const b = document.getElementById(id)?.closest('form')?.querySelector('button[type=submit]');
  if (b) b.disabled = S.busy || !value.trim();
}

function strength(p) {
  if (p.length < 10) return 0;
  let n = 1;
  if (p.length >= 14) n++;
  if (/[a-z]/.test(p) && /[A-Z]/.test(p) && /\d/.test(p)) n++;
  if (/[^A-Za-z0-9]/.test(p)) n++;
  return Math.min(n, 4);
}

function langSwitch() {
  const set = l => () => { lang = l; try { localStorage.setItem('lang', l); } catch { /* facoltativo */ } document.documentElement.lang = l; render(); };
  return h('div', { class: 'seg' }, ['it', 'en'].map(l => h('button', { type: 'button', class: lang === l ? 'on' : '', onclick: set(l) }, l.toUpperCase())));
}

const topbar = (crumb, extra) => h('div', { class: 'topbar' }, h('span', { class: 'crumb' }, crumb), langSwitch(), extra);

function wizard(n, name, ...kids) {
  const bars = [1, 2, 3, 4].map(i => h('i', { class: i <= n ? 'on' : '' }));
  return h('div', { class: 'shell' }, topbar(t('setup')),
    h('div', { class: 'body' }, h('div', { class: 'col' }, h('div', { class: 'stepper' }, h('div', { class: 'bars' }, bars), h('span', {}, t('step')(n, name))), kids)));
}

// ---- schermate ------------------------------------------------------------------------------------------------

function screenA1() {
  const submit = async () => {
    S.busy = true; render();
    const { status, data } = await api('/setup/verify', { code: S.f.code });
    S.busy = false;
    if (status === 200) { await loadState(); S.f.dbDir ||= S.st.defaults.dbDir; return go('A2'); }
    S.err = { code: status === 429 ? t('blocked')(hhmm(data.until)) : status === 401 ? t('codeWrong')(data.remaining) : t('netErr') };
    render();
  };
  return wizard(1, t('welcome'),
    h('h1', {}, t('welcomeT')), h('div', { class: 'sub' }, t('welcomeS')),
    note('warn', t('localOnly')), note('info', t('vncNote')),
    h('form', { onsubmit: e => { e.preventDefault(); submit(); } },
      field(t('code'), 'code', 'code', { cls: 'code-input', hint: t('codeHint'), onInput: v => syncNext('code', v) }),
      h('div', { class: 'actions' }, h('button', { class: 'btn primary', type: 'submit', disabled: S.busy || !S.f.code.trim() }, t('next')))));
}

function screenA2() {
  const submit = async () => {
    S.busy = true; render();
    const { status, data } = await api('/setup/check-folder', { path: S.f.dbDir });
    S.busy = false;
    if (status === 403) { await loadState(); return go('A1'); }
    if (data.error) { S.err = { dbDir: data.error === 'path' ? dbPathMsg() : t('dbBad') }; return render(); }
    S.folder = data;
    go('A3');
  };
  return wizard(2, t('database'),
    h('h1', {}, t('dbT')),
    h('form', { onsubmit: e => { e.preventDefault(); submit(); } },
      field(t('dbFolder'), 'dbDir', 'dbDir', { cls: 'path', hint: t('dbDefault'), onInput: v => syncNext('dbDir', v) }),
      h('div', { class: 'actions' }, btn(t('back'), () => go('A1')), h('button', { class: 'btn primary', type: 'submit', disabled: S.busy || !S.f.dbDir.trim() }, t('next')))),
    note('warn', t('backup')));
}

function screenA3() {
  const addrs = S.st.defaults.addresses;
  const preview = h('span', { class: 'mono' });
  const meter = h('div', { class: 'meter' }), meterLabel = h('div', { class: 'hint' });
  const updatePreview = () => { const host = S.f.addr === 'all' ? addrs[0] : S.f.addr; preview.textContent = `https://${host}:${S.f.port}`; };
  const updateMeter = () => {
    const n = strength(S.f.pwd), tone = n < 2 ? 'err' : n < 4 ? 'warn' : 'ok'; // classi e non style="...": la politica di sicurezza della pagina blocca gli stili scritti nell'HTML
    meter.replaceChildren(...[0, 1, 2, 3].map(i => h('i', { class: i < n ? tone : '' })));
    meterLabel.textContent = S.f.pwd ? `${t('strengthL')}: ${t('strength')[n].toLowerCase()} · ${t('chars')(S.f.pwd.length)}` : '';
  };
  const submit = async () => {
    const e = {};
    if (!S.f.user.trim()) e.user = t('userEmpty');
    if (S.f.pwd.length < 10) e.pwd = t('pwShort');
    else if (S.f.pwd !== S.f.pwd2) e.pwd2 = t('pwDiff');
    const port = parseInt(S.f.port, 10);
    if (!(port >= 1024 && port <= 65535)) e.port = t('portRange');
    if (Object.keys(e).length) { S.err = e; return render(); }
    S.busy = true; render();
    const { status, data } = await api('/setup/complete', { dbDir: S.f.dbDir, user: S.f.user.trim(), password: S.f.pwd, port, address: S.f.addr });
    S.busy = false;
    if (status === 200) { S.result = data; S.f.pwd = S.f.pwd2 = ''; return go('A4'); }
    if (status === 403) { await loadState(); return go('A1'); }
    S.result = { errors: data.errors || [t('netErr')] };
    go('A4');
  };
  const node = wizard(3, t('adminNet'),
    h('h1', {}, t('adminNet')),
    h('form', { class: 'stack', onsubmit: e => { e.preventDefault(); submit(); } },
      field(t('adminUser'), 'user', 'user', { cls: 'mid' }),
      field(t('password'), 'pwd', 'pwd', { type: 'password', cls: 'mid', onInput: updateMeter }),
      h('div', {}, meter, meterLabel),
      field(t('confirm'), 'pwd2', 'pwd2', { type: 'password', cls: 'mid' }),
      field(t('port'), 'port', 'port', { cls: 'mono short', hint: S.err.port ? null : t('portDefault'), onInput: updatePreview }),
      field(t('listen'), 'addr', 'addr', { select: [['all', t('listenAll')], ...addrs.map(a => [a, a])], hint: t('listenNote'), onInput: updatePreview }),
      kv([[t('clientAddr'), preview, true]]),
      h('div', { class: 'actions' }, btn(t('back'), () => go('A2')), h('button', { class: 'btn primary', type: 'submit', disabled: S.busy }, t('next')))));
  updateMeter(); updatePreview();
  return node;
}

function errText(code) {
  const [kind, detail] = code.split(':');
  if (kind === 'folder') return `${t('dbFolder')}: ${detail === 'path' ? dbPathMsg() : t('dbBad')}`;
  if (kind === 'port') return detail === 'busy' ? t('portBusy')(S.f.port) : t('portRange');
  if (kind === 'password') return t('pwShort');
  if (kind === 'user') return t('userEmpty');
  return code;
}

function screenA4() {
  const r = S.result;
  if (r.errors) {
    return wizard(4, t('done'), h('div', { class: 'result' }, h('span', { class: 'big err' }, '✕'), h('span', { class: 'title' }, t('failed')), h('span', { class: 'sub' }, t('failedS'))),
      r.errors.map(e => note('err', errText(e))), h('div', { class: 'actions' }, btn(t('retry'), () => go('A3'), true)));
  }
  const open = () => {
    if (!r.restart) return location.reload();
    const url = r.urls.find(u => new URL(u).hostname === location.hostname) || r.urls[0];
    location.href = url + '/';
  };
  return wizard(4, t('done'),
    h('div', { class: 'result' }, h('span', { class: 'big' }, '✓'), h('span', { class: 'title' }, t('ready')), h('span', { class: 'sub' }, t('readyS'))),
    kv([[t('clientAddr'), h('span', {}, r.urls.map(u => h('div', {}, u))), true], [t('fingerprint'), colonFp(r.fingerprint), true], [t('dbShort'), r.dbDir, true]]),
    note('info', t('fpNote')), r.restart && note('warn', t('restartNote')),
    h('div', { class: 'actions' }, btn(t('openConsole'), open, true)));
}

function screenB1() {
  const submit = async () => {
    S.busy = true; render();
    const { status, data } = await api('/login', { user: S.f.user, password: S.f.pwd });
    S.busy = false;
    if (status === 200) { S.f.pwd = ''; await loadState(); S.me = (await api('/me')).data; S.page = 'overview'; await loadPcs(); await loadConns(); await loadReqs(); await loadPers(); await loadConflicts(); await loadTrash(); return go('C'); }
    S.f.pwd = '';
    S.err = { form: status === 429 ? t('blocked')(hhmm(data.until)) : status === 401 ? t('wrong')(data.remaining) : t('netErr'), blocked: status === 429 };
    render();
  };
  return h('div', { class: 'shell' }, topbar(t('console')), h('div', { class: 'body' }, h('form', { class: 'login', onsubmit: e => { e.preventDefault(); submit(); } },
    h('div', { class: 'head' }, h('img', { class: 'logo', src: '/logo.svg', alt: '' }), t('consoleT')),
    field(t('user'), 'user', 'user'), field(t('password'), 'pwd', 'pwd', { type: 'password' }),
    S.err.form && note('err', S.err.form),
    h('button', { class: 'btn primary', type: 'submit', disabled: S.busy || S.err.blocked }, t('signIn')))));
}

// ---- console: pagine ----------------------------------------------------------------------------------------------

const PAGES = ['overview', 'pcs', 'pair', 'conns', 'requests', 'proposals', 'conflicts', 'trash', 'events', 'sessions']; // nell'ordine delle voci del menu; resta fuori solo Impostazioni
const pendingCount = () => S.pcs.filter(p => p.state === 'pending').length;
const reqCount = () => S.reqs.filter(r => r.state === 'pending' || r.state === 'stale').length;
const persCount = () => S.pers.filter(p => p.state === 'pending').length;
const conflictCount = () => S.conflicts.filter(q => q.state === 'pending').length;
const dt = iso => iso ? new Date(iso).toLocaleString(lang === 'it' ? 'it-IT' : 'en-GB', { dateStyle: 'short', timeStyle: 'short' }) : t('never');
const permText = p => t(p === 'write' ? 'permWrite' : p === 'requests' ? 'permReq' : 'permRead');
const chip = (state) => h('span', { class: 'chip ' + ({ approved: 'ok', pending: 'warn', rejected: 'err', revoked: 'err' }[state] || 'info') }, t('states')[state] || state);

async function sessionLost() { await loadState(); S.screen = 'B1'; S.err = { form: t('sessionEnd') }; render(); }

async function loadPcs() {
  const r = await api('/pcs');
  if (r.status === 401) { await sessionLost(); return false; }
  if (r.status !== 200) return true; // errore passeggero: si tiene l'elenco che c'e'
  const changed = JSON.stringify(r.data) !== JSON.stringify(S.pcs);
  S.pcs = r.data;
  return changed;
}

async function loadCode() {
  const r = await api('/pairing/current');
  S.code = r.status === 200 && r.data.code ? r.data : null;
}

async function openPage(key) {
  S.page = key;
  if (key === 'pair') await loadCode();
  if (key === 'conns') await loadConns();
  if (key === 'requests') await loadReqs();
  if (key === 'proposals') await loadPers();
  if (key === 'conflicts') await loadConflicts();
  if (key === 'trash') await loadTrash();
  if (key === 'events') await loadEvents(true);
  if (key === 'sessions') await loadSessions();
  if (key === 'shared') { S.sh.msg = null; await loadShared(); }
  if (key === 'admins') { S.err = {}; S.admMsg = null; await loadAdmins(); }
  render();
}

// ---- proposte: connessioni personali dei PC (Personal.cs) ---------------------------------------------------------

async function loadPers() {
  const r = await api('/personal');
  if (r.status === 401) { await sessionLost(); return false; }
  if (r.status !== 200 || !Array.isArray(r.data)) return false;
  const changed = JSON.stringify(r.data) !== JSON.stringify(S.pers);
  S.pers = r.data;
  return changed;
}

async function decidePers(id, action) {
  const r = await api(`/personal/${id}/${action}`, { note: S.persNote[id] || null });
  if (r.status === 401) return sessionLost();
  if (r.status === 200) { delete S.persNote[id]; delete S.persErr[id]; } else S.persErr[id] = t('netErr');
  await loadPers(); await loadConns();
  render();
}

async function approveAllPers(pcId) {
  const r = await api(`/personal/pc/${pcId}/approve-all`, {});
  if (r.status === 401) return sessionLost();
  await loadPers(); await loadConns();
  render();
}

function proposalsPage() {
  const open = S.pers.filter(p => p.state === 'pending');
  const done = S.pers.filter(p => p.state !== 'pending');
  const where = p => `${p.host}:${p.port}`;
  const groups = [...new Set(open.map(p => p.pcId))].map(pcId => open.filter(p => p.pcId === pcId));
  const item = p => h('div', { class: 'card' },
    h('div', { class: 'ctl' }, h('span', { class: 'chip info' }, String(p.protocol).toUpperCase()), h('span', { class: 'who' }, p.name), h('span', { class: 'grow' }),
      h('span', { class: 'meta' }, dt(p.updatedAt))),
    p.dups.map(d => note('warn', d.kind === 'shared' ? t('dupShared')(d.name) : t('dupProposal')(d.name, d.pc))),
    kv([[t('fFolder'), p.folder || '—'], [t('colWhere'), where(p), true], [t('user'), p.user || '—'], [t('password'), p.hasPassword ? '•••' : '—'], [t('fTags'), p.tags || '—']]),
    S.persErr[p.id] && note('err', S.persErr[p.id]),
    h('div', { class: 'ctl' },
      h('div', { class: 'field grow' }, h('label', { class: 'label sr', for: 'pn-' + p.id }, t('persNote')),
        h('div', { class: 'row' }, h('input', { id: 'pn-' + p.id, class: 'req-note', placeholder: t('persNote'), maxlength: '500', value: S.persNote[p.id] || '', oninput: e => { S.persNote[p.id] = e.target.value; } }))),
      btn(t('reject'), () => decidePers(p.id, 'reject')),
      btn(t('approve'), () => decidePers(p.id, 'approve'), true)));
  return [
    h('h1', {}, t('nav')[5]), h('div', { class: 'sub' }, t('persS')),
    groups.length === 0 ? h('div', { class: 'empty' }, t('persEmpty'))
      : groups.map(g => [
          h('div', { class: 'ctl' }, h('h2', {}, g[0].pc), h('span', { class: 'chip warn' }, t('persPending')(g.length)), h('span', { class: 'grow' }),
            btn(t('approveAll'), () => approveAllPers(g[0].pcId), true)),
          h('div', { class: 'cards' }, g.map(item))]),
    h('h2', {}, t('reqHistory')),
    done.length === 0 ? h('div', { class: 'empty' }, t('persHistEmpty'))
      : h('div', { class: 'tblwrap' }, h('table', { class: 'tbl' },
          h('thead', {}, h('tr', {}, [t('colDate'), t('colPc'), t('colEntry'), t('colWhere'), t('colState'), t('colNote')].map(c => h('th', {}, c)))),
          h('tbody', {}, done.map(p => h('tr', {},
            h('td', {}, dt(p.decidedAt || p.updatedAt)), h('td', {}, p.pc), h('td', {}, p.name), h('td', { class: 'mono' }, where(p)),
            h('td', {}, h('span', { class: 'chip ' + (p.state === 'published' ? 'ok' : 'err') }, t('persStates')[p.state] || p.state)), h('td', {}, p.note || '—')))))),
  ];
}

// ---- richieste dei PC (permesso "Richieste") ------------------------------------------------------------------------

async function loadReqs() {
  const r = await api('/requests');
  if (r.status === 401) { await sessionLost(); return false; }
  if (r.status !== 200 || !Array.isArray(r.data)) return false;
  const changed = JSON.stringify(r.data) !== JSON.stringify(S.reqs);
  S.reqs = r.data;
  return changed;
}

async function decideReq(id, action) {
  const r = await api(`/requests/${id}/${action}`, { note: S.reqNote[id] || null });
  if (r.status === 401) return sessionLost();
  if (r.status === 200) { delete S.reqNote[id]; delete S.reqErr[id]; } else S.reqErr[id] = r.data.error === 'notfound' ? t('reqGone') : t('netErr');
  await loadReqs();
  render();
}

const REQ_FIELDS = [['name', 'fName'], ['folder', 'fFolder'], ['protocol', 'fProto'], ['host', 'fHost'], ['port', 'fPort'], ['user', 'user'], ['password', 'password'], ['tags', 'fTags']];

function reqDiff(q) {
  const o = q.old, n = q.new;
  const val = (v, k) => !v ? '—' : k === 'protocol' ? String(v[k]).toUpperCase() : (v[k] === '' || v[k] == null ? '—' : String(v[k]));
  const rows = REQ_FIELDS.map(([k, label]) => {
    // la password non si mostra mai: solo se cambia
    const before = k === 'password' ? (o ? '•••' : '—') : val(o, k);
    const after = !n ? '—' : k === 'password' ? t(q.pwChanged ? 'pwChanged' : 'pwSame') : val(n, k);
    const changed = n && (k === 'password' ? q.pwChanged : before !== after);
    return h('tr', { class: changed ? 'chg' : '' }, h('td', {}, t(label)), h('td', { class: 'mono' }, before), h('td', { class: 'mono' }, after));
  });
  return h('div', { class: 'tblwrap' }, h('table', { class: 'tbl diff' },
    h('thead', {}, h('tr', {}, [t('colField'), t('colNow'), q.kind === 'delete' ? '' : t('colAsked')].map(c => h('th', {}, c)))), h('tbody', {}, rows)));
}

function requestsPage() {
  const open = S.reqs.filter(q => q.state === 'pending' || q.state === 'stale');
  const done = S.reqs.filter(q => q.state === 'approved' || q.state === 'rejected');
  const entry = q => q.new?.name || q.old?.name || q.connId || '—';
  const card = q => {
    const gone = q.kind !== 'create' && !q.old;
    return h('div', { class: 'card' },
      h('div', { class: 'ctl' }, h('span', { class: 'chip info' }, t('kinds')[q.kind] || q.kind), h('span', { class: 'who' }, entry(q)), h('span', { class: 'grow' }),
        h('span', { class: 'meta' }, `${q.pc} · ${dt(q.createdAt)}`)),
      gone ? note('err', t('reqGone')) : q.state === 'stale' && note('warn', t('staleNote')),
      q.kind === 'delete' && !gone && note('warn', t('delAsk')),
      reqDiff(q),
      S.reqErr[q.id] && note('err', S.reqErr[q.id]),
      h('div', { class: 'ctl' },
        h('div', { class: 'field grow' }, h('label', { class: 'label sr', for: 'rn-' + q.id }, t('reqNote')),
          h('div', { class: 'row' }, h('input', { id: 'rn-' + q.id, class: 'req-note', placeholder: t('reqNote'), maxlength: '500', value: S.reqNote[q.id] || '', oninput: e => { S.reqNote[q.id] = e.target.value; } }))),
        btn(t('reject'), () => decideReq(q.id, 'reject')),
        btn(t('approve'), () => decideReq(q.id, 'approve'), true, gone)));
  };
  return [
    h('h1', {}, t('nav')[4]), h('div', { class: 'sub' }, t('reqS')),
    open.length ? h('div', { class: 'cards' }, open.map(card)) : h('div', { class: 'empty' }, t('reqEmpty')),
    h('h2', {}, t('reqHistory')),
    done.length === 0 ? h('div', { class: 'empty' }, t('reqHistEmpty'))
      : h('div', { class: 'tblwrap' }, h('table', { class: 'tbl' },
          h('thead', {}, h('tr', {}, [t('colDate'), t('colPc'), t('colKind'), t('colEntry'), t('colState'), t('colNote')].map(c => h('th', {}, c)))),
          h('tbody', {}, done.map(q => h('tr', {},
            h('td', {}, dt(q.decidedAt)), h('td', {}, q.pc), h('td', {}, t('kinds')[q.kind] || q.kind), h('td', {}, entry(q)),
            h('td', {}, h('span', { class: 'chip ' + (q.state === 'approved' ? 'ok' : 'err') }, t('reqStates')[q.state])), h('td', {}, q.note || '—')))))),
  ];
}

// ---- connessioni condivise ------------------------------------------------------------------------------------------

async function loadConns() {
  const r = await api('/connections');
  if (r.status === 401) { await sessionLost(); return false; }
  if (r.status !== 200 || !Array.isArray(r.data)) return false;
  const changed = JSON.stringify(r.data) !== JSON.stringify(S.conns);
  S.conns = r.data;
  return changed;
}

async function connAct(path, body) {
  const r = await api('/connections' + path, body);
  if (r.status === 401) return sessionLost();
  await loadConns();
  render();
}

const connDialogOpen = () => !!(S.connForm || S.reveal || S.connDel || S.shDlg);
const closeConnDialogs = () => { S.connForm = null; S.reveal = null; S.connDel = null; S.shDlg = null; render(); };

function openConnForm(c) {
  S.connForm = {
    id: c ? c.id : null, clear: false, portTouched: !!c, err: {},
    v: c ? { name: c.name, folder: c.folder || '', protocol: c.protocol, host: c.host, port: String(c.port), user: c.user || '', password: '', tags: c.tags || '' }
      : { name: '', folder: '', protocol: 'rdp', host: '', port: '3389', user: '', password: '', tags: '' },
  };
  render();
}

async function revealPwd() {
  const R = S.reveal;
  R.error = false;
  const r = await api(`/connections/${R.id}/reveal`, {});
  if (r.status === 401) { S.reveal = null; return sessionLost(); }
  if (r.status === 200) R.pwd = r.data.password ?? ''; else R.error = true;
  render();
}

const dlg = (cls, titleId, ...kids) => h('div', { class: 'overlay', onkeydown: e => { if (e.key === 'Escape') closeConnDialogs(); } },
  h('div', { class: 'dialog ' + cls, role: 'dialog', 'aria-modal': 'true', 'aria-labelledby': titleId }, kids));

const CONN_FIELDS = ['name', 'folder', 'protocol', 'host', 'port', 'user', 'password', 'tags'];

function connFormDialog() {
  const F = S.connForm, v = F.v, edit = F.id != null;
  const first = CONN_FIELDS.find(k => F.err[k]) || 'name';
  const fld = (key, label, o = {}) => {
    const input = h(o.select ? 'select' : 'input', {
      id: 'cf-' + key, type: o.type || 'text', autocomplete: o.type === 'password' ? 'new-password' : 'off', spellcheck: 'false', class: o.cls,
      placeholder: o.ph, 'data-focus': key === first, value: o.select ? null : v[key],
      oninput: e => { v[key] = e.target.value; o.onInput?.(e.target.value); },
    });
    if (o.select) { for (const [val, text] of o.select) input.append(h('option', { value: val }, text)); input.value = v[key]; }
    const msg = F.err[key] ? (key === 'port' ? t('badPort') : t('badField')) : o.hint;
    return h('div', { class: 'field' + (o.cls2 ? ' ' + o.cls2 : '') + (F.err[key] ? ' err' : '') }, h('label', { class: 'label', for: 'cf-' + key }, label), h('div', { class: 'row' }, input),
      msg && h('div', { class: 'hint' }, msg));
  };
  const saveBtn = h('button', { class: 'btn primary', type: 'submit' }, t('save'));
  const submit = async () => {
    if (saveBtn.disabled) return;
    saveBtn.disabled = true;
    const body = { ...v, name: v.name.trim(), folder: v.folder.trim(), host: v.host.trim(), user: v.user.trim(), tags: v.tags.trim(), port: parseInt(v.port, 10) };
    if (edit) { if (F.clear) body.password = ''; else if (v.password) body.password = v.password; else delete body.password; }
    const r = await api(edit ? `/connections/${F.id}/update` : '/connections', body);
    if (r.status === 401) { S.connForm = null; return sessionLost(); }
    if (r.status === 200) { S.connForm = null; await loadConns(); return render(); }
    F.err = r.status === 400 && r.data.field ? { [r.data.field]: true } : { form: true };
    render();
  };
  const port = fld('port', t('fPort'), { cls: 'mono', cls2: 'p', onInput: () => { F.portTouched = true; } });
  const proto = fld('protocol', t('fProto'), {
    select: [['rdp', 'RDP'], ['ssh', 'SSH'], ['vnc', 'VNC']],
    onInput: val => { if (!F.portTouched) { v.port = { ssh: '22', vnc: '5900' }[val] || '3389'; document.getElementById('cf-port').value = v.port; } },
  });
  return dlg('wide', 'cf-title',
    h('div', { class: 'title', id: 'cf-title' }, t(edit ? 'editConn' : 'newConn')),
    h('form', { class: 'stack', onsubmit: e => { e.preventDefault(); submit(); } },
      fld('name', t('fName')),
      fld('folder', t('fFolder'), { hint: t('folderHint') }),
      proto,
      h('div', { class: 'row2' }, fld('host', t('fHost'), { cls: 'mono', cls2: 'grow' }), port),
      fld('user', t('user')),
      fld('password', t('password'), { type: 'password', ph: edit ? t('passKeep') : '' }),
      edit && h('label', { class: 'check' }, h('input', { type: 'checkbox', onchange: e => { F.clear = e.target.checked; document.getElementById('cf-password').disabled = F.clear; } }), t('passClear')),
      fld('tags', t('fTags'), { hint: t('tagsHint') }),
      F.err.form && note('err', t('netErr')),
      h('div', { class: 'actions' }, btn(t('cancel'), closeConnDialogs), saveBtn)));
}

function connRevealDialog() {
  const R = S.reveal, shown = R.pwd != null;
  return dlg('', 'rv-title', h('span', { class: 'ic' }, '!'), h('div', { class: 'title', id: 'rv-title' }, shown ? R.name : t('revealT')),
    shown ? h('div', { class: 'revealed mono' }, R.pwd || t('noPwd')) : [h('div', { class: 'sub' }, t('revealS')(R.name)), R.error && note('err', t('revealErr'))],
    h('div', { class: 'actions' }, shown ? h('button', { class: 'btn primary', type: 'button', 'data-focus': true, onclick: closeConnDialogs }, t('close'))
      : [btn(t('cancel'), closeConnDialogs), h('button', { class: 'btn primary', type: 'button', 'data-focus': true, onclick: revealPwd }, t('revealGo'))]));
}

function connDelDialog() {
  const D = S.connDel;
  return dlg('', 'dl-title', h('span', { class: 'ic' }, '!'), h('div', { class: 'title', id: 'dl-title' }, t('delT')), h('div', { class: 'sub' }, t('delS')(D.name)),
    h('div', { class: 'actions' }, btn(t('cancel'), closeConnDialogs),
      h('button', { class: 'btn danger', type: 'button', 'data-focus': true, onclick: async () => { const id = D.id; S.connDel = null; await connAct(`/${id}/delete`, {}); } }, t('remove'))));
}

const connDialogs = () => S.connForm ? connFormDialog() : S.reveal ? connRevealDialog() : S.connDel ? connDelDialog() : null;

function connsPage() {
  const matches = () => {
    const q = S.connFilter.trim().toLowerCase();
    return S.conns.filter(c => !q || [c.name, c.host, c.folder, c.tags].some(x => (x || '').toLowerCase().includes(q)));
  };
  const row = c => {
    const rb = (text, fn, cls) => h('button', { class: 'btn ' + (cls || ''), type: 'button', onclick: fn, 'aria-label': `${text}: ${c.name}` }, text);
    return h('tr', { class: c.disabled ? 'off' : '' },
      h('td', {}, h('div', { class: 'who' }, c.name), c.folder && h('div', { class: 'meta' }, c.folder)),
      h('td', {}, h('span', { class: 'mono' }, `${c.host}:${c.port}`)),
      h('td', {}, h('span', { class: 'chip info' }, String(c.protocol).toUpperCase())),
      h('td', {}, c.user || '—'),
      h('td', {}, h('span', { class: 'chip ' + (c.disabled ? 'warn' : 'ok') }, t(c.disabled ? 'disabledS' : 'active'))),
      h('td', { class: 'act' }, h('div', { class: 'ctl tight end' },
        rb(t('edit'), () => openConnForm(c)),
        rb(t('pwBtn'), () => { S.reveal = { id: c.id, name: c.name, pwd: null, error: false }; render(); }),
        rb(t(c.disabled ? 'enable' : 'disable'), () => connAct(`/${c.id}/disable`, { disabled: !c.disabled })),
        rb(t('remove'), () => { S.connDel = { id: c.id, name: c.name }; render(); }, 'danger-o'))));
  };
  const area = h('div');
  const draw = () => {
    const list = matches();
    area.replaceChildren(S.conns.length === 0 ? h('div', { class: 'empty' }, t('connsEmpty'))
      : list.length === 0 ? h('div', { class: 'empty' }, t('connsNone'))
      : h('div', { class: 'tblwrap' }, h('table', { class: 'tbl' },
          h('thead', {}, h('tr', {}, [t('colName'), t('colAddr'), t('colProto'), t('user'), t('colState'), ''].map(c => h('th', {}, c)))),
          h('tbody', {}, list.map(row)))));
  };
  draw();
  return [
    h('h1', {}, t('nav')[3]), h('div', { class: 'sub' }, t('connsS')),
    h('div', { class: 'toolbar' },
      h('div', { class: 'field grow' }, h('label', { class: 'label sr', for: 'conn-q' }, t('search')),
        h('div', { class: 'row' }, h('input', { id: 'conn-q', type: 'search', placeholder: t('search'), autocomplete: 'off', value: S.connFilter, oninput: e => { S.connFilter = e.target.value; draw(); } }))),
      btn(t('newConn'), () => openConnForm(null), true)),
    area,
  ];
}

async function newCode() {
  const r = await api('/pairing/new', {});
  if (r.status === 401) return sessionLost();
  S.code = r.data;
  render();
}

async function decide(id, action, body) {
  const r = await api(`/pcs/${id}/${action}`, body || {});
  if (r.status === 401) return sessionLost();
  delete S.sel[id];
  await loadPcs();
  render();
}

/// scelta di permesso per un PC: tiene quello che l'amministratore sta impostando anche se l'elenco si aggiorna da solo
const selection = p => S.sel[p.id] ??= { permission: p.state === 'pending' ? 'read' : p.permission, noCopy: p.noCopy };

function permControls(p) {
  const s = selection(p);
  const sel = h('select', { class: 'perm', 'aria-label': t('colPerm'), onchange: e => { s.permission = e.target.value; render(); } },
    h('option', { value: 'read' }, t('permRead')), h('option', { value: 'requests' }, t('permReq')), h('option', { value: 'write' }, t('permWrite')));
  sel.value = s.permission;
  const box = h('label', { class: 'check' }, h('input', { type: 'checkbox', checked: s.noCopy, onchange: e => { s.noCopy = e.target.checked; render(); } }), t('noCopy'));
  return [sel, box];
}

/// stato della sincronizzazione comunicato dal PC (shareState on | off | unknown); "non copiare" deciso dall'amministratore ha la precedenza sull'interruttore del PC
function syncChip(p) {
  if (p.noCopy) return h('span', { class: 'chip info' }, t('syncBlocked'));
  if (p.shareState === 'off') return [h('span', { class: 'chip warn' }, t('syncOff')), h('div', { class: 'meta' }, dt(p.shareChangedAt))];
  if (p.shareState === 'on') return h('span', { class: 'chip ok' }, t('syncOn'));
  return h('span', { class: 'meta' }, t('syncUnknown'));
}

function pcsPage() {
  const pending = S.pcs.filter(p => p.state === 'pending');
  const others = S.pcs.filter(p => p.state !== 'pending');
  const name = p => `${p.pc} - ${p.user}`;
  const pendingCards = pending.map(p => {
    const s = selection(p);
    return h('div', { class: 'card' },
      h('div', { class: 'who' }, name(p)),
      h('div', { class: 'meta mono' }, `${p.address || ''} · ${t('requestedAt')} ${dt(p.requestedAt)}${p.appVersion ? ' · v' + p.appVersion : ''}`),
      h('div', { class: 'ctl' }, permControls(p),
        h('span', { class: 'grow' }),
        btn(t('reject'), () => decide(p.id, 'reject')),
        btn(t('approve'), () => decide(p.id, 'approve', { permission: s.permission, noCopy: s.noCopy }), true)));
  });
  const rows = others.map(p => {
    const s = selection(p);
    const approved = p.state === 'approved';
    const dirty = approved && (s.permission !== p.permission || s.noCopy !== p.noCopy);
    return h('tr', {},
      h('td', {}, h('div', { class: 'who' }, name(p)), h('div', { class: 'meta mono' }, p.address || '')),
      h('td', {}, chip(p.state)),
      h('td', {}, approved ? h('div', { class: 'ctl tight' }, permControls(p)) : '—'),
      h('td', {}, approved ? dt(p.lastSeen) : '—'),
      h('td', {}, approved ? syncChip(p) : '—'),
      h('td', { class: 'act' }, approved && h('div', { class: 'ctl tight end' }, [dirty && btn(t('save'), () => decide(p.id, 'update', { permission: s.permission, noCopy: s.noCopy }), true),
        h('button', { class: 'btn danger-o', type: 'button', onclick: () => { S.confirm = { id: p.id, name: name(p) }; render(); } }, t('disconnect'))])));
  });
  return [
    h('h1', {}, t('pcsT')), h('div', { class: 'sub' }, t('pcsS')),
    pending.length > 0 && [h('h2', {}, `${t('pendingT')} (${pending.length})`), h('div', { class: 'cards' }, pendingCards)],
    h('h2', {}, t('allPcs')),
    others.length === 0 && pending.length === 0 ? h('div', { class: 'empty' }, t('pcsEmpty'))
      : others.length > 0 && h('div', { class: 'tblwrap' }, h('table', { class: 'tbl' }, h('thead', {}, h('tr', {}, [t('colPc'), t('colState'), t('colPerm'), t('colSeen'), t('colSync'), ''].map(c => h('th', {}, c)))), h('tbody', {}, rows))),
  ];
}

function pairPage() {
  const me = S.me || {};
  const left = S.code ? new Date(S.code.expires) - Date.now() : 0;
  const mmss = ms => { const s = Math.max(0, Math.ceil(ms / 1000)); return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`; };
  const timer = h('div', { class: 'timer' }, left > 0 ? t('expiresIn')(mmss(left)) : t('expired'));
  if (S.code && left > 0) {
    S.tick = setInterval(() => {
      const l = new Date(S.code.expires) - Date.now();
      if (l <= 0) { S.code = null; return render(); }
      timer.textContent = t('expiresIn')(mmss(l));
    }, 1000);
  }
  const active = S.code && left > 0;
  return [
    h('h1', {}, t('pairT')), h('div', { class: 'sub' }, t('pairS')),
    active ? h('div', { class: 'codebox' }, h('div', { class: 'bigcode', id: 'pair-code' }, S.code.code.slice(0, 3) + '-' + S.code.code.slice(3)), timer) : note('info', t('noCode')),
    h('div', { class: 'actions left' }, btn(active ? t('newCode') : t('genCode'), newCode, !active), active && h('span', { class: 'hint' }, t('pairNew'))),
    h('h2', {}, t('address')),
    kv([[t('address'), h('span', {}, (me.urls || []).map(u => h('div', {}, u))), true], [t('fingerprint'), colonFp(me.fingerprint || ''), true]]),
    h('ol', { class: 'steps' }, t('pairHow').map(s => h('li', {}, s))),
  ];
}

function overviewPage() {
  const me = S.me || {};
  return [
    h('h1', {}, t('overview')), h('div', { class: 'sub' }, t('overviewS')),
    h('div', { class: 'tiles' },
      h('button', { class: 'tile', type: 'button', onclick: () => openPage('pcs') }, h('span', {}, t('connected')), h('b', { class: 'ok' }, S.pcs.filter(p => p.state === 'approved').length)),
      h('button', { class: 'tile', type: 'button', onclick: () => openPage('pcs') }, h('span', {}, t('waiting')), h('b', { class: pendingCount() ? 'warn' : '' }, pendingCount())),
      h('button', { class: 'tile', type: 'button', onclick: () => openPage('conns') }, h('span', {}, t('nav')[3]), h('b', {}, S.conns.filter(c => !c.disabled).length)),
      h('button', { class: 'tile', type: 'button', onclick: () => openPage('conflicts') }, h('span', {}, t('ovConflicts')), h('b', { class: conflictCount() ? 'warn' : '' }, conflictCount())),
      h('button', { class: 'tile', type: 'button', onclick: () => openPage('trash') }, h('span', {}, t('ovTrash')), h('b', {}, S.trash.length))),
    note('info', t('soon')),
    h('h2', {}, t('serverStatus')),
    kv([[t('address'), h('span', {}, (me.urls || []).map(u => h('div', {}, u))), true], [t('fingerprint'), colonFp(me.fingerprint || ''), true], [t('dbShort'), me.dbDir || '', true]]),
  ];
}

function screenC() {
  const out = async () => { await api('/logout', {}); await loadState(); go('B1'); };
  const body = { overview: overviewPage, pcs: pcsPage, pair: pairPage, conns: connsPage, requests: requestsPage, proposals: proposalsPage, conflicts: conflictsPage, trash: trashPage, events: eventsPage, sessions: sessionsPage, shared: sharedPage, admins: adminsPage }[S.page]();
  const items = t('nav').map((n, i) => PAGES[i]
    ? h('button', { class: 'item link' + (S.page === PAGES[i] ? ' on' : ''), type: 'button', onclick: () => openPage(PAGES[i]) }, n,
        PAGES[i] === 'pcs' && pendingCount() > 0 && h('span', { class: 'badge' }, pendingCount()),
        PAGES[i] === 'requests' && reqCount() > 0 && h('span', { class: 'badge' }, reqCount()),
        PAGES[i] === 'proposals' && persCount() > 0 && h('span', { class: 'badge' }, persCount()),
        PAGES[i] === 'conflicts' && conflictCount() > 0 && h('span', { class: 'badge' }, conflictCount()),
        PAGES[i] === 'trash' && S.trash.length > 0 && h('span', { class: 'badge' }, S.trash.length))
    : h('div', { class: 'item soon' }, n));
  items.push(h('button', { class: 'item link' + (S.page === 'shared' ? ' on' : ''), type: 'button', onclick: () => openPage('shared') }, t('shNav')));
  items.push(h('button', { class: 'item link' + (S.page === 'admins' ? ' on' : ''), type: 'button', onclick: () => openPage('admins') }, t('admNav')));
  const dialog = S.confirm && h('div', { class: 'overlay' }, h('div', { class: 'dialog', role: 'dialog', 'aria-modal': 'true' },
    h('span', { class: 'ic' }, '!'), h('div', { class: 'title' }, t('confirmT')), h('div', { class: 'sub' }, t('confirmS')(S.confirm.name)),
    h('div', { class: 'actions' }, btn(t('cancel'), () => { S.confirm = null; render(); }),
      h('button', { class: 'btn danger', type: 'button', onclick: async () => { const id = S.confirm.id; S.confirm = null; await decide(id, 'revoke'); } }, t('disconnect')))));
  return h('div', { class: 'shell' }, topbar(t('console'), h('button', { class: 'chip-btn', type: 'button', onclick: out }, `${S.me?.user || 'admin'} · ${t('exit')}`)),
    h('div', { class: 'layout' },
      h('div', { class: 'nav' }, h('div', { class: 'brand' }, h('img', { class: 'logo', src: '/logo.svg', alt: '' }), 'Server'), items),
      h('div', { class: 'main' }, h('div', { class: 'body' }, h('div', { class: 'col wide' }, body)))),
    dialog, connDialogs(), shDialogs(), trashDialog(), admDelDialog());
}

// ---- amministratori (Admins.cs): tutti uguali, ognuno ne crea altri ed elimina chiunque tranne se stesso ------------

Object.assign(I18N.it, {
  admNav: 'Amministratori', admS: 'Chi può entrare in questa console. Tutti hanno gli stessi permessi: ognuno può creare altri amministratori ed eliminare chiunque tranne se stesso e l\'amministratore principale.',
  admYou: '(tu)', admCreated: 'Creato il', admBy: 'Creato da', admSetup: 'creazione guidata', admNew: 'Nuovo amministratore', admCreate: 'Crea amministratore',
  admUserBad: 'Usa da 1 a 64 caratteri tra lettere, numeri e . _ - @ (niente spazi).', admExists: 'Esiste già un amministratore con questo nome.',
  admDelT: 'Eliminare l\'amministratore?', admDelS: n => `Eliminare l'amministratore ${n}? Non potrà più accedere alla console.`, admOk: n => `Amministratore ${n} creato.`,
  admSelf: 'Non puoi eliminare te stesso: chiedi a un altro amministratore.', admLast: 'È l\'ultimo amministratore: non si può eliminare.',
  admMain: 'principale', admMainNo: 'L\'amministratore principale non si può eliminare.',
});
Object.assign(I18N.en, {
  admNav: 'Admins', admS: 'Who can sign in to this console. Everyone has the same permissions: each admin can create other admins and delete anyone except themselves and the main administrator.',
  admYou: '(you)', admCreated: 'Created on', admBy: 'Created by', admSetup: 'setup wizard', admNew: 'New administrator', admCreate: 'Create administrator',
  admUserBad: 'Use 1 to 64 characters among letters, digits and . _ - @ (no spaces).', admExists: 'An administrator with this name already exists.',
  admDelT: 'Delete the administrator?', admDelS: n => `Delete the administrator ${n}? They will no longer be able to sign in to the console.`, admOk: n => `Administrator ${n} created.`,
  admSelf: 'You cannot delete yourself: ask another administrator.', admLast: 'This is the last administrator: it cannot be deleted.',
  admMain: 'main', admMainNo: 'The main administrator cannot be deleted.',
});
S.admins = []; S.admDel = null; S.admMsg = null;

async function loadAdmins() {
  const r = await api('/admins');
  if (r.status === 401) return sessionLost();
  if (r.status === 200 && Array.isArray(r.data)) S.admins = r.data;
}

async function deleteAdmin(user) {
  S.admDel = null;
  const r = await api(`/admins/${encodeURIComponent(user)}/delete`, {});
  if (r.status === 401) return sessionLost();
  S.admMsg = r.status === 200 ? null : ['err', r.status === 403 ? t(r.data.error === 'main' ? 'admMainNo' : 'admSelf') : r.status === 409 ? t('admLast') : t('netErr')];
  await loadAdmins();
  render();
}

const admDelDialog = () => S.admDel && h('div', { class: 'overlay', onkeydown: e => { if (e.key === 'Escape') { S.admDel = null; render(); } } },
  h('div', { class: 'dialog', role: 'dialog', 'aria-modal': 'true', 'aria-labelledby': 'ad-title' },
    h('span', { class: 'ic' }, '!'), h('div', { class: 'title', id: 'ad-title' }, t('admDelT')), h('div', { class: 'sub' }, t('admDelS')(S.admDel)),
    h('div', { class: 'actions' }, btn(t('cancel'), () => { S.admDel = null; render(); }),
      h('button', { class: 'btn danger', type: 'button', 'data-focus': true, onclick: () => deleteAdmin(S.admDel) }, t('remove')))));

function adminsPage() {
  const meter = h('div', { class: 'meter' }), meterLabel = h('div', { class: 'hint' });
  const updateMeter = () => { // come nella creazione guidata
    const p = S.f.aPwd || '', n = strength(p), tone = n < 2 ? 'err' : n < 4 ? 'warn' : 'ok';
    meter.replaceChildren(...[0, 1, 2, 3].map(i => h('i', { class: i < n ? tone : '' })));
    meterLabel.textContent = p ? `${t('strengthL')}: ${t('strength')[n].toLowerCase()} · ${t('chars')(p.length)}` : '';
  };
  const submit = async () => {
    const user = (S.f.aUser || '').trim(), p = S.f.aPwd || '', e = {};
    if (!/^[\p{L}\p{N}._@-]{1,64}$/u.test(user)) e.aUser = user ? t('admUserBad') : t('userEmpty');
    if (p.length < 10) e.aPwd = t('pwShort');
    else if (p !== (S.f.aPwd2 || '')) e.aPwd2 = t('pwDiff');
    S.err = e; S.admMsg = null;
    if (Object.keys(e).length) return render();
    const r = await api('/admins', { user, password: p });
    if (r.status === 401) return sessionLost();
    if (r.status === 200) { S.f.aUser = S.f.aPwd = S.f.aPwd2 = ''; S.admMsg = ['ok', t('admOk')(user)]; await loadAdmins(); return render(); }
    const err = r.data.error;
    S.err = err === 'user' ? { aUser: t('admUserBad') } : err === 'exists' ? { aUser: t('admExists') } : err === 'password' ? { aPwd: t('pwShort') } : {};
    if (!Object.keys(S.err).length) S.admMsg = ['err', t('netErr')];
    render();
  };
  const node = [
    h('h1', {}, t('admNav')), h('div', { class: 'sub' }, t('admS')),
    S.admMsg && note(S.admMsg[0], S.admMsg[1]),
    h('div', { class: 'tblwrap' }, h('table', { class: 'tbl' },
      h('thead', {}, h('tr', {}, [t('user'), t('admCreated'), t('admBy'), ''].map(c => h('th', {}, c)))),
      h('tbody', {}, S.admins.map(a => h('tr', {},
        h('td', {}, h('span', { class: 'who' }, a.user), a.me && [' ', h('span', { class: 'meta' }, t('admYou'))], a.main && [' ', h('span', { class: 'chip info' }, t('admMain'))]),
        h('td', {}, dt(a.createdAt)), h('td', {}, a.createdBy || t('admSetup')),
        h('td', { class: 'act' }, !a.me && !a.main && h('div', { class: 'ctl tight end' },
          h('button', { class: 'btn danger-o', type: 'button', 'aria-label': `${t('remove')}: ${a.user}`, onclick: () => { S.admDel = a.user; render(); } }, t('remove'))))))))),
    h('h2', {}, t('admNew')),
    h('form', { class: 'stack adm-form', onsubmit: e => { e.preventDefault(); submit(); } },
      field(t('user'), 'adm-user', 'aUser', { cls: 'mid' }),
      field(t('password'), 'adm-pwd', 'aPwd', { type: 'password', cls: 'mid', onInput: updateMeter }),
      h('div', {}, meter, meterLabel),
      field(t('confirm'), 'adm-pwd2', 'aPwd2', { type: 'password', cls: 'mid' }),
      h('div', { class: 'actions left' }, h('button', { class: 'btn primary', type: 'submit' }, t('admCreate')))),
  ];
  updateMeter();
  return node;
}

// ---- conflitti, cestino, registro attivita' ----------------------------------------------------------------------

const EV_KINDS = ['code-created', 'pair-request', 'pair-cancelled', 'key-acked', 'approve', 'reject', 'revoke', 'update',
  'admin-created', 'admin-deleted', 'admins-migrated', 'connection-created', 'connection-updated', 'connection-deleted',
  'connection-restored', 'connection-purged', 'trash-emptied', 'password-shown', 'sync', 'request-created', 'request-approved',
  'request-rejected', 'personal-received', 'personal-published', 'personal-approved', 'personal-rejected', 'personal-removed',
  'conflict-recorded', 'conflict-kept-server', 'conflict-used-pc', 'sync-setting',
  'device-created', 'device-updated', 'device-deleted', 'fcred-created', 'fcred-updated', 'fcred-deleted', 'folder-created', 'folder-updated', 'folder-deleted', 'folder-renamed', 'copy'];

Object.assign(I18N.it, {
  colSync: 'Sincronizzazione', syncOn: 'Attiva', syncOff: 'Disattivata dal PC', syncUnknown: 'Non ancora nota', syncBlocked: 'Bloccata dall\'amministratore',
  ovConflicts: 'Conflitti in sospeso', ovTrash: 'Nel cestino',
  cfS: 'La versione proposta da un PC quando la voce era già cambiata sul server. Scegli quale tenere.',
  cfNone: 'Nessun conflitto in sospeso. Un conflitto avviene quando due PC modificano la stessa voce insieme: vince la versione sul server e il PC riceve un avviso per risincronizzare.',
  cfServer: 'Sul server', cfPc: 'Dal PC', cfKeep: 'Tieni versione server', cfUse: 'Usa versione del PC',
  cfGone: 'La voce non è più tra le condivise (è nel cestino): si può solo tenere la versione del server.',
  cfStates: { pending: 'In sospeso', 'kept-server': 'Tenuta quella del server', 'used-pc': 'Usata quella del PC' },
  cfDecided: 'Già decisi', cfDecEmpty: 'Nessun conflitto deciso.',
  trashS: 'Le connessioni condivise eliminate. Spariscono dai PC ma restano qui finché non le ripristini o le elimini davvero.',
  trashEmpty: 'Il cestino è vuoto.', restore: 'Ripristina', purge: 'Elimina definitivamente', emptyTrash: 'Svuota il cestino',
  colDeleted: 'Eliminata', byAdmin: 'Amministratore',
  purgeT: 'Eliminare definitivamente?', purgeS: n => `${n} verrà eliminata per sempre dal server, insieme alla sua password. Non si può annullare.`,
  emptyT: 'Svuotare il cestino?', emptyS: n => `Tutte le ${n} voci nel cestino verranno eliminate per sempre. Non si può annullare.`,
  evS: 'Gli eventi registrati dal server, dal più recente. Le password non compaiono mai.', evEmpty: 'Nessun evento.',
  evAll: 'Tutti', evKind: 'Tipo di evento', evPc: 'PC', evMore: 'Mostra altri', evShown: (n, tot) => `${n} di ${tot}`, colEvent: 'Evento', colDetail: 'Dettaglio',
  evKinds: {
    'code-created': 'Codice di abbinamento creato', 'pair-request': 'Richiesta di abbinamento', 'pair-cancelled': 'Abbinamento annullato', 'key-acked': 'Chiave ricevuta dal PC',
    approve: 'PC approvato', reject: 'PC rifiutato', revoke: 'PC scollegato', update: 'Permesso del PC cambiato',
    'admin-created': 'Amministratore creato', 'admin-deleted': 'Amministratore eliminato', 'admins-migrated': 'Amministratori aggiornati',
    'connection-created': 'Connessione creata', 'connection-updated': 'Connessione modificata', 'connection-deleted': 'Connessione eliminata (nel cestino)',
    'connection-restored': 'Connessione ripristinata', 'connection-purged': 'Connessione eliminata definitivamente', 'trash-emptied': 'Cestino svuotato',
    'connection-disabled': 'Connessione disabilitata', 'connection-enabled': 'Connessione riabilitata',
    'password-shown': 'Password mostrata', sync: 'Sincronizzazione', 'request-created': 'Richiesta inviata', 'request-approved': 'Richiesta approvata', 'request-rejected': 'Richiesta rifiutata',
    'personal-received': 'Proposta ricevuta', 'personal-published': 'Proposta pubblicata', 'personal-approved': 'Proposta approvata', 'personal-rejected': 'Proposta rifiutata', 'personal-removed': 'Copie personali rimosse',
    'conflict-recorded': 'Conflitto di scrittura', 'conflict-kept-server': 'Conflitto: tenuta la versione server', 'conflict-used-pc': 'Conflitto: usata la versione del PC',
    'sync-setting': 'Sincronizzazione cambiata dal PC',
    'device-created': 'Dispositivo creato', 'device-updated': 'Dispositivo modificato', 'device-deleted': 'Dispositivo eliminato',
    'fcred-created': 'Credenziale di cartella creata', 'fcred-updated': 'Credenziale di cartella modificata', 'fcred-deleted': 'Credenziale di cartella eliminata',
    'folder-created': 'Cartella creata', 'folder-updated': 'Cartella modificata', 'folder-deleted': 'Cartella eliminata', 'folder-renamed': 'Cartella rinominata', copy: 'Credenziale copiata',
  },
});
Object.assign(I18N.en, {
  colSync: 'Sync', syncOn: 'On', syncOff: 'Turned off by the PC', syncUnknown: 'Not known yet', syncBlocked: 'Blocked by the administrator',
  ovConflicts: 'Pending conflicts', ovTrash: 'In the recycle bin',
  cfS: 'The version a PC proposed when the entry had already changed on the server. Choose which one to keep.',
  cfNone: 'No pending conflicts. A conflict happens when two PCs change the same entry together: the server version wins and the PC gets a notice to resync.',
  cfServer: 'On the server', cfPc: 'From the PC', cfKeep: 'Keep server version', cfUse: 'Use PC version',
  cfGone: 'The entry is no longer shared (it is in the recycle bin): you can only keep the server version.',
  cfStates: { pending: 'Pending', 'kept-server': 'Server version kept', 'used-pc': 'PC version used' },
  cfDecided: 'Already decided', cfDecEmpty: 'No decided conflicts.',
  trashS: 'The deleted shared connections. They disappear from the PCs but stay here until you restore or permanently delete them.',
  trashEmpty: 'The recycle bin is empty.', restore: 'Restore', purge: 'Delete permanently', emptyTrash: 'Empty the recycle bin',
  colDeleted: 'Deleted', byAdmin: 'Administrator',
  purgeT: 'Delete permanently?', purgeS: n => `${n} will be deleted from the server forever, together with its password. This cannot be undone.`,
  emptyT: 'Empty the recycle bin?', emptyS: n => `All ${n} entries in the recycle bin will be deleted forever. This cannot be undone.`,
  evS: 'The events recorded by the server, most recent first. Passwords never appear.', evEmpty: 'No events.',
  evAll: 'All', evKind: 'Event type', evPc: 'PC', evMore: 'Show more', evShown: (n, tot) => `${n} of ${tot}`, colEvent: 'Event', colDetail: 'Detail',
  evKinds: {
    'code-created': 'Pairing code created', 'pair-request': 'Pairing request', 'pair-cancelled': 'Pairing cancelled', 'key-acked': 'Key received by the PC',
    approve: 'PC approved', reject: 'PC rejected', revoke: 'PC disconnected', update: 'PC permission changed',
    'admin-created': 'Admin created', 'admin-deleted': 'Admin deleted', 'admins-migrated': 'Admins upgraded',
    'connection-created': 'Connection created', 'connection-updated': 'Connection changed', 'connection-deleted': 'Connection deleted (to recycle bin)',
    'connection-restored': 'Connection restored', 'connection-purged': 'Connection permanently deleted', 'trash-emptied': 'Recycle bin emptied',
    'connection-disabled': 'Connection disabled', 'connection-enabled': 'Connection re-enabled',
    'password-shown': 'Password shown', sync: 'Sync', 'request-created': 'Request submitted', 'request-approved': 'Request approved', 'request-rejected': 'Request rejected',
    'personal-received': 'Proposal received', 'personal-published': 'Proposal published', 'personal-approved': 'Proposal approved', 'personal-rejected': 'Proposal rejected', 'personal-removed': 'Personal copies removed',
    'conflict-recorded': 'Write conflict', 'conflict-kept-server': 'Conflict: server version kept', 'conflict-used-pc': 'Conflict: PC version used',
    'sync-setting': 'Sync setting changed by the PC',
    'device-created': 'Device created', 'device-updated': 'Device changed', 'device-deleted': 'Device deleted',
    'fcred-created': 'Folder credential created', 'fcred-updated': 'Folder credential changed', 'fcred-deleted': 'Folder credential deleted',
    'folder-created': 'Folder created', 'folder-updated': 'Folder changed', 'folder-deleted': 'Folder deleted', 'folder-renamed': 'Folder renamed', copy: 'Credential copied',
  },
});

async function loadConflicts() {
  const r = await api('/conflicts');
  if (r.status === 401) { await sessionLost(); return false; }
  if (r.status !== 200 || !Array.isArray(r.data)) return false;
  const changed = JSON.stringify(r.data) !== JSON.stringify(S.conflicts);
  S.conflicts = r.data;
  return changed;
}

async function resolveConflict(id, action) {
  const r = await api(`/conflicts/${id}/${action}`, {});
  if (r.status === 401) return sessionLost();
  if (r.status === 200) delete S.cfErr[id]; else S.cfErr[id] = t('netErr');
  await loadConflicts(); await loadConns();
  render();
}

const CF_FIELDS = [['name', 'fName'], ['folder', 'fFolder'], ['protocol', 'fProto'], ['host', 'fHost'], ['port', 'fPort'], ['user', 'user'], ['tags', 'fTags']];

function cfDiff(q) {
  const val = (v, k) => !v ? '—' : k === 'protocol' ? String(v[k]).toUpperCase() : (v[k] === '' || v[k] == null ? '—' : String(v[k]));
  const rows = CF_FIELDS.map(([k, label]) => {
    const s = val(q.server, k), p = val(q.client, k);
    return h('tr', { class: q.server && s !== p ? 'chg' : '' }, h('td', {}, t(label)), h('td', { class: 'mono' }, s), h('td', { class: 'mono' }, p));
  });
  // la password non si mostra mai: solo se cambia
  rows.push(h('tr', { class: q.pwChanged ? 'chg' : '' }, h('td', {}, t('password')), h('td', { class: 'mono' }, q.server ? '•••' : '—'), h('td', { class: 'mono' }, t(q.pwChanged ? 'pwChanged' : 'pwSame'))));
  return h('div', { class: 'tblwrap' }, h('table', { class: 'tbl diff' },
    h('thead', {}, h('tr', {}, [t('colField'), t('cfServer'), t('cfPc')].map(c => h('th', {}, c)))), h('tbody', {}, rows)));
}

function conflictsPage() {
  const open = S.conflicts.filter(q => q.state === 'pending');
  const done = S.conflicts.filter(q => q.state !== 'pending');
  const card = q => h('div', { class: 'card' },
    h('div', { class: 'ctl' }, h('span', { class: 'chip warn' }, t('kinds').update), h('span', { class: 'who' }, q.client.name), h('span', { class: 'grow' }),
      h('span', { class: 'meta' }, `${q.pc} · ${dt(q.createdAt)}`)),
    q.gone && note('warn', t('cfGone')),
    cfDiff(q),
    S.cfErr[q.id] && note('err', S.cfErr[q.id]),
    h('div', { class: 'ctl end' },
      btn(t('cfUse'), () => resolveConflict(q.id, 'use-pc'), false, q.gone),
      btn(t('cfKeep'), () => resolveConflict(q.id, 'keep-server'), true)));
  return [
    h('h1', {}, t('nav')[6]), h('div', { class: 'sub' }, t('cfS')),
    open.length ? h('div', { class: 'cards' }, open.map(card)) : note('info', t('cfNone')),
    done.length > 0 && [h('h2', {}, t('cfDecided')),
      h('div', { class: 'tblwrap' }, h('table', { class: 'tbl' },
        h('thead', {}, h('tr', {}, [t('colDate'), t('colPc'), t('colEntry'), t('colState')].map(c => h('th', {}, c)))),
        h('tbody', {}, done.map(q => h('tr', {},
          h('td', {}, dt(q.decidedAt)), h('td', {}, q.pc), h('td', {}, q.client.name),
          h('td', {}, h('span', { class: 'chip ' + (q.state === 'used-pc' ? 'ok' : 'info') }, t('cfStates')[q.state] || q.state)))))))],
  ];
}

async function loadTrash() {
  const r = await api('/trash');
  if (r.status === 401) { await sessionLost(); return false; }
  if (r.status !== 200 || !Array.isArray(r.data)) return false;
  const changed = JSON.stringify(r.data) !== JSON.stringify(S.trash);
  S.trash = r.data;
  return changed;
}

async function trashAct(path, body) {
  const r = await api('/trash' + path, body);
  if (r.status === 401) return sessionLost();
  await loadTrash(); await loadConns();
  render();
}

const trashDialog = () => S.trashConfirm && h('div', { class: 'overlay', onkeydown: e => { if (e.key === 'Escape') { S.trashConfirm = null; render(); } } },
  h('div', { class: 'dialog', role: 'dialog', 'aria-modal': 'true', 'aria-labelledby': 'tr-title' },
    h('span', { class: 'ic' }, '!'), h('div', { class: 'title', id: 'tr-title' }, t(S.trashConfirm.all ? 'emptyT' : 'purgeT')),
    h('div', { class: 'sub' }, S.trashConfirm.all ? t('emptyS')(S.trash.length) : t('purgeS')(S.trashConfirm.name)),
    h('div', { class: 'actions' }, btn(t('cancel'), () => { S.trashConfirm = null; render(); }),
      h('button', { class: 'btn danger', type: 'button', 'data-focus': true, onclick: async () => { const C = S.trashConfirm; S.trashConfirm = null; await trashAct(C.all ? '/empty' : `/${C.id}/purge`, {}); } },
        t(S.trashConfirm.all ? 'emptyTrash' : 'purge')))));

function trashPage() {
  const rows = S.trash.map(c => h('tr', {},
    h('td', {}, h('div', { class: 'who' }, c.name), c.folder && h('div', { class: 'meta' }, c.folder)),
    h('td', {}, h('span', { class: 'mono' }, `${c.host}:${c.port}`)),
    h('td', {}, h('span', { class: 'chip info' }, String(c.protocol).toUpperCase())),
    h('td', {}, c.user || '—'),
    h('td', {}, h('div', {}, dt(c.deletedAt)), h('div', { class: 'meta' }, c.by || t('byAdmin'))),
    h('td', { class: 'act' }, h('div', { class: 'ctl tight end' },
      btn(t('restore'), () => trashAct(`/${c.id}/restore`, {}), true),
      h('button', { class: 'btn danger-o', type: 'button', onclick: () => { S.trashConfirm = { id: c.id, name: c.name }; render(); } }, t('purge'))))));
  return [
    h('h1', {}, t('nav')[7]), h('div', { class: 'sub' }, t('trashS')),
    S.trash.length === 0 ? h('div', { class: 'empty' }, t('trashEmpty'))
      : [h('div', { class: 'ctl end' }, h('button', { class: 'btn danger-o', type: 'button', onclick: () => { S.trashConfirm = { all: true }; render(); } }, t('emptyTrash'))),
         h('div', { class: 'tblwrap' }, h('table', { class: 'tbl' },
           h('thead', {}, h('tr', {}, [t('colName'), t('colAddr'), t('colProto'), t('user'), t('colDeleted'), ''].map(c => h('th', {}, c)))),
           h('tbody', {}, rows)))],
  ];
}

async function loadEvents(reset) {
  if (reset) S.events = { items: [], total: 0, offset: 0 };
  const f = S.evFilter;
  const qs = `?limit=100&offset=${S.events.offset}` + (f.kind ? `&kind=${encodeURIComponent(f.kind)}` : '') + (f.pc ? `&pc=${encodeURIComponent(f.pc)}` : '');
  const r = await api('/events' + qs);
  if (r.status === 401) { await sessionLost(); return; }
  if (r.status !== 200) return;
  S.events.items = S.events.items.concat(r.data.items || []);
  S.events.total = r.data.total || 0;
  S.events.offset = S.events.items.length;
}

const evKind = k => t('evKinds')[k] || k;

function eventsPage() {
  const f = S.evFilter;
  const kindOpts = [['', t('evAll')], ...EV_KINDS.map(k => [k, evKind(k)])];
  const pcOpts = [['', t('evAll')], ...S.pcs.map(p => [p.id, `${p.pc} - ${p.user}`])];
  const sel = (id, value, opts, on) => {
    const s = h('select', { id, onchange: e => on(e.target.value) }, opts.map(([v, txt]) => h('option', { value: v }, txt)));
    s.value = value; return s;
  };
  return [
    h('h1', {}, t('nav')[8]), h('div', { class: 'sub' }, t('evS')),
    h('div', { class: 'toolbar' },
      h('div', { class: 'field' }, h('label', { class: 'label', for: 'ev-kind' }, t('evKind')), h('div', { class: 'row' }, sel('ev-kind', f.kind, kindOpts, async v => { f.kind = v; await loadEvents(true); render(); }))),
      h('div', { class: 'field' }, h('label', { class: 'label', for: 'ev-pc' }, t('evPc')), h('div', { class: 'row' }, sel('ev-pc', f.pc, pcOpts, async v => { f.pc = v; await loadEvents(true); render(); })))),
    S.events.items.length === 0 ? h('div', { class: 'empty' }, t('evEmpty'))
      : h('div', { class: 'tblwrap' }, h('table', { class: 'tbl' },
          h('thead', {}, h('tr', {}, [t('colDate'), t('colPc'), t('colEvent'), t('colDetail')].map(c => h('th', {}, c)))),
          h('tbody', {}, S.events.items.map(e => h('tr', {},
            h('td', { class: 'mono' }, dt(e.at)), h('td', {}, e.pc || t('byAdmin')), h('td', {}, evKind(e.kind)), h('td', {}, e.detail || '—')))))),
    S.events.items.length < S.events.total && h('div', { class: 'ctl' }, h('span', { class: 'meta' }, t('evShown')(S.events.items.length, S.events.total)),
      h('span', { class: 'grow' }), btn(t('evMore'), async () => { await loadEvents(false); render(); })),
  ];
}

// ---- collegamenti: durata delle sessioni verso le voci condivise (Sessions.cs) -----------------------------------

Object.assign(I18N.it, {
  sessS: 'Da quando e per quanto tempo ogni PC è stato collegato alle voci condivise (RDP, SSH, VNC). La password non compare mai.',
  sessEmpty: 'Nessun collegamento registrato. Compaiono qui quando un PC apre una voce condivisa.',
  sessByPc: 'Per PC', sessByConn: 'Per voce', sessDetail: 'Dettaglio',
  sessCount: 'Collegamenti', sessTotal: 'Tempo totale', sessLast: 'Ultimo collegamento', sessPcs: 'PC',
  colStart: 'Inizio', colEnd: 'Fine', colDuration: 'Durata', colStatus: 'Stato',
  sessStatus: { closed: 'Concluso', open: 'In corso', interrupted: 'Interrotto' },
  sessStaleNote: m => `Un collegamento ancora aperto è «in corso». Se il PC non si fa sentire da oltre ${m} minuti risulta «interrotto» e la fine è stimata dall'ultimo contatto.`,
  sessOngoing: 'in corso',
});
Object.assign(I18N.en, {
  sessS: 'Since when and for how long each PC has been connected to the shared entries (RDP, SSH, VNC). The password never appears.',
  sessEmpty: 'No connections recorded. They appear here when a PC opens a shared entry.',
  sessByPc: 'By PC', sessByConn: 'By entry', sessDetail: 'Detail',
  sessCount: 'Connections', sessTotal: 'Total time', sessLast: 'Last connection', sessPcs: 'PCs',
  colStart: 'Start', colEnd: 'End', colDuration: 'Duration', colStatus: 'Status',
  sessStatus: { closed: 'Closed', open: 'Ongoing', interrupted: 'Interrupted' },
  sessStaleNote: m => `A still-open connection is "ongoing". If the PC has not been seen for over ${m} minutes it is shown as "interrupted" and the end is estimated from the last contact.`,
  sessOngoing: 'ongoing',
});

async function loadSessions() {
  const r = await api('/sessions');
  if (r.status === 401) { await sessionLost(); return; }
  if (r.status === 200) S.sessions = r.data;
}

const dur = s => { s = Math.max(0, Math.round(s)); const h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60); return h ? `${h} h ${m} min` : m ? `${m} min` : `${s} s`; };
const sessChip = st => h('span', { class: 'chip ' + (st === 'open' ? 'ok' : st === 'interrupted' ? 'warn' : 'info') }, t('sessStatus')[st] || st);

function sessionsPage() {
  const d = S.sessions;
  const head = [h('h1', {}, t('nav')[9]), h('div', { class: 'sub' }, t('sessS'))];
  if (!d || (d.byPc.length === 0 && d.recent.length === 0)) return [...head, h('div', { class: 'empty' }, t('sessEmpty'))];
  const tab = (key, label) => h('button', { class: 'btn' + (S.sessView === key ? ' primary' : ''), type: 'button', onclick: () => { S.sessView = key; render(); } }, label);
  const toolbar = h('div', { class: 'ctl' }, tab('pc', t('sessByPc')), tab('conn', t('sessByConn')), tab('detail', t('sessDetail')));
  const nm = (name, id) => name || ('#' + (id || '').slice(0, 8));
  let table;
  if (S.sessView === 'pc')
    table = h('table', { class: 'tbl' },
      h('thead', {}, h('tr', {}, [t('colPc'), t('sessCount'), t('sessTotal'), t('sessLast')].map(c => h('th', {}, c)))),
      h('tbody', {}, d.byPc.map(x => h('tr', {},
        h('td', {}, x.pc),
        h('td', {}, String(x.count), x.open > 0 && [' ', h('span', { class: 'chip ok' }, x.open + ' ' + t('sessOngoing'))]),
        h('td', {}, dur(x.totalSeconds)), h('td', { class: 'mono' }, dt(x.lastAt))))));
  else if (S.sessView === 'conn')
    table = h('table', { class: 'tbl' },
      h('thead', {}, h('tr', {}, [t('colName'), t('sessPcs'), t('sessCount'), t('sessTotal'), t('sessLast')].map(c => h('th', {}, c)))),
      h('tbody', {}, d.byConn.map(x => h('tr', {},
        h('td', {}, nm(x.name, x.connId)),
        h('td', {}, String(x.pcCount)),
        h('td', {}, String(x.count), x.open > 0 && [' ', h('span', { class: 'chip ok' }, x.open + ' ' + t('sessOngoing'))]),
        h('td', {}, dur(x.totalSeconds)), h('td', { class: 'mono' }, dt(x.lastAt))))));
  else
    table = h('table', { class: 'tbl' },
      h('thead', {}, h('tr', {}, [t('colPc'), t('colName'), t('colProto'), t('colStart'), t('colEnd'), t('colDuration'), t('colStatus')].map(c => h('th', {}, c)))),
      h('tbody', {}, d.recent.map(x => h('tr', {},
        h('td', {}, x.pc), h('td', {}, nm(x.name, x.connId)), h('td', { class: 'mono' }, (x.protocol || '').toUpperCase()),
        h('td', { class: 'mono' }, dt(x.start)), h('td', { class: 'mono' }, x.end ? dt(x.end) : '—'),
        h('td', {}, dur(x.seconds)), h('td', {}, sessChip(x.status))))));
  return [...head, note('info', t('sessStaleNote')(d.staleMinutes)), toolbar, h('div', { class: 'tblwrap' }, table)];
}

// ---- credenziali e cartelle condivise (Shared.cs): cartelle, credenziali di cartella, password dei dispositivi. Le password non arrivano mai alla pagina ----

Object.assign(I18N.it, {
  shNav: 'Credenziali e cartelle', shS: 'Le cartelle condivise con dominio e prefisso IP, le credenziali di cartella e le password dei dispositivi che i PC si scambiano. Qui le password non si vedono mai.',
  shTabFolders: 'Cartelle', shTabCreds: 'Credenziali di cartella', shTabDevs: 'Password dei dispositivi',
  shSearch: 'Cerca per cartella, nome, utente o indirizzo', shNewFolder: 'Nuova cartella', shRename: 'Rinomina',
  shColPath: 'Percorso', shColDomain: 'Dominio', shColPrefix: 'Prefisso IP', shColInside: 'Voci dentro', shColFolder: 'Cartella', shColIp: 'Indirizzo IP', shColUsers: 'Utenti', shColPw: 'Password',
  shInside: (c, d, f) => `${c} connessioni · ${d} dispositivi · ${f} credenziali`, shUsers: n => n === 1 ? '1 utente' : `${n} utenti`, shPwSaved: 'Salvata', shPwNone: 'Nessuna',
  shEmptyFolders: 'Nessuna cartella. Creane una con «Nuova cartella».', shEmptyCreds: 'Nessuna credenziale di cartella.', shEmptyDevs: 'Nessun dispositivo con password.', shNone: 'Niente corrisponde alla ricerca.',
  shFormNew: 'Nuova cartella', shFormEdit: 'Modifica cartella', shFormRename: 'Rinomina cartella', shNewPath: 'Nuovo percorso', shRenameHint: 'Sposta anche le sottocartelle e tutto ciò che contengono, su tutti i PC.',
  shFolderHint: 'Per esempio Uffici/Sede, con la barra per le sottocartelle.', shDomainHint: 'Usato dai PC per le credenziali della cartella (facoltativo).', shPrefixHint: 'Per esempio 10.1. (facoltativo).',
  shExists: 'Esiste già una cartella con questo percorso.', shInto: 'Una cartella non si può spostare dentro se stessa.', shGone: 'La cartella non esiste più: aggiorno l\'elenco.',
  shNotEmpty: n => `Non posso eliminare «${n}»: contiene connessioni condivise. Sposta o elimina prima le connessioni.`,
  shDelFolderT: 'Eliminare la cartella?', shDelFolderS: n => `${n} verrà eliminata dai PC al prossimo contatto, insieme alle sottocartelle, alle credenziali e alle password dei dispositivi che contiene.`,
  shDelCredT: 'Eliminare la credenziale?', shDelCredS: n => `${n} verrà tolta da tutti i PC al prossimo contatto, con la sua password.`,
  shDelDevT: 'Eliminare il dispositivo?', shDelDevS: n => `${n} verrà tolto da tutti i PC al prossimo contatto, con le password dei suoi utenti.`,
});
Object.assign(I18N.en, {
  shNav: 'Credentials and folders', shS: 'The shared folders with domain and IP prefix, the folder credentials and the device passwords the PCs exchange. Passwords are never shown here.',
  shTabFolders: 'Folders', shTabCreds: 'Folder credentials', shTabDevs: 'Device passwords',
  shSearch: 'Search by folder, name, user or address', shNewFolder: 'New folder', shRename: 'Rename',
  shColPath: 'Path', shColDomain: 'Domain', shColPrefix: 'IP prefix', shColInside: 'Entries inside', shColFolder: 'Folder', shColIp: 'IP address', shColUsers: 'Users', shColPw: 'Password',
  shInside: (c, d, f) => `${c} connections · ${d} devices · ${f} credentials`, shUsers: n => n === 1 ? '1 user' : `${n} users`, shPwSaved: 'Saved', shPwNone: 'None',
  shEmptyFolders: 'No folders. Create one with "New folder".', shEmptyCreds: 'No folder credentials.', shEmptyDevs: 'No devices with passwords.', shNone: 'Nothing matches the search.',
  shFormNew: 'New folder', shFormEdit: 'Edit folder', shFormRename: 'Rename folder', shNewPath: 'New path', shRenameHint: 'Also moves the subfolders and everything they contain, on every PC.',
  shFolderHint: 'For example Offices/HQ, with the slash for subfolders.', shDomainHint: 'Used by the PCs for the folder credentials (optional).', shPrefixHint: 'For example 10.1. (optional).',
  shExists: 'A folder with this path already exists.', shInto: 'A folder cannot be moved inside itself.', shGone: 'The folder no longer exists: refreshing the list.',
  shNotEmpty: n => `Cannot delete "${n}": it contains shared connections. Move or delete the connections first.`,
  shDelFolderT: 'Delete the folder?', shDelFolderS: n => `${n} will be removed from the PCs at their next contact, together with its subfolders, credentials and device passwords.`,
  shDelCredT: 'Delete the credential?', shDelCredS: n => `${n} will be removed from every PC at their next contact, with its password.`,
  shDelDevT: 'Delete the device?', shDelDevS: n => `${n} will be removed from every PC at their next contact, with its users' passwords.`,
});
S.sh = { tab: 'folders', folders: [], devs: [], fcs: [], q: '', msg: null }; S.shDlg = null;

async function loadShared() {
  const rs = await Promise.all([api('/folders'), api('/devices'), api('/fcreds')]);
  if (rs.some(r => r.status === 401)) return sessionLost();
  if (rs[0].status === 200 && Array.isArray(rs[0].data)) S.sh.folders = rs[0].data;
  if (rs[1].status === 200 && Array.isArray(rs[1].data)) S.sh.devs = rs[1].data;
  if (rs[2].status === 200 && Array.isArray(rs[2].data)) S.sh.fcs = rs[2].data;
}

const shCloseDlg = () => { S.shDlg = null; render(); };
function shOpenForm(kind, f) {
  S.shDlg = { kind, path: f ? f.path : '', err: '', v: { path: '', domain: f ? f.domain : '', ipPrefix: f ? f.ipPrefix : '', to: f ? f.path : '' } };
  render();
}

async function shSubmit() {
  const D = S.shDlg, v = D.v;
  const norm = p => p.trim().replace(/\\/g, '/').replace(/^\/+|\/+$/g, '').toLowerCase();
  if (D.kind === 'new' && S.sh.folders.some(f => norm(f.path) === norm(v.path))) { D.err = t('shExists'); return render(); }
  const r = D.kind === 'rename' ? await api('/folders/rename', { from: D.path, to: v.to.trim() })
    : await api('/folders', { path: D.kind === 'new' ? v.path.trim() : D.path, domain: v.domain.trim(), ipPrefix: v.ipPrefix.trim() });
  if (r.status === 401) { S.shDlg = null; return sessionLost(); }
  if (r.status === 200) { S.shDlg = null; S.sh.msg = null; await loadShared(); return render(); }
  D.err = r.status === 400 ? t('badField') : r.data.error === 'exists' ? t('shExists') : r.data.error === 'into_itself' ? t('shInto') : r.status === 404 ? t('shGone') : t('netErr');
  if (r.status === 404) await loadShared();
  render();
}

async function shDelete() {
  const D = S.shDlg; S.shDlg = null;
  const r = D.what === 'folder' ? await api('/folders/delete', { path: D.id }) : await api(`/${D.what === 'dev' ? 'devices' : 'fcreds'}/${D.id}/delete`, {});
  if (r.status === 401) return sessionLost();
  S.sh.msg = r.status === 409 && r.data.error === 'notempty' ? ['err', t('shNotEmpty')(D.name)] : r.status === 200 || r.status === 404 ? null : ['err', t('netErr')];
  await loadShared();
  render();
}

function shFormDialog() {
  const D = S.shDlg, v = D.v, rename = D.kind === 'rename';
  const fld = (key, label, o = {}) => h('div', { class: 'field' }, h('label', { class: 'label', for: 'sh-' + key }, label),
    h('div', { class: 'row' }, h('input', { id: 'sh-' + key, type: 'text', autocomplete: 'off', spellcheck: 'false', class: o.cls, 'data-focus': o.focus, value: v[key], oninput: e => { v[key] = e.target.value; } })),
    o.hint && h('div', { class: 'hint' }, o.hint));
  return dlg('wide', 'sh-title', h('div', { class: 'title', id: 'sh-title' }, t(rename ? 'shFormRename' : D.kind === 'new' ? 'shFormNew' : 'shFormEdit')),
    h('form', { class: 'stack', onsubmit: e => { e.preventDefault(); shSubmit(); } },
      D.kind === 'new' ? fld('path', t('shColPath'), { hint: t('shFolderHint'), focus: true }) : h('div', {}, h('div', { class: 'label' }, t('shColPath')), h('div', { class: 'mono' }, D.path)),
      rename ? fld('to', t('shNewPath'), { hint: t('shRenameHint'), focus: true }) : [fld('domain', t('shColDomain'), { hint: t('shDomainHint'), focus: D.kind === 'edit' }), fld('ipPrefix', t('shColPrefix'), { hint: t('shPrefixHint'), cls: 'mono' })],
      D.err && note('err', D.err),
      h('div', { class: 'actions' }, btn(t('cancel'), shCloseDlg), h('button', { class: 'btn primary', type: 'submit' }, t('save')))));
}

function shDelDialog() {
  const D = S.shDlg, k = { folder: 'Folder', cred: 'Cred', dev: 'Dev' }[D.what];
  return dlg('', 'shd-title', h('span', { class: 'ic' }, '!'), h('div', { class: 'title', id: 'shd-title' }, t('shDel' + k + 'T')), h('div', { class: 'sub' }, t('shDel' + k + 'S')(D.name)),
    h('div', { class: 'actions' }, btn(t('cancel'), shCloseDlg), h('button', { class: 'btn danger', type: 'button', 'data-focus': true, onclick: shDelete }, t('remove'))));
}

const shDialogs = () => S.shDlg ? (S.shDlg.kind === 'del' ? shDelDialog() : shFormDialog()) : null;

function sharedPage() {
  const Z = S.sh;
  const del = (what, id, name) => () => { S.shDlg = { kind: 'del', what, id, name }; render(); };
  const rb = (text, fn, cls, label) => h('button', { class: 'btn ' + (cls || ''), type: 'button', onclick: fn, 'aria-label': `${text}: ${label}` }, text);
  const match = (...xs) => { const q = Z.q.trim().toLowerCase(); return !q || xs.some(x => (x || '').toLowerCase().includes(q)); };
  const area = h('div');
  const draw = () => {
    let list, head, row, empty;
    if (Z.tab === 'folders') {
      list = Z.folders.filter(f => match(f.path, f.domain, f.ipPrefix)); empty = t('shEmptyFolders');
      head = [t('shColPath'), t('shColDomain'), t('shColPrefix'), t('shColInside'), ''];
      row = f => h('tr', {}, h('td', {}, h('span', { class: 'who' }, f.path)), h('td', {}, f.domain || '—'), h('td', { class: 'mono' }, f.ipPrefix || '—'),
        h('td', {}, t('shInside')(f.connections, f.devices, f.fcreds)),
        h('td', { class: 'act' }, h('div', { class: 'ctl tight end' }, rb(t('edit'), () => shOpenForm('edit', f), '', f.path), rb(t('shRename'), () => shOpenForm('rename', f), '', f.path),
          rb(t('remove'), del('folder', f.path, f.path), 'danger-o', f.path))));
    } else if (Z.tab === 'creds') {
      list = Z.fcs.filter(c => match(c.folder, c.user)); empty = t('shEmptyCreds');
      head = [t('shColFolder'), t('user'), t('shColPw'), ''];
      row = c => h('tr', {}, h('td', {}, c.folder || '—'), h('td', {}, h('span', { class: 'who' }, c.user)),
        h('td', {}, h('span', { class: 'chip ' + (c.hasPassword ? 'ok' : 'info') }, t(c.hasPassword ? 'shPwSaved' : 'shPwNone'))),
        h('td', { class: 'act' }, h('div', { class: 'ctl tight end' }, rb(t('remove'), del('cred', c.id, `${c.folder || '—'} / ${c.user}`), 'danger-o', c.user))));
    } else {
      list = Z.devs.filter(d => match(d.folder, d.name, d.ip, ...(d.users || []))); empty = t('shEmptyDevs');
      head = [t('shColFolder'), t('colName'), t('shColIp'), t('shColUsers'), ''];
      row = d => h('tr', {}, h('td', {}, d.folder || '—'), h('td', {}, h('span', { class: 'who' }, d.name)), h('td', { class: 'mono' }, d.ip || '—'),
        h('td', {}, h('div', {}, t('shUsers')(d.userCount)), h('div', { class: 'meta' }, (d.users || []).join(', '))),
        h('td', { class: 'act' }, h('div', { class: 'ctl tight end' }, rb(t('remove'), del('dev', d.id, `${d.folder || '—'} / ${d.name}`), 'danger-o', d.name))));
    }
    const total = Z.tab === 'folders' ? Z.folders.length : Z.tab === 'creds' ? Z.fcs.length : Z.devs.length;
    area.replaceChildren(total === 0 ? h('div', { class: 'empty' }, empty) : list.length === 0 ? h('div', { class: 'empty' }, t('shNone'))
      : h('div', { class: 'tblwrap' }, h('table', { class: 'tbl' }, h('thead', {}, h('tr', {}, head.map(c => h('th', {}, c)))), h('tbody', {}, list.map(row)))));
  };
  draw();
  const tab = (key, label) => h('button', { class: 'btn' + (Z.tab === key ? ' primary' : ''), type: 'button', onclick: () => { Z.tab = key; Z.msg = null; render(); } }, label);
  return [
    h('h1', {}, t('shNav')), h('div', { class: 'sub' }, t('shS')),
    h('div', { class: 'ctl' }, tab('folders', t('shTabFolders')), tab('creds', t('shTabCreds')), tab('devs', t('shTabDevs'))),
    Z.msg && note(Z.msg[0], Z.msg[1]),
    h('div', { class: 'toolbar' },
      h('div', { class: 'field grow' }, h('label', { class: 'label sr', for: 'sh-q' }, t('shSearch')),
        h('div', { class: 'row' }, h('input', { id: 'sh-q', type: 'search', placeholder: t('shSearch'), autocomplete: 'off', value: Z.q, oninput: e => { Z.q = e.target.value; draw(); } }))),
      Z.tab === 'folders' && btn(t('shNewFolder'), () => shOpenForm('new'), true)),
    area,
  ];
}

// ---- avvio ------------------------------------------------------------------------------------------------------

async function loadState() {
  const { status, data } = await api('/state');
  if (status === 200) S.st = data;
  return status === 200;
}

function render() {
  clearInterval(S.tick); S.tick = null;
  const screens = { A1: screenA1, A2: screenA2, A3: screenA3, A4: screenA4, B1: screenB1, C: screenC };
  app.replaceChildren(screens[S.screen]());
  app.querySelector('[data-focus]')?.focus(); // dialoghi: il focus va sul primo campo (o sul pulsante principale)
  document.title = 'Lemora Server';
  // dentro la console le richieste dei PC compaiono da sole: ogni 5 secondi si ricontrolla l'elenco e si ridisegna solo se e' cambiato
  if (S.screen === 'C' && !S.poll) S.poll = setInterval(async () => {
    if (S.screen !== 'C' || S.confirm || S.trashConfirm || connDialogOpen() || document.activeElement?.id === 'conn-q' || document.activeElement?.id === 'sh-q' || document.activeElement?.classList.contains('req-note') || S.admDel || document.activeElement?.closest('.adm-form')) return;
    let changed = await loadPcs();
    if (S.screen === 'C') changed = await loadConns() || changed; // se la sessione e' scaduta loadPcs ha gia' portato al login
    if (S.screen === 'C') changed = await loadReqs() || changed;
    if (S.screen === 'C') changed = await loadPers() || changed;
    if (S.screen === 'C') changed = await loadConflicts() || changed;
    if (S.screen === 'C') changed = await loadTrash() || changed;
    if (S.screen === 'C' && !S.confirm && !S.trashConfirm && !connDialogOpen() && changed) render();
  }, 5000);
  if (S.screen !== 'C' && S.poll) { clearInterval(S.poll); S.poll = null; }
}

(async () => {
  document.documentElement.lang = lang;
  if (!(await loadState())) { app.replaceChildren(h('div', { class: 'body' }, note('err', t('netErr')))); return; }
  if (!S.st.configured) {
    S.f.dbDir = S.st.defaults.dbDir;
    S.f.port = String(S.st.defaults.port);
    S.screen = S.st.setupVerified ? 'A2' : 'A1';
  }
  else if (S.st.signedIn) { S.me = (await api('/me')).data; await loadPcs(); await loadConns(); await loadReqs(); await loadPers(); await loadConflicts(); await loadTrash(); S.screen = 'C'; }
  else S.screen = 'B1';
  render();
})();
