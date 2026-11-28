using System;
using System.Collections.Generic;
using System.Linq;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Wpf;

namespace Lemora.Controls.NoteDisplay
{
    /// <summary>
    /// Markdown notes (also those from an import or a shared source): images load on their own as soon as the note is shown.
    /// A file://server/... or http://... image would send Windows credentials to another PC or tell
    /// whoever wrote the note when and from where it is read. Only https and embedded images (data:image) are kept (security hardening).
    /// </summary>
    public static class NoteMarkdown
    {
        private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseSupportedExtensions().Build();

        public static bool IsSafeImage(string? url) =>
            !string.IsNullOrWhiteSpace(url)
            && (url!.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
                || (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && !u.IsUnc));

        /// <summary>note text with the addresses of unsafe images replaced by "#" (the image does not load, the text stays)</summary>
        public static string SafeForDisplay(string? markdown)
        {
            if (string.IsNullOrEmpty(markdown)) return "";
            MarkdownDocument doc;
            try { doc = Markdig.Markdown.Parse(markdown, Pipeline); }
            catch { return ""; }
            var spans = new List<SourceSpan>();
            foreach (var link in doc.Descendants<LinkInline>().Where(l => l.IsImage))
            {
                if (IsSafeImage(link.Url)) continue;
                if (link.UrlSpan is { IsEmpty: false } urlSpan) spans.Add(urlSpan);
                if (link.Reference is { } def && def.UrlSpan is { IsEmpty: false } refSpan) spans.Add(refSpan);
            }
            if (spans.Count == 0) return markdown;
            var text = markdown;
            foreach (var s in spans.Distinct().OrderByDescending(x => x.Start))
            {
                if (s.Start < 0 || s.End >= text.Length) continue;   // End is the last included character
                text = text.Substring(0, s.Start) + "#" + text.Substring(s.End + 1);
            }
            return text;
        }
    }
}
