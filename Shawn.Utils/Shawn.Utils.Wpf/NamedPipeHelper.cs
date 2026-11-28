using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace Shawn.Utils.Wpf
{
    /// <summary>
    /// Single instance: the first copy opens a pipe, the others send it their arguments (.mora files, commands) and close.
    /// Pipes are visible to all users of the PC (even on a terminal server): the pipe is open only to the user,
    /// the first instance is exclusive (another user cannot have created it first) and each side checks that the other is the same user.
    /// </summary>
    public class NamedPipeHelper : IDisposable
    {
        private readonly string _pipName;
        private readonly Mutex? _singleAppMutex = null;
        private readonly CancellationTokenSource _cancellationTokenSource = new();

        public delegate void OnMessageReceivedDelegate(string message);

        public OnMessageReceivedDelegate? OnMessageReceived;

        public NamedPipeHelper(string pipName)
        {
            this._pipName = pipName;
            _singleAppMutex = new Mutex(true, pipName, out var isFirst);
            IsServer = isFirst;
            if (isFirst)
                StartNamedPipeServer();
        }

        public bool IsServer { get; }

        private static string UserSid => WindowsIdentity.GetCurrent().User?.Value ?? "";

        public void NamedPipeSendMessage(string message)
        {
            // Identification: the server can know who we are but not act on our behalf
            var client = new NamedPipeClientStream(".", _pipName, PipeDirection.Out, PipeOptions.None, TokenImpersonationLevel.Identification);
            try
            {
                client.Connect(1 * 1000);
                // pipe created by another user with the same name: file paths and arguments are not sent to it
                if (!GetNamedPipeServerProcessId(client.SafePipeHandle.DangerousGetHandle(), out var pid) || !ProcessIsMine(pid))
                    throw new IOException("the pipe of the running instance does not belong to this user");
                var writer = new StreamWriter(client);
                writer.WriteLine(message);
                writer.Flush();
            }
            finally
            {
                client.Close();
                client.Dispose();
            }
        }

        private void StartNamedPipeServer()
        {
            Task.Factory.StartNew(() =>
            {
                while (!_cancellationTokenSource.IsCancellationRequested)
                {
                    NamedPipeServerStream? server = null;
                    try
                    {
                        var sec = new PipeSecurity();
                        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(UserSid), PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
                        // one instance at a time, always exclusive: if another user has taken the name in the meantime, creation fails
                        server = NamedPipeServerStreamAcl.Create(_pipName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.FirstPipeInstance, 0, 0, sec);
                        server.WaitForConnection();
                        if (!GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out var pid) || !ProcessIsMine(pid))
                        {
                            SimpleLogHelper.Warning("Single instance: message from another user's process refused");
                            continue;
                        }
                        var reader = new StreamReader(server);
                        var line = reader.ReadLine();
                        if (string.IsNullOrEmpty(line) == false && line.Length <= 64 * 1024)
                            OnMessageReceived?.Invoke(line);
                    }
                    catch (Exception e)
                    {
                        SimpleLogHelper.Warning("Single instance: pipe not available (" + e.Message + "), retrying in 5 s");
                        Thread.Sleep(5000);
                    }
                    finally { server?.Dispose(); }
                }
            }, _cancellationTokenSource.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public void Dispose()
        {
            if (_cancellationTokenSource.IsCancellationRequested == false)
            {
                _cancellationTokenSource.Cancel(false);
            }
            _singleAppMutex?.Dispose();
            _cancellationTokenSource?.Dispose();
        }

        // ---- who owns the process on the other side of the pipe
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr info, int len, out int retLen);
        [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
        [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

        private static int IntegrityRid(IntPtr tok)
        {
            GetTokenInformation(tok, 25 /* TokenIntegrityLevel */, IntPtr.Zero, 0, out var len);
            if (len <= 0) return -1;
            var buf = Marshal.AllocHGlobal(len);
            try
            {
                if (!GetTokenInformation(tok, 25, buf, len, out _)) return -1;
                var sid = Marshal.ReadIntPtr(buf);
                var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                return count == 0 ? -1 : Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private static bool IsAppContainer(IntPtr tok)
        {
            var buf = Marshal.AllocHGlobal(4);
            try { return !GetTokenInformation(tok, 29 /* TokenIsAppContainer */, buf, 4, out _) || Marshal.ReadInt32(buf) != 0; }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private static bool ProcessIsMine(uint pid)
        {
            if (pid == 0) return false;
            var p = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
            if (p == IntPtr.Zero) return false;
            try
            {
                if (!OpenProcessToken(p, 0x0008 /* TOKEN_QUERY */, out var tok)) return false;
                // same user, not in an AppContainer and at least medium integrity: a user process in a sandbox (low integrity) does not count
                try { using var id = new WindowsIdentity(tok); return id.User?.Value == UserSid && !IsAppContainer(tok) && IntegrityRid(tok) >= 0x2000; }
                finally { CloseHandle(tok); }
            }
            finally { CloseHandle(p); }
        }
    }
}
