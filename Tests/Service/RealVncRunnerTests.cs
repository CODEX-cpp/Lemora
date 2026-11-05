using Lemora.Model.Protocol;
using Xunit;

namespace Tests.Service
{
    public class RealVncRunnerTests
    {
        // vncviewer.exe è anche il nome di UltraVNC: il percorso RealVNC non deve essere scambiato per UltraVNC
        [Fact]
        public void RealVncExePath_IsNotMistakenForUltraVnc()
        {
            var app = AppArgumentHelper.GetPresetArgumentList(@"C:\Program Files\RealVNC\VNC Viewer\vncviewer.exe");
            Assert.Equal("RealVNC", app?.DisplayName);
        }
    }
}
