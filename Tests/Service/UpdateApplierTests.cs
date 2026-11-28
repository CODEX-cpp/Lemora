using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Lemora.Setup;
using Xunit;

namespace Tests.Service
{
    /// <summary>versions\ folders, current.txt and cleanup of old versions (the part that runs with administrator rights on the user's PC)</summary>
    public class UpdateApplierTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "lemora-applier-" + Guid.NewGuid().ToString("N"));
        public UpdateApplierTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static byte[] Zip(params (string name, string content)[] files)
        {
            using var ms = new MemoryStream();
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
                foreach (var (name, content) in files)
                {
                    var e = z.CreateEntry(name);
                    using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                    w.Write(content);
                }
            return ms.ToArray();
        }

        [Fact]
        public void Activate_PutsTheVersionInItsFolder_AndPointsCurrentToIt()
        {
            UpdateApplier.ActivateFromZip(Zip(("Lemora.exe", "app-1"), ("extra/readme.txt", "x")), _dir, "1.2.3.4");
            Assert.Equal("app-1", File.ReadAllText(Path.Combine(_dir, "versions", "1.2.3.4", "Lemora.exe")));
            Assert.True(File.Exists(Path.Combine(_dir, "versions", "1.2.3.4", "extra", "readme.txt")));
            Assert.Equal("1.2.3.4", UpdateApplier.ReadCurrent(_dir));
            Assert.Empty(Directory.GetDirectories(Path.Combine(_dir, "versions"), "*.tmp"));
        }

        [Fact]
        public void Prune_KeepsCurrentAndTheNewestOther_RemovesTheRest()
        {
            foreach (var v in new[] { "1.0.0.1", "1.0.0.2", "1.0.0.3" })
            {
                UpdateApplier.ActivateFromZip(Zip(("Lemora.exe", v)), _dir, v);
                Directory.SetCreationTimeUtc(Path.Combine(_dir, "versions", v), DateTime.UtcNow.AddMinutes(Array.IndexOf(new[] { "1.0.0.1", "1.0.0.2", "1.0.0.3" }, v)));
            }
            UpdateApplier.Prune(_dir, "1.0.0.3");
            var left = Directory.GetDirectories(Path.Combine(_dir, "versions")).Select(Path.GetFileName).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { "1.0.0.2", "1.0.0.3" }, left);
        }

        [Fact]
        public void ZipSlip_IsRejected_AndNothingIsLeftBehind()
        {
            UpdateApplier.ActivateFromZip(Zip(("Lemora.exe", "ok")), _dir, "1.0.0.1");
            var evil = Zip(("Lemora.exe", "x"), ("../../outside.txt", "x"));
            Assert.Throws<InvalidDataException>(() => UpdateApplier.ActivateFromZip(evil, _dir, "1.0.0.2"));
            Assert.False(File.Exists(Path.Combine(_dir, "outside.txt")));
            Assert.Equal("1.0.0.1", UpdateApplier.ReadCurrent(_dir));   // the selected version does not change
        }

        [Fact]
        public void PackageWithoutTheApp_IsRejected_CurrentUnchanged()
        {
            UpdateApplier.ActivateFromZip(Zip(("Lemora.exe", "ok")), _dir, "1.0.0.1");
            Assert.Throws<InvalidDataException>(() => UpdateApplier.ActivateFromZip(Zip(("other.txt", "x")), _dir, "1.0.0.2"));
            Assert.Equal("1.0.0.1", UpdateApplier.ReadCurrent(_dir));
            Assert.False(Directory.Exists(Path.Combine(_dir, "versions", "1.0.0.2")));
            Assert.False(Directory.Exists(Path.Combine(_dir, "versions", "1.0.0.2.tmp")));
        }

        [Theory]
        [InlineData("..\\evil")]
        [InlineData("1.0.0.1\\..\\x")]
        [InlineData("latest")]
        [InlineData("")]
        public void BadVersionNames_AreRejected(string version) =>
            Assert.Throws<ArgumentException>(() => UpdateApplier.ActivateFromZip(Zip(("Lemora.exe", "x")), _dir, version));

        [Fact]
        public void SameVersionAgain_ReplacesTheFolder()
        {
            UpdateApplier.ActivateFromZip(Zip(("Lemora.exe", "before")), _dir, "1.0.0.1");
            UpdateApplier.ActivateFromZip(Zip(("Lemora.exe", "after")), _dir, "1.0.0.1");
            Assert.Equal("after", File.ReadAllText(Path.Combine(_dir, "versions", "1.0.0.1", "Lemora.exe")));
        }

        [Theory]
        [InlineData(@"C:\Program Files\Lemora\versions\1.2.3.4\Lemora.exe", @"C:\Program Files\Lemora")]
        [InlineData(@"C:\Program Files\Lemora\Lemora.exe", null)]
        [InlineData(@"D:\download\Lemora.exe", null)]
        [InlineData(@"C:\x\versions\Lemora.exe", null)]
        public void RootOfVersionedExe(string exe, string? root) => Assert.Equal(root, UpdateApplier.RootOfVersionedExe(exe));

        [Fact]
        public void ReadCurrent_MissingOrEmpty_IsNull()
        {
            Assert.Null(UpdateApplier.ReadCurrent(_dir));
            File.WriteAllText(Path.Combine(_dir, "current.txt"), "  \n");
            Assert.Null(UpdateApplier.ReadCurrent(_dir));
        }
    }
}
