using System;
using System.IO;
using Shawn.Utils.Wpf.FileSystem;

namespace Lemora.Utils
{
    public static class DbFileAccess
    {
        /// <summary>
        /// Like <see cref="IoPermissionHelper.HasWritePermissionOnFile"/>, but an SQLite database ALREADY OPEN in another Lemora instance
        /// (e.g. installed app + development app on the same file) does not count as "busy": SQLite handles shared access by itself.
        /// The old check opened the file exclusively and always failed if another instance kept it open.
        /// </summary>
        public static bool HasWritePermissionShared(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (!File.Exists(path)) return IoPermissionHelper.HasWritePermissionOnFile(path);   // new file: only the folder permission matters
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
