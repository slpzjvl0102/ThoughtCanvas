using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
namespace ThoughtCanvas;

// Supported note syntax: headings, nested bullet/numbered lines and bold spans.
// Raw HTML and executable links are always treated as plain text.
public static class NoteMarkdown
{
    public record Line(string Text, int Heading = 0, string Marker = "", int Indent = 0);
    public static IEnumerable<Line> Parse(string text)
    {
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var heading = Regex.Match(raw, @"^\s{0,3}(#{1,6})\s+(.+)$");
            if (heading.Success) { yield return new(heading.Groups[2].Value, heading.Groups[1].Length); continue; }
            var item = Regex.Match(raw, @"^(\s*)([-+*]|\d+[.)])\s+(.+)$");
            if (item.Success) yield return new(item.Groups[3].Value, Marker: item.Groups[2].Value, Indent: item.Groups[1].Value.Replace("\t", "    ").Length);
            else yield return new(raw.Trim());
        }
    }
    public static IEnumerable<(string Text, bool Bold)> Spans(string text)
    {
        int start = 0;
        foreach (Match match in Regex.Matches(text, @"\*\*(.+?)\*\*"))
        {
            if (match.Index > start) yield return (text[start..match.Index], false);
            yield return (match.Groups[1].Value, true); start = match.Index + match.Length;
        }
        if (start < text.Length) yield return (text[start..], false);
    }
    public static void Append(FlowDocument document, string text)
    {
        foreach (var line in Parse(text))
        {
            var p = new Paragraph { Margin = new Thickness(Math.Min(line.Indent, 24) * 9, line.Heading > 0 ? 16 : 3, 0, 6), LineHeight = 25 };
            if (line.Heading > 0) { p.FontSize = Math.Max(17, 27 - line.Heading * 2); p.FontWeight = FontWeights.SemiBold; p.KeepWithNext = true; }
            if (line.Marker.Length > 0) p.Inlines.Add(new Run(char.IsDigit(line.Marker[0]) ? line.Marker + "  " : "•  "));
            foreach (var span in Spans(line.Text)) p.Inlines.Add(new Run(span.Text) { FontWeight = span.Bold || line.Heading > 0 ? FontWeights.SemiBold : FontWeights.Normal });
            document.Blocks.Add(p);
        }
    }
    public static string Html(string text)
    {
        var html = new StringBuilder();
        foreach (var line in Parse(text))
        {
            string content = string.Concat(Spans(line.Text).Select(s => s.Bold ? "<strong>" + WebUtility.HtmlEncode(s.Text) + "</strong>" : WebUtility.HtmlEncode(s.Text)));
            if (line.Heading > 0) html.Append($"<h{line.Heading}>{content}</h{line.Heading}>");
            else if (line.Marker.Length > 0)
            {
                bool numbered = char.IsDigit(line.Marker[0]); string tag = numbered ? "ol" : "ul";
                string start = numbered && int.TryParse(line.Marker[..^1], out int n) ? $" start='{n}'" : "";
                html.Append($"<{tag}{start} style='margin:4px 0 4px {Math.Min(line.Indent, 24) * 9}px'><li>{content}</li></{tag}>");
            }
            else html.Append($"<p>{content}</p>");
        }
        return html.ToString();
    }
}
