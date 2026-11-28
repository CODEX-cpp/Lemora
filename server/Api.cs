using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Lemora.Server;

internal static class Api
{
    private static readonly AttemptLimiter SetupLimiter = new(5, TimeSpan.FromMinutes(5));
    private static readonly AttemptLimiter LoginLimiter = new(5, TimeSpan.FromMinutes(5));
    // all addresses together: 30 failures in 5 minutes = console closed to logins for 5 minutes (someone trying from many IPs gets no more than ~6 passwords a minute)
    private static readonly AttemptLimiter LoginAllLimiter = new(30, TimeSpan.FromMinutes(5));

    private record CodeReq(string? Code);
    private record FolderReq(string? Path);
    private record PortReq(int Port, string? Address);
    private record CompleteReq(string? DbDir, string? User, string? Password, int Port, string? Address);
    private record LoginReq(string? User, string? Password);
    private record AutoReq(bool On);

    internal static string Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "?";
    private static bool IsLoopback(HttpContext c) => c.Connection.RemoteIpAddress is { } a && IPAddress.IsLoopback(a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a);
    /// <summary>addresses an administrator has signed in from (in memory, 30 days): exempt from the global attempt cap</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> GoodLoginIps = new();

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
        // without a linked user the session is invalid (closed by Delete); renew only if it still exists: never put back a session just removed
        if (!Admins.SessionUser.ContainsKey(t!)) { State.Sessions.TryRemove(t!, out _); return false; }
        if (State.Sessions.TryGetValue(t!, out var exp)) State.Sessions.TryUpdate(t!, DateTime.UtcNow.AddHours(8), exp); // expires after 8 hours of inactivity
        return Admins.SessionUser.ContainsKey(t!);
    }

    private static bool SetupAllowed(HttpContext c) => !State.Config.Configured && Valid(State.SetupTokens, c.Request.Cookies["setup"]);

    /// <summary>console logins in the event log (never the password); a log error does not block the login</summary>
    private static void LogLogin(string kind, string detail, string? admin)
    {
        try { using var c = Pcs.Open(); Pcs.Log(c, null, kind, detail, admin); }
        catch (Exception ex) { Console.Error.WriteLine($"access log: {ex.Message}"); }
    }

    /// <summary>name typed by whoever tries to sign in: at most 64 characters, no line breaks (keeps the log readable)</summary>
    private static string Short(string? s)
    {
        s = new string((s ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return s.Length > 64 ? s[..64] + "…" : s;
    }

    public static void Map(WebApplication app)
    {
        // security headers and protection against requests made by other web pages
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
            Pcs.CurrentAdmin.Value = Admins.Current(c);   // the event log (Pcs.Log) records who performed the action from the console
            try { await next(); }
            catch (Exception ex) when (!c.Response.HasStarted) { Errors.Log(c, ex); c.Response.StatusCode = 500; }   // the error used to vanish: now it is written to errors.log
        });

        var api = app.MapGroup("/api");

        api.MapGet("/state", (HttpContext c) => Results.Json(new
        {
            configured = State.Config.Configured,
            signedIn = SignedIn(c),
            setupVerified = SetupAllowed(c),
            // internal paths and addresses only while the server is being set up: afterwards nobody sees them without signing in
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

        // automatic update (Updater): version, last night's result, on/off. The update itself is done by the scheduled task, not by the service
        api.MapGet("/update", (HttpContext c) =>
        {
            if (!SignedIn(c)) return Results.StatusCode(401);
            var st = Updater.ReadState(State.ConfigDir);
            return Results.Json(new { version = Updater.Current.ToString(), auto = State.Config.AutoUpdate, hour = Updater.Hour, last = st });
        });
        api.MapPost("/update/auto", (HttpContext c, AutoReq r) =>
        {
            if (!SignedIn(c)) return Results.StatusCode(401);
            State.Config.AutoUpdate = r.On;
            State.Config.Save(State.ConfigFile);
            LogLogin(r.On ? "update-auto-on" : "update-auto-off", r.On ? $"automatic updates turned on (every night at {Updater.Hour})" : "automatic updates turned off", Admins.Current(c));
            return Results.Json(new { ok = true, auto = r.On });
        });

        api.MapPost("/setup/verify", (HttpContext c, CodeReq r) =>
        {
            if (State.Config.Configured) return Results.NotFound();
            var ip = Ip(c);
            int left = SetupLimiter.Take(ip, out var until);   // counted before the check: no parallel bursts
            if (left < 0) return Results.Json(new { error = "blocked", until }, statusCode: 429);
            var given = Encoding.UTF8.GetBytes((r.Code ?? "").Replace("-", "").Replace(" ", "").ToUpperInvariant());
            var want = Encoding.UTF8.GetBytes(State.SetupCode.Replace("-", ""));
            if (State.SetupCode.Length == 0 || !CryptographicOperations.FixedTimeEquals(given, want))
                return left == 0 ? Results.Json(new { error = "blocked", until = DateTime.UtcNow.AddMinutes(5) }, statusCode: 429) : Results.Json(new { error = "code", remaining = left }, statusCode: 401);
            SetupLimiter.Succeeded(ip);
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
            if ((r.Password ?? "").Length < 6) errors.Add("password");
            if (errors.Count > 0) return Results.Json(new { errors }, statusCode: 400);

            var dir = Path.GetFullPath(r.DbDir!);
            DirAcl.Protect(dir);   // before writing the database: the folder does not inherit the parent's permissions
            try { Admins.CreateFirst(dir, r.User!.Trim(), r.Password!); }
            catch (Exception ex) { return Results.Json(new { errors = new[] { "db:" + ex.Message } }, statusCode: 500); }

            bool restart = r.Port != State.CurrentPort || (r.Address ?? "all") != State.Config.Listen;
            State.Config = new ServerConfig { DbDir = dir, Port = r.Port, Listen = r.Address ?? "all", Configured = true };
            State.Config.Save(State.ConfigFile);
            State.SetupTokens.Clear();
            State.ClearSetupCode();
            Console.WriteLine("Setup completed: administrator created.");
            if (restart) _ = Task.Run(async () => { await Task.Delay(800); State.Restart.TrySetResult(); });

            var hosts = r.Address is null or "all" ? State.Addresses.Select(a => a.ToString()) : [r.Address];
            return Results.Json(new { port = r.Port, urls = hosts.Select(h => $"https://{h}:{r.Port}"), fingerprint = CertStore.Fingerprint(State.Cert), dbDir = dir, restart });
        });

        api.MapPost("/login", async (HttpContext c, LoginReq r) =>
        {
            if (!State.Config.Configured) return Results.NotFound();
            var ip = Ip(c);
            // attempt counted before the check (even with parallel requests) plus a cap for all addresses together (many IPs)
            int left = LoginLimiter.Take(ip, out var until);
            if (left < 0) return Results.Json(new { error = "blocked", until }, statusCode: 429);
            // the global cap must not lock the administrator out (anyone on the network could fill it with 7 addresses): it does not apply to the server
            // itself (127.0.0.1/::1) nor to addresses an administrator has already signed in from
            bool trusted = IsLoopback(c) || (GoodLoginIps.TryGetValue(ip, out var seen) && DateTime.UtcNow - seen < TimeSpan.FromDays(30));
            if (!trusted && LoginAllLimiter.Take("*", out var untilAll) < 0) return Results.Json(new { error = "blocked", until = untilAll }, statusCode: 429);
            var who = Admins.Check(r.User ?? "", r.Password ?? "");
            bool ok = who != null;
            await Task.Delay(250); // slows down rapid-fire password guessing
            LogLogin(ok ? "admin-login" : "admin-login-failed", ok ? $"from {ip}" : $"user '{Short(r.User)}' from {ip}", who);
            if (!ok)
                return left == 0 ? Results.Json(new { error = "blocked", until = DateTime.UtcNow.AddMinutes(5) }, statusCode: 429) : Results.Json(new { error = "credentials", remaining = left }, statusCode: 401);
            LoginLimiter.Succeeded(ip);
            if (!trusted) LoginAllLimiter.Undo("*");
            GoodLoginIps[ip] = DateTime.UtcNow;
            var token = Admins.StartSession(who!, TimeSpan.FromHours(8));
            if (token == null) return Results.Json(new { error = "credentials", remaining = left }, statusCode: 401);   // deleted in the meantime
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
        BrowserLogApi.Map(api);
        ConflictsApi.Map(api);
        Admins.Map(api);
    }

    /// <summary>checks that the folder exists (or can be created) and is writable</summary>
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

    /// <summary>actually tries to open the port on the chosen addresses; null = fine. A port already used by ourselves is fine</summary>
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
