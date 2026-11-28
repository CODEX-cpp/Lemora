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
        public bool BrowserExtension = true;

        public string ToArguments() =>
            $"--setup-install --dir \"{Dir.TrimEnd('\\')}\" --mode {Mode.ToString().ToLower()} --desktop {(Desktop ? 1 : 0)} --start {(StartMenu ? 1 : 0)} --autostart {(AutoStart ? 1 : 0)} --browserext {(BrowserExtension ? 1 : 0)}";

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
                BrowserExtension = Get("browserext", "1") == "1",
                Mode = Get("mode", "local") == "teams" ? SetupMode.Teams : SetupMode.Local,
            };
            return o;
        }
    }

    /// <summary>
    /// The single-file exe installs itself: it copies itself into Program Files, creates shortcuts and an entry in "Installed apps".
    /// Must run with administrator rights (it writes to Program Files and HKLM).
    /// </summary>
    internal static class SetupEngine
    {
        private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Lemora";
        private const string AppKey = @"SOFTWARE\Lemora";
        private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        public const string ExeName = "Lemora.exe";
        private const string AppName = "Lemora";
        private const string ContactUrl = "https://github.com/CODEX-cpp";

        public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, ExeName);
        public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);

        /// <summary>true if the folder is entirely ours: never delete a badly chosen folder (e.g. Program Files)</summary>
        public static bool IsOurFolderName(string dir) =>
            Path.GetFileName(dir.TrimEnd('\\')) is { } n && string.Equals(n, AppName, StringComparison.OrdinalIgnoreCase);

        /// <summary>path (folder + exe) of the registered installation</summary>
        private static (string? dir, string exe) FindRegistered()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(AppKey);
                var loc = k?.GetValue("InstallLocation") as string;
                if (!string.IsNullOrWhiteSpace(loc)) return (loc, ExeName);
            }
            catch { /* no installation */ }
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

        /// <summary>installation folder of this exe: if it sits in versions\&lt;version&gt; it is the folder above (installation with launcher), otherwise its own folder</summary>
        public static string ThisRoot => UpdateApplier.RootOfVersionedExe(ExePath) ?? Path.GetDirectoryName(ExePath)!;

        /// <summary>fixed path for shortcuts and start with Windows: the launcher (which does not change with updates) if the app is installed with versions, otherwise this exe</summary>
        public static string StableExePath
        {
            get
            {
                var root = UpdateApplier.RootOfVersionedExe(ExePath);
                return root != null && File.Exists(Path.Combine(root, ExeName)) ? Path.Combine(root, ExeName) : ExePath;
            }
        }

        /// <summary>true if this exe is the copy installed by this mechanism (and not just any exe)</summary>
        public static bool IsInstalledCopy()
        {
            try
            {
                var (loc, _) = FindRegistered();
                if (string.IsNullOrWhiteSpace(loc)) return false;
                return string.Equals(Norm(loc), Norm(ThisRoot), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>folder of the app installed by this mechanism (even if this exe is a different one), null if there is no valid installation</summary>
        public static string? InstalledLocation()
        {
            try
            {
                var (loc, exe) = FindRegistered();
                return string.IsNullOrWhiteSpace(loc) || File.Exists(Path.Combine(loc, exe)) == false ? null : loc;
            }
            catch { return null; }
        }

        /// <summary>options of the current installation (folder, mode, shortcuts, start with Windows) to repair it with the same choices</summary>
        public static SetupOptions CurrentInstallOptions()
        {
            bool autoStart = false;
            try { using var k = Registry.LocalMachine.OpenSubKey(RunKey); autoStart = k?.GetValue(AppName) != null; } catch { /* not read: off */ }
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

        /// <summary>relaunches this exe with administrator rights; false if the user declines the Windows prompt</summary>
        public static bool RelaunchElevated(string arguments)
        {
            try
            {
                Process.Start(new ProcessStartInfo(ExePath, arguments) { UseShellExecute = true, Verb = "runas" });
                return true;
            }
            catch (System.ComponentModel.Win32Exception) // 1223: cancelled by the user
            {
                return false;
            }
        }

        public static int StepCount => 4;

        /// <param name="report">(step completed 0..4, description of the step that starts)</param>
        public static void Install(SetupOptions o, Action<int> report)
        {
            var target = Path.Combine(o.Dir, ExeName);   // the launcher (or, without an embedded launcher, the app itself)

            // 1. files: launcher in the main folder, real app in versions\<version>, current.txt points to it (see UpdateApplier)
            report(0);
            if (IsUnsafeInstallDir(o.Dir)) throw new InvalidOperationException(SetupText.T("dir_in_profile"));
            // folder (or a parent) that is a link: the permissions would go to the target and the link would stay owned by whoever created it,
            // free to repoint it elsewhere (security hardening)
            if (PathHasReparsePoint(o.Dir)) throw new InvalidOperationException(SetupText.T("dir_reparse"));
            // folder already created by another user: besides owning it, they can keep a handle open with the right to change its
            // permissions even after we have reset them (security hardening)
            if (Directory.Exists(o.Dir) && !OwnedByAdmins(o.Dir)) throw new InvalidOperationException(SetupText.T("dir_not_admin"));
            // and no parent folder that a normal user can rename and replace (security hardening)
            if (!ParentsSafe(o.Dir)) throw new InvalidOperationException(SetupText.T("dir_in_profile"));
            // missing folders (parents included): created one by one already with the right permissions. Before, ParentsSafe skipped them and
            // CreateDirectory made them with inherited permissions (e.g. Modify for everyone under C:\), so anyone could rename them
            // (security hardening)
            CreateProtected(o.Dir);
            ProtectInstallDir(o.Dir);
            if (!InstallDirStillSafe(o.Dir)) throw new InvalidOperationException(SetupText.T("dir_unsafe_now"));
            StopRunning(o.Dir);
            var launcher = UpdateApplier.LauncherBytes();
            bool sameAsTarget = string.Equals(Norm(ExePath), Norm(target), StringComparison.OrdinalIgnoreCase);
            if (launcher != null && !sameAsTarget)
            {
                var runVersion = UpdateApplier.RunningVersion;
                var verExe = Path.Combine(o.Dir, UpdateApplier.VersionsDir, runVersion, ExeName);
                Directory.CreateDirectory(Path.GetDirectoryName(verExe)!);
                if (string.Equals(Norm(ExePath), Norm(verExe), StringComparison.OrdinalIgnoreCase) == false)
                    File.Copy(ExePath, verExe, true);
                File.WriteAllBytes(target, launcher);   // after the copy: if that fails, the previous installation stays
                UpdateApplier.SetCurrent(o.Dir, runVersion);
                UpdateApplier.Prune(o.Dir, runVersion);
            }
            else if (!sameAsTarget)
                File.Copy(ExePath, target, true);
            // FreeRDP licenses (bundled in Lemora.exe) in the installation folder, where whoever receives the program finds them
            Lemora.Model.ProtocolRunner.FreeRdpRunner.WriteLicense(o.Dir);

            // 2. shortcuts
            report(1);
            if (o.StartMenu) CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName + ".lnk"), target, o.Dir);
            if (o.Desktop) CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), AppName + ".lnk"), target, o.Dir);

            // 3. registration in Windows
            report(2);
            var version = "";
            // version and date are read from the real app (this exe), not from the launcher
            try { version = FileVersionInfo.GetVersionInfo(ExePath).ProductVersion?.Split('+')[0] ?? ""; } catch { /* informational only */ }
            // the project version does not change between builds: the exe date is added (the copy keeps it), so updates can be told apart
            try { version = $"{version} (build {File.GetLastWriteTime(ExePath):yyyy-MM-dd HH:mm})".Trim(); } catch { /* informational only */ }
            long sizeKb = 0;
            try { sizeKb = new FileInfo(ExePath).Length / 1024; } catch { /* informational only */ }
            using (var k = Registry.LocalMachine.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", AppName);
                k.SetValue("DisplayVersion", version);
                k.SetValue("Publisher", AppName);
                k.SetValue("URLInfoAbout", ContactUrl);
                k.SetValue("HelpLink", ContactUrl);
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
                // read by Lemora on first start (BrowserExtensionSetup.OfferAfterSetup): the extension is offered in the browser as a normal user, not from here (administrator)
                k.SetValue("BrowserExtension", o.BrowserExtension ? 1 : 0, RegistryValueKind.DWord);
            }
            using (var k = Registry.LocalMachine.CreateSubKey(RunKey))
            {
                if (o.AutoStart) k.SetValue(AppName, $"\"{target}\" --start-minimized");
                else k.DeleteValue(AppName, false);
            }

            // .mora files open with this copy of Lemora
            Lemora.Service.MoraFile.Register(target, true);

            // 4. data: each user creates it on their own first start (empty database in AppData)
            report(3);
            report(4);
        }

        /// <summary>
        /// own permissions of the installation folder, like Program Files: administrators and SYSTEM write, users only read and execute.
        /// A hand-picked folder (e.g. C:\Lemora) inherited Modify for all authenticated users from C:\: anyone could change
        /// the exe started by others or with administrator rights (security hardening)
        /// </summary>
        /// <summary>
        /// creates the folder and the missing parents, each already with the installation folder permissions (no moment with the inherited ones).
        /// If a folder appears in the meantime created by another user (non-administrator owner), it stops
        /// </summary>
        internal static void CreateProtected(string dir)
        {
            var missing = new Stack<DirectoryInfo>();
            for (var d = new DirectoryInfo(Path.GetFullPath(dir)); d != null && !d.Exists; d = d.Parent) missing.Push(d);
            while (missing.Count > 0)
            {
                var d = missing.Pop();
                System.IO.FileSystemAclExtensions.Create(d, InstallDirSecurity());
                if (!OwnedByAdmins(d.FullName) || PathHasReparsePoint(d.FullName)) throw new InvalidOperationException(SetupText.T("dir_not_admin"));
            }
        }

        internal static void ProtectInstallDir(string dir)
        {
            var sec = InstallDirSecurity();
            new DirectoryInfo(dir).SetAccessControl(sec);
            ResetChildren(dir, new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null));
        }

        private static System.Security.AccessControl.DirectorySecurity InstallDirSecurity()
        {
            var sec = new System.Security.AccessControl.DirectorySecurity();
            sec.SetAccessRuleProtection(true, false);
            const System.Security.AccessControl.InheritanceFlags all = System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit;
            void Add(System.Security.Principal.WellKnownSidType who, System.Security.AccessControl.FileSystemRights r) =>
                sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier(who, null), r, all,
                    System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
            Add(System.Security.Principal.WellKnownSidType.LocalSystemSid, System.Security.AccessControl.FileSystemRights.FullControl);
            Add(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, System.Security.AccessControl.FileSystemRights.FullControl);
            Add(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, System.Security.AccessControl.FileSystemRights.ReadAndExecute);
            // owner = Administrators: whoever had created the folder earlier (C:\ lets everyone create one) stayed the owner and could give themselves
            // write access again whenever they wanted (security hardening)
            var admins = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
            sec.SetOwner(admins);
            return sec;
        }

        /// <summary>
        /// every file and subfolder goes back to inheriting from the installation folder, with Administrators as owner: a file prepared earlier
        /// with its own protected permissions (e.g. Lemora.exe or versions\...) would have kept them even after being copied over. Links (junction, symlink)
        /// are removed without following them: otherwise the permissions of folders outside the installation would be changed
        /// </summary>
        private static void ResetChildren(string dir, System.Security.Principal.SecurityIdentifier owner)
        {
            foreach (var p in Directory.EnumerateFileSystemEntries(dir))
            {
                var attr = File.GetAttributes(p);
                bool isDir = (attr & FileAttributes.Directory) != 0;
                if ((attr & FileAttributes.ReparsePoint) != 0)
                {
                    if (isDir) Directory.Delete(p, false); else File.Delete(p);   // removes the link, not what it points to
                    continue;
                }
                if (isDir)
                {
                    var s = new System.Security.AccessControl.DirectorySecurity();
                    s.SetOwner(owner);
                    s.SetAccessRuleProtection(false, false);
                    new DirectoryInfo(p).SetAccessControl(s);
                    ResetChildren(p, owner);
                }
                else
                {
                    var s = new System.Security.AccessControl.FileSecurity();
                    s.SetOwner(owner);
                    s.SetAccessRuleProtection(false, false);
                    new FileInfo(p).SetAccessControl(s);
                }
            }
        }

        /// <summary>
        /// folder inside a user's profile (C:\Users\...): that user can rename it and put another one in its place, so the exe started
        /// for everyone and as administrator during the update would be theirs (security hardening)
        /// </summary>
        public static bool IsUnsafeInstallDir(string dir)
        {
            var users = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\'));
            var full = Norm(dir) + "\\";
            return !string.IsNullOrEmpty(users) && full.StartsWith(Norm(users) + "\\", StringComparison.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------------- protected start with administrator rights (see Installer\launcher)
        // The single exe extracts the native DLLs into the user's %TEMP% and loads them from there: elevated directly, a program of the same user
        // could prepare those DLLs (or, during uninstall, the exe copied into %TEMP%) and have them run as administrator (security hardening).
        // Instead the launcher of the installation folder is elevated: it copies the app into an administrators-only folder and starts it from there.

        internal const string ElevatedLauncherArg = "--lemora-elevated";
        internal const string ElevatedDirPrefix = "Lemora-elev-";
        private static readonly byte[] ElevatedMarker = "LEMORA-ELEVATED-V1"u8.ToArray();
        private const long MaxLauncherSize = 5 * 1024 * 1024;   // the launcher is small; the whole app (single exe without launcher) is not

        /// <summary>true if these bytes belong to a launcher that knows the protected start (older launchers do not)</summary>
        internal static bool HasElevatedMarker(ReadOnlySpan<byte> exe) => exe.IndexOf(ElevatedMarker) >= 0;

        /// <summary>launcher of the installation folder that can do the protected start; null if missing, old, or the whole app is there instead</summary>
        internal static string? ElevatedLauncher(string? root)
        {
            if (string.IsNullOrEmpty(root)) return null;
            try
            {
                var l = Path.Combine(root, ExeName);
                var fi = new FileInfo(l);
                return fi.Exists && fi.Length <= MaxLauncherSize && HasElevatedMarker(File.ReadAllBytes(l)) ? l : null;
            }
            catch { return null; }
        }

        /// <summary>administrator side: replaces an old launcher (without protected start) with the one included in this version</summary>
        internal static void RefreshLauncher(string root)
        {
            try
            {
                var l = Path.Combine(root, ExeName);
                var fi = new FileInfo(l);
                if (!fi.Exists || fi.Length > MaxLauncherSize || !Directory.Exists(Path.Combine(root, UpdateApplier.VersionsDir))) return;
                // it is renewed when it is DIFFERENT from the included one, not only when the marker is missing: a launcher with the marker but with the
                // old variable filter (without COREHOST_/LEMORA_) stayed forever (security hardening)
                if (UpdateApplier.LauncherBytes() is not { } bytes || !HasElevatedMarker(bytes)) return;
                if (System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(l)).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(bytes))) return;
                // the launcher may be running: an open exe cannot be overwritten but can be renamed, then the new one takes its name
                var old = l + ".old";
                try { if (File.Exists(old)) File.Delete(old); } catch { /* still in use: it stays, removed next time */ }
                File.WriteAllBytes(l + ".new", bytes);
                if (!File.Exists(old)) File.Move(l, old); else File.Delete(l);
                File.Move(l + ".new", l);
                Shawn.Utils.SimpleLogHelper.Warning("Launcher refreshed with the one from this version");
            }
            catch (Exception e) { Shawn.Utils.SimpleLogHelper.Warning("Launcher not refreshed: " + e.Message); }
        }

        // ---------------------------------------------------------------- uninstall (Local and Teams only)

        private const string UninstallTempDirName = "Lemora-uninstall";
        public const int UninstallStepCount = 5;

        /// <summary>
        /// The installed exe cannot delete itself: it copies itself into a temporary folder and relaunches from there with administrator rights.
        /// false if the user declines the Windows prompt.
        /// </summary>
        public static bool StartUninstallElevated(string dir, bool purge, SetupMode mode)
        {
            if (!InstallDirStillSafe(dir)) { Lemora.Utils.MessageBoxHelper.Warning(SetupText.T("dir_unsafe_now")); return false; }
            var args = $"--setup-uninstall-run --dir \"{dir.TrimEnd('\\')}\" --purge {(purge ? 1 : 0)} --mode {mode.ToString().ToLower()}";
            // safe path: the installed launcher (protected folder) copies the app into an administrators-only folder and starts it from there
            if (ElevatedLauncher(dir) is { } launcher)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(launcher, ElevatedLauncherArg + " nowait " + args) { UseShellExecute = true, Verb = "runas" });
                    return true;
                }
                catch (System.ComponentModel.Win32Exception) { return false; }
            }
            // shortcut: old launcher (without the protected start and never updated): copy into the user's %TEMP% as before; goes away with the first update (RefreshLauncher)
            Shawn.Utils.SimpleLogHelper.Warning("Uninstall: launcher without protected start, copying to %TEMP%");
            var tmpDir = Path.Combine(Path.GetTempPath(), UninstallTempDirName);
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
            Directory.CreateDirectory(tmpDir);
            string tmpExe;
            if (string.IsNullOrEmpty(typeof(SetupEngine).Assembly.Location))
            {
                // single-file exe: it is enough on its own
                tmpExe = Path.Combine(tmpDir, UninstallTempDirName + ".exe");
                File.Copy(ExePath, tmpExe, true);
            }
            else
            {
                // multi-file program (e.g. installed with the ReadyToRun build): Lemora.exe alone does not start without Lemora.dll and the libraries,
                // so the whole program folder is copied, keeping the exe name
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
                Process.Start(new ProcessStartInfo(tmpExe, args) { UseShellExecute = true, Verb = "runas" });
                return true;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        public static void Uninstall(string dir, bool purge, Action<int> report)
        {
            // 1. closing the app (only the processes of this installation)
            report(0);
            StopRunning(dir);

            // 2. disconnecting from the Teams server: the server does not exist yet, for now only the local copy is removed together with the user's data (if requested)
            report(1);

            // 3. files
            report(2);
            DeleteInstallDir(dir);
            TryDeleteDir(Path.Combine(Path.GetTempPath(), ".net", AppName));

            // 4. shortcuts and registration in Windows
            report(3);
            foreach (var f in new[] { Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Startup })
                TryDeleteFile(Path.Combine(Environment.GetFolderPath(f), AppName + ".lnk"));
            try { Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false); } catch { /* was not there */ }
            try { Registry.LocalMachine.DeleteSubKeyTree(AppKey, false); } catch { /* was not there */ }
            try { using var k = Registry.LocalMachine.OpenSubKey(RunKey, true); k?.DeleteValue(AppName, false); } catch { /* was not there */ }
            try { using var k = Registry.CurrentUser.OpenSubKey(RunKey, true); k?.DeleteValue(AppName, false); } catch { /* was not there */ }

            // 5. data of all users (only if requested)
            report(4);
            if (purge) PurgeAllUsersData();
            report(5);
        }

        /// <summary>the temporary exe running the uninstall deletes itself once the process has ended</summary>
        public static void ScheduleSelfCleanup()
        {
            try
            {
                var tmpDir = Path.GetDirectoryName(ExePath)!;
                // protected copy made by the launcher: <Windows>\SystemTemp\Lemora-elev-...\app\Lemora.exe, the whole folder is deleted (the .NET extraction too)
                if (Path.GetDirectoryName(tmpDir) is { } elev && Path.GetFileName(elev).StartsWith(ElevatedDirPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    // current folder outside the one to delete (inherited, it would be ...\app: Windows does not delete a folder in use)
                    Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{elev}\"") { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Environment.SystemDirectory });
                    return;
                }
                if (string.Equals(Path.GetFileName(tmpDir), UninstallTempDirName, StringComparison.OrdinalIgnoreCase) == false) return;
                var net = Path.Combine(Path.GetTempPath(), ".net", UninstallTempDirName);
                if (PathHasReparsePoint(tmpDir) || PathHasReparsePoint(net)) return;   // see TryDeleteDir
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{tmpDir}\" & rmdir /s /q \"{net}\"") { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Environment.SystemDirectory });
            }
            catch { /* only a temporary file is left */ }
        }

        private static void DeleteInstallDir(string dir)
        {
            var exe = Path.Combine(dir, ExeName);
            // the whole folder is deleted only if it is a "Lemora" folder entirely ours: never a badly chosen folder (e.g. Program Files)
            bool ours = IsOurFolderName(dir);
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    if (ours) { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                    else
                    {
                        if (File.Exists(exe)) File.Delete(exe);
                        var v = Path.Combine(dir, UpdateApplier.VersionsDir);
                        if (Directory.Exists(v)) Directory.Delete(v, true);
                        TryDeleteFile(Path.Combine(dir, UpdateApplier.CurrentFile));
                        TryDeleteFile(Path.Combine(dir, Lemora.Model.ProtocolRunner.FreeRdpRunner.LicenseFileName));
                    }
                    return;
                }
                catch (Exception)
                {
                    System.Threading.Thread.Sleep(600); // a file still in use: the app is closing
                }
            }
            if (ours) Directory.Delete(dir, true); // last attempt: if it fails the error reaches the window
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
            catch { /* at least the current user's ones are deleted */ }
            foreach (var r in roots) TryDeleteDir(r);
        }

        private static void TryDeleteDir(string path)
        {
            // a link (junction) anywhere along the path, placed by another user in their %TEMP%, would lead the administrator
            // process to delete someone else's folders (security hardening)
            if (PathHasReparsePoint(path)) { Shawn.Utils.SimpleLogHelper.Warning($"Uninstall: '{path}' goes through a link, not deleted"); return; }
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { /* a non-critical leftover remains */ }
        }

        private static readonly System.Security.Principal.SecurityIdentifier[] TrustedOwners =
        {
            new(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
            new(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
            new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"),   // TrustedInstaller
        };

        private static bool Trusted(System.Security.Principal.IdentityReference? id) =>
            id is System.Security.Principal.SecurityIdentifier sid && (TrustedOwners.Contains(sid) || sid.IsWellKnown(System.Security.Principal.WellKnownSidType.CreatorOwnerSid));

        /// <summary>folder owner: Administrators, SYSTEM or TrustedInstaller</summary>
        internal static bool OwnedByAdmins(string dir)
        {
            try { return Trusted(new DirectoryInfo(dir).GetAccessControl(System.Security.AccessControl.AccessControlSections.Owner).GetOwner(typeof(System.Security.Principal.SecurityIdentifier))); }
            catch { return false; }
        }

        /// <summary>
        /// no parent folder (drive root included) that a non-administrator user can rename or delete, or whose
        /// permissions/owner they can change: otherwise they would put another folder in place of the installation one and the exe started for everyone and
        /// with administrator rights would be theirs. Program Files and a folder in the drive root are fine
        /// </summary>
        internal static bool ParentsSafe(string dir)
        {
            const System.Security.AccessControl.FileSystemRights Dangerous =
                System.Security.AccessControl.FileSystemRights.Delete | System.Security.AccessControl.FileSystemRights.DeleteSubdirectoriesAndFiles
                | System.Security.AccessControl.FileSystemRights.ChangePermissions | System.Security.AccessControl.FileSystemRights.TakeOwnership;
            try
            {
                for (var d = new DirectoryInfo(Path.GetFullPath(dir)).Parent; d != null; d = d.Parent)   // the root too: with "delete children" the folder below can be renamed
                {
                    if (!d.Exists) continue;
                    var acl = d.GetAccessControl(System.Security.AccessControl.AccessControlSections.Access | System.Security.AccessControl.AccessControlSections.Owner);
                    if (!Trusted(acl.GetOwner(typeof(System.Security.Principal.SecurityIdentifier)))) return false;
                    foreach (System.Security.AccessControl.FileSystemAccessRule r in acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
                        if (r.AccessControlType == System.Security.AccessControl.AccessControlType.Allow && (r.PropagationFlags & System.Security.AccessControl.PropagationFlags.InheritOnly) == 0
                            && (r.FileSystemRights & Dangerous) != 0 && !Trusted(r.IdentityReference)) return false;
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>the installed folder is still safe for a start with administrator rights (before update and uninstall)</summary>
        internal static bool InstallDirStillSafe(string root) => !PathHasReparsePoint(root) && OwnedByAdmins(root) && ParentsSafe(root) && AclProtected(root) && FilesProtected(root);

        /// <summary>the files started with runas (Lemora.exe in the root and in versions\*) must also have no write permissions for others (security hardening)</summary>
        private static bool FilesProtected(string root)
        {
            try
            {
                var files = new List<string>();
                if (File.Exists(Path.Combine(root, ExeName))) files.Add(Path.Combine(root, ExeName));
                var versions = Path.Combine(root, "versions");
                if (Directory.Exists(versions))
                    foreach (var d in Directory.EnumerateDirectories(versions))
                    {
                        if (!AclProtected(d)) return false;
                        foreach (var f in Directory.EnumerateFiles(d, "*.exe")) files.Add(f);
                    }
                return files.All(f => AclProtected(f, isFile: true));
            }
            catch { return false; }
        }

        /// <summary>
        /// permissions of the installed folder: nobody outside administrators/SYSTEM/TrustedInstaller may write,
        /// delete or change permissions. An installation with the inherited ACL (e.g. Modify for everyone) would have an exe anyone could replace
        /// right before the start as administrator (security hardening)
        /// </summary>
        internal static bool AclProtected(string dir, bool isFile = false)
        {
            const System.Security.AccessControl.FileSystemRights Write =
                System.Security.AccessControl.FileSystemRights.WriteData | System.Security.AccessControl.FileSystemRights.AppendData
                | System.Security.AccessControl.FileSystemRights.WriteExtendedAttributes | System.Security.AccessControl.FileSystemRights.WriteAttributes
                | System.Security.AccessControl.FileSystemRights.Delete | System.Security.AccessControl.FileSystemRights.DeleteSubdirectoriesAndFiles
                | System.Security.AccessControl.FileSystemRights.ChangePermissions | System.Security.AccessControl.FileSystemRights.TakeOwnership;
            try
            {
                // inherited ones included (Program Files is fine: Users only read) and also those only for contained files (InheritOnly): they apply to the exe
                System.Security.AccessControl.FileSystemSecurity acl = isFile
                    ? new FileInfo(dir).GetAccessControl(System.Security.AccessControl.AccessControlSections.Access)
                    : new DirectoryInfo(dir).GetAccessControl(System.Security.AccessControl.AccessControlSections.Access);
                foreach (System.Security.AccessControl.FileSystemAccessRule r in acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
                    if (r.AccessControlType == System.Security.AccessControl.AccessControlType.Allow && (r.FileSystemRights & Write) != 0 && !Trusted(r.IdentityReference)) return false;
                return true;
            }
            catch { return false; }
        }

        /// <summary>true if the folder or one of its existing parent folders is a link (junction, symlink): the path leads elsewhere</summary>
        internal static bool PathHasReparsePoint(string path)
        {
            try
            {
                for (var d = new DirectoryInfo(Path.GetFullPath(path)); d != null; d = d.Parent)
                    if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            }
            catch { return true; }   // when in doubt, do not touch it
            return false;
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* a non-critical leftover remains */ }
        }

        /// <summary>closes the Lemora copies of this installation folder: the launcher and the apps in versions\</summary>
        private static void StopRunning(string dir)
        {
            try
            {
                var target = Path.Combine(dir, ExeName);
                var versions = Path.Combine(dir, UpdateApplier.VersionsDir) + Path.DirectorySeparatorChar;
                int me = Environment.ProcessId;
                foreach (var p in Process.GetProcessesByName("Lemora").Where(x => x.Id != me))
                {
                    try
                    {
                        var f = p.MainModule?.FileName ?? "";
                        if (string.Equals(f, target, StringComparison.OrdinalIgnoreCase) || f.StartsWith(versions, StringComparison.OrdinalIgnoreCase))
                        {
                            p.Kill();
                            p.WaitForExit(8000);
                        }
                    }
                    catch { /* process already closed or not accessible */ }
                }
            }
            catch { /* if it fails, the copy will give a clear error */ }
        }

        // ---------------------------------------------------------------- automatic update (see UpdateService and UpdateApplier)

        /// <summary>exit codes of the elevated copy: 0 done, 1 error, 2 folder different from the installed one, 3 invalid signature</summary>
        public const int UpdateOk = 0, UpdateFailed = 1, UpdateWrongDir = 2, UpdateBadSignature = 3;

        /// <summary>
        /// Administrator side: launched as "--setup-update --zip ... --dir ... --version ...". For security it does NOT trust what it receives:
        /// the folder must be the one registered in Windows for Lemora and the package is read again and re-verified with the signature (same bytes that are then extracted).
        /// </summary>
        public static int RunUpdateElevated(string zipPath, string dir, string version)
        {
            try
            {
                var installed = InstalledLocation();
                if (installed == null || !string.Equals(Norm(dir), Norm(installed), StringComparison.OrdinalIgnoreCase)) return UpdateWrongDir;
                var bytes = File.ReadAllBytes(zipPath);
                if (!UpdateService.VerifySignature(bytes, File.ReadAllText(zipPath + ".sig"))) return UpdateBadSignature;
                // older installations in a hand-picked folder still have the inherited permissions (Modify for everyone):
                // the update resets them, before writing the new files
                ProtectInstallDir(installed);
                UpdateApplier.ActivateFromZip(bytes, installed, version, checkVersion: true);
                RefreshLauncher(installed);
                RegisterVersion(Path.Combine(installed, UpdateApplier.VersionsDir, version, ExeName));
                return UpdateOk;
            }
            catch (Exception e)
            {
                Shawn.Utils.SimpleLogHelper.Error(e);
                return UpdateFailed;
            }
        }

        /// <summary>updates the version shown in "Installed apps" (same format as the installation)</summary>
        private static void RegisterVersion(string exe)
        {
            var version = "";
            try { version = FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Split('+')[0] ?? ""; } catch { /* informational only */ }
            try { version = $"{version} (build {File.GetLastWriteTime(exe):yyyy-MM-dd HH:mm})".Trim(); } catch { /* informational only */ }
            long sizeKb = 0;
            try { sizeKb = new FileInfo(exe).Length / 1024; } catch { /* informational only */ }
            try
            {
                using (var k = Registry.LocalMachine.CreateSubKey(UninstallKey)) { k.SetValue("DisplayVersion", version); k.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, sizeKb), RegistryValueKind.DWord); }
                using (var k = Registry.LocalMachine.CreateSubKey(AppKey)) k.SetValue("Version", version);
            }
            catch { /* the shown version is informational only: the update is done anyway */ }
        }

        /// <summary>
        /// User side, after "Restart to update": launches the elevated copy (Windows asks for permission ONCE), waits for it to finish and, if it went well,
        /// reopens the app from the launcher (which waits for this process to close). Returns null if everything is ready and the app must close, otherwise the reason.
        /// </summary>
        public static async System.Threading.Tasks.Task<string?> StartUpdateAsync(string zipPath, Version version)
        {
            var root = UpdateApplier.RootOfVersionedExe(ExePath);
            if (root == null || !IsInstalledCopy()) return IoC.Translate("update_not_installed_copy");
            if (!InstallDirStillSafe(root)) return SetupText.T("dir_unsafe_now");   // before asking for administrator rights (security hardening)
            int code;
            var args = $"--setup-update --zip \"{zipPath}\" --dir \"{root}\" --version {version}";
            // safe path: the launcher is elevated (no DLL extraction into %TEMP%), it starts the app from an administrators-only folder and returns its exit code
            var launcher = ElevatedLauncher(root);
            if (launcher == null) Shawn.Utils.SimpleLogHelper.Warning("Update: launcher without protected start, elevating the app directly (the launcher is refreshed by this update)");
            var psi = launcher != null ? new ProcessStartInfo(launcher, ElevatedLauncherArg + " wait " + args) : new ProcessStartInfo(ExePath, args);
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            try
            {
                using var p = Process.Start(psi)!;
                await p.WaitForExitAsync();
                code = p.ExitCode;
            }
            catch (System.ComponentModel.Win32Exception) { return IoC.Translate("update_cancelled_no_permission"); }
            if (code != UpdateOk) return string.Format(IoC.Translate("update_failed_code"), code);
            Process.Start(new ProcessStartInfo(Path.Combine(root, ExeName), $"--after-pid {Environment.ProcessId}") { UseShellExecute = false, WorkingDirectory = root });
            return null;
        }

        private static void CreateShortcut(string lnkPath, string target, string workDir)
        {
            var t = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell not available");
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic lnk = shell.CreateShortcut(lnkPath);
            lnk.TargetPath = target;
            lnk.WorkingDirectory = workDir;
            lnk.IconLocation = target + ",0";
            lnk.Description = AppName;
            lnk.Save();
            TaskbarIdentity.ApplyToShortcut(lnkPath);
        }
    }
}
