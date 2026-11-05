namespace Lemora.View.ServerView
{
    /// <summary>
    /// Disposizione della Home secondo la larghezza della finestra (blueprint "Home responsive" di Claude Design).
    /// Si cambia la disposizione, non il contenuto:
    /// Wide   (>= 1100): cartelle con nomi, lista, dettaglio fisso a destra;
    /// Medium (800-1099): cartelle a sole icone, dettaglio sovrapposto a destra quando si sceglie una voce;
    /// Small  (560-799): cartelle nel menu a scomparsa (pulsante con le tre righe), dettaglio sovrapposto, righe a una riga;
    /// Square (< 560): come Small, ma il dettaglio prende il posto della lista e le righe non mostrano l'utente.
    /// </summary>
    public enum HomeLayoutMode { Wide, Medium, Small, Square }

    public static class HomeLayout
    {
        // soglie del blueprint; vanno sulla larghezza della pagina (quasi uguale a quella della finestra)
        public static HomeLayoutMode For(double width) =>
            width >= 1100 ? HomeLayoutMode.Wide :
            width >= 800 ? HomeLayoutMode.Medium :
            width >= 560 ? HomeLayoutMode.Small : HomeLayoutMode.Square;

        /// <summary>larghezza della ricerca quando non riempie lo spazio libero</summary>
        public static double SearchWidth(HomeLayoutMode mode) => mode == HomeLayoutMode.Wide ? 440 : 340;
    }
}
