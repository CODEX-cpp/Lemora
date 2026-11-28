using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Lemora.Setup
{
    /// <summary>
    /// App identity for the taskbar. The shortcut pinned to the taskbar points to the launcher (Lemora.exe in the installation
    /// folder), but the window belongs to versions\&lt;version&gt;\Lemora.exe: without a common identifier Windows sees them as
    /// two programs and shows two icons. With the same AppUserModelID on process, window and shortcuts there is a single icon, and
    /// "pin to taskbar" from the open window creates a shortcut to the launcher (not to the version folder).
    /// </summary>
    public static class TaskbarIdentity
    {
        public const string AppId = "Lemora.App";
        private static readonly Guid FmtId = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
        private static readonly Guid IidPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
        private static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PropertyKey { public Guid Fmt; public uint Pid; }

        [StructLayout(LayoutKind.Explicit)]
        private struct PropVariant { [FieldOffset(0)] public ushort Vt; [FieldOffset(8)] public IntPtr Ptr; }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint i, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant v);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant v);
            [PreserveSig] int Commit();
        }

        [DllImport("shell32.dll", PreserveSig = false)]
        private static extern void SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

        [DllImport("shell32.dll", PreserveSig = false)]
        private static extern void SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object store);

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant v);

        private static void SetString(IPropertyStore store, uint pid, string value)
        {
            var key = new PropertyKey { Fmt = FmtId, Pid = pid };
            var v = new PropVariant { Vt = 31 /* VT_LPWSTR */, Ptr = Marshal.StringToCoTaskMemUni(value) };
            try { store.SetValue(ref key, ref v); }
            finally { PropVariantClear(ref v); }
        }

        /// <summary>to call at the start of Main, before creating windows</summary>
        public static void ApplyToProcess()
        {
            try { SetCurrentProcessExplicitAppUserModelID(AppId); } catch { /* taskbar cosmetics only: never stop the startup */ }
        }

        /// <summary>to call when the main window has its handle: tells Windows how to relaunch the app (the launcher)</summary>
        public static void ApplyToWindow(IntPtr hwnd, string launcherPath)
        {
            try
            {
                var iid = IidPropertyStore;
                SHGetPropertyStoreForWindow(hwnd, ref iid, out var o);
                var store = (IPropertyStore)o;
                SetString(store, 5, AppId);                              // System.AppUserModel.ID
                SetString(store, 2, "\"" + launcherPath + "\"");         // RelaunchCommand
                SetString(store, 3, launcherPath + ",0");                // RelaunchIconResource
                SetString(store, 4, "Lemora");                           // RelaunchDisplayNameResource
                store.Commit();
            }
            catch { }
        }

        /// <summary>writes the identifier into an already saved .lnk shortcut</summary>
        public static void ApplyToShortcut(string lnkPath)
        {
            try
            {
                var link = Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidShellLink)!)!;
                var file = (IPersistFile)link;
                file.Load(lnkPath, 2 /* STGM_READWRITE */);
                var store = (IPropertyStore)link;
                SetString(store, 5, AppId);
                store.Commit();
                file.Save(lnkPath, true);
            }
            catch { }
        }
    }
}
