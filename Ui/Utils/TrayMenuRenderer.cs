using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Lemora.Utils
{
    /// <summary>
    /// Menu del vassoio (icona vicino all'orologio) nello stile Fluent Islands: pannello scuro con bordo, voci alte con selezione tenue,
    /// separatori sottili; per i preferiti la sigla del protocollo colorata, il nome in grassetto e sotto il sottotitolo;
    /// per le azioni (nuova connessione, impostazioni, esci) un'icona al posto della sigla.
    /// </summary>
    public class TrayMenuRenderer : ToolStripProfessionalRenderer
    {
        private static readonly Color Panel = Color.FromArgb(0x26, 0x26, 0x26);
        private static readonly Color Border = Color.FromArgb(0x4A, 0x4A, 0x4A);
        private static readonly Color Hover = Color.FromArgb(0x33, 0x38, 0x3C);
        private static readonly Color Separator = Color.FromArgb(0x3A, 0x3A, 0x3A);
        private static readonly Color Text = Color.FromArgb(0xF0, 0xF0, 0xF0);
        private static readonly Color Muted = Color.FromArgb(0x9A, 0x9A, 0x9A);
        private static readonly Color Accent = Color.FromArgb(0x60, 0xB4, 0xFF);
        private static readonly Color Danger = Color.FromArgb(0xFF, 0x9B, 0x94);

        public const string TagHeader = "header";
        public const string TagHint = "hint";
        public const string TagDanger = "danger";

        public TrayMenuRenderer() : base(new Colors()) { RoundedEdges = false; }

        /// <summary>stile dei colori del menu: da assegnare anche a font e margini della ContextMenuStrip</summary>
        public static void Apply(ContextMenuStrip menu)
        {
            menu.Renderer = new TrayMenuRenderer();
            menu.BackColor = Panel;
            menu.ForeColor = Text;
            menu.Font = new Font("Segoe UI Variable Text", 10f);
            menu.ShowImageMargin = false;
            menu.Padding = new Padding(4);
        }

        /// <summary>colore della sigla del protocollo (stessi colori dell'elenco server)</summary>
        public static Color ProtocolColor(string protocol) => protocol.ToUpperInvariant() switch
        {
            "RDP" or "REMOTEAPP" => Color.FromArgb(0x8A, 0xB4, 0xF8),
            "SSH" => Color.FromArgb(0x81, 0xC9, 0x95),
            "VNC" => Color.FromArgb(0xF6, 0xAE, 0xA9),
            "SFTP" => Color.FromArgb(0xFD, 0xD6, 0x63),
            _ => Color.FromArgb(0xC4, 0xC7, 0xC5),
        };

        /// <summary>titolo di sezione (non selezionabile)</summary>
        public static ProtocolItem Header(string text) => new("", text) { Tag = TagHeader, Enabled = false, RowHeight = 30 };

        /// <summary>riga di spiegazione in grigio (non selezionabile)</summary>
        public static ProtocolItem Hint(string text) => new("", text) { Tag = TagHint, Enabled = false, RowHeight = 34 };

        public const int MinRowWidth = 290, MaxRowWidth = 440;

        /// <summary>da chiamare dopo aver riempito il menu: tutte le righe prendono la larghezza della piu' larga (minimo 290, massimo 440)</summary>
        public static void EqualizeWidths(ToolStrip menu)
        {
            var rows = menu.Items.OfType<ProtocolItem>().ToList();
            var w = Math.Min(Math.Max(rows.Select(r => r.NeedWidth).DefaultIfEmpty(0).Max(), MinRowWidth), MaxRowWidth);
            foreach (var r in rows) r.RowWidth = w;
            // il menu aggiunge a destra uno spazio fisso (per le frecce dei sottomenu, qui inutili): larghezza decisa da noi,
            // con lo stesso margine a destra e a sinistra (le righe partono a x=8), altezza quella calcolata da WinForms
            menu.AutoSize = true;
            var h = menu.GetPreferredSize(Size.Empty).Height;
            menu.AutoSize = false;
            menu.Size = new Size(w + 16, h);
        }

        /// <summary>
        /// Riga del menu: sigla del protocollo (o un'icona) prima del nome; con <c>Sub</c> il nome va in grassetto e sotto compare il sottotitolo.
        /// E' un ToolStripButton e non un ToolStripMenuItem: il menu misura le voci di menu solo dal loro testo (ignorando sigla, sottotitolo e
        /// qualsiasi larghezza indicata), con il risultato di testi tagliati o di un vuoto a destra della selezione. Per gli altri tipi di voce
        /// usa invece la larghezza che restituiamo noi, uguale per tutte le righe (<see cref="EqualizeWidths"/>).
        /// </summary>
        public class ProtocolItem : ToolStripButton
        {
            /// <summary>larghezza che servirebbe a questa riga per mostrare tutto il testo</summary>
            public int NeedWidth { get; }
            [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
            public int RowWidth { get; set; } = MinRowWidth;
            [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
            public int RowHeight { get; set; }
            public string Sigla { get; }
            public string? Sub { get; }
            /// <summary>colore dell'icona/sigla; vuoto = colore del protocollo</summary>
            [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
            public Color? LeadColor { get; set; }
            /// <summary>true: <c>Sigla</c> e' un carattere di Segoe MDL2 Assets (icona), non una sigla di testo</summary>
            [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
            public bool IsGlyph { get; set; }
            [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
            public bool Bold { get; set; }

            public ProtocolItem(string sigla, string text, string? sub = null) : base(text)
            {
                Sigla = sigla; Sub = sub; Bold = sub != null;
                AutoToolTip = false;
                RowHeight = sub != null ? 46 : 36;
                using var fb = new Font("Segoe UI Variable Text", 10f, FontStyle.Bold);
                using var small = new Font("Segoe UI Variable Text", 8.5f);
                var lead = sigla.Length > 0 ? 56 : 0;
                NeedWidth = 14 + lead + Math.Max(TextRenderer.MeasureText(text, fb).Width, sub == null ? 0 : TextRenderer.MeasureText(sub, small).Width) + 16;
            }

            public override Size GetPreferredSize(Size constrainingSize) => new(RowWidth, RowHeight);
        }

        /// <summary>larghezza utile della riga: quella del menu (le voci possono risultare piu' strette, e a destra resterebbe un vuoto)</summary>
        private static int RowWidth(ToolStripItem item) =>
            item.Owner is { } o ? Math.Max(item.Width, o.ClientSize.Width - 2 * item.Bounds.X) : item.Width;

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var b = new SolidBrush(Panel);
            e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var p = new Pen(Border);
            e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e) => OnRenderMenuItemBackground(e);

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            using var b = new SolidBrush(Hover);
            var r = new Rectangle(2, 1, RowWidth(e.Item) - 4, e.Item.Height - 2);
            e.Graphics.FillRectangle(b, r);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var p = new Pen(Separator);
            var y = e.Item.Height / 2;
            e.Graphics.DrawLine(p, 8, y, RowWidth(e.Item) - 8, y);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var item = e.Item;
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            var x = 14;
            var rw = RowWidth(item);
            var tag = item.Tag as string;
            if (tag == TagHeader)
            {
                using var f = new Font(item.Font.FontFamily, 8.5f, FontStyle.Bold);
                TextRenderer.DrawText(e.Graphics, (item.Text ?? "").ToUpperInvariant(), f, new Rectangle(x, 0, rw - x - 8, item.Height), Accent, flags);
                return;
            }
            if (tag == TagHint)
            {
                TextRenderer.DrawText(e.Graphics, item.Text, item.Font, new Rectangle(x, 0, rw - x - 8, item.Height), Muted, flags);
                return;
            }

            var color = tag == TagDanger ? Danger : Text;
            var textFont = item.Font;
            if (item is ProtocolItem pi)
            {
                var leadColor = pi.LeadColor ?? ProtocolColor(pi.Sigla);
                var lead = new Rectangle(x, 0, 52, item.Height);
                if (pi.IsGlyph)
                {
                    using var g = new Font("Segoe MDL2 Assets", 11f);
                    TextRenderer.DrawText(e.Graphics, pi.Sigla, g, lead, leadColor, flags);
                }
                else
                {
                    using var f = new Font(item.Font, FontStyle.Bold);
                    TextRenderer.DrawText(e.Graphics, pi.Sigla, f, lead, leadColor, flags);
                }
                x += 56;
                if (pi.Bold) textFont = new Font(item.Font, FontStyle.Bold);

                if (pi.Sub != null)
                {
                    TextRenderer.DrawText(e.Graphics, item.Text, textFont, new Rectangle(x, 4, rw - x - 8, 22), color, flags);
                    using var small = new Font(item.Font.FontFamily, 8.5f);
                    TextRenderer.DrawText(e.Graphics, pi.Sub, small, new Rectangle(x, 24, rw - x - 8, 18), Muted, flags);
                    if (pi.Bold) textFont.Dispose();
                    return;
                }
            }
            TextRenderer.DrawText(e.Graphics, item.Text, textFont, new Rectangle(x, 0, rw - x - 8, item.Height), color, flags);
            if (!ReferenceEquals(textFont, item.Font)) textFont.Dispose();
        }

        private sealed class Colors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Panel;
            public override Color MenuBorder => Border;
            public override Color MenuItemBorder => Hover;
            public override Color MenuItemSelected => Hover;
            public override Color ImageMarginGradientBegin => Panel;
            public override Color ImageMarginGradientMiddle => Panel;
            public override Color ImageMarginGradientEnd => Panel;
            public override Color SeparatorDark => Separator;
            public override Color SeparatorLight => Separator;
        }
    }
}
