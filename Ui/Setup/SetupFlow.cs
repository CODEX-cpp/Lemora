using System;
using System.IO;
using System.Linq;
using Lemora.Service;

namespace Lemora.Setup
{
    /// <summary>collega le finestre di installazione all'avvio dell'app (vedi AppInitHelper.InitOnStart)</summary>
    internal static class SetupFlow
    {
        /// <summary>l'utente ha scelto "Portatile" nella finestra iniziale: i dati stanno accanto all'exe</summary>
        public static bool ChosenPortable { get; private set; }

        /// <summary>processo avviato con permessi di amministratore per installare: mostra l'avanzamento ed esce. true = il processo deve terminare.</summary>
        public static bool HandleSetupCommand()
        {
            var args = Environment.GetCommandLineArgs();
            string Get(string name, string def)
            {
                var i = Array.IndexOf(args, "--" + name);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
            }

            // disinstallazione, fase 2: exe temporaneo con permessi di amministratore (vedi SetupEngine.StartUninstallElevated)
            if (args.Contains("--setup-uninstall-run"))
            {
                new SetupWindow(unRun: new UninstallRun { Dir = Get("dir", ""), Purge = Get("purge", "0") == "1", Mode = Get("mode", "local") == "teams" ? SetupMode.Teams : SetupMode.Local }, uninstall: true).ShowDialog();
                return true;
            }
            // disinstallazione, fase 1: conferma (lanciata dalla voce in "App installate" o dall'exe installato); non ha senso su un exe non installato
            if (args.Contains("--setup-uninstall"))
            {
                if (SetupEngine.IsInstalledCopy())
                    new SetupWindow(uninstall: true).ShowDialog();
                return true;
            }

            var opts = SetupOptions.Parse(args);
            if (opts == null) return false;
            new SetupWindow(opts).ShowDialog();
            return true;
        }

        /// <summary>
        /// La scelta del modo si mostra per un exe non ancora installato e senza dati accanto a se' (anche se in AppData ci sono dati
        /// mantenuti da una disinstallazione precedente).
        /// </summary>
        public static bool ShouldShowChooser(AppPathHelper portablePaths, AppPathHelper appDataPaths)
        {
#if FOR_MICROSOFT_STORE_ONLY
            return false;
#else
            if (Environment.GetEnvironmentVariable("LEMORA_NO_INSTALLER") == "1") return false;
            if (SetupEngine.IsInstalledCopy()) return false;
            if (File.Exists(AppPathHelper.FORCE_INTO_PORTABLE_MODE) || File.Exists(AppPathHelper.FORCE_INTO_APPDATA_MODE)) return false;
            if (File.Exists(portablePaths.ProfileJsonPath)) return false;
            // i dati in AppData da soli non bastano: dopo una disinstallazione che li mantiene l'exe non e' installato e va proposta l'installazione,
            // la copia installata poi ritrova da sola il database in AppData
            return true;
#endif
        }

        /// <summary>questo exe ha gia' una sua scelta fatta (dati accanto all'exe oppure un marcatore di modo): non e' una copia "nuova"</summary>
        private static bool HasOwnProfileBesideExe()
        {
            var dir = Path.GetDirectoryName(SetupEngine.ExePath) ?? AppContext.BaseDirectory;
            return File.Exists(new AppPathHelper(dir, dir).ProfileJsonPath)
                   || File.Exists(Path.Combine(dir, "Lemora-data", "Lemora.json"))
                   || File.Exists(Path.Combine(dir, AppPathHelper.FORCE_INTO_PORTABLE_MODE))
                   || File.Exists(Path.Combine(dir, AppPathHelper.FORCE_INTO_APPDATA_MODE));
        }

        /// <summary>
        /// L'app e' installata e si lancia un altro exe senza dati propri (es. quello scaricato per installare):
        /// invece di aprire l'app installata si chiede se disinstallare, riparare o usare questo file come portatile.
        /// </summary>
        public static bool NeedsMaintenanceChoice()
        {
#if FOR_MICROSOFT_STORE_ONLY
            return false;
#else
            if (Environment.GetEnvironmentVariable("LEMORA_NO_INSTALLER") == "1") return false;
            if (SetupEngine.InstalledLocation() == null || SetupEngine.IsInstalledCopy()) return false;
            return HasOwnProfileBesideExe() == false;
#endif
        }

        /// <summary>mostra la scelta disinstalla / ripara / portatile. true = si prosegue come versione portatile; false = il processo deve terminare.</summary>
        public static bool RunMaintenance()
        {
            var w = new SetupWindow(maintenance: true);
            w.ShowDialog();
            if (w.Outcome != SetupOutcome.Portable) return false;
            ChosenPortable = true;
            IsolateInstance();
            return true;
        }

        /// <summary>
        /// Una copia portatile ha la sua istanza: puo' girare insieme all'app installata (altrimenti il secondo avvio
        /// verrebbe girato all'app gia' aperta). Va chiamato prima che AppStartupHelper crei il canale tra istanze.
        /// </summary>
        public static void IsolateInstance()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LEMORA_INSTANCE_ID")) == false) return;
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(SetupEngine.ExePath.ToLowerInvariant()));
            Environment.SetEnvironmentVariable("LEMORA_INSTANCE_ID", "portable-" + Convert.ToHexString(hash, 0, 4));
        }

        /// <summary>exe con dati propri accanto a se' (portatile gia' usato) e non installato: istanza separata dall'app installata</summary>
        public static void IsolateIfPortableCopy()
        {
#if !FOR_MICROSOFT_STORE_ONLY
            if (SetupEngine.IsInstalledCopy() || SetupEngine.InstalledLocation() == null) return;
            var dir = Path.GetDirectoryName(SetupEngine.ExePath) ?? AppContext.BaseDirectory;
            if (File.Exists(new AppPathHelper(dir, dir).ProfileJsonPath) || File.Exists(Path.Combine(dir, "Lemora-data", "Lemora.json")) || File.Exists(Path.Combine(dir, AppPathHelper.FORCE_INTO_PORTABLE_MODE)))
                IsolateInstance();
#endif
        }

        /// <summary>mostra la scelta del modo. true = si prosegue con l'avvio normale (Portatile); false = il processo deve terminare.</summary>
        public static bool RunChooser()
        {
            var w = new SetupWindow();
            w.ShowDialog();
            if (w.Outcome == SetupOutcome.Portable)
            {
                ChosenPortable = true;
                return true;
            }
            return false;
        }
    }
}
