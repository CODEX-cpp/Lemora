using System;
using System.IO;
using System.Text;

namespace Lemora.Service
{
    /// <summary>
    /// One database, only one copy of Lemora open (two instances on the same file during a change
    /// of the master password or the creation of the vault corrupt each other's encrypted data).
    /// The channel between instances (AppStartupHelper) only works for the same user and the same LEMORA_INSTANCE_ID: an installed,
    /// portable or development copy, or another PC on the same shared folder, does not see it. Here the lock sits next to the database:
    /// "&lt;db&gt;.lock" opened for writing without write sharing for the whole life of the process. Windows releases it on its own
    /// if the process dies (no orphan locks); on an SMB share it also works across PCs.
    /// </summary>
    public static class DbInstanceLock
    {
        private static FileStream? _held;
        private static string _heldPath = "";

        public static string LockPath(string dbPath) => Path.GetFullPath(dbPath) + ".lock";

        /// <summary>true if this process has the database all to itself. Same path already taken: true. New path: the old one is released only if the new one succeeds</summary>
        public static bool TryAcquire(string dbPath, out string otherOwner)
        {
            otherOwner = "";
            var path = LockPath(dbPath);
            if (_held != null && string.Equals(path, _heldPath, StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                var info = Encoding.UTF8.GetBytes($"{Environment.MachineName} {Environment.UserName} pid {Environment.ProcessId} {Environment.ProcessPath}");
                fs.SetLength(0); fs.Write(info); fs.Flush(true);
                _held?.Dispose();
                _held = fs; _heldPath = path;
                return true;
            }
            catch (IOException)
            {
                // taken by another copy: read who it is (writing is theirs, reading is shared)
                try
                {
                    using var r = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sr = new StreamReader(r, Encoding.UTF8);
                    otherOwner = sr.ReadToEnd().Trim();
                }
                catch { /* not readable: stays empty */ }
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                // read-only folder: the database permission check will report it
                return true;
            }
        }

        /// <summary>tests only: releases the lock</summary>
        internal static void ReleaseForTests() { _held?.Dispose(); _held = null; _heldPath = ""; }
    }
}
