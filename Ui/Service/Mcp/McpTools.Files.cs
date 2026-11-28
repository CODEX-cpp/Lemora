using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lemora.Service.FileTransfer;
using Lemora.Utils;
using Lemora.View.Host.ProtocolHosts;
using Newtonsoft.Json.Linq;
using Stylet;

namespace Lemora.Service.Mcp
{
    /// <summary>
    /// File transfer for Claude: files and folders between this PC and the remote PC.
    /// - RDP (FreeRDP): through the clipboard, like a person with Ctrl+C / Ctrl+V (Lemora's FreeRDP copies files in both directions,
    ///   patch freerdp-zz-appunti-file-windows). invia_file puts the files on this PC's clipboard and presses Ctrl+V in the folder open on the
    ///   remote PC; prendi_file presses Ctrl+C on the files selected there and copies them into the chosen folder.
    /// - VNC: TightVNC's file transfer (FileTransferManager), with the remote folder or paths given by Claude.
    /// Which files and which folder of this PC: the user says it to Claude in the chat. Every transfer asks the user, every time.
    /// </summary>
    public static partial class McpTools
    {
        /// <summary>at most this much per transfer (500 MB is already a lot through a remote session)</summary>
        private const long MaxTransferBytes = 500L * 1024 * 1024;

        private static JObject SendFilesTool => Tool("invia_file",
            "Copies files or folders of THIS PC (where Lemora runs) to the remote PC. Which files: ask the user in the chat (full paths). " +
            "Lemora asks the user to confirm before every send. RDP: first open the destination folder on the remote PC, in File Explorer, and click " +
            "on an empty spot of the folder: Lemora puts the files on the clipboard and presses Ctrl+V there; then check with schermata that they arrived. " +
            "VNC: give cartella_remota.",
            new JObject
            {
                ["sessione"] = new JObject { ["type"] = "string", ["description"] = "session identifier" },
                ["percorsi"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" }, ["description"] = "files or folders of this PC, full paths (e.g. C:\\Users\\Name\\Documents\\offer.pdf)" },
                ["cartella_remota"] = new JObject { ["type"] = "string", ["description"] = "VNC only: folder of the remote PC to copy them to (e.g. C:\\Users\\Administrator\\Desktop)" },
            }, "sessione", "percorsi");

        private static JObject GetFilesTool => Tool("prendi_file",
            "Copies files or folders from the remote PC into a folder of THIS PC. Which folder: ask the user in the chat. " +
            "Lemora asks the user to confirm before every copy. RDP: first select the files on the remote PC in File Explorer: Lemora presses Ctrl+C and copies them. " +
            "VNC: give percorsi_remoti. Files with the same name already there are not overwritten (name with a number).",
            new JObject
            {
                ["sessione"] = new JObject { ["type"] = "string", ["description"] = "session identifier" },
                ["cartella_locale"] = new JObject { ["type"] = "string", ["description"] = "folder of this PC to put them in, full path (created if missing)" },
                ["percorsi_remoti"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" }, ["description"] = "VNC only: files or folders of the remote PC, full paths" },
            }, "sessione", "cartella_locale");

        private static List<string> Strings(JToken? t) => t switch
        {
            JArray arr => arr.Select(x => x.Type == JTokenType.String ? (string?)x : null).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()).ToList(),
            JValue { Type: JTokenType.String } v when !string.IsNullOrWhiteSpace((string?)v) => new List<string> { ((string)v!).Trim() },
            _ => new List<string>(),
        };

        /// <summary>the user decides every transfer (hidden tests: LEMORA_MCP_FILE_APPROVE=1, only in Debug builds)</summary>
        private static bool ConfirmTransfer(string text)
        {
            if (DevOnly.Env("LEMORA_MCP_FILE_APPROVE") == "1") return true;
            var ok = false;
            Execute.OnUIThreadSync(() => ok = MessageBoxHelper.Confirm(text, IoC.Translate("mcp_ft_allow"), IoC.Translate("mcp_ft_deny"), IoC.Translate("mcp_ft_title"), defaultNo: true));
            return ok;
        }

        private static (int count, long bytes) Measure(IEnumerable<string> paths)
        {
            int n = 0; long b = 0;
            foreach (var p in paths)
            {
                if (File.Exists(p)) { n++; b += new FileInfo(p).Length; }
                else if (Directory.Exists(p))
                    foreach (var f in Directory.EnumerateFiles(p, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                    { n++; try { b += new FileInfo(f).Length; } catch { } }
            }
            return (n, b);
        }

        private static string ListForUser(IReadOnlyList<string> items)
            => string.Join("\n", items.Take(10).Select(p => "  " + p)) + (items.Count > 10 ? "\n  " + string.Format(IoC.Translate("mcp_ft_and_more"), items.Count - 10) : "");

        // ---------------------------------------------------------------- invia_file

        private static async Task<JObject> SendFiles(string connectionId, List<string> paths, string? remoteDir)
        {
            var (s, error) = await VncSession(connectionId, "send files");
            if (s == null) return Fail(error);
            if (paths.Count == 0) return Fail("no file given: ask the user which files to send (full paths)");
            var full = new List<string>();
            foreach (var p in paths)
            {
                if (!Path.IsPathFullyQualified(p)) return Fail($"path not complete: {p} (for example C:\\folder\\file.txt is needed)");
                var f = Path.GetFullPath(p);
                if (!File.Exists(f) && !Directory.Exists(f)) return Fail($"does not exist on this PC: {f}");
                full.Add(f);
            }
            var (count, bytes) = Measure(full);
            if (bytes > MaxTransferBytes) return Fail($"too large: {FileTransferManager.Fmt(bytes)} (at most {FileTransferManager.Fmt(MaxTransferBytes)} at a time)");
            var who = ActivityLog.Describe(s.ProtocolServer);
            var vnc = s as VncHost;
            if (vnc != null && string.IsNullOrWhiteSpace(remoteDir)) return Fail("VNC: give cartella_remota (the folder of the remote PC to copy them to)");

            if (!ConfirmTransfer(string.Format(IoC.Translate("mcp_ft_send_confirm"), who, ListForUser(full), count, FileTransferManager.Fmt(bytes)) +
                                 (vnc != null ? "\n" + string.Format(IoC.Translate("mcp_ft_send_remote_dir"), remoteDir) : "\n" + IoC.Translate("mcp_ft_send_paste_here"))))
            {
                McpGuard.Log($"sending files to {who} refused by the user");
                return Fail("the user refused to send the files");
            }
            if (McpGuard.Halted(s.ProtocolServer) is { } halted) return Fail($"interrupted: {halted}");

            if (vnc != null)
            {
                var ft = await OnUi(() => vnc.FileTransfer);
                if (ft == null) return Fail("file transfer not available on this VNC session");
                TransferJob job;
                try { job = ft.EnqueueUpload(full, RemotePath.FromWindows(remoteDir)); }
                catch (ArgumentException e) { return Fail(e.Message); }
                var result = await WaitJob(job);
                McpGuard.Log($"sends {count} files ({FileTransferManager.Fmt(bytes)}) to {who} in {remoteDir}: {job.State}");
                return result;
            }

            // RDP: like a person, Ctrl+V in the folder open on the remote PC. Only this session's FreeRDP sees the clipboard meanwhile:
            // the other open sessions must not offer these files to their servers
            if (s is not IntegrateHost ih) return Fail("session not suitable for file transfer");
            if (!TryChord("ctrl+v", out var mods, out var key)) return Fail("keys not recognized");
            using (await McpClipboard.LockAsync())
            {
                using var otherRdp = ih.McpClipboardOnlyForThisSession();
                var backup = McpClipboard.Save();
                try
                {
                    if (!await OnUi(() => FileTransferShell.SetClipboardFiles(full))) return Fail("cannot put the files on this PC's clipboard");
                    for (var i = 0; i < 3; i++) { await OnUi(() => { ih.McpNudge(); return true; }); await Task.Delay(150); }
                    if (McpGuard.Halted(s.ProtocolServer) is { } h2) return Fail($"interrupted before pasting: {h2}");
                    await OnUi(() => { s.SendChord(mods, key); return true; });
                    // shortcut: there is no signal when the remote PC has finished reading the files; the clipboard stays with them for a time
                    // proportional to the size (1 MB/s at worst, 5 s to 10 min), then the user's clipboard comes back. Upgrade: a notice from FreeRDP
                    var seconds = (int)Math.Clamp(5 + bytes / (1024 * 1024), 5, 600);
                    for (var t = 0; t < seconds * 4 && McpGuard.Halted(s.ProtocolServer) == null; t++)
                    {
                        if (t % 4 == 0) await OnUi(() => { ih.McpNudge(); return true; });
                        await Task.Delay(250);
                    }
                }
                finally { McpClipboard.RestoreAfterFiles(backup, full); }
            }
            McpGuard.Log($"sends {count} files ({FileTransferManager.Fmt(bytes)}) to {who} with Ctrl+V");
            return Text($"Ctrl+V sent to the remote PC with {count} files ({FileTransferManager.Fmt(bytes)}): Windows copies them into the open folder. " +
                        "Check with schermata that they arrived (with many files the copy on the remote PC may still take a while). " +
                        "This PC's clipboard is back as the user had it.");
        }

        // ---------------------------------------------------------------- prendi_file

        private static async Task<JObject> GetFiles(string connectionId, string localDir, List<string> remotePaths)
        {
            var (s, error) = await VncSession(connectionId, "get files");
            if (s == null) return Fail(error);
            if (string.IsNullOrWhiteSpace(localDir) || !Path.IsPathFullyQualified(localDir))
                return Fail("give cartella_locale with the full path (ask the user where to put the files)");
            var dest = Path.GetFullPath(localDir);
            var who = ActivityLog.Describe(s.ProtocolServer);

            if (s is VncHost vnc)
            {
                if (remotePaths.Count == 0) return Fail("VNC: give percorsi_remoti (files or folders of the remote PC)");
                var ft = await OnUi(() => vnc.FileTransfer);
                if (ft == null) return Fail("file transfer not available on this VNC session");
                var entries = new List<RemoteEntry>();
                foreach (var p in remotePaths)
                {
                    var wire = RemotePath.FromWindows(p);
                    List<RemoteEntry> list;
                    try { list = await ft.ListAsync(RemotePath.Parent(wire)); }
                    catch (FileTransferException e) { return Fail($"cannot read the folder of {p}: {e.Message}"); }
                    var e2 = list.FirstOrDefault(x => string.Equals(x.Name, RemotePath.NameOf(wire), StringComparison.OrdinalIgnoreCase));
                    if (e2 == null) return Fail($"does not exist on the remote PC: {p}");
                    entries.Add(e2);
                }
                var size = entries.Where(x => !x.IsDirectory).Sum(x => x.Size);
                if (!ConfirmTransfer(string.Format(IoC.Translate("mcp_ft_get_confirm"), who, dest, ListForUser(remotePaths)) +
                                     (entries.Any(x => x.IsDirectory) ? "\n\n" + IoC.Translate("mcp_ft_get_with_folders") : $"\n\n{FileTransferManager.Fmt(size)}.")))
                {
                    McpGuard.Log($"copy of files from {who} refused by the user");
                    return Fail("the user refused to copy the files");
                }
                if (McpGuard.Halted(s.ProtocolServer) is { } halted) return Fail($"interrupted: {halted}");
                var job = ft.EnqueueDownload(entries, dest);
                var result = await WaitJob(job);
                McpGuard.Log($"gets {entries.Count} items from {who} into {dest}: {job.State}");
                return result;
            }

            // RDP: Ctrl+C on the files selected on the remote PC; Lemora's FreeRDP offers them on this PC's clipboard and downloads them when read
            if (s is not IntegrateHost ih) return Fail("session not suitable for file transfer");
            if (!TryChord("ctrl+c", out var mods, out var key)) return Fail("keys not recognized");
            if (!ConfirmTransfer(string.Format(IoC.Translate("mcp_ft_get_selected_confirm"), who, dest)))
            {
                McpGuard.Log($"copy of files from {who} refused by the user");
                return Fail("the user refused to copy the files");
            }
            List<string> copied;
            using (await McpClipboard.LockAsync())
            {
                using var otherRdp = ih.McpClipboardOnlyForThisSession();
                var backup = McpClipboard.Save();
                try
                {
                    if (McpGuard.Halted(s.ProtocolServer) is { } h1) return Fail($"interrupted: {h1}");
                    await OnUi(() => { s.SendChord(mods, key); return true; });
                    // the copy arrives when FreeRDP owns this PC's clipboard with files (at most 10 s)
                    var has = false;
                    for (var t = 0; t < 40 && !has && McpGuard.Halted(s.ProtocolServer) == null; t++)
                    {
                        await OnUi(() => { ih.McpNudge(); return true; });
                        await Task.Delay(250);
                        has = await OnUi(() => ih.McpClipboardFromRemote() && FileTransferShell.ClipboardHasFiles());
                    }
                    if (!has) return Fail("after Ctrl+C on the remote PC no files arrived within 10 s: first select the files in the remote PC's File Explorer (click, Ctrl+click for several files) and try again");
                    // reading the list downloads the files (FreeRDP, while the read waits): on its own thread, the interface does not freeze
                    var paths = await StaThread(FileTransferShell.GetLocalPathsFromClipboard);
                    if (paths.Count == 0) return Fail("the remote PC's files were not downloaded (see Lemora's log)");
                    Directory.CreateDirectory(dest);
                    copied = paths.Select(p => CopyInto(p, dest)).ToList();
                }
                finally { McpClipboard.Restore(backup, ih); }
            }
            var (count, bytes) = Measure(copied);
            McpGuard.Log($"gets {count} files ({FileTransferManager.Fmt(bytes)}) from {who} into {dest}");
            return Text($"Copied into {dest}: {count} files, {FileTransferManager.Fmt(bytes)}.\n" + string.Join("\n", copied.Select(p => "  " + p)));
        }

        /// <summary>copies a file or folder into dir without overwriting: an existing name gets a number (like File Explorer)</summary>
        internal static string CopyInto(string source, string dir)
        {
            var name = Path.GetFileName(source.TrimEnd('\\', '/'));
            var target = Path.Combine(dir, name);
            for (var n = 2; File.Exists(target) || Directory.Exists(target); n++) target = Path.Combine(dir, RemotePath.NumberedName(name, n));
            if (File.Exists(source)) File.Copy(source, target);
            else CopyDir(source, target);
            return target;
        }

        private static void CopyDir(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var f in Directory.GetFiles(source)) File.Copy(f, Path.Combine(target, Path.GetFileName(f)));
            foreach (var d in Directory.GetDirectories(source))
                if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0) CopyDir(d, Path.Combine(target, Path.GetFileName(d)));
        }

        /// <summary>clipboard reads that can take long (downloads) on their own STA thread</summary>
        private static Task<T> StaThread<T>(Func<T> f)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var t = new Thread(() => { try { tcs.SetResult(f()); } catch (Exception e) { tcs.SetException(e); } }) { IsBackground = true, Name = "Claude-file" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            return tcs.Task;
        }

        /// <summary>VNC transfer: waits up to 50 s, then says how it went (or that it goes on in the transfer window)</summary>
        private static async Task<JObject> WaitJob(TransferJob job)
        {
            var done = await Task.WhenAny(job.Completion, Task.Delay(50_000)) == job.Completion;
            if (!done) return Text($"transfer still in progress ({job.Progress:P0}): it goes on by itself, it is shown in Lemora's transfers window; check again shortly");
            return job.State switch
            {
                TransferState.Completed => Text($"transfer completed: {job.Title}"),
                TransferState.CompletedWithSkips => Text($"transfer completed with skipped items: {job.Title}"),
                TransferState.Cancelled => Fail("transfer cancelled"),
                _ => Fail($"transfer failed: {job.Error?.Message ?? "unknown error"}"),
            };
        }
    }
}
