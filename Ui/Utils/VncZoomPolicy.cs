using System;
using System.Drawing;
using System.Reflection;
using VncSharpCore;

namespace Lemora.Utils
{
    /// <summary>Come si adatta lo schermo VNC remoto alla finestra</summary>
    public enum VncFitMode
    {
        /// <summary>1:1 (dimensioni reali): si scorre se e' piu' grande della finestra</summary>
        Actual,
        /// <summary>tutta la larghezza dello schermo remoto entra nella finestra; se e' piu' alto si scorre in verticale</summary>
        Width,
        /// <summary>tutta l'altezza entra nella finestra; se e' piu' largo si scorre in orizzontale</summary>
        Height,
    }

    /// <summary>
    /// Scala fissa (calcolata dalla modalita') con scorrimento gestito da noi: niente barre native di Windows,
    /// le barre le disegna l'interfaccia WPF leggendo <see cref="ScrollX"/>/<see cref="ScrollY"/> (pixel gia' scalati).
    /// VncSharpCore (pacchetto NuGet) tiene la "policy" in un campo privato: la si sostituisce con la riflessione.
    /// </summary>
    public sealed class VncZoomPolicy : VncDesktopTransformPolicy
    {
        private static readonly FieldInfo PolicyField = typeof(RemoteDesktop).GetField("desktopPolicy", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly FieldInfo VncField = typeof(RemoteDesktop).GetField("vnc", BindingFlags.Instance | BindingFlags.NonPublic)!;

        /// <summary>il client VNC del controllo (campo privato nel pacchetto): serve per sapere quando arriva il primo fotogramma</summary>
        public static VncClient? GetClient(RemoteDesktop rd) => VncField.GetValue(rd) as VncClient;

        public VncFitMode Mode { get; }
        public int ScrollX { get; private set; }
        public int ScrollY { get; private set; }

        private VncZoomPolicy(VncClient vnc, RemoteDesktop rd, VncFitMode mode) : base(vnc, rd)
        {
            Mode = mode;
        }

        /// <summary>Sostituisce la modalita' di visualizzazione del controllo con questa</summary>
        public static VncZoomPolicy Install(RemoteDesktop rd, VncFitMode mode)
        {
            var p = new VncZoomPolicy((VncClient)VncField.GetValue(rd)!, rd, mode);
            PolicyField.SetValue(rd, p);
            rd.AutoScrollMinSize = new Size(0, 0);   // un valore diverso da zero riattiverebbe le barre di Windows
            rd.AutoScroll = false;
            rd.Invalidate();
            return p;
        }

        public override Size AutoScrollMinSize => new Size(0, 0);

        /// <summary>scala corrente (pixel locali per pixel remoto): la calcola VncHost dalla modalita' e dallo zoom scelto</summary>
        public double Zoom { get; set; } = 1;

        /// <summary>dimensioni dell'immagine gia' scalata</summary>
        public Size ContentSize
        {
            get
            {
                if (vnc?.Framebuffer == null) return new Size(1, 1);
                var c = remoteDesktop.ClientSize;
                var w = (int)Math.Round(vnc.Framebuffer.Width * Zoom);
                var h = (int)Math.Round(vnc.Framebuffer.Height * Zoom);
                // a un pixel o due dalla finestra e' un arrotondamento, non un vero eccesso: si evita uno scorrimento inutile
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

        /// <summary>angolo in alto a sinistra dell'immagine nella finestra (centrata se piu' piccola, spostata dallo scorrimento se piu' grande)</summary>
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

        public override Point UpdateRemotePointer(Point current)
        {
            var o = Origin(); var z = Zoom;
            return new Point((int)Math.Round((current.X - o.X) / z), (int)Math.Round((current.Y - o.Y) / z));
        }
    }
}
