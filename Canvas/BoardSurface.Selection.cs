using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;

namespace ThoughtCanvas;

public partial class BoardSurface : Canvas
{
    public static StreamGeometry Polygon(IEnumerable<Point> points)
    {
        var list = points.ToList(); var g = new StreamGeometry { FillRule = FillRule.EvenOdd };
        if (list.Count > 0) { using var c = g.Open(); c.BeginFigure(list[0], true, true); c.PolyLineTo(list.Skip(1).ToArray(), true, false); }
        return g;
    }
    public static bool FullyInside(Geometry lassoGeometry, Geometry element) => lassoGeometry.FillContainsWithDetail(element, .01, ToleranceType.Absolute) == IntersectionDetail.FullyContains;
    private void BeginSelectionDrag()
    {
        draggingSelection = true; dragDelta = new Vector(); dragTransform.X = dragTransform.Y = 0;
        // 원래 좌표를 한 번만 확보합니다. 드래그 중에는 Points 목록을 바꾸지 않으므로 참조를 그대로 보관합니다.
        DetachSelection(); // Undo 이력이 가진 원래 객체는 건드리지 않고, 복제본을 옮깁니다.
        dragInk = [.. Board.Ink.Where(i => Selection.Contains(i.Id)).Select(i => (i, i.Points))];
        dragTexts = [.. Board.Texts.Where(t => Selection.Contains(t.Id)).Select(t => (t, t.X, t.Y))];
        foreach (var (item, _) in dragInk) inkLayer.SetTransform(item.Id, dragTransform);
    }
    private void ApplySelectionDrag()
    {
        // 선택된 Stroke는 하나의 공유 Transform으로 한꺼번에 이동합니다 (Stroke 재생성 없음).
        dragTransform.X = dragDelta.X; dragTransform.Y = dragDelta.Y;
        if (dragTexts is { Count: > 0 }) foreach (var (text, x, y) in dragTexts) { text.X = x + dragDelta.X; text.Y = y + dragDelta.Y; }
        overlayLayer.InvalidateVisual();
    }
    private void CommitSelectionDrag()
    {
        if (dragInk != null)
            foreach (var (item, points) in dragInk)
            {
                inkLayer.SetTransform(item.Id, null);
                if (!moved) continue;
                item.Points = points.Select(p => new Sample(p.X + dragDelta.X, p.Y + dragDelta.Y, p.Pressure)).ToList();
                if (item.Points.Count > 0) { var stroke = item.ToStroke(); strokes[item.Id] = stroke; RenderInk(item.Id, stroke); }
            }
        if (dragTexts != null && !moved) foreach (var (text, x, y) in dragTexts) { text.X = x; text.Y = y; }
        dragInk = null; dragTexts = null; dragDelta = new Vector(); dragTransform.X = dragTransform.Y = 0;
    }
    // Undo 이력은 Board 목록만 얕게 복사합니다 (v0.6.7). 대신 기존 요소를 그 자리에서 바꾸기 전에
    // 복제본으로 교체해서, 이력이 참조하는 원래 객체가 바뀌지 않도록 합니다.
    private void DetachSelection()
    {
        for (int i = 0; i < Board.Ink.Count; i++) if (Selection.Contains(Board.Ink[i].Id)) Board.Ink[i] = DataJson.Clone(Board.Ink[i]);
        for (int i = 0; i < Board.Texts.Count; i++) if (Selection.Contains(Board.Texts[i].Id)) Board.Texts[i] = DataJson.Clone(Board.Texts[i]);
    }
    public void DeleteSelection()
    {
        CommitEditor(); if (Selection.Count == 0) return; History.Push(CanvasHistory.Capture(Board)); Board.Ink.RemoveAll(i => Selection.Contains(i.Id)); Board.Texts.RemoveAll(t => Selection.Contains(t.Id)); Selection.Clear(); Rebuild(); Changed?.Invoke();
    }
    public void ResizeSelection(double scale)
    {
        if (Selection.Count == 0) return; CommitEditor(); var b = Rect.Empty;
        foreach (var i in Board.Ink.Where(i => Selection.Contains(i.Id))) b.Union(strokes[i.Id].GetBounds());
        foreach (var t in Board.Texts.Where(t => Selection.Contains(t.Id))) b.Union(TextBounds(t));
        if (b.IsEmpty) return; History.Push(CanvasHistory.Capture(Board)); DetachSelection();
        // 선택 영역의 중심을 기준으로 확대·축소합니다 (v0.6.5, 이전: 좌상단 기준).
        double cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2;
        foreach (var i in Board.Ink.Where(i => Selection.Contains(i.Id)))
        {
            i.Points = i.Points.Select(p => new Sample(cx + (p.X - cx) * scale, cy + (p.Y - cy) * scale, p.Pressure)).ToList(); i.Width *= scale;
            // 선택된 Stroke만 다시 만듭니다 (이전: 전체 Rebuild).
            if (i.Points.Count > 0) { var stroke = i.ToStroke(); strokes[i.Id] = stroke; RenderInk(i.Id, stroke); }
        }
        foreach (var t in Board.Texts.Where(t => Selection.Contains(t.Id)))
        {
            bool fits = FitsWithoutWrap(t);
            t.X = cx + (t.X - cx) * scale; t.Y = cy + (t.Y - cy) * scale; t.Width *= scale; t.FontSize *= scale;
            // Enter로만 줄을 바꾼 Text는 크기 변경 후에도 자동 줄바꿈이 생기지 않도록 너비를 다시 맞춥니다.
            if (fits) FitTextWidth(t);
        }
        Redraw(); Changed?.Invoke();
    }
}
