using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ThoughtCanvas;
public partial class MainWindow : Window
{
    private readonly BoardStore store;
    private readonly ShellViewModel shell = new();
    private readonly Settings settings;
    private readonly AiService ai = new();
    private NativePenObserver? penObserver;
    private RawPenInput? rawPen;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool initialized, dirty, contentDirty, documentDirty, updating, saving, aiBusy, driveBusy;
    private DateTime lastEdit = DateTime.UtcNow, lastSave = DateTime.UtcNow, lastAi = DateTime.MinValue, lastDrive = DateTime.MinValue;
    private readonly Dictionary<string, long> uploaded = [];
    private List<DriveFolderService.Entry> driveEntries = [];
    private bool catalogBusy, openingDrive;
    private Point radialOrigin;
    private CanvasTool? radialChoice;
    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => { penObserver = new NativePenObserver(this, Surface.NativePenSample, Surface.ResetPenButton); rawPen = new RawPenInput(this, Surface.RawPenSample); };
        Deactivated += (_, _) => Surface.ResetPenButton();
        Closed += (_, _) => { rawPen?.Dispose(); penObserver?.Dispose(); Surface.ResetPenButton(); System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged; };
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        Surface.PenToolSwitched += message => SetStatus(message);
        DataContext = shell;
        string root = Environment.GetEnvironmentVariable("THOUGHTCANVAS_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThoughtCanvas");
        store = new BoardStore(root); settings = store.LoadSettings();
        var board = store.Load(settings.LastBoardId) ?? new Board(); Surface.Load(board);
        if (!store.List().Any(x => x.Id == board.Id)) store.Save(board);
        Surface.Changed += () => { MarkChanged(true); UpdateCounts(); };
        Surface.DraftChanged += () => MarkChanged(true);
        Surface.ViewChanged += () => { dirty = true; ZoomButton.Content = $"{Surface.Board.Zoom:P0}"; };
        Surface.ToolChanged += UpdateTool;
        // 크기 슬라이더를 조작해도 Text 편집이 끝나지 않도록 합니다.
        Surface.KeepEditorOpen = focus => focus is Visual target && (ReferenceEquals(target, SizeControls) || target.IsDescendantOf(SizeControls));
        Surface.RadialOpened += OpenRadial; Surface.RadialMoved += MoveRadial; Surface.RadialClosed += CloseRadial;
        initialized = true; ApplyTheme(); ApplyFocus(); UpdateBoards(); UpdatePanels(); UpdateTool(); UpdateCounts();
        timer.Tick += TimerTick; timer.Start(); Closing += OnClosing;
        SetStatus("기기에 저장됨 · AI·Drive 연결은 설정에서 추가하세요.");
        Loaded += async (_, _) => await InitializeWorkspace();
        SizeChanged += (_, _) => UpdateToolbarLayout(); UpdateToolbarLayout();
    }
    private void SetStatus(string message) => shell.Status = message;
    private void MarkChanged(bool content)
    {
        dirty = true; contentDirty |= content; lastEdit = DateTime.UtcNow; Surface.Board.Revision++; Surface.Board.ModifiedUtc = lastEdit;
        // 필기 중 매 호출마다 네트워크·Drive 폴더를 조회하지 않고, 백그라운드에서 갱신한 캐시 값을 사용합니다.
        // Online이 확인되기 전의 편집은 기록해 두었다가, 다음 확인에서 Offline으로 판명되면 미전송 목록에 넣습니다.
        if (driveOnline == false) settings.OfflinePendingBoards.Add(Surface.Board.Id);
        else editedSinceOnlineCheck.Add(Surface.Board.Id);
        SetStatus("변경됨 · 자동 저장 대기");
    }
    // Drive 연결 상태 캐시: null = 아직 확인 전
    private bool? driveOnline;
    private bool connectivityBusy;
    private DateTime lastConnectivity = DateTime.MinValue;
    private readonly HashSet<string> editedSinceOnlineCheck = [];
    private async Task RefreshConnectivity()
    {
        if (connectivityBusy) return;
        connectivityBusy = true; lastConnectivity = DateTime.UtcNow; string folder = settings.DriveSyncFolder;
        try
        {
            // 확인 직전까지의 편집만 이번 결과로 판정하고, 확인 중에 들어온 편집은 다음 확인으로 넘깁니다.
            var checkedBoards = editedSinceOnlineCheck.ToArray();
            bool online = await Task.Run(() => System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable() && Directory.Exists(folder));
            if (folder != settings.DriveSyncFolder) { lastConnectivity = DateTime.MinValue; return; }
            driveOnline = online;
            foreach (var id in checkedBoards) { editedSinceOnlineCheck.Remove(id); if (!online) settings.OfflinePendingBoards.Add(id); }
        }
        catch { driveOnline = null; }
        finally { connectivityBusy = false; }
    }
    private void OnNetworkAvailabilityChanged(object? sender, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e)
        => Dispatcher.InvokeAsync(() => { lastConnectivity = DateTime.MinValue; _ = RefreshConnectivity(); });
    private async void TimerTick(object? sender, EventArgs e)
    {
        if (!connectivityBusy && (DateTime.UtcNow - lastConnectivity).TotalSeconds >= 5) _ = RefreshConnectivity();
        if (!workspaceReady) return;
        try
        {
            var now = DateTime.UtcNow; await LiveTick();
            if (!catalogBusy && (now - lastCatalog).TotalSeconds >= 15) await RefreshDriveBoards();
            if (documentDirty && (now - lastEdit).TotalSeconds >= 2) SaveDocumentIfNeeded();
            if (dirty && ((now - lastEdit).TotalSeconds >= 2 || (now - lastSave).TotalSeconds >= 10)) await SaveAsync();
            if (!aiBusy && !saving && settings.AutomaticAi && GeminiCredentials.Resolve(settings).Key.Length > 0 && Surface.Board.SavesSinceAi >= settings.AiEverySaves && (now - lastAi).TotalMinutes >= 3 && !Surface.IsEditing) await Analyze(false);
            if (!driveBusy && !saving && !deferredSync.Contains(Surface.Board.Id) && settings.AutomaticFolderBackup && !string.IsNullOrWhiteSpace(settings.DriveSyncFolder) && (now - lastDrive).TotalSeconds >= 60 && !Surface.IsEditing && uploaded.GetValueOrDefault(Surface.Board.Id, -1) != Surface.Board.Revision) await Upload(false);
        }
        catch (Exception ex) { SetStatus("작업 대기 · " + ex.Message); }
    }
    private async Task SaveAsync()
    {
        if (saving || !dirty) return; saving = true;
        var target = Surface.Board; long revision = target.Revision; bool counted = contentDirty;
        if (counted) target.SavesSinceAi++;
        // v0.7.4: UI 스레드에서는 JSON 직렬화 한 번만 합니다. 변경 해시(Stamp)·역직렬화·저장은 백그라운드에서 하고,
        // Stamp 결과는 캡처 시점의 Board 기준이므로 이전과 같습니다. 그사이 다른 Stamp가 있었으면 그 값을 유지합니다.
        var capture = Surface.CaptureJson(); string device = settings.DeviceId; string hashBefore = target.ContentHash;
        try
        {
            var stamp = await Task.Run(() =>
            {
                var snapshot = DataJson.Read<Board>(capture.Json); SyncEngine.Stamp(snapshot, device);
                var result = (Clock: new Dictionary<string, long>(snapshot.Clock), snapshot.ContentHash, snapshot.LastWriter);
                store.Save(BoardSurface.Apply(capture, snapshot)); return result;
            });
            if (target.ContentHash == hashBefore) { target.Clock = stamp.Clock; target.ContentHash = stamp.ContentHash; target.LastWriter = stamp.LastWriter; }
            lastSave = DateTime.UtcNow;
            store.SaveSettings(settings);
            if (ReferenceEquals(target, Surface.Board) && target.Revision == revision) { dirty = false; contentDirty = false; }
            SetStatus($"기기에 저장됨 · {DateTime.Now:HH:mm:ss} · AI {target.SavesSinceAi}/{settings.AiEverySaves}");
        }
        catch { if (counted) target.SavesSinceAi--; throw; }
        finally { saving = false; }
    }
    private void SaveNow()
    {
        Surface.CommitEditor(); Surface.EndGesture(); SaveDocumentIfNeeded();
        if (saving) throw new InvalidOperationException("저장 중입니다. 잠시 후 다시 시도해 주세요.");
        if (contentDirty) Surface.Board.SavesSinceAi++;
        try { SyncEngine.Stamp(Surface.Board, settings.DeviceId); store.Save(Surface.Board); dirty = false; contentDirty = false; lastSave = DateTime.UtcNow; SetStatus("기기에 저장됨"); }
        catch { if (contentDirty) Surface.Board.SavesSinceAi--; throw; }
        settings.LastBoardId = Surface.Board.Id; store.SaveSettings(settings);
    }
    private record BoardChoice(string Id, string Title, DriveFolderService.Entry? Remote = null) { public override string ToString() => Title; }
    private async Task RefreshDriveBoards()
    {
        if (catalogBusy) return;
        catalogBusy = true; lastCatalog = DateTime.UtcNow; string folder = settings.DriveSyncFolder;
        try
        {
            var catalog = await Task.Run(() => DriveFolderService.Discover(folder, lifetime.Token));
            registeredDevices = await Task.Run(() => DriveFolderService.RegisterDevice(folder, settings.DeviceId, settings.DeviceName));
            if (folder != settings.DriveSyncFolder || lifetime.IsCancellationRequested) return;
            driveEntries = catalog.Entries; UpdateBoards();
            await RefreshLibrary();
            if (catalog.Skipped > 0) SetStatus($"Drive 목록 {driveEntries.Count}개 · 읽지 못한 저장본 {catalog.Skipped}개");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { driveEntries = []; UpdateBoards(); SetStatus("Local 목록 유지 · Drive 목록 확인 실패: " + ex.Message); }
        finally { catalogBusy = false; }
    }
    private async void RefreshBoardsClick(object sender, RoutedEventArgs e) => await RefreshDriveBoards();
    // 툴바 배치 (v0.7.1): 자동이면 창이 세로로 길 때 Canvas 오른쪽에 세로로 둡니다. 크기가 바뀔 때만 계산하므로 필기에는 영향이 없습니다.
    private bool? verticalTools; private bool? stackedHeader;
    private void ToolbarModeClick(object sender, RoutedEventArgs e)
    {
        settings.ToolbarLayout = settings.ToolbarLayout switch { "Auto" => "Horizontal", "Horizontal" => "Vertical", _ => "Auto" };
        store.SaveSettings(settings); UpdateToolbarLayout();
    }
    private void UpdateToolbarLayout()
    {
        ToolbarModeButton.Content = settings.ToolbarLayout switch { "Horizontal" => "툴바 가로", "Vertical" => "툴바 세로", _ => "툴바 자동" };
        bool vertical = settings.ToolbarLayout switch { "Horizontal" => false, "Vertical" => true, _ => ActualWidth > 0 && ActualHeight > ActualWidth };
        if (verticalTools != vertical) ApplyToolbarLayout(vertical);
        // 좁은 창에서는 상단 오른쪽 버튼들을 두 번째 줄로 내립니다.
        bool stacked = ActualWidth > 0 && ActualWidth < 920;
        if (stackedHeader != stacked)
        {
            stackedHeader = stacked; Grid.SetRow(HeaderActions, stacked ? 1 : 0); Grid.SetColumn(HeaderActions, stacked ? 0 : 1);
            HeaderActions.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Stretch; HeaderActions.Margin = new Thickness(0, stacked ? 8 : 0, 0, 0);
        }
    }
    internal void ApplyToolbarLayout(bool vertical)
    {
        verticalTools = vertical;
        ((Panel)ToolsBar.Parent).Children.Remove(ToolsBar);
        if (vertical) { ContentGrid.Children.Add(ToolsBar); Grid.SetRow(ToolsBar, 0); Grid.SetColumn(ToolsBar, 1); }
        else { RootGrid.Children.Add(ToolsBar); Grid.SetRow(ToolsBar, 1); Grid.SetColumn(ToolsBar, 0); }
        var flow = vertical ? Orientation.Vertical : Orientation.Horizontal;
        ToolsBar.BorderThickness = vertical ? new Thickness(1, 0, 0, 0) : new Thickness(0, 0, 0, 1);
        ToolsDock.Margin = vertical ? new Thickness(8, 10, 8, 10) : new Thickness(16, 8, 16, 8);
        DockPanel.SetDock(ToolsActions, vertical ? Dock.Bottom : Dock.Right);
        ToolsActions.Orientation = ToolsWrap.Orientation = SizeStack.Orientation = SizeSlider.Orientation = flow;
        ToolsActions.VerticalAlignment = vertical ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        ToolsWrap.ItemWidth = vertical ? 104 : double.NaN;
        SizeSlider.Width = vertical ? double.NaN : 95; SizeSlider.Height = vertical ? 120 : double.NaN;
        SizeSlider.HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        SizeCaption.Margin = vertical ? new Thickness(0, 6, 0, 4) : new Thickness(14, 0, 5, 0);
        SizeCaption.HorizontalAlignment = SizeLabel.HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        SizeLabel.Margin = vertical ? new Thickness(0, 4, 0, 0) : new Thickness(6, 0, 0, 0); SizeLabel.TextAlignment = vertical ? TextAlignment.Center : TextAlignment.Left;
    }
    // 자동 검증 (v0.7.0): 같은 실행 파일을 --self-test로 따로 실행합니다. 작업 중인 Board와 데이터는 건드리지 않습니다.
    private async void SelfTestClick(object sender, RoutedEventArgs e)
    {
        SelfTestButton.IsEnabled = false;
        try
        {
            SaveNow(); SetStatus("자동 검증 실행 중 · 1분 정도 걸릴 수 있습니다");
            string report = Path.Combine(Path.GetTempPath(), "ThoughtCanvas", "self-test.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!); if (File.Exists(report)) File.Delete(report);
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false }; start.ArgumentList.Add("--self-test"); start.ArgumentList.Add(report);
            using (var process = Process.Start(start)!) await process.WaitForExitAsync();
            if (!File.Exists(report)) throw new IOException("자동 검증 결과 파일이 만들어지지 않았습니다.");
            var lines = File.ReadAllText(report).Split("\n\n");
            var failed = lines.Skip(1).Where(x => x.StartsWith("FAIL")).ToList();
            string summary = lines[0].Split('\n').Last();
            SetStatus("자동 검증 " + summary);
            Dialogs.DiagnosticMessage(this, "자동 검증 · " + summary,
                (failed.Count == 0 ? "모든 항목 통과" : "실패 항목\n\n" + string.Join("\n\n", failed)) + "\n\n전체 결과 파일: " + report);
        }
        catch (Exception ex) { SetStatus("자동 검증 실패"); Error(ex); }
        finally { SelfTestButton.IsEnabled = true; }
    }
    private void UpdateBoards()
    {
        // 이 기기에 있는 Board는 한 줄만 보입니다 (v0.6.9). 다른 기기에 확인하지 않은 변경이 있으면 그 줄에 표시합니다.
        // Drive(☁) 줄은 이 기기에 없는 Board만 보여 줍니다.
        var items = store.List().Select(x => new BoardChoice(x.Id, RemotePending(x.Id) ? $"{x.Title} · ☁ 다른 기기 변경 있음" : x.Title)).ToList();
        var local = items.Select(i => i.Id).ToHashSet();
        items.AddRange(driveEntries.Where(x => !local.Contains(x.BoardId)).Select(x => new BoardChoice(x.LocalId,
            $"☁ {x.Title} · {DeviceLabel(x.Device)} · {x.ModifiedUtc.ToLocalTime():MM-dd HH:mm} · v{x.Revision}", x)));
        // 목록이 그대로면 다시 넣지 않습니다 (15초마다 갱신될 때 펼친 목록이 닫히지 않도록).
        if (BoardPicker.ItemsSource is List<BoardChoice> shown && shown.Select(x => x.Id + x.Title).SequenceEqual(items.Select(x => x.Id + x.Title)))
        {
            var current = shown.FirstOrDefault(x => x.Id == Surface.Board.Id);
            if (!ReferenceEquals(BoardPicker.SelectedItem, current)) { updating = true; BoardPicker.SelectedItem = current; updating = false; }
            return;
        }
        updating = true; BoardPicker.ItemsSource = items; BoardPicker.SelectedItem = items.FirstOrDefault(x => x.Id == Surface.Board.Id); updating = false;
    }
    private bool RemotePending(string id)
    {
        var accepted = library.FirstOrDefault(x => x.Remote == null && x.Board.Id == id)?.Board.AcceptedSnapshots ?? [];
        return library.Any(x => x.Remote is { } r && x.Board.Id == id && r.Device != settings.DeviceId && !accepted.Contains(r.LocalId));
    }
    private async void BoardSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || updating || BoardPicker.SelectedItem is not BoardChoice item || item.Id == Surface.Board.Id) return;
        if (openingDrive) return;
        try
        {
            SaveNow();
            if (item.Remote is { } remote)
            {
                openingDrive = true; BoardPicker.IsEnabled = false;
                await OpenRemote(remote);
            }
            else { await CheckRemoteBeforeOpen(item.Id); var board = store.Load(item.Id); if (board != null) OpenBoard(board); }
        }
        catch (Exception ex) { Error(ex); UpdateBoards(); }
        finally { openingDrive = false; BoardPicker.IsEnabled = true; }
    }
    private void OpenBoard(Board board)
    {
        Surface.Load(board); dirty = false; contentDirty = false; documentDirty = false; settings.LastBoardId = board.Id; store.SaveSettings(settings); UpdateBoards(); UpdatePanels(); UpdateCounts();
    }
    private void NewBoard(object sender, RoutedEventArgs e)
    {
        try { SaveNow(); string? title = Dialogs.Ask(this, "새 Board", "큰 주제 이름", "새로운 생각"); if (title == null) return; var board = new Board { Title = title }; store.Save(board); OpenBoard(board); } catch (Exception ex) { Error(ex); }
    }
    private void RenameBoard(object sender, RoutedEventArgs e)
    {
        string? title = Dialogs.Ask(this, "Board 이름", "제목", Surface.Board.Title); if (title == null) return;
        try { Surface.Board.Title = title; MarkChanged(true); SaveNow(); UpdateBoards(); } catch (Exception ex) { Error(ex); }
    }
    private async void SaveClick(object sender, RoutedEventArgs e) { try { Surface.CommitEditor(); SaveDocumentIfNeeded(); await SaveAsync(); } catch (Exception ex) { Error(ex); } }
    private void SelectTool(object sender, RoutedEventArgs e) { Surface.CommitEditor(); Surface.EndGesture(); Surface.Tool = Enum.Parse<CanvasTool>((string)((Button)sender).Tag); UpdateTool(); }
    private void UpdateTool()
    {
        foreach (var button in new[] { PenButton, EraserButton, LassoButton, TextButton }) button.SetResourceReference(Control.BackgroundProperty, (string)button.Tag == Surface.Tool.ToString() ? "SelectedBrush" : "SurfaceBrush");
        // 선택 크기 버튼은 Lasso일 때만 보입니다 (v0.7.3).
        GrowButton.Visibility = ShrinkButton.Visibility = Surface.Tool == CanvasTool.Lasso ? Visibility.Visible : Visibility.Collapsed;
        bool text = Surface.SizeTargetsText;
        updating = true;
        SizeSlider.IsSnapToTickEnabled = text; SizeSlider.TickFrequency = 1;
        SizeSlider.Minimum = text ? 8 : 1; SizeSlider.Maximum = text ? 72 : Surface.Tool == CanvasTool.Eraser ? 100 : 12;
        SizeSlider.Value = text ? Surface.CurrentTextSize : Surface.Tool == CanvasTool.Eraser ? Surface.EraserSize : Surface.PenWidth;
        SizeLabel.Text = SizeSlider.Value.ToString("0.#"); updating = false; UpdateSizePreview();
    }
    private void ToolSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!initialized || updating) return;
        // Text Tool 또는 Text 편집 중에는 글자 크기를 바꿉니다 (새 Text 기본값 + 편집 중인 Text에 즉시 적용).
        if (Surface.SizeTargetsText) { double size = Math.Round(e.NewValue); Surface.TextSize = size; Surface.SetEditingFontSize(size); }
        else if (Surface.Tool == CanvasTool.Eraser) Surface.EraserSize = e.NewValue; else Surface.PenWidth = e.NewValue;
        SizeLabel.Text = e.NewValue.ToString("0.#"); UpdateSizePreview();
    }
    private void UpdateSizePreview()
    {
        bool text = Surface.SizeTargetsText;
        bool sized = text || Surface.Tool is CanvasTool.Pen or CanvasTool.Eraser;
        SizeControls.IsEnabled = sized;
        if (text)
        {
            double fontSize = Surface.CurrentTextSize;
            SizePreview.Visibility = Visibility.Collapsed; SizePreviewText.Visibility = Visibility.Visible; SizePreviewText.FontSize = Math.Min(26, fontSize);
            SizeControls.ToolTip = $"글자 크기 {fontSize:0} · 새 Text와 편집 중인 Text에 적용됩니다. 미리보기는 최대 26으로 축소 표시합니다.";
        }
        else
        {
            double size = Surface.Tool == CanvasTool.Eraser ? Surface.EraserSize : Surface.PenWidth;
            SizePreview.Visibility = Visibility.Visible; SizePreviewText.Visibility = Visibility.Collapsed;
            SizePreview.Width = SizePreview.Height = Math.Min(26, size);
            SizePreview.Fill = Surface.Tool == CanvasTool.Eraser ? Brushes.Transparent : (Brush)FindResource("TextBrush");
            SizeControls.ToolTip = $"Canvas 기준 지름 {size:0.#} · 화면에서는 Zoom에 따라 크기가 달라집니다. 미리보기는 최대 26으로 축소 표시합니다.";
        }
        Surface.UpdatePointer();
    }
    private void ToggleFocus(object sender, RoutedEventArgs e)
    {
        Surface.CommitEditor(); Surface.EndGesture(); settings.FocusMode = !settings.FocusMode; ApplyFocus(); store.SaveSettings(settings);
    }
    private void ApplyFocus()
    {
        HeaderBar.Visibility = ToolsBar.Visibility = StatusBar.Visibility = settings.FocusMode ? Visibility.Collapsed : Visibility.Visible;
        StatusRow.Height = new GridLength(settings.FocusMode ? 0 : 34);
        FocusButton.Content = settings.FocusMode ? "UI 표시 · F11" : "UI 숨기기 · F11";
    }
    private void ToggleTheme(object sender, RoutedEventArgs e)
    {
        settings.DarkMode = !settings.DarkMode; ApplyTheme(); UpdateTool(); store.SaveSettings(settings);
    }
    private void ApplyTheme()
    {
        string[] names = ["CanvasBrush", "SurfaceBrush", "ToolbarBrush", "TextBrush", "MutedBrush", "BorderBrush", "AccentBrush", "SelectedBrush"];
        string[] colors = settings.DarkMode
            ? ["#171B21", "#20262E", "#1B2229", "#FFFFFF", "#A3B3BC", "#38444D", "#267E77", "#315B56"]
            : ["#F7F9F8", "#FFFFFF", "#EEF3F1", "#223C36", "#667C74", "#DAE3DF", "#126C66", "#D3EADF"];
        for (int i = 0; i < names.Length; i++) Application.Current.Resources[names[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        Surface.SetTheme(settings.DarkMode); ThemeButton.Content = settings.DarkMode ? "Light mode" : "Dark mode";
    }
    private void UpdateCounts() { CountsLabel.Text = $"Stroke {Surface.Board.Ink.Count} · Text {Surface.Board.Texts.Count} · Undo {Surface.History.UndoCount}/50"; Welcome.Visibility = Surface.Board.Ink.Count + Surface.Board.Texts.Count == 0 ? Visibility.Visible : Visibility.Collapsed; }
    private void UndoClick(object sender, RoutedEventArgs e) => Surface.Undo();
    private void RedoClick(object sender, RoutedEventArgs e) => Surface.Redo();
    private void ShrinkClick(object sender, RoutedEventArgs e) => Surface.ResizeSelection(.9);
    private void GrowClick(object sender, RoutedEventArgs e) => Surface.ResizeSelection(1.1);
    private void FitClick(object sender, RoutedEventArgs e) => Surface.Fit();
    private void ZoomIn(object sender, RoutedEventArgs e) => Surface.ZoomAt(new(Surface.ActualWidth / 2, Surface.ActualHeight / 2), 1.2);
    private void ZoomOut(object sender, RoutedEventArgs e) => Surface.ZoomAt(new(Surface.ActualWidth / 2, Surface.ActualHeight / 2), 1 / 1.2);
    private void ResetZoom(object sender, RoutedEventArgs e) => Surface.ZoomAt(new(Surface.ActualWidth / 2, Surface.ActualHeight / 2), 1 / Surface.Board.Zoom);
    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11) { ToggleFocus(this, e); e.Handled = true; return; }
        if (Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is PasswordBox) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (e.Key == Key.Z) { Surface.Undo(); e.Handled = true; } if (e.Key == Key.Y) { Surface.Redo(); e.Handled = true; } if (e.Key == Key.S) { SaveClick(sender, e); e.Handled = true; }
        }
        else if (e.Key == Key.Delete) { Surface.DeleteSelection(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Surface.Selection.Clear(); Surface.Redraw(); }
    }
    private void ShowPanel(bool visible) { PanelColumn.Width = new GridLength(visible ? 470 : 0); SidePanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed; }
    private void TogglePanel(object sender, RoutedEventArgs e) => ShowPanel(SidePanel.Visibility != Visibility.Visible);
    private void UpdatePanels()
    {
        updating = true; var b = Surface.Board; DocumentEditor.Text = b.UserDocument; documentDirty = false;
        ArchiveEditor.Text = b.Analysis?.ArchivedDocument ?? "취소·보류한 아이디어는 AI 정리 후 맥락과 함께 표시됩니다.";
        AnalysisInfo.Text = b.Analysis == null ? "API 설정 후 ‘지금 정리’를 실행하세요." : $"AI 기준 v{b.Analysis.SourceRevision} · 현재 v{b.Revision} · {b.Analysis.CreatedUtc.ToLocalTime():MM/dd HH:mm}";        if (b.Analysis?.RequestMetrics is { } usage)
            AnalysisInfo.Text += $"\n입력 Token {usage.PromptTokens?.ToString("N0") ?? "미제공"} · 이미지 {usage.ImagesSent}장 / 재사용 {usage.ImagesReused}장\n전송 본문 {usage.InputCharacters:N0}자 · 과거 문서 {usage.HistorySnapshotsOmitted}개 전송 생략";
        ReviewList.ItemsSource = b.Analysis?.Reviews; int count = b.Analysis?.Reviews.Count(r => !r.Resolved) ?? 0; ReviewTab.Header = $"검증 {count}"; PanelToggle.Content = $"문서 · 검증 {count}";
        ProposalButton.Visibility = b.ProposedDocument == null ? Visibility.Collapsed : Visibility.Visible;
        TopicFilter.ItemsSource = new[] { "모든 대주제" }.Concat(SearchCandidates().Select(i => i.Idea.Topic).Distinct()); TopicFilter.SelectedIndex = 0;
        CategoryFilter.ItemsSource = new[] { "모든 분류" }; CategoryFilter.SelectedIndex = 0; updating = false; RefreshSearch();
    }
    private void DocumentChanged(object sender, TextChangedEventArgs e) { if (!updating && initialized) { documentDirty = true; lastEdit = DateTime.UtcNow; } }
    private void SaveDocumentIfNeeded()
    {
        if (!documentDirty) return; var b = Surface.Board; b.UserDocument = DocumentEditor.Text; b.DocumentEdited = true;
        b.DocumentEdits.Add(new() { Text = b.UserDocument, SourceRevision = b.Analysis?.SourceRevision ?? b.Revision }); documentDirty = false; MarkChanged(true);
    }
    private void SaveDocument(object sender, RoutedEventArgs e) { try { SaveDocumentIfNeeded(); SaveNow(); } catch (Exception ex) { Error(ex); } }
    private void CompareDocument(object sender, RoutedEventArgs e)
    {
        SaveDocumentIfNeeded(); var b = Surface.Board; if (b.ProposedDocument == null) return; string? merged = Dialogs.Compare(this, b.UserDocument, b.ProposedDocument);
        if (merged == null) return; b.UserDocument = merged; b.DocumentEdited = true; b.ProposedDocument = null;
        b.DocumentEdits.Add(new() { Text = merged, SourceRevision = b.Analysis?.SourceRevision ?? b.Revision }); MarkChanged(true); UpdatePanels();
    }
    private void ResolveReview(object sender, RoutedEventArgs e)
    {
        if (ReviewList.SelectedItem is not ReviewItem review) return;
        if (string.IsNullOrWhiteSpace(ReviewAnswer.Text)) { SetStatus("확인한 내용을 답변으로 남겨 주세요."); return; }
        review.Answer = ReviewAnswer.Text; review.Resolved = true; MarkChanged(true); SaveDocumentIfNeeded(); UpdatePanels(); SetStatus("검증 답변을 기록했습니다. 다음 AI 정리에 반영됩니다.");
    }
    private void ReviewSelectionChanged(object sender, SelectionChangedEventArgs e) { if (ReviewList.SelectedItem is ReviewItem review) ReviewAnswer.Text = review.Answer; }
    private void ReviewSource(object sender, RoutedEventArgs e) { if (ReviewList.SelectedItem is ReviewItem review) ShowSources(review.SourceIds, Surface.Board.Analysis?.SourceRevision ?? Surface.Board.Revision); }
    private void ShowSources(List<string> ids, long revision) => SourceViewer.Show(this, Surface.Board, v => store.LoadRevision(Surface.Board.Id, v), ids, revision);
    private void DocumentSourcesClick(object sender, RoutedEventArgs e) => SourceViewer.Show(this, Surface.Board, v => store.LoadRevision(Surface.Board.Id, v));
    private async void FullAnalyzeClick(object sender, RoutedEventArgs e) { try { await Analyze(true, true); } catch (Exception ex) { Error(ex); } }
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (initialized && !updating) RefreshSearch(); }
    private void SearchFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || updating) return;
        if (ReferenceEquals(sender, TopicFilter)) { updating = true; CategoryFilter.ItemsSource = new[] { "모든 분류" }.Concat(SearchCandidates().Where(i => TopicFilter.SelectedIndex == 0 || i.Idea.Topic == TopicFilter.SelectedItem?.ToString()).Select(i => i.Idea.Category).Distinct()); CategoryFilter.SelectedIndex = 0; updating = false; }
        RefreshSearch();
    }
    private void RefreshSearch()
    {
        if (!initialized) return;
        SearchResults.ItemsSource = LibrarySearch.Keyword(SearchCandidates(), SearchBox.Text.Trim(), TopicFilter.SelectedIndex <= 0 ? null : TopicFilter.SelectedItem?.ToString(), CategoryFilter.SelectedIndex <= 0 ? null : CategoryFilter.SelectedItem?.ToString());
    }
    private void SearchSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SearchResults.SelectedItem is not SearchHit hit) return;
        var idea = hit.Idea; EvidenceBox.Text = $"{hit.Match} · {hit.Explanation}\n상태: {idea.Status}\n근거: {idea.Evidence}\n맥락: {idea.Context}\n이유: {idea.Reason}\n관계: {string.Join(" / ", idea.Relations)}\n원본 v{idea.SourceRevision}";
        ShowHit(hit);
    }
    private async void AnalyzeClick(object sender, RoutedEventArgs e) { try { await Analyze(true); } catch (Exception ex) { Error(ex); } }
    private async Task Analyze(bool interactive, bool full = false)
    {
        if (aiBusy) { SetStatus("AI 정리 중입니다."); return; }
        string key = GeminiCredentials.Resolve(settings).Key; if (key.Length == 0) { if (interactive) Dialogs.Message(this, "AI 설정 필요", "GEMINI_API_KEY 또는 GOOGLE_API_KEY 환경 변수를 설정하거나 앱 설정에 Gemini API key를 입력하세요. Model은 설정에서 변경할 수 있습니다. 자동 실행은 설정에서 켤 수 있습니다."); return; }
        SaveNow(); var target = Surface.Board; string json = DataJson.Write(target); aiBusy = true;
        Board snapshot; List<RegionData> regions;
        try { (snapshot, regions) = await RenderOffUi(() => { var board = DataJson.Read<Board>(json); return (board, ExportService.RenderRegions(board)); }); }
        catch { aiBusy = false; throw; }
        lastAi = DateTime.UtcNow; SetStatus("AI가 원본을 구조화하고 있습니다. 계속 필기하셔도 됩니다.");
        try
        {
            var result = await ai.Analyze(snapshot, regions, key, settings.Model, lifetime.Token, full);
            if (Surface.Board.Id == snapshot.Id) { SaveDocumentIfNeeded(); target = Surface.Board; } else target = store.Load(snapshot.Id) ?? target;
            AiService.Apply(target, result); target.SavesSinceAi = Math.Max(0, target.SavesSinceAi - snapshot.SavesSinceAi); target.Revision++; target.ModifiedUtc = DateTime.UtcNow;
            TopicDocuments.Refresh(target); SyncEngine.Stamp(target, settings.DeviceId); store.Save(target);
            if (ReferenceEquals(target, Surface.Board)) { dirty = true; UpdatePanels(); if (interactive) ShowPanel(true); }
            SetStatus("AI 초안이 준비되었습니다. 검증 Panel에서 불확실한 내용을 확인하세요.");
        }
        finally { aiBusy = false; }
    }
    private void ExportClick(object sender, RoutedEventArgs e)
    {
        try { SaveNow(); var snapshot = DataJson.Clone(Surface.Board); var regions = ExportService.RenderRegions(snapshot); string directory = MakeExport(snapshot, regions); PdfExporter.Export(directory, snapshot, regions); SetStatus("Export 완료 · " + directory); Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true }); } catch (Exception ex) { Error(ex); }
    }
    private string MakeExport(Board snapshot, List<RegionData> regions)
    {
        var versions = (snapshot.Analysis?.Ideas.Select(i => i.SourceRevision) ?? []).Append(snapshot.Analysis?.SourceRevision ?? snapshot.Revision).Distinct();
        var sources = versions.Select(v => store.LoadRevision(snapshot.Id, v)).Where(b => b != null).Cast<Board>();
        return ExportService.Export(snapshot, Path.Combine(store.Root, "Exports"), regions, sources);
    }
    // WPF 렌더링(구역 PNG·원본 이미지)과 Export 파일 쓰기를 별도 STA 스레드에서 실행합니다 (v0.7.4).
    // 그 스레드에서 실패하면 이전처럼 UI 스레드에서 다시 실행하므로, 결과와 오류 안내는 이전과 같습니다.
    internal static Task<T> OnRenderThread<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception ex) { done.SetException(ex); }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true, Name = "ThoughtCanvas render" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
    private static async Task<T> RenderOffUi<T>(Func<T> work)
    {
        try { return await OnRenderThread(work); }
        catch { return work(); }
    }
    private async Task Upload(bool interactive)
    {
        if (interactive) deferredSync.Remove(Surface.Board.Id);
        if (driveBusy) return; lastDrive = DateTime.UtcNow; SaveNow(); string json = DataJson.Write(Surface.Board); driveBusy = true;
        Board snapshot; string export;
        try { (snapshot, export) = await RenderOffUi(() => { var board = DataJson.Read<Board>(json); return (board, MakeExport(board, ExportService.RenderRegions(board))); }); }
        catch { driveBusy = false; throw; }
        SetStatus("기기에 저장됨 · Drive 폴더에 저장 중");
        try
        {
            string folder = settings.DriveSyncFolder; string device = settings.DeviceId;
            await Task.Run(() =>
            {
                DriveFolderService.Publish(folder, device, snapshot, export, lifetime.Token);
                // 자동 백업용 Export는 Drive 폴더로 복사한 뒤 기기에서 지웁니다 (v0.7.4, 이전: 1분마다 기기에 누적).
                try { Directory.Delete(export, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            });
            uploaded[snapshot.Id] = snapshot.Revision; lastCatalog = DateTime.MinValue; // 폴더 정리 후 목록을 바로 다시 읽습니다.
            settings.OfflinePendingBoards.Remove(snapshot.Id); store.SaveSettings(settings);
            SetStatus("Drive 폴더 저장 완료 · v" + snapshot.Revision + " · Cloud 업로드는 Google Drive에서 확인하세요.");
        }
        catch (Exception ex) { settings.OfflinePendingBoards.Add(snapshot.Id); store.SaveSettings(settings); SetStatus("Local 저장됨 · Drive 폴더 저장 대기: " + ex.Message); if (interactive) throw; }
        finally { driveBusy = false; }
    }
    private async void DriveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string? choice = Dialogs.Choose(this, "Google Drive 폴더", new[] { "현재 Board를 폴더에 저장", "저장본 열기 (복사본)", "폴더 열기" });
            if (choice == "현재 Board를 폴더에 저장") await Upload(true);
            if (choice == "폴더 열기")
            {
                var root = DriveFolderService.Root(settings.DriveSyncFolder); Directory.CreateDirectory(root);
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { root } });
            }
            if (choice == "저장본 열기 (복사본)")
            {
                var picker = new Microsoft.Win32.OpenFileDialog { Filter = "ThoughtCanvas 저장본 (snapshot.zip)|snapshot.zip", InitialDirectory = DriveFolderService.Root(settings.DriveSyncFolder), Title = "복사본으로 열 저장본 선택" };
                if (picker.ShowDialog(this) != true) return;
                var bundle = await Task.Run(() => DriveFolderService.Read(picker.FileName)); var board = bundle.Board;
                SaveNow(); board.Id = Guid.NewGuid().ToString("N"); board.Title += " · Drive 복사본";
                foreach (var source in bundle.Sources.OrderBy(b => b.Revision)) { source.Id = board.Id; store.Save(source); }
                store.Save(board); OpenBoard(board);
            }
        }
        catch (OperationCanceledException) { SetStatus("Drive 폴더 작업이 취소되었습니다."); } catch (Exception ex) { Error(ex); }
    }
    private async void SettingsClick(object sender, RoutedEventArgs e) { try { if (Dialogs.Settings(this, settings)) { uploaded.Clear(); store.SaveSettings(settings); SetStatus("설정을 저장했습니다."); lastConnectivity = DateTime.MinValue; await RefreshDriveBoards(); } } catch (Exception ex) { Error(ex); } }
    private void HelpClick(object sender, RoutedEventArgs e) => Dialogs.DiagnosticMessage(this, "Prototype 사용 안내 · v0.7.4", "Pen: 필기 / S Pen Button: Pen → Eraser → Lasso\n한 손가락: 이동 / 두 손가락: 이동·확대 / 회전 없음\nMouse: 좌클릭 현재 Tool / 우클릭 이동 / Wheel Zoom\n휠클릭을 누른 채 방향으로 Drag하고 놓으면 Tool 선택\nText Tool: Click하여 생성, 기존 Text Click하여 수정\nLasso: 완전히 포함한 요소 선택, 선택 위 Drag로 이동\nShift+Click: Lasso에서 추가·해제 / Delete: 선택 삭제\nCtrl+Z / Ctrl+Y: 50회 Undo·Redo\n\nAI는 Gemini API key, Drive는 동기화 폴더 경로를 사용합니다. OAuth는 필요 없습니다.\n찾기: 모든 Board·Drive Keyword 검색 및 AI 관련 검색.\nDrive 목록에서 같은 Board를 이어 열 수 있습니다. 작업 → 다른 기기 변경 확인에서 유지·교체·빈 공간 통합을 선택합니다.\n작업 → 공동 편집: 같은 Network의 PC·노트북 연결. Export: PDF·HTML·Markdown·원본. 모바일은 이번 범위에서 제외되었습니다.\n\n" + Surface.PenInputSummary + "\n" + (rawPen?.Summary ?? "HID 초기화 전"));
    private void OpenRadial(Point p) { radialOrigin = p; radialChoice = null; DrawRadial(); }
    private void MoveRadial(Point p)
    {
        Vector d = p - radialOrigin; radialChoice = d.Length < 20 ? null : Math.Abs(d.X) > Math.Abs(d.Y) ? (d.X > 0 ? CanvasTool.Eraser : CanvasTool.Text) : (d.Y > 0 ? CanvasTool.Lasso : CanvasTool.Pen); DrawRadial();
    }
    private void CloseRadial(Point p) { MoveRadial(p); if (radialChoice.HasValue) { Surface.Tool = radialChoice.Value; UpdateTool(); } RadialLayer.Children.Clear(); }
    private void DrawRadial()
    {
        RadialLayer.Children.Clear(); double radius = 88;
        foreach (var (tool, angle) in new[] { (CanvasTool.Pen, -90d), (CanvasTool.Eraser, 0d), (CanvasTool.Lasso, 90d), (CanvasTool.Text, 180d) })
        {
            Point P(double a, double r) => new(radialOrigin.X + r * Math.Cos(a * Math.PI / 180), radialOrigin.Y + r * Math.Sin(a * Math.PI / 180));
            var geometry = new StreamGeometry(); using (var c = geometry.Open()) { c.BeginFigure(radialOrigin, true, true); c.LineTo(P(angle - 45, radius), true, false); c.ArcTo(P(angle + 45, radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false); }
            RadialLayer.Children.Add(new System.Windows.Shapes.Path { Data = geometry, Fill = tool == radialChoice ? new SolidColorBrush(Color.FromRgb(190, 225, 210)) : Brushes.White, Stroke = Brushes.Teal, StrokeThickness = 1 });
            var label = new TextBlock { Text = tool.ToString(), FontWeight = FontWeights.SemiBold, Foreground = Brushes.DarkSlateGray, Width = 60, TextAlignment = TextAlignment.Center }; var p = P(angle, 54); Canvas.SetLeft(label, p.X - 30); Canvas.SetTop(label, p.Y - 10); RadialLayer.Children.Add(label);
        }
    }
    private void Error(Exception ex) { SetStatus(ex.Message); Dialogs.Message(this, "작업을 완료하지 못했습니다", ex.Message); }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        try { if (saving) { e.Cancel = true; SetStatus("저장을 마친 뒤 다시 닫아 주세요."); return; } SaveNow(); timer.Stop(); liveSession?.Dispose(); lifetime.Cancel(); }
        catch (Exception ex) { e.Cancel = true; Error(ex); }
    }
}


