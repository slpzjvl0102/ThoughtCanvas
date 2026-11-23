using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace ThoughtCanvas;

public static class PdfExporter
{
    public static void Export(string directory, Board board, List<RegionData> regions)
    {
        var pages = new List<byte[]>();
        var doc = new FlowDocument { FontFamily = new FontFamily("Malgun Gothic"), FontSize = 14, PageWidth = 794, PageHeight = 1123, PagePadding = new Thickness(48), ColumnWidth = double.PositiveInfinity, Background = Brushes.White, Foreground = Brushes.Black };
        doc.Blocks.Add(new Paragraph(new Run(board.Title)) { FontSize = 24 });
        NoteMarkdown.Append(doc, board.UserDocument);
        doc.Blocks.Add(new Paragraph(new Run("검토했던 아이디어")) { FontSize = 22, BreakPageBefore = true });
        NoteMarkdown.Append(doc, board.Analysis?.ArchivedDocument ?? "없음");
        var paginator = ((IDocumentPaginatorSource)doc).DocumentPaginator; paginator.PageSize = new Size(794, 1123); paginator.ComputePageCount();
        for (int n = 0; n < paginator.PageCount; n++)
        {
            using var page = paginator.GetPage(n); var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) { dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 794, 1123)); dc.DrawRectangle(new VisualBrush(page.Visual), null, new Rect(0, 0, 794, 1123)); }
            var bitmap = new RenderTargetBitmap(1191, 1685, 144, 144, PixelFormats.Pbgra32); bitmap.Render(visual); pages.Add(Jpeg(bitmap));
        }
        Write(Path.Combine(directory, "문서.pdf"), pages.Select(j => (j, 1191, 1685)).ToList());
        if (regions.Count > 0)
        {
            var images = regions.Select(r => { using var s = new MemoryStream(r.Png); var b = BitmapFrame.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad); return (Jpeg(b), b.PixelWidth, b.PixelHeight); }).ToList();
            Write(Path.Combine(directory, "Canvas-전체격자.pdf"), images);
        }
        var sectionPages = new List<(byte[] Data, int Width, int Height)>();
        foreach (var section in SourceSections.Build(board))
        {
            string path = Path.Combine(directory, "구역별 보기", section.Key + ".png");
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 794, 1123));
                var title = BoardSurface.Format(new TextItem { Text = (section.Archived ? "[검토] " : "") + section.Topic + " / " + section.Category, FontSize = 20, Width = 698 });
                dc.DrawText(title, new Point(48, 36));
                double top = 52 + title.Height;
                if (File.Exists(path))
                {
                    using var stream = File.OpenRead(path); var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    double scale = Math.Min(698d / bitmap.PixelWidth, Math.Max(1, 1075 - top) / bitmap.PixelHeight);
                    dc.DrawImage(bitmap, new Rect(48, top, bitmap.PixelWidth * scale, bitmap.PixelHeight * scale));
                }
                else dc.DrawText(BoardSurface.Format(new TextItem { Text = "연결된 과거 원본을 찾지 못했습니다.", Width = 698 }), new Point(48, top));
            }
            var page = new RenderTargetBitmap(1191, 1685, 144, 144, PixelFormats.Pbgra32); page.Render(visual); sectionPages.Add((Jpeg(page), 1191, 1685));
        }
        if (sectionPages.Count > 0) Write(Path.Combine(directory, "Canvas-구역.pdf"), sectionPages);
    }
    private static byte[] Jpeg(BitmapSource bitmap) { var encoder = new JpegBitmapEncoder { QualityLevel = 92 }; encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var s = new MemoryStream(); encoder.Save(s); return s.ToArray(); }
    public static void Write(string path, List<(byte[] Data, int Width, int Height)> pages)
    {
        using var stream = File.Create(path); var offsets = new List<long> { 0 };
        void Text(string text) { var bytes = Encoding.ASCII.GetBytes(text); stream.Write(bytes); }
        void Obj(int id, Action body) { offsets.Add(stream.Position); Text($"{id} 0 obj\n"); body(); Text("\nendobj\n"); }
        Text("%PDF-1.4\n"); Obj(1, () => Text("<< /Type /Catalog /Pages 2 0 R >>"));
        Obj(2, () => Text($"<< /Type /Pages /Count {pages.Count} /Kids [{string.Join(" ", Enumerable.Range(0, pages.Count).Select(n => $"{3 + n * 3} 0 R"))}] >>"));
        for (int n = 0; n < pages.Count; n++)
        {
            int id = 3 + n * 3; var page = pages[n];
            Obj(id, () => Text($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /XObject << /Img {id + 1} 0 R >> >> /Contents {id + 2} 0 R >>"));
            Obj(id + 1, () => { Text($"<< /Type /XObject /Subtype /Image /Width {page.Width} /Height {page.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {page.Data.Length} >>\nstream\n"); stream.Write(page.Data); Text("\nendstream"); });
            double scale = Math.Min(595d / page.Width, 842d / page.Height); double w = page.Width * scale, h = page.Height * scale;
            string content = FormattableString.Invariant($"q {w:0.###} 0 0 {h:0.###} {(595 - w) / 2:0.###} {(842 - h) / 2:0.###} cm /Img Do Q");
            Obj(id + 2, () => Text($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream"));
        }
        long start = stream.Position; Text($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (long offset in offsets.Skip(1)) Text($"{offset:D10} 00000 n \n");
        Text($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{start}\n%%EOF\n");
    }
}
