using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using Microsoft.Win32;

namespace Lemora.Setup
{
    internal enum SetupMode { Portable, Local, Teams }

    internal class SetupOptions
    {
        public string Dir = SetupEngine.DefaultDir;
        public SetupMode Mode = SetupMode.Local;
        public bool Desktop = true;
        public bool StartMenu = true;
        public bool AutoStart = false;

        public string ToArguments() =>
            $"--setup-install --dir \"{Dir.TrimEnd('\\')}\" --mode {Mode.ToString().ToLower()} --desktop {(Desktop ? 1 : 0)} --start {(StartMenu ? 1 : 0)} --autostart {(AutoStart ? 1 : 0)}";

        public static SetupOptions? Parse(string[] args)
        {
            if (args.Contains("--setup-install") == false) return null;
            string Get(string name, string def)
            {
                var i = Array.IndexOf(args, "--" + name);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
            }
            var o = new SetupOptions
            {
                Dir = Get("dir", SetupEngine.DefaultDir),
                Desktop = Get("desktop", "1") == "1",
                StartMenu = Get("start", "1") == "1",
                AutoStart = Get("autostart", "0") == "1",
                Mode = Get("mode", "local") == "teams" ? SetupMode.Teams : SetupMode.Local,
            };
            return o;
        }
    }

    /// <summary>
    /// L'exe in un solo file si installa da solo: si copia in Programmi, crea collegamenti e voce in "App installate".
    /// Va eseguito con permessi di amministratore (scrive in Program Files e in HKLM).
    /// </summary>
    internal static class SetupEngine
    {
        private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Lemora";
        private const string AppKey = @"SOFTWARE\Lemora";
        private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        public const string ExeName = "Lemora.exe";
        private const string AppName = "Lemora";

        public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, ExeName);
        public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);

        /// <summary>true se la cartella e' una cartella tutta nostra: mai cancellare una cartella scelta male (es. Programmi)</summary>
        public static bool IsOurFolderName(string dir) =>
            Path.GetFileName(dir.TrimEnd('\\')) is { } n && string.Equals(n, AppName, StringComparison.OrdinalIgnoreCase);

        /// <summary>percorso (cartella + exe) dell'installazione registrata</summary>
        private static (string? dir, string exe) FindRegistered()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(AppKey);
                var loc = k?.GetValue("InstallLocation") as string;
                if (!string.IsNullOrWhiteSpace(loc)) return (loc, ExeName);
            }
            catch { /* nessuna installazione */ }
            return (null, ExeName);
        }

        public static bool IsAdmin()
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private static string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));

        /// <summary>vero se questo exe e' la copia installata da questo meccanismo (e non un exe qualsiasi)</summary>
        public static bool IsInstalledCopy()
        {
            try
            {
                var (loc, _) = FindRegistered();
                if (string.IsNullOrWhiteSpace(loc)) return false;
                return string.Equals(Norm(loc), Norm(Path.GetDirectoryName(ExePath)!), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>cartella dell'app installata con questo meccanismo (anche se questo exe e' un altro), null se non c'e' una installazione valida</summary>
        public static string? InstalledLocation()
        {
            try
            {
                var (loc, exe) = FindRegistered();
                return string.IsNullOrWhiteSpace(loc) || File.Exists(Path.Combine(loc, exe)) == false ? null : loc;
            }
            catch { return null; }
        }

        /// <summary>opzioni dell'installazione attuale (cartella, modo, collegamenti, avvio con Windows) per ripararla con le stesse scelte</summary>
        public static SetupOptions CurrentInstallOptions()
        {
            bool autoStart = false;
            try { using var k = Registry.LocalMachine.OpenSubKey(RunKey); autoStart = k?.GetValue(AppName) != null; } catch { /* non letto: spento */ }
            bool Lnk(Environment.SpecialFolder f) => File.Exists(Path.Combine(Environment.GetFolderPath(f), AppName + ".lnk"));
            return new SetupOptions
            {
                Dir = InstalledLocation() ?? DefaultDir,
                Mode = InstalledMode(),
                Desktop = Lnk(Environment.SpecialFolder.CommonDesktopDirectory),
                StartMenu = Lnk(Environment.SpecialFolder.CommonPrograms),
                AutoStart = autoStart,
            };
        }

        public static SetupMode InstalledMode()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(AppKey);
                return (k?.GetValue("Mode") as string) == "teams" ? SetupMode.Teams : SetupMode.Local;
            }
            catch { return SetupMode.Local; }
        }

        /// <summary>rilancia questo exe con i permessi di amministratore; false se l'utente rifiuta la richiesta di Windows</summary>
        public static bool RelaunchElevated(string arguments)
        {
            try
            {
                Process.Start(new ProcessStartInfo(ExePath, arguments) { UseShellExecute = true, Verb = "runas" });
                return true;
            }
            catch (System.ComponentModel.Win32Exception) // 1223: annullato dall'utente
            {
                return false;
            }
        }

        public static int StepCount => 4;

        /// <param name="report">(passo completato 0..4, descrizione del passo che inizia)</param>
        public static void Install(SetupOptions o, Action<int> report)
        {
            var target = Path.Combine(o.Dir, ExeName);

            // 1. file
            report(0);
            Directory.CreateDirectory(o.Dir);
            StopRunning(target);
            if (string.Equals(Norm(ExePath), Norm(target), StringComparison.OrdinalIgnoreCase) == false)
                File.Copy(ExePath, target, true);

            // 2. collegamenti
            report(1);
            if (o.StartMenu) CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName + ".lnk"), target, o.Dir);
            if (o.Desktop) CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), AppName + ".lnk"), target, o.Dir);

            // 3. registrazione in Windows
            report(2);
            var version = "";
            try { version = FileVersionInfo.GetVersionInfo(target).ProductVersion?.Split('+')[0] ?? ""; } catch { /* solo informativo */ }
            // la versione del progetto non cambia da una build all'altra: si aggiunge la data dell'exe (la copia la conserva), cosi' si distinguono gli aggiornamenti
            try { version = $"{version} (build {File.GetLastWriteTime(target):yyyy-MM-dd HH:mm})".Trim(); } catch { /* solo informativo */ }
            long sizeKb = 0;
            try { sizeKb = new FileInfo(target).Length / 1024; } catch { /* solo informativo */ }
            using (var k = Registry.LocalMachine.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", AppName);
                k.SetValue("DisplayVersion", version);
                k.SetValue("Publisher", AppName);
                k.SetValue("InstallLocation", o.Dir);
                k.SetValue("DisplayIcon", target);
                k.SetValue("UninstallString", $"\"{target}\" --setup-uninstall");
                k.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, sizeKb), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            using (var k = Registry.LocalMachine.CreateSubKey(AppKey))
            {
                k.SetValue("InstallLocation", o.Dir);
                k.SetValue("Mode", o.Mode == SetupMode.Teams ? "teams" : "local");
                k.SetValue("Version", version);
            }
            using (var k = Registry.LocalMachine.CreateSubKey(RunKey))
            {
                if (o.AutoStart) k.SetValue(AppName, $"\"{target}\" --start-minimized");
                else k.DeleteValue(AppName, false);
            }

            // i file .mora si aprono con questa copia di Lemora
            Lemora.Service.MoraFile.Register(target, true);

            // 4. i dati: ogni utente li crea da solo al suo primo avvio (database vuoto in AppData)
            report(3);
            report(4);
        }

        // ---------------------------------------------------------------- disinstallazione (solo Locale e Teams)

        private const string UninstallTempDirName = "Lemora-uninstall";
        public const int UninstallStepCount = 5;

        /// <summary>
        /// L'exe installato non puo' cancellare se stesso: si copia in una cartella temporanea e da li' si rilancia con i permessi di amministratore.
        /// false se l'utente rifiuta la richiesta di Windows.
        /// </summary>
        public static bool StartUninstallElevated(string dir, bool purge, SetupMode mode)
        {
            var tmpDir = Path.Combine(Path.GetTempPath(), UninstallTempDirName);
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
            Directory.CreateDirectory(tmpDir);
            string tmpExe;
            if (string.IsNullOrEmpty(typeof(SetupEngine).Assembly.Location))
            {
                // exe in un solo file: basta lui
                tmpExe = Path.Combine(tmpDir, UninstallTempDirName + ".exe");
                File.Copy(ExePath, tmpExe, true);
            }
            else
            {
                // programma in piu' file (es. installato con la build ReadyToRun): Lemora.exe da solo non parte senza Lemora.dll e le librerie,
                // si copia tutta la cartella del programma tenendo il nome dell'exe
                var src = Path.GetDirectoryName(ExePath)!;
                foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
                {
                    var to = Path.Combine(tmpDir, Path.GetRelativePath(src, f));
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Copy(f, to, true);
                }
                tmpExe = Path.Combine(tmpDir, ExeName);
            }
            try
            {
                Process.Start(new ProcessStartInfo(tmpExe, $"--setup-uninstall-run --dir \"{dir.TrimEnd('\\')}\" --purge {(purge ? 1 : 0)} --mode {mode.ToString().ToLower()}") { UseShellExecute = true, Verb = "runas" });
                return true;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        public static void Uninstall(string dir, bool purge, Action<int> report)
        {
            // 1. chiusura dell'app (solo i processi di questa installazione)
            report(0);
            StopRunning(Path.Combine(dir, ExeName));

            // 2. scollegamento dal server Teams: il server non esiste ancora, per ora si toglie solo la copia locale insieme ai dati dell'utente (se richiesto)
            report(1);

            // 3. file
            report(2);
            DeleteInstallDir(dir);
            TryDeleteDir(Path.Combine(Path.GetTempPath(), ".net", AppName));

            // 4. collegamenti e registrazione in Windows
            report(3);
            foreach (var f in new[] { Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Startup })
                TryDeleteFile(Path.Combine(Environment.GetFolderPath(f), AppName + ".lnk"));
            try { Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false); } catch { /* non c'era */ }
            try { Registry.LocalMachine.DeleteSubKeyTree(AppKey, false); } catch { /* non c'era */ }
            try { using var k = Registry.LocalMachine.OpenSubKey(RunKey, true); k?.DeleteValue(AppName, false); } catch { /* non c'era */ }
            try { using var k = Registry.CurrentUser.OpenSubKey(RunKey, true); k?.DeleteValue(AppName, false); } catch { /* non c'era */ }

            // 5. dati di tutti gli utenti (solo se richiesto)
            report(4);
            if (purge) PurgeAllUsersData();
            report(5);
        }

        /// <summary>l'exe temporaneo che esegue la disinstallazione si cancella da solo a processo finito</summary>
        public static void ScheduleSelfCleanup()
        {
            try
            {
                var tmpDir = Path.GetDirectoryName(ExePath)!;
                if (string.Equals(Path.GetFileName(tmpDir), UninstallTempDirName, StringComparison.OrdinalIgnoreCase) == false) return;
                var net = Path.Combine(Path.GetTempPath(), ".net", UninstallTempDirName);
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{tmpDir}\" & rmdir /s /q \"{net}\"") { CreateNoWindow = true, UseShellExecute = false });
            }
            catch { /* resta solo un file temporaneo */ }
        }

        private static void DeleteInstallDir(string dir)
        {
            var exe = Path.Combine(dir, ExeName);
            // si cancella la cartella intera solo se e' una cartella "Lemora" tutta nostra: mai una cartella scelta male (es. Programmi)
            bool ours = IsOurFolderName(dir);
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    if (ours) { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                    else { if (File.Exists(exe)) File.Delete(exe); }
                    return;
                }
                catch (Exception)
                {
                    System.Threading.Thread.Sleep(600); // un file ancora in uso: l'app si sta chiudendo
                }
            }
            if (ours) Directory.Delete(dir, true); // ultimo tentativo: se fallisce l'errore arriva alla finestra
            else File.Delete(exe);
        }

        private static void PurgeAllUsersData()
        {
            var roots = new System.Collections.Generic.List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName),
            };
            try
            {
                var users = Path.Combine((Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") + "\\", "Users");
                foreach (var u in Directory.GetDirectories(users))
                {
                    roots.Add(Path.Combine(u, "AppData", "Roaming", AppName));
                    roots.Add(Path.Combine(u, "AppData", "Local", AppName));
                }
            }
            catch { /* si cancellano almeno quelle dell'utente corrente */ }
            foreach (var r in roots) TryDeleteDir(r);
        }

        private static void TryDeleteDir(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { /* resta un residuo non critico */ }
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* resta un residuo non critico */ }
        }

        private static void StopRunning(string target)
        {
            try
            {
                int me = Environment.ProcessId;
                foreach (var p in Process.GetProcessesByName("Lemora").Where(x => x.Id != me))
                {
                    try
                    {
                        if (string.Equals(p.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase))
                        {
                            p.Kill();
                            p.WaitForExit(8000);
                        }
                    }
                    catch { /* processo gia' chiuso o non accessibile */ }
                }
            }
            catch { /* se non si riesce, la copia dara' un errore chiaro */ }
        }

        private static void CreateShortcut(string lnkPath, string target, string workDir)
        {
            var t = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell non disponibile");
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic lnk = shell.CreateShortcut(lnkPath);
            lnk.TargetPath = target;
            lnk.WorkingDirectory = workDir;
            lnk.IconLocation = target + ",0";
            lnk.Description = AppName;
            lnk.Save();
        }
    }
}
