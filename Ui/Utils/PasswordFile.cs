using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shawn.Utils;

namespace Lemora.Utils
{
    /// <summary>
    /// Password for PuTTY via -pwfile instead of -pw: on the command line it ended up in process creation logs (event 4688, Sysmon, EDR)
    /// and every program of the user could see it (security hardening). The file sits in the user's temp folder (only the user and administrators),
    /// PuTTY reads it at startup and it is deleted here after a few seconds; any left behind by a sudden exit are removed at the next startup
    /// </summary>
    public static class PasswordFile
    {
        private const string Prefix = "lemora-pw-";

        public static string Create(string password, int deleteAfterSeconds = 15)
        {
            var path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N") + ".tmp");
            using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var w = new StreamWriter(fs)) w.Write(password);
            _ = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(deleteAfterSeconds)); Delete(path); });
            return path;
        }

        public static void Delete(string path)
        {
            try { if (File.Exists(path)) { File.WriteAllBytes(path, new byte[64]); File.Delete(path); } }
            catch (Exception e) { SimpleLogHelper.Warning("password file not deleted: " + e.Message); }
        }

        /// <summary>at startup: password files left behind by a sudden exit</summary>
        public static void CleanLeftovers()
        {
            try
            {
                foreach (var f in Directory.GetFiles(Path.GetTempPath(), Prefix + "*.tmp").Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddMinutes(-1)))
                    Delete(f);
            }
            catch { /* temp folder not readable */ }
        }
    }
}
