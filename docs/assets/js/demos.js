// demos.js: piccole demo cliccabili del programma, disegnate in HTML (non sono schermate).
// Ogni demo e' un elemento con data-demo="nome"; senza JS resta visibile la versione ferma scritta nell'HTML.
// Tutti i dati sono di esempio. Niente viene copiato davvero negli appunti.
(function () {
    'use strict';
    var en = document.documentElement.lang === 'en';
    var calmo = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    var T = en ? {
        copiato: 'Copied (sample). Clipboard cleared in {s} s', svuotati: 'Clipboard cleared',
        nessuna: 'No connection matches', soloRdp: 'The Passwords tab is available in RDP and VNC sessions',
        chiudi: 'Close', sessione: 'Session', bloccata: 'Locked', aperta: 'Unlocked with Windows Hello', attesa: 'Waiting for Windows Hello…',
        nonValido: 'Wrong code', inAttesa: 'Waiting for approval', approvato: 'Approved: read-only', codiceScaduto: 'Code expired',
        bloccato: 'Blocked: this PC is read-only', richiesta: 'Request sent: an administrator must approve it', salvato: 'Saved for all PCs',
        log: { blocco: 'tried to edit (blocked)', richiesta: 'requested an edit', modifica: 'edited', cancella: 'deleted', scollega: 'unpaired the PC' },
        salvare: 'Save this login in Lemora?', salvatoL: 'Saved in Lemora, folder {c}', nienteProposta: 'Domain name in the address bar: no save prompt',
        compilato: 'Filled in from Lemora', terminale: 'sample command, output not real'
    } : {
        copiato: 'Copiata (esempio). Appunti svuotati tra {s} s', svuotati: 'Appunti svuotati',
        nessuna: 'Nessuna connessione trovata', soloRdp: 'La linguetta Password c’è nelle sessioni RDP e VNC',
        chiudi: 'Chiudi', sessione: 'Sessione', bloccata: 'Bloccata', aperta: 'Aperta con Windows Hello', attesa: 'In attesa di Windows Hello…',
        nonValido: 'Codice sbagliato', inAttesa: 'In attesa di approvazione', approvato: 'Approvato: sola lettura', codiceScaduto: 'Codice scaduto',
        bloccato: 'Bloccato: questo PC è in sola lettura', richiesta: 'Richiesta inviata: deve approvarla un amministratore', salvato: 'Salvato per tutti i PC',
        log: { blocco: 'ha provato a modificare (bloccato)', richiesta: 'ha chiesto una modifica', modifica: 'ha modificato', cancella: 'ha cancellato', scollega: 'ha scollegato il PC' },
        salvare: 'Salvare questo accesso in Lemora?', salvatoL: 'Salvato in Lemora, cartella {c}', nienteProposta: 'Nella barra c’è un nome di dominio: nessuna proposta di salvataggio',
        compilato: 'Compilato da Lemora', terminale: 'comando di esempio, risposta non reale'
    };
    function $(r, s) { return r.querySelector(s); }
    function $$(r, s) { return Array.prototype.slice.call(r.querySelectorAll(s)); }
    function ora() { var d = new Date(); return ('0' + d.getHours()).slice(-2) + ':' + ('0' + d.getMinutes()).slice(-2); }

    // ---- appunti finti: messaggio con il conto alla rovescia di 30 s, come nel programma
    function contoAppunti(nota) {
        clearInterval(nota._t);
        var s = 30;
        nota.textContent = T.copiato.replace('{s}', s);
        nota.classList.add('mostra');
        nota._t = setInterval(function () {
            s--;
            if (s <= 0) { clearInterval(nota._t); nota.textContent = T.svuotati; return; }
            nota.textContent = T.copiato.replace('{s}', s);
        }, 1000);
    }

    // ---- fuoco: le demo spostano il fuoco solo quando agisce chi legge, mai durante il giro del finto mouse
    function fuoco(el, x) { if (!el._giro && x) x.focus({ preventScroll: true }); }
    function vis(x) { return !!(x && x.getClientRects().length); }

    // ---- finto mouse: gira da solo nella demo finche' chi legge non ci entra (mouse, dito o tastiera);
    // torna 10 s dopo che ne e' uscito. I passi sono clic veri (element.click()) e testo scritto nei campi,
    // quindi la demo fa esattamente quello che farebbe chi legge. Passi: ['clic', sel, attesa], ['scrivi', sel, testo, attesa],
    // ['pausa', ms], ['fai', funzione]; sel e' un selettore dentro la demo o una funzione che restituisce l'elemento.
    var DOPO_USCITA = 10000, VIAGGIO = 750;
    function giro(el, passi) {
        if (calmo || !('IntersectionObserver' in window)) return;
        var m = document.createElement('span'); m.className = 'mouse'; m.setAttribute('aria-hidden', 'true');
        m.innerHTML = '<svg viewBox="0 0 14 21" width="14" height="21"><path d="M1 1v16.5l4-3.8 2.9 6.3 2.6-1.2-2.9-6.2h5.8z"/></svg>';
        el.classList.add('con-mouse'); el.appendChild(m);
        var i = 0, gen = 0, va = false, visto = false, mouseDentro = false, ditoFino = 0, ritorno = null;
        function trova(s) { var t = typeof s === 'function' ? s() : $(el, s); return vis(t) ? t : null; }
        function porta(t, campo) {
            var r = el.getBoundingClientRect(), b = t.getBoundingClientRect();
            var x = b.left - r.left + (campo ? Math.min(20, b.width / 2) : b.width / 2), y = b.top - r.top + b.height / 2;
            m.style.transform = 'translate(' + Math.round(x) + 'px,' + Math.round(y) + 'px)';
        }
        function dopo(ms, g, f) { setTimeout(function () { if (g === gen) f(); }, ms); }
        function passo(g) {
            if (i >= passi.length) { i = 0; dopo(2600, g, function () { passo(g); }); return; }
            var p = passi[i++], tipo = p[0];
            if (tipo === 'pausa') { dopo(p[1], g, function () { passo(g); }); return; }
            if (tipo === 'fai') { el._giro = true; try { p[1](); } finally { el._giro = false; } dopo(200, g, function () { passo(g); }); return; }
            var t = trova(p[1]);
            if (!t) { dopo(30, g, function () { passo(g); }); return; }   // elemento non visibile in questo momento: passo saltato
            porta(t, tipo === 'scrivi');
            dopo(VIAGGIO, g, function () {
                if (tipo === 'clic') {
                    m.classList.remove('clic'); void m.offsetWidth; m.classList.add('clic');
                    var prima = document.activeElement;
                    el._giro = true; try { t.click(); } finally { el._giro = false; }
                    // il clic su un'etichetta porta il fuoco sul suo campo: il fuoco preso dal giro si restituisce
                    var ora = document.activeElement;
                    if (ora !== prima && ora && el.contains(ora)) { el._giro = true; ora.blur(); el._giro = false; }
                    dopo(p[2] || 1200, g, function () { passo(g); });
                    return;
                }
                var testo = typeof p[2] === 'function' ? p[2]() : p[2], k = 0;
                t.value = ''; t.dispatchEvent(new Event('input', { bubbles: true }));
                (function lettera() {
                    if (k >= testo.length) { dopo(p[3] || 900, g, function () { passo(g); }); return; }
                    t.value += testo[k++]; t.dispatchEvent(new Event('input', { bubbles: true }));
                    dopo(110, g, lettera);
                })();
            });
        }
        // occupata: il mouse e' dentro, un dito l'ha toccata da poco, o il fuoco da tastiera (o in un campo) e' dentro
        function occupata() {
            var a = document.activeElement;
            return mouseDentro || Date.now() < ditoFino || !!(a && a !== el && el.contains(a) && (a.matches(':focus-visible') || a.matches('input, textarea, select')));
        }
        function ferma() { gen++; va = false; m.classList.remove('vis'); }
        function parti() {
            if (va || !visto || occupata()) return;
            va = true; var g = ++gen;
            m.classList.add('vis');
            dopo(300, g, function () { passo(g); });
        }
        function riprova(ms) { clearTimeout(ritorno); ritorno = setTimeout(parti, ms); }
        el.addEventListener('pointerenter', function (e) { if (e.pointerType === 'mouse') { mouseDentro = true; clearTimeout(ritorno); ferma(); } });
        el.addEventListener('pointerleave', function (e) { if (e.pointerType === 'mouse') { mouseDentro = false; riprova(DOPO_USCITA); } });
        el.addEventListener('pointerdown', function (e) { if (e.pointerType !== 'mouse') { ditoFino = Date.now() + DOPO_USCITA; ferma(); riprova(DOPO_USCITA + 50); } });
        el.addEventListener('focusin', function () { if (!el._giro && occupata()) { clearTimeout(ritorno); ferma(); } });
        el.addEventListener('focusout', function (e) { if (!el._giro && !el.contains(e.relatedTarget)) riprova(DOPO_USCITA); });
        new IntersectionObserver(function (v) { visto = v[0].isIntersecting; if (visto) parti(); else ferma(); }, { threshold: 0.2 }).observe(el);
    }

    var demo = {};

    // ================= 1. la finestra del programma (home): cerca, filtra per cliente, apre schede, linguetta Password
    demo.app = function (el) {
        var cerca = $(el, '[data-cerca]');
        var righe = $$(el, '[data-riga]');
        var clienti = $$(el, '[data-cliente-filtro]');
        var vuoto = $(el, '[data-vuoto]');
        var barra = $(el, '[data-schede]');
        var home = $(el, '[data-scheda="home"]');
        var vHome = $(el, '[data-vista="home"]'), vSess = $(el, '[data-vista="sessione"]');
        var titolo = $(el, '[data-sess-titolo]'), schermo = $(el, '[data-sess-schermo]');
        var bPass = $(el, '[data-azione="password"]'), cassetto = $(el, '[data-cassetto]');
        var nota = $(el, '[data-nota]');
        var dispositivi = JSON.parse($(el, '[data-dispositivi]').textContent);
        var filtroCliente = '', scelta = 0, aperte = [], attiva = null, auto = true;

        function visibili() { return righe.filter(function (r) { return !r.hidden; }); }
        function filtra() {
            var parole = cerca.value.trim().toLowerCase().split(/\s+/).filter(Boolean);
            righe.forEach(function (r) {
                var k = (r.dataset.nome + ' ' + r.dataset.ip + ' ' + r.dataset.utente).toLowerCase();
                var ok = parole.every(function (p) { return k.indexOf(p) >= 0; }) && (!filtroCliente || r.dataset.cliente === filtroCliente);
                r.hidden = !ok;
            });
            vuoto.hidden = visibili().length > 0;
            scelta = 0; segna();
        }
        function segna() {
            var v = visibili();
            righe.forEach(function (r) { r.classList.remove('scelta'); r.setAttribute('aria-selected', 'false'); });
            if (v[scelta]) { v[scelta].classList.add('scelta'); v[scelta].setAttribute('aria-selected', 'true'); }
        }
        function mostra(vista) {
            vHome.hidden = vista !== 'home'; vSess.hidden = vista === 'home';
            $$(barra, '[role="tab"]').forEach(function (t) { var on = (vista === 'home' && t === home) || t._riga === attiva; t.setAttribute('aria-selected', on); t.classList.toggle('app__scheda--on', on); });
        }
        function apri(r, daUtente) {
            attiva = r;
            if (aperte.indexOf(r) < 0) {
                aperte.push(r);
                var t = document.createElement('span'); t.className = 'app__scheda-g';
                var b = document.createElement('button'); b.type = 'button'; b.className = 'app__scheda'; b.setAttribute('role', 'tab');
                b.innerHTML = '<i class="dot"></i>' + r.dataset.nome; b._riga = r;
                b.addEventListener('click', function () { apri(r, true); });
                var x = document.createElement('button'); x.type = 'button'; x.className = 'app__x'; x.setAttribute('aria-label', T.chiudi + ' ' + r.dataset.nome); x.textContent = '×';
                x.addEventListener('click', function () { chiudi(r, t); });
                t.appendChild(b); t.appendChild(x); barra.appendChild(t); r._scheda = t;
            }
            var proto = r.dataset.proto;
            titolo.textContent = r.dataset.nome + ' · ' + r.dataset.ip + ' · ' + proto;
            schermo.className = 'sess__schermo sess__schermo--' + (proto === 'SSH' ? 'term' : 'desk');
            if (proto === 'SSH') {
                schermo.innerHTML = '<pre class="sess__term" aria-live="polite"><span class="pr">admin@' + r.dataset.nome.toLowerCase() + ':~$</span> <span class="caret"></span></pre>' +
                    '<label class="sess__cmd"><span class="pr">$</span><input type="text" data-cmd aria-label="' + (en ? 'Type a command: help, uptime, ip' : 'Scrivi un comando: help, uptime, ip') + '" placeholder="help"></label>';
                var inp = $(schermo, '[data-cmd]'), pre = $(schermo, 'pre');
                inp.addEventListener('keydown', function (e) {
                    if (e.key !== 'Enter') return;
                    var c = inp.value.trim(), out = { help: 'help  uptime  ip', uptime: 'up 41 days, 3 users, load 0.08', ip: 'wan1 82.91.14.6/29 · lan 10.20.0.1/24' }[c] || ('-- ' + T.terminale);
                    pre.innerHTML = pre.innerHTML.replace('<span class="caret"></span>', '') + c + '\n' + out + '\n<span class="pr">admin@' + r.dataset.nome.toLowerCase() + ':~$</span> <span class="caret"></span>';
                    inp.value = '';
                });
            } else {
                schermo.innerHTML = '<div class="desk__fin" aria-hidden="true"><p class="desk__tit">' + (r.dataset.finestra || 'Server Manager') + '</p><div class="desk__righe"><i></i><i></i><i></i><i></i></div></div><div class="desk__barra" aria-hidden="true"><i></i><i></i><i></i></div>';
            }
            bPass.disabled = proto === 'SSH';
            bPass.title = proto === 'SSH' ? T.soloRdp : '';
            chiudiCassetto();
            mostra('sessione');
            // aperta da chi legge (clic o Invio): il fuoco va nella sessione, non resta sul campo di ricerca nascosto
            if (daUtente) (proto === 'SSH' ? $(schermo, '[data-cmd]') : bPass).focus({ preventScroll: true });
        }
        function chiudi(r, t) {
            aperte.splice(aperte.indexOf(r), 1); t.remove();
            if (attiva === r) { attiva = aperte[aperte.length - 1] || null; if (attiva) apri(attiva); else mostra('home'); }
        }
        function chiudiCassetto() { cassetto.classList.remove('aperto'); cassetto.hidden = true; bPass.setAttribute('aria-expanded', 'false'); }
        function apriCassetto() {
            var c = attiva.dataset.cliente, voci = dispositivi[c] || [];
            $(cassetto, '[data-cass-titolo]').textContent = 'Password · ' + c;
            var ul = $(cassetto, '[data-cass-voci]'); ul.innerHTML = '';
            voci.forEach(function (v) {
                var li = document.createElement('li'); li.className = 'app__voce';
                li.innerHTML = '<span>' + v[0] + '</span><code>' + v[1] + '</code><button type="button" class="app__copia">' + (en ? 'Copy' : 'Copia') + '</button>';
                $(li, 'button').addEventListener('click', function () { contoAppunti(nota); });
                ul.appendChild(li);
            });
            cassetto.hidden = false; requestAnimationFrame(function () { cassetto.classList.add('aperto'); });
            bPass.setAttribute('aria-expanded', 'true');
        }

        cerca.addEventListener('input', filtra);
        cerca.addEventListener('keydown', function (e) {
            var v = visibili();
            if (e.key === 'ArrowDown') { scelta = Math.min(scelta + 1, v.length - 1); segna(); e.preventDefault(); }
            if (e.key === 'ArrowUp') { scelta = Math.max(scelta - 1, 0); segna(); e.preventDefault(); }
            if (e.key === 'Enter' && v[scelta]) { apri(v[scelta], true); e.preventDefault(); }
        });
        righe.forEach(function (r) { r.addEventListener('click', function () { apri(r, true); }); });
        clienti.forEach(function (b) {
            b.addEventListener('click', function () {
                filtroCliente = b.dataset.clienteFiltro;
                clienti.forEach(function (x) { x.setAttribute('aria-pressed', x === b); });
                cerca.value = ''; filtra();
            });
        });
        home.addEventListener('click', function () { mostra('home'); cerca.focus({ preventScroll: true }); });
        bPass.addEventListener('click', function () { if (cassetto.hidden) apriCassetto(); else chiudiCassetto(); });
        filtra();

        // racconto automatico, una volta, la prima volta che si vede; si ferma al primo gesto dell'utente
        function ferma() { auto = false; }
        el.addEventListener('pointerdown', ferma, true); el.addEventListener('keydown', ferma, true);
        function racconto() {
            if (!auto || calmo) return;
            var testo = 'fs', i = 0;
            (function scrivi() {
                if (!auto) return;
                cerca.value = testo.slice(0, ++i); filtra();
                if (i < testo.length) { setTimeout(scrivi, 160); return; }
                setTimeout(function () { if (auto) apri(visibili()[0]); }, 900);
                setTimeout(function () { if (auto && attiva) apriCassetto(); }, 1900);
            })();
        }
        if ('IntersectionObserver' in window) {
            var io = new IntersectionObserver(function (v) { if (v[0].isIntersecting) { io.disconnect(); setTimeout(racconto, 700); } }, { threshold: 0.25 });
            io.observe(el);
        }
    };

    // ================= 1b. finestra principale fedele al programma (home): albero, elenco, riepilogo; ricerca e filtri veri,
    // stella dei preferiti, menu delle azioni (le stesse voci del programma), viste List/Card/Tree,
    // connessione rapida, scheda di sessione con la linguetta Password. Le parti nuove le crea il JS: senza JS resta l'HTML fermo.
    demo.finestra = function (el) {
        var cartelle = JSON.parse($(el, '[data-cartelle]').textContent);
        var righe = $$(el, '[data-conn]'), nodi = $$(el, '[data-nodo]'), filtri = $$(el, '[data-proto-filtro]');
        var cerca = $(el, '[data-cerca]'), pann = $(el, '[data-pannello]'), vuoto = $(el, '[data-vuoto]'), titoloLista = $(el, '[data-titolo-lista]');
        var corpo = $(el, '.fin__corpo'), barra = $(el, '.fin__barra'), titolo = $(el, '.fin__titolo'), lista = $(el, '.fin__lista ul');
        var nodo = 'Officine Brenta/Server', proto = '', scelta = null, vista = 'lista', protoRapido = 'VNC', aperte = [], attiva = null;
        var L = en ? { server: 'servers', sotto: 'subfolders', rete: 'Network', dominio: 'Domain', cred: 'Credentials', pw: 'Passwords', dest: 'Target', cart: 'Folder', acc: 'Access', eredita: 'from the folder', connetti: 'Connect', modifica: 'Edit', salvati: 'SAVED SERVERS', risultati: 'RESULTS',
                         azioniDi: 'Actions: ', duplica: 'Duplicate', elimina: 'Delete', copiaHost: 'Copy hostname to clipboard', copiaUtente: 'Copy username to clipboard', copiaPw: 'Copy password to clipboard', collegamento: 'Create desktop shortcut', sftp: 'Open SFTP', espRdp: 'Export RDP file', espLink: 'Export shortcut',
                         m: { modifica: 'In the program the editor of {n} opens.', duplica: 'In the program the editor opens with a copy of {n}.', elimina: 'In the program a confirmation is asked before deleting {n}.', host: 'In the clipboard (sample): {a}', utente: 'In the clipboard (sample): {u}', pw: 'Before copying the password the program asks for verification (Windows Hello or master password); the clipboard is cleared after 30 s.', link: 'Shortcut "{n}" on the desktop (sample).', sftp: 'In the program an SFTP tab opens to {a}.', rdp: 'File {n}.rdp (sample).', mora: 'File {n}.mora, without password (sample).', pref: '{n} added to favourites', nopref: '{n} removed from favourites', rapida: 'Quick connection {p} to {a}', nessunaRapida: 'Not saved: Enter opens a quick connection with the protocol on the right.' },
                         desk: 'Here the program shows the remote desktop of {n}.', accesso: 'Access {u}', dallaCartella: ', from the folder', senzaUtente: 'user and password are asked on connection', password: 'Passwords', cercaPw: 'Search name, IP or user…', soloRdp: 'The Passwords tab is available in RDP and VNC sessions.', chiudi: 'Close', protoR: 'Protocol of the quick connection', viste: 'View', copia: 'Copy ' }
                   : { server: 'server', sotto: 'sottocartelle', rete: 'Rete', dominio: 'Dominio', cred: 'Credenziali', pw: 'Password', dest: 'Destinazione', cart: 'Cartella', acc: 'Accesso', eredita: 'dalla cartella', connetti: 'Connetti', modifica: 'Modifica', salvati: 'SERVER SALVATI', risultati: 'RISULTATI',
                         azioniDi: 'Azioni: ', duplica: 'Duplica', elimina: 'Elimina', copiaHost: 'Copia il nome host negli appunti', copiaUtente: 'Copia il nome utente negli appunti', copiaPw: 'Copia la password negli appunti', collegamento: 'Crea collegamento sul desktop', sftp: 'Apri SFTP', espRdp: 'Esporta file RDP', espLink: 'Esporta collegamento',
                         m: { modifica: 'Nel programma si apre l’editor di {n}.', duplica: 'Nel programma si apre l’editor con una copia di {n}.', elimina: 'Nel programma chiede conferma prima di eliminare {n}.', host: 'Negli appunti (esempio): {a}', utente: 'Negli appunti (esempio): {u}', pw: 'Prima di copiare la password il programma chiede la verifica (Windows Hello o password principale); gli appunti si svuotano dopo 30 s.', link: 'Collegamento “{n}” sul desktop (esempio).', sftp: 'Nel programma si apre una scheda SFTP verso {a}.', rdp: 'File {n}.rdp (esempio).', mora: 'File {n}.mora, senza password (esempio).', pref: '{n} tra i preferiti', nopref: '{n} tolta dai preferiti', rapida: 'Connessione rapida {p} verso {a}', nessunaRapida: 'Non salvata: Invio apre una connessione rapida con il protocollo a destra.' },
                         desk: 'Qui il programma mostra il desktop remoto di {n}.', accesso: 'Accesso {u}', dallaCartella: ', preso dalla cartella', senzaUtente: 'utente e password chiesti al collegamento', password: 'Password', cercaPw: 'Cerca nome, IP o utente…', soloRdp: 'La linguetta Password c’è nelle sessioni RDP e VNC.', chiudi: 'Chiudi', protoR: 'Protocollo della connessione rapida', viste: 'Vista', copia: 'Copia ' };
        // password dei dispositivi per cliente (stesse voci della schermata vera della linguetta, per Officine Brenta)
        var DISP = {
            'Officine Brenta': [['Centralino VoIP', '10.20.1.3', ['admin', 'tecnico']], ['Firewall', '10.20.9.1', ['admin', 'audit']], ['iLO DC01', '10.20.0.205', ['Administrator']], ['NAS-Backup', '10.20.0.40', ['admin', 'backup']], ['Stampante-Piano1', '10.20.1.80', ['admin']]],
            'Studio Rossi': [['NAS-Studio', '192.168.10.20', ['admin']], ['Router', '192.168.10.1', ['admin']], ['Stampante', '192.168.10.30', ['admin']], ['Switch', '192.168.10.2', ['admin']], ['Centralino', '192.168.10.3', ['admin', 'tecnico']]],
            'Farmacia Centrale': [['Router', '192.168.1.1', ['admin']], ['NVR telecamere', '192.168.1.40', ['admin']], ['Stampante', '192.168.1.30', ['admin']]],
            'Hotel Lago Blu': [['Firewall', '192.168.50.1', ['admin']], ['Centralino', '192.168.50.3', ['admin']]],
            'Clinica Sole': [['NAS', '172.16.8.20', ['admin']], ['Router', '172.16.8.1', ['admin']]]
        };
        function f(s, o) { return s.replace(/\{(\w)\}/g, function (_, k) { return o[k] || ''; }); }

        // barra di stato in fondo: l'esito delle azioni che nella demo non si possono fare davvero
        var stato = document.createElement('p'); stato.className = 'fin__stato'; stato.setAttribute('aria-live', 'polite'); el.appendChild(stato);
        function dice(t) { clearInterval(stato._t); stato.textContent = t; }

        // schede in alto: Home + una per sessione
        titolo.removeAttribute('aria-hidden');
        $$(titolo, 'img, .fin__piu, .fin__ctrl').forEach(function (x) { x.setAttribute('aria-hidden', 'true'); });
        var schede = document.createElement('span'); schede.className = 'fin__schede'; schede.setAttribute('role', 'tablist');
        var homeTab = document.createElement('button'); homeTab.type = 'button'; homeTab.className = 'fin__scheda'; homeTab.setAttribute('role', 'tab'); homeTab.textContent = 'Home';
        var vecchia = $(titolo, '.fin__scheda'); vecchia.replaceWith(schede); schede.appendChild(homeTab);

        // protocollo della connessione rapida: il menu a destra del campo
        var chipVecchio = $(el, '.fin__chip'), chip = document.createElement('button');
        chip.type = 'button'; chip.className = 'fin__chip'; chip.setAttribute('aria-label', L.protoR); chip.textContent = protoRapido + ' ▾';
        chipVecchio.replaceWith(chip);
        chip.addEventListener('click', function () { protoRapido = { RDP: 'VNC', VNC: 'SSH', SSH: 'RDP' }[protoRapido]; chip.textContent = protoRapido + ' ▾'; });

        // viste List / Card / Tree (nel programma i nomi sono in inglese anche in italiano)
        var segV = document.createElement('span'); segV.className = 'fin__seg'; segV.setAttribute('role', 'group'); segV.setAttribute('aria-label', L.viste);
        [['lista', 'List'], ['schede', 'Card'], ['albero', 'Tree']].forEach(function (v) {
            var b = document.createElement('button'); b.type = 'button'; b.dataset.vista = v[0]; b.textContent = v[1]; b.setAttribute('aria-pressed', v[0] === vista);
            b.addEventListener('click', function () { vista = v[0]; $$(segV, 'button').forEach(function (x) { x.setAttribute('aria-pressed', x === b); }); el.classList.toggle('fin--schede', vista === 'schede'); filtra(); });
            segV.appendChild(b);
        });
        barra.appendChild(segV);

        // vista della sessione, al posto di barra e corpo
        var sess = document.createElement('div'); sess.className = 'fin__sess'; sess.hidden = true; corpo.after(sess);

        function cartellaDi(r) { return r.dataset.cartella; }
        function dentro(r) { return cartellaDi(r) === nodo || cartellaDi(r).indexOf(nodo + '/') === 0 || nodo === ''; }
        function visibili() { return righe.filter(function (r) { return !r.hidden; }); }
        function filtra() {
            var q = cerca.value.trim().toLowerCase(), n = 0;
            righe.forEach(function (r) {
                var k = (r.dataset.nome + ' ' + r.dataset.ip + ' ' + r.dataset.utente).toLowerCase();
                var ok = (q ? k.indexOf(q) >= 0 : dentro(r)) && (!proto || r.dataset.proto === proto);
                r.hidden = !ok; r.parentElement.hidden = !ok; if (ok) n++;
            });
            vuoto.hidden = n > 0;
            titoloLista.textContent = q ? L.risultati : L.salvati;
            // vista Tree: un'intestazione per cartella davanti al primo server visibile di quella cartella
            $$(lista, '.fin__gruppo').forEach(function (g) { g.remove(); });
            if (vista === 'albero') {
                var ultima = null;
                visibili().forEach(function (r) {
                    if (cartellaDi(r) === ultima) return;
                    ultima = cartellaDi(r);
                    var g = document.createElement('li'); g.className = 'fin__gruppo'; g.textContent = ultima.replace(/\//g, ' › ');
                    lista.insertBefore(g, r.parentElement);
                });
            }
            if (!scelta || scelta.hidden) riepilogoCartella();
        }
        function riga(k, v) { return '<div><dt>' + k + '</dt><dd>' + v + '</dd></div>'; }
        function riepilogoCartella() {
            scelta = null; righe.forEach(function (r) { r.classList.remove('scelta'); });
            var c = cartelle[nodo] || cartelle[''];
            var nome = nodo ? nodo.split('/').pop() : 'Home';
            pann.innerHTML = '<div class="fin__ptesta"><span class="fin__lettera">' + nome[0] + '</span><div><p class="fin__pnome">' + nome + '</p><p class="fin__pmeta">' + c.server + ' ' + L.server + ' · ' + c.sotto + ' ' + L.sotto + '</p></div></div>' +
                '<p class="fin__pbott"><span class="fin__bp">+ Server</span><span class="fin__bg">' + L.modifica + '</span></p>' +
                '<dl class="fin__ptab">' + riga(L.rete, '<code>' + c.rete + '</code>') + riga(L.dominio, c.dominio) + riga(L.cred, c.cred) + riga(L.pw, c.pw) + '</dl>';
        }
        function riepilogoConn(r) {
            scelta = r; righe.forEach(function (x) { x.classList.toggle('scelta', x === r); });
            var c = cartelle[cartellaDi(r)] || {};
            var acc = r.dataset.utente + (r.dataset.ereditato ? ' <small>' + L.eredita + '</small>' : '');
            pann.innerHTML = '<div class="fin__ptesta"><span class="fin__lettera fin__lettera--' + r.dataset.proto.toLowerCase() + '">' + r.dataset.proto + '</span><div><p class="fin__pnome">' + r.dataset.nome + '</p><p class="fin__pmeta">' + cartellaDi(r).replace(/\//g, ' / ') + '</p></div></div>' +
                '<dl class="fin__ptab">' + riga(L.dest, '<code>' + r.dataset.ip + ':' + r.dataset.porta + '</code>') + riga(L.cart, cartellaDi(r).split('/').pop()) + riga(L.acc, acc) + (c.dominio ? riga(L.dominio, c.dominio) : '') + '</dl>' +
                '<p class="fin__pbott"><button type="button" class="fin__bp" data-connetti>' + L.connetti + '</button></p>';
            $(pann, '[data-connetti]').addEventListener('click', function () { connetti(datiDi(r)); });
        }
        function datiDi(r) { var d = r.dataset; return { nome: d.nome, ip: d.ip, porta: d.porta, proto: d.proto, utente: d.utente, ereditato: !!d.ereditato, cartella: d.cartella }; }

        // ---- menu delle azioni della freccia ›: le stesse voci del programma, nello stesso ordine
        var menu = document.createElement('ul'); menu.className = 'fin__menu'; menu.setAttribute('role', 'menu'); menu.hidden = true; el.appendChild(menu);
        var menuDi = null;
        function chiudiMenu() { menu.hidden = true; if (menuDi) menuDi.setAttribute('aria-expanded', 'false'); menuDi = null; }
        function apriMenu(r, freccia) {
            if (menuDi === freccia) { chiudiMenu(); return; }
            chiudiMenu();
            var d = datiDi(r), voci = [['connetti', L.connetti], ['modifica', L.modifica], ['duplica', L.duplica], ['elimina', L.elimina], ['host', L.copiaHost], ['utente', L.copiaUtente], ['pw', L.copiaPw], ['link', L.collegamento]];
            if (d.proto === 'SSH') voci.push(['sftp', L.sftp]);
            if (d.proto === 'RDP') voci.push(['rdp', L.espRdp]);
            voci.push(['mora', L.espLink]);
            menu.innerHTML = '';
            voci.forEach(function (v, i) {
                var li = document.createElement('li'), b = document.createElement('button');
                b.type = 'button'; b.setAttribute('role', 'menuitem'); b.dataset.az = v[0]; b.textContent = v[1];
                if (i === 3 || i === 6) li.className = 'fin__menu-sep';   // separatori dopo Elimina e dopo le copie, come nel programma
                b.addEventListener('click', function () { chiudiMenu(); azione(v[0], d, r); });
                li.appendChild(b); menu.appendChild(li);
            });
            var re = el.getBoundingClientRect(), rf = freccia.getBoundingClientRect();
            menu.hidden = false;
            var top = rf.bottom - re.top + 2, alto = menu.offsetHeight;
            if (top + alto > el.clientHeight - 4) top = Math.max(4, rf.top - re.top - alto - 2);   // in fondo alla finestra si apre verso l'alto
            menu.style.top = top + 'px'; menu.style.left = Math.max(4, rf.right - re.left - menu.offsetWidth) + 'px';
            menuDi = freccia; freccia.setAttribute('aria-expanded', 'true');
            fuoco(el, $(menu, 'button'));
        }
        menu.addEventListener('keydown', function (e) {
            var b = $$(menu, 'button'), i = b.indexOf(document.activeElement);
            if (e.key === 'Escape') { var fr = menuDi; chiudiMenu(); fuoco(el, fr); }
            if (e.key === 'ArrowDown') { e.preventDefault(); b[(i + 1) % b.length].focus(); }
            if (e.key === 'ArrowUp') { e.preventDefault(); b[(i - 1 + b.length) % b.length].focus(); }
        });
        // solo i clic veri chiudono il menu: quelli del finto mouse di un'altra demo della pagina non contano
        document.addEventListener('click', function (e) { if (e.isTrusted && !menu.hidden && !menu.contains(e.target) && e.target !== menuDi) chiudiMenu(); });
        function azione(a, d, r) {
            var o = { n: d.nome, a: d.ip + ':' + d.porta, u: d.utente };
            if (a === 'connetti') { connetti(d); return; }
            dice(f(L.m[a], o));
            if (r && (a === 'host' || a === 'utente')) riepilogoConn(r);
        }

        // ---- righe: clic = riepilogo, doppio clic = connessione, stella = preferiti, freccia = azioni
        righe.forEach(function (r) {
            var li = r.parentElement, fr = document.createElement('button');
            li.classList.add('fin__li');
            fr.type = 'button'; fr.className = 'fin__freccia'; fr.textContent = '›';
            fr.setAttribute('aria-label', L.azioniDi + r.dataset.nome); fr.setAttribute('aria-haspopup', 'menu'); fr.setAttribute('aria-expanded', 'false');
            fr.addEventListener('click', function (e) { e.stopPropagation(); apriMenu(r, fr); });
            li.appendChild(fr);
            r.addEventListener('click', function (e) {
                var st = e.target.closest && e.target.closest('.fin__stella');
                if (st) {
                    var on = !st.classList.contains('fin__stella--on');
                    st.classList.toggle('fin__stella--on', on); st.textContent = on ? '★' : '☆';
                    dice(f(on ? L.m.pref : L.m.nopref, { n: r.dataset.nome }));
                    return;
                }
                riepilogoConn(r);
            });
            r.addEventListener('dblclick', function () { connetti(datiDi(r)); });
        });

        // ---- sessioni: una scheda per connessione; RDP e VNC con la linguetta Password, SSH con un terminale di prova
        function mostra(s) {
            attiva = s;
            var home = !s;
            barra.hidden = !home; corpo.hidden = !home; sess.hidden = home;
            homeTab.setAttribute('aria-selected', home);
            aperte.forEach(function (x) { x.tab.setAttribute('aria-selected', x === s); });
            if (s) disegna(s);
        }
        function connetti(d) {
            chiudiMenu();
            var s = aperte.filter(function (x) { return x.d.nome === d.nome && x.d.ip === d.ip; })[0];
            if (!s) {
                s = { d: d };
                var g = document.createElement('span'); g.className = 'fin__sch';
                var b = document.createElement('button'); b.type = 'button'; b.className = 'fin__scheda'; b.setAttribute('role', 'tab');
                b.innerHTML = '<i class="fin__vivo" aria-hidden="true"></i>' + d.nome;
                var x = document.createElement('button'); x.type = 'button'; x.className = 'fin__x'; x.textContent = '×'; x.setAttribute('aria-label', L.chiudi + ' ' + d.nome);
                b.addEventListener('click', function () { mostra(s); });
                x.addEventListener('click', function () { aperte.splice(aperte.indexOf(s), 1); g.remove(); if (attiva === s) mostra(aperte[aperte.length - 1] || null); });
                g.appendChild(b); g.appendChild(x); schede.appendChild(g);
                s.tab = b; s.g = g; aperte.push(s);
            }
            mostra(s);
            if (!d.cartella) dice(f(L.m.rapida, { p: d.proto, a: d.ip }));
        }
        function disegna(s) {
            var d = s.d, cliente = (d.cartella || '').split('/')[0];
            var acc = d.utente ? f(L.accesso, { u: d.utente }) + (d.ereditato ? L.dallaCartella : '') : L.senzaUtente;
            if (d.proto === 'SSH') {
                var pr = (d.utente || 'admin') + '@' + d.nome.toLowerCase().replace(/[^a-z0-9-]/g, '') + ':~$';
                sess.innerHTML = '<div class="fin__term"><pre aria-live="polite"><span class="pr">' + pr + '</span> </pre>' +
                    '<label class="fin__cmd"><span class="pr" aria-hidden="true">$</span><input type="text" data-cmd aria-label="' + (en ? 'Type a command: help, uptime, ip' : 'Scrivi un comando: help, uptime, ip') + '" placeholder="help" autocomplete="off" spellcheck="false"></label></div>';
                var inp = $(sess, '[data-cmd]'), pre = $(sess, 'pre');
                inp.addEventListener('keydown', function (e) {
                    if (e.key !== 'Enter') return;
                    var c = inp.value.trim(), out = { help: 'help  uptime  ip', uptime: 'up 41 days, 3 users, load 0.08', ip: 'lan ' + d.ip + '/24' }[c] || ('-- ' + T.terminale);
                    pre.appendChild(document.createTextNode(c + '\n' + out + '\n'));
                    var p = document.createElement('span'); p.className = 'pr'; p.textContent = pr; pre.appendChild(p); pre.appendChild(document.createTextNode(' '));
                    inp.value = ''; pre.scrollTop = pre.scrollHeight;
                });
                dice(L.soloRdp);
                return;
            }
            var voci = DISP[cliente] || [];
            sess.innerHTML = '<div class="fin__desk"><p class="fin__desk-tit"><b class="fin__p fin__p--' + d.proto.toLowerCase() + '">' + d.proto + '</b> ' + d.nome + ' · <code>' + d.ip + ':' + d.porta + '</code></p>' +
                '<p>' + f(L.desk, { n: d.nome }) + '</p><p class="fin__desk-meta">' + acc + '</p></div>' +
                (voci.length ? '<button type="button" class="fin__ling" aria-expanded="false">' + L.password + '</button>' +
                '<aside class="fin__cass" hidden><label class="fin__ccerca"><span class="vh">' + L.cercaPw + '</span><input type="text" placeholder="' + L.cercaPw + '" autocomplete="off" spellcheck="false"></label><ul role="list"></ul></aside>' : '');
            if (!voci.length) return;
            var ling = $(sess, '.fin__ling'), cass = $(sess, '.fin__cass'), ul = $(cass, 'ul'), q = $(cass, 'input');
            voci.forEach(function (v) {
                var li = document.createElement('li'); li.className = 'fin__cvoce'; li.dataset.k = (v[0] + ' ' + v[1] + ' ' + v[2].join(' ')).toLowerCase();
                li.innerHTML = '<p><b>' + v[0] + '</b><code>' + v[1] + '</code></p>';
                v[2].forEach(function (u) {
                    var b = document.createElement('button'); b.type = 'button'; b.className = 'fin__cu'; b.textContent = u; b.setAttribute('aria-label', L.copia + v[0] + ', ' + u);
                    b.addEventListener('click', function () { contoAppunti(stato); });
                    li.appendChild(b);
                });
                ul.appendChild(li);
            });
            q.addEventListener('input', function () { var t = q.value.trim().toLowerCase(); $$(ul, 'li').forEach(function (li) { li.hidden = !!t && li.dataset.k.indexOf(t) < 0; }); });
            ling.addEventListener('click', function () { var ap = cass.hidden; cass.hidden = !ap; ling.setAttribute('aria-expanded', ap); if (ap) fuoco(el, q); });
        }
        homeTab.addEventListener('click', function () { mostra(null); });

        nodi.forEach(function (b) {
            b.addEventListener('click', function () {
                nodo = b.dataset.nodo; cerca.value = '';
                nodi.forEach(function (x) { x.setAttribute('aria-current', x === b ? 'true' : 'false'); });
                scelta = null; filtra();
            });
        });
        filtri.forEach(function (b) {
            b.addEventListener('click', function () { proto = b.dataset.protoFiltro; filtri.forEach(function (x) { x.setAttribute('aria-pressed', x === b); }); filtra(); });
        });
        cerca.addEventListener('input', function () { scelta = null; filtra(); if (!visibili().length && cerca.value.trim()) dice(L.m.nessunaRapida); });
        cerca.addEventListener('keydown', function (e) {
            if (e.key !== 'Enter') return;
            e.preventDefault();
            var primo = visibili()[0], a = cerca.value.trim();
            if (primo) connetti(datiDi(primo));
            else if (/^[\w.-]+(:\d+)?$/.test(a)) {   // indirizzo non salvato: connessione rapida con il protocollo scelto a destra
                var h = a.split(':');
                connetti({ nome: a, ip: h[0], porta: h[1] || { RDP: '3389', VNC: '5900', SSH: '22' }[protoRapido], proto: protoRapido, utente: '', cartella: '' });
            }
        });
        mostra(null);
        filtra();

        // giro del finto mouse: cartella, filtro, ricerca, menu delle azioni, sessione con la linguetta, viste
        function primo() { return visibili()[0]; }
        giro(el, [
            ['clic', '[data-nodo="Studio Rossi"]'],
            ['clic', '[data-proto-filtro="RDP"]'],
            ['clic', '[data-proto-filtro=""]'],
            ['scrivi', '[data-cerca]', 'fs'],
            ['clic', primo, 1400],
            ['clic', function () { var r = primo(); return r && $(r.parentElement, '.fin__freccia'); }, 1300],
            ['clic', function () { return menu.hidden ? null : $(menu, '[data-az="host"]'); }, 2000],
            ['clic', '[data-connetti]', 1500],
            ['clic', '.fin__ling', 1300],
            ['clic', '.fin__cu', 2200],
            ['clic', function () { return homeTab; }],
            ['scrivi', '[data-cerca]', ''],
            ['clic', '[data-nodo=""]'],
            ['clic', '[data-vista="schede"]', 2000],
            ['clic', '[data-vista="albero"]', 2000],
            ['clic', '[data-vista="lista"]'],
            ['clic', function () { var s = aperte[0]; return s && $(s.g, '.fin__x'); }],
            ['clic', '[data-nodo="Officine Brenta/Server"]', 1500]
        ]);
    };

    // ================= 2. mappa dell'interfaccia (home): un punto alla volta, la descrizione accanto
    // ogni punto ha data-zona="x,y,l,a" in % della schermata: il clic riquadra la zona e ne mostra un ingrandimento accanto
    demo.mappa = function (el) {
        var punti = $$(el, '[data-punto]'), voci = $$(el, '[data-voce]'), palco = $(el, '.mappa__palco');
        var riquadro = document.createElement('span'); riquadro.className = 'mappa__zona'; riquadro.setAttribute('aria-hidden', 'true'); palco.appendChild(riquadro);
        function zona(p) { return p.dataset.zona.split(',').map(Number); }
        punti.forEach(function (p) { var z = zona(p); p.style.left = z[0] + '%'; p.style.top = z[1] + '%'; });
        function visibile() { return $$(palco, 'img').filter(function (i) { return i.offsetParent; })[0]; }
        function ingrandisci(v, z) {
            var box = $(v, '.mappa__zoom');
            if (!box) { box = document.createElement('div'); box.className = 'mappa__zoom'; box.setAttribute('aria-hidden', 'true'); box.appendChild(document.createElement('img')); v.appendChild(box); }
            var src = visibile(), img = box.firstChild;
            if (!src) return;
            // sempre il file da 1400 (src), mai la versione ridotta scelta dal browser, e mai oltre la grandezza vera: sgranerebbe
            img.src = src.getAttribute('src');
            var nat = +src.getAttribute('width'), ratio = (+src.getAttribute('height')) / nat, bw = box.clientWidth || 300;
            var w = Math.min(bw * 100 / z[2], nat), h = w * ratio;
            var bh = Math.min(z[3] / 100 * h, 260);
            var x = -(z[0] + z[2] / 2) / 100 * w + bw / 2, y = -(z[1] + z[3] / 2) / 100 * h + bh / 2;   // zona al centro del riquadro
            img.style.width = w + 'px';
            img.style.left = Math.min(0, Math.max(bw - w, x)) + 'px'; img.style.top = Math.min(0, Math.max(bh - h, y)) + 'px';
            box.style.height = bh + 'px';
        }
        function scegli(n) {
            var p = punti.filter(function (x) { return x.dataset.punto === n; })[0], z = zona(p);
            punti.forEach(function (x) { var on = x === p; x.setAttribute('aria-pressed', on); x.classList.toggle('on', on); });
            riquadro.style.left = z[0] + '%'; riquadro.style.top = z[1] + '%'; riquadro.style.width = z[2] + '%'; riquadro.style.height = z[3] + '%';
            voci.forEach(function (v) { v.hidden = v.dataset.voce !== n; if (!v.hidden) ingrandisci(v, z); });
        }
        punti.forEach(function (p) { p.addEventListener('click', function () { scegli(p.dataset.punto); }); });
        el.classList.add('mappa--attiva');
        scegli(punti[0].dataset.punto);
        window.addEventListener('resize', function () { var on = punti.filter(function (x) { return x.classList.contains('on'); })[0]; if (on) scegli(on.dataset.punto); });
        giro(el, ['2', '3', '4', '5', '6', '7', '1'].map(function (n) { return ['clic', '[data-punto="' + n + '"]', 2800]; }));
    };

    // ================= 3. galleria: schede che cambiano la schermata grande
    demo.galleria = function (el) {
        var tab = $$(el, '[role="tab"]'), pannelli = $$(el, '[role="tabpanel"]');
        function scegli(i, fuoco) {
            tab.forEach(function (t, j) { t.setAttribute('aria-selected', i === j); t.tabIndex = i === j ? 0 : -1; });
            pannelli.forEach(function (p, j) { p.hidden = i !== j; });
            if (fuoco) tab[i].focus();
        }
        tab.forEach(function (t, i) {
            t.addEventListener('click', function () { scegli(i); });
            t.addEventListener('keydown', function (e) {
                if (e.key === 'ArrowRight') scegli((i + 1) % tab.length, true);
                if (e.key === 'ArrowLeft') scegli((i - 1 + tab.length) % tab.length, true);
            });
        });
        el.classList.add('galleria--attiva');
        scegli(0);
    };

    // ================= 4. eredita' della cartella (Prodotto P.1): cambi dominio, prefisso, utente e vedi le connessioni
    demo.eredita = function (el) {
        var dom = $(el, '[data-campo="dominio"]'), pre = $(el, '[data-campo="prefisso"]'), ute = $(el, '[data-campo="utente"]');
        var righe = $$(el, '[data-finale]');   // le righe con data-fisso hanno valori propri: la cartella non le cambia
        function calcola() {
            var p = pre.value.trim();
            righe.forEach(function (r) {
                $(r, '[data-indirizzo]').textContent = p + r.dataset.finale;
                $(r, '[data-accesso]').textContent = (dom.value.trim() ? dom.value.trim() + '\\' : '') + (ute.value.trim() || '·');
            });
        }
        [dom, pre, ute].forEach(function (i) { i.addEventListener('input', calcola); });
        calcola();
        giro(el, [
            ['scrivi', '[data-campo="prefisso"]', '192.168.5.', 1600], ['scrivi', '[data-campo="dominio"]', 'OFFICINA', 1600], ['scrivi', '[data-campo="utente"]', 'tecnico', 2200],
            ['scrivi', '[data-campo="prefisso"]', '10.20.0.', 900], ['scrivi', '[data-campo="dominio"]', 'BRENTA', 900], ['scrivi', '[data-campo="utente"]', 'admin', 1600]
        ]);
    };

    // ================= 5. linguetta Password (Prodotto P.3): cerca e copia, appunti a tempo
    demo.linguetta = function (el) {
        var cerca = $(el, '[data-cerca]'), voci = $$(el, '[data-voce]'), nota = $(el, '[data-nota]');
        cerca.addEventListener('input', function () {
            var q = cerca.value.trim().toLowerCase();
            voci.forEach(function (v) { v.hidden = q && v.textContent.toLowerCase().indexOf(q) < 0; });
        });
        $$(el, '[data-copia]').forEach(function (b) { b.addEventListener('click', function () { contoAppunti(nota); }); });
    };

    // ================= 6. estensione (Prodotto P.4): compila da Lemora; dopo un accesso nuovo propone di salvare solo su IP
    demo.browser = function (el) {
        var url = $(el, '.browser__url'), ute = $(el, '[data-utente]'), pw = $(el, '[data-pw]'), icona = $(el, '[data-icona]');
        var popup = $(el, '[data-popup]'), barra = $(el, '[data-proposta]'), esito = $(el, '[data-esito]'), modulo = $(el, 'form');
        var indirizzi = $$(el, '[data-indirizzo]'), daLemora = false, suIp = true;
        indirizzi.forEach(function (b) {
            b.addEventListener('click', function () {
                suIp = b.dataset.indirizzo === 'ip';
                indirizzi.forEach(function (x) { x.setAttribute('aria-pressed', x === b); });
                url.textContent = b.dataset.url; ute.value = ''; pw.value = ''; daLemora = false;
                popup.hidden = true; barra.hidden = true; esito.textContent = '';
                icona.hidden = !suIp;
            });
        });
        icona.addEventListener('click', function () { popup.hidden = !popup.hidden; icona.setAttribute('aria-expanded', !popup.hidden); });
        $$(el, '[data-voce-pw]').forEach(function (v) {
            v.addEventListener('click', function () {
                ute.value = v.dataset.u; pw.value = 'esempio-pw'; daLemora = true;
                popup.hidden = true; icona.setAttribute('aria-expanded', 'false'); esito.textContent = T.compilato;
            });
        });
        [ute, pw].forEach(function (i) { i.addEventListener('input', function () { daLemora = false; }); });
        modulo.addEventListener('submit', function (e) {
            e.preventDefault();
            if (!ute.value || !pw.value) { fuoco(el, ute.value ? pw : ute); return; }
            if (daLemora) { esito.textContent = T.compilato; return; }
            if (suIp) { barra.hidden = false; esito.textContent = ''; fuoco(el, $(barra, 'button')); }
            else esito.textContent = T.nienteProposta;
        });
        $(el, '[data-salva]').addEventListener('click', function () { barra.hidden = true; esito.textContent = T.salvatoL.replace('{c}', $(el, '[data-cartella]').value); });
        $(el, '[data-non-ora]').addEventListener('click', function () { barra.hidden = true; esito.textContent = ''; });
        giro(el, [
            ['clic', '[data-indirizzo="ip"]', 700], ['clic', '[data-icona]'], ['clic', '[data-voce-pw]', 2000],
            ['clic', '[data-indirizzo="dominio"]', 700], ['scrivi', '[data-utente]', 'mario.rossi', 300], ['scrivi', '[data-pw]', 'esempio', 300], ['clic', '.bdemo__invia', 2400],
            ['clic', '[data-indirizzo="ip"]', 700], ['scrivi', '[data-utente]', 'tecnico', 300], ['scrivi', '[data-pw]', 'esempio', 300], ['clic', '.bdemo__invia', 1500],
            ['clic', '[data-salva]', 2400]
        ]);
    };

    // ================= 7. abbinamento di un PC (Per aziende A.2): codice, inserimento, approvazione
    demo.abbinamento = function (el) {
        var crea = $(el, '[data-crea]'), codice = $(el, '[data-codice]'), scad = $(el, '[data-scadenza]');
        var campo = $(el, '[data-campo]'), invia = $(el, '[data-invia]'), stato = $(el, '[data-stato]'), approva = $(el, '[data-approva]');
        var riga = $(el, '[data-scad-riga]'), vero = '', t = null;
        function esito(txt, male) { stato.textContent = txt; stato.classList.toggle('errore', !!male); }
        crea.addEventListener('click', function () {
            var a = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789'; vero = '';
            for (var i = 0; i < 6; i++) vero += a[Math.floor(Math.random() * a.length)];
            codice.textContent = vero; var s = 30 * 60; clearInterval(t);
            t = setInterval(function () { s--; scad.textContent = Math.floor(s / 60) + ':' + ('0' + s % 60).slice(-2); if (s <= 0) { clearInterval(t); vero = ''; codice.textContent = T.codiceScaduto; } }, 1000);
            scad.textContent = '30:00'; riga.hidden = false; campo.disabled = false; invia.disabled = false; esito(''); approva.hidden = true; campo.value = '';
            fuoco(el, campo);
        });
        function prova() {
            if (!vero || campo.value.trim().toUpperCase() !== vero) { esito(T.nonValido, true); return; }
            esito(T.inAttesa); approva.hidden = false; campo.disabled = true; invia.disabled = true; vero = '';
            clearInterval(t); codice.textContent = '······'; riga.hidden = true;
        }
        invia.addEventListener('click', prova);
        campo.addEventListener('keydown', function (e) { if (e.key === 'Enter') { e.preventDefault(); prova(); } });
        approva.addEventListener('click', function () { esito(T.approvato); approva.hidden = true; });
        giro(el, [
            ['clic', '[data-crea]', 1000], ['scrivi', '[data-campo]', 'K7PQ2A', 300], ['clic', '[data-invia]', 2000],
            ['scrivi', '[data-campo]', function () { return codice.textContent; }, 300], ['clic', '[data-invia]', 1700], ['clic', '[data-approva]', 2800]
        ]);
    };

    // ================= 8. permessi (Per aziende A.3): stesso gesto, risultato diverso a seconda del permesso; ogni gesto va nel registro
    demo.permessi = function (el) {
        var scelte = $$(el, 'input[name="permesso"]'), esito = $(el, 'p[data-esito]'), reg = $(el, '[data-registro]');
        function perm() { var s = scelte.filter(function (x) { return x.checked; })[0]; return s ? s.value : 'read'; }
        function riga(chi, cosa, dove) {
            var li = document.createElement('li');
            li.innerHTML = '<time>' + ora() + '</time><span><span class="chi">' + chi + '</span> <span class="cosa">' + cosa + '</span></span><span class="dove">' + dove + '</span>';
            reg.insertBefore(li, reg.firstChild); li.classList.add('dentro', 'nuova'); setTimeout(function () { li.classList.remove('nuova'); }, 900);
            while (reg.children.length > 5) reg.lastChild.remove();
        }
        $$(el, '[data-gesto]').forEach(function (b) {
            b.addEventListener('click', function () {
                var g = b.dataset.gesto, voce = b.dataset.voce, p = perm();
                esito.classList.toggle('errore', g !== 'scollega' && p === 'read');
                if (g === 'scollega') { esito.textContent = b.dataset.messaggio; riga('admin', T.log.scollega, 'PC-GIULIA · giulia'); return; }
                if (p === 'read') { esito.textContent = T.bloccato; riga('PC-GIULIA · giulia', T.log.blocco, voce); }
                else if (p === 'requests') { esito.textContent = T.richiesta; riga('PC-GIULIA · giulia', T.log.richiesta, voce); }
                else { esito.textContent = T.salvato; riga('PC-GIULIA · giulia', g === 'cancella' ? T.log.cancella : T.log.modifica, voce); }
            });
        });
        function etichetta(v) { return function () { return scelte.filter(function (x) { return x.value === v; })[0].parentElement; }; }
        giro(el, [
            ['clic', etichetta('read'), 700], ['clic', '[data-gesto="modifica"]', 2200],
            ['clic', etichetta('requests'), 700], ['clic', '[data-gesto="modifica"]', 2200],
            ['clic', etichetta('write'), 700], ['clic', '[data-gesto="cancella"]', 2400]
        ]);
    };

    // ================= 9. filtri del registro (Per aziende A.6)
    demo.filtri = function (el) {
        var bott = $$(el, '[data-filtro]'), righe = $$(el.closest('.registro') || el, 'li');
        bott.forEach(function (b) {
            b.addEventListener('click', function () {
                var f = b.dataset.filtro;
                bott.forEach(function (x) { x.setAttribute('aria-pressed', x === b); });
                righe.forEach(function (r) { r.hidden = f !== 'tutti' && (r.dataset.tipo || '').split(' ').indexOf(f) < 0; });
            });
        });
    };

    // ================= 10. cassaforte (Sicurezza S.2): si apre con "Windows Hello", la password si vede solo da aperta
    demo.cassaforte = function (el) {
        var sb = $(el, '[data-sblocca]'), mo = $(el, '[data-mostra]'), bl = $(el, '[data-blocca]');
        var pw = $(el, '[data-rivela]'), stato = $(el, '[data-stato]'), coperta = pw.textContent;
        el.classList.remove('aperta');
        function chiudi() { el.classList.remove('aperta', 'attesa'); pw.textContent = coperta; stato.textContent = T.bloccata; mo.disabled = true; bl.disabled = true; sb.disabled = false; mo.setAttribute('aria-pressed', 'false'); }
        sb.addEventListener('click', function () {
            sb.disabled = true; el.classList.add('attesa'); stato.textContent = T.attesa;
            setTimeout(function () { el.classList.remove('attesa'); el.classList.add('aperta'); stato.textContent = T.aperta; mo.disabled = false; bl.disabled = false; }, calmo ? 0 : 900);
        });
        mo.addEventListener('click', function () {
            var vis = mo.getAttribute('aria-pressed') === 'true';
            mo.setAttribute('aria-pressed', !vis); pw.textContent = vis ? coperta : pw.dataset.rivela;
        });
        bl.addEventListener('click', chiudi);
        chiudi();
    };

    // ================= 11. associazione tasti (Prodotto P.5): ogni combinazione al PC remoto o qui, con eccezione per una connessione
    demo.tasti = function (el) {
        var ecc = $(el, 'input[data-eccezione]');
        function aggiorna() {
            $$(el, '[data-combo]').forEach(function (r) {
                var dove = r.querySelector('input:checked').value;
                var e = ecc.checked && r.dataset.eccezione ? r.dataset.eccezione : dove;
                var out = $(r, '[data-dove]');
                out.textContent = e === 'remoto' ? (en ? 'remote PC' : 'PC remoto') : (en ? 'this PC' : 'questo PC');
                r.classList.toggle('qui', e !== 'remoto');
                r.classList.toggle('eccezione', ecc.checked && !!r.dataset.eccezione);
            });
        }
        $$(el, 'input').forEach(function (i) { i.addEventListener('change', aggiorna); });
        aggiorna();
    };

    // ================= 12. Claude nella sessione (Prodotto P.6): passo per passo, con lo schermo che cambia; Pausa e Ferma
    demo.claude = function (el) {
        var passi = JSON.parse($(el, '[data-passi]').textContent);
        var log = $(el, '[data-log]'), avvia = $(el, '[data-avvia]'), pausa = $(el, '[data-pausa]'), ferma = $(el, '[data-ferma]');
        var stato = $(el, '[data-schermo]'), i = 0, t = null, inPausa = false;
        function passo() {
            if (inPausa) return;
            if (i >= passi.length) { fine(); return; }
            var p = passi[i++];
            var li = document.createElement('li');
            li.innerHTML = '<code>' + p[0] + '</code><span>' + p[1] + '</span>';
            log.appendChild(li); log.scrollTop = log.scrollHeight;
            if (p[2]) stato.dataset.fase = p[2];
            t = setTimeout(passo, calmo ? 300 : 1100);
        }
        function fine() { clearTimeout(t); stato.classList.add('finito'); avvia.disabled = false; pausa.disabled = true; ferma.disabled = true; avvia.textContent = en ? 'Run again' : 'Riprova'; }
        avvia.addEventListener('click', function () {
            log.innerHTML = ''; i = 0; inPausa = false; stato.dataset.fase = 'inizio'; stato.classList.remove('finito');
            avvia.disabled = true; pausa.disabled = false; ferma.disabled = false; pausa.textContent = en ? 'Pause' : 'Pausa'; passo();
        });
        pausa.addEventListener('click', function () {
            inPausa = !inPausa; pausa.textContent = inPausa ? (en ? 'Resume' : 'Riprendi') : (en ? 'Pause' : 'Pausa');
            if (!inPausa) passo(); else clearTimeout(t);
        });
        ferma.addEventListener('click', function () {
            var li = document.createElement('li'); li.className = 'stop';
            li.innerHTML = '<code>' + (en ? 'stopped' : 'fermato') + '</code><span>' + (en ? 'Claude stopped by the user.' : 'Claude fermato dall’utente.') + '</span>';
            log.appendChild(li); fine();
        });
        giro(el, [['clic', '[data-avvia]', (calmo ? 300 : 1100) * passi.length + 2500]]);
    };

    // ================= 13. lato tecnico di Teams (Prodotto P.9): cosa succede sul PC con sola lettura, richieste, scrittura.
    // I messaggi sono quelli del programma.
    demo.teams = function (el) {
        var M = en ? {
            ro: 'This PC is not allowed to do this: with read-only access to the Teams server you can only view, not create, change or delete.',
            req: 'Change request sent to the administrator: the entry stays as it is until approved.',
            nuova: 'New entry request sent to the administrator: it will appear once approved.',
            coda: 'The server does not respond: the request is saved on this PC (shown with the clock) and will go to the administrator as soon as the server is reachable.',
            rif: 'Request refused by the administrator: {0}. Note: {1}', nota: 'DC01 is not moving, the address stays .10',
            scritto: 'Saved on the server: every PC receives it at the next sync.', nascosti: 'Read-only: Edit, Duplicate, Delete and Rename are not shown on server entries.'
        } : {
            ro: 'Questo PC non ha il permesso per farlo: con la sola lettura sul server Teams si può solo vedere, non creare, modificare o eliminare.',
            req: 'Richiesta di modifica inviata all’amministratore: la voce resta com’è fino all’approvazione.',
            nuova: 'Richiesta di nuova voce inviata all’amministratore: la vedi subito con l’orologio. Se approva diventa condivisa, se rifiuta sparisce.',
            coda: 'Il server non risponde: la richiesta è salvata su questo PC (la vedi con l’orologio) e partirà per l’amministratore appena il server torna raggiungibile.',
            rif: 'Richiesta rifiutata dall’amministratore: {0}. Nota: {1}', nota: 'DC01 non si sposta, l’indirizzo resta .10',
            scritto: 'Salvata sul server: ogni PC la riceve alla prossima sincronizzazione.', nascosti: 'Sola lettura: Modifica, Duplica, Elimina e Rinomina non compaiono sulle voci del server.'
        };
        var scelte = $$(el, 'input[name="tc-permesso"]'), online = $(el, '[data-online]'), msg = $(el, '[data-msg]');
        var azioni = $(el, '[data-azioni]'), editor = $(el, '[data-editor]'), comeRich = $(el, '[data-come-richiesta]');
        var rifiuta = $(el, '[data-rifiuta]'), voce = $(el, '[data-voce-dc01]'), lista = $(el, '[data-lista]');
        function perm() { return scelte.filter(function (x) { return x.checked; })[0].value; }
        function attesa(riga, on) { var o = riga.querySelector('.tc__orologio'); if (o) o.hidden = !on; }
        function stato() {
            var p = perm();
            azioni.hidden = p === 'read';
            editor.hidden = true; rifiuta.hidden = true;
            msg.textContent = p === 'read' ? M.nascosti : ''; msg.classList.remove('errore');
        }
        scelte.forEach(function (s) { s.addEventListener('change', stato); });
        $(el, '[data-modifica]').addEventListener('click', function () {
            editor.hidden = false; comeRich.parentElement.hidden = perm() !== 'requests'; comeRich.checked = true; msg.textContent = '';
            fuoco(el, $(editor, 'input[type="text"]'));
        });
        $(el, '[data-salva]').addEventListener('click', function () {
            editor.hidden = true;
            if (perm() === 'requests' && comeRich.checked) {
                attesa(voce, true);
                msg.textContent = online.checked ? M.req : M.coda;
                rifiuta.hidden = !online.checked;
            } else msg.textContent = M.scritto;
        });
        $(el, '[data-nuova]').addEventListener('click', function () {
            var p = perm();
            if (p === 'read') { msg.textContent = M.ro; msg.classList.add('errore'); return; }
            msg.classList.remove('errore');
            var li = document.createElement('li'); li.className = 'tc__riga';
            li.innerHTML = '<b class="tag tag--rdp">RDP</b><span>SRV-Nuovo<code>10.20.0.30</code></span><span class="tc__stato"><span class="tc__nuvola" aria-label="' + (en ? 'shared' : 'condivisa') + '">☁</span>' + (p === 'requests' ? '<span class="tc__orologio" aria-label="' + (en ? 'waiting for approval' : 'in attesa di approvazione') + '">◷</span>' : '') + '</span>';
            lista.appendChild(li);
            msg.textContent = p === 'requests' ? (online.checked ? M.nuova : M.coda) : M.scritto;
        });
        rifiuta.addEventListener('click', function () { attesa(voce, false); rifiuta.hidden = true; msg.textContent = M.rif.replace('{0}', 'DC01').replace('{1}', M.nota); });
        stato();
        var prime = $$(lista, 'li').length;
        function etichetta(v) { return function () { return scelte.filter(function (x) { return x.value === v; })[0].parentElement; }; }
        giro(el, [
            ['fai', function () { $$(lista, 'li').slice(prime).forEach(function (li) { li.remove(); }); attesa(voce, false); }],   // da capo: via le voci aggiunte nel giro prima
            ['clic', etichetta('requests'), 900], ['clic', '[data-modifica]', 1000], ['clic', '[data-salva]', 2600], ['clic', '[data-rifiuta]', 3000],
            ['clic', etichetta('read'), 1800], ['clic', '[data-nuova]', 2800],
            ['clic', etichetta('write'), 900], ['clic', '[data-nuova]', 2600], ['clic', etichetta('read'), 1200]
        ]);
    };

    // ================= 14. temi (Prodotto P.8): i dieci temi del programma, con i colori calcolati dalle stesse regole
    // del programma (tinta dei grigi + accento in armonia), applicati a una finestra in miniatura
    demo.temi = function (el) {
        function hsl(h, s, l) {
            h = ((h % 360) + 360) % 360 / 360;
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
            function c(t) { t = t < 0 ? t + 1 : t > 1 ? t - 1 : t; return t < 1 / 6 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3 ? p + (q - p) * (2 / 3 - t) * 6 : p; }
            function b(v) { return ('0' + Math.round(Math.min(1, Math.max(0, v)) * 255).toString(16)).slice(-2); }
            return '#' + b(c(h + 1 / 3)) + b(c(h)) + b(c(h - 1 / 3));
        }
        function lum(x) { var n = parseInt(x.slice(1), 16); return [n >> 16 & 255, n >> 8 & 255, n & 255].map(function (v) { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); }).reduce(function (a, v, i) { return a + v * [0.2126, 0.7152, 0.0722][i]; }, 0); }
        function contr(a, b) { var x = lum(a), y = lum(b); return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05); }
        function meglioSu(bg, a, b) { return contr(a, bg) >= 4.5 || contr(a, bg) >= contr(b, bg) ? a : b; }
        function scuro(h, s, ah, as, al) {
            var t = hsl(h, Math.min(0.2, s), 0.93), acc = hsl(ah, as, al);
            return { title: hsl(h, s, 0.075), bg: hsl(h, s, 0.118), island: hsl(h, s, 0.15), sel: hsl(h, s, 0.21), line: hsl(h, s, 0.21), text: t, dim: hsl(h, Math.min(0.2, s), 0.64),
                     accent: acc, onAccent: meglioSu(acc, hsl(ah, as, 0.10), '#ffffff'), rdp: '#8ab4f8', vnc: '#f6aea9', ok: '#81c995' };
        }
        function chiaro(h, s, ah, as, al) {
            var t = hsl(h, Math.min(0.35, s + 0.15), 0.12);
            while (al > 0.15 && contr(hsl(ah, as, al), hsl(h, s, 0.82)) < 4.6) al -= 0.01;
            var acc = hsl(ah, as, al);
            return { title: hsl(h, s, 0.865), bg: hsl(h, s, 0.905), island: hsl(h, s, 0.945), sel: hsl(h, s, 0.82), line: hsl(h, s, 0.865), text: t, dim: hsl(h, Math.min(0.35, s + 0.15), 0.36),
                     accent: acc, onAccent: meglioSu(acc, '#ffffff', hsl(ah, as, 0.10)), rdp: hsl(216, 0.7, 0.36), vnc: hsl(4, 0.66, 0.36), ok: hsl(145, 0.62, 0.22) };
        }
        var TEMI = [
            ['Grafite', scuro(210, 0.03, 199, 0.95, 0.68)], ['Notte', scuro(226, 0.24, 262, 0.72, 0.76)], ['Asfalto', scuro(30, 0.05, 38, 0.92, 0.60)],
            ['Carta', chiaro(40, 0.10, 212, 0.80, 0.40)], ['Ghiaccio', chiaro(205, 0.32, 190, 0.90, 0.29)], ['Lavanda', chiaro(265, 0.30, 268, 0.48, 0.44)],
            ['Ardesia', chiaro(210, 0.16, 18, 0.72, 0.39)], ['Vino', chiaro(345, 0.32, 340, 0.62, 0.36)], ['Bosco', chiaro(150, 0.18, 156, 0.50, 0.30)], ['Terra', chiaro(35, 0.32, 72, 0.60, 0.28)]
        ];
        var fin = $(el, '.fin'), nome = $(el, '[data-tema-nome]'), scelte = $(el, '[data-temi]');
        TEMI.forEach(function (t, i) {
            var b = document.createElement('button'); b.type = 'button'; b.className = 'temi__voce'; b.dataset.tema = t[0]; b.setAttribute('aria-pressed', i === 0);
            b.innerHTML = '<span class="temi__campione" aria-hidden="true"><i></i><i></i><i></i></span>' + t[0];
            var c = $$(b, 'i'); c[0].style.background = t[1].bg; c[1].style.background = t[1].island; c[2].style.background = t[1].accent;
            b.addEventListener('click', function () { applica(t); $$(scelte, 'button').forEach(function (x) { x.setAttribute('aria-pressed', x === b); }); });
            scelte.appendChild(b);
        });
        function applica(t) {
            var c = t[1];
            [['title', c.title], ['bg', c.bg], ['island', c.island], ['sel', c.sel], ['row-sel', c.sel], ['line', c.line], ['edge', c.line], ['edge-2', c.sel],
             ['text', c.text], ['dim', c.dim], ['accent', c.accent], ['on-accent', c.onAccent], ['rdp', c.rdp], ['vnc', c.vnc], ['ok', c.ok]].forEach(function (v) { fin.style.setProperty('--app-' + v[0], v[1]); });
            nome.textContent = t[0];
        }
        applica(TEMI[0]);
        giro(el, ['Notte', 'Asfalto', 'Carta', 'Ghiaccio', 'Lavanda', 'Ardesia', 'Vino', 'Bosco', 'Terra', 'Grafite'].map(function (n) { return ['clic', '[data-tema="' + n + '"]', 1700]; }));
    };

    document.querySelectorAll('[data-demo]').forEach(function (el) {
        var f = demo[el.dataset.demo];
        if (!f) return;
        try { f(el); el.classList.add('demo--pronta'); } catch (e) { /* una demo rotta non deve fermare le altre */ }
    });
})();
