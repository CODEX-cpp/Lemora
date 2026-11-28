// Pagina iniziale: italiano se la prima lingua del browser e' l'italiano, inglese per tutte le altre; una scelta fatta a mano sul sito vince.
(function () {
    var l = null;
    try { l = localStorage.getItem('lemora-lingua'); } catch (e) { /* memoria non disponibile */ }
    if (l !== 'it' && l !== 'en') {
        var prima = ((navigator.languages && navigator.languages[0]) || navigator.language || 'en').toLowerCase();
        l = prima.indexOf('it') === 0 ? 'it' : 'en';
    }
    location.replace(l + '/' + location.hash);
})();
