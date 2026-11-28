using System.Collections.Generic;
using System.Linq;
using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;
using Lemora.Utils;
using Newtonsoft.Json;
using Xunit;

namespace Tests.Utils
{
    /// <summary>security review points 2 and 3: an imported JSON with local Apps or commands before/after connecting</summary>
    public class ImportGuardTests
    {
        // file as someone who wants to launch their own program would prepare it: local App disguised as "RDP", command hidden in a "secret" argument
        private static List<ProtocolBase> HostileFile()
        {
            var app = new LocalApp { DisplayName = "Customer server", AppProtocolDisplayName = "RDP", ExePath = @"C:\Windows\System32\cmd.exe" };
            app.ArgumentList.Add(new AppArgument { Type = AppArgumentType.Secret, Key = "", Value = "/c calc.exe" });
            var rdp = new RDP { DisplayName = "Gateway", Address = "10.0.0.1", CommandBeforeConnected = "powershell -w hidden -c x", HideCommandBeforeConnectedWindow = true, CommandAfterDisconnected = "cmd /c y" };
            var ok = new SSH { DisplayName = "Linux", Address = "10.0.0.2" };
            var json = JsonConvert.SerializeObject(new object[] { app, rdp, ok });
            // same path as the import: list of objects, each recreated from its JSON
            return JsonConvert.DeserializeObject<List<object>>(json)!
                .Select(o => ItemCreateHelper.CreateFromJsonString(o.ToString()!)!).ToList();
        }

        [Fact]
        public void Describe_ShowsRealProgramArgumentsAndCommands()
        {
            var list = HostileFile();
            Assert.IsType<LocalApp>(list[0]);
            var lines = ImportGuard.Describe(list);
            Assert.Equal(3, lines.Count);
            Assert.Contains(lines, l => l.Contains(@"C:\Windows\System32\cmd.exe") && l.Contains("/c calc.exe"));   // also the "secret" argument, not ******
            Assert.Contains(lines, l => l.Contains("powershell -w hidden -c x"));
            Assert.Contains(lines, l => l.Contains("cmd /c y"));
        }

        [Fact]
        public void Strip_RemovesLocalAppsAndCommands_KeepsTheRest()
        {
            var list = HostileFile();
            Assert.Equal(1, ImportGuard.Strip(list));
            Assert.Equal(2, list.Count);
            Assert.DoesNotContain(list, p => p is LocalApp);
            Assert.All(list, p => Assert.True(p.CommandBeforeConnected == "" && p.CommandAfterDisconnected == "" && !p.HideCommandBeforeConnectedWindow));
            Assert.Empty(ImportGuard.Describe(list));
            Assert.Equal("10.0.0.1", ((RDP)list[0]).Address);
        }

        [Fact]
        public void NormalFile_NothingToAsk()
        {
            var list = new List<ProtocolBase> { new RDP { DisplayName = "a", Address = "1.2.3.4" }, new SSH { DisplayName = "b" } };
            Assert.Empty(ImportGuard.Describe(list));
            ImportGuard.Ask(list);   // no window
            Assert.Equal(2, list.Count);
        }
    }
}
