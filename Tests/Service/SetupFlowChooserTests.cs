using System;
using System.IO;
using Lemora.Service;
using Lemora.Setup;
using Xunit;

namespace Tests.Service
{
    [CollectionDefinition("ProcessState", DisableParallelization = true)]
    public class ProcessStateCollection { }

    /// <summary>
    /// ShouldShowChooser con cartelle temporanee. Cambia cartella corrente e variabile d'ambiente (stato di processo: niente parallelismo).
    /// Non scrive nel registro: IsInstalledCopy lo legge soltanto e, per l'exe dei test, da' falso (il caso "installato" resta fuori).
    /// </summary>
    [Collection("ProcessState")]
    public class SetupFlowChooserTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "lemora-setup-" + Guid.NewGuid().ToString("N"));
        private readonly string oldCwd = Environment.CurrentDirectory;
        private readonly string? oldEnv = Environment.GetEnvironmentVariable("LEMORA_NO_INSTALLER");
        private readonly AppPathHelper portable, appData;

        public SetupFlowChooserTests()
        {
            var pDir = Path.Combine(root, "exe"); var aDir = Path.Combine(root, "appdata");
            Directory.CreateDirectory(pDir); Directory.CreateDirectory(aDir);
            portable = new AppPathHelper(pDir, pDir);
            appData = new AppPathHelper(aDir, aDir);
            Environment.SetEnvironmentVariable("LEMORA_NO_INSTALLER", null);
            Environment.CurrentDirectory = pDir;
        }

        public void Dispose()
        {
            Environment.CurrentDirectory = oldCwd;
            Environment.SetEnvironmentVariable("LEMORA_NO_INSTALLER", oldEnv);
            try { Directory.Delete(root, true); } catch { }
        }

        [Fact]
        public void NoProfiles_ShowsChooser() => Assert.True(SetupFlow.ShouldShowChooser(portable, appData));

        [Fact]
        public void ProfileBesideExe_NoChooser()
        {
            File.WriteAllText(portable.ProfileJsonPath, "{}");
            Assert.False(SetupFlow.ShouldShowChooser(portable, appData));
        }

        [Fact]
        public void OnlyAppDataProfile_StillShowsChooser()
        {
            File.WriteAllText(appData.ProfileJsonPath, "{}");
            Assert.True(SetupFlow.ShouldShowChooser(portable, appData));
        }

        [Fact]
        public void NoInstallerEnvVar_NoChooser()
        {
            Environment.SetEnvironmentVariable("LEMORA_NO_INSTALLER", "1");
            Assert.False(SetupFlow.ShouldShowChooser(portable, appData));
        }

        [Theory]
        [InlineData(AppPathHelper.FORCE_INTO_APPDATA_MODE)]
        [InlineData(AppPathHelper.FORCE_INTO_PORTABLE_MODE)]
        public void ModeMarkerInCurrentDir_NoChooser(string marker)
        {
            File.WriteAllText(Path.Combine(Environment.CurrentDirectory, marker), "");
            Assert.False(SetupFlow.ShouldShowChooser(portable, appData));
        }
    }
}
