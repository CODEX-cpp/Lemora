using Lemora.Model.Protocol;
using Xunit;

namespace Tests.Service
{
    public class RealVncRunnerTests
    {
        // vncviewer.exe is also UltraVNC's name: the RealVNC path must not be mistaken for UltraVNC
        [Fact]
        public void RealVncExePath_IsNotMistakenForUltraVnc()
        {
            var app = AppArgumentHelper.GetPresetArgumentList(@"C:\Program Files\RealVNC\VNC Viewer\vncviewer.exe");
            Assert.Equal("RealVNC", app?.DisplayName);
        }
    }
}
