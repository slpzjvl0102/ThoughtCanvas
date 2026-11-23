using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;

namespace ThoughtCanvas;

public enum CanvasTool { Pen, Eraser, Lasso, Text }
public class BoardSurface : Canvas
{
    public Board Board { get; private set; } = new();
    public CanvasHistory History { get; } = new();
    private CanvasTool tool;
    public CanvasTool Tool { get => tool; set { tool = value; UpdatePointer(); } }
    public bool DarkMode { get; private set; }
    private Point? pointerPosition;
    private readonly System.Windows.Shapes.Ellipse pointerRing = new() { IsHitTestVisible = false, StrokeThickness = 1.2, Visibility = Visibility.Collapsed };
    public void SetTheme(bool dark)
    {
        DarkMode = dark; background = new SolidColorBrush(dark ? Color.FromRgb(23, 27, 33) : Color.FromRgb(247, 249, 248));
        Background = background;
        if (editor != null) { editor.Background = dark ? new SolidColorBrush(Color.FromRgb(32, 38, 46)) : Brushes.White; editor.Foreground = dark ? Brushes.White : Brushes.DarkSlateGray; }
        UpdatePointer(); InvalidateVisual();
    }
    public void UpdatePointer(Point? position = null)
    {
        if (position.HasValue) pointerPosition = position;
        pointerRing.Visibility = pointerPosition.HasValue && Tool is CanvasTool.Pen or CanvasTool.Eraser ? Visibility.Visible : Visibility.Collapsed;
        if (pointerPosition is not Point p) return;
        double diameter = (Tool == CanvasTool.Eraser ? EraserSize : PenWidth) * Board.Zoom;
        pointerRing.Width = pointerRing.Height = Math.Max(1, diameter);
        pointerRing.Stroke = DarkMode ? Brushes.White : Brushes.DarkSlateGray;
        pointerRing.Fill = Tool == CanvasTool.Eraser ? new SolidColorBrush(Color.FromArgb(24, 128, 160, 160)) : Brushes.Transparent;
        SetLeft(pointerRing, p.X - pointerRing.Width / 2); SetTop(pointerRing, p.Y - pointerRing.Height / 2);
    }
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Stroke, Stroke> whiteStrokes = new();
    private void DrawInk(DrawingContext dc, Stroke stroke)
    {
        if (!DarkMode) { stroke.Draw(dc); return; }
        var visible = whiteStrokes.GetValue(stroke, original => { var copy = original.Clone(); copy.DrawingAttributes.Color = Colors.White; return copy; }); visible.Draw(dc);
    }
    public double PenWidth { get; set; } = 2.5;
    public double EraserSize { get; set; } = 24;
    public string PenColor { get; set; } = "#243047";
    public HashSet<string> Selection { get; } = [];
    public event Action? Changed;
    public event Action? DraftChanged;
    public event Action? ViewChanged;
    public event Action? ToolChanged;
    public event Action<Point>? RadialOpened;
    public event Action<Point>? RadialMoved;
    public event Action<Point>? RadialClosed;
    private readonly Dictionary<string, Stroke> strokes = [];
    private List<Sample>? live;
    private string liveId = "";
    private readonly List<Point> lasso = [];
    private CanvasSnapshot? before;
    private Point lastScreen, lastWorld, dragOrigin;
    private bool active, moved, penDown, panning, draggingSelection, radial;
    private DateTime suppressMouseUntil;
    private readonly PenButtonGate buttonGate = new();
    private StylusDevice? trackedPen;
    private readonly System.Windows.Threading.DispatcherTimer penPoll = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private int penPackets, nativePackets, buttonEvents, switches;
    private string lastButton = "없음";
    public string PenInputSummary => $"Pen 입력 진단: WPF {penPackets}, Windows Pen {nativePackets}, Button 이벤트 {buttonEvents}, 도구 전환 {switches}\n최근 Button: {lastButton}";
    public event Action<string>? PenToolSwitched;
    public void ResetPenButton() { trackedPen = null; buttonGate.Reset(); penPoll.Stop(); }
    public void NativePenSample(Point screen, bool pressed)
    {
        if (!IsVisible || Window.GetWindow(this)?.IsActive != true) return;
        var point = PointFromScreen(screen);
        if (!new Rect(RenderSize).Contains(point)) return;
        nativePackets++; UpdatePointer(point); HandlePenButton(pressed, true, "Windows Pen");
    }
    private long penMouseUntil;
    public void RawPenSample(Point screen, bool pressed, bool raw)
    {
        if (!IsVisible || Window.GetWindow(this)?.IsActive != true) return;
        var point = PointFromScreen(screen);
        if (!new Rect(RenderSize).Contains(point)) return;
        UpdatePointer(point);
        if (!raw) penMouseUntil = Environment.TickCount64 + 500;
        if (raw ? buttonGate.UpdateRaw(pressed, Environment.TickCount64) : buttonGate.Update(pressed, true, Environment.TickCount64))
            CyclePenTool(raw ? "HID Raw Input" : "Pen 우클릭");
    }
    private void HandlePenButton(bool down, bool native, string source)
    {
        if (!buttonGate.Update(down, native, Environment.TickCount64)) return;
        CyclePenTool(source);
    }
    private void CyclePenTool(string source)
    {
        EndGesture(); CommitEditor();
        Tool = Tool switch { CanvasTool.Pen => CanvasTool.Eraser, CanvasTool.Eraser => CanvasTool.Lasso, _ => CanvasTool.Pen };
        switches++; lastButton = source; suppressMouseUntil = DateTime.UtcNow.AddMilliseconds(500);
        ToolChanged?.Invoke(); PenToolSwitched?.Invoke($"S Pen Button · {Tool}");
    }
    private void ObservePen(StylusEventArgs e)
    {
        if (e.StylusDevice.TabletDevice.Type == TabletDeviceType.Touch) return;
        trackedPen = e.StylusDevice; penPoll.Start(); penPackets++;
        bool? packetDown = null;
        var points = e.GetStylusPoints(this);
        if (points.Count > 0 && points[^1].HasProperty(StylusPointProperties.BarrelButton))
            packetDown = points[^1].GetPropertyValue(StylusPointProperties.BarrelButton) != 0;
        bool? collectionDown = ReadButton(trackedPen);
        if (packetDown.HasValue || collectionDown.HasValue)
            HandlePenButton(packetDown == true || collectionDown == true, false, "WPF Pen 상태");
    }
    private static bool? ReadButton(StylusDevice device)
    {
        foreach (StylusButton button in device.StylusButtons)
            if (button.Guid == StylusPointProperties.BarrelButton.Id) return button.StylusButtonState == StylusButtonState.Down;
        return null;
    }
    private TextBox? editor;
    private TextItem? editedText;
    private readonly Dictionary<int, Point> touches = [];
    private Point touchCenter;
    private double touchDistance;
    private Brush background = new SolidColorBrush(Color.FromRgb(250, 249, 245));

    public BoardSurface()
    {
        Children.Add(pointerRing); SetZIndex(pointerRing, 100);
        StylusInAirMove += (_, e) => { if (e.StylusDevice.TabletDevice.Type != TabletDeviceType.Touch) { ObservePen(e); UpdatePointer(e.GetPosition(this)); } };
        StylusOutOfRange += (_, _) => ResetPenButton();
        Unloaded += (_, _) => ResetPenButton();
        penPoll.Tick += (_, _) =>
        {
            if (trackedPen == null || !trackedPen.InRange || Window.GetWindow(this)?.IsActive != true) { ResetPenButton(); return; }
            var pos = trackedPen.GetPosition(this);
            if (!new Rect(RenderSize).Contains(pos)) return;
            if (ReadButton(trackedPen) is bool pressed) HandlePenButton(pressed, false, "WPF Button 감시");
        };
        Focusable = true; ClipToBounds = true; Background = background;
        Stylus.SetIsPressAndHoldEnabled(this, false); Stylus.SetIsFlicksEnabled(this, false);
        Stylus.SetIsTapFeedbackEnabled(this, false); Stylus.SetIsTouchFeedbackEnabled(this, false);
        AddHandler(Stylus.PreviewStylusButtonDownEvent, new StylusButtonEventHandler((_, e) =>
        {
            if (e.StylusDevice.TabletDevice.Type == TabletDeviceType.Touch) return;
            buttonEvents++; lastButton = e.StylusButton.Guid.ToString();
            if (e.StylusButton.Guid != StylusPointProperties.BarrelButton.Id) return;
            trackedPen = e.StylusDevice; penPoll.Start();
            HandlePenButton(true, false, "WPF Button 이벤트"); e.Handled = true;
        }), true);
        AddHandler(Stylus.PreviewStylusButtonUpEvent, new StylusButtonEventHandler((_, e) =>
        {
            if (e.StylusButton.Guid == StylusPointProperties.BarrelButton.Id) { HandlePenButton(false, false, "WPF Button 해제"); e.Handled = true; }
        }), true);
        LostStylusCapture += (_, _) => { if (penDown) { EndGesture(); penDown = false; } };
        LostMouseCapture += (_, _) => { if (active) EndGesture(); panning = false; };
    }
    public bool IsEditing => active || editor != null;
    public Board CaptureForSave()
    {
        var snapshot = DataJson.Clone(Board);
        if (live is { Count: > 0 }) snapshot.Ink.Add(new() { Id = liveId, Points = live.ToList(), Color = PenColor, Width = PenWidth });
        if (editor != null && editedText != null)
        {
            var text = snapshot.Texts.First(t => t.Id == editedText.Id); text.Text = editor.Text;
        }
        return snapshot;
    }
    public void Load(Board board)
    {
        CommitEditor(); Board = board; History.Clear(); Selection.Clear(); Rebuild();
    }
    public void Rebuild()
    {
        strokes.Clear(); foreach (var item in Board.Ink.Where(i => i.Points.Count > 0)) strokes[item.Id] = item.ToStroke(); InvalidateVisual();
    }
    public Point ToWorld(Point p) => new((p.X - Board.ViewX) / Board.Zoom, (p.Y - Board.ViewY) / Board.Zoom);
    public Point ToScreen(Point p) => new(p.X * Board.Zoom + Board.ViewX, p.Y * Board.Zoom + Board.ViewY);
    public static FormattedText Format(TextItem t) => new(t.Text.Length == 0 ? " " : t.Text, CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight, new Typeface("Malgun Gothic"), t.FontSize, Brushes.DarkSlateGray, 1)
    { MaxTextWidth = t.Width, Trimming = TextTrimming.None };
    public static Rect TextBounds(TextItem t) => new(t.X, t.Y, t.Width, Math.Max(t.FontSize * 1.5, Format(t).Height));
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); dc.DrawRectangle(background, null, new Rect(RenderSize));
        double step = 32 * Board.Zoom; while (step < 20) step *= 2; while (step > 90) step /= 2;
        var grid = new SolidColorBrush(DarkMode ? Color.FromRgb(50, 57, 66) : Color.FromRgb(218, 225, 222));
        for (double x = ((Board.ViewX % step) + step) % step; x < ActualWidth; x += step)
            for (double y = ((Board.ViewY % step) + step) % step; y < ActualHeight; y += step) dc.DrawEllipse(grid, null, new Point(x, y), .85, .85);
        dc.PushTransform(new MatrixTransform(Board.Zoom, 0, 0, Board.Zoom, Board.ViewX, Board.ViewY));
        var viewport = new Rect(ToWorld(new Point(0, 0)), ToWorld(new Point(ActualWidth, ActualHeight)));
        foreach (var item in Board.Ink)
        {
            if (!strokes.TryGetValue(item.Id, out var stroke) || !viewport.IntersectsWith(stroke.GetBounds())) continue;
            DrawInk(dc, stroke);
            if (Selection.Contains(item.Id)) dc.DrawRectangle(null, new Pen(Brushes.Teal, 1 / Board.Zoom) { DashStyle = DashStyles.Dash }, stroke.GetBounds());
        }
        foreach (var text in Board.Texts)
        {
            if (editedText?.Id == text.Id || !viewport.IntersectsWith(TextBounds(text))) continue;
            var formatted = Format(text); if (DarkMode) formatted.SetForegroundBrush(Brushes.White);
            dc.DrawText(formatted, new Point(text.X, text.Y));
            if (Selection.Contains(text.Id)) dc.DrawRectangle(null, new Pen(Brushes.Teal, 1 / Board.Zoom), TextBounds(text));
        }
        if (live is { Count: > 0 }) DrawInk(dc, new InkItem { Points = live, Width = PenWidth, Color = PenColor }.ToStroke());
        if (lasso.Count > 1)
        {
            var geometry = Polygon(lasso); dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(20, 20, 130, 130)), new Pen(Brushes.Teal, 1 / Board.Zoom) { DashStyle = DashStyles.Dash }, geometry);
        }
        dc.Pop();
    }
    public static StreamGeometry Polygon(IEnumerable<Point> points)
    {
        var list = points.ToList(); var g = new StreamGeometry { FillRule = FillRule.EvenOdd };
        if (list.Count > 0) { using var c = g.Open(); c.BeginFigure(list[0], true, true); c.PolyLineTo(list.Skip(1).ToArray(), true, false); }
        return g;
    }
    public static bool FullyInside(Geometry lassoGeometry, Geometry element) => lassoGeometry.FillContainsWithDetail(element, .01, ToleranceType.Absolute) == IntersectionDetail.FullyContains;
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
            if (t == null) { t = new TextItem { X = lastWorld.X, Y = lastWorld.Y }; Board.Texts.Add(t); }
            OpenEditor(t); return;
        }
        if (Tool == CanvasTool.Pen) { liveId = Guid.NewGuid().ToString("N"); live = [new(lastWorld.X, lastWorld.Y, pressure)]; DraftChanged?.Invoke(); }
        if (Tool == CanvasTool.Eraser) Erase(lastWorld, lastWorld);
        if (Tool == CanvasTool.Lasso)
        {
            string? hit = Hit(lastWorld);
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                if (hit != null && !Selection.Add(hit)) Selection.Remove(hit); active = false;
            }
            else if (hit != null && Selection.Contains(hit)) draggingSelection = true;
            else { Selection.Clear(); lasso.Clear(); lasso.Add(lastWorld); }
        }
        InvalidateVisual();
    }
    // notify=false: 여러 점을 한 번에 처리하는 호출자(OnStylusMove)가 마지막에 NotifyDraft()를 한 번만 호출합니다.
    private void ContinueGesture(Point screen, float pressure, bool notify = true)
    {
        UpdatePointer(screen); if (!active) return; var world = ToWorld(screen);
        if (Tool == CanvasTool.Pen) live?.Add(new(world.X, world.Y, pressure));
        if (Tool == CanvasTool.Eraser) Erase(lastWorld, world);
        if (Tool == CanvasTool.Lasso)
        {
            if (draggingSelection && before != null)
            {
                var delta = world - dragOrigin;
                foreach (var i in Board.Ink.Where(i => Selection.Contains(i.Id)))
                { var original = before.Ink.First(x => x.Id == i.Id); i.Points = original.Points.Select(p => new Sample(p.X + delta.X, p.Y + delta.Y, p.Pressure)).ToList(); }
                foreach (var t in Board.Texts.Where(i => Selection.Contains(i.Id)))
                { var original = before.Texts.First(x => x.Id == t.Id); t.X = original.X + delta.X; t.Y = original.Y + delta.Y; }
                moved = delta.Length > .01; Rebuild();
            }
            else lasso.Add(world);
        }
        lastWorld = world; lastScreen = screen; if (notify) NotifyDraft();
    }
    private void NotifyDraft()
    {
        if (!active) return;
        if (Tool != CanvasTool.Lasso || draggingSelection) DraftChanged?.Invoke();
        InvalidateVisual();
    }
    private void Erase(Point a, Point b)
    {
        var shape = new EllipseStylusShape(EraserSize, EraserSize); var path = new[] { a, b };
        var area = new Rect(a, b); area.Inflate(EraserSize, EraserSize);
        foreach (var item in Board.Ink.ToArray())
        {
            if (!strokes.TryGetValue(item.Id, out var stroke) || !area.IntersectsWith(stroke.GetBounds()) || !stroke.HitTest(path, shape)) continue;
            var fragments = stroke.GetEraseResult(path, shape); Board.Ink.Remove(item); strokes.Remove(item.Id);
            foreach (var fragment in fragments) { var next = InkItem.FromStroke(fragment, item.ParentId ?? item.Id); Board.Ink.Add(next); strokes[next.Id] = fragment; }
            moved = true;
        }
    }
    public void EndGesture()
    {
        if (!active) return;
        if (live is { Count: > 0 }) { var ink = new InkItem { Id = liveId, Points = live, Width = PenWidth, Color = PenColor }; Board.Ink.Add(ink); strokes[ink.Id] = ink.ToStroke(); moved = true; }
        if (lasso.Count > 2)
        {
            var shape = Polygon(lasso);
            foreach (var (id, stroke) in strokes) if (FullyInside(shape, stroke.GetGeometry())) Selection.Add(id);
            foreach (var text in Board.Texts) if (FullyInside(shape, new RectangleGeometry(TextBounds(text)))) Selection.Add(text.Id);
        }
        live = null; lasso.Clear(); active = false; draggingSelection = false;
        if (moved && before != null) { History.Push(before); Changed?.Invoke(); }
        before = null; InvalidateVisual();
    }
    protected override void OnStylusDown(StylusDownEventArgs e)
    {
        if (editor?.IsKeyboardFocusWithin == true && editor.IsMouseOver) return;
        if (e.StylusDevice.TabletDevice.Type == TabletDeviceType.Touch) return;
        ObservePen(e); penDown = true; suppressMouseUntil = DateTime.UtcNow.AddMilliseconds(500); CaptureStylus();
        var p = e.GetStylusPoints(this).Last(); StartGesture(new Point(p.X, p.Y), p.PressureFactor); e.Handled = true;
    }
    protected override void OnStylusMove(StylusEventArgs e)
    {
        ObservePen(e); if (!penDown || e.InAir) return;
        // Stylus 이벤트 하나에 여러 점이 묶여 오므로, 변경 알림·화면 갱신은 이벤트당 한 번만 합니다.
        foreach (var p in e.GetStylusPoints(this)) ContinueGesture(new Point(p.X, p.Y), p.PressureFactor, notify: false);
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
    public void ZoomAt(Point screen, double factor)
    {
        var world = ToWorld(screen); Board.Zoom = Math.Clamp(Board.Zoom * factor, .08, 8); Board.ViewX = screen.X - world.X * Board.Zoom; Board.ViewY = screen.Y - world.Y * Board.Zoom; UpdateView();
    }
    private void UpdateView() { UpdatePointer(); InvalidateVisual(); ViewChanged?.Invoke(); }
    public void CenterSources(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet(); var bounds = Rect.Empty;
        foreach (var ink in Board.Ink.Where(x => set.Contains(x.Id))) bounds.Union(strokes[ink.Id].GetBounds());
        foreach (var text in Board.Texts.Where(x => set.Contains(x.Id))) bounds.Union(TextBounds(text));
        if (bounds.IsEmpty) return;
        Board.ViewX = ActualWidth / 2 - (bounds.Left + bounds.Width / 2) * Board.Zoom;
        Board.ViewY = ActualHeight / 2 - (bounds.Top + bounds.Height / 2) * Board.Zoom;
        Selection.Clear(); Selection.UnionWith(set); UpdateView();
    }
    public void Fit()
    {
        CommitEditor(); var b = Rect.Empty; foreach (var s in strokes.Values) b.Union(s.GetBounds()); foreach (var t in Board.Texts) b.Union(TextBounds(t));
        if (b.IsEmpty) { Board.ViewX = 80; Board.ViewY = 80; Board.Zoom = 1; }
        else { Board.Zoom = Math.Clamp(Math.Min((ActualWidth - 100) / Math.Max(100, b.Width), (ActualHeight - 100) / Math.Max(100, b.Height)), .08, 2); Board.ViewX = (ActualWidth - b.Width * Board.Zoom) / 2 - b.X * Board.Zoom; Board.ViewY = (ActualHeight - b.Height * Board.Zoom) / 2 - b.Y * Board.Zoom; }
        UpdateView();
    }
    private void OpenEditor(TextItem text)
    {
        editedText = text; var p = ToScreen(new(text.X, text.Y));
        editor = new TextBox { Text = text.Text, FontFamily = new FontFamily("Malgun Gothic"), FontSize = text.FontSize * Board.Zoom, Width = Math.Max(80, text.Width * Board.Zoom), MinHeight = 35, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, BorderBrush = Brushes.Teal, BorderThickness = new Thickness(1), Padding = new Thickness(2), Background = Brushes.White };
        editor.Background = DarkMode ? new SolidColorBrush(Color.FromRgb(32, 38, 46)) : Brushes.White; editor.Foreground = DarkMode ? Brushes.White : Brushes.DarkSlateGray;
        SetLeft(editor, p.X); SetTop(editor, p.Y); Children.Add(editor);
        editor.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape || (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))) { CommitEditor(); e.Handled = true; } };
        editor.TextChanged += (_, _) => DraftChanged?.Invoke();
        editor.LostKeyboardFocus += (_, _) => { if (editor != null) CommitEditor(); };
        editor.Focus(); editor.CaretIndex = editor.Text.Length; InvalidateVisual();
    }
    public void CommitEditor()
    {
        if (editor == null || editedText == null) return;
        var box = editor; var text = editedText; editor = null; editedText = null;
        text.Text = box.Text; Children.Remove(box); if (string.IsNullOrWhiteSpace(text.Text)) Board.Texts.Remove(text);
        if (before != null && DataJson.Write(before) != DataJson.Write(CanvasHistory.Capture(Board))) { History.Push(before); Changed?.Invoke(); }
        before = null; InvalidateVisual();
    }
    public void Undo() { CommitEditor(); EndGesture(); if (History.Undo(Board)) { Selection.Clear(); Rebuild(); Changed?.Invoke(); } }
    public void ApplyCanvas(Board arranged)
    {
        CommitEditor(); EndGesture(); History.Push(CanvasHistory.Capture(Board));
        Board.Ink = DataJson.Clone(arranged.Ink); Board.Texts = DataJson.Clone(arranged.Texts);
        Selection.Clear(); Rebuild(); Changed?.Invoke(); Fit();
    }
    public void Redo() { CommitEditor(); EndGesture(); if (History.Redo(Board)) { Selection.Clear(); Rebuild(); Changed?.Invoke(); } }
    public void DeleteSelection()
    {
        CommitEditor(); if (Selection.Count == 0) return; History.Push(CanvasHistory.Capture(Board)); Board.Ink.RemoveAll(i => Selection.Contains(i.Id)); Board.Texts.RemoveAll(t => Selection.Contains(t.Id)); Selection.Clear(); Rebuild(); Changed?.Invoke();
    }
    public void ResizeSelection(double scale)
    {
        if (Selection.Count == 0) return; CommitEditor(); var b = Rect.Empty;
        foreach (var i in Board.Ink.Where(i => Selection.Contains(i.Id))) b.Union(strokes[i.Id].GetBounds());
        foreach (var t in Board.Texts.Where(t => Selection.Contains(t.Id))) b.Union(TextBounds(t));
        if (b.IsEmpty) return; History.Push(CanvasHistory.Capture(Board));
        foreach (var i in Board.Ink.Where(i => Selection.Contains(i.Id))) { i.Points = i.Points.Select(p => new Sample(b.X + (p.X - b.X) * scale, b.Y + (p.Y - b.Y) * scale, p.Pressure)).ToList(); i.Width *= scale; }
        foreach (var t in Board.Texts.Where(t => Selection.Contains(t.Id))) { t.X = b.X + (t.X - b.X) * scale; t.Y = b.Y + (t.Y - b.Y) * scale; t.Width *= scale; t.FontSize *= scale; }
        Rebuild(); Changed?.Invoke();
    }
}
