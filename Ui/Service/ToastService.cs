using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Shawn.Utils.Wpf;

namespace Lemora.Service
{
    public enum ToastKind { Success, Warning, Error }

    /// <summary>a short notification: text, kind (colored strip on the left) and, if the action is reversible, the Undo button</summary>
    public class ToastItem
    {
        public string Text { get; init; } = "";
        public ToastKind Kind { get; init; }
        public string UndoText { get; init; } = "";
        public bool HasUndo => !string.IsNullOrEmpty(UndoText);
        /// <summary>operation in progress: shows the spinner and stays until whoever started it closes it</summary>
        public bool IsBusy { get; init; }
        public RelayCommand CmdUndo { get; }
        public RelayCommand CmdClose { get; }

        /// <summary>optional second line of text (smaller and dimmed)</summary>
        public string Body { get; init; } = "";
        public bool HasBody => !string.IsNullOrEmpty(Body);
        /// <summary>notification with actions: primary (blue) and secondary button</summary>
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
    /// Short notifications (blueprint §12, A4): at the bottom center of the main window, they disappear on their own after a few seconds
    /// (longer if they have "Undo"). Usable from anywhere: <c>ToastService.Show("Address copied")</c>.
    /// </summary>
    public static class ToastService
    {
        public static ObservableCollection<ToastItem> Items { get; } = new ObservableCollection<ToastItem>();

        /// <summary>
        /// "Operation in progress" notification (e.g. "Connecting to lab..."): appears right away and stays until the returned action is called
        /// (as a safety net it disappears on its own after 25 s).
        /// </summary>
        public static Action ShowBusy(string text, System.Threading.CancellationTokenSource? cancelOnX = null)
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
                // the user's X also stops the operation (cancelOnX); closing at the end of the work and the 25 s limit use Close and do not cancel
                item = new ToastItem(null, () => { try { cancelOnX?.Cancel(); } catch (ObjectDisposedException) { } Close(); }) { Text = text, Kind = ToastKind.Success, IsBusy = true };
                Items.Add(item);
                timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
                timer.Tick += (_, _) => Close();
                timer.Start();
            }));
            return () => app.Dispatcher.BeginInvoke(new Action(Close));
        }

        /// <summary>
        /// Notification with a question and two buttons (e.g. "Save in ACME" / "Ignore"): stays longer and blocks nothing.
        /// </summary>
        public static void ShowAction(string text, string body, string primaryText, Action primary, string secondaryText, Action? secondary = null, int seconds = 20, ToastKind kind = ToastKind.Success)
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
                item = new ToastItem(null, Close, primary, secondary) { Text = text, Body = body, Kind = kind, PrimaryText = primaryText, SecondaryText = secondaryText };
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
                // time to read it: 3.5 s were too few for the long notices (Teams queue, refusals) and they vanished unread
                var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                var secs = Math.Clamp(1.5 + words * 0.35, 3.5, 12);
                if (!string.IsNullOrEmpty(undoText)) secs = Math.Max(secs, 7);
                timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(secs) };
                timer.Tick += (_, _) => Close();
                timer.Start();
            }));
        }
    }
}
