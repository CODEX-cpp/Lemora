namespace Lemora
{
    internal static class Assert
    {
        // APP_NAME e' il nome TECNICO: da lui dipendono la cartella dei dati (%APPDATA%\Lemora), i file di profilo (Lemora.json, Lemora.db),
        // le voci nel gestore credenziali di Windows e i nomi delle sessioni PuTTY.
        // APP_DISPLAY_NAME e' il nome che si VEDE (titoli, tray, gestione attivita', testi).
        private const string APP_NAME_RAW = "Lemora";
        private const string APP_BRAND = "Lemora";
#if DEBUG
        public const string APP_NAME = $"{APP_NAME_RAW}_Debug";
#if FOR_MICROSOFT_STORE_ONLY
        public const string APP_DISPLAY_NAME = $"{APP_BRAND}(Store)_Debug";
#else
        public const string APP_DISPLAY_NAME = $"{APP_BRAND}_Debug";
#endif
#else
        public const string APP_NAME = $"{APP_NAME_RAW}";
#if FOR_MICROSOFT_STORE_ONLY
        public const string APP_DISPLAY_NAME = $"{APP_BRAND}(Store)";
#else
        public const string APP_DISPLAY_NAME = APP_BRAND;
#endif
#endif


        public const string SENTRY_IO_DEN = "===REPLACE_ME_WITH_SENTRY_IO_DEN===";
    }
}
