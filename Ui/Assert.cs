namespace Lemora
{
    internal static class Assert
    {
        // APP_NAME is the TECHNICAL name: the data folder (%APPDATA%\Lemora), the profile files (Lemora.json, Lemora.db),
        // the entries in the Windows Credential Manager and the PuTTY session names depend on it.
        // APP_DISPLAY_NAME is the name users SEE (titles, tray, Task Manager, texts).
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
