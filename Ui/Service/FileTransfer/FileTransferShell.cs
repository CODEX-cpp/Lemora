using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace Lemora.Service.FileTransfer
{
    /// <summary>
    /// Windows integration for drag&amp;drop and copy-paste of files: reads the paths dragged or copied in File Explorer (to upload them),
    /// prepares the data to put on the clipboard or to drag out (for downloaded files) and manages the temporary staging folder.
    /// All clipboard functions must be called from the UI thread.
    /// </summary>
    public static class FileTransferShell
    {
        #region reading (what comes in from outside)

        /// <summary>true if the dragged data contains Windows files or folders (File Explorer, Desktop...)</summary>
        public static bool HasFiles(IDataObject? data) => data != null && data.GetDataPresent(DataFormats.FileDrop);

        /// <summary>local paths of files and folders contained in the dragged data (only those that exist)</summary>
        public static List<string> GetLocalPaths(IDataObject? data)
        {
            if (data?.GetData(DataFormats.FileDrop) is string[] arr) return arr.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            return new List<string>();
        }

        public static bool ClipboardHasFiles() => Retry(() => Clipboard.ContainsFileDropList(), false);

        /// <summary>files and folders copied to the clipboard (from File Explorer): for "Paste" in the transfer window</summary>
        public static List<string> GetLocalPathsFromClipboard()
        {
            var l = Retry(() => Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList().Cast<string>().ToList() : new List<string>(), new List<string>());
            return l.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        }

        #endregion

        #region writing (what goes out)

        /// <summary>puts local files on the clipboard the way File Explorer would (then "Paste" copies or moves them)</summary>
        public static bool SetClipboardFiles(IEnumerable<string> paths, bool cut = false)
        {
            var list = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (list.Count == 0) return false;
            var data = CreateDataObject(list, cut);
            return Retry(() => { Clipboard.SetDataObject(data, true); return true; }, false);
        }

        /// <summary>data to pass to DragDrop.DoDragDrop to drag local files out of the program (File Explorer, Desktop, other applications)</summary>
        public static DataObject CreateDragData(IEnumerable<string> paths) => CreateDataObject(paths.ToList(), false);

        private static DataObject CreateDataObject(List<string> paths, bool cut)
        {
            var data = new DataObject();
            var sc = new StringCollection();
            sc.AddRange(paths.ToArray());
            data.SetFileDropList(sc);
            // 2 = move, 5 = copy (like File Explorer's "Preferred DropEffect")
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
            return data;
        }

        #endregion

        #region staging folder

        private static string StagingRoot => Path.Combine(Path.GetTempPath(), "Lemora-file-transfer");

        /// <summary>new folder to download files into, which are then copied to the clipboard or dragged out</summary>
        public static string NewStagingDir()
        {
            var d = Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        /// <summary>removes the staging folders (at startup and exit): downloaded files remain only where the user put them</summary>
        public static void CleanStaging()
        {
            try { if (Directory.Exists(StagingRoot)) Directory.Delete(StagingRoot, true); }
            catch { /* file still in use by another application: retry on next startup */ }
        }

        #endregion

        /// <summary>the clipboard may be held by another application for a few moments</summary>
        private static T Retry<T>(Func<T> f, T fallback)
        {
            for (var i = 0; i < 6; i++)
            {
                try { return f(); }
                catch (COMException) { Thread.Sleep(40); }
                catch (ExternalException) { Thread.Sleep(40); }
            }
            return fallback;
        }
    }
}
