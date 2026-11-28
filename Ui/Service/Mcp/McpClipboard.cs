using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Lemora.View.Host.ProtocolHosts;
using Stylet;

namespace Lemora.Service.Mcp
{
    /// <summary>
    /// The remote PC's clipboard for Claude (incolla_testo, leggi_appunti).
    /// VNC: the text goes to and comes from the server directly (RFB), this PC's clipboard is not touched.
    /// RDP with FreeRDP: FreeRDP links this PC's clipboard to the remote one, so it goes through here: first what the user had copied
    /// is saved and at the end it is put back (only if the user has not copied something else in the meantime).
    /// Only one operation at a time.
    /// </summary>
    internal static class McpClipboard
    {
        private static readonly SemaphoreSlim Gate = new(1, 1);

        public static async Task<IDisposable> LockAsync()
        {
            await Gate.WaitAsync();
            return new Releaser(() => Gate.Release());
        }

        private sealed class Releaser : IDisposable
        {
            private Action? _a;
            public Releaser(Action a) => _a = a;
            public void Dispose() { _a?.Invoke(); _a = null; }
        }

        [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();

        public static uint Sequence() => GetClipboardSequenceNumber();

        // ---------------------------------------------------------------- backup of the user's clipboard (RDP only)

        /// <summary>what the user had on the clipboard: text, formatting, image, files</summary>
        public sealed class Backup
        {
            internal DataObject? Data;
            /// <summary>the text Lemora put there: if it is gone at the end, the user copied something else and nothing is restored</summary>
            internal string? Ours;
            /// <summary>the clipboard holding Lemora's text: tells whether and when the remote PC asked for it</summary>
            internal Watched? Live;
            /// <summary>the user had a password copied by Lemora (flag for clipboard monitors): on restore the flag comes back, otherwise FreeRDP would announce it to the remote PC (security hardening)</summary>
            internal bool Secret;
        }

        /// <summary>
        /// "live" clipboard text (SetDataObject with copy=false): it stays Lemora's and Windows asks Lemora for it only when a program reads it.
        /// FreeRDP reads it when the remote PC pastes and asks for the data: so we know whether and when the paste really happened
        /// </summary>
        internal sealed class Watched : IDataObject
        {
            private readonly DataObject _d;
            /// <summary>counting starts only from Ctrl+V on: before that some other clipboard-monitoring program may read it</summary>
            internal volatile bool Armed;
            internal int EarlyReads;
            internal readonly TaskCompletionSource<bool> Read = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Watched(DataObject d) => _d = d;

            private object Mark(object? o)
            {
                if (o is string) { if (Armed) Read.TrySetResult(true); else Interlocked.Increment(ref EarlyReads); }
                return o!;
            }

            public object GetData(string format) => Mark(_d.GetData(format));
            public object GetData(Type format) => Mark(_d.GetData(format));
            public object GetData(string format, bool autoConvert) => Mark(_d.GetData(format, autoConvert));
            public bool GetDataPresent(string format) => _d.GetDataPresent(format);
            public bool GetDataPresent(Type format) => _d.GetDataPresent(format);
            public bool GetDataPresent(string format, bool autoConvert) => _d.GetDataPresent(format, autoConvert);
            public string[] GetFormats() => _d.GetFormats();
            public string[] GetFormats(bool autoConvert) => _d.GetFormats(autoConvert);
            public void SetData(object data) => _d.SetData(data);
            public void SetData(string format, object data) => _d.SetData(format, data);
            public void SetData(Type format, object data) => _d.SetData(format, data);
            public void SetData(string format, object data, bool autoConvert) => _d.SetData(format, data, autoConvert);
        }

        private static readonly string[] Formats = { DataFormats.UnicodeText, DataFormats.Rtf, DataFormats.Html, DataFormats.Bitmap, DataFormats.FileDrop };

        public static Backup Save() => OnUiSync(() =>
        {
            var b = new Backup();
            try
            {
                try { b.Secret = Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing"); } catch { b.Secret = true; }   // when in doubt, treat it as a password
                var d = new DataObject();
                var any = false;
                // files copied on a remote PC (offered by Lemora's FreeRDP): reading them would download them all right now, and putting them back
                // later would make FreeRDP send them to the server as if they had been copied here (seen in the sandbox): not saved
                var remoteFiles = IsFreeRdpFileOffer();
                foreach (var f in Formats)
                {
                    try
                    {
                        if (remoteFiles && f == DataFormats.FileDrop) continue;
                        if (!Clipboard.ContainsData(f)) continue;
                        var v = Clipboard.GetData(f);
                        if (v == null) continue;
                        d.SetData(f, v);
                        any = true;
                    }
                    catch { /* format not readable: skip it */ }
                }
                b.Data = any ? d : null;
            }
            catch { b.Data = null; }
            return b;
        });

        [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);

        /// <summary>the clipboard holds files of a remote PC, offered by a FreeRDP session (window of patch freerdp-zz-appunti-file-windows)</summary>
        internal static bool IsFreeRdpFileOffer()
        {
            var owner = GetClipboardOwner();
            if (owner == IntPtr.Zero) return false;
            var sb = new System.Text.StringBuilder(64);
            return GetClassName(owner, sb, sb.Capacity) > 0 && sb.ToString() == "FreeRDPClipboardFiles";
        }

        /// <summary>restores the user's clipboard, if they have not copied something else in the meantime (the clipboard still holds Lemora's or the remote PC's text)</summary>
        public static void Restore(Backup b, IntegrateHost host) => OnUiSync(() =>
        {
            try
            {
                var current = Clipboard.ContainsText() ? Clipboard.GetText() : null;
                // ours: the text put by Lemora or what the remote PC copied
                var stillOurs = (b.Ours != null && current == b.Ours) || host.McpClipboardFromRemote();
                if (!stillOurs) return true;
                // out of history (Win+V): the original is already there, putting it back would create a duplicate
                // user's password: the flag for clipboard monitors comes back too (FreeRDP holds it back until an intentional paste)
                if (b.Data != null) Retry(() => Clipboard.SetDataObject(Lemora.View.Host.SecretClipboard.NoHistory(b.Data, monitors: b.Secret), true));
                else Retry(Clipboard.Clear);
            }
            catch (Exception e) { Shawn.Utils.SimpleLogHelper.Warning($"MCP: user's clipboard not restored ({e.Message})"); }
            return true;
        });

        /// <summary>
        /// after invia_file: the user's clipboard comes back if it still holds the files Lemora put there (same list). Otherwise the files would stay
        /// on the clipboard and, once the hold ends, the other open FreeRDP sessions would announce them to their servers
        /// </summary>
        public static void RestoreAfterFiles(Backup b, IReadOnlyCollection<string> ours) => OnUiSync(() =>
        {
            try
            {
                if (IsFreeRdpFileOffer() || !Clipboard.ContainsFileDropList()) return true;   // the user copied files on a remote PC meanwhile: not read (it would download them)
                var current = Clipboard.GetFileDropList().Cast<string>().ToList();
                if (current.Count != ours.Count || !current.All(p => ours.Contains(p, StringComparer.OrdinalIgnoreCase))) return true;   // the user copied something else
                if (b.Data != null) Retry(() => Clipboard.SetDataObject(Lemora.View.Host.SecretClipboard.NoHistory(b.Data, monitors: b.Secret), true));
                else Retry(Clipboard.Clear);
            }
            catch (Exception e) { Shawn.Utils.SimpleLogHelper.Warning($"MCP: user's clipboard not restored after the files ({e.Message})"); }
            return true;
        });

        /// <summary>text on this PC's clipboard (FreeRDP passes it to the remote PC)</summary>
        public static bool SetLocal(string text, Backup b) => OnUiSync(() =>
        {
            // Claude's texts pass through this PC's clipboard only to reach the remote one: no Win+V history
            // without the flag for clipboard monitors: FreeRDP is one of them and might ignore the text (see NoHistory)
            try
            {
                var d = new DataObject(); d.SetText(text);
                var w = new Watched(Lemora.View.Host.SecretClipboard.NoHistory(d, monitors: false));
                Retry(() => Clipboard.SetDataObject(w, false));   // copy=false: the data stays "live" and we know when the remote PC asks for it
                b.Ours = text; b.Live = w; return true;
            }
            catch { return false; }
        });

        /// <summary>
        /// removes from the clipboard history (Win+V) the items with this text created from <paramref name="since"/> on: what the remote PC copies
        /// for Claude is put there by FreeRDP, which does not know about the "out of history" flags. History off or unreadable = nothing to do
        /// </summary>
        public static async Task ForgetHistoryAsync(string text, DateTimeOffset since)
        {
            try
            {
                var r = await Windows.ApplicationModel.DataTransfer.Clipboard.GetHistoryItemsAsync();
                if (r.Status != Windows.ApplicationModel.DataTransfer.ClipboardHistoryItemsResultStatus.Success) return;
                foreach (var item in r.Items)
                {
                    if (item.Timestamp < since || !item.Content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)) continue;
                    if (await item.Content.GetTextAsync() == text) Windows.ApplicationModel.DataTransfer.Clipboard.DeleteItemFromHistory(item);
                }
            }
            catch (Exception e) { Shawn.Utils.SimpleLogHelper.Warning($"MCP: clipboard history not cleaned ({e.Message})"); }
        }

        /// <summary>text on this PC's clipboard, only if FreeRDP put it there (copied on the remote PC)</summary>
        public static string? GetFromRemote(IntegrateHost host) => OnUiSync(() =>
        {
            try { return host.McpClipboardFromRemote() && Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch { return null; }
        });

        private static string? GetAny() => OnUiSync(() =>
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch { return null; }
        });

        /// <summary>clipboard held by another program: retry a few times</summary>
        private static void Retry(Action a)
        {
            for (var i = 0; ; i++)
            {
                try { a(); return; }
                catch (COMException) when (i < 8) { Thread.Sleep(60); }
                catch (ExternalException) when (i < 8) { Thread.Sleep(60); }
            }
        }

        private static T OnUiSync<T>(Func<T> f)
        {
            if (Application.Current?.Dispatcher.CheckAccess() != false) return f();
            T r = default!;
            Execute.OnUIThreadSync(() => r = f());
            return r;
        }

        // ---------------------------------------------------------------- operations by session type

        /// <summary>
        /// puts <paramref name="text"/> on the remote PC's clipboard. RDP: the backup must be restored with Restore once the remote PC
        /// has read it (after the paste). Error = text with the reason.
        /// </summary>
        public static async Task<string?> PutAsync(IMcpScreen s, string text, Backup? backup)
        {
            switch (s)
            {
                case VncHost v:
                    if (Latin1Problem(text) is { } bad) return bad;
                    return await OnUi(() => v.McpSetRemoteClipboard(text)) ? null : "the VNC session is not connected";
                case IntegrateHost h:
                    if (backup == null) return "the backup copy of the clipboard is missing";
                    if (!SetLocal(text, backup)) return "this PC's clipboard is held by another program: try again";
                    // FreeRDP (SDL) notices the change on its next message loop pass and announces it to the remote PC: wake it up right away
                    for (var i = 0; i < 3; i++) { await OnUi(() => { h.McpNudge(); return true; }); await Task.Delay(150); }
                    return null;
                default:
                    return "this session has no clipboard";
            }
        }

        /// <summary>RFB passes only Latin-1 characters: with other characters it reports which ones (null = fine)</summary>
        public static string? Latin1Problem(string text)
        {
            foreach (var c in text)
                if (c > 0xFF) return $"the character '{c}' cannot pass through the VNC clipboard (only Latin letters, digits and common symbols): type it separately or replace it";
            return null;
        }

        /// <summary>
        /// runs <paramref name="trigger"/> and waits for the first text the remote PC copies to the clipboard (that satisfies <paramref name="match"/>).
        /// VNC: the text does not go to this PC's clipboard. RDP: it has to pass through it (FreeRDP puts it there); the caller then restores the backup.
        /// </summary>
        /// <param name="anyOwner">RDP: accepts the text even if FreeRDP did not put it on the clipboard. Only when <paramref name="match"/> recognizes a marker
        /// that can come only from the remote PC (esegui_comando): so Claude is never given what the user copied on their own PC</param>
        /// <param name="halted">true = the user pressed Pause/Stop/Stop Claude: stop waiting (and the caller discards the text)</param>
        public static async Task<string?> WaitRemoteAsync(IMcpScreen s, Action trigger, int timeoutMs, Func<string, bool>? match = null, bool anyOwner = false, Func<bool>? halted = null)
        {
            switch (s)
            {
                case VncHost v:
                    return await OnUiTask(() => v.CaptureServerClipboardAsync(trigger, timeoutMs, match));
                case IntegrateHost h:
                    {
                        var before = Sequence();
                        await OnUi(() => { trigger(); return true; });
                        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                        while (DateTime.UtcNow < until && halted?.Invoke() != true)
                        {
                            await Task.Delay(150);
                            await OnUi(() => { h.McpNudge(); return true; });   // FreeRDP runs and puts on the clipboard what the remote PC copied
                            if (Sequence() == before) continue;
                            var t = anyOwner && match != null ? GetAny() : GetFromRemote(h);
                            if (t != null && (match == null || match(t))) return t;
                        }
                        return null;
                    }
                default:
                    return null;
            }
        }

        /// <summary>the last text copied on the remote PC without pressing anything (null = none, or this PC's clipboard belongs to the user)</summary>
        public static Task<string?> LastRemoteAsync(IMcpScreen s) => s switch
        {
            VncHost v => OnUi(v.McpLastRemoteClipboard),
            IntegrateHost h => LastRdpAsync(h),
            _ => Task.FromResult<string?>(null),
        };

        /// <summary>FreeRDP puts on this PC's clipboard what the remote PC copied only when its loop runs (and late on a slow link): wake it up and retry for ~2 s</summary>
        private static async Task<string?> LastRdpAsync(IntegrateHost h)
        {
            for (var i = 0; i < 14; i++)
            {
                if (GetFromRemote(h) is { } t) return t;
                await OnUi(() => { h.McpNudge(); return true; });
                await Task.Delay(150);
            }
            return GetFromRemote(h);
        }

        private static Task<T> OnUi<T>(Func<T> f)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Execute.OnUIThread(() => { try { tcs.SetResult(f()); } catch (Exception e) { tcs.SetException(e); } });
            return tcs.Task;
        }

        private static Task<T> OnUiTask<T>(Func<Task<T>> f)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Execute.OnUIThread(async () => { try { tcs.SetResult(await f()); } catch (Exception e) { tcs.SetException(e); } });
            return tcs.Task;
        }
    }
}
