using System.Windows;

namespace ThoughtCanvas;

// Board 보관함·삭제 (v0.7.7)
public partial class MainWindow
{
    // Drive 폴더의 삭제 기록 (Board ID → 삭제 시각 UTC)
    private Dictionary<string, DateTime> deletedBoards = [];

    private void OpenFallbackBoard()
    {
        var next = store.ListEntries().FirstOrDefault(x => !x.Archived);
        var board = next.Id == null ? null : store.Load(next.Id);
        if (board == null) { board = new Board(); store.Save(board); }
        OpenBoard(board);
    }

    private void ArchiveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveNow();
            var archived = store.ListEntries().Where(x => x.Archived && x.Id != Surface.Board.Id).ToList();
            string toggle = Surface.Board.Archived ? "↩ 현재 Board 보관 해제 (Board 목록에 다시 표시)" : "🗄 현재 Board를 보관함으로 옮기기 (Board 목록에서 숨김)";
            var labels = new List<string> { toggle };
            labels.AddRange(archived.Select((x, i) => $"{i + 1}. {x.Title} · {x.ModifiedUtc.ToLocalTime():yyyy-MM-dd HH:mm}"));
            var chosen = Dialogs.Choose(this, $"보관함 · 보관한 Board {archived.Count}개", [.. labels]); if (chosen == null) return;
            if (chosen == toggle)
            {
                // 보관 여부는 내용이 아니므로 동기화 충돌(Clock)을 만들지 않습니다.
                Surface.Board.Archived = !Surface.Board.Archived; SaveNow(); UpdateBoards();
                SetStatus(Surface.Board.Archived ? "보관함으로 옮겼습니다. 다른 Board를 열면 목록에서 숨겨집니다." : "보관을 해제했습니다. Board 목록에 다시 표시됩니다.");
                return;
            }
            var board = store.Load(archived[labels.IndexOf(chosen) - 1].Id);
            if (board != null) { OpenBoard(board); SetStatus("보관한 Board를 열었습니다 · 목록에는 🗄 표시로 잠시 보입니다."); }
        }
        catch (Exception ex) { Error(ex); }
    }

    private async void DeleteBoardClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveNow();
            var board = Surface.Board; string id = board.Id, title = board.Title;
            var answer = MessageBox.Show(this,
                $"‘{title}’ Board를 삭제할까요?\n\n" +
                "· 이 기기의 필기·문서·복원 지점이 모두 지워집니다.\n" +
                "· Drive 폴더에 있는 이 Board의 저장본도 지웁니다 (Google Drive 휴지통으로 이동).\n" +
                "· 다른 기기에서도 목록에서 사라지고, 그 기기에 남은 사본은 다음 동기화 때 지워집니다.\n" +
                "  (삭제 뒤 그 기기에서 고친 내용이 있으면 지우지 않고 보관함으로 옮깁니다.)\n\n" +
                "이 기기에서는 되돌릴 수 없습니다.",
                "Board 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
            if (liveSession != null && liveBoardId == id) StopLive();
            store.Delete(id);
            settings.OfflinePendingBoards.Remove(id); deferredSync.Remove(id); uploaded.Remove(id);
            // 삭제 기록은 다음 Drive 목록 갱신 때 남깁니다. Offline이면 연결될 때까지 기다립니다.
            settings.PendingBoardDeletes[id] = DateTime.UtcNow; store.SaveSettings(settings);
            OpenFallbackBoard();
            SetStatus($"‘{title}’ Board를 삭제했습니다.");
            if (!string.IsNullOrWhiteSpace(settings.DriveSyncFolder)) { await RefreshDriveBoards(); if (!settings.PendingBoardDeletes.ContainsKey(id)) SetStatus($"‘{title}’ Board를 삭제했습니다 · 다른 기기에도 삭제 기록을 남겼습니다."); }
        }
        catch (Exception ex) { Error(ex); }
    }

    // 다른 기기에서 삭제한 Board가 이 기기에 남아 있으면 지웁니다.
    // 삭제 시각 뒤에 이 기기에서 고친 Board는 지우지 않고, 기록을 모두 유지한 채 새 ID의 보관 Board로 옮깁니다.
    private void ApplyRemoteDeletes()
    {
        if (deletedBoards.Count == 0 || saving) return;
        var found = store.ListEntries().Where(x => deletedBoards.ContainsKey(x.Id)).ToList();
        if (found.Count == 0) return;
        foreach (var entry in found)
        {
            bool current = entry.Id == Surface.Board.Id;
            if (current) SaveNow();
            var board = store.Load(entry.Id); if (board == null) continue;
            if (liveSession != null && liveBoardId == entry.Id) StopLive();
            settings.OfflinePendingBoards.Remove(entry.Id); deferredSync.Remove(entry.Id);
            if (board.ModifiedUtc > deletedBoards[entry.Id])
            {
                string newId = Guid.NewGuid().ToString("N");
                store.MoveToArchive(entry.Id, newId, board.Title + " · 삭제 후 이 기기 수정본");
                if (current) OpenBoard(store.Load(newId)!);
                SetStatus($"다른 기기에서 삭제한 ‘{board.Title}’을 이 기기에서 그 뒤에 고쳐, 지우지 않고 보관함으로 옮겼습니다.");
            }
            else
            {
                store.Delete(entry.Id);
                if (current) OpenFallbackBoard();
                SetStatus($"다른 기기에서 삭제한 ‘{board.Title}’ Board를 이 기기에서도 지웠습니다.");
            }
        }
        store.SaveSettings(settings);
    }
}
