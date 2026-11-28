using System;
using System.IO;
using System.Linq;
using Lemora.Service;

namespace Lemora.Setup
{
    /// <summary>connects the installation windows to the app startup (see AppInitHelper.InitOnStart)</summary>
    internal static class SetupFlow
    {
        /// <summary>the user chose "Portable" in the initial window: the data sits next to the exe</summary>
        public static bool ChosenPortable { get; private set; }

        /// <summary>process started with administrator rights to install: shows the progress and exits. true = the process must terminate.</summary>
        public static bool HandleSetupCommand()
        {
            var args = Environment.GetCommandLineArgs();
            string Get(string name, string def)
            {
                var i = Array.IndexOf(args, "--" + name);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
            }

            // uninstall, phase 2: temporary exe with administrator rights (see SetupEngine.StartUninstallElevated)
            if (args.Contains("--setup-uninstall-run"))
            {
                // like the update: only the folder registered in Windows for Lemora, never the one from the command line, and "delete everyone's data"
                // is confirmed again here (with administrator rights). Before, launching the launcher with these arguments was enough (security hardening)
                var installed = SetupEngine.InstalledLocation();
                static string N(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
                if (installed == null || !string.Equals(N(installed), N(Get("dir", "")), StringComparison.OrdinalIgnoreCase))
                {
                    Shawn.Utils.SimpleLogHelper.Warning("Uninstall: folder different from the installed one, cancelled");
                    return true;
                }
                var purge = Get("purge", "0") == "1";
                if (purge && System.Windows.MessageBox.Show(SetupText.T("purge_confirm_elevated"), "Lemora", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.No) != System.Windows.MessageBoxResult.Yes)
                    purge = false;
                new SetupWindow(unRun: new UninstallRun { Dir = installed, Purge = purge, Mode = Get("mode", "local") == "teams" ? SetupMode.Teams : SetupMode.Local }, uninstall: true).ShowDialog();
                return true;
            }
            // automatic update, administrator side: no window, the result is the exit code (see SetupEngine.RunUpdateElevated)
            if (args.Contains("--setup-update"))
                Environment.Exit(SetupEngine.RunUpdateElevated(Get("zip", ""), Get("dir", ""), Get("version", "")));
            // uninstall, phase 1: confirmation (launched from the entry in "Installed apps" or from the installed exe); makes no sense on a non-installed exe
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
        /// The mode choice is shown for an exe not yet installed and without data next to itself (even if AppData holds data
        /// kept from a previous uninstall).
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
            // data in AppData alone is not enough: after an uninstall that keeps it the exe is not installed and the installation must be offered,
            // the installed copy then finds the database in AppData on its own
            return true;
#endif
        }

        /// <summary>this exe already has its own choice made (data next to the exe or a mode marker): it is not a "new" copy</summary>
        private static bool HasOwnProfileBesideExe()
        {
            var dir = Path.GetDirectoryName(SetupEngine.ExePath) ?? AppContext.BaseDirectory;
            return File.Exists(new AppPathHelper(dir, dir).ProfileJsonPath)
                   || File.Exists(Path.Combine(dir, "Lemora-data", "Lemora.json"))
                   || File.Exists(Path.Combine(dir, AppPathHelper.FORCE_INTO_PORTABLE_MODE))
                   || File.Exists(Path.Combine(dir, AppPathHelper.FORCE_INTO_APPDATA_MODE));
        }

        /// <summary>
        /// The app is installed and another exe without its own data is launched (e.g. the one downloaded to install):
        /// instead of opening the installed app, the user is asked whether to uninstall, repair or use this file as portable.
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

        /// <summary>shows the uninstall / repair / portable choice. true = continue as the portable version; false = the process must terminate.</summary>
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
        /// A portable copy has its own instance: it can run alongside the installed app (otherwise the second start
        /// would be forwarded to the already open app). Must be called before AppStartupHelper creates the channel between instances.
        /// </summary>
        public static void IsolateInstance()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LEMORA_INSTANCE_ID")) == false) return;
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(SetupEngine.ExePath.ToLowerInvariant()));
            Environment.SetEnvironmentVariable("LEMORA_INSTANCE_ID", "portable-" + Convert.ToHexString(hash, 0, 4));
        }

        /// <summary>exe with its own data next to itself (portable already used) and not installed: instance separate from the installed app</summary>
        public static void IsolateIfPortableCopy()
        {
#if !FOR_MICROSOFT_STORE_ONLY
            if (SetupEngine.IsInstalledCopy() || SetupEngine.InstalledLocation() == null) return;
            var dir = Path.GetDirectoryName(SetupEngine.ExePath) ?? AppContext.BaseDirectory;
            if (File.Exists(new AppPathHelper(dir, dir).ProfileJsonPath) || File.Exists(Path.Combine(dir, "Lemora-data", "Lemora.json")) || File.Exists(Path.Combine(dir, AppPathHelper.FORCE_INTO_PORTABLE_MODE)))
                IsolateInstance();
#endif
        }

        /// <summary>shows the mode choice. true = continue with the normal start (Portable); false = the process must terminate.</summary>
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
