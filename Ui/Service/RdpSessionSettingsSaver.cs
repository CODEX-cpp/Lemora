using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using Lemora.Model;
using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;
using Lemora.View.Host;

namespace Lemora.Service
{
    /// <summary>
    /// Graphics quality, display and advanced settings changed during an RDP session are kept in the saved server:
    /// the next connection starts from them. Only the changed fields are copied (never the session's user and password) and saving happens after a pause.
    /// </summary>
    public static class RdpSessionSettingsSaver
    {
        private static readonly HashSet<string> Persisted = SessionRdpSettingsDialogViewModel.LiveDisplay
            .Concat(SessionRdpSettingsDialogViewModel.ReconnectDisplay).Concat(SessionRdpSettingsDialogViewModel.Advanced).ToHashSet();

        private static int _ignore;

        /// <summary>changes made by the program and not by the user (picture in picture, leaving "multiple monitors"): they do not go into the saved server</summary>
        public static void Ignoring(Action change)
        {
            _ignore++;
            try { change(); } finally { _ignore--; }
        }

        public static void Attach(RDP session, ProtocolBase saved)
        {
            var changed = new HashSet<string>();
            // Attach runs on the thread that opens the connection (not the UI thread): a timer created there never fires and nothing got saved.
            // It must be explicitly bound to the UI thread.
            var timer = new DispatcherTimer(DispatcherPriority.Normal, System.Windows.Application.Current.Dispatcher) { Interval = TimeSpan.FromMilliseconds(700) };   // width and height are typed digit by digit
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var names = changed.ToList(); changed.Clear();
                Save(session, saved, names);
            };
            session.PropertyChanged += (_, e) =>
            {
                if (_ignore > 0 || e.PropertyName == null || !Persisted.Contains(e.PropertyName)) return;
                changed.Add(e.PropertyName);
                timer.Stop(); timer.Start();
            };
        }

        /// <summary>saves all the session settings right away (without the pause): needed before reopening the session with another RDP client</summary>
        public static void SaveNow(RDP session, ProtocolBase saved) => Save(session, saved, Persisted.ToList());

        private static void Save(RDP session, ProtocolBase saved, List<string> names)
        {
            try
            {
                var data = IoC.Get<GlobalData>();
                // unsaved entry (quick connection) or read-only: nothing to write
                // Teams entry: the server does not keep these settings (with "with approval" an empty request would start at every window change)
                if (data.GetItemById(saved.DataSource?.DataSourceName ?? "", saved.Id)?.Server is not RDP target)
                {
                    Shawn.Utils.SimpleLogHelper.Info($"RDP settings changed ({string.Join(",", names)}) but '{saved.DisplayName}' is not a saved entry: not kept");
                    return;
                }
                bool teams = target.DataSource is Lemora.Service.DataSource.Model.TeamsSource;
                if (!teams && target.DataSource?.IsWritable != true)
                {
                    Shawn.Utils.SimpleLogHelper.Info($"RDP settings changed ({string.Join(",", names)}) but the source of '{saved.DisplayName}' is not writable: not kept");
                    return;
                }
                Shawn.Utils.SimpleLogHelper.Info($"RDP settings kept for '{saved.DisplayName}' ({(teams ? "Teams, only on this PC" : "local source")}): {string.Join(",", names)}");
                foreach (var n in names)
                {
                    var p = typeof(RDP).GetProperty(n);
                    if (p?.CanWrite == true) p.SetValue(target, p.GetValue(session));
                }
                if (teams)
                {
                    // entry shared by the Teams server: quality, display and advanced settings stay on THIS PC (personal settings), never sent to the server and with no requests
                    // to the administrator, under any permission (before they were not saved and the next time the automatic quality came back)
                    const string prefix = "teams-";
                    if (target.Id.StartsWith(prefix)) Lemora.Service.DataSource.Model.TeamsOverlay.Save(target.Id.Substring(prefix.Length), target);
                    return;
                }
                var r = data.UpdateServer(target);
                if (!r.IsSuccess) Shawn.Utils.SimpleLogHelper.Warning("RDP settings not saved: " + r.ErrorInfo);
            }
            catch (Exception e)
            {
                Shawn.Utils.SimpleLogHelper.Warning("RdpSessionSettingsSaver: " + e.Message);
            }
        }
    }
}
