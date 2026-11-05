using System;
using System.IO;
using Shawn.Utils.Wpf.FileSystem;

namespace Lemora.Utils
{
    public static class DbFileAccess
    {
        /// <summary>
        /// Come <see cref="IoPermissionHelper.HasWritePermissionOnFile"/>, ma un database SQLite GIA' APERTO da un'altra istanza di Lemora
        /// (es. app installata + app di sviluppo sullo stesso file) non conta come "occupato": SQLite gestisce da solo l'accesso condiviso.
        /// Il vecchio controllo apriva il file in esclusiva e falliva sempre se un'altra istanza lo teneva aperto.
        /// </summary>
        public static bool HasWritePermissionShared(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (!File.Exists(path)) return IoPermissionHelper.HasWritePermissionOnFile(path);   // file nuovo: conta solo il permesso sulla cartella
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
