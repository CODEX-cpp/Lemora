using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Lemora.Service.Mcp
{
    /// <summary>
    /// "Code mode": Claude sends one JavaScript script that calls Lemora's MCP tools as functions (clic({sessione, testo}), attendi(...), ...),
    /// with loops and conditions, instead of one tool call per step: fewer round trips and fewer tokens.
    /// The script runs in Jint (a JavaScript interpreter written in .NET) with NO access to .NET, files, network or processes:
    /// it only sees the functions given here, which go through McpTools.Call like a normal call (same rules, approvals, "Stop Claude", log).
    /// It runs as a job: esegui_script waits up to ~45 s, then Claude follows it with stato_script (its client stops waiting after about a minute).
    /// </summary>
    public sealed class McpScript
    {
        public const int MaxCalls = 1000;          // tool calls per script
        public const int MaxSeconds = 1800;        // total duration
        public const int MaxImages = 4;            // images attached to the answer with allega()
        private const int MaxLogLines = 400;
        private const int MaxResultChars = 20000;
        internal const int MaxStringChars = 10_000_000;   // repeat/padStart/padEnd/join/concat
        // 5_000_000 stopped normal long loops (5M objects in 8 s) as "endless loop?": duration and memory have their own limits
        internal const int MaxStatements = 50_000_000;
        internal const int MaxArrayItems = 1 << 20;       // 1_048_576: a power of two, the capacity Jint checks
        private static readonly string[] NotFromScripts = { "esegui_script", "stato_script", "ferma_script" };
        private static readonly string[] Secret = { "password", "utente", "dominio" };

        private readonly Func<string, JObject, Task<JObject>> _call;
        private readonly Func<string?> _halted;
        private readonly CancellationTokenSource _cts;
        private readonly Stopwatch _sw = new();
        private readonly object _lock = new();
        private readonly List<string> _log = new();
        private readonly Dictionary<int, JObject> _images = new();   // the latest images returned by the tools, by handle (older ones dropped: memory)
        private readonly List<int> _attached = new();
        private int _nextImage;
        private const int KeptImages = 20;
        private int _droppedLog;

        public string Id { get; } = Guid.NewGuid().ToString("N").Substring(0, 8);
        public int Calls { get; private set; }

        private readonly Dictionary<string, int> _counts = new();
        /// <summary>
        /// the tools really called, with how many times ("clic x3, tasto x1"): for the activity log at the end. The "[calls ...]" at the start is read from the code
        /// and missed calls with a name built at run time (globalThis['cl' + 'ic'])
        /// </summary>
        public string CallSummary { get { lock (_lock) return string.Join(", ", _counts.Select(c => $"{c.Key} x{c.Value}")); } }
        public bool Done { get; private set; }
        public string? Result { get; private set; }
        public string? Error { get; private set; }
        public Task Run { get; private set; } = Task.CompletedTask;
        public TimeSpan Elapsed => _sw.Elapsed;

        /// <param name="call">the tool executor (McpTools.Call; a fake one in the tests)</param>
        /// <param name="halted">reason why Claude must stop now ("Stop Claude", Teams block), null = go on</param>
        public McpScript(Func<string, JObject, Task<JObject>> call, Func<string?> halted, int seconds)
        {
            _call = call;
            _halted = halted;
            _cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(seconds, 5, MaxSeconds)));
        }

        /// <param name="byRequest">ferma_script: an outcome asked for, not an error</param>
        public void Stop(string why, bool byRequest = false)
        {
            lock (_lock) { if (!Done) AddLog("stopped: " + why); _stopRequested |= byRequest; _stopWhy ??= why; }
            try { _cts.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
        }

        /// <summary>starts the script on its own thread (the tools wait for the UI thread: never run it there)</summary>
        public McpScript Start(string code)
        {
            _sw.Start();
            Run = Task.Factory.StartNew(() => Execute(code), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            return this;
        }

        /// <summary>
        /// Lemora's own program, started again as the script process (see <see cref="ScriptChild"/>); null = run inside Lemora (tests, or an unknown host).
        /// Tests set it to the Lemora.exe next to them to try the real process
        /// </summary>
        internal static Func<string?> ChildExe = () =>
            Environment.ProcessPath is { } p && System.IO.Path.GetFileName(p).Equals("Lemora.exe", StringComparison.OrdinalIgnoreCase) ? p : null;

        private void Execute(string code)
        {
            try
            {
                if (ChildExe() is { } exe) ExecuteInChild(exe, code);
                else
                {
                    var engine = NewEngine(_cts.Token, CallTool, s => { lock (_lock) AddLog("log: " + OneLine(s, 500)); }, Pause, Attach, ToolNames());
                    var json = RunCode(engine, code);
                    lock (_lock) Result = json;
                }
            }
            catch (Exception e)
            {
                // cut: throw new Error('e'.repeat(1e7)) sent 10 million characters to Claude and to the activity log
                lock (_lock) Error = Cut(Describe(e), 2000);
            }
            finally
            {
                lock (_lock) { Done = true; _sw.Stop(); }
                _cts.Dispose();
            }
        }

        /// <summary>the engine with the limits and Lemora's functions; the same inside Lemora and in the script process</summary>
        internal static Engine NewEngine(CancellationToken ct, Func<string, string, string> call, Action<string> log, Action<double> pause, Action<int> attach, IEnumerable<string> tools)
        {
            // no Jint LimitMemory: it counts every byte ALLOCATED since the start (also by the tools running on this thread), not the memory in use,
            // so normal scripts stopped after a few hundred calls. HeapGrowth checks the memory really in use instead
            var engine = new Engine(o => o
                .Strict()
                .LimitRecursion(200)
                .MaxStatements(MaxStatements)
                // a catastrophic regular expression ends with its own message before secondi_max (with Jint's 10 s, secondi_max 10 said "maximum duration")
                .RegexTimeoutInterval(TimeSpan.FromSeconds(3))
                .MaxArraySize(MaxArrayItems)   // Jint checks the capacity, which grows by doubling: 100_000 refused a push of 70_000 (capacity 131_072)
                .Constraint(new HeapGrowth())
                .CancellationToken(ct));
            // no AllowClr: the script cannot reach .NET types, files, network or processes
            engine.SetValue("__call", call);
            engine.SetValue("__log", log);
            engine.SetValue("__pausa", pause);
            engine.SetValue("__allega", attach);
            engine.SetValue("__size", new Func<JsValue, double>(v => JsonSize(v, MaxStringChars)));
            engine.SetValue("__count", new Func<string, string, double>(Occurrences));
            engine.SetValue("__locale", new Func<string, double, string, string>(LocaleDate));
            engine.Execute(PreludeFor(tools), "lemora");   // named: errors raised inside it point to Claude's line, not to these
            return engine;
        }

        /// <summary>runs Claude's code: the returned value as JSON ("" = nothing returned), cut for the answer</summary>
        internal static string RunCode(Engine engine, string code)
        {
            var json = engine.Evaluate("JSON.stringify(__sync((function () {\n" + code + "\n})()))").ToString();
            return json == "undefined" ? "" : Cut(json, MaxResultChars);
        }

        private string Describe(Exception e) => e switch
        {
            OperationCanceledException or ExecutionCanceledException or ChildEnded => Stopped(e as ChildEnded),
            _ => EngineError(e),
        };

        /// <summary>why the script ended without finishing: Lemora's reasons first, then the script process' own end (memory)</summary>
        private string Stopped(ChildEnded? child) =>
            _halted() is { } why ? "stopped: " + why
            : _stopRequested ? "stopped with ferma_script"
            : _stopWhy != null ? "stopped: " + _stopWhy   // Stop on a session
            : _cts.IsCancellationRequested ? $"stopped: maximum duration reached (secondi_max): split the work or give a higher secondi_max (at most {MaxSeconds})"
            : child?.Message ?? "stopped";

        /// <summary>the script process ended without its result</summary>
        internal sealed class ChildEnded : Exception { public ChildEnded(string m) : base(m) { } }

        /// <summary>an error of the script itself, in words Claude can act on (also used by the script process)</summary>
        internal static string EngineError(Exception e) => e switch
        {
            ScriptFault f => f.Message,
            OutOfMemoryException or InsufficientExecutionStackException
                => $"stopped: the script used more memory than allowed ({ScriptChild.HeapLimitMb} MB): work on smaller pieces and keep only what you need",
            // thrown inside Lemora's functions (wrong argument): the position is the line of Claude's script that called them, from the stack
            JavaScriptException js when js.Location.SourceFile == "lemora" && StackPos.Match(js.JavaScriptStackTrace ?? "") is { Success: true } at
                => "script error: " + WithLine(Explain(AnonymousPos.Replace(js.Message, "")), int.Parse(at.Groups[1].Value), int.Parse(at.Groups[2].Value) - 1),
            JavaScriptException js => "script error: " + WithLine(Explain(js.Message), js.Location.Start.Line, js.Location.Start.Column),
            MemoryLimitExceededException m when m.Message.Contains("array size", StringComparison.OrdinalIgnoreCase)
                => $"stopped: an array would have more than {MaxArrayItems} elements: work on smaller pieces",
            StatementsCountOverflowException => $"stopped: more than {MaxStatements} statements (an endless loop, or a loop too big: work on smaller pieces)",
            RecursionDepthOverflowException => "stopped: recursion too deep",
            System.Text.RegularExpressions.RegexMatchTimeoutException => "stopped: a regular expression took too long on its text: simplify it (no nested repetitions like (a+)+)",
            _ when e.InnerException is System.Text.RegularExpressions.RegexMatchTimeoutException => EngineError(e.InnerException),
            _ when e.GetType().Name.Contains("Syntax") || e.GetType().Name.Contains("Parse") => "syntax error: " + WithLine(e.Message, 0, 0),
            _ => $"error: {e.GetType().Name}: {e.Message}",
        };

        /// <summary>Jint's messages that do not tell what to do</summary>
        private static string Explain(string? m) => m switch
        {
            null or "" or "undefined" => "the script threw something that is not an Error with a text: use throw new Error('what went wrong')",
            _ when m.Contains("Invalid string length", StringComparison.Ordinal) => m + ": a string grew too long, work on smaller pieces and keep only what you need",
            _ => m,
        };

        private static readonly System.Text.RegularExpressions.Regex AnonymousPos = new(@"\s*\(<anonymous>:(\d+):(\d+)\)");
        private static readonly System.Text.RegularExpressions.Regex StackPos = new(@"<anonymous>:(\d+):(\d+)");

        /// <summary>
        /// one position, counted in the script Claude wrote: the code runs inside a wrapper whose first line is not Claude's (Jint's own
        /// "(&lt;anonymous&gt;:2:9)" was one line off and appeared next to a second, different number)
        /// </summary>
        internal static string WithLine(string message, int line, int column)
        {
            var m = AnonymousPos.Match(message);
            if (m.Success) { line = int.Parse(m.Groups[1].Value); column = int.Parse(m.Groups[2].Value); message = AnonymousPos.Replace(message, ""); }
            if (line <= 0) return message;
            if (line == 1) return message + (message.Contains("yclic", StringComparison.Ordinal)
                ? " (while turning the returned value into JSON: return plain data, no objects that point to themselves)"
                : " (while turning the returned value into JSON)");
            return $"{message} (line {line - 1}, column {column + 1})";
        }

        private bool _stopRequested;
        private string? _stopWhy;

        /// <summary>
        /// stops the script when the memory really in use grows too much. Checked after every 16 MB allocated by the script's thread (a thread counter, almost free:
        /// every 256 statements was too rare, 50 statements of s += 'x'.repeat(1e7) reached 1 GB); when the limit seems crossed a full collection tells garbage
        /// from live data, and the next full collection waits for 64 MB more (a script that keeps 400 MB and makes garbage must not collect at every step).
        /// It cannot stop a single huge allocation midway: the functions that build one are capped in the prelude
        /// </summary>
        private sealed class HeapGrowth : Constraint
        {
            internal const long MaxGrowth = 512L * 1024 * 1024;
            private const long Step = 16L * 1024 * 1024;
            private long _start, _nextAlloc, _nextFull;
            // the base after a full collection: with GetTotalMemory(false) it held the garbage of the previous script, and the new one got that much
            // memory as a present (fourth external test: the same script stopped the first time and passed the second, up to 2.8 GB)
            public override void Reset() { _start = GC.GetTotalMemory(true); _nextAlloc = GC.GetAllocatedBytesForCurrentThread() + Step; _nextFull = 0; }
            public override void Check()
            {
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                if (allocated < _nextAlloc) return;
                _nextAlloc = allocated + Step;
                var total = GC.GetTotalMemory(false);
                if (total - _start < MaxGrowth || total < _nextFull) return;
                var live = GC.GetTotalMemory(true);
                if (live - _start >= MaxGrowth)
                    throw new ScriptFault("stopped: the script is holding too much data in memory (over 512 MB): work on smaller pieces and keep only what you need");
                _nextFull = live + 4 * Step;
            }
        }

        /// <summary>
        /// about how many characters JSON.stringify would produce (strings, keys, punctuation), stopping as soon as it passes limit: JSON.stringify builds its text
        /// in one step, so the check between statements came too late (100 strings of 1e7 took Lemora to 2 GB). Only data properties are read: no getter runs here
        /// </summary>
        internal static double JsonSize(JsValue v, long limit)
        {
            long n = 0;
            var seen = new HashSet<ObjectInstance>(ReferenceEqualityComparer.Instance);
            var todo = new Stack<JsValue>();
            todo.Push(v);
            while (todo.Count > 0 && n <= limit)
            {
                var x = todo.Pop();
                if (x is JsString s) n += s.Length + 2;
                else if (x is ObjectInstance o && x is not Jint.Native.Function.Function)
                {
                    if (!seen.Add(o)) continue;   // a cycle: JSON.stringify gives its own clear error
                    if (o is JsArray a)
                    {
                        var len = a.Length;
                        n += 2 + len;
                        for (uint i = 0; i < len && n <= limit; i++) todo.Push(a.Get(i));
                    }
                    else
                        foreach (var key in o.GetOwnPropertyKeys(Types.String))
                        {
                            n += key.ToString().Length + 4;
                            if (o.GetOwnProperty(key) is { } d && d.IsDataDescriptor()) todo.Push(d.Value);
                        }
                }
                else n += 8;
            }
            return n;
        }

        /// <summary>
        /// a date written the way of the language asked for (toLocaleDateString('de-DE')): Jint ignores the language and always wrote it the same way.
        /// "" = unknown language: the script falls back to Jint's own text
        /// </summary>
        internal static string LocaleDate(string kind, double ms, string locale)
        {
            try
            {
                var ci = System.Globalization.CultureInfo.GetCultureInfo(locale);
                var d = DateTimeOffset.FromUnixTimeMilliseconds((long)ms).ToLocalTime().DateTime;
                return d.ToString(kind == "date" ? "d" : kind == "time" ? "T" : "G", ci);
            }
            catch (Exception e) when (e is System.Globalization.CultureNotFoundException or ArgumentException) { return ""; }
        }

        /// <summary>how many times f appears in s (ordinal, not overlapping): the size of a replaceAll is known before building it</summary>
        internal static double Occurrences(string s, string f)
        {
            if (f.Length == 0) return s.Length + 1;
            var n = 0;
            for (var i = s.IndexOf(f, StringComparison.Ordinal); i >= 0; i = s.IndexOf(f, i + f.Length, StringComparison.Ordinal)) n++;
            return n;
        }

        /// <summary>ends the script with this text (Lemora's own stops and misuse of the functions)</summary>
        internal sealed class ScriptFault : Exception { public ScriptFault(string m) : base(m) { } }

        /// <summary>
        /// the script runs in a separate process (Lemora.exe started as <see cref="ScriptChild"/>) with a ceiling on its memory set by Windows: inside Lemora a
        /// single statement could take it to 3-4 GB before any check. The tools still run here: the process only asks for them, one line of JSON per message
        /// </summary>
        private void ExecuteInChild(string exe, string code)
        {
            var utf8 = new UTF8Encoding(false);
            var psi = new System.Diagnostics.ProcessStartInfo(exe, ScriptChild.Arg)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = utf8, StandardOutputEncoding = utf8, StandardErrorEncoding = utf8,
            };
            // the .NET heap of the process cannot grow past this: an allocation beyond it fails inside the script, with a clear message
            psi.Environment["DOTNET_GCHeapHardLimit"] = "0x" + ((long)ScriptChild.HeapLimitMb * 1024 * 1024).ToString("X");
            psi.Environment["DOTNET_gcServer"] = "0";
            using var job = ScriptChild.Job.Create(ScriptChild.ProcessLimitMb);
            using var p = System.Diagnostics.Process.Start(psi) ?? throw new ChildEnded("stopped: the script process did not start");
            job.Assign(p);
            Shawn.Utils.SimpleLogHelper.Warning($"MCP: script {Id} in its own process {p.Id} (memory at most {ScriptChild.HeapLimitMb} MB)");
            var err = new StringBuilder();
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) if (err.Length < 4000) err.AppendLine(e.Data); };
            p.BeginErrorReadLine();
            using var kill = _cts.Token.Register(() => { try { p.Kill(true); } catch { /* already ended */ } });
            p.StandardInput.WriteLine(new JObject { ["code"] = code, ["tools"] = new JArray(ToolNames().ToArray()) }.ToString(Formatting.None));
            p.StandardInput.Flush();
            while (p.StandardOutput.ReadLine() is { } line)
            {
                JObject m;
                try { m = JObject.Parse(line); } catch (JsonException) { continue; }   // not a message (a stray line from the runtime)
                switch (m.Value<string>("t"))
                {
                    case "call":
                        Reply(p, () => CallTool(m.Value<string>("name") ?? "", m.Value<string>("args") ?? ""));
                        break;
                    case "allega":
                        Reply(p, () => { Attach(m.Value<int>("h")); return ""; });
                        break;
                    case "log":
                        lock (_lock) AddLog("log: " + OneLine(m.Value<string>("text") ?? "", 500));
                        break;
                    case "end":
                        lock (_lock) { Result = m.Value<string>("result"); Error = m.Value<string>("error"); }
                        p.WaitForExit(5000);
                        return;
                }
            }
            p.WaitForExit(5000);
            string tail; lock (err) tail = err.ToString();
            if (!_cts.IsCancellationRequested)   // ended by Lemora (ferma_script, Stop, duration): expected, nothing to report
                Shawn.Utils.SimpleLogHelper.Warning($"MCP: script process ended without a result (exit {(p.HasExited ? p.ExitCode : -1)}): {OneLine(tail, 1000)}");
            throw new ChildEnded(tail.Contains("OutOfMemory", StringComparison.Ordinal) || job.HitMemoryLimit
                ? $"stopped: the script used more memory than allowed ({ScriptChild.HeapLimitMb} MB): work on smaller pieces and keep only what you need"
                : "stopped: the script process ended unexpectedly");
        }

        /// <summary>answers the script process: the result of the function, or the fault that ends the script there (with Claude's line)</summary>
        private void Reply(System.Diagnostics.Process p, Func<string> f)
        {
            JObject answer;
            try { answer = new JObject { ["r"] = f() }; }
            catch (ScriptFault e) { answer = new JObject { ["fault"] = e.Message }; }
            catch (OperationCanceledException) { answer = new JObject { ["fault"] = "stopped" }; }
            try { p.StandardInput.WriteLine(answer.ToString(Formatting.None)); p.StandardInput.Flush(); }
            catch (System.IO.IOException) { /* the process was ended meanwhile (ferma_script, Stop) */ }
        }

        private string CallTool(string name, string argsJson)
        {
            if (NotFromScripts.Contains(name)) throw new ScriptFault($"error: {name} cannot be called from a script");
            if (_halted() is { } why) throw new ScriptFault("stopped: " + why);
            _cts.Token.ThrowIfCancellationRequested();
            if (++Calls > MaxCalls) throw new ScriptFault($"stopped: more than {MaxCalls} tool calls");
            JObject args;
            try { args = string.IsNullOrWhiteSpace(argsJson) ? new JObject() : JObject.Parse(argsJson); }
            catch (JsonException) { Calls--; throw new ScriptFault($"error: {name} takes one object with its parameters, e.g. {name}({{sessione: s}})"); }
            lock (_lock) _counts[name] = _counts.GetValueOrDefault(name) + 1;
            var sw = Stopwatch.StartNew();
            // the wait ends with ferma_script, Stop or secondi_max even if the tool is still waiting (apri_sessione waited up to 60 s): the action
            // already started in Lemora may still complete, the script does not wait for it
            var task = _call(name, args);
            task.Wait(_cts.Token);
            var res = task.GetAwaiter().GetResult();
            var error = res.Value<bool?>("isError") == true;
            var text = new StringBuilder();
            var news = "";
            var handles = new JArray();
            foreach (var c in res["content"] as JArray ?? new JArray())
            {
                // Lemora's news about the sessions (paused, dropped...) were glued in front of the tool's text: a regex anchored at ^ failed.
                // They go to r.notizie and to the log Claude reads
                if (c.Value<string>("type") == "text" && c.Value<string>("text") is { } t && t.StartsWith(McpTools.NewsHeader, StringComparison.Ordinal)) news = t;
                else if (c.Value<string>("type") == "text") { if (text.Length > 0) text.Append('\n'); text.Append(c.Value<string>("text")); }
                else if (c.Value<string>("type") == "image")
                    lock (_lock)
                    {
                        var h = _nextImage++;
                        _images[h] = (JObject)c;
                        handles.Add(h);
                        foreach (var old in _images.Keys.Where(k => !_attached.Contains(k)).OrderBy(k => k).Take(Math.Max(0, _images.Count - KeptImages)).ToList()) _images.Remove(old);
                    }
            }
            lock (_lock)
            {
                if (news.Length > 0) AddLog(OneLine(news, 500));
                AddLog(FormattableString.Invariant($"{name}({ArgsForLog(args)}) {sw.Elapsed.TotalSeconds:0.0}s -> ") + $"{(error ? "ERROR " : "")}{OneLine(text.ToString(), 160)}");
            }
            return new JObject { ["ok"] = !error, ["testo"] = text.ToString(), ["immagini"] = handles, ["notizie"] = news }.ToString(Formatting.None);
        }

        /// <summary>argument values for the log: never credentials, long texts cut</summary>
        internal static string ArgsForLog(JObject a) =>
            string.Join(", ", a.Properties().Select(p => $"{p.Name}={(Secret.Contains(p.Name) ? "***" : OneLine(p.Value.ToString(Formatting.None), 40))}"));

        private void Pause(double ms)
        {
            var wait = TimeSpan.FromMilliseconds(Math.Clamp(ms, 0, 55_000));
            if (_cts.Token.WaitHandle.WaitOne(wait)) _cts.Token.ThrowIfCancellationRequested();
        }

        private void Attach(int handle)
        {
            lock (_lock)
            {
                if (!_images.ContainsKey(handle)) throw new ScriptFault($"error: allega({handle}): unknown or too old image (use a number from r.immagini of one of the last {KeptImages} images)");
                if (_attached.Count >= MaxImages && !_attached.Contains(handle)) throw new ScriptFault($"error: allega: at most {MaxImages} images per script");
                if (!_attached.Contains(handle)) _attached.Add(handle);
            }
        }

        private void AddLog(string line)   // under _lock
        {
            _log.Add(FormattableString.Invariant($"{_sw.Elapsed.TotalSeconds,6:0.0}s {line}"));
            if (_log.Count > MaxLogLines) { _log.RemoveAt(0); _droppedLog++; }
        }

        /// <summary>the answer for Claude: state, result or error, the log of the calls and the attached images</summary>
        public JObject Report(string? extra = null)
        {
            lock (_lock)
            {
                var stopped = Done && _stopRequested && Error == "stopped with ferma_script";
                var head = Done
                    ? FormattableString.Invariant($"script {Id}: {(Error == null ? "finished" : stopped ? "stopped as requested" : "ended with an error")} in {Elapsed.TotalSeconds:0.0} s, {Calls} tool calls")
                    : FormattableString.Invariant($"script {Id}: still running ({Elapsed.TotalSeconds:0.0} s, {Calls} tool calls so far): call stato_script id={Id} to follow it, ferma_script to stop it");
                var sb = new StringBuilder(head);
                if (extra != null) sb.Append('\n').Append(extra);
                if (Error != null) sb.Append("\n").Append(Error);
                if (Done && Error == null) sb.Append("\nresult: ").Append(Result is { Length: > 0 } r ? r : "(nothing returned: use return)");
                sb.Append("\nlog:");
                if (_droppedLog > 0) sb.Append($"\n({_droppedLog} older lines left out)");
                foreach (var l in _log) sb.Append('\n').Append(l);
                var content = new JArray(new JObject { ["type"] = "text", ["text"] = sb.ToString() });
                if (Done) foreach (var h in _attached) content.Add(_images[h]);
                return new JObject { ["content"] = content, ["isError"] = Done && Error != null && !stopped };
            }
        }

        private static string OneLine(string s, int max) => McpTools.OneLine(s, max);
        internal static string Cut(string s, int max) => s.Length > max ? s.Substring(0, max) + "… (cut)" : s;

        /// <summary>the tool functions the script sees, plus log/pausa/allega; built from the tool list so it always matches what Lemora can do</summary>
        internal static string PreludeFor(IEnumerable<string> tools)
        {
            var sb = new StringBuilder("'use strict';\n");
            // the scripts are synchronous: an async function returned {} without a word
            sb.Append("var __sync = function (r) { if (r instanceof Promise) throw new TypeError('scripts are synchronous: no async functions, await or Promise (the tools already wait by themselves)'); return r; };\n");
            // each value cut before joining: log of a 1e7 string failed with a message about join, which the script did not use
            sb.Append("var log = function () { __log(Array.prototype.map.call(arguments, function (x) { var t; " +
                      "try { t = typeof x === 'string' ? x : JSON.stringify(x); } catch (e) { t = '(not shown: ' + e.message + ')'; } " +
                      "return String(t).slice(0, 1000); }).join(' ')); };\n");
            sb.Append("var pausa = function (ms) { if (typeof ms !== 'number' || !isFinite(ms)) throw new TypeError('pausa wants the milliseconds as a number, e.g. pausa(2000)'); " +
                      "if (ms < 0) throw new RangeError('pausa wants 0 or more milliseconds, not ' + ms); __pausa(ms); };\n");
            sb.Append("var allega = function (r) { if (typeof r === 'number') { __allega(r); return; } if (!r || !Array.isArray(r.immagini)) throw new TypeError('allega wants the result of a tool, e.g. allega(schermata({sessione: s}))'); r.immagini.forEach(function (h) { __allega(h); }); };\n");
            // a single call can build a huge string before any check runs (repeat(5e8) took Lemora to 1 GB): these functions are capped
            sb.Append("(function () { var MAX = " + MaxStringChars + ";\n");
            sb.Append("  ['repeat', 'padStart', 'padEnd'].forEach(function (n) { var f = String.prototype[n];\n");
            sb.Append("    Object.defineProperty(String.prototype, n, { configurable: false, writable: false, value: function () {\n");
            sb.Append("      var len = n === 'repeat' ? this.length * Number(arguments[0]) : Number(arguments[0]);\n");
            sb.Append("      if (len > MAX) throw new RangeError(n + ': a string of ' + len + ' characters is too long (at most ' + MAX + ')');\n");
            sb.Append("      return f.apply(this, arguments); } }); });\n");
            // join and concat build their result in one step too (join of 1000 strings of 1e7 took Lemora to 2.2 GB): the length is added up first
            sb.Append("  var cap = function (n, len) { if (len > MAX) throw new RangeError(n + ': the result would be too long (more than ' + MAX + ' characters)'); };\n");
            sb.Append("  var join = Array.prototype.join; Object.defineProperty(Array.prototype, 'join', { configurable: false, writable: false, value: function (sep) {\n");
            sb.Append("    var s = sep === undefined ? 1 : String(sep).length, len = 0;\n");
            sb.Append("    for (var i = 0; i < this.length; i++) { var x = this[i]; len += (x === undefined || x === null ? 0 : String(x).length) + s; cap('join', len); }\n");
            sb.Append("    return join.apply(this, arguments); } });\n");
            // toLocaleString and String.raw join too (fourth external test: 3.7 GB): the same length check as join
            sb.Append("  var toLocale = Array.prototype.toLocaleString; Object.defineProperty(Array.prototype, 'toLocaleString', { configurable: false, writable: false, value: function () {\n");
            sb.Append("    var len = 0; for (var i = 0; i < this.length; i++) { var x = this[i]; len += (x === undefined || x === null ? 0 : String(x).length) + 1; cap('toLocaleString', len); }\n");
            sb.Append("    return toLocale.apply(this, arguments); } });\n");
            sb.Append("  var raw = String.raw; Object.defineProperty(String, 'raw', { configurable: false, writable: false, value: function (t) {\n");
            sb.Append("    var r = t && t.raw, len = 0; if (r) for (var i = 0; i < r.length; i++) { len += String(r[i]).length; cap('String.raw', len); }\n");
            sb.Append("    for (var j = 1; j < arguments.length; j++) { len += String(arguments[j]).length; cap('String.raw', len); }\n");
            sb.Append("    return raw.apply(String, arguments); } });\n");
            sb.Append("  var concat = String.prototype.concat; Object.defineProperty(String.prototype, 'concat', { configurable: false, writable: false, value: function () {\n");
            sb.Append("    var len = String(this).length; for (var i = 0; i < arguments.length; i++) { len += String(arguments[i]).length; cap('concat', len); }\n");
            sb.Append("    return concat.apply(this, arguments); } });\n");
            // JSON.stringify and replace/replaceAll build their text in one step too: the size is known (or checked) first
            sb.Append("  var stringify = JSON.stringify; Object.defineProperty(JSON, 'stringify', { configurable: false, writable: false, value: function (v) {\n");
            sb.Append("    if (__size(v) > MAX) throw new RangeError('JSON.stringify: the text would be too long (more than ' + MAX + ' characters): return or log less data');\n");
            sb.Append("    return stringify.apply(JSON, arguments); } });\n");
            sb.Append("  ['replace', 'replaceAll'].forEach(function (n) { var f = String.prototype[n];\n");
            sb.Append("    Object.defineProperty(String.prototype, n, { configurable: false, writable: false, value: function (search, repl) {\n");
            sb.Append("      var s = String(this);\n");
            sb.Append("      if (typeof search === 'string' && typeof repl === 'string' && repl.length > search.length)\n");
            sb.Append("        cap(n, s.length + (n === 'replaceAll' ? __count(s, search) : 1) * (repl.length - search.length));\n");
            sb.Append("      var r = f.apply(this, arguments); cap(n, r.length); return r; } }); });\n");
            // dates in the language asked for (only with a language and no options: the rest is Jint's own)
            sb.Append("  ['toLocaleDateString', 'toLocaleTimeString', 'toLocaleString'].forEach(function (n) { var f = Date.prototype[n];\n");
            sb.Append("    var kind = n === 'toLocaleDateString' ? 'date' : n === 'toLocaleTimeString' ? 'time' : 'all';\n");
            sb.Append("    Object.defineProperty(Date.prototype, n, { configurable: false, writable: false, value: function (loc, opt) {\n");
            sb.Append("      if (typeof loc === 'string' && opt === undefined && isFinite(this.getTime())) { var r = __locale(kind, this.getTime(), loc); if (r) return r; }\n");
            sb.Append("      return f.apply(this, arguments); } }); });\n");
            // typed arrays reserve their memory in one step (new Uint8Array(1e9) = 1 GB before any check) and are of no use to drive PCs: removed
            sb.Append("  ['ArrayBuffer', 'SharedArrayBuffer', 'DataView', 'Int8Array', 'Uint8Array', 'Uint8ClampedArray', 'Int16Array', 'Uint16Array', 'Int32Array', 'Uint32Array',\n");
            sb.Append("   'Float16Array', 'Float32Array', 'Float64Array', 'BigInt64Array', 'BigUint64Array'].forEach(function (n) { delete globalThis[n]; });\n");
            sb.Append("})();\n");
            foreach (var t in tools.Concat(NotFromScripts).Distinct())   // the script tools too: calling them gives a clear refusal, not "not defined"
                sb.Append($"var {t} = function (a) {{ if (a !== undefined && (a === null || typeof a !== 'object' || Array.isArray(a))) " +
                          $"throw new TypeError('{t} takes one object with its parameters, e.g. {t}({{sessione: s}})'); " +
                          $"return JSON.parse(__call('{t}', JSON.stringify(a || {{}}))); }};\n");
            return sb.ToString();
        }

        internal static Func<IEnumerable<string>> ToolNames = () => McpTools.BaseList().Select(t => t.Value<string>("name")!);
    }
}
