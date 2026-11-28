using System.Runtime.InteropServices;

namespace Lemora.Utils.WindowsApi.WindowsShortcutFactory
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct ShellLinkInst
    {
        public unsafe ShellLinkV* Vtbl;
    }
}
