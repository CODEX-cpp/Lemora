using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Lemora.Service.Mcp
{
    /// <summary>
    /// Reads the text on the remote screen with Windows OCR (Windows.Media.Ocr, no extra programs).
    /// Returns lines and words with their rectangle in remote screen pixels.
    /// </summary>
    internal static class McpOcr
    {
        public record Word(string Text, Rectangle Box);
        public record Line(string Text, Rectangle Box, IReadOnlyList<Word> Words);

        private static OcrEngine? _engine;

        /// <summary>engine for the user's language; if missing, Italian or English (null = Windows OCR not available)</summary>
        private static OcrEngine? Engine()
        {
            if (_engine != null) return _engine;
            _engine = OcrEngine.TryCreateFromUserProfileLanguages();
            foreach (var tag in new[] { "it-IT", "it", "en-US", "en" })
            {
                if (_engine != null) break;
                try { _engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(tag)); } catch { /* language not installed */ }
            }
            // any other language with OCR installed (UI text is almost always in Latin characters)
            if (_engine == null)
                foreach (var lang in OcrEngine.AvailableRecognizerLanguages)
                {
                    try { _engine = OcrEngine.TryCreateFromLanguage(lang); } catch { }
                    if (_engine != null) break;
                }
            return _engine;
        }

        public static string? Unavailable() => Engine() == null
            ? "text reading (Windows OCR) is not available on this PC: a Windows language with text recognition installed is needed"
            : null;

        /// <summary>the text of <paramref name="screen"/> (remote screen pixels). A small screen is enlarged: OCR reads UI text better</summary>
        public static async Task<List<Line>> ReadAsync(Bitmap screen, double? scale = null, InterpolationMode? mode = null)
        {
            if (scale != null || mode != null || Math.Max(screen.Width, screen.Height) < 600) return await ReadOnce(screen, scale, mode ?? InterpolationMode.HighQualityBicubic);
            // whole screen: two reads. Smoothly enlarged reads modern text (ClearType) better; with sharp pixels the unsmoothed text
            // of classic windows (Active Directory on Windows Server: from 201 to 224 words, some names found again). The cleaner one wins;
            // on a tie the smooth one, as before
            var soft = await ReadOnce(screen, null, InterpolationMode.HighQualityBicubic);
            var sharp = await ReadOnce(screen, null, InterpolationMode.NearestNeighbor);
            return await WithDarkZones(screen, Score(sharp) > Score(soft) ? sharp : soft);
        }

        /// <summary>
        /// dark areas (PowerShell or cmd terminals, dark themes) are reread separately: in grayscale, inverted to light and enlarged more.
        /// Test on a drawn terminal: correct text from 10 to 16 out of 22,
        /// in JPEG from 5 to 9 (a domain name misread with an extra letter is read correctly). Lines inside dark areas come from this read, the others from the normal read.
        /// It costs one extra read as large as the dark areas (nothing, if there are none)
        /// </summary>
        private static async Task<List<Line>> WithDarkZones(Bitmap screen, List<Line> normal)
        {
            const int T = 16;   // decided in 16-pixel tiles
            var (light, comp, zones) = DarkToLight(screen, T);
            if (light == null) return normal;
            using (light)
            {
                int tw = (screen.Width + T - 1) / T;
                bool InZone(Rectangle r)
                {
                    int cx = Math.Clamp(r.X + r.Width / 2, 0, screen.Width - 1), cy = Math.Clamp(r.Y + r.Height / 2, 0, screen.Height - 1);
                    return comp[cy / T * tw + cx / T] >= 0;
                }
                var fromDark = new List<Line>();
                // enlarged 2.5 times (test: 2x 11 and 7 out of 22, 2.5x 16 and 9, 3x 17 and 10 but twice the time; 2.45x only 9: OCR changes a lot
                // with size, so only half steps), at most 1.6 times the pixels of the normal whole-screen read
                // (2x up to 2000 pixels per side, 1x beyond): an area as large as the screen is still read at 2.5x
                double budget = 1.6 * screen.Width * screen.Height * (Math.Max(screen.Width, screen.Height) <= 2000 ? 4 : 1);
                foreach (var z in zones)
                {
                    using var crop = light.Clone(z, PixelFormat.Format32bppArgb);
                    var k = Math.Max(1, Math.Floor(Math.Min(2.5, Math.Sqrt(budget / (z.Width * (double)z.Height))) * 2) / 2);
                    foreach (var l in await ReadOnce(crop, k, InterpolationMode.HighQualityBicubic))
                    {
                        Rectangle Move(Rectangle r) => new(r.X + z.X, r.Y + z.Y, r.Width, r.Height);
                        var moved = new Line(l.Text, Move(l.Box), l.Words.Select(w => new Word(w.Text, Move(w.Box))).ToList());
                        if (InZone(moved.Box)) fromDark.Add(moved);
                    }
                }
                // a line from the normal read stays if the reread has nothing at that spot (what was already read is not lost)
                return normal.Where(l => !InZone(l.Box) || !fromDark.Any(d => d.Box.IntersectsWith(l.Box))).Concat(fromDark).ToList();
            }
        }

        /// <summary>
        /// how readable a read is: the letters of the "clean" words (only letters, digits and common punctuation).
        /// Misread words often have odd symbols ("C«twters"); counting words alone would reward fragmented reads
        /// </summary>
        private static int Score(List<Line> lines) => lines.SelectMany(l => l.Words).Where(w => w.Text.Length >= 2 && w.Text.All(c => char.IsLetterOrDigit(c) || "().,:;-_'?!/\\[]&%+#".Contains(c))).Sum(w => w.Text.Length);

        /// <summary>
        /// the same screen in grayscale, with the dark areas inverted to light (Windows OCR reads light text on a black or blue background poorly:
        /// extra letters in names, "\" read as "N"). <c>comp</c> tells for each tile which area it belongs to (-1 = none), <c>zones</c> the area rectangles
        /// (at most 3, the largest; small dark spots like icons do not count). light null = no dark area
        /// </summary>
        private static (Bitmap? light, int[] comp, List<Rectangle> zones) DarkToLight(Bitmap screen, int T)
        {
            using var src = screen.PixelFormat == PixelFormat.Format32bppArgb ? null : screen.Clone(new Rectangle(0, 0, screen.Width, screen.Height), PixelFormat.Format32bppArgb);
            var b = src ?? screen;
            var data = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            byte[] px;
            try { px = new byte[data.Stride * b.Height]; Marshal.Copy(data.Scan0, px, 0, px.Length); }
            finally { b.UnlockBits(data); }
            int stride = data.Stride, w = b.Width, h = b.Height, tw = (w + T - 1) / T, th = (h + T - 1) / T;
            var lum = new byte[w * h];
            var darkPx = new int[tw * th];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var i = y * stride + x * 4;
                    var l = (byte)((px[i + 2] * 3 + px[i + 1] * 6 + px[i]) / 10);
                    lum[y * w + x] = l;
                    if (l < 96) darkPx[y / T * tw + x / T]++;
                }
            var dark = new bool[tw * th];
            for (var t = 0; t < dark.Length; t++)
            {
                int cw = Math.Min(T, w - t % tw * T), ch = Math.Min(T, h - t / tw * T);
                // more than half of the points dark = dark background (light letters are less than half); PowerShell blue too (gray 30)
                dark[t] = darkPx[t] * 100 > 55 * cw * ch;
            }
            // areas = adjacent dark tiles (flood fill); only those of at least 40 tiles and at least 4 tall (64 pixels) count:
            // a terminal yes, a blue button (Explorer's "Recent" button, read worse) or the taskbar alone no
            var comp = Enumerable.Repeat(-1, dark.Length).ToArray();
            var found = new List<(List<int> tiles, Rectangle box)>();
            var seen = new bool[dark.Length];
            for (var t0 = 0; t0 < dark.Length; t0++)
            {
                if (!dark[t0] || seen[t0]) continue;
                var tiles = new List<int>();
                var stack = new Stack<int>();
                stack.Push(t0); seen[t0] = true;
                int x0 = int.MaxValue, y0 = int.MaxValue, x1 = 0, y1 = 0;
                while (stack.Count > 0)
                {
                    var t = stack.Pop();
                    tiles.Add(t);
                    int tx = t % tw, ty = t / tw;
                    x0 = Math.Min(x0, tx); y0 = Math.Min(y0, ty); x1 = Math.Max(x1, tx); y1 = Math.Max(y1, ty);
                    foreach (var n in new[] { tx > 0 ? t - 1 : -1, tx < tw - 1 ? t + 1 : -1, ty > 0 ? t - tw : -1, ty < th - 1 ? t + tw : -1 })
                        if (n >= 0 && dark[n] && !seen[n]) { seen[n] = true; stack.Push(n); }
                }
                if (tiles.Count >= 40 && y1 - y0 + 1 >= 4)
                    found.Add((tiles, Rectangle.FromLTRB(x0 * T, y0 * T, Math.Min(w, (x1 + 1) * T), Math.Min(h, (y1 + 1) * T))));
            }
            var zones = new List<Rectangle>();
            foreach (var (tiles, box) in found.OrderByDescending(f => f.tiles.Count).Take(3))
            {
                foreach (var t in tiles) comp[t] = zones.Count;
                zones.Add(box);
            }
            if (zones.Count == 0) return (null, comp, zones);
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var l = lum[y * w + x];
                    if (comp[y / T * tw + x / T] >= 0) l = (byte)(255 - l);
                    var i = y * stride + x * 4;
                    px[i] = px[i + 1] = px[i + 2] = l; px[i + 3] = 255;
                }
            var outB = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var od = outB.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try { Marshal.Copy(px, 0, od.Scan0, px.Length); }   // same width and format: same row size in bytes
            finally { outB.UnlockBits(od); }
            return (outB, comp, zones);
        }

        /// <summary>
        /// The engine is one shared instance and Windows refuses two RecognizeAsync at once ("Another RecognizeAsync operation is already running"):
        /// it happened when an attendi with a text was polling while Claude called leggi_schermo or another read. Readers queue here instead.
        /// </summary>
        private static readonly SemaphoreSlim RecognizeGate = new(1, 1);

        private static async Task<List<Line>> ReadOnce(Bitmap screen, double? scale, InterpolationMode mode)
        {
            var engine = Engine() ?? throw new InvalidOperationException(Unavailable());
            var k = scale ?? (Math.Max(screen.Width, screen.Height) <= 2000 ? 2.0 : 1.0);
            k = Math.Min(k, (OcrEngine.MaxImageDimension - 1) / (double)Math.Max(screen.Width, screen.Height));
            int w = (int)(screen.Width * k), h = (int)(screen.Height * k);
            using var big = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(big))
            {
                g.InterpolationMode = mode;
                if (mode == InterpolationMode.NearestNeighbor) g.PixelOffsetMode = PixelOffsetMode.Half;   // without it, the image shifts by half a pixel
                g.DrawImage(screen, 0, 0, w, h);
            }
            using var soft = ToSoftwareBitmap(big);
            OcrResult result;
            await RecognizeGate.WaitAsync();
            try { result = await engine.RecognizeAsync(soft); }
            finally { RecognizeGate.Release(); }
            Rectangle Back(Windows.Foundation.Rect r) => new((int)Math.Round(r.X / k), (int)Math.Round(r.Y / k), (int)Math.Round(r.Width / k), (int)Math.Round(r.Height / k));
            var lines = new List<Line>();
            foreach (var l in result.Lines)
            {
                var words = l.Words.Select(x => new Word(x.Text, Back(x.BoundingRect))).ToList();
                if (words.Count == 0) continue;
                var box = words.Select(x => x.Box).Aggregate(Rectangle.Union);
                lines.Add(new Line(l.Text, box, words));
            }
            return lines;
        }

        private static SoftwareBitmap ToSoftwareBitmap(Bitmap b)
        {
            var data = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var bytes = new byte[data.Stride * b.Height];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                var writer = new DataWriter();
                writer.WriteBytes(bytes);
                var soft = SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(), BitmapPixelFormat.Bgra8, b.Width, b.Height, BitmapAlphaMode.Premultiplied);
                return soft;
            }
            finally { b.UnlockBits(data); }
        }

        /// <summary>
        /// where <paramref name="query"/> is: first equal consecutive words (case-insensitive), then lines containing it,
        /// then nearly equal text (OCR gets some letters wrong: "LIROUALA", "NUsersNAdninistrator"; see <see cref="Near"/>).
        /// Each result is the rectangle of the found words only; approx = nearly equal, text = what OCR actually read.
        /// <paramref name="all"/> false (click): only the best kind available (nearly equal ones only without the others, and only those with fewest errors).
        /// true (wait, which counts occurrences): each line counts with its best kind, so the count does not jump if a line reads slightly differently
        /// </summary>
        public static List<(string text, Rectangle box, bool approx)> Find(IEnumerable<Line> lines, string query, bool all = false)
        {
            var q = Norm(query);
            var qWords = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var exact = new List<(string, Rectangle, bool)>();
            var partial = new List<(string, Rectangle, bool)>();
            var near = new List<(string, Rectangle, bool, int errors)>();
            if (qWords.Length == 0) return exact;
            foreach (var l in lines)
            {
                var ws = l.Words.Select(x => Norm(x.Text)).ToList();
                var found = false;
                for (var i = 0; i + qWords.Length <= ws.Count; i++)
                {
                    if (!qWords.Select((x, j) => x == ws[i + j]).All(ok => ok)) continue;
                    var box = l.Words.Skip(i).Take(qWords.Length).Select(x => x.Box).Aggregate(Rectangle.Union);
                    exact.Add((string.Join(" ", l.Words.Skip(i).Take(qWords.Length).Select(x => x.Text)), box, false));
                    found = true;
                }
                if (found) continue;
                if (Norm(l.Text).Contains(q)) partial.Add((l.Text, l.Box, false));
                else if (Near(l, query) is { } n) near.Add((string.Join(" ", n.words.Select(x => x.Text)), n.words.Select(x => x.Box).Aggregate(Rectangle.Union), true, n.errors));
            }
            if (all) return exact.Concat(partial).Concat(near.Select(n => (n.Item1, n.Item2, n.Item3))).ToList();
            if (exact.Count > 0) return exact;
            if (partial.Count > 0) return partial;
            return near.Where(n => n.errors == near.Min(m => m.errors)).Select(n => (n.Item1, n.Item2, n.Item3)).ToList();
        }

        /// <summary>
        /// the part of the line most similar to <paramref name="query"/>, case-insensitive and ignoring spaces (OCR adds or drops them: "PS C: \Users").
        /// At most one wrong, missing or extra character per 6 of the searched text: below 6 characters none (spaces only),
        /// so a 5-letter word never matches one with a different letter, and a 10-letter word does not match one with 2 letters missing
        /// </summary>
        public static (List<Word> words, int errors)? Near(Line line, string query)
        {
            var q = new string(query.ToLowerInvariant().Where(c => !char.IsWhiteSpace(c)).ToArray());
            var s = new System.Text.StringBuilder();
            var owner = new List<int>();   // for each character of s, the word it comes from
            for (var i = 0; i < line.Words.Count; i++)
                foreach (var c in line.Words[i].Text.ToLowerInvariant().Where(c => !char.IsWhiteSpace(c))) { s.Append(c); owner.Add(i); }
            int m = q.Length, n = s.Length, allowed = m / 6;
            if (m == 0 || n == 0) return null;
            // edit distance of q from the part of s ending at j (the part can start anywhere): d = errors, from = where it starts
            var d = new int[n + 1];
            var from = new int[n + 1];
            for (var j = 0; j <= n; j++) from[j] = j;
            for (var i = 1; i <= m; i++)
            {
                int diag = d[0], diagFrom = from[0];
                d[0] = i;
                for (var j = 1; j <= n; j++)
                {
                    int up = d[j], upFrom = from[j];
                    int best = diag + (q[i - 1] == s[j - 1] ? 0 : 1), bestFrom = diagFrom;
                    if (up + 1 < best) { best = up + 1; bestFrom = upFrom; }
                    if (d[j - 1] + 1 < best) { best = d[j - 1] + 1; bestFrom = from[j - 1]; }
                    diag = up; diagFrom = upFrom;
                    d[j] = best; from[j] = bestFrom;
                }
            }
            var end = Enumerable.Range(1, n).OrderBy(j => d[j]).First();
            if (d[end] > allowed || end - from[end] == 0) return null;
            int w0 = owner[from[end]], w1 = owner[end - 1];
            return (line.Words.Skip(w0).Take(w1 - w0 + 1).ToList(), d[end]);
        }

        /// <summary>comparison ignoring case and punctuation attached to words ("OK" also matches "OK," and "[OK]")</summary>
        private static string Norm(string s)
            => string.Join(" ", s.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Trim('.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']', '<', '>', '«', '»')).Where(w => w.Length > 0));
    }
}
