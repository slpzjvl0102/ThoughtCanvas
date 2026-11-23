using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;

namespace ThoughtCanvas;

public enum CanvasTool { Pen, Eraser, Lasso, Text }
public partial class BoardSurface : Canvas
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
    // 저장용 캡처 (v0.7.4): UI 스레드에서는 Board를 JSON으로 한 번만 직렬화하고, 화면에만 있는 상태(드래그 이동량·그리는 중인 Stroke·편집 중 Text)는
    // 값으로 담아 둡니다. 역직렬화와 반영은 Restore에서 하므로 백그라운드 스레드에서 실행할 수 있습니다.
    public sealed record SaveCapture(string Json, HashSet<string>? DragIds, Vector DragDelta, InkItem? LiveInk, string? EditedTextId, string? EditedText);
    public SaveCapture CaptureJson() => new(DataJson.Write(Board),
        dragInk != null && dragDelta.Length > .01 ? dragInk.Select(d => d.Item.Id).ToHashSet() : null, dragDelta,
        live is { Count: > 0 } ? new InkItem { Id = liveId, Points = live.ToList(), Color = PenColor, Width = PenWidth } : null,
        editor != null && editedText != null ? editedText.Id : null, editor?.Text);
    public static Board Restore(SaveCapture capture) => Apply(capture, DataJson.Read<Board>(capture.Json));
    public static Board Apply(SaveCapture capture, Board snapshot)
    {
        // 드래그 중 저장되면, 화면에 보이는 이동 위치를 저장본에도 반영합니다.
        if (capture.DragIds != null)
            foreach (var ink in snapshot.Ink.Where(i => capture.DragIds.Contains(i.Id))) ink.Points = ink.Points.Select(p => new Sample(p.X + capture.DragDelta.X, p.Y + capture.DragDelta.Y, p.Pressure)).ToList();
        if (capture.LiveInk != null) snapshot.Ink.Add(capture.LiveInk);
        if (capture.EditedTextId != null) snapshot.Texts.First(t => t.Id == capture.EditedTextId).Text = capture.EditedText ?? "";
        return snapshot;
    }
    public Board CaptureForSave() => Restore(CaptureJson());
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
    public void Undo() { CommitEditor(); EndGesture(); if (History.Undo(Board)) { Selection.Clear(); Rebuild(); Changed?.Invoke(); } }
    public void ApplyCanvas(Board arranged)
    {
        CommitEditor(); EndGesture(); History.Push(CanvasHistory.Capture(Board));
        Board.Ink = DataJson.Clone(arranged.Ink); Board.Texts = DataJson.Clone(arranged.Texts);
        Selection.Clear(); Rebuild(); Changed?.Invoke(); Fit();
    }
    public void Redo() { CommitEditor(); EndGesture(); if (History.Redo(Board)) { Selection.Clear(); Rebuild(); Changed?.Invoke(); } }

}
