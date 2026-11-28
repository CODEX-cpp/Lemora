using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;

namespace Lemora.Service.Mcp
{
    /// <summary>
    /// Control recognition from the remote screen's pixels alone (nothing on the remote PC, no models to download):
    /// edges -> connected pieces -> rectangles (buttons, fields, boxes), small squares (checkboxes), small circles (radio options), icons;
    /// OCR names each one. Works well on classic windows; on flat borderless designs only the text remains.
    /// </summary>
    internal static class McpVision
    {
        public record Element(string Kind, Rectangle Box, string Text, string State)
        {
            /// <summary>where the name sits outside the box (right of checkboxes and icons, left of or above fields): the numbers must not go over it</summary>
            public Rectangle NameBox { get; init; }
        }

        private const int EdgeStep = 18;   // gray step that counts as an "edge": the light border of Windows 11 buttons (E5 on FD) just passes

        /// <summary>
        /// boxes and numbers of the list drawn on the image (already scaled down by <paramref name="k"/>), as OmniParser and browser agents do:
        /// the model sees at a glance what a button is and answers with the number. White number on a solid label, at the top left of the box
        /// </summary>
        public static void DrawMarks(Graphics g, IReadOnlyList<Element> list, double k)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var font = new Font("Segoe UI", 9.5f, FontStyle.Bold, GraphicsUnit.Pixel);   // 8 was hard to read in the test screenshots
            RectangleF Scaled(Rectangle b) => new((float)(b.X * k), (float)(b.Y * k), (float)(b.Width * k), (float)(b.Height * k));
            // numbers must not cover text, controls, names or other numbers: in dense lists (rows of 12-16 pixels) a number
            // always on top ended up on the previous row and the tree's numbers overlapped
            var obstacles = list.Select((e, i) => (rect: Scaled(e.Box), i)).Concat(list.Select((e, i) => (rect: Scaled(e.NameBox), i)).Where(o => o.rect.Width > 0)).ToList();
            var placed = new List<RectangleF>();
            var bounds = g.VisibleClipBounds;
            float Overlap(RectangleF a, RectangleF b) { var x = RectangleF.Intersect(a, b); return x.IsEmpty ? 0 : x.Width * x.Height; }
            for (var i = 0; i < list.Count; i++)
            {
                var e = list[i];
                var color = e.Kind switch
                {
                    "button" => Color.FromArgb(0, 140, 60), "field" => Color.FromArgb(0, 110, 190), "checkbox" or "radio" => Color.FromArgb(190, 0, 150),
                    "icon" => Color.FromArgb(215, 110, 0), "box" => Color.FromArgb(120, 120, 0), _ => Color.FromArgb(200, 30, 30),
                };
                var r = new Rectangle((int)(e.Box.X * k), (int)(e.Box.Y * k), Math.Max(2, (int)(e.Box.Width * k)), Math.Max(2, (int)(e.Box.Height * k)));
                var label = (i + 1).ToString();
                var size = g.MeasureString(label, font);
                float tw = size.Width, th = size.Height - 1;
                // possible spots in order of preference; the first free one wins, if none is free the one that covers least
                RectangleF[] spots;
                if (e.Kind == "text")
                {
                    // no box on text (right against the text it would cover the letters): just the number, left, above or below
                    float cy = r.Y + (r.Height - th) / 2 + 1;
                    spots = new[] { new RectangleF(r.X - tw - 2, cy, tw, th), new RectangleF(r.X, r.Y - th, tw, th), new RectangleF(r.X, r.Bottom, tw, th), new RectangleF(r.Right + 2, cy, tw, th) };
                }
                else
                {
                    // box 3 pixels larger than the control, so it does not touch the content; number outside the box, inside only if there is no room outside
                    var big = Rectangle.Inflate(r, 3, 3);
                    using var pen = new Pen(color, 1);
                    g.DrawRectangle(pen, big);
                    spots = new[]
                    {
                        new RectangleF(big.X, big.Y - th + 1, tw, th), new RectangleF(big.X - tw, big.Y + (big.Height - th) / 2, tw, th),
                        new RectangleF(big.X, big.Bottom, tw, th), new RectangleF(big.X + 1, big.Y + 1, tw, th),
                    };
                }
                var tag = spots[0];
                var best = float.MaxValue;
                foreach (var s in spots)
                {
                    var cost = obstacles.Where(o => o.i != i).Sum(o => Overlap(s, o.rect)) + placed.Sum(p => Overlap(s, p) * 3);
                    if (s.Left < bounds.Left || s.Top < bounds.Top || s.Right > bounds.Right || s.Bottom > bounds.Bottom) cost += 10000;
                    if (cost < best) { best = cost; tag = s; }
                    if (cost == 0) break;
                }
                tag.X = Math.Clamp(tag.X, bounds.Left, Math.Max(bounds.Left, bounds.Right - tw));
                tag.Y = Math.Clamp(tag.Y, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - th));
                placed.Add(tag);
                using var fill = new SolidBrush(color);
                g.FillRectangle(fill, tag);
                g.DrawString(label, font, Brushes.White, tag.X, tag.Y - 1);
            }
        }

        private static int[] Argb(Bitmap b)
        {
            var data = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var px = new int[b.Width * b.Height];
                for (var y = 0; y < b.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, px, y * b.Width, b.Width);
                return px;
            }
            finally { b.UnlockBits(data); }
        }

        /// <summary>
        /// OCR, controls and a second read of buttons and fields left without text: OCR on the whole screen often misses short words
        /// inside a border ("OK", "Cancel"); cropped and enlarged they become readable
        /// </summary>
        public static async System.Threading.Tasks.Task<List<Element>> DetectAsync(Bitmap screen, bool ocr)
        {
            var lines = ocr ? await McpOcr.ReadAsync(screen) : new List<McpOcr.Line>();
            var list = await System.Threading.Tasks.Task.Run(() => Detect(screen, lines));
            if (!ocr) return list;
            var retry = 0;
            for (var i = 0; i < list.Count && retry < 30; i++)
            {
                var e = list[i];
                if (!(e.Kind == "field" && e.State == "empty" || e.Kind == "box" && e.State == "no text") || e.Box.Height > 40 || e.Box.Width < 20) continue;
                retry++;
                var inner = Rectangle.Inflate(e.Box, -2, -2);
                const int k = 3;
                using var crop = new Bitmap(inner.Width * k, inner.Height * k, PixelFormat.Format32bppArgb);
                using (var gr = Graphics.FromImage(crop))
                {
                    gr.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    gr.DrawImage(screen, new Rectangle(0, 0, crop.Width, crop.Height), inner, GraphicsUnit.Pixel);
                }
                var read = await McpOcr.ReadAsync(crop);
                var text = string.Join(" ", read.Select(l => l.Text)).Trim();
                if (text.Length == 0) continue;
                // centered text = button; left-aligned = field containing text
                var tb = read.Select(l => l.Box).Aggregate(Rectangle.Union);
                var centered = Math.Abs(tb.Left + tb.Width / 2 - crop.Width / 2) < crop.Width * 0.15;
                list[i] = centered || e.Kind == "box" ? new Element("button", e.Box, text, "") : e with { State = "contains: " + text };
            }
            return list;
        }

        public static byte[] Gray(Bitmap b)
        {
            var data = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var px = new byte[data.Stride * b.Height];
                Marshal.Copy(data.Scan0, px, 0, px.Length);
                var g = new byte[b.Width * b.Height];
                for (var y = 0; y < b.Height; y++)
                {
                    var row = y * data.Stride;
                    for (var x = 0; x < b.Width; x++)
                    {
                        var i = row + x * 4;
                        g[y * b.Width + x] = (byte)((px[i + 2] * 3 + px[i + 1] * 6 + px[i]) / 10);
                    }
                }
                return g;
            }
            finally { b.UnlockBits(data); }
        }

        /// <summary>
        /// area changed between two screens of the same size (remote screen pixels), in 8x8 blocks: null = nothing changed.
        /// <paramref name="blocks"/> = how many blocks changed (few = the blinking cursor)
        /// </summary>
        public static Rectangle? Changed(Bitmap before, Bitmap after, out int blocks)
        {
            blocks = 0;
            int W = after.Width, H = after.Height;
            var a = Gray(before);
            var b = Gray(after);
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            for (var by = 0; by < H; by += 8)
                for (var bx = 0; bx < W; bx += 8)
                {
                    var hit = false;
                    for (var y = by; y < Math.Min(by + 8, H) && !hit; y++)
                        for (var x = bx; x < Math.Min(bx + 8, W); x++)
                            if (Math.Abs(a[y * W + x] - b[y * W + x]) > 24) { hit = true; break; }
                    if (!hit) continue;
                    blocks++;
                    minX = Math.Min(minX, bx); minY = Math.Min(minY, by);
                    maxX = Math.Max(maxX, Math.Min(bx + 8, W)); maxY = Math.Max(maxY, Math.Min(by + 8, H));
                }
            return blocks == 0 ? null : Rectangle.FromLTRB(minX, minY, maxX, maxY);
        }

        private sealed class Comp { public int MinX = int.MaxValue, MinY = int.MaxValue, MaxX, MaxY, Count; public int W => MaxX - MinX + 1; public int H => MaxY - MinY + 1; public Rectangle Box => new(MinX, MinY, W, H); }

        /// <summary>tests only (DevDemo vision-files): the pieces containing these points end up in DebugLog with their numbers</summary>
        public static List<Point>? DebugPoints;
        public static readonly List<string> DebugLog = new();

        public static List<Element> Detect(Bitmap bmp, IReadOnlyList<McpOcr.Line> lines)
        {
            int W = bmp.Width, H = bmp.Height;
            var g = Gray(bmp);
            var rgb = Argb(bmp);

            // 1. edges: gray step to the right or downward
            var edge = new bool[W * H];
            for (var y = 0; y < H - 1; y++)
                for (var x = 0; x < W - 1; x++)
                {
                    int i = y * W + x, v = g[i];
                    if (Math.Abs(v - g[i + 1]) > EdgeStep || Math.Abs(v - g[i + W]) > EdgeStep) edge[i] = true;
                }

            // 2. connected edge pieces (diagonals included)
            var label = new int[W * H];
            var comps = new List<Comp> { new() };   // index 0 = no piece
            var stack = new Stack<int>();
            for (var start = 0; start < edge.Length; start++)
            {
                if (!edge[start] || label[start] != 0) continue;
                var id = comps.Count;
                var c = new Comp();
                comps.Add(c);
                label[start] = id;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    var i = stack.Pop();
                    int x = i % W, y = i / W;
                    c.Count++;
                    if (x < c.MinX) c.MinX = x; if (x > c.MaxX) c.MaxX = x;
                    if (y < c.MinY) c.MinY = y; if (y > c.MaxY) c.MaxY = y;
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                            var j = ny * W + nx;
                            if (edge[j] && label[j] == 0) { label[j] = id; stack.Push(j); }
                        }
                }
            }

            bool Has(int id, int x, int y) => x >= 0 && y >= 0 && x < W && y < H && label[y * W + x] == id;
            // how much of one side of the piece's rectangle is an edge of the piece itself (3-pixel band toward the inside)
            double Side(int id, bool horizontal, int fixedAt, int from, int to, int inward)
            {
                if (to < from) return 0;
                var hit = 0;
                for (var p = from; p <= to; p++)
                {
                    var ok = false;
                    // 6-pixel band: a stray pixel next to the control (halo, shadow) enlarges the piece's rectangle
                    for (var d = 0; d < 6 && !ok; d++) ok = horizontal ? Has(id, p, fixedAt + d * inward) : Has(id, fixedAt + d * inward, p);
                    if (ok) hit++;
                }
                return hit / (double)(to - from + 1);
            }
            double MinSide(int id, Comp c, double skip)
            {
                int mx = (int)(c.W * skip), my = (int)(c.H * skip);
                return new[]
                {
                    Side(id, true, c.MinY, c.MinX + mx, c.MaxX - mx, 1), Side(id, true, c.MaxY, c.MinX + mx, c.MaxX - mx, -1),
                    Side(id, false, c.MinX, c.MinY + my, c.MaxY - my, 1), Side(id, false, c.MaxX, c.MinY + my, c.MaxY - my, -1),
                }.Min();
            }

            // a lone symbol ("O", "@", "©") is usually OCR reading a small circle or an icon as a letter: it does not count as text
            var allWords = lines.SelectMany(l => l.Words).ToList();
            var words = allWords.Where(w => w.Text.Length >= 2).Select(w => w.Box).ToList();
            bool IsText(Rectangle r) => words.Any(w => { var i = Rectangle.Intersect(Rectangle.Inflate(w, 1, 1), r); return !i.IsEmpty && i.Width * i.Height * 2 > r.Width * r.Height; });
            // for checkboxes and options only a word much wider than the shape counts: the "O" of "OK" yes, the small circle read as "C)" no
            // and also a short word in the middle of a line ("in" of "Stored in local folder"): the symbol misread is always the first word of the name
            bool InsideWord(Rectangle r) => lines.Any(l => l.Words.Select((w, i) => (w, i)).Any(t =>
                Rectangle.Inflate(t.w.Box, 1, 1).Contains(r.Left + r.Width / 2, r.Top + r.Height / 2) && (t.w.Box.Width >= r.Width * 1.6 || t.i > 0)));
            // there is text right next to it on the right (checkboxes and options almost always have their name beside them; toolbar icons do not)
            bool LabelRight(Rectangle r) => allWords.Any(w => w.Box.Left >= r.Right - 2 && w.Box.Left - r.Right < 30 && w.Text.Length >= 2 &&
                                                              Math.Abs(w.Box.Top + w.Box.Height / 2 - (r.Top + r.Height / 2)) < 10);
            // checkboxes and options are drawn with few colors (border, background, mark); a folder or program icon has many
            bool FewColors(Rectangle r)
            {
                var inner = Rectangle.Inflate(r, -2, -2);
                if (inner.Width < 3 || inner.Height < 3) return true;
                var bins = new Dictionary<int, int>();
                for (var y = inner.Top; y < inner.Bottom; y++)
                    for (var x = inner.Left; x < inner.Right; x++)
                    {
                        var p = rgb[y * W + x];
                        var k = ((p >> 21) & 7) << 6 | ((p >> 13) & 7) << 3 | ((p >> 5) & 7);
                        bins[k] = bins.GetValueOrDefault(k) + 1;
                    }
                var all = inner.Width * inner.Height;
                return bins.Values.Count(n => n >= all * 0.06) <= 3;   // background, border/fill, mark
            }

            // diagonal steps from each corner (with one pixel of tolerance) before touching the piece, on average: square even if rounded ~0-1,
            // circle about 15% of the side
            double CornerDepth(int id, Comp c)
            {
                var depth = 0.0;
                foreach (var (x0, y0, sx, sy) in new[] { (c.MinX, c.MinY, 1, 1), (c.MaxX, c.MinY, -1, 1), (c.MinX, c.MaxY, 1, -1), (c.MaxX, c.MaxY, -1, -1) })
                {
                    var t = 0;
                    bool Near(int cx, int cy) { for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++) if (Has(id, cx + dx, cy + dy)) return true; return false; }
                    while (t < Math.Min(c.W, c.H) / 2 && !Near(x0 + t * sx, y0 + t * sy)) t++;
                    depth += t / 4.0;
                }
                return depth;
            }

            // a round option looks the same in the 4 directions from the center (ring, dot); an icon almost never does
            bool Symmetric(Rectangle r)
            {
                // the 4 rays from the center (right, left, down, up) must have the same profile; antialiasing shifts edges by half a pixel,
                // so each point is compared with the most similar of the 3 neighbors on the other ray
                int rad = Math.Min(r.Width, r.Height) / 2 - 1, cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
                int At(int x, int y) => g[Math.Clamp(y, 0, H - 1) * W + Math.Clamp(x, 0, W - 1)];
                var rays = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }.Select(v => Enumerable.Range(0, rad + 2).Select(d => At(cx + v.Item1 * d, cy + v.Item2 * d)).ToArray()).ToArray();
                bool Same(int[] p, int[] q)
                {
                    var bad = 0;
                    for (var d = 1; d <= rad; d++)
                        if (new[] { q[d - 1], q[d], q[d + 1] }.Min(v => Math.Abs(v - p[d])) > 40 && ++bad > 0) return false;
                    return true;
                }
                // diagonals too: the arrows of a round icon (Windows Update) break the symmetry off the axes
                var diag = new[] { (1, 1), (-1, -1), (1, -1), (-1, 1) }.Select(v => Enumerable.Range(0, rad + 2).Select(d => At(cx + v.Item1 * d * 7 / 10, cy + v.Item2 * d * 7 / 10)).ToArray()).ToArray();
                return Same(rays[0], rays[1]) && Same(rays[2], rays[3]) && Same(rays[0], rays[2]) && Same(diag[0], diag[1]) && Same(diag[2], diag[3]) && Same(diag[0], diag[2]);
            }

            // 3. shapes
            var rects = new List<(Rectangle box, string kind)>();
            var used = new HashSet<int>();
            // Windows 11 style fields: no border, just a dark line under a box slightly lighter (or darker) than the background
            for (var id = 1; id < comps.Count; id++)
            {
                var c = comps[id];
                if (c.H > 6 || c.W < 30 || c.W > 600 || c.MinY < 16 || c.Count < c.W * 0.8) continue;   // up to 6: rounded corners rise a bit
                // column on the right: the field's text is usually on the left
                int x0 = c.MaxX - 6, inside = g[(c.MinY - 2) * W + x0], top = c.MinY - 1, miss = 0;
                // go up while the color stays that of the inside; a single different pixel is JPEG noise
                for (var y = c.MinY - 2; y > c.MinY - 42 && y > 0; y--)
                {
                    if (Math.Abs(g[y * W + x0] - inside) <= 3) { top = y; miss = 0; }   // tight: the inside (255) and the background (249) differ only slightly
                    else if (++miss >= 2) break;
                }
                var h = c.MinY - top;
                if (DebugPoints != null && DebugPoints.Any(p => c.Box.Contains(p)))
                    DebugLog.Add($"line {c.Box} n={c.Count} inside={inside} height={h} outside={(c.MinX >= 3 ? g[(c.MinY - Math.Max(1, h / 2)) * W + c.MinX - 2] : -1)}");
                if (h < 14 || h > 32) continue;
                // the inside must stand out from the background just outside, on the left (even slightly: 255 on 249)
                if (c.MinX < 3 || Math.Abs(g[(c.MinY - h / 2) * W + c.MinX - 2] - inside) < 3) continue;
                rects.Add((new Rectangle(c.MinX, top, c.W, h + c.H), "line field"));
                used.Add(id);
            }
            if (DebugPoints != null)
                foreach (var p in DebugPoints)
                {
                    DebugLog.Add($"vertical grays from {p.X},{p.Y - 14}: " + string.Join(" ", Enumerable.Range(p.Y - 14, 29).Where(y => y >= 0 && y < H).Select(y => g[y * W + p.X])));
                    DebugLog.Add($"horizontal grays from {p.X - 40},{p.Y}: " + string.Join(" ", Enumerable.Range(p.X - 40, 81).Where(x => x >= 0 && x < W).Select(x => g[p.Y * W + x])));
                }
            for (var id = 1; id < comps.Count; id++)
            {
                var c = comps[id];
                if (DebugPoints != null && DebugPoints.Any(p => c.Box.Contains(p) && c.W < 400))
                {
                    var sk = 0.18; int dx = (int)(c.W * sk), dy = (int)(c.H * sk);
                    DebugLog.Add($"piece {c.Box} n={c.Count} sides T{Side(id, true, c.MinY, c.MinX + dx, c.MaxX - dx, 1):0.00} B{Side(id, true, c.MaxY, c.MinX + dx, c.MaxX - dx, -1):0.00} " +
                                 $"L{Side(id, false, c.MinX, c.MinY + dy, c.MaxY - dy, 1):0.00} R{Side(id, false, c.MaxX, c.MinY + dy, c.MaxY - dy, -1):0.00} text={IsText(c.Box)} insideWord={InsideWord(c.Box)} " +
                                 $"name={LabelRight(c.Box)} colors={FewColors(c.Box)} symmetric={Symmetric(c.Box)} corners={CornerDepth(id, c):0.0}");
                }
                if (c.W < 10 || c.H < 10 || c.W > W * 0.6 || c.H > H * 0.5) continue;
                var small = c.W <= 24 && c.H <= 24 && Math.Abs(c.W - c.H) <= 3;
                if (!small && c.H < 12) continue;
                if (small ? InsideWord(c.Box) : IsText(c.Box)) continue;   // letters like "O" and "D", pieces of words
                if (small)
                {
                    if (MinSide(id, c, 0.3) < 0.8 || !LabelRight(c.Box) || !FewColors(c.Box) && !Symmetric(c.Box)) continue;   // otherwise it is (maybe) an icon
                    rects.Add((c.Box, CornerDepth(id, c) >= 0.75 ? "radio" : "checkbox"));
                    used.Add(id);
                    continue;
                }
                var corners = Math.Max(2, Math.Min(4, Math.Min(c.W, c.H) / 4));
                var skip = corners / (double)Math.Min(c.W, c.H);
                int mx = (int)(c.W * skip), my = (int)(c.H * skip);
                double top = Side(id, true, c.MinY, c.MinX + mx, c.MaxX - mx, 1), bottom = Side(id, true, c.MaxY, c.MinX + mx, c.MaxX - mx, -1),
                       left = Side(id, false, c.MinX, c.MinY + my, c.MaxY - my, 1), right = Side(id, false, c.MaxX, c.MinY + my, c.MaxY - my, -1);
                // all 4 sides, or the classic 3D field style: top and left dark, the third side visible, the fourth as light as the background
                // a border is a thin line (a few pixels per perimeter point); a filled icon has edges everywhere, inside too
                // and corners close to the vertices: a circle or a round icon passes the sides test but has empty corners
                var thin = c.Count <= 3.0 * 2 * (c.W + c.H) && CornerDepth(id, c) <= Math.Max(2.5, Math.Min(c.W, c.H) * 0.08);
                if (thin && (Math.Min(Math.Min(top, bottom), Math.Min(left, right)) >= 0.85 || top >= 0.9 && left >= 0.9 && Math.Max(bottom, right) >= 0.9))
                {
                    rects.Add((c.Box, "rectangle"));
                    used.Add(id);
                }
            }

            // outer and inner border of the same control (or the focus rectangle): keep the larger one
            var shapes = new List<(Rectangle box, string kind)>();
            foreach (var r in rects.OrderByDescending(r => r.box.Width * r.box.Height))
                if (!shapes.Any(s => Math.Abs(s.box.Left - r.box.Left) <= 4 && Math.Abs(s.box.Top - r.box.Top) <= 4 &&
                                     Math.Abs(s.box.Right - r.box.Right) <= 4 && Math.Abs(s.box.Bottom - r.box.Bottom) <= 4))
                    shapes.Add(r);

            // 4. names and states from the OCR text
            var result = new List<Element>();
            var usedLines = new HashSet<McpOcr.Line>();
            var usedWords = new HashSet<McpOcr.Word>();
            void MarkLines() { foreach (var l in lines.Where(l => l.Words.All(usedWords.Contains))) usedLines.Add(l); }
            var lastName = Rectangle.Empty;   // where the last name taken by RightOf, LeftWords or Take was (for Element.NameBox)
            Rectangle Union(IEnumerable<McpOcr.Word> ws) => ws.Select(w => w.Box).DefaultIfEmpty().Aggregate(Rectangle.Union);
            // name on the right (checkboxes, options, icons), word by word: OCR often puts the small circle read as "@" in the same line as the name
            string RightOf(Rectangle r)
            {
                // inside the shape only symbols (1-2 characters) are discarded: an icon widened over the text ("pc01" next to the PC icon,
                // Active Directory) made real words disappear
                foreach (var w in allWords.Where(w => !usedWords.Contains(w) && w.Text.Length <= 2 && Rectangle.Inflate(r, 2, 2).Contains(w.Box.Left + w.Box.Width / 2, w.Box.Top + w.Box.Height / 2)).ToList())
                    usedWords.Add(w);
                bool Next(McpOcr.Word w) => !usedWords.Contains(w) && w.Box.Left >= r.Right - 2 && w.Box.Left - r.Right < 30 &&
                                            Math.Abs(w.Box.Top + w.Box.Height / 2 - (r.Top + r.Height / 2)) < 10;
                lastName = Rectangle.Empty;
                var line = lines.Where(l => !usedLines.Contains(l) && l.Words.Any(Next)).OrderBy(l => l.Words.Where(Next).Min(w => w.Box.Left)).FirstOrDefault();
                if (line == null) { MarkLines(); return ""; }
                var taken = new List<McpOcr.Word>();
                foreach (var w in line.Words.Where(w => !usedWords.Contains(w) && w.Box.Left >= r.Right - 2).OrderBy(w => w.Box.Left))
                {
                    if (taken.Count > 0 && w.Box.Left - taken[^1].Box.Right > 25) break;   // after a big gap it is another piece of text
                    taken.Add(w);
                }
                foreach (var w in taken) usedWords.Add(w);
                MarkLines();
                lastName = Union(taken);
                return string.Join(" ", taken.Select(w => w.Text));
            }
            // name to the left of a field, word by word: "Maximum size (MB): 7721" is a single OCR line, the value is inside the field
            string LeftWords(Rectangle r)
            {
                bool Near(McpOcr.Word w) => !usedWords.Contains(w) && w.Box.Right <= r.Left + 2 && r.Left - w.Box.Right < 80 &&
                                            Math.Abs(w.Box.Top + w.Box.Height / 2 - (r.Top + r.Height / 2)) < Math.Max(8, r.Height / 2);
                lastName = Rectangle.Empty;
                var line = lines.Where(l => !usedLines.Contains(l) && l.Words.Any(Near)).OrderBy(l => r.Left - l.Words.Where(Near).Max(w => w.Box.Right)).FirstOrDefault();
                if (line == null) return "";
                var taken = new List<McpOcr.Word>();
                foreach (var w in line.Words.Where(w => !usedWords.Contains(w) && w.Box.Right <= r.Left + 2).OrderByDescending(w => w.Box.Right))
                {
                    if (taken.Count > 0 ? taken[^1].Box.Left - w.Box.Right > 25 : r.Left - w.Box.Right >= 80) break;
                    taken.Add(w);
                }
                foreach (var w in taken) usedWords.Add(w);
                MarkLines();
                lastName = Union(taken);
                return string.Join(" ", taken.AsEnumerable().Reverse().Select(w => w.Text));
            }
            string FieldName(Rectangle r) { var n = LeftWords(r); return n != "" ? n : Take(Above(r)); }
            McpOcr.Line? Above(Rectangle r) => lines.Where(l => !usedLines.Contains(l) && l.Box.Bottom <= r.Top + 2 && r.Top - l.Box.Bottom < 24 && Math.Abs(l.Box.Left - r.Left) < 30)
                                                   .OrderBy(l => r.Top - l.Box.Bottom).FirstOrDefault();
            string Take(McpOcr.Line? l) { lastName = l?.Box ?? Rectangle.Empty; if (l == null) return ""; usedLines.Add(l); return l.Text; }

            // checkbox and option state: there is something inside (mark, dot, fill with a light mark) or it is all one color
            bool Marked(Rectangle r)
            {
                var inner = Rectangle.Inflate(r, -3, -3);
                if (inner.Width < 3 || inner.Height < 3) return false;
                var hist = new int[32];
                for (var y = inner.Top; y < inner.Bottom; y++) for (var x = inner.Left; x < inner.Right; x++) hist[g[y * W + x] / 8]++;
                var mode = Array.IndexOf(hist, hist.Max());
                int far = 0, all = inner.Width * inner.Height;
                for (var y = inner.Top; y < inner.Bottom; y++) for (var x = inner.Left; x < inner.Right; x++) if (Math.Abs(g[y * W + x] / 8 - mode) > 5) far++;
                return far > all * 0.08;
            }

            foreach (var (box, kind) in shapes.OrderBy(s => s.box.Width * s.box.Height))
            {
                if (kind is "checkbox" or "radio")
                {
                    var name = RightOf(box);
                    var on = Marked(box);
                    result.Add(new Element(kind, box, name, kind == "checkbox" ? (on ? "checked" : "unchecked") : (on ? "selected" : "not selected")) { NameBox = lastName });
                    continue;
                }
                if (box.Height > 70)
                {
                    // large box (list, group, panel): only the name, the text inside stays as text
                    result.Add(new Element("box", box, Take(Above(box)), "") { NameBox = lastName });
                    continue;
                }
                // words, not lines: OCR can put the text of two nearby buttons in the same line
                var inside = lines.SelectMany(l => l.Words).Where(w => !usedWords.Contains(w) && box.Contains(w.Box.Left + w.Box.Width / 2, w.Box.Top + w.Box.Height / 2)).ToList();
                if (inside.Count > 0)
                {
                    var tb = inside.Select(w => w.Box).Aggregate(Rectangle.Union);
                    var text = string.Join(" ", inside.OrderBy(w => w.Box.Y / 8).ThenBy(w => w.Box.X).Select(w => w.Text));
                    foreach (var w in inside) usedWords.Add(w);
                    MarkLines();
                    var centered = Math.Abs(tb.Left + tb.Width / 2 - (box.Left + box.Width / 2)) < Math.Max(8, box.Width * 0.12);
                    if (kind == "line field") result.Add(new Element("field", box, FieldName(box), "contains: " + text) { NameBox = lastName });
                    else if (centered && box.Width <= 400) result.Add(new Element("button", box, text, ""));
                    else result.Add(new Element("field", box, FieldName(box), "contains: " + text) { NameBox = lastName });
                }
                else if (kind == "line field" || box.Width >= box.Height * 2.5) result.Add(new Element("field", box, FieldName(box), "empty") { NameBox = lastName });
                else result.Add(new Element("box", box, "", "no text"));
            }

            // 5. icons: small dense pieces that are neither text nor part of a control; nearby pieces (a multicolor icon) are merged
            var controls = result.Select(e => e.Box).ToList();
            var bits = new List<Rectangle>();
            for (var id = 1; id < comps.Count && bits.Count < 4000; id++)
            {
                var c = comps[id];
                if (used.Contains(id) || c.W > 64 || c.H > 64 || c.Count < 4) continue;
                var r = c.Box;
                if (IsText(r) || controls.Any(k => k.Contains(r))) continue;
                bits.Add(r);
            }
            for (var merged = true; merged;)
            {
                merged = false;
                for (var i = 0; i < bits.Count && !merged; i++)
                    for (var j = i + 1; j < bits.Count; j++)
                    {
                        if (!Rectangle.Inflate(bits[i], 2, 2).IntersectsWith(bits[j])) continue;
                        var u = Rectangle.Union(bits[i], bits[j]);
                        if (u.Width > 64 || u.Height > 64) continue;
                        bits[i] = u; bits.RemoveAt(j); merged = true; break;
                    }
            }
            foreach (var r in bits.Where(r => r.Width >= 12 && r.Height >= 12 && r.Width <= 2 * r.Height && r.Height <= 2 * r.Width && !IsText(r)).Take(150))
            {
                // a small square drawn inside the icon is not a checkbox
                result.RemoveAll(e => e.Kind is "checkbox" or "radio" && Rectangle.Inflate(r, 1, 1).Contains(e.Box));
                result.Add(new Element("icon", r, RightOf(r), "") { NameBox = lastName });
            }

            // 6. the remaining text: it can be clicked too (links, menu items, flat borderless buttons)
            foreach (var l in lines.Where(l => !usedLines.Contains(l)))
            {
                var rest = l.Words.Where(w => !usedWords.Contains(w)).ToList();
                if (rest.Count == l.Words.Count) result.Add(new Element("text", l.Box, l.Text, ""));
                else if (rest.Count > 0) result.Add(new Element("text", rest.Select(w => w.Box).Aggregate(Rectangle.Union), string.Join(" ", rest.Select(w => w.Text)), ""));
            }

            return result.OrderBy(e => (e.Box.Top + e.Box.Height / 2) / 12).ThenBy(e => e.Box.Left).ToList();
        }
    }
}
