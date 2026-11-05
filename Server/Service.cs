using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace Lemora.Server;

/// <summary>installa il server come servizio sempre attivo (Windows: gestore dei servizi, Linux: systemd) e lo rimuove</summary>
internal static class Svc
{
    public const string WinName = "LemoraServer";
    const string FwRule = "Lemora Server";
    const string LinuxName = "lemora-server";
    const string LinuxUnit = "/etc/systemd/system/lemora-server.service";

    public static bool IsElevated() => OperatingSystem.IsWindows()
        ? new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)
        : Environment.UserName == "root";

    /// <summary>con --test si decide al momento; in produzione si installa sempre</summary>
    public static bool AskInstall()
    {
        if (Console.IsInputRedirected) return false;
        Console.Write("Installare come servizio sempre attivo (parte con il PC)? [s/N] ");
        return Console.ReadLine()?.Trim().ToLowerInvariant() is "s" or "si" or "sì" or "y" or "yes";
    }

    public static int Install(string[] args, string? data) => Elevated(args, () => SavePreferredPort(args, data) ?? (OperatingSystem.IsWindows() ? InstallWindows(data ?? Paths.DefaultDataDir) : InstallLinux(data)), true);

    /// <summary>il servizio parte senza opzioni: la porta chiesta con --port all'installazione si salva in server.json, altrimenti il servizio userebbe la predefinita.
    /// Su un server gia' configurato la porta non si cambia (i PC abbinati la usano)</summary>
    public static string? SavePreferredPort(string[] args, string? data)
    {
        if (!int.TryParse(Arg(args, "--port"), out var port)) return null;
        if (port < 1024 || port > 65535) return "La porta deve essere tra 1024 e 65535.";
        var dir = data ?? Paths.DefaultDataDir;
        var file = Path.Combine(dir, "server.json");
        var cfg = ServerConfig.Load(file);
        if (cfg.Configured) return cfg.Port == port ? null : $"Server gia' configurato sulla porta {cfg.Port}: --port {port} non e' ammesso, i PC abbinati si perderebbero.";
        Directory.CreateDirectory(dir);
        cfg.Port = port;
        cfg.Save(file);
        return null;
    }

    public static int Uninstall(string[] args, string? data) => Elevated(args, () => OperatingSystem.IsWindows() ? UninstallWindows(args, data ?? Paths.DefaultDataDir, args.Contains("--purge")) : UninstallLinux(args.Contains("--purge")), false);

    /// <summary>la disinstallazione ha fatto domande nella finestra: si aspetta un tasto prima di chiuderla</summary>
    static bool askedQuestions;
    /// <summary>l'exe in uso sta nella cartella del programma: la si cancella dopo che il processo e' finito</summary>
    static bool deleteInstallDirOnExit;

    /// <summary>se mancano i permessi li richiede subito (Windows: conferma UAC, Linux: sudo) e rilancia se stesso</summary>
    static int Elevated(string[] args, Func<string?> action, bool show)
    {
        if (!IsElevated())
        {
            Console.WriteLine("Servono i permessi di amministratore: confermali nella finestra che compare.");
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
                Console.Error.WriteLine("Permessi di amministratore non concessi: " + ex.Message);
                return 4;
            }
        }

        // --log <file>: per le prove automatiche, la finestra amministratore scrive qui quello che mostrerebbe (compreso il codice di avvio)
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
        if ((args.Contains("--elevated") || askedQuestions) && !args.Contains("--no-pause")) { Console.WriteLine(); Console.Write("Premi Invio per chiudere."); Console.ReadLine(); }
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

    static string TryExec(string file, params string[] a)
    {
        try { return Exec(file, a).output; } catch { return ""; } // programma non installato
    }

    static string? Fail(string what, (int code, string output) r) => r.code == 0 ? null : $"{what} non riuscito (codice {r.code}): {r.output}";

    /// <summary>il database puo' stare fuori dalla cartella dei dati (scelto nel wizard): con --purge si cancella anche quello</summary>
    static void PurgeDb(string data)
    {
        var db = ServerConfig.Load(Path.Combine(data, "server.json")).DbDir;
        if (db.Length == 0 || !Directory.Exists(db)) return;
        var inside = Path.GetFullPath(db).StartsWith(Path.GetFullPath(data).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (inside) return; // lo cancella la pulizia della cartella dei dati
        Directory.Delete(db, true);
        Console.WriteLine($"Database cancellato: {db}");
    }

    // ---- Windows ----

    static bool WinExists() => Exec("sc.exe", "query", WinName).code == 0;

    static void WinWaitStopped()
    {
        for (int i = 0; i < 40 && !Exec("sc.exe", "query", WinName).output.Contains("STOPPED"); i++) Thread.Sleep(500);
    }

    // il servizio gira sempre dalla copia in Programmi: l'exe scaricato puo' stare in Download, su una chiavetta o su una share
    // (il servizio, utente LocalService, spesso non potrebbe nemmeno leggerlo) e puo' essere spostato o cancellato dopo l'installazione
    static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Lemora Server");
    static string InstalledExe => Path.Combine(InstallDir, "Lemora.Server.exe");

    static bool RunningFromInstallDir => string.Equals(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\'), InstallDir, StringComparison.OrdinalIgnoreCase);

    /// <summary>copia il programma in Programmi (a servizio fermo: i file in uso non si sovrascrivono). Solo i file del programma, mai il resto della cartella di partenza</summary>
    static string? CopyProgram()
    {
        if (RunningFromInstallDir) return null;
        try
        {
            Directory.CreateDirectory(InstallDir);
            File.Copy(Environment.ProcessPath!, InstalledExe, true);
            if (string.IsNullOrEmpty(typeof(Svc).Assembly.Location)) return null; // exe in un solo file: basta lui
            var src = AppContext.BaseDirectory;
            foreach (var f in Directory.GetFiles(src))
            {
                var n = Path.GetFileName(f);
                if (n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || (n.StartsWith("Lemora.Server.", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
                    File.Copy(f, Path.Combine(InstallDir, n), true);
            }
            var rt = Path.Combine(src, "runtimes"); // SQLite nativo
            if (Directory.Exists(rt))
                foreach (var f in Directory.GetFiles(rt, "*", SearchOption.AllDirectories))
                {
                    var to = Path.Combine(InstallDir, Path.GetRelativePath(src, f));
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Copy(f, to, true);
                }
            return null;
        }
        catch (Exception ex) { return $"Copia del programma in {InstallDir} non riuscita: {ex.Message}"; }
    }

    [SupportedOSPlatform("windows")]
    static string? InstallWindows(string data)
    {
        // cartella dei dati leggibile solo da SYSTEM, amministratori e dal servizio: dentro ci sono la chiave del certificato e il database
        Directory.CreateDirectory(data);
        var acl = Exec("icacls.exe", data, "/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F", "*S-1-5-19:(OI)(CI)M");
        if (Fail("Protezione della cartella dei dati", acl) is { } e0) return e0;

        bool exists = WinExists();
        if (exists) { Exec("sc.exe", "stop", WinName); WinWaitStopped(); }
        if (CopyProgram() is { } ec) return ec;
        Console.WriteLine($"Programma installato in {InstallDir}");

        var dataArg = data == Paths.DefaultDataDir ? "" : $" --data \"{data}\"";
        var bin = $"\"{InstalledExe}\" --service" + dataArg;
        if (exists)
        {
            if (Fail("Aggiornamento del servizio", Exec("sc.exe", "config", WinName, "binPath=", bin, "start=", "auto", "obj=", "NT AUTHORITY\\LocalService")) is { } e1) return e1;
        }
        else if (Fail("Creazione del servizio", Exec("sc.exe", "create", WinName, "binPath=", bin, "start=", "auto", "obj=", "NT AUTHORITY\\LocalService", "DisplayName=", "Lemora Server")) is { } e2) return e2;

        // regola legata al programma e non alla porta: resta valida quando dal wizard si cambia porta. Solo indirizzi privati e reti Dominio/Privata (mai Pubblica)
        Exec("netsh.exe", "advfirewall", "firewall", "delete", "rule", "name=" + FwRule);
        if (Fail("Regola del firewall", Exec("netsh.exe", "advfirewall", "firewall", "add", "rule", "name=" + FwRule, "dir=in", "action=allow", "protocol=TCP",
                "program=" + InstalledExe, "profile=domain,private", "remoteip=10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,169.254.0.0/16")) is { } e3) return e3;
        Exec("sc.exe", "description", WinName, "Lemora Server: console web e sincronizzazione per i PC del team");
        // se il processo si ferma da solo (anche per il cambio di porta) Windows lo riavvia
        Exec("sc.exe", "failure", WinName, "reset=", "86400", "actions=", "restart/3000/restart/3000/restart/60000");
        // voce in Impostazioni > App: scritta appena il servizio esiste, cosi' si puo' togliere anche se l'avvio non riesce
        try { WriteUninstallKey(InstalledExe, dataArg); }
        catch (Exception ex) { Console.Error.WriteLine("Voce in Impostazioni > App non scritta: " + ex.Message); }
        return Fail("Avvio del servizio", Exec("sc.exe", "start", WinName));
    }

    static bool Ask(string question)
    {
        Console.Write(question);
        return Console.ReadLine()?.Trim().ToLowerInvariant() is "s" or "si" or "sì" or "y" or "yes";
    }

    [SupportedOSPlatform("windows")]
    static string? UninstallWindows(string[] args, string data, bool purge)
    {
        // lanciata da Impostazioni > App (o a mano) con una persona davanti: si spiega e si chiede. Con --purge, --log o senza console niente domande
        if (!purge && !Console.IsInputRedirected && Arg(args, "--log") == null)
        {
            askedQuestions = true;
            Console.WriteLine("Disinstallazione di Lemora Server. Verranno tolti:");
            Console.WriteLine($"  - il servizio {WinName} (fermato e rimosso)");
            Console.WriteLine($"  - la regola del firewall \"{FwRule}\"");
            Console.WriteLine($"  - il programma in {InstallDir}");
            Console.WriteLine("  - la voce in Impostazioni > App");
            Console.WriteLine($"I dati del server stanno in {data}.");
            Console.WriteLine();
            purge = Ask("Cancellare anche i dati del server (database, certificato, impostazioni)? [s/N] ");
            Console.WriteLine(purge ? "  I dati verranno CANCELLATI: i PC abbinati non potranno piu' collegarsi." : "  I dati restano: reinstallando il server riparte con le stesse impostazioni.");
            if (!Ask("Procedere con la disinstallazione? [s/N] ")) return "Disinstallazione annullata: nulla e' stato modificato.";
            Console.WriteLine();
        }

        if (WinExists())
        {
            Exec("sc.exe", "stop", WinName);
            WinWaitStopped();
            if (Fail("Rimozione del servizio", Exec("sc.exe", "delete", WinName)) is { } e) return e;
            Console.WriteLine("Servizio rimosso.");
        }
        else Console.WriteLine("Il servizio non era installato.");
        if (Exec("netsh.exe", "advfirewall", "firewall", "delete", "rule", "name=" + FwRule).code == 0) Console.WriteLine("Regola del firewall rimossa.");
        try { RemoveUninstallKey(); Console.WriteLine("Voce in Impostazioni > App rimossa."); }
        catch (Exception ex) { Console.Error.WriteLine("Voce in Impostazioni > App non rimossa: " + ex.Message); }

        if (RunningFromInstallDir) { deleteInstallDirOnExit = true; Console.WriteLine($"Programma: la cartella {InstallDir} si cancella appena questa finestra si chiude."); }
        else if (Directory.Exists(InstallDir))
        {
            try { Directory.Delete(InstallDir, true); Console.WriteLine($"Programma cancellato da {InstallDir}"); }
            catch (Exception ex) { Console.Error.WriteLine($"Cartella {InstallDir} non cancellata: {ex.Message}"); }
        }

        if (purge) { PurgeDb(data); }
        if (purge && Directory.Exists(data)) { Directory.Delete(data, true); Console.WriteLine("Impostazioni e certificato cancellati."); }
        else Console.WriteLine($"Impostazioni e certificato restano in {data} (--purge per cancellarli).");
        Console.WriteLine();
        Console.WriteLine("Disinstallazione completata.");
        return null;
    }

    /// <summary>
    /// l'exe in uso non puo' cancellare se stesso: un cmd staccato aspetta qualche secondo che finiscano questo processo
    /// e quello che lo ha lanciato senza permessi (in attesa del suo codice di uscita), poi cancella la cartella. Come ScheduleSelfCleanup del client
    /// </summary>
    static void ScheduleInstallDirDelete()
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 6 127.0.0.1 >nul & rmdir /s /q \"{InstallDir}\"")
                { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        }
        catch { /* resta solo la cartella del programma */ }
    }

    // ---- voce in Impostazioni > App (Programmi e funzionalita') ----

    /// <summary>
    /// di norma HKLM\...\Uninstall\LemoraServer. Solo per le prove senza permessi di amministratore:
    /// LEMORA_SERVER_UNINSTALL_ROOT=HKCU\Software\LemoraServerTest\Uninstall sposta la chiave sotto l'utente (non si accetta altro che HKCU)
    /// </summary>
    [SupportedOSPlatform("windows")]
    static (RegistryKey hive, string path) UninstallKeyPath(string name = WinName)
    {
        var test = Environment.GetEnvironmentVariable("LEMORA_SERVER_UNINSTALL_ROOT");
        if (string.IsNullOrEmpty(test)) return (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + name);
        if (!test.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(@"LEMORA_SERVER_UNINSTALL_ROOT deve iniziare con HKCU\");
        return (Registry.CurrentUser, test[5..].TrimEnd('\\') + "\\" + name);
    }

    [SupportedOSPlatform("windows")]
    static void WriteUninstallKey(string exe, string dataArg)
    {
        var version = "";
        try { version = FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Split('+')[0] ?? ""; } catch { /* solo informativo */ }
        // come il client: la versione del progetto non cambia tra una build e l'altra, la data dell'exe distingue gli aggiornamenti
        try { version = $"{version} (build {File.GetLastWriteTime(exe):yyyy-MM-dd HH:mm})".Trim(); } catch { /* solo informativo */ }
        var dir = Path.GetDirectoryName(exe)!;
        long sizeKb = 0;
        try { sizeKb = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1024; } catch { /* solo informativo */ }
        var (hive, path) = UninstallKeyPath();
        using var k = hive.CreateSubKey(path);
        k.SetValue("DisplayName", "Lemora Server");
        k.SetValue("DisplayVersion", version);
        k.SetValue("Publisher", "Lemora");
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
    /// solo per le prove (--register-uninstall-key / --remove-uninstall-key): funziona soltanto con LEMORA_SERVER_UNINSTALL_ROOT impostata,
    /// cosi' non scrive mai in HKLM. Registra l'exe in uso come se fosse quello installato
    /// </summary>
    public static int UninstallKeyTest(string[] args, string? data)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LEMORA_SERVER_UNINSTALL_ROOT")))
        {
            Console.Error.WriteLine("Comando solo per le prove: serve LEMORA_SERVER_UNINSTALL_ROOT (HKCU\\...) su Windows.");
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
        if (data != null && data != Paths.DefaultDataDir) return "Su Linux il servizio usa sempre " + Paths.DefaultDataDir + " (niente --data).";
        if (!Directory.Exists("/run/systemd/system")) return "systemd non e' attivo su questo sistema: avvia il server a mano con --foreground.";
        // utente temporaneo senza privilegi; systemd crea e protegge la cartella dei dati (solo lui e root)
        File.WriteAllText(LinuxUnit, $"""
            [Unit]
            Description=Lemora Server
            After=network-online.target
            Wants=network-online.target

            [Service]
            ExecStart={Environment.ProcessPath} --service
            Restart=always
            RestartSec=3
            ExecStartPost=+{Environment.ProcessPath} --firewall-sync
            DynamicUser=yes
            StateDirectory={LinuxName}
            StateDirectoryMode=0700

            [Install]
            WantedBy=multi-user.target
            """.Replace("\r", "") + "\n");
        if (Fail("systemctl daemon-reload", Exec("systemctl", "daemon-reload")) is { } e1) return e1;
        if (Fail("systemctl enable", Exec("systemctl", "enable", LinuxName)) is { } e2) return e2;
        return Fail("systemctl restart", Exec("systemctl", "restart", LinuxName));
    }

    static string? UninstallLinux(bool purge)
    {
        Exec("systemctl", "disable", "--now", LinuxName);
        LinuxFirewallRemove(UfwApp, UfwFile, FwdService, FwdFile);
        if (File.Exists(LinuxUnit)) { File.Delete(LinuxUnit); Console.WriteLine("Servizio rimosso."); }
        else Console.WriteLine("Il servizio non era installato.");
        Exec("systemctl", "daemon-reload");
        if (purge)
        {
            PurgeDb(Paths.DefaultDataDir);
            Exec("rm", "-rf", "/var/lib/private/" + LinuxName, "/var/lib/" + LinuxName);
            Console.WriteLine("Impostazioni e certificato cancellati.");
        }
        else Console.WriteLine($"Impostazioni e certificato restano in {Paths.DefaultDataDir} (--purge per cancellarli).");
        return null;
    }

    // ---- firewall Linux (ufw, firewalld) ----

    static readonly string[] PrivateNets = ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16"];
    const string UfwApp = "Lemora Server";
    const string UfwFile = "/etc/ufw/applications.d/lemora-server";
    const string FwdService = "lemora-server";
    const string FwdFile = "/etc/firewalld/services/lemora-server.xml";

    /// <summary>
    /// lanciato da systemd come root a ogni avvio del servizio (ExecStartPost=+): il servizio gira senza privilegi e non puo' toccare il firewall.
    /// Legge la porta davvero usata e la allinea. Le regole non citano la porta ma un "profilo" (ufw) o un "servizio" (firewalld): al cambio porta si riscrive solo quello.
    /// Non fallisce mai: un errore qui non deve impedire l'avvio del server.
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
                    // systemd passa il processo appena avviato: un file di un altro processo e' quello dell'avvio precedente
                    if (Environment.GetEnvironmentVariable("MAINPID") is { } mp && root.GetProperty("pid").GetInt32().ToString() != mp) throw new IOException("file vecchio");
                    port = root.GetProperty("port").GetInt32();
                }
                catch { Thread.Sleep(500); } // il server non ha ancora scritto il file, o lo sta scrivendo
            }
            if (port == 0) { Console.Error.WriteLine("firewall: il server non ha indicato la porta, regole non aggiornate."); return 0; }

            if (TryExec("ufw", "status").StartsWith("Status: active"))
            {
                // ufw copia la porta del profilo dentro la regola quando la crea ("app update" non la riallinea): si toglie la regola vecchia e si rimette
                foreach (var n in PrivateNets) TryExec("ufw", "--force", "delete", "allow", "from", n, "to", "any", "app", UfwApp);
                File.WriteAllText(UfwFile, $"[{UfwApp}]\ntitle=Lemora Server\ndescription=Console web e sincronizzazione di Lemora\nports={port}/tcp\n");
                foreach (var n in PrivateNets) TryExec("ufw", "allow", "from", n, "to", "any", "app", UfwApp);
                Console.WriteLine($"firewall: ufw aperto sulla porta {port}/tcp per le reti private.");
            }
            if (TryExec("firewall-cmd", "--state").Trim() == "running")
            {
                Directory.CreateDirectory("/etc/firewalld/services");
                File.WriteAllText(FwdFile, $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<service>\n  <short>Lemora Server</short>\n  <description>Console web e sincronizzazione di Lemora</description>\n  <port protocol=\"tcp\" port=\"{port}\"/>\n</service>\n");
                TryExec("firewall-cmd", "--reload"); // legge la definizione nuova o aggiornata del servizio
                foreach (var n in PrivateNets) TryExec("firewall-cmd", "--permanent", "--add-rich-rule", FwdRule(n));
                TryExec("firewall-cmd", "--reload");
                Console.WriteLine($"firewall: firewalld aperto sulla porta {port}/tcp per le reti private.");
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
        if (any) Console.WriteLine("Regole del firewall rimosse.");
    }

    // ---- risultato per chi ha installato ----

    static string? PrintInfo(string[] args, DateTime since)
    {
        var data = Arg(args, "--data") ?? Paths.DefaultDataDir;
        var file = Path.Combine(data, "runtime.json");
        Console.Write("Servizio avviato, attendo che il server risponda");
        for (int i = 0; i < 60 && !(File.Exists(file) && File.GetLastWriteTimeUtc(file) >= since); i++) { Console.Write('.'); Thread.Sleep(500); }
        Console.WriteLine();
        if (!File.Exists(file) || File.GetLastWriteTimeUtc(file) < since)
        {
            // il server non cambia porta da solo: se quella scelta e' presa da un altro programma lo si dice chiaramente
            int port = ServerConfig.Load(Path.Combine(data, "server.json")).Port;
            try { var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, port); l.Start(); l.Stop(); }
            catch (System.Net.Sockets.SocketException) { return $"La porta {port} e' occupata da un altro programma: chiudilo, il servizio riprova da solo sulla stessa porta."; }
            return "Il server non ha risposto entro 30 secondi: controlla lo stato del servizio.";
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        Console.WriteLine("Lemora Server e' attivo come servizio e parte con il PC. Apri la console da:");
        foreach (var u in doc.RootElement.GetProperty("urls").EnumerateArray()) Console.WriteLine("  " + u.GetString());
        Console.WriteLine("Impronta del certificato (SHA-256): " + doc.RootElement.GetProperty("fingerprint").GetString());
        foreach (var w in FirewallWarnings()) { Console.WriteLine(); Console.WriteLine("  ATTENZIONE: " + w); }
        var codeFile = Path.Combine(data, "setup-code.txt");
        if (File.Exists(codeFile))
        {
            Console.WriteLine();
            Console.WriteLine("  CODICE DI AVVIO: " + File.ReadAllText(codeFile).Trim());
            Console.WriteLine("  Aprila nel browser e inseriscilo per creare l'amministratore.");
        }
        return null;
    }

    /// <summary>cose che il programma non puo' correggere da solo e che impediscono ai PC della rete di raggiungere il server</summary>
    static List<string> FirewallWarnings()
    {
        var list = new List<string>();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var cat = Exec("powershell.exe", "-NoProfile", "-Command", "(Get-NetConnectionProfile | ForEach-Object { $_.NetworkCategory }) -join ','").output;
                if (cat.Contains("Public"))
                    list.Add("una rete di questo PC e' classificata Pubblica: il firewall blocca le connessioni in arrivo. Impostala come Privata (Impostazioni > Rete) se e' la rete dell'ufficio.");
                foreach (var p in new[] { "Domain", "Private" })
                    if (Exec("reg.exe", "query", $@"HKLM\SOFTWARE\Policies\Microsoft\WindowsFirewall\{p}Profile", "/v", "AllowLocalPolicyMerge").output.Contains("0x0"))
                        list.Add($"un criterio di gruppo ignora le regole locali del firewall (profilo {p}): chiedi all'amministratore del dominio di aprire il programma Lemora.Server.exe.");
            }
        }
        catch { /* strumento non presente: niente da segnalare */ }
        return list;
    }

    static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
