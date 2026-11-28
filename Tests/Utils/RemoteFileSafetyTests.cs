using System;
using System.IO;
using Lemora.Model.Protocol.FileTransmit.Transmitters.TransmissionController;
using Lemora.Utils;
using Lemora.View.Host.ProtocolHosts;
using Xunit;

namespace Tests.Utils
{
    /// <summary>file names chosen by a remote server (old SFTP/FTP) and Mark-of-the-Web tag</summary>
    public class RemoteFileSafetyTests
    {
        [Theory]
        [InlineData(@"a\b\file.txt", true)]
        [InlineData(@"a\b\..\file.txt", true)]
        [InlineData(@"..\outside.txt", false)]
        [InlineData(@"a\..\..\outside.txt", false)]
        [InlineData(@"C:\Windows\outside.txt", false)]
        [InlineData(@"file.txt:stream", false)]
        public void DownloadPathMustStayInsideDestination(string rel, bool expected)
        {
            var dest = Path.Combine(Path.GetTempPath(), "lemora-test-dl");
            Assert.Equal(expected, TransmitTask.IsInside(dest, Path.Combine(dest, rel)));
        }

        [Fact]
        public void PrefixOfDestinationIsNotInside()
        {
            // C:\dl and C:\dl-other: the same prefix is not enough
            var dest = Path.Combine(Path.GetTempPath(), "dl");
            Assert.False(TransmitTask.IsInside(dest, dest + @"-other\x.txt"));
        }

        [Theory]
        [InlineData("note.txt", true)]
        [InlineData("photo.JPG", true)]
        [InlineData("test.cmd", false)]
        [InlineData("setup.exe", false)]
        [InlineData("doc.docm", false)]
        [InlineData("no-extension", false)]
        public void PreviewOpensOnlySafeTypes(string name, bool expected) => Assert.Equal(expected, VmFileTransmitHost.PreviewIsSafe(name));

        [Fact]
        public void MarkOfTheWebWritesZone3()
        {
            var f = Path.Combine(Path.GetTempPath(), "lemora-motw-" + Guid.NewGuid().ToString("N") + ".exe");
            File.WriteAllText(f, "x");
            try
            {
                Assert.False(MarkOfTheWeb.Has(f));
                MarkOfTheWeb.Apply(f);
                Assert.True(MarkOfTheWeb.Has(f));
                Assert.Contains("ZoneId=3", File.ReadAllText(f + ":Zone.Identifier"));
            }
            finally { File.Delete(f); }
        }
    }
}
