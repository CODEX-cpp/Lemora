using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shawn.Utils;
using Stylet;

namespace Lemora.Service.Mcp
{
    /// <summary>
    /// Lemora's MCP server: Claude (Claude Code, Claude Desktop) uses it to open the saved RDP/VNC connections, see the screen and act.
    /// MCP "Streamable HTTP" protocol reduced to the essentials: POST of JSON-RPC messages on http://localhost:PORT/mcp, JSON response.
    /// Only from this PC (localhost), only with the token (Authorization: Bearer ...), no requests from web pages (foreign Origin).
    /// It starts from the switch on the MCP Server page (LEMORA_MCP=1 in development builds), never if the Teams administrator turned it off for this PC.
    /// </summary>
    public static class McpServer
    {
        public const int DefaultPort = 47800;
        private const string ServerName = "lemora";
        private static HttpListener? _listener;

        public static int Port { get; private set; }
        public static bool IsRunning => _listener?.IsListening == true;

        /// <summary>at Lemora startup: starts if it is on in the settings (MCP Server page) and the Teams administrator has not turned it off; LEMORA_MCP=1 for tests</summary>
        public static void StartIfRequested()
        {
            var forced = Lemora.Utils.DevOnly.Env("LEMORA_MCP") == "1";   // development builds only: in the installed version the switch decides
            if (!forced && !McpGuard.Settings.Enabled) return;
            var port = int.TryParse(Lemora.Utils.DevOnly.Env("LEMORA_MCP_PORT"), out var p) && p is > 1024 and < 65536 ? p : McpGuard.Settings.Port;
            Start(port);
        }

        /// <summary>switch or port changed on the MCP Server page: stop and restart with the new values</summary>
        public static void ApplySettings()
        {
            Stop();
            var s = McpGuard.Settings;
            if (s.Enabled && !TeamsClaudePolicy.Current().BlocksAll) Start(s.Port);
            else McpGuard.HideBanner(evenIfStopped: true);
        }

        /// <summary>the Teams administrator's rule has just changed (sync or link check): off = stopped right away, on again = restarted if the user's switch is on</summary>
        public static void ApplyPolicy()
        {
            var blocked = TeamsClaudePolicy.Current().BlocksAll;
            if (blocked && IsRunning)
            {
                Stop();
                McpGuard.HideBanner(evenIfStopped: true);
                SimpleLogHelper.Warning("MCP: server stopped, " + TeamsClaudePolicy.BlockedAllReason);
            }
            else if (!blocked && !IsRunning && McpGuard.Settings.Enabled) Start(McpGuard.Settings.Port);
            // MCP page in the settings: hidden or shown again (and closed if it was open)
            Execute.OnUIThread(() => { try { IoC.TryGet<Lemora.View.Settings.SettingsPageViewModel>()?.RefreshMcpAllowed(); } catch { /* settings not built yet */ } });
        }

        /// <summary>last startup error (e.g. port in use), for the MCP Server page</summary>
        public static string LastError { get; private set; } = "";

        public static void Start(int port)
        {
            if (IsRunning) return;
            // checked here, where every start passes (development variables included): the administrator's block cannot be bypassed
            if (TeamsClaudePolicy.Current().BlocksAll) { SimpleLogHelper.Warning("MCP: server not started, " + TeamsClaudePolicy.BlockedAllReason); return; }
            try
            {
                var l = new HttpListener();
                l.Prefixes.Add($"http://localhost:{port}/");   // "localhost" needs no administrator rights and is not reachable from other PCs
                // also the exact path Claude Code uses: another user registering "/mcp/" in http.sys (more specific than "/") would take
                // the requests without any conflict. This way whoever comes later finds it taken, and if they were there first the startup fails with a warning (security hardening)
                l.Prefixes.Add($"http://localhost:{port}/mcp/");
                l.Start();
                _listener = l;
                Port = port;
                _ = Task.Run(() => AcceptLoop(l));
                LastError = "";
                SimpleLogHelper.Warning($"MCP: server for Claude listening on http://localhost:{port}/mcp");
                // started, but another program can listen on the same port on 127.0.0.1 or ::1 only and grab part of the connections
                WarnPortTaken(port);
            }
            catch (Exception e)
            {
                Port = port;
                LastError = e.Message;
                SimpleLogHelper.Warning($"MCP: server for Claude not started on port {port}: {e.Message}");
                // not started: the address is taken by someone else. If it is another program, name it; if it is the Windows http server (pid 4,
                // registration by another user or program) say so anyway: Claude Code would send it the token and commands (security hardening)
                if (!WarnPortTaken(port) && e is HttpListenerException { ErrorCode: 183 or 32 }) WarnOwner(port, IoC.Translate("mcp_port_owner_httpsys"));
            }
        }

        private static void WarnOwner(int port, string who)
        {
            LastError = string.Format(IoC.Translate("mcp_port_used_by"), port, who);
            SimpleLogHelper.Warning($"MCP: port {port} taken by {who}");
            if (Lemora.Utils.DevOnly.Env("LEMORA_HEADLESS") == "1") return;
            Stylet.Execute.OnUIThread(() => Lemora.Utils.MessageBoxHelper.Warning(string.Format(IoC.Translate("mcp_port_taken_warning"), port, who)));
        }

        /// <summary>
        /// port already taken by another program (even one of another user on the same PC): Claude Code would connect to THAT one,
        /// sending it the token and the commands meant for Lemora (security hardening). Warn right away, clearly visible
        /// </summary>
        /// <returns>true if it found (and reported) another program</returns>
        private static bool WarnPortTaken(int port)
        {
            // pid 4 = Windows http server (the one Lemora itself uses)
            var pid = PortOwner.ListenerPids(port).FirstOrDefault(p => p != 4 && p != Environment.ProcessId);
            if (pid == 0) return false;
            WarnOwner(port, PortOwner.Describe(pid));
            return true;
        }

        public static void Stop()
        {
            try { _listener?.Stop(); _listener?.Close(); } catch { /* already closed */ }
            _listener = null;
        }

        private static async Task AcceptLoop(HttpListener l)
        {
            while (l.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await l.GetContextAsync(); }
                catch { break; }   // stopped
                _ = Task.Run(() => Handle(ctx));
            }
        }

        private static async Task Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var call = "";   // the tool called, for the log
            try
            {
                // a web page open in the browser must not be able to talk to Lemora ("DNS rebinding" attack)
                var origin = req.Headers["Origin"];
                if (!string.IsNullOrEmpty(origin) && !(Uri.TryCreate(origin, UriKind.Absolute, out var o) && (o.Host == "localhost" || o.Host == "127.0.0.1")))
                {
                    await Reply(res, 403, null); return;
                }
                // the connecting program must belong to this user: another user of the PC who stole the token
                // (for example by listening on the port while Lemora was closed) does not command Lemora. Owner not readable = rejection
                // passed through a local proxy: the connected process would be the proxy, not the real sender of the request
                if (new[] { "Via", "X-Forwarded-For", "Forwarded", "Proxy-Connection", "X-Real-IP" }.Any(h => req.Headers[h] != null) || req.RawUrl?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true)
                {
                    SimpleLogHelper.Warning("MCP: request passed through a proxy rejected");
                    await Reply(res, 403, null); return;
                }
                var clientPid = ClientPid(req);
                if (!Lemora.Service.Browser.BrowserProtocol.ProcessIsMine((uint)Math.Max(0, clientPid)))
                {
                    SimpleLogHelper.Warning($"MCP: request from a process of another user or unknown rejected (pid {clientPid})");
                    await Reply(res, 403, null); return;
                }
                // without Origin the only barrier would be the token: requests made by a browser (page or extension) are rejected anyway
                if (BrowserRequest(req.Headers, clientPid) is { } browser)
                {
                    var leaked = TokenOk(req.Headers["Authorization"]);
                    SimpleLogHelper.Warning($"MCP: request from a browser rejected ({browser}){(leaked ? ", WITH THE RIGHT TOKEN" : "")}");
                    if (leaked) WarnTokenLeaked(browser);
                    await Reply(res, 403, null); return;
                }
                if (!TokenOk(req.Headers["Authorization"]))
                {
                    SimpleLogHelper.Warning("MCP: request without a valid token rejected");
                    res.AddHeader("WWW-Authenticate", "Bearer");
                    await Reply(res, 401, null); return;
                }
                if (req.Url?.AbsolutePath.TrimEnd('/') != "/mcp") { await Reply(res, 404, null); return; }
                if (req.HttpMethod == "DELETE") { await Reply(res, 200, null); return; }   // client session end: nothing to free
                if (req.HttpMethod != "POST") { res.AddHeader("Allow", "POST"); await Reply(res, 405, null); return; }

                string body;
                using (var sr = new StreamReader(req.InputStream, Encoding.UTF8)) body = await sr.ReadToEndAsync();
                var token = JToken.Parse(body);
                if (token is JArray batch)
                {
                    var answers = new JArray();
                    foreach (var m in batch.OfType<JObject>()) if (await Dispatch(m) is { } a) answers.Add(a);
                    await Reply(res, answers.Count == 0 ? 202 : 200, answers.Count == 0 ? null : answers);
                }
                else if (token is JObject msg)
                {
                    call = msg["params"]?.Value<string>("name") ?? msg.Value<string>("method") ?? "";
                    var answer = await Dispatch(msg);
                    await Reply(res, answer == null ? 202 : 200, answer);   // notification (no id): 202 with no body
                }
                else await Reply(res, 400, null);
            }
            catch (JsonException) { await Reply(res, 400, Error(null, -32700, "invalid JSON")); }
            catch (HttpListenerException e) when (call != "")
            {
                // Claude closed the request before the response: usually its maximum wait time (it gets "The operation timed out")
                SimpleLogHelper.Warning($"MCP: response to {call} not delivered after {sw.Elapsed.TotalSeconds:0.0} s, Claude was no longer waiting ({e.Message})");
            }
            catch (Exception e)
            {
                SimpleLogHelper.Warning("MCP: error in the request: " + e.Message);
                try { await Reply(res, 500, null); } catch { /* client already gone */ }
            }
        }

        /// <summary>browser programs: an extension can strip Origin from its requests, but the connection still comes from the browser process</summary>
        private static readonly string[] BrowserProcesses = { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "iexplore", "chromium", "thorium", "waterfox", "librewolf", "arc", "zen" };

        /// <summary>
        /// null if the request does not come from a browser, otherwise the reason. Browsers add Sec-Fetch-Site and Sec-Fetch-Dest to every request on their own
        /// (and Sec-CH-UA); Claude Code 2.1 sends no Sec-Fetch-* at all, Node's fetch only "sec-fetch-mode: cors", which therefore does NOT count
        /// (tested; security hardening)
        /// </summary>
        internal static string? BrowserRequest(System.Collections.Specialized.NameValueCollection headers, int clientPid)
        {
            foreach (var k in headers.AllKeys)
                if (k != null && (k.Equals("Sec-Fetch-Site", StringComparison.OrdinalIgnoreCase) || k.Equals("Sec-Fetch-Dest", StringComparison.OrdinalIgnoreCase) ||
                                  k.Equals("Sec-Fetch-User", StringComparison.OrdinalIgnoreCase) || k.StartsWith("Sec-CH-UA", StringComparison.OrdinalIgnoreCase)))
                    return IoC.Translate("mcp_who_header", k);
            if (clientPid > 0)
            {
                try
                {
                    using var p = System.Diagnostics.Process.GetProcessById(clientPid);
                    if (BrowserProcesses.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase)) return IoC.Translate("mcp_who_program", p.ProcessName, clientPid);
                }
                catch { /* process already closed or belongs to another user: the token remains */ }
            }
            return null;
        }

        /// <summary>process that opened the connection (from the TCP table); 0 if not readable</summary>
        private static int ClientPid(HttpListenerRequest req)
        {
            try { if (req.RemoteEndPoint is { } ep && IPAddress.IsLoopback(ep.Address) && req.LocalEndPoint is { } local) return PortOwner.ClientPidExact(ep, local); }
            catch { /* TCP table not readable */ }
            return 0;
        }

        private static int _leakWarned;

        /// <summary>a browser had the right token: the token has leaked (e.g. to an extension), tell the user to regenerate it (once per startup)</summary>
        private static void WarnTokenLeaked(string who)
        {
            McpGuard.Log($"request from a browser with the right token rejected ({who})");
            if (Lemora.Utils.DevOnly.Env("LEMORA_HEADLESS") == "1" || Interlocked.Exchange(ref _leakWarned, 1) == 1) return;
            Stylet.Execute.OnUIThread(() => Lemora.Utils.MessageBoxHelper.Warning(string.Format(IoC.Translate("mcp_token_leaked_warning"), who)));
        }

        private static bool TokenOk(string? header)
        {
            if (header == null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
            var given = Encoding.UTF8.GetBytes(header.Substring(7).Trim());
            var expected = Encoding.UTF8.GetBytes(McpGuard.Token);
            return given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected);
        }

        private static async Task<JObject?> Dispatch(JObject msg)
        {
            var id = msg["id"];
            var method = msg.Value<string>("method") ?? "";
            if (id == null || id.Type == JTokenType.Null) return null;   // notifications (e.g. notifications/initialized): no response
            try
            {
                switch (method)
                {
                    case "initialize":
                        {
                            var asked = msg["params"]?.Value<string>("protocolVersion");
                            return Result(id, new JObject
                            {
                                ["protocolVersion"] = asked is "2025-06-18" or "2025-03-26" or "2024-11-05" ? asked : "2025-06-18",
                                ["capabilities"] = new JObject { ["tools"] = new JObject { ["listChanged"] = false } },
                                ["serverInfo"] = new JObject { ["name"] = ServerName, ["version"] = typeof(McpServer).Assembly.GetName().Version?.ToString() ?? "1" },
                                ["instructions"] = McpTools.Instructions,
                            });
                        }
                    case "ping":
                        return Result(id, new JObject());
                    case "tools/list":
                        return Result(id, new JObject { ["tools"] = McpTools.List() });
                    case "tools/call":
                        {
                            var name = msg["params"]?.Value<string>("name") ?? "";
                            var args = msg["params"]?["arguments"] as JObject ?? new JObject();
                            return Result(id, await McpTools.Call(name, args));
                        }
                    default:
                        return Error(id, -32601, "method not supported: " + method);
                }
            }
            catch (Exception e)
            {
                SimpleLogHelper.Warning($"MCP: {method} failed: {e}");
                // never the bare .NET message (e.g. "The operation has timed out."): what was being done and what to try
                var tool = method == "tools/call" ? msg["params"]?.Value<string>("name") : null;
                var what = e is TimeoutException or OperationCanceledException ? "a step took too long" : $"unexpected error ({e.GetType().Name}: {e.Message})";
                return Error(id, -32603, $"Lemora: {(tool != null ? $"the tool {tool} failed" : $"{method} failed")}: {what}. " +
                                         "Check the state with sessioni_aperte or schermata and try again; if it happens again, the details are in Lemora's log");
            }
        }

        private static JObject Result(JToken id, JObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        private static JObject Error(JToken? id, int code, string message) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JObject { ["code"] = code, ["message"] = message } };

        private static async Task Reply(HttpListenerResponse res, int status, JToken? body)
        {
            res.StatusCode = status;
            if (body != null)
            {
                var bytes = Encoding.UTF8.GetBytes(body.ToString(Formatting.None));
                res.ContentType = "application/json; charset=utf-8";
                res.ContentLength64 = bytes.Length;
                await res.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            }
            res.Close();
        }
    }
}
