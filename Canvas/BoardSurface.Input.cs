using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;

namespace ThoughtCanvas;

public partial class BoardSurface : Canvas
{
    private string? Hit(Point p)
    {
        foreach (var t in Board.Texts.AsEnumerable().Reverse()) if (TextBounds(t).Contains(p)) return t.Id;
        foreach (var s in Board.Ink.AsEnumerable().Reverse()) if (strokes.TryGetValue(s.Id, out var stroke) && stroke.HitTest(p, 8 / Board.Zoom)) return s.Id;
        return null;
    }
    private void StartGesture(Point screen, float pressure)
    {
        UpdatePointer(screen); CommitEditor(); Focus(); lastScreen = screen; lastWorld = ToWorld(screen); dragOrigin = lastWorld;
        before = CanvasHistory.Capture(Board); moved = false; active = true;
        if (Tool == CanvasTool.Text)
        {
            active = false; var hit = Hit(lastWorld); var t = Board.Texts.FirstOrDefault(x => x.Id == hit);
            if (t == null) { t = new TextItem { X = lastWorld.X, Y = lastWorld.Y, FontSize = TextSize }; Board.Texts.Add(t); }
            OpenEditor(t); return;
        }
        if (Tool == CanvasTool.Pen)
        {
            liveId = Guid.NewGuid().ToString("N"); live = [new(lastWorld.X, lastWorld.Y, pressure)];
            liveStroke = new InkItem { Points = live, Width = PenWidth, Color = PenColor }.ToStroke();
            if (DarkMode) liveStroke.DrawingAttributes.Color = Colors.White;
            DraftChanged?.Invoke(); liveLayer.InvalidateVisual(); return;
        }
        if (Tool == CanvasTool.Eraser) Erase(lastWorld, lastWorld);
        if (Tool == CanvasTool.Lasso)
        {
            string? hit = Hit(lastWorld);
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                if (hit != null && !Selection.Add(hit)) Selection.Remove(hit); active = false;
            }
            else if (hit != null && Selection.Contains(hit)) BeginSelectionDrag();
            else { Selection.Clear(); lasso.Clear(); lasso.Add(lastWorld); }
        }
        Redraw();
    }
    // notify=false: 여러 점을 한 번에 처리하는 호출자(OnStylusMove)가 마지막에 NotifyDraft()를 한 번만 호출합니다.
    private void ContinueGesture(Point screen, float pressure, bool notify = true)
    {
        UpdatePointer(screen); if (!active) return; var world = ToWorld(screen);
        if (Tool == CanvasTool.Pen && live != null)
        {
            live.Add(new(world.X, world.Y, pressure));
            liveStroke?.StylusPoints.Add(new StylusPoint(world.X, world.Y, Math.Clamp(pressure, 0, 1)));
        }
        if (Tool == CanvasTool.Eraser) Erase(lastWorld, world);
        if (Tool == CanvasTool.Lasso)
        {
            // 점마다 이동량만 기록하고, 화면 반영은 NotifyDraft()에서 이벤트당 한 번 합니다.
            if (draggingSelection) { dragDelta = world - dragOrigin; moved = dragDelta.Length > .01; }
            else lasso.Add(world);
        }
        lastWorld = world; lastScreen = screen; if (notify) NotifyDraft();
    }
    private void NotifyDraft()
    {
        if (!active) return;
        if (draggingSelection) ApplySelectionDrag();
        if (Tool != CanvasTool.Lasso || draggingSelection) DraftChanged?.Invoke();
        // 필기 중에는 그리는 중인 Stroke Layer만 다시 그립니다. Eraser는 Erase()에서 바뀐 Stroke만 갱신합니다.
        if (Tool is CanvasTool.Pen or CanvasTool.Lasso) liveLayer.InvalidateVisual();
    }
    private void Erase(Point a, Point b)
    {
        var shape = new EllipseStylusShape(EraserSize, EraserSize); var path = new[] { a, b };
        var area = new Rect(a, b); area.Inflate(EraserSize, EraserSize);
        bool selectedChanged = false;
        foreach (var item in Board.Ink.ToArray())
        {
            if (!strokes.TryGetValue(item.Id, out var stroke) || !area.IntersectsWith(stroke.GetBounds()) || !stroke.HitTest(path, shape)) continue;
            var fragments = stroke.GetEraseResult(path, shape); Board.Ink.Remove(item); strokes.Remove(item.Id); inkLayer.Remove(item.Id);
            selectedChanged |= Selection.Contains(item.Id);
            foreach (var fragment in fragments) { var next = InkItem.FromStroke(fragment, item.ParentId ?? item.Id); Board.Ink.Add(next); strokes[next.Id] = fragment; RenderInk(next.Id, fragment); }
            moved = true;
        }
        if (selectedChanged) overlayLayer.InvalidateVisual();
    }
    public void EndGesture()
    {
        if (!active) return;
        bool penStroke = live != null;
        if (draggingSelection) CommitSelectionDrag();
        if (live is { Count: > 0 }) { var ink = new InkItem { Id = liveId, Points = live, Width = PenWidth, Color = PenColor }; Board.Ink.Add(ink); var stroke = ink.ToStroke(); strokes[ink.Id] = stroke; RenderInk(ink.Id, stroke); moved = true; }
        if (lasso.Count > 2)
        {
            var shape = Polygon(lasso);
            // 완전히 포함되려면 경계 상자도 Lasso 경계 안에 있어야 하므로, 정밀 비교 전에 범위로 먼저 거릅니다.
            var area = shape.Bounds; area.Inflate(1, 1);
            foreach (var (id, stroke) in strokes) if (area.Contains(stroke.GetBounds()) && FullyInside(shape, stroke.GetGeometry())) Selection.Add(id);
            foreach (var text in Board.Texts) { var bounds = TextBounds(text); if (area.Contains(bounds) && FullyInside(shape, new RectangleGeometry(bounds))) Selection.Add(text.Id); }
        }
        live = null; liveStroke = null; lasso.Clear(); active = false; draggingSelection = false;
        if (moved && before != null) { History.Push(before); Changed?.Invoke(); }
        before = null;
        if (penStroke) liveLayer.InvalidateVisual(); else Redraw();
    }
    protected override void OnStylusDown(StylusDownEventArgs e)
    {
        if (editor?.IsKeyboardFocusWithin == true && editor.IsMouseOver) return;
        if (e.StylusDevice.TabletDevice.Type == TabletDeviceType.Touch) return;
        var points = e.GetStylusPoints(this);
        ObservePen(e, points); penDown = true; suppressMouseUntil = DateTime.UtcNow.AddMilliseconds(500); CaptureStylus();
        lastStylusPosition = Environment.TickCount64;
        var p = points[^1]; StartGesture(new Point(p.X, p.Y), p.PressureFactor); e.Handled = true;
    }
    protected override void OnStylusMove(StylusEventArgs e)
    {
        // 점 목록은 한 번만 가져와 Button 확인과 필기에 함께 사용합니다.
        var points = e.GetStylusPoints(this);
        ObservePen(e, points); if (!penDown || e.InAir) return;
        lastStylusPosition = Environment.TickCount64;
        // Stylus 이벤트 하나에 여러 점이 묶여 오므로, 변경 알림·화면 갱신은 이벤트당 한 번만 합니다.
        foreach (var p in points) ContinueGesture(new Point(p.X, p.Y), p.PressureFactor, notify: false);
        NotifyDraft(); e.Handled = true;
    }
    protected override void OnStylusUp(StylusEventArgs e)
    {
        ObservePen(e); if (!penDown) return; EndGesture(); penDown = false; ReleaseStylusCapture(); suppressMouseUntil = DateTime.UtcNow.AddMilliseconds(500); e.Handled = true;
    }
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Right && e.StylusDevice is { } device && device.TabletDevice.Type != TabletDeviceType.Touch)
        { RawPenSample(PointToScreen(e.GetPosition(this)), true, false); e.Handled = true; return; }
        if (e.ChangedButton == MouseButton.Right && Environment.TickCount64 < penMouseUntil) { e.Handled = true; return; }
        if (e.StylusDevice != null || DateTime.UtcNow < suppressMouseUntil || e.OriginalSource is TextBox) return;
        Focus(); var p = e.GetPosition(this);
        if (e.ChangedButton == MouseButton.Middle) { CommitEditor(); radial = true; CaptureMouse(); RadialOpened?.Invoke(p); e.Handled = true; return; }
        if (e.ChangedButton == MouseButton.Right) { CommitEditor(); panning = true; lastScreen = p; CaptureMouse(); e.Handled = true; return; }
        if (e.ChangedButton == MouseButton.Left)
        {
            if (e.ClickCount == 2 && Board.Texts.FirstOrDefault(t => t.Id == Hit(ToWorld(p))) is { } text)
            { CommitEditor(); before = CanvasHistory.Capture(Board); OpenEditor(text); e.Handled = true; return; }
            CaptureMouse(); StartGesture(p, .5f); if (editor != null) ReleaseMouseCapture(); e.Handled = true;
        }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (e.StylusDevice != null || penDown) return;
        var p = e.GetPosition(this); UpdatePointer(p);
        if (radial) { RadialMoved?.Invoke(p); return; }
        if (panning) { var delta = p - lastScreen; Board.ViewX += delta.X; Board.ViewY += delta.Y; lastScreen = p; UpdateView(); return; }
        if (e.LeftButton == MouseButtonState.Pressed) ContinueGesture(p, .5f);
    }
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Right && e.StylusDevice is { } device && device.TabletDevice.Type != TabletDeviceType.Touch)
        { RawPenSample(PointToScreen(e.GetPosition(this)), false, false); e.Handled = true; return; }
        if (e.StylusDevice != null) return;
        if (radial && e.ChangedButton == MouseButton.Middle) { radial = false; RadialClosed?.Invoke(e.GetPosition(this)); }
        else if (e.ChangedButton == MouseButton.Left) EndGesture();
        panning = false; ReleaseMouseCapture(); e.Handled = true;
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e) { CommitEditor(); ZoomAt(e.GetPosition(this), e.Delta > 0 ? 1.12 : 1 / 1.12); e.Handled = true; }
    protected override void OnTouchDown(TouchEventArgs e)
    {
        suppressMouseUntil = DateTime.UtcNow.AddSeconds(1); if (penDown) { e.Handled = true; return; }
        CommitEditor(); touches[e.TouchDevice.Id] = e.GetTouchPoint(this).Position; e.TouchDevice.Capture(this); ResetTouchReference(); e.Handled = true;
    }
    protected override void OnTouchMove(TouchEventArgs e)
    {
        if (!touches.ContainsKey(e.TouchDevice.Id)) { e.Handled = true; return; }
        touches[e.TouchDevice.Id] = e.GetTouchPoint(this).Position;
        if (!penDown)
        {
            var center = TouchCenter(); var delta = center - touchCenter; Board.ViewX += delta.X; Board.ViewY += delta.Y;
            var distance = TouchDistance(); if (touches.Count >= 2 && touchDistance > 5) ZoomAt(center, distance / touchDistance);
            UpdateView();
        }
        ResetTouchReference(); e.Handled = true;
    }
    protected override void OnTouchUp(TouchEventArgs e) { touches.Remove(e.TouchDevice.Id); e.TouchDevice.Capture(null); ResetTouchReference(); suppressMouseUntil = DateTime.UtcNow.AddMilliseconds(500); e.Handled = true; }
    protected override void OnLostTouchCapture(TouchEventArgs e) { touches.Remove(e.TouchDevice.Id); ResetTouchReference(); base.OnLostTouchCapture(e); }
    private Point TouchCenter() => touches.Count == 0 ? new() : new(touches.Values.Average(p => p.X), touches.Values.Average(p => p.Y));
    private double TouchDistance() { var p = touches.Values.Take(2).ToArray(); return p.Length < 2 ? 0 : (p[0] - p[1]).Length; }
    private void ResetTouchReference() { touchCenter = TouchCenter(); touchDistance = TouchDistance(); }
}
