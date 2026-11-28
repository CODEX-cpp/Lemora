using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Lemora.Service.Mcp
{
    /// <summary>esegui_script / stato_script / ferma_script: Claude's JavaScript scripts over the other tools (McpScript)</summary>
    public static partial class McpTools
    {
        private const int FirstWaitSeconds = 45;   // Claude's client stops waiting after about a minute
        private static readonly object ScriptsLock = new();
        private static readonly List<McpScript> Scripts = new();   // the running one and the last finished ones

        /// <summary>the API of the scripts, generated from the tool list: name({param, optional?}) - first sentence of the description</summary>
        internal static string ScriptApi(JArray tools) => string.Join("\n", tools.Select(t =>
        {
            var schema = t["inputSchema"] as JObject;
            var required = (schema?["required"] as JArray)?.Select(r => r.ToString()).ToHashSet() ?? new HashSet<string>();
            var props = (schema?["properties"] as JObject)?.Properties().Select(p => required.Contains(p.Name) ? p.Name : p.Name + "?") ?? Enumerable.Empty<string>();
            return $"{t.Value<string>("name")}({{{string.Join(", ", props)}}}) - {FirstSentence(t.Value<string>("description") ?? "")}";
        }));

        /// <summary>up to the first ". " that ends a sentence: not the one of "e.g." or "i.e." (the list was cut at "(e.g.")</summary>
        internal static string FirstSentence(string s)
        {
            for (var i = s.IndexOf(". ", StringComparison.Ordinal); i > 0; i = s.IndexOf(". ", i + 1, StringComparison.Ordinal))
            {
                var before = s.Substring(Math.Max(0, i - 3), Math.Min(3, i));
                if (before.EndsWith("e.g", StringComparison.Ordinal) || before.EndsWith("i.e", StringComparison.Ordinal) || before.EndsWith("etc", StringComparison.Ordinal)) continue;
                return s.Substring(0, i + 1);
            }
            return s;
        }

        /// <summary>an integer argument also when Claude sends it as text ("30"); null if it is not a number (instead of a JSON-RPC -32603)</summary>
        private static int? IntArg(JObject a, string name, int fallback)
        {
            var v = a[name];
            if (v == null || v.Type == JTokenType.Null) return fallback;
            if (v.Type == JTokenType.Integer) return v.Value<int>();
            if (v.Type == JTokenType.Float) return (int)Math.Round(v.Value<double>());
            return int.TryParse(v.ToString().Trim(), out var n) ? n : null;
        }

        private const System.Text.RegularExpressions.RegexOptions Ic = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
        private const string SecretKeyWords = "password|passwd|pwd|pass|utente|user|username|dominio|domain|secret|segreto|token|apikey|api_key|chiave";
        /// <summary>a variable that by its name holds a secret (pw, pass1, secretKey, token...)</summary>
        private static readonly System.Text.RegularExpressions.Regex SecretVarName = new(@"^(?:pw|pwd|pass|psw)\d*$|passw|secret|segret|token|chiave|credenz", Ic);
        private static readonly System.Text.RegularExpressions.Regex DeclaredVar = new(@"\b(?:var|let|const)\s+([A-Za-z_$][\w$]*)");
        // password: k, o.password = k, password: cred[1] -> k and cred hold secrets (the third external test found them in clear)
        private static readonly System.Text.RegularExpressions.Regex VarAsSecretValue = new(@"(?<![\w$])['""]?(?:" + SecretKeyWords + @")['""]?\s*\]?\s*(?::|=(?![=>]))\s*([A-Za-z_$][\w$]*)", Ic);
        private static readonly System.Text.RegularExpressions.Regex ComputedKey = new(@"\]\s*:");
        private static readonly System.Text.RegularExpressions.Regex NumberValue = new(@"\G\s*-?(\d[\w.]*)");
        private static readonly System.Text.RegularExpressions.Regex AssignAt = new(@"\G\s*=(?![=>])");
        private static readonly System.Text.RegularExpressions.Regex TypingCall = new(@"\b(?:scrivi|incolla_testo)\s*\(");

        /// <summary>
        /// the script's code for Lemora's activity log, so the user sees what Claude meant to do and not only the single actions.
        /// Masked (the second external test found these in clear): the texts after a credential key (password: '...', a['password'] = "...",
        /// password: '..' + '..'), the values of the variables used as credentials or named like one (var pw = '...'), and everything Claude types with
        /// scrivi/incolla_testo (the single calls never log what is typed: it could be a password). One line; over 3000 characters the start and the end
        /// stay with how much was left out, and the list of the tools the code calls comes first, so a long script cannot hide what it does
        /// </summary>
        internal static string CodeForLog(string code)
        {
            code = WithoutComments(code);   // a comment can hold anything, a password too
            var masks = new List<(int start, int end)>();   // contents of string literals to replace with ***
            var secretVars = new HashSet<string>();
            foreach (System.Text.RegularExpressions.Match m in DeclaredVar.Matches(code)) if (SecretVarName.IsMatch(m.Groups[1].Value)) secretVars.Add(m.Groups[1].Value);
            foreach (System.Text.RegularExpressions.Match m in VarAsSecretValue.Matches(code)) secretVars.Add(m.Groups[1].Value);
            foreach (System.Text.RegularExpressions.Match m in TypingCall.Matches(code))
            {
                var end = Closing(code, m.Index + m.Length - 1);
                // only what is typed: sessione and the other parameters stay readable (all the literals of the call were masked)
                foreach (System.Text.RegularExpressions.Match v in new System.Text.RegularExpressions.Regex(@"\btesto['""]?\s*:").Matches(code.Substring(m.Index, end - m.Index)))
                {
                    var from = m.Index + v.Index + v.Length;
                    Literals(code, from, ValueEnd(code, from), masks);
                }
                foreach (System.Text.RegularExpressions.Match v in new System.Text.RegularExpressions.Regex(@"\btesto\s*:\s*([A-Za-z_$][\w$]*)\s*(?=[,}])").Matches(code.Substring(m.Index, end - m.Index)))
                    secretVars.Add(v.Groups[1].Value);
            }
            var keys = SecretKeyWords + (secretVars.Count > 0 ? "|" + string.Join("|", secretVars.Select(System.Text.RegularExpressions.Regex.Escape)) : "");
            // key, then : or = (not ==, =>): object property, assignment, a['key'] = ...
            var keyRx = new System.Text.RegularExpressions.Regex(@"(?<![\w$])['""]?(?:" + keys + @")['""]?\s*\]?\s*(?::|=(?![=>]))", Ic);
            foreach (System.Text.RegularExpressions.Match m in keyRx.Matches(code))
            {
                Literals(code, m.Index + m.Length, ValueEnd(code, m.Index + m.Length), masks);
                // a password written as a number (password: 12345678)
                if (NumberValue.Match(code, m.Index + m.Length) is { Success: true } num) masks.Add((num.Groups[1].Index, num.Groups[1].Index + num.Groups[1].Length));
            }
            // o[<key built at run time>] = '...': the name is not known before running (o[['pa','ss','wo','rd'].join('')] = ...), the value is masked
            for (var i = code.IndexOf('['); i > 0; i = code.IndexOf('[', i + 1))
            {
                var b = i - 1;
                while (b >= 0 && char.IsWhiteSpace(code[b])) b--;
                if (b < 0 || !(char.IsLetterOrDigit(code[b]) || code[b] is '_' or '$' or ']' or ')')) continue;   // not o[...]
                var close = Closing(code, i);
                var inside = code.Substring(i, close - i);
                if (inside.IndexOfAny(new[] { '\'', '"', '`', '(' }) < 0) continue;   // a[0], a[i]: an ordinary index
                if (AssignAt.Match(code, close) is { Success: true } eq) Literals(code, close + eq.Length, ValueEnd(code, close + eq.Length), masks);
            }
            // computed keys ({['pass' + 'word']: '...'}): the name is not known before running, so their values are always masked
            foreach (System.Text.RegularExpressions.Match m in ComputedKey.Matches(code)) Literals(code, m.Index + m.Length, ValueEnd(code, m.Index + m.Length), masks);

            var sb = new System.Text.StringBuilder();
            var at = 0;
            foreach (var (s, e) in masks.Distinct().OrderBy(x => x.start))
            {
                if (s < at) continue;
                sb.Append(code, at, s - at).Append("***");
                at = e;
            }
            sb.Append(code, at, code.Length - at);
            // line separator in plain ASCII: "⏎" became "?" in Lemora's log file
            var line = OneLine(string.Join(" <nl> ", sb.ToString().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)), int.MaxValue);
            if (line.Length > 3000) line = line.Substring(0, 2000) + $" … [{line.Length - 2800} characters left out] … " + line.Substring(line.Length - 800);
            var calls = McpScript.ToolNames().Where(t => new System.Text.RegularExpressions.Regex(@"\b" + t + @"\s*\(").IsMatch(code)).ToList();
            return (calls.Count > 0 ? "[calls " + string.Join(", ", calls) + "] " : "") + line;
        }

        /// <summary>the code without // and /* */ comments (string literals kept as they are)</summary>
        internal static string WithoutComments(string code)
        {
            var sb = new System.Text.StringBuilder(code.Length);
            for (var i = 0; i < code.Length; i++)
            {
                var c = code[i];
                if (c is '\'' or '"' or '`') { var end = SkipLiteral(code, i); sb.Append(code, i, end - i); i = end - 1; continue; }
                if (c == '/' && i + 1 < code.Length && code[i + 1] == '/') { while (i < code.Length && code[i] != '\n') i++; i--; continue; }
                if (c == '/' && i + 1 < code.Length && code[i + 1] == '*')
                {
                    var close = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    sb.Append(' ');
                    i = close < 0 ? code.Length : close + 1;
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>index after the closing quote of the literal that starts at i (its quote character)</summary>
        private static int SkipLiteral(string code, int i)
        {
            var q = code[i];
            for (var j = i + 1; j < code.Length; j++)
            {
                if (code[j] == '\\') { j++; continue; }
                if (code[j] == q) return j + 1;
                if (q != '`' && code[j] == '\n') return j;   // unterminated: stops at the end of the line
            }
            return code.Length;
        }

        /// <summary>index of the bracket that closes the one at open (literals skipped); the end of the code if missing</summary>
        private static int Closing(string code, int open)
        {
            var depth = 0;
            for (var i = open; i < code.Length; i++)
            {
                var c = code[i];
                if (c is '\'' or '"' or '`') { i = SkipLiteral(code, i) - 1; continue; }
                if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']' or '}' && --depth == 0) return i + 1;
            }
            return code.Length;
        }

        /// <summary>where the value that starts at i ends: a , ; or line break outside brackets, or the bracket that closes the enclosing one</summary>
        private static int ValueEnd(string code, int i)
        {
            var depth = 0;
            for (; i < code.Length; i++)
            {
                var c = code[i];
                if (c is '\'' or '"' or '`') { i = SkipLiteral(code, i) - 1; continue; }
                if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']' or '}') { if (depth-- == 0) return i; }
                else if (depth == 0 && c is ',' or ';' or '\n') return i;
            }
            return code.Length;
        }

        /// <summary>the contents (between the quotes) of every string literal in [from, to)</summary>
        private static void Literals(string code, int from, int to, List<(int, int)> masks)
        {
            for (var i = from; i < to && i < code.Length; i++)
            {
                if (code[i] is not ('\'' or '"' or '`')) continue;
                var end = SkipLiteral(code, i);
                var close = end > i + 1 && end <= code.Length && code[end - 1] == code[i] ? end - 1 : end;
                masks.Add((i + 1, close));
                i = end - 1;
            }
        }

        private static IEnumerable<JObject> ScriptTools(JArray tools)
        {
            yield return Tool("esegui_script",
                "Runs a JavaScript script (ES2020, synchronous) that calls Lemora's tools as functions, with loops and conditions: use it for repetitive or predictable sequences " +
                "instead of many single calls. Every tool takes one object with the same parameters and returns {ok, testo, immagini}: ok=false is the error a normal call would give " +
                "(the script goes on: check r.ok); r.notizie holds Lemora's news about the sessions (paused, dropped...), when there are some. Images cannot be seen by the script: allega(r) attaches them to the answer (at most 4). Also log(...), pausa(ms) and return <value> " +
                "(the result, as JSON). The same rules apply as for single calls: approvals, \"Stop Claude\", excluded devices; no access to files, network or programs of this PC. " +
                "It answers within ~45 s; if the script is still running it gives its id: follow it with stato_script. Limits: " + McpScript.MaxCalls + " tool calls, secondi_max, 512 MB of data, " +
                McpScript.MaxArrayItems + " elements per array, " + McpScript.MaxStatements + " statements, " + McpScript.MaxStringChars + " characters per string built with repeat/join/concat/padStart/padEnd, no typed arrays. " +
                "If the user presses Stop on a session or \"Stop Claude\", the script ends. " +
                "r.testo is the same free text a single call returns: parse it with regular expressions. E.g. apri_sessione and apri_indirizzo answer \"session: <id>\" (/session: *(\\S+)/); " +
                "cerca and elenca_connessioni give one row per connection, \"<id> | <name> | <protocol address> | <folder> | credentials ...\", after a header row that starts with \"id |\" (skip it); " +
                "leggi_schermo gives \"x,y,width,height | text\" rows. Try a single call first if you are not sure of a format. " +
                "Example: var r = clic({sessione: s, testo: 'OK'}); if (!r.ok) return r.testo; attendi({sessione: s, testo: 'Done', secondi: 30}); return leggi_schermo({sessione: s}).testo;\n" +
                "Functions:\n" + ScriptApi(tools),
                Props(("codice", "string", "the script body (it runs inside a function: use return for the result)"),
                      ("secondi_max", "integer", $"maximum duration in seconds (default 300, at most {McpScript.MaxSeconds})")), "codice");
            yield return Tool("stato_script", "State of a script started with esegui_script: waits up to attendi seconds for it to finish, then gives result, error and the log of its calls.",
                Props(("id", "string", "script id"), ("attendi", "integer", "seconds to wait for the end (default 45, at most 55; 0 = answer now)")), "id");
            yield return Tool("ferma_script", "Stops a running script at once (an action already started in Lemora, e.g. a connection being opened, may still complete).", Props(("id", "string", "script id")), "id");
        }

        private static async Task<JObject> RunScript(string code, int seconds)
        {
            if (string.IsNullOrWhiteSpace(code)) return Fail("codice is empty: write the script body");
            McpScript script;
            lock (ScriptsLock)
            {
                if (Scripts.FirstOrDefault(s => !s.Done) is { } running)
                    return Fail($"script {running.Id} is still running: follow it with stato_script or stop it with ferma_script");
                script = new McpScript(Call, () => TeamsClaudePolicy.Current().BlocksAll ? TeamsClaudePolicy.BlockedAllReason : McpGuard.IsStopped ? McpGuard.StoppedReason : null, seconds);
                Scripts.Add(script);
                while (Scripts.Count > 10) Scripts.RemoveAt(0);
            }
            McpGuard.Log($"script {script.Id} started ({code.Split('\n').Length} lines): {CodeForLog(code)}");
            script.Start(code);
            await Task.WhenAny(script.Run, Task.Delay(TimeSpan.FromSeconds(FirstWaitSeconds)));
            if (script.Done) LogEnd(script);
            return script.Report();
        }

        private static void LogEnd(McpScript script)
            => McpGuard.Log($"script {script.Id} {(script.Error == null ? "finished" : "ended: " + OneLine(script.Error, 500))} ({script.Calls} calls{(script.Calls > 0 ? ": " + script.CallSummary : "")})");

        private static McpScript? FindScript(string id) { lock (ScriptsLock) return Scripts.FirstOrDefault(s => s.Id == id.Trim()); }

        private static async Task<JObject> ScriptState(string id, int wait)
        {
            if (FindScript(id) is not { } script) return Fail("no script with this id (only the last 10 are kept)");
            var wasDone = script.Done;
            if (!wasDone) await Task.WhenAny(script.Run, Task.Delay(TimeSpan.FromSeconds(Math.Clamp(wait, 0, 55))));
            if (!wasDone && script.Done) LogEnd(script);
            return script.Report();
        }

        private static JObject StopScript(string id)
        {
            if (FindScript(id) is not { } script) return Fail("no script with this id");
            if (script.Done) return script.Report("(it had already ended)");
            script.Stop("ferma_script", byRequest: true);
            McpGuard.Log($"script {script.Id} stopped by Claude");
            script.Run.Wait(TimeSpan.FromSeconds(3));   // the script process is ended at once: the answer already says how it ended
            if (script.Done) LogEnd(script);
            return script.Report(script.Done ? null : "stop requested: it is ending");
        }

        /// <summary>"Stop Claude" pressed: also the running script stops at once (its next call would be refused anyway)</summary>
        internal static void StopScripts(string why)
        {
            lock (ScriptsLock) foreach (var s in Scripts.Where(s => !s.Done)) s.Stop(why);
        }
    }
}
