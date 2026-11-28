using System.IO;
using System.Text;
using Lemora.Service;
using Xunit;

namespace Tests.Service
{
    /// <summary>.mora file: reads back identical, is not human-readable, is rejected if tampered with</summary>
    public class MoraFileTests
    {
        private static string Tmp() => Path.Combine(Path.GetTempPath(), "mora-test-" + System.Guid.NewGuid().ToString("N") + ".mora");

        [Fact]
        public void WriteThenRead_GivesTheSameConnection()
        {
            var p = Tmp();
            try
            {
                MoraFile.Write(p, new MoraConnection { Protocol = "RDP", Name = "Customer server", Address = "192.0.2.1", Port = "3390", UserName = @"ACME\Administrator", Domain = "ACME", NewWindow = true });
                var c = MoraFile.Read(p)!;
                Assert.NotNull(c);
                Assert.Equal("RDP", c.Protocol);
                Assert.Equal("Customer server", c.Name);
                Assert.Equal("192.0.2.1", c.Address);
                Assert.Equal("3390", c.Port);
                Assert.Equal(@"ACME\Administrator", c.UserName);
                Assert.True(c.NewWindow);
            }
            finally { File.Delete(p); }
        }

        [Fact]
        public void FileIsNotReadable()
        {
            var p = Tmp();
            try
            {
                MoraFile.Write(p, new MoraConnection { Protocol = "RDP", Name = "Secret", Address = "10.20.30.40", UserName = "mario" });
                var text = Encoding.UTF8.GetString(File.ReadAllBytes(p));
                Assert.DoesNotContain("10.20.30.40", text);
                Assert.DoesNotContain("Secret", text);
                Assert.DoesNotContain("mario", text);
            }
            finally { File.Delete(p); }
        }

        [Fact]
        public void TamperedOrForeignFile_IsRejected()
        {
            var p = Tmp();
            try
            {
                MoraFile.Write(p, new MoraConnection { Protocol = "VNC", Address = "10.0.0.1" });
                var bytes = File.ReadAllBytes(p);
                bytes[bytes.Length - 1] ^= 0xFF;   // one changed byte: the encryption detects it
                File.WriteAllBytes(p, bytes);
                Assert.Null(MoraFile.Read(p));
                File.WriteAllText(p, "{\"Protocol\":\"RDP\",\"Address\":\"1.2.3.4\"}");   // hand-written plain text
                Assert.Null(MoraFile.Read(p));
            }
            finally { File.Delete(p); }
        }

        [Fact]
        public void SafeFileName_RemovesForbiddenCharacters()
        {
            Assert.Equal("a_b_c", MoraFile.SafeFileName("a/b:c"));
            Assert.Equal("Lemora", MoraFile.SafeFileName("  "));
        }
    }
}
