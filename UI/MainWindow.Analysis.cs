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
}
