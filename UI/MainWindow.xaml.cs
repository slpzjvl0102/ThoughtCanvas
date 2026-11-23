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
    private readonly bool firstRun;
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
        store = new BoardStore(root); firstRun = !store.HasSettings; settings = store.LoadSettings();
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
        Loaded += async (_, _) => { if (firstRun) RunFirstSetup(); await InitializeWorkspace(); };
        SizeChanged += (_, _) => UpdateToolbarLayout(); UpdateToolbarLayout();
        InitUpdater();
    }
    private void SetStatus(string message) => shell.Status = message;
    // Drive 연결 상태 캐시: null = 아직 확인 전
    private bool? driveOnline;
    private bool connectivityBusy;
    private DateTime lastConnectivity = DateTime.MinValue;
    private readonly HashSet<string> editedSinceOnlineCheck = [];
    // 툴바 배치 (v0.7.1): 자동이면 창이 세로로 길 때 Canvas 오른쪽에 세로로 둡니다. 크기가 바뀔 때만 계산하므로 필기에는 영향이 없습니다.
    private bool? verticalTools; private bool? stackedHeader;
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
    private async void SettingsClick(object sender, RoutedEventArgs e) { try { if (Dialogs.Settings(this, settings)) { uploaded.Clear(); store.SaveSettings(settings); SetStatus("설정을 저장했습니다."); lastConnectivity = DateTime.MinValue; await RefreshDriveBoards(); } } catch (Exception ex) { Error(ex); } }
    private void HelpClick(object sender, RoutedEventArgs e) => Dialogs.DiagnosticMessage(this, "Prototype 사용 안내 · v" + updater.CurrentVersion, "Pen: 필기 / S Pen Button: Pen → Eraser → Lasso\n한 손가락: 이동 / 두 손가락: 이동·확대 / 회전 없음\nMouse: 좌클릭 현재 Tool / 우클릭 이동 / Wheel Zoom\n휠클릭을 누른 채 방향으로 Drag하고 놓으면 Tool 선택\nText Tool: Click하여 생성, 기존 Text Click하여 수정\nLasso: 완전히 포함한 요소 선택, 선택 위 Drag로 이동\nShift+Click: Lasso에서 추가·해제 / Delete: 선택 삭제\nCtrl+Z / Ctrl+Y: 50회 Undo·Redo\n\nAI는 Gemini API key, Drive는 동기화 폴더 경로를 사용합니다. OAuth는 필요 없습니다.\n찾기: 모든 Board·Drive Keyword 검색 및 AI 관련 검색.\nDrive 목록에서 같은 Board를 이어 열 수 있습니다. 작업 → 다른 기기 변경 확인에서 유지·교체·빈 공간 통합을 선택합니다.\n작업 → 공동 편집: 같은 Network의 PC·노트북 연결. Export: PDF·HTML·Markdown·원본. 모바일은 이번 범위에서 제외되었습니다.\n\n" + Surface.PenInputSummary + "\n" + (rawPen?.Summary ?? "HID 초기화 전"));
    private void Error(Exception ex) { SetStatus(ex.Message); Dialogs.Message(this, "작업을 완료하지 못했습니다", ex.Message); }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        try { if (saving) { e.Cancel = true; SetStatus("저장을 마친 뒤 다시 닫아 주세요."); return; } SaveNow(); timer.Stop(); liveSession?.Dispose(); lifetime.Cancel(); }
        catch (Exception ex) { e.Cancel = true; Error(ex); }
    }
}


