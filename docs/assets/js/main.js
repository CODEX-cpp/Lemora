// main.js: tema, lingua ricordata, entrate allo scorrimento, indice che segue la lettura,
// animazioni con un motivo (app che cerca e apre, cassaforte che si apre, registro che si riempie).
// Tutto facoltativo: senza JS la pagina e' completa e ferma.
(function () {
    'use strict';
    var d = document.documentElement;
    var calmo = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    function salva(k, v) { try { localStorage.setItem(k, v); } catch (e) { /* niente memoria: va bene lo stesso */ } }

    // ---- tema: chiaro <-> scuro, la scelta vince sul sistema e si ricorda
    var btn = document.querySelector('[data-tema-btn]');
    if (btn) btn.addEventListener('click', function () {
        var scuroOra = d.getAttribute('data-theme') ? d.getAttribute('data-theme') === 'dark' : window.matchMedia('(prefers-color-scheme: dark)').matches;
        var nuovo = scuroOra ? 'light' : 'dark';
        d.setAttribute('data-theme', nuovo);
        salva('lemora-tema', nuovo);
    });

    // ---- lingua scelta a mano: la pagina iniziale non la ricambia piu'
    document.querySelectorAll('[data-lang]').forEach(function (a) {
        a.addEventListener('click', function () { salva('lemora-lingua', a.getAttribute('data-lang')); });
    });

    // ---- ingrandimento delle schermate: clic (o Invio) su una foto la apre a grandezza vera in un dialogo; Esc o clic chiude
    var en = d.lang === 'en';
    var foto = Array.prototype.slice.call(document.querySelectorAll('.fig__palco img')).filter(function (i) { return !i.closest('[data-demo]') && (i.getAttribute('width') || 0) >= 600; });
    if (foto.length && window.HTMLDialogElement) {
        var dlg = document.createElement('dialog');
        dlg.className = 'lente';
        dlg.setAttribute('aria-label', en ? 'Enlarged screenshot' : 'Schermata ingrandita');
        dlg.innerHTML = '<button type="button" class="lente__chiudi">' + (en ? 'Close' : 'Chiudi') + '</button><img alt="">';
        document.body.appendChild(dlg);
        var grande = dlg.querySelector('img');
        dlg.addEventListener('click', function (e) { if (e.target !== grande) dlg.close(); });
        foto.forEach(function (i) {
            i.classList.add('ingrandibile');
            i.tabIndex = 0;
            i.setAttribute('role', 'button');
            i.setAttribute('aria-label', (en ? 'Enlarge: ' : 'Ingrandisci: ') + (i.alt || ''));
            function apri() {
                grande.src = i.getAttribute('src'); grande.alt = i.alt; dlg.showModal(); dlg.scrollTop = 0; dlg.scrollLeft = 0;
                dlg.addEventListener('close', function () { i.focus({ preventScroll: true }); }, { once: true });
            }
            i.addEventListener('click', apri);
            i.addEventListener('keydown', function (e) { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); apri(); } });
        });
    }

    // ---- indice laterale: segna la sezione che si sta leggendo
    var link = Array.prototype.slice.call(document.querySelectorAll('.indice a[href^="#"]'));
    if (link.length && 'IntersectionObserver' in window) {
        var perId = {};
        link.forEach(function (a) { perId[a.getAttribute('href').slice(1)] = a; });
        var guarda = new IntersectionObserver(function (voci) {
            voci.forEach(function (v) {
                if (!v.isIntersecting) return;
                link.forEach(function (a) { a.removeAttribute('aria-current'); });
                var a = perId[v.target.id]; if (a) a.setAttribute('aria-current', 'true');
            });
        }, { rootMargin: '-20% 0px -70% 0px' });
        Object.keys(perId).forEach(function (id) { var s = document.getElementById(id); if (s) guarda.observe(s); });
    }

    // ---- entrate allo scorrimento: una volta sola per elemento
    var daVedere = document.querySelectorAll('[data-anim], .fig, .schema, .flusso, .porte, .passi, .registro');
    if ('IntersectionObserver' in window && !calmo) {
        var io = new IntersectionObserver(function (voci) {
            voci.forEach(function (v) {
                if (!v.isIntersecting) return;
                v.target.classList.add('visto');
                if (v.target.matches('.cassaforte:not([data-demo])')) apriCassaforte(v.target);   // quella con data-demo la apre chi legge
                if (v.target.matches('.registro')) riempiRegistro(v.target);
                io.unobserve(v.target);
            });
        }, { rootMargin: '0px 0px -12% 0px', threshold: 0.15 });
        daVedere.forEach(function (el) { io.observe(el); });
    } else {
        daVedere.forEach(function (el) { el.classList.add('visto'); });
        document.querySelectorAll('.registro li').forEach(function (li) { li.classList.add('dentro'); });
        var c = document.querySelector('.cassaforte:not([data-demo])'); if (c) c.classList.add('aperta');
    }

    // ---- registro della console: le righe arrivano dalla piu' vecchia alla piu' nuova, l'ultima resta evidenziata un attimo
    function riempiRegistro(el) {
        var righe = Array.prototype.slice.call(el.querySelectorAll('li')).reverse();
        righe.forEach(function (li, i) {
            setTimeout(function () {
                li.classList.add('dentro', 'nuova');
                setTimeout(function () { li.classList.remove('nuova'); }, 900);
            }, 300 + i * 420);
        });
    }

    // ---- cassaforte: si apre con "Windows Hello" e la password si rivela lettera per lettera, poi si richiude
    function apriCassaforte(el) {
        var pw = el.querySelector('[data-rivela]');
        if (!pw) return;
        var vera = pw.getAttribute('data-rivela'), coperta = pw.textContent;
        setTimeout(function () {
            el.classList.add('aperta');
            var i = 0;
            var t = setInterval(function () {
                i++;
                pw.textContent = vera.slice(0, i) + coperta.slice(i);
                if (i >= vera.length) {
                    clearInterval(t);
                    setTimeout(function () { pw.textContent = coperta; el.classList.remove('aperta'); }, 2600);
                }
            }, 70);
        }, 600);
    }

    // ---- hero della home: scrive la ricerca, filtra l'elenco, apre la sessione e la linguetta delle password. Una volta sola.
    var hero = document.querySelector('[data-hero]');
    if (!hero) return;
    var campo = hero.querySelector('[data-hero-type]');
    var righe = Array.prototype.slice.call(hero.querySelectorAll('[data-hero-list] li'));
    var scheda = hero.querySelector('[data-hero-tab]');
    var cassetto = hero.querySelector('[data-hero-drawer]');
    var nota = hero.querySelector('.app__nota');
    var testo = 'fs';   // come nell'app vera: la ricerca guarda nomi, indirizzi e utenti (non le cartelle)

    function filtra(q) {
        var parole = q.trim().split(/\s+/).filter(Boolean);
        righe.forEach(function (r) {
            var k = r.getAttribute('data-k');
            var ok = parole.every(function (p) { return k.indexOf(p) >= 0; });
            r.classList.toggle('fuori', parole.length > 0 && !ok);
        });
    }
    function fine() {
        campo.textContent = testo; filtra(testo);
        righe[0].classList.add('scelta'); scheda.classList.add('aperta'); cassetto.classList.add('aperto'); nota.classList.add('mostra');
    }
    if (calmo) { fine(); return; }

    var i = 0;
    setTimeout(function scrivi() {
        i++;
        campo.textContent = testo.slice(0, i);
        filtra(campo.textContent);
        if (i < testo.length) { setTimeout(scrivi, 110 + Math.random() * 70); return; }
        setTimeout(function () { righe[0].classList.add('scelta'); }, 350);
        setTimeout(function () { scheda.classList.add('aperta'); }, 900);
        setTimeout(function () { cassetto.classList.add('aperto'); }, 1500);
        setTimeout(function () { nota.classList.add('mostra'); }, 2400);
    }, 900);
})();
