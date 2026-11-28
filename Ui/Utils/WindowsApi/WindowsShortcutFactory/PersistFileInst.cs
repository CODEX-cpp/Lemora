using System.Runtime.InteropServices;

namespace Lemora.Utils.WindowsApi.WindowsShortcutFactory
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct PersistFileInst
    {
        public unsafe PersistFileV* Vtbl;
    }
}
