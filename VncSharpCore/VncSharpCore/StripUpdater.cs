using System.Drawing;
using VncSharpCore.Encodings;

namespace VncSharpCore
{
    /// <summary>
    /// Una fascia di righe di un rettangolo gia' decodificato. Un rettangolo a schermo intero (anche 4 MB compressi) arriva in molti secondi
    /// su una connessione lenta: invece di aspettarlo tutto lo si disegna a fasce man mano che arriva.
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
