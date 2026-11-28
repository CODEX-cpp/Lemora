using System;
using System.IO;
using Lemora.Service.Mcp;
using Xunit;

namespace Tests.Service
{
    /// <summary>prendi_file: what comes from the remote PC never overwrites a file of the user</summary>
    public class McpFilesTests
    {
        [Fact]
        public void CopyInto_ExistingName_GetsANumber_AndKeepsTheOld()
        {
            var root = Path.Combine(Path.GetTempPath(), "lemora-mcpfiles-" + Guid.NewGuid().ToString("N"));
            try
            {
                var src = Path.Combine(root, "remote"); var dst = Path.Combine(root, "here");
                Directory.CreateDirectory(src); Directory.CreateDirectory(dst);
                File.WriteAllText(Path.Combine(src, "report.txt"), "new");
                File.WriteAllText(Path.Combine(dst, "report.txt"), "the user's");

                var first = McpTools.CopyInto(Path.Combine(src, "report.txt"), dst);

                Assert.NotEqual(Path.Combine(dst, "report.txt"), first);
                Assert.Equal("the user's", File.ReadAllText(Path.Combine(dst, "report.txt")));
                Assert.Equal("new", File.ReadAllText(first));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public void CopyInto_Folder_CopiesItsContents()
        {
            var root = Path.Combine(Path.GetTempPath(), "lemora-mcpfiles-" + Guid.NewGuid().ToString("N"));
            try
            {
                var src = Path.Combine(root, "remote", "folder"); var dst = Path.Combine(root, "here");
                Directory.CreateDirectory(Path.Combine(src, "sub")); Directory.CreateDirectory(dst);
                File.WriteAllText(Path.Combine(src, "sub", "a.txt"), "a");

                var copied = McpTools.CopyInto(src, dst);

                Assert.Equal("a", File.ReadAllText(Path.Combine(copied, "sub", "a.txt")));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }
}
