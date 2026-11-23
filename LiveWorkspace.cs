using System.Windows;
namespace ThoughtCanvas;
public partial class MainWindow
{
    private LiveSession? liveSession;
    private bool liveHost, liveBusy;
    private string liveBoardId = "";
    private Board? liveBasis, liveSent, livePending;
    private List<Board>? livePendingSources;
    private List<Board> LiveSources(Board board) => (board.Analysis?.Ideas.Select(i => i.SourceRevision) ?? []).Append(board.Analysis?.SourceRevision ?? board.Revision).Distinct().Select(v => store.LoadRevision(board.Id, v)).Where(x => x != null).Cast<Board>().ToList();
    private void ImportLiveEvidence(Board board, List<Board>? sources)
    {
        var map = (sources ?? []).GroupBy(b => b.Revision).ToDictionary(g => g.Key, g => store.StoreEvidence(board.Id, g.First()));
        if (board.Analysis != null)
        {
            board.Analysis.SourceRevision = map.GetValueOrDefault(board.Analysis.SourceRevision, board.Analysis.SourceRevision);
            foreach (var idea in board.Analysis.Ideas) idea.SourceRevision = map.GetValueOrDefault(idea.SourceRevision, idea.SourceRevision);
        }
    }
    private DateTime lastLive = DateTime.MinValue;
    private async void LiveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string? choice = Dialogs.Choose(this, "PC·노트북 공동 편집 · 같은 Network", ["현재 Board 공유 시작", "다른 기기 Code로 참여", "연결 Code 보기", "공동 편집 종료"]);
            if (choice == null) return;
            if (choice == "공동 편집 종료") { StopLive(); return; }
            if (choice == "연결 Code 보기") { if (liveSession != null) Dialogs.DiagnosticMessage(this, "연결 Code", liveSession.Code); return; }
            SaveNow(); StopLive();
            if (choice == "현재 Board 공유 시작")
            {
                liveHost = true; liveBoardId = Surface.Board.Id;
                liveSession = LiveSession.Host(request => Dispatcher.InvokeAsync(() =>
                {
                    if (Surface.Board.Id != liveBoardId) return new LiveReply(null, "공유한 Board를 열어 주세요.");
                    if (Surface.IsEditing || saving || openingDrive || aiBusy) return new LiveReply(null, "busy");
                    SaveNow();
                    if (request.Basis != null && request.Edited != null)
                    {
                        ImportLiveEvidence(request.Edited, request.Sources);
                        var merged = LiveMerge.Apply(Surface.Board, request.Basis, request.Edited, settings.DeviceId);
                        if (!SyncEngine.Equivalent(merged, Surface.Board)) { store.Save(merged); OpenBoard(merged); }
                    }
                    return new LiveReply(DataJson.Clone(Surface.Board), Sources: LiveSources(Surface.Board));
                }).Task);
                Dialogs.DiagnosticMessage(this, "다른 기기에서 Code로 참여", liveSession.Code + "\n\n같은 Network의 Windows 앱에서 공동 편집 → Code로 참여를 선택하세요. Windows가 묻는 경우 Private Network 연결을 허용하세요. 이 창을 닫으면 공유가 계속됩니다.");
            }
            else
            {
                var code = Dialogs.Ask(this, "공동 편집 참여", "다른 PC·노트북의 연결 Code", ""); if (code == null) return;
                liveSession = LiveSession.Join(code);
                var reply = await liveSession.Exchange(new(null, null), lifetime.Token);
                if (reply.Board == null) throw new InvalidOperationException("공유 기기가 작업 중입니다. 잠시 후 다시 참여하세요.");
                SaveNow();
                var old = store.Load(reply.Board.Id); if (old != null) store.Save(SyncEngine.Backup(old));
                var initial = DataJson.Clone(reply.Board); ImportLiveEvidence(initial, reply.Sources); initial.Revision = Math.Max(initial.Revision, (old?.Revision ?? 0) + 1);
                store.Save(initial); OpenBoard(initial); liveBasis = DataJson.Clone(reply.Board); liveBoardId = initial.Id;
                SetStatus("공동 편집 연결됨 · 완료된 Stroke와 문서를 주고받습니다.");
            }
        }
        catch (Exception ex) { StopLive(); Error(ex); }
    }
    private void StopLive()
    {
        liveSession?.Dispose(); liveSession = null; liveHost = false; liveBasis = livePending = liveSent = null; liveBoardId = ""; SetStatus("공동 편집 연결 종료 · 작업은 Local에 유지됩니다.");
    }
    private async Task LiveTick()
    {
        if (liveSession == null || liveHost || liveBusy || Surface.IsEditing || saving || openingDrive || aiBusy || (DateTime.UtcNow - lastLive).TotalMilliseconds < 700) return;
        if (Surface.Board.Id != liveBoardId) { StopLive(); return; }
        liveBusy = true; lastLive = DateTime.UtcNow;
        var session = liveSession;
        try
        {
            if (livePending != null && liveSent != null)
            {
                SaveNow(); ImportLiveEvidence(livePending, livePendingSources); var merged = LiveMerge.Apply(livePending, liveSent, Surface.Board, settings.DeviceId);
                liveBasis = livePending; livePending = null;
                if (!SyncEngine.Equivalent(merged, Surface.Board)) { merged.Revision = Math.Max(merged.Revision, Surface.Board.Revision + 1); store.Save(merged); OpenBoard(merged); }
            }
            SaveNow(); liveSent = DataJson.Clone(Surface.Board);
            var reply = await session.Exchange(new(liveBasis, liveSent, LiveSources(liveSent)), lifetime.Token);
            if (!ReferenceEquals(session, liveSession)) return;
            if (reply.Board != null) { livePending = reply.Board; livePendingSources = reply.Sources; }
            else if (reply.Error != "busy") SetStatus("공동 편집 대기: " + reply.Error);
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.Net.Sockets.SocketException or OperationCanceledException or System.Security.Cryptography.CryptographicException)
        { SetStatus("공동 편집 재연결 대기 · Local 변경은 유지됩니다."); lastLive = DateTime.UtcNow.AddSeconds(4); }
        finally { liveBusy = false; }
    }
}
