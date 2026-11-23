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
}
