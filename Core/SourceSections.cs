using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace ThoughtCanvas;

public record SourceSection(string Key, string Topic, string Category, long Revision, List<string> SourceIds, string Text, bool Archived = false);
public static class SourceSections
{
    public static List<SourceSection> Build(Board board)
    {
        var list = new List<SourceSection>(); string document = board.UserDocument;
        var groups = (board.Analysis?.Ideas ?? []).GroupBy(i => (i.Topic, i.Category, i.SourceRevision, Archived: i.Status == "cancelled_or_deferred"))
            .OrderBy(g => g.Key.Archived).ThenBy(g => { int index = document.IndexOf(g.Key.Topic, StringComparison.Ordinal); return index < 0 ? int.MaxValue : index; });
        foreach (var group in groups)
            list.Add(new("section_" + list.Count, group.Key.Topic, group.Key.Category, group.Key.SourceRevision,
                group.SelectMany(i => i.SourceIds).Distinct().ToList(), string.Join("\n", group.Select(i => "- " + i.Content)), group.Key.Archived));
        var linked = (board.Analysis?.Ideas.SelectMany(i => i.SourceIds) ?? []).ToHashSet();
        var remaining = board.AllSourceIds().Except(linked).ToList();
        if (remaining.Count > 0) list.Add(new("unmapped", "정리 전·연결 미확인", "원본 확인 필요", board.Revision, remaining, "문서의 특정 항목과 아직 연결되지 않은 원본입니다. 임의로 다른 주제에 배정하지 않았습니다."));
        return list;
    }
    public static byte[] Render(Board board, IEnumerable<string> ids)
    {
        var selected = ids.ToHashSet(); var target = new Board { Ink = board.Ink.Where(i => selected.Contains(i.Id)).ToList(), Texts = board.Texts.Where(t => selected.Contains(t.Id)).ToList() };
        var bounds = SyncEngine.Bounds(target); if (bounds.IsEmpty) throw new InvalidDataException("연결된 원본을 찾을 수 없습니다.");
        bounds.Inflate(48, 48); double scale = Math.Min(1.5, 2048 / Math.Max(bounds.Width, bounds.Height));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, bounds.Width * scale, bounds.Height * scale));
            dc.PushTransform(new ScaleTransform(scale, scale)); dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y)); dc.PushClip(new RectangleGeometry(bounds));
            foreach (bool active in new[] { false, true })
            {
                dc.PushOpacity(active ? 1 : .18);
                foreach (var i in board.Ink.Where(i => i.Points.Count > 0 && selected.Contains(i.Id) == active))
                { var stroke = i.ToStroke(); if (stroke.GetBounds().IntersectsWith(bounds)) stroke.Draw(dc); }
                foreach (var t in board.Texts.Where(t => selected.Contains(t.Id) == active && BoardSurface.TextBounds(t).IntersectsWith(bounds))) dc.DrawText(BoardSurface.Format(t), new Point(t.X, t.Y));
                dc.Pop();
            }
            dc.Pop(); dc.Pop(); dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(bounds.Width * scale)), Math.Max(1, (int)Math.Ceiling(bounds.Height * scale)), 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
}
