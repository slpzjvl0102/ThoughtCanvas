using System.Windows;
using System.Windows.Controls;
namespace ThoughtCanvas;

public partial class MainWindow
{
    private DateTime lastCatalog = DateTime.MinValue;
    private bool workspaceReady;
    private void ReadDocumentClick(object sender, RoutedEventArgs e) { SaveDocumentIfNeeded(); Dialogs.ReadDocument(this, Surface.Board.Title, Surface.Board.UserDocument); }
    private void DocumentHistoryClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveNow(); var edits = Surface.Board.DocumentEdits.AsEnumerable().Reverse().ToList();
            var labels = edits.Select((x, i) => $"{i + 1}. {x.AtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {x.Origin}").ToArray();
            var chosen = Dialogs.Choose(this, "문서 수정·대체 기록", labels); if (chosen == null) return;
            string? merged = Dialogs.Compare(this, Surface.Board.UserDocument, edits[Array.IndexOf(labels, chosen)].Text);
            if (merged == null) return; Surface.Board.UserDocument = merged; Surface.Board.DocumentEdited = true;
            Surface.Board.DocumentEdits.Add(new() { Origin = "history_restore", Text = merged, SourceRevision = Surface.Board.Revision }); MarkChanged(true); SaveNow(); UpdatePanels();
        }
        catch (Exception ex) { Error(ex); }
    }
    private readonly HashSet<string> deferredSync = [];
    private async Task InitializeWorkspace()
    {
        Surface.IsEnabled = false;
        try
        {
            await RefreshDriveBoards();
            if (settings.OfflinePendingBoards.Contains(Surface.Board.Id) && System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable() && Directory.Exists(settings.DriveSyncFolder))
            {
                string? choice = Dialogs.Choose(this, "연결 복구 · Offline 작업 확인", ["현재 기기 작업을 유지하고 Drive 폴더에 저장", "다른 기기 변경과 비교", "나중에 확인"]);
                if (choice == "현재 기기 작업을 유지하고 Drive 폴더에 저장") await Upload(true);
                if (choice == "나중에 확인" || choice == null) { deferredSync.Add(Surface.Board.Id); lastDrive = DateTime.UtcNow; return; }
            }
            await CheckRemoteBeforeOpen(Surface.Board.Id);
        }
        catch (Exception ex) { Error(ex); }
        finally { Surface.IsEnabled = true; workspaceReady = true; }
    }
    private bool notionBusy;
    private async void NotionClick(object sender, RoutedEventArgs e)
    {
        if (notionBusy) return;
        try { notionBusy = true; SaveNow(); var url = await NotionExporter.Export(DataJson.Clone(Surface.Board), Secrets.Unprotect(settings.ProtectedNotionToken), settings.NotionParentId, lifetime.Token); Dialogs.DiagnosticMessage(this, "Notion 전송 완료", url); }
        catch (Exception ex) { Error(ex); }
        finally { notionBusy = false; }
    }
    private readonly Dictionary<string, BoardBundle> remoteBundles = [];
    private List<LibraryItem> library = [];
    private List<SearchHit> SearchCandidates()
    {
        // 보관용 원본은 같은 내용이 겹쳐 보이므로 검색 대상에서 뺍니다 (보관함에서 열면 현재 Board로 검색됩니다).
        var list = library.Where(x => (x.Board.Id != Surface.Board.Id || x.Remote != null) && !x.Board.Archived && !(x.Remote?.Archived ?? false)).ToList();
        list.Add(new(Surface.Board, "현재 Board", null));
        if (SearchAll?.IsChecked != true) list = list.Where(x => x.Board.Id == Surface.Board.Id && x.Remote == null).ToList();
        return LibrarySearch.Candidates(list);
    }
    private void SearchScopeChanged(object sender, RoutedEventArgs e) { if (initialized) { SaveDocumentIfNeeded(); UpdatePanels(); } }
    private bool relatedBusy;
    private async void RelatedClick(object sender, RoutedEventArgs e)
    {
        if (relatedBusy || string.IsNullOrWhiteSpace(SearchBox.Text)) return;
        string query = SearchBox.Text.Trim(); string? topic = TopicFilter.SelectedIndex <= 0 ? null : TopicFilter.SelectedItem?.ToString(); string? category = CategoryFilter.SelectedIndex <= 0 ? null : CategoryFilter.SelectedItem?.ToString();
        bool all = SearchAll.IsChecked == true; string boardId = Surface.Board.Id;
        try
        {
            relatedBusy = true; SetStatus("모든 검색 대상에서 관련 아이디어를 확인하고 있습니다.");
            var candidates = LibrarySearch.Keyword(SearchCandidates(), "", topic, category);
            var found = await LibrarySearch.Related(candidates, query, GeminiCredentials.Resolve(settings).Key, settings.Model, lifetime.Token, autoSwitch: settings.AutoSwitchModel);
            if (SearchBox.Text.Trim() != query || all != (SearchAll.IsChecked == true) || boardId != Surface.Board.Id || topic != (TopicFilter.SelectedIndex <= 0 ? null : TopicFilter.SelectedItem?.ToString()) || category != (CategoryFilter.SelectedIndex <= 0 ? null : CategoryFilter.SelectedItem?.ToString())) { SetStatus("검색 조건이 바뀌어 이전 AI 검색 결과를 표시하지 않았습니다."); return; }
            var exact = LibrarySearch.Keyword(candidates, query, topic, category);
            SearchResults.ItemsSource = exact.Concat(found.Where(f => !exact.Any(e => e.Item == f.Item && e.Idea == f.Idea))).ToList();
            SetStatus($"Keyword {exact.Count}개 · AI 관련 {found.Count}개");
        }
        catch (Exception ex) { Error(ex); }
        finally { relatedBusy = false; }
    }
    private void ShowHit(SearchHit hit)
    {
        if (hit.Idea.SourceIds.Count == 0) return;
        SourceViewer.Show(this, hit.Item.Board, revision =>
            hit.Item.Remote is { } remote && remoteBundles.TryGetValue(remote.Path, out var bundle)
                ? bundle.Sources.FirstOrDefault(b => b.Revision == revision)
                : store.LoadRevision(hit.Item.Board.Id, revision), hit.Idea.SourceIds, hit.Idea.SourceRevision);
    }    private void TopicsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveNow(); TopicDocuments.Refresh(Surface.Board);
            var names = Surface.Board.TopicDocuments.Select(d => (d.Archived ? "[검토 보관] " : "") + d.Topic).ToArray();
            if (names.Length == 0) { SetStatus("AI 정리 후 주제별 문서를 편집할 수 있습니다."); return; }
            var selected = Dialogs.Choose(this, "주제별 문서", names); if (selected == null) return;
            var doc = Surface.Board.TopicDocuments[Array.IndexOf(names, selected)];
            var edited = Dialogs.Compare(this, doc.Text, doc.ProposedText ?? doc.Text);
            if (edited == null) return; doc.Text = edited; doc.Edited = true; doc.ProposedText = null; MarkChanged(true); SaveNow();
        }
        catch (Exception ex) { Error(ex); }
    }
    private void ClassifyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveNow(); if (Surface.Board.Analysis == null) return;
            var clone = DataJson.Clone(Surface.Board.Analysis.Ideas);
            if (!Dialogs.EditIdeas(this, clone)) return;
            Surface.Board.Analysis.Ideas = clone;
            var active = clone.Where(i => i.Status != "cancelled_or_deferred");
            var document = string.Join("\n\n", active.GroupBy(i => i.Topic).Select(g => g.Key + "\n\n" + string.Join("\n\n", g.Select(i => i.Category + " · " + i.Content))));
            if (Surface.Board.DocumentEdited) Surface.Board.ProposedDocument = document; else Surface.Board.UserDocument = document;
            Surface.Board.Analysis.ArchivedDocument = string.Join("\n\n", clone.Where(i => i.Status == "cancelled_or_deferred").Select(i => $"{i.Topic} / {i.Category}\n{i.Content}\n맥락: {i.Context}\n이유: {i.Reason}"));
            TopicDocuments.Refresh(Surface.Board); MarkChanged(true); SaveNow(); UpdatePanels();
        }
        catch (Exception ex) { Error(ex); }
    }
    private void LayoutClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveNow(); var arranged = CanvasLayout.Arrange(Surface.Board);
            var preview = new BoardSurface { IsHitTestVisible = false }; preview.Load(arranged);
            if (!Dialogs.PreviewBoard(this, preview)) return;
            Surface.ApplyCanvas(arranged); SaveNow();
        }
        catch (Exception ex) { Error(ex); }
    }
    private async Task<BoardBundle> RemoteBundle(DriveFolderService.Entry entry)
    {
        if (remoteBundles.TryGetValue(entry.Path, out var found)) return DataJson.Clone(found);
        var result = await Task.Run(() => DriveFolderService.Read(entry.Path));
        remoteBundles[entry.Path] = result; return DataJson.Clone(result);
    }
    private async Task CheckRemoteBeforeOpen(string id)
    {
        foreach (var entry in driveEntries.Where(e => e.BoardId == id && e.Device != settings.DeviceId).ToList())
        {
            var remote = await RemoteBundle(entry); var local = store.Load(id);
            if (local == null || local.AcceptedSnapshots.Contains(entry.LocalId) || SyncEngine.Equivalent(local, remote.Board) || SyncEngine.Dominates(local, remote.Board)) continue;
            await OpenRemote(entry, false);
        }
    }
    private void SaveBundle(BoardBundle bundle, string id) => BoardTransfer.Import(store, bundle, id);
    private async Task OpenRemote(DriveFolderService.Entry entry, bool open = true)
    {
        var bundle = await RemoteBundle(entry); SaveNow();
        var local = store.Load(bundle.Board.Id);
        if (local == null) { bundle.Board.AcceptedSnapshots.Add(entry.LocalId); SaveBundle(bundle, bundle.Board.Id); }
        else if (!local.AcceptedSnapshots.Contains(entry.LocalId) && !SyncEngine.Equivalent(local, bundle.Board) && !SyncEngine.Dominates(local, bundle.Board))
        {
            string? choice = Dialogs.Choose(this, "다른 기기 변경사항 확인", [
                $"이 기기 유지 · {local.ModifiedUtc.ToLocalTime():MM-dd HH:mm} (다른 기기 원본도 보관)",
                $"다른 기기 사용 · {DeviceLabel(entry.Device)} · {entry.ModifiedUtc.ToLocalTime():MM-dd HH:mm} (현재 원본 보관)",
                "현재 Canvas를 기준으로 변경사항을 빈 공간에 합치기", "나중에 확인"]);
            if (choice == null || choice == "나중에 확인") return;
            var localBackup = SyncEngine.Backup(local);
            var sourceVersions = (local.Analysis?.Ideas.Select(i => i.SourceRevision) ?? []).Append(local.Analysis?.SourceRevision ?? local.Revision).Distinct();
            SaveBundle(new(localBackup, sourceVersions.Select(v => store.LoadRevision(local.Id, v)).Where(b => b != null).Cast<Board>().ToList()), localBackup.Id);
            var backupId = Guid.NewGuid().ToString("N"); var backup = DataJson.Clone(bundle); backup.Board.Title += " · 다른 기기 원본 보관"; backup.Board.Archived = true; SaveBundle(backup, backupId);
            if (choice.StartsWith("다른 기기 사용"))
            {
                foreach (var pair in local.Clock) bundle.Board.Clock[pair.Key] = Math.Max(bundle.Board.Clock.GetValueOrDefault(pair.Key), pair.Value);
                bundle.Board.Archived = local.Archived; bundle.Board.ContentHash = ""; SyncEngine.Stamp(bundle.Board, settings.DeviceId); SaveBundle(bundle, local.Id);
            }
            else if (choice.StartsWith("현재 Canvas"))
            {
                // Import a source branch first so historical evidence has distinct local revisions.
                var original = DataJson.Clone(local); SaveBundle(bundle, local.Id);
                var merged = SyncEngine.Merge(original, bundle.Board, settings.DeviceId); store.Save(merged);
            }
            else
            {
                foreach (var pair in bundle.Board.Clock) local.Clock[pair.Key] = Math.Max(local.Clock.GetValueOrDefault(pair.Key), pair.Value);
                local.Revision = Math.Max(local.Revision, bundle.Board.Revision) + 1; local.ContentHash = ""; SyncEngine.Stamp(local, settings.DeviceId); store.Save(local);
            }
            var accepted = store.Load(local.Id)!; accepted.AcceptedSnapshots.Add(entry.LocalId); accepted.Revision++; store.Save(accepted);
        }
        if (open || Surface.Board.Id == entry.BoardId) OpenBoard(store.Load(entry.BoardId)!);
        await RefreshLibrary(); UpdateBoards();
    }
    private async void SyncClick(object sender, RoutedEventArgs e)
    {
        try { SaveNow(); await RefreshDriveBoards(); await CheckRemoteBeforeOpen(Surface.Board.Id); deferredSync.Remove(Surface.Board.Id); UpdateBoards(); }
        catch (Exception ex) { Error(ex); }
    }
    private string DeviceLabel(string id) => registeredDevices.GetValueOrDefault(id, id[..Math.Min(8, id.Length)]);
    private Dictionary<string, string> registeredDevices = [];
    private async void DevicesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshDriveBoards();
            Dialogs.DiagnosticMessage(this, "등록 기기", "현재 기기: " + settings.DeviceName + "\n\n" + string.Join("\n", registeredDevices.Select(x => x.Value + " · " + x.Key)) + "\n\n기기 이름은 설정에서 변경합니다. 기기 선택은 ‘변경사항 확인’에서 할 수 있습니다.");
        }
        catch (Exception ex) { Error(ex); }
    }
    // 목록 비교용 캐시 (v0.7.4): 로컬 Board는 저장 상태가 같으면, Drive 저장본은 경로가 같으면 다시 읽거나 해시하지 않습니다.
    // 읽기와 해시 계산은 백그라운드에서 하고, 캐시와 목록은 UI 스레드에서만 바꿉니다. 비교 결과는 이전(Equivalent·Dominates)과 같습니다.
    private readonly Dictionary<string, (string Stamp, Board Board, string Digest)> localLibrary = [];
    private readonly Dictionary<string, string> remoteDigests = [];
    private async Task RefreshLibrary()
    {
        var activePaths = driveEntries.Select(x => x.Path).ToHashSet();
        foreach (var path in remoteBundles.Keys.Where(p => !activePaths.Contains(p)).ToList()) { remoteBundles.Remove(path); remoteDigests.Remove(path); }
        var stamps = await Task.Run(store.ListStamps); var entries = driveEntries.ToList();
        foreach (var id in localLibrary.Keys.Except(stamps.Select(s => s.Id)).ToList()) localLibrary.Remove(id);
        var staleLocal = stamps.Where(s => !localLibrary.TryGetValue(s.Id, out var cached) || cached.Stamp != s.Stamp).ToList();
        var staleRemote = entries.Where(e => !remoteDigests.ContainsKey(e.Path)).Select(e => (Entry: e, Cached: remoteBundles.GetValueOrDefault(e.Path))).ToList();
        var (loaded, read) = await Task.Run(() =>
        {
            var boards = new List<(string Id, string Stamp, Board Board, string Digest)>();
            foreach (var (id, stamp) in staleLocal) if (store.Load(id) is { } board) boards.Add((id, stamp, board, SyncEngine.Digest(board)));
            var bundles = new List<(string Path, BoardBundle Bundle, string Digest)>();
            foreach (var (entry, cached) in staleRemote)
            {
                try { var bundle = cached ?? DriveFolderService.Read(entry.Path); bundles.Add((entry.Path, bundle, SyncEngine.Digest(bundle.Board))); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException) { }
            }
            return (boards, bundles);
        });
        foreach (var board in loaded) localLibrary[board.Id] = (board.Stamp, board.Board, board.Digest);
        foreach (var remote in read) if (activePaths.Contains(remote.Path)) { remoteBundles[remote.Path] = remote.Bundle; remoteDigests[remote.Path] = remote.Digest; }
        var items = stamps.Where(s => localLibrary.ContainsKey(s.Id)).Select(s => (Item: new LibraryItem(localLibrary[s.Id].Board, "이 기기", null), localLibrary[s.Id].Digest)).ToList();
        foreach (var entry in entries)
        {
            if (!remoteBundles.TryGetValue(entry.Path, out var bundle) || !remoteDigests.TryGetValue(entry.Path, out var digest)) continue;
            if (items.Any(x => x.Item.Board.Id == bundle.Board.Id && (x.Digest == digest || SyncEngine.Dominates(x.Item.Board, bundle.Board)))) continue;
            items.Add((new(bundle.Board, "Drive · " + DeviceLabel(entry.Device), entry), digest));
        }
        var result = items.Select(x => x.Item).ToList();
        library = result; UpdateBoards();
        if (initialized)
        {
            var topic = TopicFilter.SelectedItem?.ToString(); updating = true;
            TopicFilter.ItemsSource = new[] { "모든 대주제" }.Concat(SearchCandidates().Select(i => i.Idea.Topic).Distinct()); TopicFilter.SelectedItem = topic; if (TopicFilter.SelectedIndex < 0) TopicFilter.SelectedIndex = 0;
            var category = CategoryFilter.SelectedItem?.ToString();
            CategoryFilter.ItemsSource = new[] { "모든 분류" }.Concat(SearchCandidates().Where(i => TopicFilter.SelectedIndex == 0 || i.Idea.Topic == TopicFilter.SelectedItem?.ToString()).Select(i => i.Idea.Category).Distinct()); CategoryFilter.SelectedItem = category; if (CategoryFilter.SelectedIndex < 0) CategoryFilter.SelectedIndex = 0;
            updating = false; RefreshSearch();
            int pending = library.Count(x => x.Remote != null && x.Board.Id == Surface.Board.Id && !Surface.Board.AcceptedSnapshots.Contains(x.Remote.LocalId));
            SyncMenu.Header = $"다른 기기 변경 확인 ({pending})";
        }
    }
}

