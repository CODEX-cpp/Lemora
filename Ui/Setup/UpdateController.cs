using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Lemora.Service;
using Shawn.Utils;
using Shawn.Utils.Wpf;

namespace Lemora.Setup
{
    public enum UpdateStatus { Idle, Downloading, Ready }

    /// <summary>what the box at the top above "Options" in the side menu shows (UpdateBadge): hidden, "downloading", "restart to update"</summary>
    public class UpdateState : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private UpdateStatus _status = UpdateStatus.Idle;
        private string _version = "";

        public UpdateStatus Status => _status;
        public string Version => _version;
        public bool IsVisible => _status != UpdateStatus.Idle;
        public bool IsDownloading => _status == UpdateStatus.Downloading;
        public bool IsReady => _status == UpdateStatus.Ready;
        public string Title => _status == UpdateStatus.Downloading ? SetupText.T("up_downloading") : SetupText.T("up_restart");
        public string Sub => string.Format(SetupText.T("up_version"), _version);

        public RelayCommand CmdClick { get; }
        public UpdateState() => CmdClick = new RelayCommand(_ => { if (_status == UpdateStatus.Ready) UpdateController.RestartRequested(); });

        public void Set(UpdateStatus status, string version)
        {
            void Apply()
            {
                _status = status; _version = version;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
            }
            var d = Application.Current?.Dispatcher;
            if (d == null || d.CheckAccess()) Apply(); else d.BeginInvoke(new Action(Apply));
        }
    }

    /// <summary>
    /// Automatic update, part 3: when and how the check runs and what the user sees.
    /// The check runs every time the interface is shown (from the tray to the full window, or at opening), never when the app starts hidden;
    /// at most once every two minutes (GitHub without login accepts 60 requests per hour). When there is a new version it downloads by itself
    /// and the box above "Options" goes from "Downloading" to "Restart to update": one click and the app reopens updated.
    /// Only for the versioned installed app (launcher): an arbitrary or portable exe does not update itself.
    /// </summary>
    public static class UpdateController
    {
        private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(2);
        private static int _busy;
        private static DateTime _lastCheck = DateTime.MinValue;
        private static UpdateInfo? _info;       // version found
        private static string? _zip;            // package already downloaded and verified

        /// <summary>state shown by the box</summary>
        public static UpdateState State { get; } = new();

        /// <summary>for tests: replaces the "close the app to restart" action</summary>
        internal static Action? ExitOverride;

        /// <summary>to be called when the interface becomes visible</summary>
        public static void OnInterfaceShown()
        {
            if (Lemora.Utils.DevOnly.Env("LEMORA_NO_UPDATE") == "1") return;
            if (!CanUpdate()) return;
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            _ = Task.Run(async () =>
            {
                try { await RunAsync(); }
                catch (Exception e) { SimpleLogHelper.Warning("Update check: " + e.Message); State.Set(UpdateStatus.Idle, ""); }
                finally { Volatile.Write(ref _busy, 0); }
            });
        }

        /// <summary>only the app installed with this mechanism, in the "launcher + versions" layout (or, for tests, with LEMORA_UPDATE_FORCE=1)</summary>
        private static bool CanUpdate() =>
            Lemora.Utils.DevOnly.Env("LEMORA_UPDATE_FORCE") == "1" || (SetupEngine.IsInstalledCopy() && UpdateApplier.RootOfVersionedExe(SetupEngine.ExePath) != null);

        private static async Task RunAsync()
        {
            if (_zip != null && _info != null) { State.Set(UpdateStatus.Ready, _info.Version.ToString()); return; }   // already downloaded
            if (DateTime.UtcNow - _lastCheck < MinInterval) return;
            _lastCheck = DateTime.UtcNow;
            var info = await UpdateService.CheckAsync();
            if (info == null) return;
            _info = info;
            await DownloadAsync(info);
        }

        internal static async Task DownloadAsync(UpdateInfo info)
        {
            State.Set(UpdateStatus.Downloading, info.Version.ToString());
            try { _zip = await UpdateService.DownloadAsync(info); }
            catch (Exception e)
            {
                _zip = null;
                SimpleLogHelper.Warning("Update download: " + e.Message);
                State.Set(UpdateStatus.Idle, "");   // retried the next time the interface is shown
                ToastService.Show(string.Format(SetupText.T("up_error"), e.Message), ToastKind.Error);
                return;
            }
            State.Set(UpdateStatus.Ready, info.Version.ToString());
        }

        /// <summary>click on "Restart to update"</summary>
        internal static void RestartRequested() => _ = RestartAsync();

        private static async Task RestartAsync()
        {
            if (_zip == null || _info == null) return;
            var error = await SetupEngine.StartUpdateAsync(_zip, _info.Version);
            if (error != null) { ToastService.Show(string.Format(SetupText.T("up_error"), error), ToastKind.Error); return; }
            if (ExitOverride != null) ExitOverride(); else App.Close();
        }
    }
}
