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
    private static readonly Brush EraserFill = Frozen(new SolidColorBrush(Color.FromArgb(24, 128, 160, 160)));
    private static readonly Brush LassoFill = Frozen(new SolidColorBrush(Color.FromArgb(20, 20, 130, 130)));
    private static T Frozen<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }

    // 렌더링 Layer (v0.6.3)
    // - BoardSurface.OnRender: 배경·Grid. 화면 이동·확대, Theme, 크기 변경 때만 다시 그립니다.
    // - inkLayer: 완성된 Stroke. Stroke마다 DrawingVisual 하나를 두고 추가·삭제·변경된 Stroke만 다시 그립니다.
    // - overlayLayer: Text와 선택 표시. Text·선택·확대 배율이 바뀔 때만 다시 그립니다.
    // - liveLayer: 그리는 중인 Stroke와 Lasso. 필기 중에는 이 Layer만 다시 그립니다.
    // 세 Layer는 같은 viewTransform을 공유하므로 화면 이동 시 Stroke를 다시 그리지 않습니다.
    private readonly MatrixTransform viewTransform = new();
    private readonly InkLayer inkLayer;
    private readonly DrawLayer overlayLayer, liveLayer;
    private double renderedZoom = double.NaN;

    public void SetTheme(bool dark)
    {
        DarkMode = dark; background = new SolidColorBrush(dark ? Color.FromRgb(23, 27, 33) : Color.FromRgb(247, 249, 248));
        Background = background;
        if (editor != null) { editor.Background = dark ? new SolidColorBrush(Color.FromRgb(32, 38, 46)) : Brushes.White; editor.Foreground = dark ? Brushes.White : Brushes.DarkSlateGray; }
        UpdatePointer(); RenderAllInk(); InvalidateVisual(); Redraw();
    }
    public void UpdatePointer(Point? position = null)
    {
        // 펜 입력마다 여러 경로에서 호출되므로, 실제로 바뀐 속성만 설정해 불필요한 Layout 갱신을 막습니다.
        if (position.HasValue) pointerPosition = position;
        var visibility = pointerPosition.HasValue && Tool is CanvasTool.Pen or CanvasTool.Eraser ? Visibility.Visible : Visibility.Collapsed;
        if (pointerRing.Visibility != visibility) pointerRing.Visibility = visibility;
        if (pointerPosition is not Point p) return;
        double diameter = Math.Max(1, (Tool == CanvasTool.Eraser ? EraserSize : PenWidth) * Board.Zoom);
        if (pointerRing.Width != diameter) pointerRing.Width = diameter;
        if (pointerRing.Height != diameter) pointerRing.Height = diameter;
        var stroke = DarkMode ? Brushes.White : Brushes.DarkSlateGray;
        if (!ReferenceEquals(pointerRing.Stroke, stroke)) pointerRing.Stroke = stroke;
        var fill = Tool == CanvasTool.Eraser ? EraserFill : Brushes.Transparent;
        if (!ReferenceEquals(pointerRing.Fill, fill)) pointerRing.Fill = fill;
        double left = p.X - diameter / 2, top = p.Y - diameter / 2;
        if (GetLeft(pointerRing) != left) SetLeft(pointerRing, left);
        if (GetTop(pointerRing) != top) SetTop(pointerRing, top);
    }
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Stroke, Stroke> whiteStrokes = new();
    private void DrawInk(DrawingContext dc, Stroke stroke)
    {
        if (!DarkMode) { stroke.Draw(dc); return; }
        var visible = whiteStrokes.GetValue(stroke, original => { var copy = original.Clone(); copy.DrawingAttributes.Color = Colors.White; return copy; }); visible.Draw(dc);
    }
    public double PenWidth { get; set; } = 2.5;
    /// <summary>새로 만드는 Text의 글자 크기 (v0.6.5).</summary>
    public double TextSize { get; set; } = 18;
    /// <summary>크기 조절이 Text에 적용되는 상태: Text Tool이거나 Text를 편집 중일 때.</summary>
    public bool SizeTargetsText => Tool == CanvasTool.Text || editor != null;
    /// <summary>편집 중인 Text가 있으면 그 크기, 없으면 새 Text 크기.</summary>
    public double CurrentTextSize => editedText?.FontSize ?? TextSize;
    /// <summary>편집 중 포커스가 이 요소로 옮겨가도 편집을 끝내지 않을지 판단합니다 (예: 크기 슬라이더).</summary>
    public Func<IInputElement?, bool>? KeepEditorOpen { get; set; }
    public void SetEditingFontSize(double size)
    {
        if (editor == null || editedText == null || editedText.FontSize == size) return;
        editedText.FontSize = size; editor.FontSize = size * Board.Zoom; DraftChanged?.Invoke();
    }
    public void FocusEditor() => editor?.Focus();
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
    // 그리는 중인 Stroke를 점마다 새로 만들지 않고 점만 추가합니다. 화면 표시 전용이며 저장은 live 목록을 사용합니다.
    private Stroke? liveStroke;
    private string liveId = "";
    private readonly List<Point> lasso = [];
    private CanvasSnapshot? before;
    private Point lastScreen, lastWorld, dragOrigin;
    // Lasso 이동 (v0.6.4): 드래그 중에는 선택된 Stroke의 표시 위치만 옮기고, 좌표는 펜을 뗄 때 한 번 반영합니다.
    private List<(InkItem Item, List<Sample> Points)>? dragInk;
    private List<(TextItem Item, double X, double Y)>? dragTexts;
    private Vector dragDelta;
    private readonly TranslateTransform dragTransform = new();
    private bool active, moved, penDown, panning, draggingSelection, radial;
    private DateTime suppressMouseUntil;
    private readonly PenButtonGate buttonGate = new();
    private StylusDevice? trackedPen;
    private readonly System.Windows.Threading.DispatcherTimer penPoll = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private int penPackets, nativePackets, buttonEvents, switches;
    private string lastButton = "없음";
    // WPF Stylus가 최근에 위치를 준 경우, Windows Pen·Raw Input 경로는 크기 원을 다시 갱신하지 않습니다.
    private long lastStylusPosition = long.MinValue / 2;
    private bool StylusPositionFresh => Environment.TickCount64 - lastStylusPosition < 150;
    public string PenInputSummary => $"Pen 입력 진단: WPF {penPackets}, Windows Pen {nativePackets}, Button 이벤트 {buttonEvents}, 도구 전환 {switches}\n최근 Button: {lastButton}";
    public event Action<string>? PenToolSwitched;
    public void ResetPenButton() { trackedPen = null; buttonGate.Reset(); penPoll.Stop(); }
    public void NativePenSample(Point screen, bool pressed)
    {
        if (!IsVisible || Window.GetWindow(this)?.IsActive != true) return;
        var point = PointFromScreen(screen);
        if (!new Rect(RenderSize).Contains(point)) return;
        nativePackets++; if (!StylusPositionFresh) UpdatePointer(point); HandlePenButton(pressed, true, "Windows Pen");
    }
    private long penMouseUntil;
    public void RawPenSample(Point screen, bool pressed, bool raw)
    {
        if (!IsVisible || Window.GetWindow(this)?.IsActive != true) return;
        var point = PointFromScreen(screen);
        if (!new Rect(RenderSize).Contains(point)) return;
        if (!StylusPositionFresh) UpdatePointer(point);
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
    private void ObservePen(StylusEventArgs e) => ObservePen(e, e.GetStylusPoints(this));
    private void ObservePen(StylusEventArgs e, StylusPointCollection points)
    {
        if (e.StylusDevice.TabletDevice.Type == TabletDeviceType.Touch) return;
        trackedPen = e.StylusDevice; if (!penPoll.IsEnabled) penPoll.Start(); penPackets++;
        bool? packetDown = null;
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
    private string editorInitialText = "";
    private double editorInitialFontSize;
    private readonly Dictionary<int, Point> touches = [];
    private Point touchCenter;
    private double touchDistance;
    private Brush background = new SolidColorBrush(Color.FromRgb(250, 249, 245));

    public BoardSurface()
    {
        inkLayer = new InkLayer { RenderTransform = viewTransform };
        overlayLayer = new DrawLayer(RenderOverlay) { RenderTransform = viewTransform, IsHitTestVisible = false };
        liveLayer = new DrawLayer(RenderLive) { RenderTransform = viewTransform, IsHitTestVisible = false };
        Children.Add(inkLayer); Children.Add(overlayLayer); Children.Add(liveLayer);
        Children.Add(pointerRing); SetZIndex(pointerRing, 100);
        StylusInAirMove += (_, e) =>
        {
            if (e.StylusDevice.TabletDevice.Type == TabletDeviceType.Touch) return;
            ObservePen(e); lastStylusPosition = Environment.TickCount64; UpdatePointer(e.GetPosition(this));
        };
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
        SyncView();
    }
    // Layer는 Canvas 전체 크기로 배치합니다. Text Layer가 화면 범위를 계산할 때 이번 배치 크기를 쓰도록
    // Canvas 크기가 확정되기 전에 layoutSize를 먼저 기록합니다 (v0.6.5: 첫 표시·창 크기 변경 시 Text 누락 수정).
    private Size layoutSize = Size.Empty;
    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (layoutSize != arrangeSize) { layoutSize = arrangeSize; overlayLayer.InvalidateVisual(); }
        var full = new Rect(arrangeSize);
        foreach (UIElement child in InternalChildren)
        {
            if (ReferenceEquals(child, inkLayer) || ReferenceEquals(child, overlayLayer) || ReferenceEquals(child, liveLayer)) { child.Arrange(full); continue; }
            double x = GetLeft(child), y = GetTop(child);
            child.Arrange(new Rect(new Point(double.IsNaN(x) ? 0 : x, double.IsNaN(y) ? 0 : y), child.DesiredSize));
        }
        return arrangeSize;
    }
    public bool IsEditing => active || editor != null;
    public Board CaptureForSave()
    {
        var snapshot = DataJson.Clone(Board);
        // 드래그 중 저장되면, 화면에 보이는 이동 위치를 저장본에도 반영합니다.
        if (dragInk != null && dragDelta.Length > .01)
        {
            var ids = dragInk.Select(d => d.Item.Id).ToHashSet();
            foreach (var ink in snapshot.Ink.Where(i => ids.Contains(i.Id))) ink.Points = ink.Points.Select(p => new Sample(p.X + dragDelta.X, p.Y + dragDelta.Y, p.Pressure)).ToList();
        }
        if (live is { Count: > 0 }) snapshot.Ink.Add(new() { Id = liveId, Points = live.ToList(), Color = PenColor, Width = PenWidth });
        if (editor != null && editedText != null)
        {
            var text = snapshot.Texts.First(t => t.Id == editedText.Id); text.Text = editor.Text;
        }
        return snapshot;
    }
    public void Load(Board board)
    {
        CommitEditor(); Board = board; History.Clear(); Selection.Clear(); SyncView(); Rebuild(); InvalidateVisual();
    }
    public void Rebuild()
    {
        strokes.Clear(); inkLayer.Clear();
        foreach (var item in Board.Ink.Where(i => i.Points.Count > 0)) { var stroke = item.ToStroke(); strokes[item.Id] = stroke; RenderInk(item.Id, stroke); }
        Redraw();
    }
    /// <summary>선택·Text 등 Canvas 위 표시를 다시 그립니다. 완성된 Stroke는 다시 만들지 않습니다.</summary>
    public void Redraw() { overlayLayer.InvalidateVisual(); liveLayer.InvalidateVisual(); }
    private void RenderInk(string id, Stroke stroke) => inkLayer.Render(id, dc => DrawInk(dc, stroke));
    private void RenderAllInk() { foreach (var item in Board.Ink) if (strokes.TryGetValue(item.Id, out var stroke)) RenderInk(item.Id, stroke); }
    private void SyncView()
    {
        viewTransform.Matrix = new Matrix(Board.Zoom, 0, 0, Board.Zoom, Board.ViewX, Board.ViewY);
        // 선 굵기를 1/Zoom으로 그리는 선택·Lasso 표시는 배율이 바뀔 때만 다시 그립니다.
        if (Board.Zoom != renderedZoom) { renderedZoom = Board.Zoom; Redraw(); }
    }
    public Point ToWorld(Point p) => new((p.X - Board.ViewX) / Board.Zoom, (p.Y - Board.ViewY) / Board.Zoom);
    public Point ToScreen(Point p) => new(p.X * Board.Zoom + Board.ViewX, p.Y * Board.Zoom + Board.ViewY);
    public static FormattedText Format(TextItem t) => new(t.Text.Length == 0 ? " " : t.Text, CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight, new Typeface("Malgun Gothic"), t.FontSize, Brushes.DarkSlateGray, 1)
    { MaxTextWidth = t.Width, Trimming = TextTrimming.None };
    public static Rect TextBounds(TextItem t) => new(t.X, t.Y, t.Width, Math.Max(t.FontSize * 1.5, Format(t).Height));
    // v0.6.6 Text 줄바꿈: Enter로만 줄을 바꾸고 박스 너비는 내용에 맞춥니다.
    private const double TextWidthMargin = 4;
    private static FormattedText Unwrapped(string text, double fontSize) => new(text.Length == 0 ? " " : text, CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight, new Typeface("Malgun Gothic"), fontSize, Brushes.DarkSlateGray, 1);
    /// <summary>자동 줄바꿈 없이 그렸을 때 가장 긴 줄의 너비.</summary>
    public static double NaturalWidth(string text, double fontSize) => Unwrapped(text, fontSize).WidthIncludingTrailingWhitespace;
    /// <summary>현재 박스 너비 안에서 자동 줄바꿈 없이 그려지는지 (Enter로만 줄을 바꾼 Text인지).</summary>
    public static bool FitsWithoutWrap(TextItem t) => NaturalWidth(t.Text, t.FontSize) <= t.Width + .5;
    /// <summary>이전 버전에서 박스 너비로 자동 줄바꿈되던 Text를 같은 모양의 명시적 줄바꿈으로 바꿉니다.</summary>
    public static string HardWrap(TextItem t)
    {
        if (FitsWithoutWrap(t)) return t.Text;
        var lines = new List<string>();
        foreach (var paragraph in t.Text.Replace("\r\n", "\n").Split('\n'))
        {
            string rest = paragraph;
            while (rest.Length > 0 && NaturalWidth(rest, t.FontSize) > t.Width)
            {
                int fit = 1;
                for (int lo = 1, hi = rest.Length; lo <= hi;)
                {
                    int mid = (lo + hi) / 2;
                    if (NaturalWidth(rest[..mid], t.FontSize) <= t.Width) { fit = mid; lo = mid + 1; } else hi = mid - 1;
                }
                int cut = fit;
                // 라틴 문자 단어 중간이면 앞의 공백에서 끊습니다. 한글은 WPF처럼 글자 단위로 끊습니다.
                if (cut < rest.Length && rest[cut] != ' ' && !IsHangul(rest[cut - 1]))
                {
                    int space = rest.LastIndexOf(' ', cut - 1);
                    if (space > 0) cut = space + 1;
                }
                lines.Add(rest[..cut].TrimEnd()); rest = rest[cut..].TrimStart(' ');
            }
            lines.Add(rest);
        }
        return string.Join("\n", lines);
    }
    private static bool IsHangul(char c) => c is >= '\uAC00' and <= '\uD7A3' or >= '\u1100' and <= '\u11FF' or >= '\u3130' and <= '\u318F';
    private static void FitTextWidth(TextItem t) => t.Width = Math.Max(20, NaturalWidth(t.Text, t.FontSize) + TextWidthMargin);
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); dc.DrawRectangle(background, null, new Rect(RenderSize));
        double step = 32 * Board.Zoom; while (step < 20) step *= 2; while (step > 90) step /= 2;
        var grid = Frozen(new SolidColorBrush(DarkMode ? Color.FromRgb(50, 57, 66) : Color.FromRgb(218, 225, 222)));
        for (double x = ((Board.ViewX % step) + step) % step; x < ActualWidth; x += step)
            for (double y = ((Board.ViewY % step) + step) % step; y < ActualHeight; y += step) dc.DrawEllipse(grid, null, new Point(x, y), .85, .85);
    }
    private void RenderOverlay(DrawingContext dc)
    {
        // viewTransform이 Layer에 적용되어 있으므로 World 좌표로 그립니다.
        var visible = layoutSize.IsEmpty ? RenderSize : layoutSize;
        var viewport = new Rect(ToWorld(new Point(0, 0)), ToWorld(new Point(visible.Width, visible.Height)));
        if (Selection.Count > 0)
        {
            var dashed = new Pen(Brushes.Teal, 1 / Board.Zoom) { DashStyle = DashStyles.Dash }; dashed.Freeze();
            foreach (var item in Board.Ink)
                if (Selection.Contains(item.Id) && strokes.TryGetValue(item.Id, out var stroke))
                {
                    var bounds = stroke.GetBounds(); if (dragInk != null) bounds.Offset(dragDelta);
                    dc.DrawRectangle(null, dashed, bounds);
                }
        }
        foreach (var text in Board.Texts)
        {
            if (editedText?.Id == text.Id || !viewport.IntersectsWith(TextBounds(text))) continue;
            var formatted = Format(text); if (DarkMode) formatted.SetForegroundBrush(Brushes.White);
            dc.DrawText(formatted, new Point(text.X, text.Y));
            if (Selection.Contains(text.Id)) dc.DrawRectangle(null, new Pen(Brushes.Teal, 1 / Board.Zoom), TextBounds(text));
        }
    }
    private void RenderLive(DrawingContext dc)
    {
        if (liveStroke != null && live is { Count: > 0 }) liveStroke.Draw(dc);
        if (lasso.Count > 1)
        {
            var geometry = Polygon(lasso); dc.DrawGeometry(LassoFill, new Pen(Brushes.Teal, 1 / Board.Zoom) { DashStyle = DashStyles.Dash }, geometry);
        }
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
    private void BeginSelectionDrag()
    {
        draggingSelection = true; dragDelta = new Vector(); dragTransform.X = dragTransform.Y = 0;
        // 원래 좌표를 한 번만 확보합니다. 드래그 중에는 Points 목록을 바꾸지 않으므로 참조를 그대로 보관합니다.
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
    public void ZoomAt(Point screen, double factor)
    {
        var world = ToWorld(screen); Board.Zoom = Math.Clamp(Board.Zoom * factor, .08, 8); Board.ViewX = screen.X - world.X * Board.Zoom; Board.ViewY = screen.Y - world.Y * Board.Zoom; UpdateView();
    }
    // 화면 이동·확대: Grid와 Layer Transform만 갱신합니다. Text는 화면 밖 생략 기준이 바뀌므로 함께 다시 그립니다.
    private void UpdateView() { UpdatePointer(); SyncView(); InvalidateVisual(); overlayLayer.InvalidateVisual(); ViewChanged?.Invoke(); }
    public void CenterSources(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet(); var bounds = Rect.Empty;
        foreach (var ink in Board.Ink.Where(x => set.Contains(x.Id))) bounds.Union(strokes[ink.Id].GetBounds());
        foreach (var text in Board.Texts.Where(x => set.Contains(x.Id))) bounds.Union(TextBounds(text));
        if (bounds.IsEmpty) return;
        Board.ViewX = ActualWidth / 2 - (bounds.Left + bounds.Width / 2) * Board.Zoom;
        Board.ViewY = ActualHeight / 2 - (bounds.Top + bounds.Height / 2) * Board.Zoom;
        Selection.Clear(); Selection.UnionWith(set); UpdateView(); Redraw();
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
        // 자동 줄바꿈 없이 입력한 만큼 늘어납니다. 이전 버전의 자동 줄바꿈 Text는 보이던 모양대로 줄을 나눠 엽니다.
        editorInitialText = HardWrap(text); editorInitialFontSize = text.FontSize;
        editor = new TextBox { Text = editorInitialText, FontFamily = new FontFamily("Malgun Gothic"), FontSize = text.FontSize * Board.Zoom, MinWidth = 80, MinHeight = 35, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, BorderBrush = Brushes.Teal, BorderThickness = new Thickness(1), Padding = new Thickness(2), Background = Brushes.White };
        editor.Background = DarkMode ? new SolidColorBrush(Color.FromRgb(32, 38, 46)) : Brushes.White; editor.Foreground = DarkMode ? Brushes.White : Brushes.DarkSlateGray;
        // 입력 상자의 테두리·여백만큼 당겨서 입력 중 글자 위치가 완성된 Text와 겹치도록 합니다.
        SetLeft(editor, p.X - 5); SetTop(editor, p.Y - 3); Children.Add(editor);
        editor.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape || (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))) { CommitEditor(); e.Handled = true; } };
        editor.TextChanged += (_, _) => DraftChanged?.Invoke();
        editor.LostKeyboardFocus += (_, e) => { if (editor != null && KeepEditorOpen?.Invoke(e.NewFocus) != true) CommitEditor(); };
        editor.Focus(); editor.CaretIndex = editor.Text.Length; Redraw();
        ToolChanged?.Invoke(); // 크기 슬라이더가 편집 중인 Text 크기를 표시하도록 갱신
    }
    public void CommitEditor()
    {
        if (editor == null || editedText == null) return;
        var box = editor; var text = editedText; editor = null; editedText = null;
        // 내용·크기가 그대로면 원래 Text와 너비를 유지합니다 (열어 보기만 해도 Undo 기록이 생기지 않도록).
        if (box.Text != editorInitialText || text.FontSize != editorInitialFontSize) { text.Text = box.Text; FitTextWidth(text); }
        Children.Remove(box); if (string.IsNullOrWhiteSpace(text.Text)) Board.Texts.Remove(text);
        if (before != null && DataJson.Write(before) != DataJson.Write(CanvasHistory.Capture(Board))) { History.Push(before); Changed?.Invoke(); }
        before = null; Redraw(); ToolChanged?.Invoke();
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

    // 완성된 Stroke 하나당 DrawingVisual 하나. 순서는 Board.Ink 순서(나중 Stroke가 위)를 따릅니다.
    private sealed class InkLayer : FrameworkElement
    {
        private readonly VisualCollection children;
        private readonly Dictionary<string, DrawingVisual> visuals = [];
        public InkLayer() { children = new VisualCollection(this); IsHitTestVisible = false; }
        protected override int VisualChildrenCount => children.Count;
        protected override Visual GetVisualChild(int index) => children[index];
        public void Clear() { children.Clear(); visuals.Clear(); }
        public void Render(string id, Action<DrawingContext> draw)
        {
            if (!visuals.TryGetValue(id, out var visual)) { visual = new DrawingVisual(); visuals[id] = visual; children.Add(visual); }
            using var dc = visual.RenderOpen(); draw(dc);
        }
        public void Remove(string id) { if (visuals.Remove(id, out var visual)) children.Remove(visual); }
        public void SetTransform(string id, Transform? transform) { if (visuals.TryGetValue(id, out var visual)) visual.Transform = transform; }
    }
    private sealed class DrawLayer(Action<DrawingContext> render) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc) => render(dc);
    }
}
