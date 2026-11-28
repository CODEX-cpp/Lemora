using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lemora.Model.Protocol.FileTransmit;
using Lemora.Model.Protocol.FileTransmit.Transmitters;
using Lemora.Model.Protocol.FileTransmit.Transmitters.TransmissionController;
using Lemora.Service;
using Shawn.Utils.Interface;
using Xunit;

namespace Tests.Service
{
    /// <summary>
    /// legacy SFTP against the hostile fake server (ui-map\tools\fake-servers\sftp_ostile.py, user test/test).
    /// Runs only with LEMORA_SFTP_FAKE_PORT set (prova-sftp-ostile.ps1 starts the server and sets it).
    /// </summary>
    [Collection("vault-key")]
    public class LegacySftpLiveTests
    {
        private static int? Port => int.TryParse(Environment.GetEnvironmentVariable("LEMORA_SFTP_FAKE_PORT"), out var p) ? p : null;

        private sealed class NoLang : ILanguageService
        {
            public void AddXamlLanguageResources(string code, string fullName) { }
            public string Translate(Enum e) => e.ToString();
            public string Translate(string key) => key;
            public string Translate(string key, params object[] parameters) => key;
        }

        [Fact]
        public async Task UnknownServerRefused_PasswordNeverSent()
        {
            if (Port is not { } port) { Assert.Skip("fake SFTP server not started"); return; }
            var asked = 0;
            TerminalKnownHosts.AskForTests = (_, _) => { asked++; return false; };   // the user answers "Cancel"
            try
            {
                var t = new TransmitterSFtp("127.0.0.1", port, "test", "password-that-must-not-arrive", true);
                await t.Conn();
                Assert.False(t.IsConnected());
                Assert.Equal(1, asked);   // asked only once, even with RetryHelper's retries
                t.Release();
            }
            finally { TerminalKnownHosts.AskForTests = null; }
        }

        [Fact]
        public async Task HostileNameIsListedButNeverWrittenOutsideTheFolder()
        {
            if (Port is not { } port) { Assert.Skip("fake SFTP server not started"); return; }
            TerminalKnownHosts.AskForTests = (_, _) => true;
            var root = Path.Combine(Path.GetTempPath(), "lemora-sftp-" + Guid.NewGuid().ToString("N"));
            var dest = Path.Combine(root, "a", "b");
            Directory.CreateDirectory(dest);
            try
            {
                var t = new TransmitterSFtp("127.0.0.1", port, "test", "test", true);
                await t.Conn();
                Assert.True(t.IsConnected());
                var items = await t.ListDirectoryItems("/d");
                // the library lets the name with ..\ through (this was the fact to verify)
                Assert.Contains(items, i => i.Name.Contains("..\\"));

                var task = new TransmitTask(new NoLang(), t, "test", dest, items.ToArray());
                var done = new TaskCompletionSource<ETransmitTaskStatus>();
                task.OnTaskEnd += (s, _) => done.TrySetResult(s);
                task.StartTransmitAsync(items);
                await Task.WhenAny(done.Task, Task.Delay(30000));

                Assert.True(File.Exists(Path.Combine(dest, "normale.txt")), "the normal file must be downloaded");
                Assert.False(File.Exists(Path.Combine(root, "lemora-trav.txt")), "the name with ..\\ wrote outside the folder");
                Assert.False(File.Exists(Path.Combine(root, "a", "lemora-trav.txt")));
                Assert.True(Lemora.Utils.MarkOfTheWeb.Has(Path.Combine(dest, "normale.txt")), "Mark-of-the-Web missing");
                t.Release();
            }
            finally
            {
                TerminalKnownHosts.AskForTests = null;
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}
