using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Lemora.Server;

// Startup: prepares the certificate and setup code, starts the web server listening and stays running.
// All settings are made from the web page. Options (for tests and special installations):
//   --data <folder>     where the certificate and settings live (default: ProgramData / /var/lib)
//   --port <number>     port to use (default 25353; before configuration, if it is busy, the next one is tried)
//   --foreground        stays in the terminal without installing the service (for tests and development)
//   --test              asks whether to install the service instead of doing it right away
//   --uninstall         removes the service (--purge also deletes settings and certificate; without --purge and with a console it asks what to do)
// Without options it installs the always-on service, asking for administrator permissions right away.
// On Windows the program copies itself to "Program Files\Lemora Server" and shows up in Settings > Apps (Uninstall runs --uninstall).
if (args.Contains("--version")) { Console.WriteLine(Updater.Current); return 0; } // read by the updater to check a new program before installing it
if (args.Contains("--update")) return Updater.Run(args, Arg("--data")); // run by the scheduled task / systemd timer at 02:00, as administrator
if (args.Contains("--firewall-sync")) return Svc.FirewallSync(Arg("--data")); // run by systemd as root at every service start
if (args.Contains("--register-uninstall-key") || args.Contains("--remove-uninstall-key")) return Svc.UninstallKeyTest(args, Arg("--data")); // tests only
if (args.Contains("--save-port-test")) { var e = Svc.SavePreferredPort(args, Arg("--data")); if (e != null) Console.Error.WriteLine(e); return e == null ? 0 : 3; } // tests only
bool service = args.Contains("--service"); // run by the service manager
if (!service)
{
    if (args.Contains("--uninstall")) return Svc.Uninstall(args, Arg("--data"));
    if (!args.Contains("--foreground") && (!args.Contains("--test") || Svc.AskInstall())) return Svc.Install(args, Arg("--data"));
}

State.ConfigDir = Arg("--data") ?? Paths.DefaultDataDir;
Directory.CreateDirectory(State.ConfigDir);
State.Config = ServerConfig.Load(State.ConfigFile);
bool portForced = int.TryParse(Arg("--port"), out var forced);
if (portForced)
{
    // once the server is configured the port is the saved one: paired PCs use it and changing it would leave them without a server
    if (State.Config.Configured && forced != State.Config.Port)
    {
        Console.Error.WriteLine($"Server already configured on port {State.Config.Port}: --port {forced} is not allowed, paired PCs would be lost. Start it without --port.");
        return 2;
    }
    State.Config.Port = forced;
}

// at PC startup the network may not be ready yet: as a service it waits up to 5 minutes
for (int i = 0; ; i++)
{
    State.Addresses = NetUtil.PrivateAddresses();
    if (State.Addresses.Count > 0) break;
    if (!service || i >= 150)
    {
        Console.Error.WriteLine("No local network address found: the server only accepts private networks (10.x, 172.16-31.x, 192.168.x).");
        return 1;
    }
    await Task.Delay(2000);
}
State.Cert = CertStore.LoadOrCreate(Path.Combine(State.ConfigDir, "server.pfx"), State.Addresses);
State.LoadSetupCode();
try { File.Delete(Path.Combine(State.ConfigDir, "runtime.json")); } catch { } // whoever waits for it (installer, firewall) wants the one from this start, not an old one
// first start after a night update: once in the activity log (the updater runs without the service and does not write to the database)
try
{
    var upd = Updater.ReadState(State.ConfigDir);
    var mark = Path.Combine(State.ConfigDir, "update-logged.txt");
    if (State.Config.Configured && upd is { Result: "ok" } && upd.Current == Updater.Current.ToString() && (!File.Exists(mark) || File.ReadAllText(mark).Trim() != upd.Current))
    {
        using (var c = Pcs.Open()) Pcs.Log(c, null, "server-updated", upd.Detail);
        File.WriteAllText(mark, upd.Current);
    }
}
catch (Exception ex) { Console.Error.WriteLine("update log: " + ex.Message); }

while (true)
{
    State.Restart = new(TaskCreationOptions.RunContinuationsAsynchronously);
    var app = await StartAsync();
    if (app == null) return 2;

    Console.WriteLine();
    Console.WriteLine("Lemora Server listening. Open the console from one of these addresses:");
    foreach (var ip in Listening().Where(x => x != IPAddress.Loopback.ToString())) Console.WriteLine($"  https://{ip}:{State.CurrentPort}");
    Console.WriteLine($"Certificate fingerprint (SHA-256): {CertStore.Fingerprint(State.Cert)}");
    Console.WriteLine($"Settings folder: {State.ConfigDir}");
    if (!State.Config.Configured)
    {
        Console.WriteLine();
        Console.WriteLine($"  SETUP CODE: {State.SetupCode}");
        Console.WriteLine("  Open it in the browser and enter it to create the administrator.");
    }

    File.WriteAllText(Path.Combine(State.ConfigDir, "runtime.json"), JsonSerializer.Serialize(new
    {
        pid = Environment.ProcessId, // systemd compares it with $MAINPID: it needs this start's file, not the previous one's
        version = Updater.Current.ToString(), // the updater waits for the new version here before declaring the update done
        port = State.CurrentPort,
        fingerprint = CertStore.Fingerprint(State.Cert),
        urls = Listening().Where(x => x != IPAddress.Loopback.ToString()).Select(ip => $"https://{ip}:{State.CurrentPort}"),
    }));

    var done = await Task.WhenAny(app.WaitForShutdownAsync(), State.Restart.Task);
    if (service && done == State.Restart.Task)
    {
        // as a service the server is not recreated in the same process: it exits with an error and the service manager (or systemd) restarts it
        await Task.Delay(500);
        Environment.Exit(1);
    }
    await app.StopAsync();
    await app.DisposeAsync();
    if (done != State.Restart.Task) return 0;
    Console.WriteLine("Restarting with the new settings.");
}

async Task<WebApplication?> StartAsync()
{
    // Before configuration (no paired PC) and without --port: if the default is busy the next one is tried and the wizard
    // offers the one that is actually free, which is then saved. After configuration the port never changes: if it is busy it retries
    // the same one for 5 minutes (then the service manager restarts it). With --port before the wizard it exits right away with the message.
    bool configured = State.Config.Configured;
    bool autoPort = !configured && !portForced;
    int port = State.Config.Port, tries = autoPort ? 30 : configured || service ? 60 : 1;
    for (int i = 0; i < tries; i++)
    {
        // as a service the working folder is / (or System32): without a fixed root the server scans the whole disk looking for configuration files
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        if (service && OperatingSystem.IsWindows()) builder.Host.UseWindowsService(o => o.ServiceName = Svc.WinName);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Server.Kestrel", LogLevel.None); // we handle the busy port ourselves, without a stack trace in the terminal
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.None);
        builder.Logging.AddFilter("Microsoft.Extensions.Hosting.Internal.Host", LogLevel.None);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            foreach (var ip in Listening().Select(IPAddress.Parse))
                k.Listen(ip, port, o => o.UseHttps(State.Cert));
        });
        var app = builder.Build();
        var files = new ManifestEmbeddedFileProvider(typeof(Api).Assembly, "wwwroot");
        Api.Map(app); // first: the security headers also apply to the static pages
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
        app.MapFallback(async c =>
        {
            c.Response.ContentType = "text/html; charset=utf-8";
            await using var s = files.GetFileInfo("index.html").CreateReadStream();
            await s.CopyToAsync(c.Response.Body);
        });

        try
        {
            await app.StartAsync();
            State.CurrentPort = port;
            return app;
        }
        catch (Exception ex) when (ex is IOException or SocketException || ex.InnerException is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
        {
            await app.DisposeAsync();
            Console.Error.WriteLine($"Port {port} is in use by another program: close it or choose another port.");
            if (autoPort && i + 1 < tries) { port++; Console.Error.WriteLine($"  Trying {port}."); }
            else if (i + 1 < tries)
            {
                Console.Error.WriteLine($"  Retrying the same port in 5 seconds ({i + 1}/{tries}).");
                await Task.Delay(5000);
            }
        }
    }
    Console.Error.WriteLine(configured
        ? $"Server not started: port {port} is still in use. Paired PCs use this port: free port {port}."
        : $"Server not started. To use another port start it with --port <number> (e.g. --port {port + 1}); you can choose it again during setup.");
    return null;
}

IEnumerable<string> Listening()
{
    var ips = State.Config.Listen is "all" or "" || !IPAddress.TryParse(State.Config.Listen, out var one) || !State.Addresses.Contains(one)
        ? State.Addresses
        : [one];
    return ips.Select(a => a.ToString()).Append(IPAddress.Loopback.ToString());
}

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
