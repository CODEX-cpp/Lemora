using System.Drawing;
using VncSharpCore.Encodings;

namespace VncSharpCore
{
    /// <summary>
    /// A strip of rows of an already decoded rectangle. A full-screen rectangle (even 4 MB compressed) takes many seconds to arrive
    /// on a slow connection: instead of waiting for all of it, it is drawn in strips as it arrives.
    /// </summary>
    public sealed class StripUpdater : IDesktopUpdater
    {
        private readonly EncodedRectangle _rect;
        private readonly int _y0, _y1;

        public StripUpdater(EncodedRectangle rect, int y0, int y1)
        {
            _rect = rect;
            _y0 = y0;
            _y1 = y1;
        }

        public Rectangle UpdateRectangle
        {
            get
            {
                var r = _rect.UpdateRectangle;
                return new Rectangle(r.X, r.Y + _y0, r.Width, _y1 - _y0);
            }
        }

        public void Draw(Bitmap desktop) => _rect.DrawRows(desktop, _y0, _y1);
    }
}
