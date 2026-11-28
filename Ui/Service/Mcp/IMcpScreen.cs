using System.Drawing;
using Lemora.Model.Protocol.Base;

namespace Lemora.Service.Mcp
{
    /// <summary>
    /// remote screen that Claude can see and use: VNC sessions (VncHost) and RDP sessions opened with FreeRDP (IntegrateHost).
    /// Coordinates and keys as in VNC: pixels of the screen capture, X11 keysyms. Must be called on the UI thread.
    /// </summary>
    internal interface IMcpScreen
    {
        ProtocolBase ProtocolServer { get; }
        /// <summary>screen capture; null if not available yet (or the session is not visible: see the McpUnavailable message)</summary>
        Bitmap? McpCapture();
        /// <summary>size of the screen capture without taking it (to convert click coordinates); null if unknown</summary>
        Size? McpScreenSize();
        /// <summary>mask: 1 left, 2 middle, 4 right, 8/16 wheel up/down</summary>
        void McpPointer(byte mask, int x, int y);
        /// <summary>remote screen point -> pixel on this PC's screen (for the "Claude" cursor); null if unknown</summary>
        Point? McpToScreen(int x, int y);
        void SendKey(uint keysym, bool pressed);
        void SendChord(uint[] modifiers, uint key);
    }
}
