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
    /// Qualita' grafica, schermo e impostazioni avanzate cambiate durante una sessione RDP restano nel server salvato:
    /// alla connessione dopo si riparte da quelle. Si copiano solo i campi cambiati (mai utente e password della sessione) e si salva dopo una pausa.
    /// </summary>
    public static class RdpSessionSettingsSaver
    {
        private static readonly HashSet<string> Persisted = SessionRdpSettingsDialogViewModel.LiveDisplay
            .Concat(SessionRdpSettingsDialogViewModel.ReconnectDisplay).Concat(SessionRdpSettingsDialogViewModel.Advanced).ToHashSet();

        private static int _ignore;

        /// <summary>cambi fatti dal programma e non dall'utente (picture in picture, uscita da "schermi multipli"): non vanno nel server salvato</summary>
        public static void Ignoring(Action change)
        {
            _ignore++;
            try { change(); } finally { _ignore--; }
        }

        public static void Attach(RDP session, ProtocolBase saved)
        {
            var changed = new HashSet<string>();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };   // larghezza e altezza si scrivono cifra per cifra
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

        private static void Save(RDP session, ProtocolBase saved, List<string> names)
        {
            try
            {
                var data = IoC.Get<GlobalData>();
                // voce non salvata (connessione rapida) o in sola lettura: niente da scrivere
                if (data.GetItemById(saved.DataSource?.DataSourceName ?? "", saved.Id)?.Server is not RDP target || target.DataSource?.IsWritable != true) return;
                foreach (var n in names)
                {
                    var p = typeof(RDP).GetProperty(n);
                    if (p?.CanWrite == true) p.SetValue(target, p.GetValue(session));
                }
                data.UpdateServer(target);
            }
            catch (Exception e)
            {
                Shawn.Utils.SimpleLogHelper.Warning("RdpSessionSettingsSaver: " + e.Message);
            }
        }
    }
}
