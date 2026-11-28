// Caricato nel <head> prima dei CSS: applica il tema scelto in precedenza prima che la pagina si veda (niente lampo del tema sbagliato)
// e segna che il JavaScript c'e' (classe .js): solo allora gli elementi partono nascosti per le animazioni.
(function () {
    var d = document.documentElement;
    d.classList.add('js');
    try {
        var t = localStorage.getItem('lemora-tema');
        if (t === 'light' || t === 'dark') d.setAttribute('data-theme', t);
    } catch (e) { /* memoria del browser non disponibile: si segue il sistema */ }
})();
