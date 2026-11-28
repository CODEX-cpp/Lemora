using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace Lemora.Server;

/// <summary>installs the server as an always-on service (Windows: service manager, Linux: systemd) and removes it</summary>
internal static class Svc
{
    public const string WinName = "LemoraServer";
    const string FwRule = "Lemora Server";
    const string LinuxName = "lemora-server";
    const string LinuxUnit = "/etc/systemd/system/lemora-server.service";

    public static bool IsElevated() => OperatingSystem.IsWindows()
        ? new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)
        : Environment.UserName == "root";

    /// <summary>with --test it is decided on the spot; in production it always installs</summary>
    public static bool AskInstall()
    {
        if (Console.IsInputRedirected) return false;
        Console.Write("Install as an always-on service (starts with the PC)? [y/N] ");
        return Console.ReadLine()?.Trim().ToLowerInvariant() is "s" or "si" or "sì" or "y" or "yes";
    }

    public static int Install(string[] args, string? data) => Elevated(args, () => SavePreferredPort(args, data) ?? (OperatingSystem.IsWindows() ? InstallWindows(data ?? Paths.DefaultDataDir) : InstallLinux(data)), true);

    /// <summary>the service starts without options: the port requested with --port at install time is saved in server.json, otherwise the service would use the default.
    /// On an already configured server the port is not changed (paired PCs use it)</summary>
    public static string? SavePreferredPort(string[] args, string? data)
    {
        if (!int.TryParse(Arg(args, "--port"), out var port)) return null;
        if (port < 1024 || port > 65535) return "The port must be between 1024 and 65535.";
        var dir = data ?? Paths.DefaultDataDir;
        var file = Path.Combine(dir, "server.json");
        var cfg = ServerConfig.Load(file);
        if (cfg.Configured) return cfg.Port == port ? null : $"Server already configured on port {cfg.Port}: --port {port} is not allowed, paired PCs would lose the connection.";
        Directory.CreateDirectory(dir);
        cfg.Port = port;
        cfg.Save(file);
        return null;
    }

    public static int Uninstall(string[] args, string? data) => Elevated(args, () => OperatingSystem.IsWindows() ? UninstallWindows(args, data ?? Paths.DefaultDataDir, args.Contains("--purge")) : UninstallLinux(args.Contains("--purge")), false);

    /// <summary>the uninstall asked questions in the window: wait for a key before closing it</summary>
    static bool askedQuestions;
    /// <summary>the running exe is in the program folder: the folder is deleted after the process has ended</summary>
    static bool deleteInstallDirOnExit;

    /// <summary>if permissions are missing it asks for them right away (Windows: UAC prompt, Linux: sudo) and relaunches itself</summary>
    static int Elevated(string[] args, Func<string?> action, bool show)
    {
        if (!IsElevated())
        {
            Console.WriteLine("Administrator permissions are required: confirm them in the window that appears.");
            try
            {
                var exe = Environment.ProcessPath!;
                var extra = args.Append("--elevated");
                ProcessStartInfo psi;
                if (OperatingSystem.IsWindows())
                    psi = new ProcessStartInfo(exe, string.Join(" ", extra.Select(a => "\"" + a.Replace("\"", "\\\"") + "\""))) { UseShellExecute = true, Verb = "runas" };
                else
                {
                    psi = new ProcessStartInfo("sudo");
                    psi.ArgumentList.Add(exe);
                    foreach (var a in extra) psi.ArgumentList.Add(a);
                }
                using var p = Process.Start(psi)!;
                p.WaitForExit();
                return p.ExitCode;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Administrator permissions not granted: " + ex.Message);
                return 4;
            }
        }

        // --log <file>: for automated tests, the administrator window writes here what it would show (including the setup code)
        if (Arg(args, "--log") is { } logFile)
        {
            var w = new StreamWriter(logFile) { AutoFlush = true };
            Console.SetOut(w);
            Console.SetError(w);
        }
        var started = DateTime.UtcNow.AddSeconds(-1);
        var err = action();
        if (err == null && show) err = PrintInfo(args, started);
        if (err != null) Console.Error.WriteLine(err);
        if ((args.Contains("--elevated") || askedQuestions) && !args.Contains("--no-pause")) { Console.WriteLine(); Console.Write("Press Enter to close."); Console.ReadLine(); }
        if (deleteInstallDirOnExit && OperatingSystem.IsWindows()) ScheduleInstallDirDelete();
        return err == null ? 0 : 3;
    }

    static (int code, string output) Exec(string file, params string[] a)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var x in a) psi.ArgumentList.Add(x);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o.Trim());
    }

    /// <summary>like Exec but prints "[n/total] what it is doing", a dot per second while waiting and at the end "ok" or "ERROR" with the seconds: a long command (e.g. the service start) no longer leaves the terminal silent</summary>
    static (int code, string output) ExecStep(string step, string file, params string[] a)
    {
        Console.Write(step + " ");
        var sw = Stopwatch.StartNew();
        var t = Task.Run(() => Exec(file, a));
        while (!t.Wait(1000)) Console.Write('.');
        var r = t.Result;
        Console.WriteLine(r.code == 0 ? $"ok ({sw.Elapsed.TotalSeconds:0} s)" : $"ERROR ({sw.Elapsed.TotalSeconds:0} s)");
        return r;
    }

    static string TryExec(string file, params string[] a)
    {
        try { return Exec(file, a).output; } catch { return ""; } // program not installed
    }

    static string? Fail(string what, (int code, string output) r) => r.code == 0 ? null : $"{what} failed (code {r.code}): {r.output}";

    /// <summary>the database can live outside the data folder (chosen in the wizard): with --purge it is deleted too</summary>
    static void PurgeDb(string data)
    {
        var db = ServerConfig.Load(Path.Combine(data, "server.json")).DbDir;
        if (db.Length == 0 || !Directory.Exists(db)) return;
        var inside = Path.GetFullPath(db).StartsWith(Path.GetFullPath(data).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (inside) return; // the data folder cleanup deletes it
        Directory.Delete(db, true);
        Console.WriteLine($"Database deleted: {db}");
    }

    // ---- Windows ----

    static bool WinExists() => Exec("sc.exe", "query", WinName).code == 0;

    static void WinWaitStopped()
    {
        for (int i = 0; i < 40 && !Exec("sc.exe", "query", WinName).output.Contains("STOPPED"); i++) Thread.Sleep(500);
    }

    // the service always runs from the copy in Program Files: the downloaded exe may be in Downloads, on a USB stick or on a share
    // (the service, user LocalService, often could not even read it) and may be moved or deleted after installation
    static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Lemora Server");
    static string InstalledExe => Path.Combine(InstallDir, "Lemora.Server.exe");

    static bool RunningFromInstallDir => string.Equals(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\'), InstallDir, StringComparison.OrdinalIgnoreCase);

    /// <summary>copies the program into Program Files (with the service stopped: files in use cannot be overwritten). Only the program files, never the rest of the source folder</summary>
    static string? CopyProgram()
    {
        if (RunningFromInstallDir) return null;
        try
        {
            Directory.CreateDirectory(InstallDir);
            File.Copy(Environment.ProcessPath!, InstalledExe, true);
            if (string.IsNullOrEmpty(typeof(Svc).Assembly.Location)) return null; // single-file exe: it is enough on its own
            var src = AppContext.BaseDirectory;
            foreach (var f in Directory.GetFiles(src))
            {
                var n = Path.GetFileName(f);
                if (n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || (n.StartsWith("Lemora.Server.", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
                    File.Copy(f, Path.Combine(InstallDir, n), true);
            }
            var rt = Path.Combine(src, "runtimes"); // native SQLite
            if (Directory.Exists(rt))
                foreach (var f in Directory.GetFiles(rt, "*", SearchOption.AllDirectories))
                {
                    var to = Path.Combine(InstallDir, Path.GetRelativePath(src, f));
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Copy(f, to, true);
                }
            return null;
        }
        catch (Exception ex) { return $"Copying the program to {InstallDir} failed: {ex.Message}"; }
    }

    [SupportedOSPlatform("windows")]
    static string? InstallWindows(string data)
    {
        // data folder readable only by SYSTEM, administrators and the service: it holds the certificate key and the database
        Directory.CreateDirectory(data);
        // the service is granted access further down with ITS OWN identity (NT SERVICE\LemoraServer): with "LocalService" every other LocalService service on the PC could read the data
        var acl = Exec("icacls.exe", data, "/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F");
        if (Fail("Protecting the data folder", acl) is { } e0) return e0;
        Exec("icacls.exe", data, "/remove:g", "*S-1-5-19");   // older installs: LocalService as a whole removed

        bool exists = WinExists();
        if (exists) { Exec("sc.exe", "stop", WinName); WinWaitStopped(); }
        if (CopyProgram() is { } ec) return ec;
        Console.WriteLine($"Program installed in {InstallDir}");

        var dataArg = data == Paths.DefaultDataDir ? "" : $" --data \"{data}\"";
        var bin = $"\"{InstalledExe}\" --service" + dataArg;
        if (exists)
        {
            if (Fail("Updating the service", Exec("sc.exe", "config", WinName, "binPath=", bin, "start=", "auto", "obj=", "NT AUTHORITY\\LocalService")) is { } e1) return e1;
        }
        else if (Fail("Creating the service", Exec("sc.exe", "create", WinName, "binPath=", bin, "start=", "auto", "obj=", "NT AUTHORITY\\LocalService", "DisplayName=", "Lemora Server")) is { } e2) return e2;

        // rule tied to the program, not the port: it stays valid when the port is changed from the wizard. Only private addresses and Domain/Private networks (never Public)
        Exec("netsh.exe", "advfirewall", "firewall", "delete", "rule", "name=" + FwRule);
        if (Fail("Adding the firewall rule", Exec("netsh.exe", "advfirewall", "firewall", "add", "rule", "name=" + FwRule, "dir=in", "action=allow", "protocol=TCP",
                "program=" + InstalledExe, "profile=domain,private", "remoteip=10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,169.254.0.0/16")) is { } e3) return e3;
        // the service's own identity (service SID), and only it among services, on the data folder
        if (Fail("Setting the service SID", Exec("sc.exe", "sidtype", WinName, "unrestricted")) is { } e4) return e4;
        if (Fail("Granting the service access to the data folder", Exec("icacls.exe", data, "/grant:r", $"NT SERVICE\\{WinName}:(OI)(CI)M")) is { } e5) return e5;
        Exec("sc.exe", "description", WinName, "Lemora Server: web console and sync for the team's PCs");
        // if the process stops on its own (including for a port change) Windows restarts it
        Exec("sc.exe", "failure", WinName, "reset=", "86400", "actions=", "restart/3000/restart/3000/restart/60000");
        // automatic update every night at 02:00: scheduled task as SYSTEM (the service itself cannot replace its program). Not blocking: the server works without it
        if (Updater.Schedule(InstalledExe, dataArg) is { } eu) Console.Error.WriteLine("WARNING: " + eu);
        else Console.WriteLine($"Automatic update: every night at {Updater.Hour} (scheduled task \"{Updater.WinTask}\").");
        // entry in Settings > Apps: written as soon as the service exists, so it can be removed even if the start fails
        try { WriteUninstallKey(InstalledExe, dataArg); }
        catch (Exception ex) { Console.Error.WriteLine("Entry in Settings > Apps not written: " + ex.Message); }
        return Fail("Starting the service", Exec("sc.exe", "start", WinName));
    }

    static bool Ask(string question)
    {
        Console.Write(question);
        return Console.ReadLine()?.Trim().ToLowerInvariant() is "s" or "si" or "sì" or "y" or "yes";
    }

    [SupportedOSPlatform("windows")]
    static string? UninstallWindows(string[] args, string data, bool purge)
    {
        // launched from Settings > Apps (or by hand) with a person in front of it: explain and ask. With --purge, --log or without a console, no questions
        if (!purge && !Console.IsInputRedirected && Arg(args, "--log") == null)
        {
            askedQuestions = true;
            Console.WriteLine("Uninstalling Lemora Server. The following will be removed:");
            Console.WriteLine($"  - the {WinName} service (stopped and removed)");
            Console.WriteLine($"  - the firewall rule \"{FwRule}\"");
            Console.WriteLine($"  - the program in {InstallDir}");
            Console.WriteLine("  - the entry in Settings > Apps");
            Console.WriteLine($"The server data is in {data}.");
            Console.WriteLine();
            purge = Ask("Also delete the server data (database, certificate, settings)? [y/N] ");
            Console.WriteLine(purge ? "  The data will be DELETED: paired PCs will no longer be able to connect." : "  The data is kept: if reinstalled, the server starts again with the same settings.");
            if (!Ask("Proceed with the uninstall? [y/N] ")) return "Uninstall cancelled: nothing was changed.";
            Console.WriteLine();
        }

        if (WinExists())
        {
            Exec("sc.exe", "stop", WinName);
            WinWaitStopped();
            if (Fail("Removing the service", Exec("sc.exe", "delete", WinName)) is { } e) return e;
            Console.WriteLine("Service removed.");
        }
        else Console.WriteLine("The service was not installed.");
        if (Exec("netsh.exe", "advfirewall", "firewall", "delete", "rule", "name=" + FwRule).code == 0) Console.WriteLine("Firewall rule removed.");
        if (Updater.TaskExists()) { Updater.Unschedule(); Console.WriteLine("Automatic update scheduled task removed."); }
        try { RemoveUninstallKey(); Console.WriteLine("Entry in Settings > Apps removed."); }
        catch (Exception ex) { Console.Error.WriteLine("Entry in Settings > Apps not removed: " + ex.Message); }

        if (RunningFromInstallDir) { deleteInstallDirOnExit = true; Console.WriteLine($"Program: the folder {InstallDir} will be deleted as soon as this window closes."); }
        else if (Directory.Exists(InstallDir))
        {
            try { Directory.Delete(InstallDir, true); Console.WriteLine($"Program deleted from {InstallDir}"); }
            catch (Exception ex) { Console.Error.WriteLine($"Folder {InstallDir} not deleted: {ex.Message}"); }
        }

        if (purge) { PurgeDb(data); }
        if (purge && Directory.Exists(data)) { Directory.Delete(data, true); Console.WriteLine("Settings and certificate deleted."); }
        else Console.WriteLine($"Settings and certificate kept in {data} (use --purge to delete them).");
        Console.WriteLine();
        Console.WriteLine("Uninstall complete.");
        return null;
    }

    /// <summary>
    /// the running exe cannot delete itself: a detached cmd waits a few seconds for this process to end
    /// and for the unelevated one that launched it (waiting for its exit code), then deletes the folder. Like the client's ScheduleSelfCleanup
    /// </summary>
    static void ScheduleInstallDirDelete()
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 6 127.0.0.1 >nul & rmdir /s /q \"{InstallDir}\"")
                { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        }
        catch { /* only the program folder is left behind */ }
    }

    // ---- entry in Settings > Apps (Programs and Features) ----

    /// <summary>
    /// normally HKLM\...\Uninstall\LemoraServer. Only for tests without administrator permissions:
    /// LEMORA_SERVER_UNINSTALL_ROOT=HKCU\Software\LemoraServerTest\Uninstall moves the key under the user (nothing but HKCU is accepted)
    /// </summary>
    [SupportedOSPlatform("windows")]
    static (RegistryKey hive, string path) UninstallKeyPath(string name = WinName)
    {
        var test = Environment.GetEnvironmentVariable("LEMORA_SERVER_UNINSTALL_ROOT");
        if (string.IsNullOrEmpty(test)) return (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + name);
        if (!test.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(@"LEMORA_SERVER_UNINSTALL_ROOT must start with HKCU\");
        return (Registry.CurrentUser, test[5..].TrimEnd('\\') + "\\" + name);
    }

    [SupportedOSPlatform("windows")]
    static void WriteUninstallKey(string exe, string dataArg)
    {
        var version = "";
        try { version = FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Split('+')[0] ?? ""; } catch { /* informational only */ }
        // like the client: the project version does not change between builds, the exe date tells updates apart
        try { version = $"{version} (build {File.GetLastWriteTime(exe):yyyy-MM-dd HH:mm})".Trim(); } catch { /* informational only */ }
        var dir = Path.GetDirectoryName(exe)!;
        long sizeKb = 0;
        try { sizeKb = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1024; } catch { /* informational only */ }
        var (hive, path) = UninstallKeyPath();
        using var k = hive.CreateSubKey(path);
        k.SetValue("DisplayName", "Lemora Server");
        k.SetValue("DisplayVersion", version);
        k.SetValue("Publisher", "Lemora");
        k.SetValue("URLInfoAbout", "https://github.com/CODEX-cpp");
        k.SetValue("HelpLink", "https://github.com/CODEX-cpp");
        k.SetValue("InstallLocation", dir);
        k.SetValue("DisplayIcon", exe);
        k.SetValue("UninstallString", $"\"{exe}\" --uninstall{dataArg}");
        k.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, sizeKb), RegistryValueKind.DWord);
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    [SupportedOSPlatform("windows")]
    static void RemoveUninstallKey()
    {
        var (hive, path) = UninstallKeyPath();
        hive.DeleteSubKeyTree(path, false);
    }

    /// <summary>
    /// tests only (--register-uninstall-key / --remove-uninstall-key): works only with LEMORA_SERVER_UNINSTALL_ROOT set,
    /// so it never writes to HKLM. Registers the running exe as if it were the installed one
    /// </summary>
    public static int UninstallKeyTest(string[] args, string? data)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LEMORA_SERVER_UNINSTALL_ROOT")))
        {
            Console.Error.WriteLine("Test-only command: requires LEMORA_SERVER_UNINSTALL_ROOT (HKCU\\...) on Windows.");
            return 2;
        }
        try
        {
            if (args.Contains("--remove-uninstall-key")) RemoveUninstallKey();
            else WriteUninstallKey(Environment.ProcessPath!, data == null || data == Paths.DefaultDataDir ? "" : $" --data \"{data}\"");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 3; }
    }

    // ---- Linux ----

    static string? InstallLinux(string? data)
    {
        if (data != null && data != Paths.DefaultDataDir) return "On Linux the service always uses " + Paths.DefaultDataDir + " (no --data).";
        if (!Directory.Exists("/run/systemd/system")) return "systemd is not running on this system: start the server manually with --foreground.";
        // the service runs as an unprivileged temporary user: it cannot execute a file inside /home/<user> (private folder, "Permission denied" 203/EXEC)
        // and the downloaded copy may be moved or deleted. As on Windows (Program Files) it always runs from a fixed copy in /opt
        const string installDir = "/opt/lemora-server";
        var exe = installDir + "/Lemora.Server";
        Console.WriteLine("Installing Lemora Server as a service (4 steps).");
        Console.WriteLine($"[1/4] Copying the program to {installDir}...");
        try
        {
            Exec("systemctl", "stop", LinuxName);   // a file in use cannot be overwritten ("Text file busy")
            Directory.CreateDirectory(installDir);
            if (!string.Equals(Path.GetFullPath(Environment.ProcessPath!), exe, StringComparison.Ordinal)) File.Copy(Environment.ProcessPath!, exe, true);
            Exec("chmod", "755", installDir, exe);
        }
        catch (Exception ex) { return $"Copying the program to {installDir} failed: {ex.Message}"; }
        // unprivileged temporary user; systemd creates and protects the data folder (only it and root).
        // The firewall step (ExecStartPost=+) runs as root WITHOUT the service's private /tmp: with a shared /tmp any user could prepare
        // the files .NET extracts and root would have loaded them. For it, a root-owned extraction folder, never writable by others
        try { Directory.CreateDirectory($"/var/lib/{LinuxName}-root"); Exec("chmod", "700", $"/var/lib/{LinuxName}-root"); } catch { /* .NET creates it at first start */ }
        File.WriteAllText(LinuxUnit, $"""
            [Unit]
            Description=Lemora Server
            After=network-online.target
            Wants=network-online.target

            [Service]
            ExecStart={exe} --service
            Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp
            Environment=HOME=/tmp
            Restart=always
            RestartSec=3
            ExecStartPost=+/usr/bin/env DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/lib/{LinuxName}-root HOME=/root {exe} --firewall-sync
            DynamicUser=yes
            StateDirectory={LinuxName}
            StateDirectoryMode=0700

            [Install]
            WantedBy=multi-user.target
            """.Replace("\r", "") + "\n");
        Console.WriteLine($"[2/4] Program copied to {installDir}, service written to {LinuxUnit}");
        if (Fail("systemctl daemon-reload", ExecStep("[3/4] Registering the service...", "systemctl", "daemon-reload")) is { } e1) return e1;
        if (Fail("systemctl enable", Exec("systemctl", "enable", LinuxName)) is { } e2) return e2;
        // automatic update every night at 02:00: systemd timer + oneshot service as root (the service itself is unprivileged). Not blocking
        if (Updater.Schedule(exe, "") is { } eu) Console.Error.WriteLine("WARNING: " + eu);
        else Console.WriteLine($"      Automatic update: every night at {Updater.Hour} (timer {Updater.LinuxUnit}.timer).");
        // the start may take several seconds (the server generates the certificate at first start): show that it is working
        return Fail("systemctl restart", ExecStep("[4/4] Starting the service (the first start can take up to half a minute)...", "systemctl", "restart", LinuxName));
    }

    static string? UninstallLinux(bool purge)
    {
        Exec("systemctl", "disable", "--now", LinuxName);
        Updater.Unschedule();
        LinuxFirewallRemove(UfwApp, UfwFile, FwdService, FwdFile);
        if (File.Exists(LinuxUnit)) { File.Delete(LinuxUnit); Console.WriteLine("Service removed."); }
        else Console.WriteLine("The service was not installed.");
        Exec("systemctl", "daemon-reload");
        try { if (Directory.Exists("/opt/lemora-server")) Directory.Delete("/opt/lemora-server", true); } catch { }
        if (purge)
        {
            PurgeDb(Paths.DefaultDataDir);
            Exec("rm", "-rf", "/var/lib/private/" + LinuxName, "/var/lib/" + LinuxName);
            Console.WriteLine("Settings and certificate deleted.");
        }
        else Console.WriteLine($"Settings and certificate kept in {Paths.DefaultDataDir} (use --purge to delete them).");
        return null;
    }

    // ---- firewall Linux (ufw, firewalld) ----

    static readonly string[] PrivateNets = ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16"];
    const string UfwApp = "Lemora Server";
    const string UfwFile = "/etc/ufw/applications.d/lemora-server";
    const string FwdService = "lemora-server";
    const string FwdFile = "/etc/firewalld/services/lemora-server.xml";

    /// <summary>
    /// run by systemd as root at every service start (ExecStartPost=+): the service runs unprivileged and cannot touch the firewall.
    /// Reads the port actually in use and aligns the rules. The rules do not name the port but a "profile" (ufw) or a "service" (firewalld): on a port change only that is rewritten.
    /// Never fails: an error here must not prevent the server from starting.
    /// </summary>
    public static int FirewallSync(string? dataArg)
    {
        try
        {
            var file = Path.Combine(dataArg ?? Paths.DefaultDataDir, "runtime.json");
            int port = 0;
            for (int i = 0; i < 80 && port == 0; i++)
            {
                try
                {
                    var root = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
                    // systemd passes the process just started: a file from another process is from the previous start
                    if (Environment.GetEnvironmentVariable("MAINPID") is { } mp && root.GetProperty("pid").GetInt32().ToString() != mp) throw new IOException("stale file");
                    port = root.GetProperty("port").GetInt32();
                }
                catch { Thread.Sleep(500); } // the server has not written the file yet, or is writing it
            }
            if (port == 0) { Console.Error.WriteLine("firewall: the server did not report its port, rules not updated."); return 0; }

            if (TryExec("ufw", "status").StartsWith("Status: active"))
            {
                // ufw copies the profile's port into the rule when creating it ("app update" does not realign it): the old rule is removed and added again
                foreach (var n in PrivateNets) TryExec("ufw", "--force", "delete", "allow", "from", n, "to", "any", "app", UfwApp);
                File.WriteAllText(UfwFile, $"[{UfwApp}]\ntitle=Lemora Server\ndescription=Lemora web console and sync\nports={port}/tcp\n");
                foreach (var n in PrivateNets) TryExec("ufw", "allow", "from", n, "to", "any", "app", UfwApp);
                Console.WriteLine($"firewall: ufw opened port {port}/tcp for private networks.");
            }
            if (TryExec("firewall-cmd", "--state").Trim() == "running")
            {
                Directory.CreateDirectory("/etc/firewalld/services");
                File.WriteAllText(FwdFile, $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<service>\n  <short>Lemora Server</short>\n  <description>Lemora web console and sync</description>\n  <port protocol=\"tcp\" port=\"{port}\"/>\n</service>\n");
                TryExec("firewall-cmd", "--reload"); // reads the new or updated service definition
                foreach (var n in PrivateNets) TryExec("firewall-cmd", "--permanent", "--add-rich-rule", FwdRule(n));
                TryExec("firewall-cmd", "--reload");
                Console.WriteLine($"firewall: firewalld opened port {port}/tcp for private networks.");
            }
        }
        catch (Exception ex) { Console.Error.WriteLine("firewall: " + ex.Message); }
        return 0;
    }

    static string FwdRule(string net, string service = FwdService) => $"rule family=\"ipv4\" source address=\"{net}\" service name=\"{service}\" accept";

    static void LinuxFirewallRemove(string ufwApp, string ufwFile, string fwdService, string fwdFile)
    {
        bool any = false;
        if (File.Exists(ufwFile))
        {
            foreach (var n in PrivateNets) TryExec("ufw", "--force", "delete", "allow", "from", n, "to", "any", "app", ufwApp);
            File.Delete(ufwFile);
            any = true;
        }
        if (File.Exists(fwdFile))
        {
            foreach (var n in PrivateNets) TryExec("firewall-cmd", "--permanent", "--remove-rich-rule", FwdRule(n, fwdService));
            File.Delete(fwdFile);
            TryExec("firewall-cmd", "--reload");
            any = true;
        }
        if (any) Console.WriteLine("Firewall rules removed.");
    }

    // ---- result for whoever installed it ----

    static string? PrintInfo(string[] args, DateTime since)
    {
        var data = Arg(args, "--data") ?? Paths.DefaultDataDir;
        var file = Path.Combine(data, "runtime.json");
        Console.Write("Service started, waiting for the server to respond");
        for (int i = 0; i < 60 && !(File.Exists(file) && File.GetLastWriteTimeUtc(file) >= since); i++) { Console.Write('.'); Thread.Sleep(500); }
        Console.WriteLine();
        if (!File.Exists(file) || File.GetLastWriteTimeUtc(file) < since)
        {
            // the server does not change port by itself: if the chosen one is taken by another program, say so clearly
            int port = ServerConfig.Load(Path.Combine(data, "server.json")).Port;
            try { var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, port); l.Start(); l.Stop(); }
            catch (System.Net.Sockets.SocketException) { return $"Port {port} is in use by another program: close it, the service retries on the same port by itself."; }
            return "The server did not respond within 30 seconds: check the service status.";
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        Console.WriteLine("Lemora Server is running as a service and starts with the PC. Open the console at:");
        foreach (var u in doc.RootElement.GetProperty("urls").EnumerateArray()) Console.WriteLine("  " + u.GetString());
        Console.WriteLine("Certificate fingerprint (SHA-256): " + doc.RootElement.GetProperty("fingerprint").GetString());
        foreach (var w in FirewallWarnings()) { Console.WriteLine(); Console.WriteLine("  WARNING: " + w); }
        var codeFile = Path.Combine(data, "setup-code.txt");
        if (File.Exists(codeFile))
        {
            Console.WriteLine();
            Console.WriteLine("  SETUP CODE: " + File.ReadAllText(codeFile).Trim());
            Console.WriteLine("  Open the console in the browser and enter this code to create the administrator.");
        }
        return null;
    }

    /// <summary>things the program cannot fix by itself that prevent network PCs from reaching the server</summary>
    static List<string> FirewallWarnings()
    {
        var list = new List<string>();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var cat = Exec("powershell.exe", "-NoProfile", "-Command", "(Get-NetConnectionProfile | ForEach-Object { $_.NetworkCategory }) -join ','").output;
                if (cat.Contains("Public"))
                    list.Add("a network on this PC is set to Public: the firewall blocks incoming connections. Set it to Private (Settings > Network) if it is the office network.");
                foreach (var p in new[] { "Domain", "Private" })
                    if (Exec("reg.exe", "query", $@"HKLM\SOFTWARE\Policies\Microsoft\WindowsFirewall\{p}Profile", "/v", "AllowLocalPolicyMerge").output.Contains("0x0"))
                        list.Add($"a Group Policy ignores local firewall rules ({p} profile): ask the domain administrator to allow the program Lemora.Server.exe.");
            }
        }
        catch { /* tool not present: nothing to report */ }
        return list;
    }

    static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
