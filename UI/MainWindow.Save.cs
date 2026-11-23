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
    private void MarkChanged(bool content)
    {
        dirty = true; contentDirty |= content; lastEdit = DateTime.UtcNow; Surface.Board.Revision++; Surface.Board.ModifiedUtc = lastEdit;
        // 필기 중 매 호출마다 네트워크·Drive 폴더를 조회하지 않고, 백그라운드에서 갱신한 캐시 값을 사용합니다.
        // Online이 확인되기 전의 편집은 기록해 두었다가, 다음 확인에서 Offline으로 판명되면 미전송 목록에 넣습니다.
        if (driveOnline == false) settings.OfflinePendingBoards.Add(Surface.Board.Id);
        else editedSinceOnlineCheck.Add(Surface.Board.Id);
        SetStatus("변경됨 · 자동 저장 대기");
    }
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
    private async void SaveClick(object sender, RoutedEventArgs e) { try { Surface.CommitEditor(); SaveDocumentIfNeeded(); await SaveAsync(); } catch (Exception ex) { Error(ex); } }
}
