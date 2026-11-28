using System;
using System.Drawing;
using System.Reflection;
using VncSharpCore;

namespace Lemora.Utils
{
    /// <summary>How the remote VNC screen fits the window</summary>
    public enum VncFitMode
    {
        /// <summary>1:1 (actual size): scrolls if it is larger than the window</summary>
        Actual,
        /// <summary>the whole width of the remote screen fits the window; if it is taller, it scrolls vertically</summary>
        Width,
        /// <summary>the whole height fits the window; if it is wider, it scrolls horizontally</summary>
        Height,
    }

    /// <summary>
    /// Fixed scale (computed from the mode) with scrolling handled by us: no native Windows scroll bars,
    /// the bars are drawn by the WPF UI reading <see cref="ScrollX"/>/<see cref="ScrollY"/> (already scaled pixels).
    /// VncSharpCore (NuGet package) keeps the "policy" in a private field: it is replaced via reflection.
    /// </summary>
    public sealed class VncZoomPolicy : VncDesktopTransformPolicy
    {
        private static readonly FieldInfo PolicyField = typeof(RemoteDesktop).GetField("desktopPolicy", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly FieldInfo VncField = typeof(RemoteDesktop).GetField("vnc", BindingFlags.Instance | BindingFlags.NonPublic)!;

        /// <summary>the control's VNC client (private field in the package): needed to know when the first frame arrives</summary>
        public static VncClient? GetClient(RemoteDesktop rd) => VncField.GetValue(rd) as VncClient;

        public VncFitMode Mode { get; }
        public int ScrollX { get; private set; }
        public int ScrollY { get; private set; }

        private VncZoomPolicy(VncClient vnc, RemoteDesktop rd, VncFitMode mode) : base(vnc, rd)
        {
            Mode = mode;
        }

        /// <summary>Replaces the control's display mode with this one</summary>
        public static VncZoomPolicy Install(RemoteDesktop rd, VncFitMode mode)
        {
            var p = new VncZoomPolicy((VncClient)VncField.GetValue(rd)!, rd, mode);
            PolicyField.SetValue(rd, p);
            rd.AutoScrollMinSize = new Size(0, 0);   // a non-zero value would turn the Windows scroll bars back on
            rd.AutoScroll = false;
            rd.Invalidate();
            return p;
        }

        public override Size AutoScrollMinSize => new Size(0, 0);

        /// <summary>current scale (local pixels per remote pixel): VncHost computes it from the mode and the chosen zoom</summary>
        public double Zoom { get; set; } = 1;

        /// <summary>size of the already scaled image</summary>
        public Size ContentSize
        {
            get
            {
                if (vnc?.Framebuffer == null) return new Size(1, 1);
                var c = remoteDesktop.ClientSize;
                var w = (int)Math.Round(vnc.Framebuffer.Width * Zoom);
                var h = (int)Math.Round(vnc.Framebuffer.Height * Zoom);
                // one or two pixels off the window is rounding, not a real overflow: avoids a useless scroll
                if (Math.Abs(w - c.Width) <= 2) w = c.Width;
                if (Math.Abs(h - c.Height) <= 2) h = c.Height;
                return new Size(Math.Max(1, w), Math.Max(1, h));
            }
        }

        public void ScrollTo(int x, int y)
        {
            var s = ContentSize; var c = remoteDesktop.ClientSize;
            x = Math.Max(0, Math.Min(x, Math.Max(0, s.Width - c.Width)));
            y = Math.Max(0, Math.Min(y, Math.Max(0, s.Height - c.Height)));
            if (x == ScrollX && y == ScrollY) return;
            ScrollX = x; ScrollY = y;
            remoteDesktop.Invalidate();
        }

        /// <summary>top-left corner of the image in the window (centered if smaller, moved by scrolling if larger)</summary>
        private Point Origin()
        {
            var s = ContentSize; var c = remoteDesktop.ClientSize;
            return new Point(s.Width > c.Width ? -ScrollX : (c.Width - s.Width) / 2,
                             s.Height > c.Height ? -ScrollY : (c.Height - s.Height) / 2);
        }

        public override Rectangle AdjustUpdateRectangle(Rectangle r)
        {
            var o = Origin(); var z = Zoom;
            var a = new Rectangle(o.X + (int)Math.Floor(r.X * z), o.Y + (int)Math.Floor(r.Y * z), (int)Math.Ceiling(r.Width * z), (int)Math.Ceiling(r.Height * z));
            a.Inflate(2, 2);
            return a;
        }

        public override Rectangle RepositionImage(Image desktopImage)
        {
            var o = Origin(); var s = ContentSize;
            return new Rectangle(o.X, o.Y, s.Width, s.Height);
        }

        public override Rectangle GetMouseMoveRectangle() => RepositionImage(null!);

        public override Point GetMouseMovePoint(Point current) => UpdateRemotePointer(current);

        /// <summary>the reverse of <see cref="UpdateRemotePointer"/>: remote pixel -> control pixel</summary>
        public Point RemoteToClient(Point remote)
        {
            var o = Origin(); var z = Zoom;
            return new Point(o.X + (int)Math.Round(remote.X * z), o.Y + (int)Math.Round(remote.Y * z));
        }

        public override Point UpdateRemotePointer(Point current)
        {
            var o = Origin(); var z = Zoom;
            return new Point((int)Math.Round((current.X - o.X) / z), (int)Math.Round((current.Y - o.Y) / z));
        }
    }
}
