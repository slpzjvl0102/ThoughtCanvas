using System.IO.Compression;
using System.Net;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ThoughtCanvas;

public record RegionData(string Name, double X, double Y, double Size, List<string> SourceIds, byte[] Png);
public static class ExportService
{
    public const double RegionSize = 1200;
    public static List<RegionData> RenderRegions(Board board)
    {
        var ink = board.Ink.Where(x => x.Points.Count > 0).Select(x => (Item: x, Stroke: x.ToStroke())).ToArray();
        var cells = new HashSet<(int X, int Y)>();
        void Add(Rect b)
        {
            int minX = (int)Math.Floor(b.Left / RegionSize), maxX = (int)Math.Floor(b.Right / RegionSize);
            int minY = (int)Math.Floor(b.Top / RegionSize), maxY = (int)Math.Floor(b.Bottom / RegionSize);
            if ((long)(maxX - minX + 1) * (maxY - minY + 1) > 100) throw new InvalidOperationException("매우 큰 요소가 있습니다. 구역 Export를 위해 요소를 줄이거나 나누어 주세요.");
            for (int x = minX; x <= maxX; x++) for (int y = minY; y <= maxY; y++) cells.Add((x, y));
        }
        foreach (var i in ink) Add(i.Stroke.GetBounds()); foreach (var t in board.Texts) Add(BoardSurface.TextBounds(t));
        if (cells.Count > 100) throw new InvalidOperationException("현재 Prototype은 한 번에 100개 구역까지 Export합니다. 원본은 그대로 저장되어 있습니다.");
        var regions = new List<RegionData>();
        foreach (var (x, y) in cells.OrderBy(c => c.Y).ThenBy(c => c.X))
        {
            var bounds = new Rect(x * RegionSize - 24, y * RegionSize - 24, RegionSize + 48, RegionSize + 48);
            var sources = new List<string>(); var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, bounds.Width, bounds.Height));
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, bounds.Width, bounds.Height)));
                dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
                foreach (var i in ink.Where(i => bounds.IntersectsWith(i.Stroke.GetBounds()))) { i.Stroke.Draw(dc); sources.Add(i.Item.Id); }
                foreach (var t in board.Texts.Where(t => bounds.IntersectsWith(BoardSurface.TextBounds(t)))) { dc.DrawText(BoardSurface.Format(t), new Point(t.X, t.Y)); sources.Add(t.Id); }
                dc.Pop(); dc.Pop();
            }
            var bitmap = new RenderTargetBitmap((int)bounds.Width, (int)bounds.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = new MemoryStream(); encoder.Save(stream);
            regions.Add(new($"region_{x}_{y}", bounds.X, bounds.Y, bounds.Width, sources, stream.ToArray()));
        }
        return regions;
    }
    public static string Export(Board board, string root, List<RegionData> regions, IEnumerable<Board>? sourceVersions = null)
    {
        string directory = Path.Combine(root, $"{SafeName(board.Title)}_{board.Id[..8]}", $"v{board.Revision}_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}");
        Directory.CreateDirectory(directory);
        // 중간에 실패하면 쓰다 만 Export 폴더를 남기지 않습니다 (v0.7.4).
        try { Fill(directory, board, regions, sourceVersions); return directory; }
        catch { try { Directory.Delete(directory, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } throw; }
    }
    private static void Fill(string directory, Board board, List<RegionData> regions, IEnumerable<Board>? sourceVersions)
    {
        var versions = (sourceVersions ?? []).GroupBy(b => b.Revision).ToDictionary(g => g.Key, g => g.First());
        Write(directory, "AppData/원본/board.json", DataJson.Write(board));
        foreach (var source in versions.Values) Write(directory, $"AppData/복원 지점/v{source.Revision}.json", DataJson.Write(source));
        Write(directory, "AppData/구조화 기록/analysis.json", DataJson.Write(board.Analysis));
        Write(directory, "AppData/구조화 기록/document-edits.json", DataJson.Write(board.DocumentEdits));
        Write(directory, "AppData/구조화 기록/regions.json", DataJson.Write(regions.Select(r => new { r.Name, r.X, r.Y, r.Size, r.SourceIds, SourceRevision = board.Revision })));
        string doc = board.UserDocument.Length > 0 ? board.UserDocument : board.Analysis?.Document ?? "아직 AI 문서를 생성하지 않았습니다.";
        Write(directory, "문서/정리.html", Html(board.Title, doc, board).Replace("<main>", "<p><a href='../구역별 보기/index.html'>문서 구조별 원본 보기</a></p><main>"));
        Write(directory, "문서/정리.md", "# " + board.Title + "\n\n" + doc);
        foreach (var (topic, index) in board.TopicDocuments.Select((t, i) => (t, i)))
        {
            string section = topic.Archived ? "검토했던 아이디어" : "문서";
            Write(directory, $"{section}/주제별/{index + 1}_{SafeName(topic.Topic)}.html", Html(topic.Topic, topic.Text, board));
            Write(directory, $"{section}/주제별/{index + 1}_{SafeName(topic.Topic)}.md", "# " + topic.Topic + "\n\n" + topic.Text);
        }
        Write(directory, "검토했던 아이디어/검토.html", Html("검토했던 아이디어", board.Analysis?.ArchivedDocument ?? "아직 분석하지 않았습니다.", board));
        var links = new List<string>();
        foreach (var r in regions)
        {
            string path = Path.Combine(directory, "구역별 보기", r.Name + ".png"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, r.Png);
            links.Add($"<section><h2>{r.Name}</h2><img loading='lazy' src='{r.Name}.png' style='max-width:100%'></section>");
        }
        Write(directory, "구역별 보기/원본격자.html", "<!doctype html><meta charset='utf-8'><title>Canvas 원본 격자</title>" + string.Join("", links));
        var sections = SourceSections.Build(board);
        Directory.CreateDirectory(Path.Combine(directory, "구역별 보기"));
        Write(directory, "AppData/구조화 기록/source-sections.json", DataJson.Write(sections));
        var body = new System.Text.StringBuilder("<p><a href='../문서/정리.html'>정리 문서</a> · <a href='원본격자.html'>전체 원본 격자</a></p><p>진한 부분: 해당 항목의 원본 · 흐린 부분: 주변 맥락</p><nav>");
        foreach (var s in sections) body.Append($"<p><a href='#{s.Key}'>{WebUtility.HtmlEncode((s.Archived ? "[검토] " : "") + s.Topic + " / " + s.Category)}</a></p>");
        body.Append("</nav>");
        foreach (var s in sections)
        {
            body.Append($"<section id='{s.Key}'><h2>{WebUtility.HtmlEncode((s.Archived ? "[검토] " : "") + s.Topic)}</h2><h3>{WebUtility.HtmlEncode(s.Category)}</h3>{NoteMarkdown.Html(s.Text)}");
            var source = s.Revision == board.Revision ? board : versions.GetValueOrDefault(s.Revision);
            if (source == null) body.Append("<p>해당 항목의 과거 원본을 찾지 못했습니다.</p>");
            else
            {
                int missing = s.SourceIds.Except(source.AllSourceIds()).Count();
                if (missing > 0) body.Append($"<p>연결 확인 필요: {missing}개</p>");
                try
                {
                    byte[] png = SourceSections.Render(source, s.SourceIds);
                    File.WriteAllBytes(Path.Combine(directory, "구역별 보기", s.Key + ".png"), png);
                    body.Append($"<img loading='lazy' src='{s.Key}.png' alt='{WebUtility.HtmlEncode(s.Category)} 원본'>");
                }
                catch (InvalidDataException ex) { body.Append("<p>" + WebUtility.HtmlEncode(ex.Message) + "</p>"); }
            }
            body.Append("</section>");
        }
        Write(directory, "구역별 보기/index.html", "<!doctype html><html lang='ko'><meta charset='utf-8'><title>문서 구조별 원본</title><style>body{font-family:'Malgun Gothic';max-width:1100px;margin:32px auto;padding:24px;line-height:1.7}section{border-top:1px solid #ccd;margin-top:40px}img{max-width:100%;height:auto}</style><h1>문서 구조별 원본</h1>" + body + "</html>");
    }
    private static void Write(string root, string relative, string value) => BoardStore.AtomicWrite(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)), value);
    public static string SafeName(string name) { foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_'); return name.Length > 60 ? name[..60] : name; }
    public static string Html(string title, string text, Board board) => $"<!doctype html><html lang='ko'><meta charset='utf-8'><title>{WebUtility.HtmlEncode(title)}</title><style>body{{font-family:'Malgun Gothic',sans-serif;max-width:900px;margin:48px auto;padding:0 24px;color:#243047;line-height:1.85}}pre{{white-space:pre-wrap;font:inherit}}small{{color:#667}}</style><h1>{WebUtility.HtmlEncode(title)}</h1><small>Board {board.Id} · 원본 v{board.Revision} · AI 기준 v{board.Analysis?.SourceRevision.ToString() ?? "없음"}</small><main>{NoteMarkdown.Html(text)}</main></html>";
}
