using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Lemora.Server;

internal static class Api
{
    private static readonly AttemptLimiter SetupLimiter = new(5, TimeSpan.FromMinutes(5));
    private static readonly AttemptLimiter LoginLimiter = new(5, TimeSpan.FromMinutes(5));

    private record CodeReq(string? Code);
    private record FolderReq(string? Path);
    private record PortReq(int Port, string? Address);
    private record CompleteReq(string? DbDir, string? User, string? Password, int Port, string? Address);
    private record LoginReq(string? User, string? Password);

    internal static string Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "?";

    private static void SetCookie(HttpContext c, string name, string value, TimeSpan life) =>
        c.Response.Cookies.Append(name, value, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = life });

    private static bool Valid(IDictionary<string, DateTime> store, string? token)
    {
        if (string.IsNullOrEmpty(token) || !store.TryGetValue(token, out var exp)) return false;
        if (exp > DateTime.UtcNow) return true;
        store.Remove(token);
        return false;
    }

    internal static bool SignedIn(HttpContext c)
    {
        var t = c.Request.Cookies["sid"];
        if (!Valid(State.Sessions, t)) return false;
        State.Sessions[t!] = DateTime.UtcNow.AddHours(8); // la sessione scade dopo 8 ore senza uso
        return true;
    }

    private static bool SetupAllowed(HttpContext c) => !State.Config.Configured && Valid(State.SetupTokens, c.Request.Cookies["setup"]);

    public static void Map(WebApplication app)
    {
        // intestazioni di sicurezza e protezione dalle richieste fatte da altre pagine web
        app.Use(async (c, next) =>
        {
            var h = c.Response.Headers;
            h["Content-Security-Policy"] = "default-src 'self'; style-src 'self'; script-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            h["X-Content-Type-Options"] = "nosniff";
            h["Referrer-Policy"] = "no-referrer";
            h["Cache-Control"] = "no-store";
            if (HttpMethods.IsPost(c.Request.Method) && c.Request.Headers["X-Lemora"] != "1")
            {
                c.Response.StatusCode = 403;
                return;
            }
            await next();
        });

        var api = app.MapGroup("/api");

        api.MapGet("/state", (HttpContext c) => Results.Json(new
        {
            configured = State.Config.Configured,
            signedIn = SignedIn(c),
            setupVerified = SetupAllowed(c),
            // percorsi e indirizzi interni solo finche' si sta creando il server: dopo non li vede nessuno senza accesso
            defaults = State.Config.Configured ? null : new
            {
                dbDir = Path.Combine(State.ConfigDir, "db"),
                port = State.CurrentPort,
                addresses = State.Addresses.Select(a => a.ToString()),
            },
        }));

        api.MapGet("/me", (HttpContext c) => !SignedIn(c) ? Results.StatusCode(401) : Results.Json(new
        {
            port = State.CurrentPort,
            urls = State.Addresses.Select(a => $"https://{a}:{State.CurrentPort}"),
            fingerprint = CertStore.Fingerprint(State.Cert),
            dbDir = State.Config.DbDir,
            sqliteVersion = Admin.SqliteVersion(),
            user = Admins.Current(c),
        }));

        api.MapPost("/setup/verify", (HttpContext c, CodeReq r) =>
        {
            if (State.Config.Configured) return Results.NotFound();
            var ip = Ip(c);
            if (SetupLimiter.IsBlocked(ip, out var until)) return Results.Json(new { error = "blocked", until }, statusCode: 429);
            var given = Encoding.UTF8.GetBytes((r.Code ?? "").Replace("-", "").Replace(" ", "").ToUpperInvariant());
            var want = Encoding.UTF8.GetBytes(State.SetupCode.Replace("-", ""));
            if (State.SetupCode.Length == 0 || !CryptographicOperations.FixedTimeEquals(given, want))
            {
                int left = SetupLimiter.Fail(ip);
                return left == 0 ? Results.Json(new { error = "blocked", until = DateTime.UtcNow.AddMinutes(5) }, statusCode: 429) : Results.Json(new { error = "code", remaining = left }, statusCode: 401);
            }
            SetupLimiter.Reset(ip);
            var token = State.NewToken();
            State.SetupTokens[token] = DateTime.UtcNow.AddMinutes(30);
            SetCookie(c, "setup", token, TimeSpan.FromMinutes(30));
            return Results.Json(new { ok = true });
        });

        api.MapPost("/setup/check-folder", (HttpContext c, FolderReq r) =>
        {
            if (!SetupAllowed(c)) return Results.StatusCode(403);
            return Results.Json(CheckFolder(r.Path));
        });

        api.MapPost("/setup/check-port", (HttpContext c, PortReq r) =>
        {
            if (!SetupAllowed(c)) return Results.StatusCode(403);
            return Results.Json(new { error = CheckPort(r.Port, r.Address) });
        });

        api.MapPost("/setup/complete", (HttpContext c, CompleteReq r) =>
        {
            if (!SetupAllowed(c)) return Results.StatusCode(403);
            var errors = new List<string>();
            var folder = CheckFolder(r.DbDir);
            if (folder.error != null) errors.Add("folder:" + folder.error);
            var portErr = CheckPort(r.Port, r.Address);
            if (portErr != null) errors.Add("port:" + portErr);
            if (string.IsNullOrWhiteSpace(r.User) || r.User.Length > 64) errors.Add("user");
            if ((r.Password ?? "").Length < 10) errors.Add("password");
            if (errors.Count > 0) return Results.Json(new { errors }, statusCode: 400);

            var dir = Path.GetFullPath(r.DbDir!);
            try { Admins.CreateFirst(dir, r.User!.Trim(), r.Password!); }
            catch (Exception ex) { return Results.Json(new { errors = new[] { "db:" + ex.Message } }, statusCode: 500); }

            bool restart = r.Port != State.CurrentPort || (r.Address ?? "all") != State.Config.Listen;
            State.Config = new ServerConfig { DbDir = dir, Port = r.Port, Listen = r.Address ?? "all", Configured = true };
            State.Config.Save(State.ConfigFile);
            State.SetupTokens.Clear();
            State.ClearSetupCode();
            Console.WriteLine("Configurazione completata: amministratore creato.");
            if (restart) _ = Task.Run(async () => { await Task.Delay(800); State.Restart.TrySetResult(); });

            var hosts = r.Address is null or "all" ? State.Addresses.Select(a => a.ToString()) : [r.Address];
            return Results.Json(new { port = r.Port, urls = hosts.Select(h => $"https://{h}:{r.Port}"), fingerprint = CertStore.Fingerprint(State.Cert), dbDir = dir, restart });
        });

        api.MapPost("/login", async (HttpContext c, LoginReq r) =>
        {
            if (!State.Config.Configured) return Results.NotFound();
            var ip = Ip(c);
            if (LoginLimiter.IsBlocked(ip, out var until)) return Results.Json(new { error = "blocked", until }, statusCode: 429);
            var who = Admins.Check(r.User ?? "", r.Password ?? "");
            bool ok = who != null;
            await Task.Delay(250); // rallenta chi prova password a raffica
            if (!ok)
            {
                int left = LoginLimiter.Fail(ip);
                return left == 0 ? Results.Json(new { error = "blocked", until = DateTime.UtcNow.AddMinutes(5) }, statusCode: 429) : Results.Json(new { error = "credentials", remaining = left }, statusCode: 401);
            }
            LoginLimiter.Reset(ip);
            var token = State.NewToken();
            State.Sessions[token] = DateTime.UtcNow.AddHours(8);
            Admins.SessionUser[token] = who!;
            SetCookie(c, "sid", token, TimeSpan.FromHours(8));
            return Results.Json(new { ok = true });
        });

        api.MapPost("/logout", (HttpContext c) =>
        {
            var t = c.Request.Cookies["sid"];
            if (t != null) { State.Sessions.Remove(t, out _); Admins.SessionUser.TryRemove(t, out _); }
            c.Response.Cookies.Delete("sid");
            return Results.Json(new { ok = true });
        });

        PairingApi.Map(api);
        ConnectionsApi.Map(api);
        SharedApi.Map(api);
        RequestsApi.Map(api);
        PersonalApi.Map(api);
        TrashApi.Map(api);
        EventsApi.Map(api);
        SessionsApi.Map(api);
        ConflictsApi.Map(api);
        Admins.Map(api);
    }

    /// <summary>verifica che la cartella esista (o si possa creare) e sia scrivibile</summary>
    private record FolderCheck(string? error, long freeBytes, long totalBytes);

    private static FolderCheck CheckFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return new("path", 0, 0);
        try
        {
            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(full);
            var probe = Path.Combine(full, ".lemora-write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            var d = new DriveInfo(Path.GetPathRoot(full)!);
            return new(null, d.AvailableFreeSpace, d.TotalSize);
        }
        catch { return new("write", 0, 0); }
    }

    /// <summary>prova ad aprire davvero la porta sugli indirizzi scelti; null = va bene. La porta in uso da noi stessi va bene</summary>
    private static string? CheckPort(int port, string? address)
    {
        if (port < 1024 || port > 65535) return "range";
        IEnumerable<IPAddress> ips;
        if (string.IsNullOrEmpty(address) || address == "all") ips = State.Addresses;
        else if (IPAddress.TryParse(address, out var ip) && State.Addresses.Contains(ip)) ips = [ip];
        else return "address";
        if (port == State.CurrentPort) return null;
        foreach (var ip in ips)
        {
            try { using var l = new TcpListener(ip, port); l.Start(); l.Stop(); }
            catch (SocketException) { return "busy"; }
        }
        return null;
    }
}
