using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Lemora.Server;

// Avvio: prepara certificato e codice di avvio, mette in ascolto il server web e resta attivo.
// Tutte le impostazioni si fanno dalla pagina web. Opzioni (per prove e installazioni particolari):
//   --data <cartella>   dove stanno certificato e impostazioni (predefinita: ProgramData / /var/lib)
//   --port <numero>     porta da usare (predefinita 25353; prima della configurazione, se e' occupata, si prova la successiva)
//   --foreground        resta nel terminale senza installare il servizio (per prove e sviluppo)
//   --test              chiede se installare il servizio invece di farlo subito
//   --uninstall         toglie il servizio (--purge cancella anche impostazioni e certificato; senza --purge e con una console chiede cosa fare)
// Senza opzioni installa il servizio sempre attivo, chiedendo subito i permessi di amministratore.
// Su Windows il programma si copia in "Programmi\Lemora Server" e compare in Impostazioni > App (Disinstalla lancia --uninstall).
if (args.Contains("--firewall-sync")) return Svc.FirewallSync(Arg("--data")); // lanciato da systemd come root a ogni avvio del servizio
if (args.Contains("--register-uninstall-key") || args.Contains("--remove-uninstall-key")) return Svc.UninstallKeyTest(args, Arg("--data")); // solo prove
if (args.Contains("--save-port-test")) { var e = Svc.SavePreferredPort(args, Arg("--data")); if (e != null) Console.Error.WriteLine(e); return e == null ? 0 : 3; } // solo prove
bool service = args.Contains("--service"); // lanciato dal gestore dei servizi
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
    // a server configurato la porta e' quella salvata: i PC abbinati la usano e cambiarla li lascerebbe senza server
    if (State.Config.Configured && forced != State.Config.Port)
    {
        Console.Error.WriteLine($"Server gia' configurato sulla porta {State.Config.Port}: --port {forced} non e' ammesso, i PC abbinati si perderebbero. Avvialo senza --port.");
        return 2;
    }
    State.Config.Port = forced;
}

// all'avvio del PC la rete puo' non essere ancora pronta: come servizio si aspetta fino a 5 minuti
for (int i = 0; ; i++)
{
    State.Addresses = NetUtil.PrivateAddresses();
    if (State.Addresses.Count > 0) break;
    if (!service || i >= 150)
    {
        Console.Error.WriteLine("Nessun indirizzo di rete locale trovato: il server accetta solo reti private (10.x, 172.16-31.x, 192.168.x).");
        return 1;
    }
    await Task.Delay(2000);
}
State.Cert = CertStore.LoadOrCreate(Path.Combine(State.ConfigDir, "server.pfx"), State.Addresses);
State.LoadSetupCode();
try { File.Delete(Path.Combine(State.ConfigDir, "runtime.json")); } catch { } // chi lo aspetta (installazione, firewall) vuole quello di questo avvio, non uno vecchio

while (true)
{
    State.Restart = new(TaskCreationOptions.RunContinuationsAsynchronously);
    var app = await StartAsync();
    if (app == null) return 2;

    Console.WriteLine();
    Console.WriteLine("Lemora Server in ascolto. Apri la console da uno di questi indirizzi:");
    foreach (var ip in Listening().Where(x => x != IPAddress.Loopback.ToString())) Console.WriteLine($"  https://{ip}:{State.CurrentPort}");
    Console.WriteLine($"Impronta del certificato (SHA-256): {CertStore.Fingerprint(State.Cert)}");
    Console.WriteLine($"Cartella delle impostazioni: {State.ConfigDir}");
    if (!State.Config.Configured)
    {
        Console.WriteLine();
        Console.WriteLine($"  CODICE DI AVVIO: {State.SetupCode}");
        Console.WriteLine("  Aprila nel browser e inseriscilo per creare l'amministratore.");
    }

    File.WriteAllText(Path.Combine(State.ConfigDir, "runtime.json"), JsonSerializer.Serialize(new
    {
        pid = Environment.ProcessId, // systemd lo confronta con $MAINPID: serve il file di questo avvio, non quello del precedente
        port = State.CurrentPort,
        fingerprint = CertStore.Fingerprint(State.Cert),
        urls = Listening().Where(x => x != IPAddress.Loopback.ToString()).Select(ip => $"https://{ip}:{State.CurrentPort}"),
    }));

    var done = await Task.WhenAny(app.WaitForShutdownAsync(), State.Restart.Task);
    if (service && done == State.Restart.Task)
    {
        // come servizio non si ricrea il server nello stesso processo: si esce con errore e il gestore dei servizi (o systemd) lo riavvia
        await Task.Delay(500);
        Environment.Exit(1);
    }
    await app.StopAsync();
    await app.DisposeAsync();
    if (done != State.Restart.Task) return 0;
    Console.WriteLine("Riavvio con le nuove impostazioni.");
}

async Task<WebApplication?> StartAsync()
{
    // Prima della configurazione (nessun PC abbinato) e senza --port: se la predefinita e' occupata si prova la successiva e il wizard
    // propone quella davvero libera, che poi resta salvata. Dopo la configurazione la porta non cambia piu': se e' occupata si riprova
    // sulla stessa per 5 minuti (poi il gestore dei servizi lo rilancia). Con --port prima del wizard si esce subito con il messaggio.
    bool configured = State.Config.Configured;
    bool autoPort = !configured && !portForced;
    int port = State.Config.Port, tries = autoPort ? 30 : configured || service ? 60 : 1;
    for (int i = 0; i < tries; i++)
    {
        // come servizio la cartella di lavoro e' / (o System32): senza una radice fissa il server controlla tutto il disco cercando file di configurazione
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        if (service && OperatingSystem.IsWindows()) builder.Host.UseWindowsService(o => o.ServiceName = Svc.WinName);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Server.Kestrel", LogLevel.None); // la porta occupata la gestiamo noi, senza stack nel terminale
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
        Api.Map(app); // prima: le intestazioni di sicurezza valgono anche per le pagine statiche
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
            Console.Error.WriteLine($"La porta {port} e' occupata da un altro programma: chiudilo o scegli un'altra porta.");
            if (autoPort && i + 1 < tries) { port++; Console.Error.WriteLine($"  Provo la {port}."); }
            else if (i + 1 < tries)
            {
                Console.Error.WriteLine($"  Riprovo sulla stessa porta tra 5 secondi ({i + 1}/{tries}).");
                await Task.Delay(5000);
            }
        }
    }
    Console.Error.WriteLine(configured
        ? $"Server non avviato: la porta {port} e' ancora occupata. I PC abbinati usano questa porta: libera la {port}."
        : $"Server non avviato. Per usare un'altra porta avvialo con --port <numero> (es. --port {port + 1}); nella configurazione potrai sceglierla di nuovo.");
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
