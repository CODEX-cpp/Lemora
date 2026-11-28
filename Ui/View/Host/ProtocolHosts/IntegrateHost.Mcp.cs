using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Lemora.Model.ProtocolRunner;
using Lemora.Service.Mcp;

namespace Lemora.View.Host.ProtocolHosts
{
    /// <summary>
    /// Claude (MCP server) on RDP sessions opened with FreeRDP. Screenshot with PrintWindow and mouse/keyboard via messages sent
    /// to the FreeRDP window: no real mouse or keyboard, the user can keep working. The coordinates are the pixels
    /// of the FreeRDP window, which with dynamic resolution match the remote screen.
    /// </summary>
    public partial class IntegrateHost : IMcpScreen
    {
        internal bool IsFreeRdp => _runner is FreeRdpRunner;

        /// <summary>the FreeRDP window that draws the session (class SDL_app)</summary>
        private IntPtr McpWindow()
        {
            if (!IsFreeRdp || !_freeRdpEverConnected) return IntPtr.Zero;
            lock (_exeHandles) return _exeHandles.FirstOrDefault(IsSessionWindow);
        }

        /// <summary>the keyboard is in this FreeRDP session (for "Key mapping": the keys go to the remote PC only from here)</summary>
        internal bool KeyboardInSession()
        {
            if (!IsFreeRdp) return false;
            int pid;
            try { pid = _process?.Id ?? 0; } catch { return false; }
            if (pid == 0) return false;
            // window with the keyboard of the foreground thread (0): with the FreeRDP window inside Lemora the two threads share the input,
            // so it is a window of the FreeRDP process exactly when the user is typing in the session
            var info = new GuiThreadInfo { cbSize = Marshal.SizeOf<GuiThreadInfo>() };
            if (!GetGUIThreadInfo(0, ref info)) return false;
            var f = info.hwndFocus != IntPtr.Zero ? info.hwndFocus : GetForegroundWindow();
            return f != IntPtr.Zero && GetWindowThreadProcessId(f, out var p) != 0 && p == pid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GuiThreadInfo
        {
            public int cbSize, flags;
            public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
            public int left, top, right, bottom;
        }
        [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
        [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern IntPtr GetForegroundWindow();

        /// <summary>
        /// FreeRDP session window: class SDL_app but NOT the probe window "SdlWindow::query(...)" that SDL creates right at startup
        /// (measured in the sandbox: the probe at +0.3 s, the session with the connection title only once connected)
        /// </summary>
        private static bool IsSessionWindow(IntPtr h)
        {
            if (!IsWindow(h)) return false;
            var sb = new StringBuilder(128);
            GetClassName(h, sb, 128);
            if (sb.ToString() != "SDL_app") return false;
            sb.Clear();
            GetWindowText(h, sb, 128);
            return !sb.ToString().StartsWith("SdlWindow::", StringComparison.Ordinal);
        }

        // ---- FreeRDP connection timings in the log (to compare with the RDP-DIAG lines of the Microsoft component)

        private readonly System.Diagnostics.Stopwatch _freeRdpClock = new();

        private void FreeRdpTime(string what)
            => Shawn.Utils.SimpleLogHelper.Warning($"FREERDP-TIMES({ProtocolServer.DisplayName}): +{_freeRdpClock.ElapsedMilliseconds} ms {what}");

        /// <summary>
        /// after the session is visible: every 250 ms the window is captured and the log records when the first non-black image appears
        /// (= the remote desktop has arrived). At most 30 s; still black after that = one automatic reconnection per tab.
        /// </summary>
        private bool _blackRestartDone;

        private void WatchFirstImage(System.Diagnostics.Process p)
        {
            System.Threading.Tasks.Task.Run(async () =>
            {
                var until = DateTime.Now.AddSeconds(30);
                var captured = false;
                while (DateTime.Now < until)
                {
                    try { if (p.HasExited || !ReferenceEquals(p, _process)) return; } catch { return; }
                    var black = true;
                    try
                    {
                        Stylet.Execute.OnUIThreadSync(() =>
                        {
                            using var b = McpCapture();
                            captured = b != null;
                            black = b == null || LooksBlack(b);
                        });
                    }
                    catch { return; }
                    if (!black) { FreeRdpTime("first image of the remote desktop"); return; }
                    await System.Threading.Tasks.Task.Delay(250);
                }
                FreeRdpTime("no image after 30 s (black screen or not capturable)");
                // Case seen on a real server: connection open, FreeRDP idle, the server sent nothing more (black even after a key);
                // a new connection (mstsc, or reopening) worked. One automatic reconnection per tab, only if the image
                // was really read and is black (not when it simply cannot be captured)
                if (!captured || _blackRestartDone) return;
                _blackRestartDone = true;
                Stylet.Execute.OnUIThread(() =>
                {
                    if (_disposed || !ReferenceEquals(p, _process)) return;
                    Shawn.Utils.SimpleLogHelper.Warning($"FREERDP-TIMES({ProtocolServer.DisplayName}): still black after 30 s, reconnecting once");
                    ReConn();
                });
            });
        }

        private bool HasSessionWindow()
        {
            lock (_exeHandles) return _exeHandles.Any(IsSessionWindow);
        }

        internal Size? McpDesktopSize()
        {
            var h = McpWindow();
            return h != IntPtr.Zero && GetClientRect(h, out var r) && r.Right > 0 ? new Size(r.Right, r.Bottom) : null;
        }

        /// <summary>why the screen cannot be captured (null = it can)</summary>
        internal string? McpUnavailable()
        {
            var h = McpWindow();
            if (h == IntPtr.Zero) return "the session is not connected yet";
            if (SharedFrameReady()) return null;   // image from shared memory: visible or not does not matter
            if (IsIconic(GetAncestor(h, 2))) return "Lemora's window is minimized: ask the user to restore it";
            if (!IsWindowVisible(h)) return "the session is not visible in Lemora (tab not in the foreground or window minimized): ask the user to show it";
            return null;
        }

        /// <summary>the session exists but is hidden or Lemora is minimized: it does not fix itself (unlike "not connected yet")</summary>
        internal bool McpHidden() => McpWindow() is var h && h != IntPtr.Zero && !SharedFrameReady() && (IsIconic(GetAncestor(h, 2)) || !IsWindowVisible(h));

        // ---- image of the session in shared memory, written by our FreeRDP (patch freerdp-zzz-schermo-condiviso, LEMORA_FRAME_SHARE=1):
        // with the tab in the background or Lemora minimized the window is not drawn and PrintWindow gives black, this does not.
        // "Local\LemoraFrameInfo-<pid>": magic, version, width, height, stride, gen (UInt32 each), seq (Int64, odd while FreeRDP writes), frames;
        // "Local\LemoraFrame-<pid>-<gen>": the BGRA pixels. A FreeRDP without the patch does not create them: capture from the window as before

        private System.IO.MemoryMappedFiles.MemoryMappedFile? _shInfo, _shData;
        private System.IO.MemoryMappedFiles.MemoryMappedViewAccessor? _shInfoView, _shDataView;
        private int _shPid;
        private uint _shGen;
        private DateTime _shRetryAt;

        private void CloseShared()
        {
            _shDataView?.Dispose(); _shData?.Dispose(); _shInfoView?.Dispose(); _shInfo?.Dispose();
            _shDataView = null; _shData = null; _shInfoView = null; _shInfo = null; _shGen = 0;
        }

        /// <summary>the header of the shared image is open (FreeRDP with the patch has drawn at least one frame)</summary>
        private bool SharedFrameReady()
        {
            int pid;
            try { pid = _process is { HasExited: false } p ? p.Id : 0; } catch { pid = 0; }
            if (pid == 0) { CloseShared(); return false; }
            if (pid != _shPid) { CloseShared(); _shPid = pid; _shRetryAt = DateTime.MinValue; }
            if (_shInfoView != null) return true;
            if (DateTime.UtcNow < _shRetryAt) return false;   // old FreeRDP: do not try to open it at every capture
            try
            {
                _shInfo = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting($@"Local\LemoraFrameInfo-{pid}", System.IO.MemoryMappedFiles.MemoryMappedFileRights.Read);
                _shInfoView = _shInfo.CreateViewAccessor(0, 40, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
                if (_shInfoView.ReadUInt32(0) != 0x46524D4C) { CloseShared(); _shRetryAt = DateTime.UtcNow.AddSeconds(2); return false; }
                return true;
            }
            catch { CloseShared(); _shRetryAt = DateTime.UtcNow.AddSeconds(2); return false; }
        }

        /// <summary>copy of the shared image (null if not there or always being rewritten)</summary>
        private Bitmap? SharedFrame()
        {
            if (!SharedFrameReady()) return null;
            try
            {
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    var s1 = _shInfoView!.ReadInt64(24);
                    if ((s1 & 1) != 0) { System.Threading.Thread.Sleep(2); continue; }
                    uint w = _shInfoView.ReadUInt32(8), h = _shInfoView.ReadUInt32(12), stride = _shInfoView.ReadUInt32(16), gen = _shInfoView.ReadUInt32(20);
                    if (w is 0 or > 16384 || h is 0 or > 16384 || stride != w * 4 || gen == 0) return null;
                    if (gen != _shGen || _shDataView == null)
                    {
                        _shDataView?.Dispose(); _shData?.Dispose(); _shDataView = null; _shData = null;
                        _shData = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting($@"Local\LemoraFrame-{_shPid}-{gen}", System.IO.MemoryMappedFiles.MemoryMappedFileRights.Read);
                        _shDataView = _shData.CreateViewAccessor(0, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
                        _shGen = gen;
                    }
                    var size = (long)stride * h;
                    if (_shDataView.Capacity < size) return null;
                    var px = new byte[size];
                    _shDataView.ReadArray(0, px, 0, px.Length);
                    if (_shInfoView.ReadInt64(24) != s1) continue;   // rewritten while copying: again
                    var bmp = new Bitmap((int)w, (int)h, PixelFormat.Format32bppRgb);
                    var data = bmp.LockBits(new Rectangle(0, 0, (int)w, (int)h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                    try
                    {
                        for (var y = 0; y < h; y++) Marshal.Copy(px, (int)(y * stride), data.Scan0 + y * data.Stride, (int)stride);
                    }
                    finally { bmp.UnlockBits(data); }
                    return bmp;
                }
            }
            catch (Exception e) { Shawn.Utils.SimpleLogHelper.Warning($"MCP-CAPTURE({ProtocolServer.DisplayName}): shared image not readable ({e.GetType().Name}: {e.Message})"); CloseShared(); }
            return null;
        }

        /// <summary>reason for the last failed capture, only to explain it: it does not block actions (reset only by the next successful capture)</summary>
        internal string? McpCaptureFail() => _mcpCaptureFail is { } why ? $"the session window exists but the screen capture fails ({why}): ask the user to bring the session to the foreground in Lemora" : null;

        /// <summary>the same window measurement McpCapture uses, without capturing it</summary>
        Size? IMcpScreen.McpScreenSize() => McpDesktopSize();

        private string? _mcpCaptureFail;   // last reason the capture failed (null = succeeded)

        /// <summary>remembers the reason and logs it only once until it changes (the capture is retried every 250 ms)</summary>
        private Bitmap? Captured(Bitmap? b, string? why)
        {
            if (why != null && why != _mcpCaptureFail) Shawn.Utils.SimpleLogHelper.Warning($"MCP-CAPTURE({ProtocolServer.DisplayName}): {why}");
            _mcpCaptureFail = why;
            return b;
        }

        public Bitmap? McpCapture()
        {
            var h = McpWindow();
            if (h == IntPtr.Zero) return Captured(null, "FreeRDP window missing");
            if (SharedFrame() is { } shared) return Captured(shared, null);
            if (IsIconic(GetAncestor(h, 2))) return Captured(null, "Lemora's window minimized");
            if (!IsWindowVisible(h)) return Captured(null, "FreeRDP window not visible");
            if (!GetClientRect(h, out var r) || r.Right < 50 || r.Bottom < 50) return Captured(null, $"window area too small ({r.Right}x{r.Bottom})");
            // 1) the FreeRDP window alone (PW_CLIENTONLY | PW_RENDERFULLCONTENT)
            var bmp = new Bitmap(r.Right, r.Bottom, PixelFormat.Format32bppArgb);
            bool ok;
            using (var g = Graphics.FromImage(bmp)) { var dc = g.GetHdc(); ok = PrintWindow(h, dc, 3); g.ReleaseHdc(dc); }
            if (ok && !LooksBlack(bmp)) return Captured(bmp, null);
            bmp.Dispose();
            // 2) a child window drawn with DirectX can come out black: capture the Lemora window that contains it and crop
            var first = ok ? "black capture" : "PrintWindow failed";
            var root = GetAncestor(h, 2);
            if (root == IntPtr.Zero || !GetWindowRect(root, out var rr)) return Captured(null, $"{first}, Lemora's window not found");
            var origin = new POINT();
            if (!ClientToScreen(h, ref origin)) return Captured(null, $"{first}, window position not found");
            using var whole = new Bitmap(rr.Right - rr.Left, rr.Bottom - rr.Top, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(whole)) { var dc = g.GetHdc(); ok = PrintWindow(root, dc, 2); g.ReleaseHdc(dc); }
            if (!ok) return Captured(null, $"{first}, PrintWindow failed on Lemora's window too");
            var crop = new Rectangle(origin.X - rr.Left, origin.Y - rr.Top, r.Right, r.Bottom);
            crop.Intersect(new Rectangle(0, 0, whole.Width, whole.Height));
            if (crop.Width < 50 || crop.Height < 50) return Captured(null, $"{first}, the session is outside Lemora's window");
            var cut = whole.Clone(crop, PixelFormat.Format32bppArgb);
            return Captured(cut, LooksBlack(cut) ? "black capture from Lemora's window too" : null);
        }

        private static bool LooksBlack(Bitmap b)
        {
            for (var i = 1; i < 10; i++)
                for (var j = 1; j < 10; j++)
                {
                    var c = b.GetPixel(b.Width * i / 10, b.Height * j / 10);
                    if (c.R + c.G + c.B > 15) return false;
                }
            return true;
        }

        // ---- mouse

        private byte _mcpMask;

        Point? IMcpScreen.McpToScreen(int x, int y)
        {
            var h = McpWindow();
            var p = new POINT { X = x, Y = y };
            return h != IntPtr.Zero && ClientToScreen(h, ref p) ? new Point(p.X, p.Y) : null;
        }

        void IMcpScreen.McpPointer(byte mask, int x, int y)
        {
            var h = McpWindow();
            if (h == IntPtr.Zero) return;
            McpOverlay.Pointer(this, x, y);
            var lp = (IntPtr)(((y & 0xFFFF) << 16) | (x & 0xFFFF));
            int Mk(byte m) => ((m & 1) != 0 ? 0x1 : 0) | ((m & 4) != 0 ? 0x2 : 0) | ((m & 2) != 0 ? 0x10 : 0);
            PostMessage(h, 0x0200, (IntPtr)Mk(_mcpMask), lp);   // WM_MOUSEMOVE: move there first
            foreach (var (bit, down, up) in new (byte, uint, uint)[] { (1, 0x0201, 0x0202), (4, 0x0204, 0x0205), (2, 0x0207, 0x0208) })
            {
                var was = (_mcpMask & bit) != 0; var now = (mask & bit) != 0;
                if (now && !was) PostMessage(h, down, (IntPtr)Mk(mask), lp);
                else if (!now && was) PostMessage(h, up, (IntPtr)Mk(mask), lp);
            }
            // wheel: one notch per press (as in VNC: pressed then released); WM_MOUSEWHEEL wants screen coordinates
            if ((mask & 24) != 0 && (_mcpMask & 24) == 0)
            {
                var p = new POINT { X = x, Y = y };
                ClientToScreen(h, ref p);
                var delta = (mask & 8) != 0 ? 120 : -120;
                PostMessage(h, 0x020A, (IntPtr)((delta & 0xFFFF) << 16), (IntPtr)(((p.Y & 0xFFFF) << 16) | (p.X & 0xFFFF)));
            }
            _mcpMask = mask;
        }

        // ---- keyboard: X11 keysym -> Windows key -> scan code, which FreeRDP sends to the remote PC

        private static readonly (uint keysym, byte vk)[] NamedKeys =
        {
            (0xff0d, 0x0D), (0xff09, 0x09), (0xff1b, 0x1B), (0xff08, 0x08), (0xffff, 0x2E), (0xff63, 0x2D), (0xff50, 0x24), (0xff57, 0x23),
            (0xff55, 0x21), (0xff56, 0x22), (0xff52, 0x26), (0xff54, 0x28), (0xff51, 0x25), (0xff53, 0x27), (0x20, 0x20), (0xff61, 0x2C), (0xff67, 0x5D),
            (0xffe3, 0xA2), (0xffe9, 0xA4), (0xfe03, 0xA5), (0xffe1, 0xA0), (0xffeb, 0x5B),
        };

        private const byte VkLCtrl = 0xA2, VkLShift = 0xA0, VkRAlt = 0xA5;

        /// <summary>Windows key and required modifiers (capital letters, symbols with Shift or AltGr) according to this PC's keyboard layout</summary>
        private static (byte vk, byte[] mods)? ToVk(uint keysym)
        {
            foreach (var (k, v) in NamedKeys) if (k == keysym) return (v, Array.Empty<byte>());
            if (keysym is >= 0xffbe and <= 0xffc9) return ((byte)(0x70 + keysym - 0xffbe), Array.Empty<byte>());   // F1-F12
            var c = keysym < 0x100 ? (char)keysym : (keysym & 0xff000000) == 0x01000000 ? (char)(keysym & 0xffff) : '\0';
            if (c == '\0') return null;
            var r = VkKeyScanW(c);
            if (r == -1) return null;
            // "." (and "/", "*"...) can come out as a numeric keypad key: on the remote PC with Num Lock off it becomes Del or an arrow
            // (sandbox test: "." sent as 0x53). The same character is looked up on the main keyboard
            if ((r & 0xff) is >= 0x60 and <= 0x6F && MainKey(c) is { } main) r = main;
            var shift = (r >> 8) & 0xff;
            var mods = (shift & 6) == 6 ? new[] { VkLCtrl, VkRAlt } : Array.Empty<byte>();   // Ctrl+Alt = AltGr
            if ((shift & 1) != 0) mods = mods.Append(VkLShift).ToArray();
            return ((byte)(r & 0xff), mods);
        }

        /// <summary>the character on the main keyboard (not the keypad), in the same format as VkKeyScan: key + Shift(1)/Ctrl(2)/Alt(4)</summary>
        private static short? MainKey(char c)
        {
            var state = new byte[256];
            var buf = new System.Text.StringBuilder(4);
            foreach (var mods in new[] { 0, 1, 6, 7 })   // none, Shift, AltGr, Shift+AltGr
                for (uint vk = 0x20; vk <= 0xDF; vk++)
                {
                    if (vk is >= 0x60 and <= 0x6F) continue;
                    Array.Clear(state);
                    if ((mods & 1) != 0) state[0x10] = 0x80;
                    if ((mods & 2) != 0) state[0x11] = 0x80;
                    if ((mods & 4) != 0) state[0x12] = 0x80;
                    buf.Clear();
                    // flag 4: does not touch this program's keyboard state (pending dead keys)
                    if (ToUnicodeEx(vk, (uint)MapVirtualKeyW(vk, 0), state, buf, buf.Capacity, 4, GetKeyboardLayout(0)) == 1 && buf[0] == c)
                        return (short)(vk | (uint)(mods << 8));
                }
            return null;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ToUnicodeEx(uint vk, uint sc, byte[] state, System.Text.StringBuilder buf, int size, uint flags, IntPtr hkl);
        [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint thread);

        private void PostKey(IntPtr h, byte vk, bool pressed)
        {
            var sc = MapVirtualKeyW(vk, 4);   // MAPVK_VK_TO_VSC_EX: 0xE0xx for extended keys (arrows, Del, AltGr...)
            if (vk is 0x2E or 0x2D or 0x24 or 0x23 or 0x21 or 0x22 or 0x26 or 0x28 or 0x25 or 0x27 or 0x5D or 0x5B or VkRAlt) sc |= 0xE000;
            if (vk == 0x2C) sc = 0xE037;   // Print Screen: Windows maps it to 0x54 (SysRq), the key itself sends E0 37

            // bit 25 (reserved by Windows) = "posted by Lemora", on every key: Lemora's SDL patches (sdl3-maiusc-lemora, sdl3-zzzz-tasto-win-lemora) do not
            // "release" Shift and Windows on their own (keys sent this way are not in the Windows keyboard state), and never take these keys
            // for the ones typed again for this PC
            var lp = 1 | ((sc & 0xFF) << 16) | ((sc & 0xFF00) != 0 ? 1 << 24 : 0) | (1 << 25) |
                     (pressed ? 0 : (1 << 30) | unchecked((int)0x80000000));
            PostMessage(h, pressed ? 0x0100u : 0x0101u, (IntPtr)vk, (IntPtr)lp);   // WM_KEYDOWN / WM_KEYUP
        }

        /// <summary>
        /// a Windows key straight to the FreeRDP window (Key mapping). The keys of a forwarded combination all go this way:
        /// FreeRDP reads the posted messages before the keyboard queue, so a key left in the queue arrived after the release of Windows
        /// (sandbox test: server saw Win up before R)
        /// </summary>
        internal void PostVk(int vk, bool pressed)
        {
            var h = McpWindow();
            if (h != IntPtr.Zero) PostKey(h, (byte)vk, pressed);
        }

        public void SendKey(uint keysym, bool pressed)
        {
            var h = McpWindow();
            if (h == IntPtr.Zero || ToVk(keysym) is not { } k) return;
            if (pressed) { foreach (var m in k.mods) PostKey(h, m, true); PostKey(h, k.vk, true); }
            else { PostKey(h, k.vk, false); foreach (var m in k.mods.Reverse()) PostKey(h, m, false); }
        }

        public void SendChord(uint[] modifiers, uint key)
        {
            foreach (var m in modifiers) SendKey(m, true);
            SendKey(key, true); SendKey(key, false);
            foreach (var m in modifiers.Reverse()) SendKey(m, false);
        }

        /// <summary>
        /// this PC's clipboard was set by FreeRDP (= text copied on the remote PC): the clipboard owner is a window of its process.
        /// Used to never give Claude what the user copied on their own PC.
        /// </summary>
        internal bool McpClipboardFromRemote()
        {
            int pid;
            try { pid = _process?.Id ?? 0; } catch { return false; }
            var owner = GetClipboardOwner();
            return pid != 0 && owner != IntPtr.Zero && GetWindowThreadProcessId(owner, out var op) != 0 && op == pid;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();

        /// <summary>
        /// wakes up the FreeRDP (SDL) loop: with a still screen it checks its messages, and so the clipboard, at most once per second.
        /// An empty message (WM_NULL) makes it run right away: the clipboard change reaches the remote PC before Ctrl+V (before, it worked about one time in two)
        /// </summary>
        internal void McpNudge()
        {
            var h = McpWindow();
            if (h != IntPtr.Zero) PostMessage(h, 0x0000, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>the character can be typed with this PC's keyboard layout (otherwise FreeRDP has no way to send it)</summary>
        internal static bool CanType(char c) => c is '\n' or '\t' || VkKeyScanW(c) != -1;

        /// <summary>
        /// while it stays open, only this session's FreeRDP announces and delivers the Windows clipboard to its PC: the other open FreeRDPs
        /// do not (patch sdl3-zzz-appunti-claude). Needed while Claude's text goes through the clipboard (security hardening).
        /// An old FreeRDP without the patch ignores the signals
        /// </summary>
        internal IDisposable McpClipboardOnlyForThisSession()
        {
            int pid = 0;
            try { pid = _process?.Id ?? 0; } catch { /* process already closed */ }
            var hold = new System.Threading.EventWaitHandle(true, System.Threading.EventResetMode.ManualReset, @"Local\LemoraClipHold");
            hold.Set();
            var allow = pid > 0 ? new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.ManualReset, $@"Local\LemoraClipAllow-{pid}") : null;
            return new ClipHold(hold, allow);
        }

        private sealed class ClipHold : IDisposable
        {
            private System.Threading.EventWaitHandle? _hold, _allow;
            public ClipHold(System.Threading.EventWaitHandle hold, System.Threading.EventWaitHandle? allow) { _hold = hold; _allow = allow; }
            public void Dispose() { _hold?.Reset(); _hold?.Dispose(); _allow?.Dispose(); _hold = _allow = null; }
        }

        /// <summary>
        /// the character, typed with this PC's keyboard layout, is a paste for FreeRDP: V key with Ctrl (also inside AltGr, e.g. '@' on Hungarian
        /// or Croatian keyboards) or Ins with Shift. Security hardening.
        /// </summary>
        /// <summary>the "key" combination (modifiers + key) becomes a paste for FreeRDP: Ctrl (also inside AltGr) + V or Shift + Ins</summary>
        internal static bool IsPasteChordVk(uint[] mods, uint key)
        {
            if (ToVk(key) is not { } k) return false;
            bool ctrl = mods.Any(m => m is 0xffe3 or 0xffe4) || k.mods.Contains(VkLCtrl);
            bool shift = mods.Any(m => m is 0xffe1 or 0xffe2) || k.mods.Contains(VkLShift);
            return (k.vk == (byte)'V' && ctrl) || (k.vk == 0x2D && shift);
        }

        internal static bool IsPasteKey(char c)
        {
            if (ToVk(VncKeys.FromChar(c)) is not { } k) return false;
            return (k.vk == (byte)'V' && k.mods.Contains(VkLCtrl)) || (k.vk == 0x2D && k.mods.Contains(VkLShift));
        }

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern short VkKeyScanW(char c);
        [DllImport("user32.dll")] private static extern int MapVirtualKeyW(uint code, uint mapType);
    }
}
