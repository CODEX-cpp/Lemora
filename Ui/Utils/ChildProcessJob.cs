using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Shawn.Utils;

namespace Lemora.Utils
{
    /// <summary>
    /// Child programs that must die with Lemora (FreeRDP): they live in a Windows "job" that is closed when the process ends,
    /// even if Lemora is killed abruptly. Without it, a FreeRDP stayed running and connected to the remote PC with no window.
    /// </summary>
    public static class ChildProcessJob
    {
        private static IntPtr _job = IntPtr.Zero;
        private static readonly object Lock = new();

        public static void Add(Process p)
        {
            try
            {
                lock (Lock)
                {
                    if (_job == IntPtr.Zero)
                    {
                        _job = CreateJobObject(IntPtr.Zero, null);
                        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                        info.BasicLimitInformation.LimitFlags = 0x2000;   // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                        var ptr = Marshal.AllocHGlobal(size);
                        try
                        {
                            Marshal.StructureToPtr(info, ptr, false);
                            if (!SetInformationJobObject(_job, 9 /* JobObjectExtendedLimitInformation */, ptr, (uint)size))
                                SimpleLogHelper.Warning($"ChildProcessJob: setup failed ({Marshal.GetLastWin32Error()})");
                        }
                        finally { Marshal.FreeHGlobal(ptr); }
                    }
                    if (!AssignProcessToJobObject(_job, p.Handle))
                        SimpleLogHelper.Warning($"ChildProcessJob: {p.ProcessName} not bound to Lemora ({Marshal.GetLastWin32Error()})");
                }
            }
            catch (Exception e) { SimpleLogHelper.Warning("ChildProcessJob: " + e.Message); }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr a, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS { public ulong R, W, O, RB, WB, OB; }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
    }
}
