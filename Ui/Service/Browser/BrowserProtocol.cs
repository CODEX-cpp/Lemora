using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace Lemora.Service.Browser
{
    /// <summary>
    /// Browser extension (Chrome, Edge, Firefox) for device passwords. The browser launches Lemora.exe as a "native messaging host"
    /// (BrowserHost), which forwards messages to the running Lemora instance (BrowserBridge) over a Windows pipe accessible only to the user.
    /// Messages: 4-byte length (little endian) + UTF-8 JSON, both towards the browser and on the pipe.
    /// </summary>
    internal static class BrowserProtocol
    {
        /// <summary>name by which the browser knows Lemora (native messaging manifest)</summary>
        public const string HostName = "com.lemora.passwords";
        /// <summary>extension id in the Chrome Web Store (also used by Edge); the store's public key is in the development manifest, so a manually loaded copy has this id too</summary>
        public const string ChromeExtensionId = "ipibpbpbnccannndgaackjamgbgnhcdf";
        /// <summary>Firefox extension id (browser_specific_settings.gecko.id)</summary>
        public const string FirefoxExtensionId = "lemora-passwords@lemora.local";
        public const int MaxMessage = 1024 * 1024;   // the browser accepts at most 1 MB towards the extension

        public static string UserSid => WindowsIdentity.GetCurrent().User?.Value ?? "";
        /// <summary>one pipe per user: the name contains the SID, the ACL opens it only to that user</summary>
        public static string PipeName => "Lemora.Browser." + UserSid;

        public static string? Read(Stream s)
        {
            var len = new byte[4];
            if (!ReadExact(s, len)) return null;
            var n = BitConverter.ToInt32(len, 0);
            if (n <= 0 || n > MaxMessage) return null;
            var buf = new byte[n];
            return ReadExact(s, buf) ? Encoding.UTF8.GetString(buf) : null;
        }

        public static void Write(Stream s, string json)
        {
            var b = Encoding.UTF8.GetBytes(json);
            if (b.Length > MaxMessage) b = Encoding.UTF8.GetBytes(new Newtonsoft.Json.Linq.JObject { ["ok"] = false, ["error"] = IoC.Translate("browser_err_reply_too_big") }.ToString(Newtonsoft.Json.Formatting.None));
            s.Write(BitConverter.GetBytes(b.Length), 0, 4);
            s.Write(b, 0, b.Length);
            s.Flush();
        }

        private static bool ReadExact(Stream s, byte[] b)
        {
            int got = 0;
            while (got < b.Length)
            {
                var r = s.Read(b, got, b.Length - got);
                if (r <= 0) return false;
                got += r;
            }
            return true;
        }

        // ---- who owns the process on the other side of the pipe: another user could create a pipe with the same name first

        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        private const uint ProcessQueryLimitedInformation = 0x1000, TokenQuery = 0x0008;

        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snap, ref ProcessEntry e);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snap, ref ProcessEntry e);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry
        {
            public uint dwSize, cntUsage, th32ProcessID; public IntPtr th32DefaultHeapID; public uint th32ModuleID, cntThreads, th32ParentProcessID; public int pcPriClassBase; public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        /// <summary>true if the pipe server is Lemora.exe of this same user (at least medium integrity, not in an AppContainer)</summary>
        public static bool ServerIsMine(NamedPipeClientStream pipe) =>
            GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid) && ProcessIsMine(pid)
            && string.Equals(Path.GetFileName(ProcessInfo(pid).path), "Lemora.exe", StringComparison.OrdinalIgnoreCase);

        public static uint ClientPid(NamedPipeServerStream pipe) =>
            GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid) ? pid : 0;

        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr info, int len, out int retLen);
        [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
        [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
        private const int TokenIntegrityLevel = 25, TokenIsAppContainer = 29;
        /// <summary>SECURITY_MANDATORY_MEDIUM_RID: below it (Low, Untrusted) are sandboxed processes (PDF readers, compromised browsers...)</summary>
        public const int MediumIntegrity = 0x2000;

        /// <summary>token integrity level (label RID), -1 if not readable</summary>
        internal static int IntegrityRid(IntPtr tok)
        {
            GetTokenInformation(tok, TokenIntegrityLevel, IntPtr.Zero, 0, out var len);
            if (len <= 0) return -1;
            var buf = Marshal.AllocHGlobal(len);
            try
            {
                if (!GetTokenInformation(tok, TokenIntegrityLevel, buf, len, out _)) return -1;
                var sid = Marshal.ReadIntPtr(buf);   // TOKEN_MANDATORY_LABEL.Label.Sid
                var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                return count == 0 ? -1 : Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private static bool IsAppContainer(IntPtr tok)
        {
            var buf = Marshal.AllocHGlobal(4);
            try { return !GetTokenInformation(tok, TokenIsAppContainer, buf, 4, out _) || Marshal.ReadInt32(buf) != 0; }   // not readable = treat it as one
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>
        /// true if the process belongs to this same user, is NOT in an AppContainer and has at least medium integrity (if it cannot be read: false).
        /// The same SID is not enough: a user process locked in a low-integrity sandbox could create the pipe first
        /// and receive the passwords typed into forms (security hardening)
        /// </summary>
        public static bool ProcessIsMine(uint pid)
        {
            if (pid == 0) return false;
            var p = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (p == IntPtr.Zero) return false;
            try
            {
                if (!OpenProcessToken(p, TokenQuery, out var tok)) return false;
                try
                {
                    using var id = new WindowsIdentity(tok);
                    return id.User?.Value == UserSid && !IsAppContainer(tok) && IntegrityRid(tok) >= MediumIntegrity;
                }
                finally { CloseHandle(tok); }
            }
            finally { CloseHandle(p); }
        }

        /// <summary>full executable path and process start time ("" if not readable)</summary>
        public static (string path, long started) ProcessInfo(uint pid)
        {
            var p = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (p == IntPtr.Zero) return ("", 0);
            try
            {
                var sb = new StringBuilder(1024); uint n = (uint)sb.Capacity;
                var path = QueryFullProcessImageName(p, 0, sb, ref n) ? sb.ToString() : "";
                return (path, GetProcessTimes(p, out var c, out _, out _, out _) ? c : 0);
            }
            finally { CloseHandle(p); }
        }

        private static uint ParentPid(uint pid)
        {
            var snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return 0;
            try
            {
                var e = new ProcessEntry { dwSize = (uint)Marshal.SizeOf<ProcessEntry>() };
                for (var ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                    if (e.th32ProcessID == pid) return e.th32ParentProcessID;
                return 0;
            }
            finally { CloseHandle(snap); }
        }

        /// <summary>the browsers that can launch the extension (the registry keys written by BrowserBridge.RegisterNativeHost)</summary>
        private static readonly string[] Browsers = { "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "chromium.exe" };

        /// <summary>
        /// Who is on the other side of the pipe: it must be the Lemora host (Lemora.exe, same user) launched by a browser
        /// (Chrome/Edge launch it through cmd.exe). Another user, a script connecting on its own or a program launching the host
        /// do not pass. Returns the browser executable, or null with the reason for the rejection.
        /// shortcut: a program of the same user can still spoof the parent process (PROC_THREAD_ATTRIBUTE_PARENT_PROCESS); against it the confirmed pairing applies
        /// </summary>
        public static string? CallerBrowser(NamedPipeServerStream pipe, out string why)
        {
            var pid = ClientPid(pipe);
            if (!ProcessIsMine(pid)) { why = "process of another user or not readable"; return null; }
            var (exe, started) = ProcessInfo(pid);
            if (!string.Equals(Path.GetFileName(exe), "Lemora.exe", StringComparison.OrdinalIgnoreCase) && !string.Equals(exe, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            { why = "not the Lemora host: " + Path.GetFileName(exe); return null; }
            var child = started;
            for (int i = 0, cur = (int)pid; i < 3; i++)
            {
                var parent = ParentPid((uint)cur);
                if (parent == 0 || !ProcessIsMine(parent)) break;
                var (pexe, pstart) = ProcessInfo(parent);
                if (pexe.Length == 0 || pstart == 0 || pstart > child) break;   // pid reused by a process started later: not the real parent
                var name = Path.GetFileName(pexe);
                if (Array.Exists(Browsers, b => string.Equals(b, name, StringComparison.OrdinalIgnoreCase))) { why = ""; return pexe; }
                if (!string.Equals(name, "cmd.exe", StringComparison.OrdinalIgnoreCase)) break;
                cur = (int)parent; child = pstart;
            }
            why = "the host was not started by a browser";
            return null;
        }
    }
}
