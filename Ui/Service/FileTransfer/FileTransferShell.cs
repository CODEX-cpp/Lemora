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
    /// Collegamento con Windows per drag&amp;drop e copia-incolla di file: legge i percorsi trascinati o copiati in Esplora risorse (per caricarli),
    /// prepara i dati da mettere negli appunti o da trascinare fuori (per i file scaricati) e gestisce la cartella temporanea di appoggio.
    /// Tutte le funzioni degli appunti vanno chiamate dal thread dell'interfaccia.
    /// </summary>
    public static class FileTransferShell
    {
        #region lettura (cosa arriva dall'esterno)

        /// <summary>vero se i dati trascinati contengono file o cartelle di Windows (Esplora risorse, Desktop...)</summary>
        public static bool HasFiles(IDataObject? data) => data != null && data.GetDataPresent(DataFormats.FileDrop);

        /// <summary>percorsi locali di file e cartelle contenuti nei dati trascinati (solo quelli che esistono)</summary>
        public static List<string> GetLocalPaths(IDataObject? data)
        {
            if (data?.GetData(DataFormats.FileDrop) is string[] arr) return arr.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            return new List<string>();
        }

        public static bool ClipboardHasFiles() => Retry(() => Clipboard.ContainsFileDropList(), false);

        /// <summary>file e cartelle copiati negli appunti (da Esplora risorse): per "Incolla" nella finestra del trasferimento</summary>
        public static List<string> GetLocalPathsFromClipboard()
        {
            var l = Retry(() => Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList().Cast<string>().ToList() : new List<string>(), new List<string>());
            return l.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        }

        #endregion

        #region scrittura (cosa si manda all'esterno)

        /// <summary>mette dei file locali negli appunti come farebbe Esplora risorse (poi "Incolla" li copia o li sposta)</summary>
        public static bool SetClipboardFiles(IEnumerable<string> paths, bool cut = false)
        {
            var list = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (list.Count == 0) return false;
            var data = CreateDataObject(list, cut);
            return Retry(() => { Clipboard.SetDataObject(data, true); return true; }, false);
        }

        /// <summary>dati da passare a DragDrop.DoDragDrop per trascinare file locali fuori dal programma (Esplora risorse, Desktop, altre applicazioni)</summary>
        public static DataObject CreateDragData(IEnumerable<string> paths) => CreateDataObject(paths.ToList(), false);

        private static DataObject CreateDataObject(List<string> paths, bool cut)
        {
            var data = new DataObject();
            var sc = new StringCollection();
            sc.AddRange(paths.ToArray());
            data.SetFileDropList(sc);
            // 2 = sposta, 5 = copia (come "Preferred DropEffect" di Esplora risorse)
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
            return data;
        }

        #endregion

        #region cartella di appoggio

        private static string StagingRoot => Path.Combine(Path.GetTempPath(), "Lemora-file-transfer");

        /// <summary>cartella nuova dove scaricare i file che poi si copiano negli appunti o si trascinano fuori</summary>
        public static string NewStagingDir()
        {
            var d = Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        /// <summary>toglie le cartelle di appoggio (all'avvio e all'uscita): i file scaricati restano solo dove li ha messi l'utente</summary>
        public static void CleanStaging()
        {
            try { if (Directory.Exists(StagingRoot)) Directory.Delete(StagingRoot, true); }
            catch { /* file ancora in uso da un'altra applicazione: si riprova al prossimo avvio */ }
        }

        #endregion

        /// <summary>gli appunti possono essere occupati da un'altra applicazione per qualche istante</summary>
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
