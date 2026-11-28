using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Lemora.Service.Mcp
{
    /// <summary>
    /// Lemora.exe started with <see cref="Arg"/>: the process where one script of Claude runs (<see cref="McpScript"/>). No window, no database, no network:
    /// it reads the code from its input, runs it in Jint and asks Lemora for every tool call through its output (one line of JSON per message).
    /// Its memory has a ceiling (.NET heap <see cref="HeapLimitMb"/>, whole process <see cref="ProcessLimitMb"/> through a Windows job): a script that
    /// builds huge data ends here, Lemora's memory does not grow. The job also ends the process if Lemora closes
    /// </summary>
    internal static class ScriptChild
    {
        public const string Arg = "--lemora-script-host";
        public const int HeapLimitMb = 768;
        public const int ProcessLimitMb = 1536;

        public static bool IsLaunch(string[] args) => args.Length == 1 && args[0] == Arg;

        public static int Run()
        {
            var utf8 = new UTF8Encoding(false);
            var input = new StreamReader(Console.OpenStandardInput(), utf8);
            var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true, NewLine = "\n" };
            void Send(JObject m) { lock (output) output.WriteLine(m.ToString(Formatting.None)); }
            string Ask(JObject m)
            {
                Send(m);
                var answer = JObject.Parse(input.ReadLine() ?? throw new McpScript.ScriptFault("stopped: Lemora ended the script"));
                if (answer.Value<string>("fault") is { } fault) throw new McpScript.ScriptFault(fault);
                return answer.Value<string>("r") ?? "";
            }

            JObject start;
            try { start = JObject.Parse(input.ReadLine() ?? "{}"); }
            catch (JsonException) { return 2; }   // not started by Lemora
            JObject end;
            try
            {
                var tools = (start["tools"] as JArray)?.Select(t => t.ToString()).ToList() ?? new();
                var engine = McpScript.NewEngine(CancellationToken.None,   // the duration and the stops are Lemora's: it ends this process
                    (name, args) => Ask(new JObject { ["t"] = "call", ["name"] = name, ["args"] = args }),
                    s => Send(new JObject { ["t"] = "log", ["text"] = s }),
                    ms => Thread.Sleep(TimeSpan.FromMilliseconds(Math.Clamp(ms, 0, 55_000))),
                    h => Ask(new JObject { ["t"] = "allega", ["h"] = h }),
                    tools);
                end = new JObject { ["t"] = "end", ["result"] = McpScript.RunCode(engine, start.Value<string>("code") ?? "") };
            }
            catch (Exception e)
            {
                end = new JObject { ["t"] = "end", ["error"] = McpScript.Cut(McpScript.EngineError(e), 2000) };
            }
            try { Send(end); } catch (IOException) { /* Lemora is gone */ }
            return 0;
        }

        /// <summary>a Windows job: memory ceiling of the process, ended together with Lemora, no error dialog if it dies</summary>
        internal sealed class Job : IDisposable
        {
            private IntPtr _h;
            private int _limitMb;

            public static Job Create(int limitMb)
            {
                var job = new Job { _h = CreateJobObject(IntPtr.Zero, null), _limitMb = limitMb };
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                // PROCESS_MEMORY | DIE_ON_UNHANDLED_EXCEPTION | KILL_ON_JOB_CLOSE
                info.BasicLimitInformation.LimitFlags = 0x100 | 0x400 | 0x2000;
                info.ProcessMemoryLimit = (UIntPtr)((ulong)limitMb * 1024 * 1024);
                if (job._h == IntPtr.Zero || !SetInformationJobObject(job._h, 9, ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                    Shawn.Utils.SimpleLogHelper.Warning($"MCP: memory ceiling of the script process not set (error {Marshal.GetLastWin32Error()}): only the .NET heap limit applies");
                return job;
            }

            public void Assign(System.Diagnostics.Process p)
            {
                if (_h != IntPtr.Zero && !AssignProcessToJobObject(_h, p.Handle))
                    Shawn.Utils.SimpleLogHelper.Warning($"MCP: script process not put in its job (error {Marshal.GetLastWin32Error()}): only the .NET heap limit applies");
            }

            /// <summary>the process reached the ceiling (Windows refused it more memory)</summary>
            public bool HitMemoryLimit
            {
                get
                {
                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    return _h != IntPtr.Zero && QueryInformationJobObject(_h, 9, ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(), IntPtr.Zero)
                           && (ulong)info.PeakProcessMemoryUsed >= (ulong)_limitMb * 1024 * 1024 * 95 / 100;
                }
            }

            public void Dispose()
            {
                if (_h != IntPtr.Zero) CloseHandle(_h);   // KILL_ON_JOB_CLOSE: a process still alive ends here
                _h = IntPtr.Zero;
            }

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
            private struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }

            [StructLayout(LayoutKind.Sequential)]
            private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
                public IO_COUNTERS IoInfo;
                public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
            }

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
            [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);
            [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length, IntPtr returned);
            [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
            [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
        }
    }
}
