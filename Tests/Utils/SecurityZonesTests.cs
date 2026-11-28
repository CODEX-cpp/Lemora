using System;
using System.IO;
using System.Linq;
using Lemora.Model.Protocol;
using Lemora.Service;
using Lemora.Setup;
using Newtonsoft.Json;
using Xunit;

namespace Tests.Utils
{
    /// <summary>security fixes: icons, installation folder and low findings (Hello, file transfer, quick connections)</summary>
    [Collection("ProcessMemory")]   // measures the memory of the whole process: not together with the script tests that use hundreds of MB
    public class SecurityZonesTests
    {
        private static string Png(int w, int h)
        {
            using var bmp = new System.Drawing.Bitmap(w, h);
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return Convert.ToBase64String(ms.ToArray());
        }

        [Fact]
        public void Icon_HugeImage_NotDecoded()
        {
            var big = Png(4000, 4000);   // a few KB on disk, 64 MB decoded
            var rdp = new RDP { IconBase64 = big };
            var before = GC.GetTotalMemory(true);
            Assert.Null(rdp.IconImg);
            Assert.Null(rdp.IconImg);   // the second time it is not retried
            Assert.True(GC.GetTotalMemory(false) - before < 20_000_000, "whole decode");
        }

        [Fact]
        public void Icon_NormalImage_Shown()
        {
            var rdp = new RDP { IconBase64 = Png(64, 64) };
            Assert.NotNull(rdp.IconImg);
            Assert.Equal(64, rdp.IconImg!.PixelWidth);
        }

        [Fact]
        public void Icon_TooLongText_Refused()
        {
            var rdp = new RDP { IconBase64 = new string('A', 800_000) };
            Assert.Null(rdp.IconImg);
        }

        [Fact]
        public void InstallDir_InUserProfile_Refused()
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.True(SetupEngine.IsUnsafeInstallDir(Path.Combine(profile, "AppData", "Local", "Lemora")));
            Assert.True(SetupEngine.IsUnsafeInstallDir(Path.Combine(Path.GetDirectoryName(profile)!, "OtherUser", "Lemora")));
            Assert.False(SetupEngine.IsUnsafeInstallDir(@"C:\Program Files\Lemora"));
            Assert.False(SetupEngine.IsUnsafeInstallDir(@"C:\Lemora"));
            Assert.False(SetupEngine.IsUnsafeInstallDir(@"C:\UsersLemora"));
        }

        [Fact]
        public void Hello_NewNonce_SignsDifferentMessage()
        {
            var a = VaultUnlock.HelloMessage(VaultUnlock.NewNonce());
            var b = VaultUnlock.HelloMessage(VaultUnlock.NewNonce());
            Assert.NotEqual(a, b);
            Assert.Equal("Lemora data vault v1", System.Text.Encoding.UTF8.GetString(VaultUnlock.HelloMessage("")));   // old locks: same phrase as before
        }

        [Fact]
        public void Profile_RemoteDirs_NotWritten()
        {
            var ft = new FileTransferConfig();
            ft.RemoteDefaultDirs["01ABC_10.0.0.5:5900(0123456789abcdef@admin)"] = "C:/x";
            var json = JsonConvert.SerializeObject(ft);
            Assert.DoesNotContain("10.0.0.5", json);
            Assert.DoesNotContain("RemoteDefaultDirs", json);
        }
    }
}
