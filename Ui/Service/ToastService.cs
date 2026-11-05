using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Shawn.Utils.Wpf;

namespace Lemora.Service
{
    public enum ToastKind { Success, Warning, Error }

    /// <summary>una notifica breve: testo, tipo (filo colorato a sinistra) e, se l'azione e' reversibile, il pulsante Annulla</summary>
    public class ToastItem
    {
        public string Text { get; init; } = "";
        public ToastKind Kind { get; init; }
        public string UndoText { get; init; } = "";
        public bool HasUndo => !string.IsNullOrEmpty(UndoText);
        /// <summary>operazione in corso: mostra la rotellina e resta finche' chi l'ha avviata non la chiude</summary>
        public bool IsBusy { get; init; }
        public RelayCommand CmdUndo { get; }
        public RelayCommand CmdClose { get; }

        /// <summary>seconda riga di testo (piu' piccola e attenuata), facoltativa</summary>
        public string Body { get; init; } = "";
        public bool HasBody => !string.IsNullOrEmpty(Body);
        /// <summary>notifica con azioni: pulsante principale (azzurro) e secondario</summary>
        public string PrimaryText { get; init; } = "";
        public string SecondaryText { get; init; } = "";
        public bool HasActions => !string.IsNullOrEmpty(PrimaryText);
        public RelayCommand CmdPrimary { get; }
        public RelayCommand CmdSecondary { get; }

        public ToastItem(Action? undo, Action close, Action? primary = null, Action? secondary = null)
        {
            CmdUndo = new RelayCommand(_ => { close(); undo?.Invoke(); });
            CmdClose = new RelayCommand(_ => close());
            CmdPrimary = new RelayCommand(_ => { close(); primary?.Invoke(); });
            CmdSecondary = new RelayCommand(_ => { close(); secondary?.Invoke(); });
        }
    }

    /// <summary>
    /// Notifiche brevi (blueprint §12, A4): in basso al centro della finestra principale, spariscono da sole dopo pochi secondi
    /// (di piu' se hanno "Annulla"). Si usano da qualunque punto: <c>ToastService.Show("Indirizzo copiato")</c>.
    /// </summary>
    public static class ToastService
    {
        public static ObservableCollection<ToastItem> Items { get; } = new ObservableCollection<ToastItem>();

        /// <summary>
        /// Notifica "operazione in corso" (es. "Connessione a laboratorio..."): compare subito e resta finche' non si chiama l'azione restituita
        /// (per sicurezza sparisce da sola dopo 25 s).
        /// </summary>
        public static Action ShowBusy(string text)
        {
            var app = Application.Current;
            if (app == null) return () => { };
            ToastItem? item = null;
            DispatcherTimer? timer = null;
            var closed = false;
            void Close()
            {
                closed = true;
                timer?.Stop();
                if (item != null) Items.Remove(item);
            }
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (closed) return;
                item = new ToastItem(null, Close) { Text = text, Kind = ToastKind.Success, IsBusy = true };
                Items.Add(item);
                timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
                timer.Tick += (_, _) => Close();
                timer.Start();
            }));
            return () => app.Dispatcher.BeginInvoke(new Action(Close));
        }

        /// <summary>
        /// Notifica con una domanda e due pulsanti (es. "Salva in Acme" / "Ignora"): resta piu' a lungo e non blocca niente.
        /// </summary>
        public static void ShowAction(string text, string body, string primaryText, Action primary, string secondaryText, Action? secondary = null, int seconds = 20)
        {
            var app = Application.Current;
            if (app == null) return;
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                ToastItem? item = null;
                DispatcherTimer? timer = null;
                void Close()
                {
                    timer?.Stop();
                    if (item != null) Items.Remove(item);
                }
                item = new ToastItem(null, Close, primary, secondary) { Text = text, Body = body, Kind = ToastKind.Success, PrimaryText = primaryText, SecondaryText = secondaryText };
                Items.Add(item);
                while (Items.Count > 3) Items.RemoveAt(0);
                timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
                timer.Tick += (_, _) => Close();
                timer.Start();
            }));
        }

        public static void Show(string text, ToastKind kind = ToastKind.Success, string undoText = "", Action? undo = null)
        {
            var app = Application.Current;
            if (app == null) return;
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                ToastItem? item = null;
                DispatcherTimer? timer = null;
                void Close()
                {
                    timer?.Stop();
                    if (item != null) Items.Remove(item);
                }
                item = new ToastItem(undo, Close) { Text = text, Kind = kind, UndoText = undoText };
                Items.Add(item);
                while (Items.Count > 3) Items.RemoveAt(0);
                timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(string.IsNullOrEmpty(undoText) ? 3.5 : 7) };
                timer.Tick += (_, _) => Close();
                timer.Start();
            }));
        }
    }
}
