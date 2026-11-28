using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lemora.Service.Mcp;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Tests.Service
{
    /// <summary>esegui_script: Claude's scripts call the tools through the same executor, and cannot get out of the sandbox</summary>
    [Collection("ProcessMemory")]   // the memory limit is measured on the whole process: tests that allocate or measure a lot must not run at the same time
    public class McpScriptTests
    {
        private readonly List<(string name, JObject args)> _calls = new();
        private string? _halt;

        public McpScriptTests() => McpScript.ToolNames = () => new[] { "clic", "leggi_schermo", "schermata", "apri_sessione", "esegui_script" };

        private Task<JObject> Fake(string name, JObject a)
        {
            lock (_calls) _calls.Add((name, a));
            var content = new JArray(new JObject { ["type"] = "text", ["text"] = name == "leggi_schermo" ? "Done" : $"{name} ok" });
            if (name == "schermata") content.Add(new JObject { ["type"] = "image", ["data"] = "AAAA", ["mimeType"] = "image/png" });
            return Task.FromResult(new JObject { ["content"] = content, ["isError"] = name == "clic" && a.Value<string>("testo") == "missing" });
        }

        private McpScript Run(string code, int seconds = 30)
        {
            var s = new McpScript(Fake, () => _halt, seconds).Start(code);
            Assert.True(s.Run.Wait(TimeSpan.FromSeconds(20)), "the script did not end");
            return s;
        }

        private static string Text(JObject report) => report["content"]![0]!.Value<string>("text")!;

        [Fact]
        public void Script_CallsToolsInOrder_AndReturnsItsResult()
        {
            var s = Run("for (var i = 0; i < 3; i++) clic({sessione: 'a', x: i, y: 1}); var r = leggi_schermo({sessione: 'a'}); return {letto: r.testo, ok: r.ok};");
            Assert.Null(s.Error);
            Assert.Equal(new[] { "clic", "clic", "clic", "leggi_schermo" }, _calls.Select(c => c.name));
            Assert.Equal(2, _calls[2].args.Value<int>("x"));
            Assert.Equal("{\"letto\":\"Done\",\"ok\":true}", s.Result);
            Assert.Equal(4, s.Calls);
        }

        [Fact]
        public void ToolError_IsAValue_TheScriptGoesOn()
        {
            var s = Run("var r = clic({sessione: 'a', testo: 'missing'}); if (!r.ok) clic({sessione: 'a', testo: 'Other'}); return r.ok;");
            Assert.Null(s.Error);
            Assert.Equal("false", s.Result);
            Assert.Equal(2, _calls.Count);
        }

        [Fact]
        public void NoAccessToDotNet_FilesOrNetwork()
        {
            foreach (var code in new[] { "return typeof System;", "return typeof importNamespace;", "return typeof require;", "return typeof fetch;", "return typeof XMLHttpRequest;", "return typeof clr;" })
                Assert.Equal("\"undefined\"", Run(code).Result);
            Assert.NotNull(Run("return System.IO.File.ReadAllText('c:/windows/win.ini');").Error);
        }

        [Fact]
        public void EndlessLoop_IsStopped()
        {
            var s = Run("while (true) {}", seconds: 5);
            Assert.NotNull(s.Error);
            Assert.StartsWith("stopped", s.Error);
        }

        [Fact]
        public void ScriptToolsCannotBeCalledFromAScript()
        {
            var s = Run("return esegui_script({codice: 'return 1'});");
            Assert.Contains("cannot be called from a script", s.Error);
            Assert.Empty(_calls);
        }

        [Fact]
        public void StopClaude_EndsTheScript_AtTheNextCall()
        {
            _halt = "the user pressed Stop Claude";
            var s2 = Run("clic({sessione: 'a', x: 1, y: 1}); return 'not here';");
            Assert.Equal("stopped: the user pressed Stop Claude", s2.Error);
            Assert.Empty(_calls);
        }

        [Fact]
        public void FermaScript_StopsAWaitingScript()
        {
            var s = new McpScript(Fake, () => null, 60).Start("pausa(50000); return 'not here';");
            System.Threading.Thread.Sleep(300);
            s.Stop("ferma_script");
            Assert.True(s.Run.Wait(TimeSpan.FromSeconds(5)));
            Assert.StartsWith("stopped", s.Error);
        }

        [Fact]
        public void Log_NeverShowsCredentials()
        {
            var s = Run("apri_sessione({id: 'x', utente: 'admin', password: 'S3greta!', dominio: 'CORP'}); return 1;");
            var text = Text(s.Report());
            Assert.DoesNotContain("S3greta!", text);
            Assert.DoesNotContain("admin", text);
            Assert.Contains("password=***", text);
            Assert.Equal("S3greta!", _calls[0].args.Value<string>("password"));   // the tool still gets it
        }

        [Fact]
        public void Images_OnlyTheAttachedOnes_AreInTheReport()
        {
            var s = Run("var a = schermata({sessione: 'a'}); var b = schermata({sessione: 'a'}); allega(b); return a.immagini.length;");
            Assert.Null(s.Error);
            var content = (JArray)s.Report()["content"]!;
            Assert.Equal(2, content.Count);   // text + one image
            Assert.Equal("image", content[1].Value<string>("type"));
        }

        [Fact]
        public void SyntaxError_IsReported()
        {
            var s = Run("var x = ;");
            Assert.NotNull(s.Error);
            Assert.True(s.Report().Value<bool>("isError"));
        }

        // ---------------------------------------------------------------- fixes after the external test

        [Fact]
        public void Memory_IsTheDataInUse_NotEverythingEverAllocated()
        {
            // with Jint's LimitMemory these stopped at once ("too much memory") although almost nothing is kept
            var s = Run("for (var i = 0; i < 200000; i++) JSON.parse(JSON.stringify({a: i, b: 'text ' + i})); for (var j = 0; j < 1000; j++) leggi_schermo({sessione: 'a'}); return 'ok';", seconds: 60);
            Assert.Null(s.Error);
            Assert.Equal(1000, s.Calls);
        }

        [Fact]
        public void HugeStrings_AreRefusedBeforeTheyAreBuilt()
        {
            var s = Run("return 'x'.repeat(5e8).length;");
            Assert.Contains("too long", s.Error);
            Assert.Equal("10", Run("return 'ab'.repeat(5).length;").Result);   // normal use still works
        }

        [Fact]
        public void WrongArguments_GiveAClearMessage_WithClaudesLine()
        {
            var s = Run("var a = 1;\nclic('ciao');");
            Assert.Contains("clic takes one object", s.Error);
            Assert.Contains("(line 2,", s.Error);
            Assert.Empty(_calls);   // not a call
            Assert.Contains("pausa wants the milliseconds", Run("pausa('due secondi');").Error);
        }

        [Fact]
        public void SyntaxError_HasOnePosition_CountedInTheScript()
        {
            var e = Run("var a = 1;\nvar x = ;").Error!;
            Assert.Contains("(line 2,", e);
            Assert.DoesNotContain("<anonymous>", e);
        }

        [Fact]
        public void CircularResult_IsExplained()
        {
            Assert.Contains("returned value into JSON", Run("var o = {}; o.o = o; return o;").Error);
        }

        [Fact]
        public void FermaScript_IsNotReportedAsAnError()
        {
            var s = new McpScript(Fake, () => null, 60).Start("pausa(50000); return 'not here';");
            System.Threading.Thread.Sleep(300);
            s.Stop("ferma_script", byRequest: true);
            Assert.True(s.Run.Wait(TimeSpan.FromSeconds(5)));
            var report = s.Report();
            Assert.False(report.Value<bool>("isError"));
            Assert.Contains("stopped as requested", Text(report));
        }

        [Fact]
        public void CodeForLog_MasksLiteralCredentials()
        {
            var log = McpTools.CodeForLog("apri_sessione({id: 'x', utente: 'admin', password: \"S3gr'eta\", dominio: `CORP`});\nclic({sessione: s, testo: 'OK'});");
            Assert.DoesNotContain("admin", log);
            Assert.DoesNotContain("S3gr", log);
            Assert.DoesNotContain("CORP", log);
            Assert.Contains("testo: 'OK'", log);
            Assert.Contains(" <nl> ", log);
        }

        [Fact]
        public void FirstSentence_SkipsEg()
        {
            Assert.Equal("Lists the folders (e.g. customers and sites) with counts.", McpTools.FirstSentence("Lists the folders (e.g. customers and sites) with counts. More."));
            Assert.Equal("No dot here", McpTools.FirstSentence("No dot here"));
        }

        // ---------------------------------------------------------------- fixes after the second external test

        [Fact]
        public void HostForms_TheSamePc_InEveryWriting()
        {
            foreach (var a in new[] { "localhost", "LOCALHOST.", "127.0.0.1", "127.0.0.2", "::1", "[::1]", "2130706433", "0x7f000001", "127.1", "0.0.0.0", Environment.MachineName })
                Assert.Contains("loopback", McpGuard.HostForms(a));
            Assert.Equal(new[] { "10.1.2.3" }, McpGuard.HostForms("167838211"));
            Assert.Equal(new[] { "pc39.acme.local", "pc39" }, McpGuard.HostForms(" PC39.Acme.Local. "));
        }

        [Fact]
        public void AddrKeys_LocalhostAndItsIp_AreOnePc()
        {
            var byIp = new Lemora.Model.Protocol.RDP { Address = "127.0.0.1", Port = "3389" };
            foreach (var other in new[] { "localhost", "127.0.0.2", "::1" })
                Assert.True(McpGuard.AddrKeys(byIp).Overlaps(McpGuard.AddrKeys(new Lemora.Model.Protocol.VNC { Address = other, Port = "5900" })), other);
            Assert.False(McpGuard.AddrKeys(byIp).Overlaps(McpGuard.AddrKeys(new Lemora.Model.Protocol.RDP { Address = "10.0.0.5", Port = "3389" })));
        }

        [Fact]
        public void SessionStop_EndsTheScript_WithTheReason()
        {
            var s = new McpScript(Fake, () => null, 60).Start("pausa(50000); return 'not here';");
            System.Threading.Thread.Sleep(300);
            s.Stop(McpGuard.ScriptStoppedBySessionStop);
            Assert.True(s.Run.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal("stopped: " + McpGuard.ScriptStoppedBySessionStop, s.Error);
        }

        [Fact]
        public void Memory_ConcatenatedStrings_AreStoppedNearTheLimit()
        {
            var s = Run("var x = 'x'.repeat(1e7), s = ''; for (var i = 0; i < 60; i++) s += x; return s.length;", seconds: 60);
            Assert.True(s.Error?.Contains("512 MB") == true, "concat: " + (s.Error ?? s.Result));
            var j = Run("var x = 'x'.repeat(1e7), a = []; for (var i = 0; i < 1000; i++) a.push(x); return a.join('').length;");
            Assert.True(j.Error?.Contains("too long") == true, "join: " + (j.Error ?? j.Result));
            var c = Run("var x = 'x'.repeat(6e6); return x.concat(x).length;");
            Assert.True(c.Error?.Contains("too long") == true, "concat(): " + (c.Error ?? c.Result));
            Assert.Equal("\"undefined\"", Run("return typeof Uint8Array + (typeof ArrayBuffer === 'undefined' ? '' : 'x');").Result);
        }

        [Fact]
        public void Arrays_UpToAMillion_WithAClearMessageAbove()
        {
            Assert.Equal("70000", Run("var a = []; for (var i = 0; i < 70000; i++) a.push(i); return a.length;").Result);
            var e = Run("var a = []; for (var i = 0; i < 1100000; i++) a.push(i); return a.length;", seconds: 60).Error!;
            Assert.Contains("more than " + McpScript.MaxArrayItems + " elements", e);
            Assert.DoesNotContain("Exception", e);
        }

        [Fact]
        public void Pausa_Negative_SaysSo()
        {
            Assert.Contains("0 or more milliseconds", Run("pausa(-100);").Error);
        }

        [Fact]
        public void CodeForLog_MasksVariablesBracketsConcatenationsAndTypedText()
        {
            var log = McpTools.CodeForLog(
                "var pw = 'SegretoVar11';\nvar cred = {};\ncred['password'] = 'SegretoQuadre33';\nvar o = {password: 'Primo' + 'Concat55', sessione: 'a'};\n" +
                "apri_sessione({id: 'x', password: pw});\nscrivi({sessione: s, testo: 'AltroSegreto99'});\nvar t = 'TestoInVar77'; incolla_testo({sessione: s, testo: t});\nclic({sessione: s, testo: 'OK'});");
            foreach (var secret in new[] { "SegretoVar11", "SegretoQuadre33", "Concat55", "Primo", "AltroSegreto99", "TestoInVar77" })
                Assert.DoesNotContain(secret, log);
            Assert.Contains("testo: 'OK'", log);
            Assert.Contains("sessione: 'a'", log);
            Assert.StartsWith("[calls clic, apri_sessione] ", log);
        }

        [Fact]
        public void CodeForLog_LongCode_KeepsTheEndAndSaysWhatWasLeftOut()
        {
            var code = string.Concat(Enumerable.Repeat("var x = 1;\n", 3000)) + "apri_sessione({id: 'fine'});";
            var log = McpTools.CodeForLog(code);
            Assert.Contains("characters left out", log);
            Assert.Contains("apri_sessione({id: 'fine'})", log);
            Assert.Contains("[calls apri_sessione]", log);
            Assert.True(log.Length < 3200);
        }

        [Fact]
        public void FreeRdp_Ipv6_GoesInBrackets()
        {
            Assert.Contains("/v:[::1]:3389", Lemora.Model.ProtocolRunner.FreeRdpRunner.BuildArguments(new Lemora.Model.Protocol.RDP { Address = "::1", Port = "3389", DisplayName = "x" }, false, ""));
            Assert.Contains("/v:10.0.0.5:3389", Lemora.Model.ProtocolRunner.FreeRdpRunner.BuildArguments(new Lemora.Model.Protocol.RDP { Address = "10.0.0.5", Port = "3389", DisplayName = "x" }, false, ""));
        }

        // ---------------------------------------------------------------- fixes after the third external test

        [Fact]
        public void StringifyAndReplace_TooLong_AreRefusedBeforeBuilding()
        {
            var big = "var b = 'x'.repeat(1e7), a = []; for (var i = 0; i < 100; i++) a.push(b); ";
            Assert.Contains("JSON.stringify: the text would be too long", Run(big + "return JSON.stringify(a).length;").Error);
            var result = Run(big + "return a;").Error!;
            Assert.Contains("too long", result);
            Assert.DoesNotContain("point to themselves", result);   // a size problem, not a cycle
            Assert.Contains("too long", Run("return 'x'.repeat(1e7).replaceAll('x', 'xxxxxxxxxx').length;").Error);
            Assert.Contains("too long", Run("return 'x'.repeat(1e7).replace(/x/g, 'xxxxxxxxxx').length;").Error);
            Assert.Equal("\"aXc|bbbbbb|{\\\"a\\\":[1,\\\"b\\\"]}\"", Run("return 'abc'.replace('b', 'X') + '|' + 'aaa'.replace(/a/g, 'bb') + '|' + JSON.stringify({a: [1, 'b']});").Result);
        }

        [Fact]
        public void Log_OfAHugeString_IsCutNotAnError()
        {
            var s = Run("log('x'.repeat(1e7)); return 'ok';");
            Assert.Null(s.Error);
            Assert.Equal("\"ok\"", s.Result);
        }

        [Fact]
        public void CallSummary_CountsTheRealCalls_EvenWithBuiltNames()
        {
            var s = Run("globalThis['cl' + 'ic']({sessione: 'a', x: 1, y: 1}); clic({sessione: 'a', x: 2, y: 2}); leggi_schermo({sessione: 'a'}); return 1;");
            Assert.Equal("clic x2, leggi_schermo x1", s.CallSummary);
        }

        [Fact]
        public void CodeForLog_MasksCopiesArraysComputedKeysAndComments()
        {
            var log = McpTools.CodeForLog(
                "var k = 'PwAltro12'; var o = {}; o.password = k;\nvar cred = ['admin', 'PwArray13'];\napri_sessione({id: 'x', password: cred[1]});\n" +
                "var o2 = {['pass' + 'word']: 'PwCalc16'};\n// la password e' PwCommento15\n/* PwBlocco17 */ clic({sessione: 's', testo: 'OK'});");
            foreach (var secret in new[] { "PwAltro12", "PwArray13", "PwCalc16", "PwCommento15", "PwBlocco17" })
                Assert.DoesNotContain(secret, log);
            Assert.Contains("testo: 'OK'", log);
            Assert.Contains("id: 'x'", log);
        }

        [Fact]
        public void FreeRdp_BracketedIpv6_IsNotBracketedTwice()
        {
            Assert.Contains("/v:[::1]:3389", Lemora.Model.ProtocolRunner.FreeRdpRunner.BuildArguments(new Lemora.Model.Protocol.RDP { Address = "[::1]", Port = "3389", DisplayName = "x" }, false, ""));
        }

        // ---------------------------------------------------------------- fixes after the fourth external test

        [Fact]
        public void Memory_TheSameScriptTwice_IsStoppedBothTimes()
        {
            const string code = "var x = 'x'.repeat(1e7), s = ''; for (var i = 0; i < 60; i++) s += x; return s.length;";
            Assert.Contains("512 MB", Run(code, seconds: 60).Error);
            Assert.Contains("512 MB", Run(code, seconds: 60).Error);   // the garbage of the first one is not a present to the second
        }

        [Fact]
        public void ToLocaleStringAndStringRaw_TooLong_AreRefused()
        {
            const string big = "var b = 'x'.repeat(1e7), a = []; for (var i = 0; i < 100; i++) a.push(b); ";
            Assert.Contains("toLocaleString: the result would be too long", Run(big + "return a.toLocaleString().length;").Error);
            Assert.Contains("String.raw: the result would be too long", Run(big + "return String.raw({raw: a}).length;").Error);
            Assert.Equal("\"1,2|a1b\"", Run("return [1, 2].toLocaleString() + '|' + String.raw`a${1}b`;").Result);
        }

        [Fact]
        public void ErrorText_IsCut_AndOddThrowsAreExplained()
        {
            var e = Run("throw new Error('e'.repeat(1e7));").Error!;
            Assert.True(e.Length < 2100, e.Length.ToString());
            Assert.Contains("not an Error", Run("throw {};").Error);
            Assert.Contains("synchronous", Run("async function f() { return 1; } return f();").Error);
        }

        [Fact]
        public void LemorasNews_GoToNotizie_NotInFrontOfTesto()
        {
            Task<JObject> WithNews(string name, JObject a) => Task.FromResult(new JObject
            {
                ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = McpTools.NewsHeader + "\r\n- paused" }, new JObject { ["type"] = "text", ["text"] = "done" }),
            });
            var s = new McpScript(WithNews, () => null, 30).Start("var r = clic({sessione: 'a', x: 1, y: 1}); return r.testo + '|' + (r.notizie.indexOf('paused') > 0);");
            Assert.True(s.Run.Wait(TimeSpan.FromSeconds(20)));
            Assert.Equal("\"done|true\"", s.Result);
            Assert.Contains("paused", Text(s.Report()));   // Claude still reads them in the log
        }

        [Fact]
        public void CodeForLog_BuiltKeysAndNumbers_Masked_OtherParametersReadable()
        {
            var log = McpTools.CodeForLog("var o = {}; o[['pa', 'ss', 'wo', 'rd'].join('')] = 'PwCiclo21';\napri_sessione({id: 'x', password: 12345678});\nscrivi({sessione: 'sess1', testo: 'Digitato9'});\nvar a = [1]; a[0] = 'visibile';");
            foreach (var secret in new[] { "PwCiclo21", "12345678", "Digitato9" }) Assert.DoesNotContain(secret, log);
            Assert.Contains("sessione: 'sess1'", log);
            Assert.Contains("a[0] = 'visibile'", log);
        }

        [Fact]
        public void Dates_InTheLanguageAskedFor()
        {
            var s = Run("var d = new Date(2026, 0, 1); return d.toLocaleDateString('de-DE') + '|' + d.toLocaleDateString('it-IT');");
            Assert.Equal("no error", s.Error ?? "no error");
            Assert.Equal("\"01.01.2026|01/01/2026\"", s.Result);
            Assert.Contains("Invalid language tag", Run("return new Date().toLocaleDateString('xx-nonesiste');").Error);   // as in a browser
        }

        // ---------------------------------------------------------------- the script in its own process (Lemora.exe --lemora-script-host)

        private McpScript RunInChild(string code, int seconds = 30, Func<string, JObject, Task<JObject>>? call = null)
        {
            var old = McpScript.ChildExe;
            var exe = System.IO.Path.Combine(AppContext.BaseDirectory, "Lemora.exe");
            Assert.True(System.IO.File.Exists(exe), "Lemora.exe next to the tests");
            McpScript.ChildExe = () => exe;
            try
            {
                var s = new McpScript(call ?? Fake, () => _halt, seconds).Start(code);
                Assert.True(s.Run.Wait(TimeSpan.FromSeconds(60)), "the script did not end");
                return s;
            }
            finally { McpScript.ChildExe = old; }
        }

        [Fact]
        public void Child_CallsTheToolsHere_AndGivesTheResult()
        {
            var s = RunInChild("for (var i = 0; i < 3; i++) clic({sessione: 'a', x: i, y: 1}); var r = leggi_schermo({sessione: 'a'}); log('letto', r.testo); return {letto: r.testo, ok: r.ok};");
            Assert.Null(s.Error);
            Assert.Equal(new[] { "clic", "clic", "clic", "leggi_schermo" }, _calls.Select(c => c.name));
            Assert.Equal("{\"letto\":\"Done\",\"ok\":true}", s.Result);
            Assert.Contains("log: letto Done", Text(s.Report()));
        }

        [Fact]
        public void Child_ImagesStayHere_AndAreAttached()
        {
            var s = RunInChild("var a = schermata({sessione: 'a'}); allega(a); return a.immagini.length;");
            Assert.Null(s.Error);
            Assert.Equal("image", ((JArray)s.Report()["content"]!)[1].Value<string>("type"));
        }

        [Fact]
        public void Child_ErrorsAndFaults_AsInsideLemora()
        {
            Assert.Contains("(line 2,", RunInChild("var a = 1;\nclic('ciao');").Error);
            Assert.Contains("cannot be called from a script", RunInChild("return esegui_script({codice: 'return 1'});").Error);
            _halt = "the user pressed Stop Claude";
            Assert.Equal("stopped: the user pressed Stop Claude", RunInChild("clic({sessione: 'a', x: 1, y: 1}); return 1;").Error);
        }

        [Fact]
        public void Child_FermaScript_StopsAtOnce_EvenDuringAPause()
        {
            var old = McpScript.ChildExe;
            McpScript.ChildExe = () => System.IO.Path.Combine(AppContext.BaseDirectory, "Lemora.exe");
            try
            {
                var s = new McpScript(Fake, () => null, 60).Start("pausa(50000); return 'not here';");
                System.Threading.Thread.Sleep(1500);
                s.Stop("ferma_script", byRequest: true);
                Assert.True(s.Run.Wait(TimeSpan.FromSeconds(5)), "not stopped at once");
                Assert.Contains("stopped as requested", Text(s.Report()));
            }
            finally { McpScript.ChildExe = old; }
        }

        [Fact]
        public void Child_HugeData_EndsInTheChild_ThisProcessDoesNotGrow()
        {
            var before = GC.GetTotalMemory(true);
            // one statement builds a bigger and bigger string: inside Lemora this took it to 1.5 GB
            var s = RunInChild("var b = 'x'.repeat(1e7), s = ''; for (var i = 0; i < 60; i++) s = `${s}${b}`; return s.length;", seconds: 60);
            Assert.NotNull(s.Error);
            Assert.Contains("memory", s.Error);
            Assert.True(GC.GetTotalMemory(true) - before < 100_000_000, "this process grew");
        }

        // ---------------------------------------------------------------- fixes after the fifth external test

        [Fact]
        public void FermaScript_DoesNotWaitForASlowTool()
        {
            Task<JObject> Slow(string name, JObject a) => Task.Delay(30_000).ContinueWith(_ => new JObject { ["content"] = new JArray() });
            var s = new McpScript(Slow, () => null, 60).Start("apri_sessione({id: 'x'}); return 'not here';");
            System.Threading.Thread.Sleep(500);
            s.Stop("ferma_script", byRequest: true);
            Assert.True(s.Run.Wait(TimeSpan.FromSeconds(3)), "it waited for the tool");
            Assert.Contains("stopped as requested", Text(s.Report()));
        }

        [Fact]
        public void CatastrophicRegex_SaysItIsTheRegex_BeforeSecondiMax()
        {
            var e = Run("return /^(a+)+$/.test('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!');", seconds: 10).Error!;
            Assert.Contains("regular expression", e);
        }

        [Fact]
        public void ScriptApi_ListsEveryToolWithItsParameters()
        {
            var tools = new JArray(new JObject
            {
                ["name"] = "clic", ["description"] = "Mouse click. More text.",
                ["inputSchema"] = new JObject { ["properties"] = new JObject { ["sessione"] = new JObject(), ["x"] = new JObject() }, ["required"] = new JArray("sessione") },
            });
            Assert.Equal("clic({sessione, x?}) - Mouse click.", McpTools.ScriptApi(tools));
        }
    }
}
