using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace ThoughtCanvas;
public static class FeatureTests
{
    public static void Run(string root, Action<string, Action> test, Action<bool, string> assert)
    {
        Board Sample() => new() { Title = "동시 작업", Revision = 5, Texts = [new() { Id = "text", Text = "원본", X = 20, Y = 20 }], Analysis = new() { SourceRevision = 5, Ideas = [new() { Id = "idea", Topic = "퍼즐", Category = "규칙", Content = "빙판", Context = "함께 이동", SourceIds = ["text"], SourceRevision = 5 }] } };
        test("Sync: 기기별 Clock·동시 분기·순차 반영 판별", () =>
        {
            var a = Sample(); SyncEngine.Stamp(a, "pc"); var b = DataJson.Clone(a);
            a.Texts[0].Text = "pc 수정"; SyncEngine.Stamp(a, "pc"); b.Texts[0].Text = "노트북 수정"; SyncEngine.Stamp(b, "laptop");
            assert(!SyncEngine.Dominates(a, b) && !SyncEngine.Dominates(b, a), "동시 분기를 최신 버전으로 오인");
            var merged = SyncEngine.Merge(a, b, "pc"); assert(SyncEngine.Dominates(merged, a) && SyncEngine.Dominates(merged, b), "통합 이력 누락");
            assert(a.Texts.Count == 1 && b.Texts.Count == 1, "입력 원본 변경");
        });
        test("Sync: 변경분만 삽입·동일 요소 제외·공간 비충돌·검증 생성", () =>
        {
            var a = Sample(); var b = DataJson.Clone(a); b.Texts.Add(new() { Id = "new", Text = "협동", X = 20, Y = 20 });
            var merged = SyncEngine.Merge(a, b, "pc"); assert(merged.Texts.Count == 2, "동일 요소 중복");
            assert(!BoardSurface.TextBounds(merged.Texts[0]).IntersectsWith(BoardSurface.TextBounds(merged.Texts[1])), "통합 위치 충돌");
            assert(merged.Analysis!.Reviews.Count > 0, "통합 검증 요청 누락");
        });
        test("Sync: Revision 재배정·Viewport 변경은 내용 충돌 아님", () =>
        {
            var a = Sample(); var b = DataJson.Clone(a); b.Revision = 100; b.Analysis!.SourceRevision = 99; b.Analysis.Ideas[0].SourceRevision = 99; b.Zoom = 2;
            assert(SyncEngine.Equivalent(a, b), "기기별 Revision 번호 또는 Viewport가 내용 충돌 유발");
        });
        test("Drive Import: Local 과거 원본 보존·Remote 출처 Revision 재연결", () =>
        {
            var store = new BoardStore(Path.Combine(root, "import")); var local = Sample(); store.Save(local);
            var remote = DataJson.Clone(local); remote.Revision = 2; remote.Texts[0].Text = "노트북"; remote.Analysis!.SourceRevision = 1; remote.Analysis.Ideas[0].SourceRevision = 1;
            var source = DataJson.Clone(remote); source.Revision = 1;
            var bundle = new BoardBundle(remote, [source]); var digest = SyncEngine.Digest(remote);
            BoardTransfer.Import(store, bundle, local.Id);
            var loaded = store.Load(local.Id)!;
            assert(store.LoadRevision(local.Id, 5)!.Texts[0].Text == "원본", "기존 원본 덮어쓰기");
            assert(store.LoadRevision(local.Id, loaded.Analysis!.Ideas[0].SourceRevision)!.Texts[0].Text == "노트북", "Remote 근거 연결 실패");
            assert(SyncEngine.Digest(loaded) == digest, "Import만으로 내용 충돌 생성");
        });
        test("공동 편집: 독립 추가·삭제·양쪽 수정 보존", () =>
        {
            var basis = Sample(); var pc = DataJson.Clone(basis); pc.Texts.Add(new() { Id = "pc", Text = "A" });
            var laptop = DataJson.Clone(basis); laptop.Texts.Add(new() { Id = "laptop", Text = "B" });
            var merged = LiveMerge.Apply(pc, basis, laptop, "pc"); assert(merged.Texts.Count == 3, "독립 추가 손실");
            var deletion = DataJson.Clone(basis); deletion.Texts.Clear();
            assert(LiveMerge.Apply(pc, basis, deletion, "pc").Texts.Single().Id == "pc", "정상 삭제 또는 다른 변경 손실");
            pc.Texts[0].Text = "PC"; laptop.Texts[0].Text = "Laptop";
            var conflict = LiveMerge.Apply(pc, basis, laptop, "pc"); assert(conflict.Texts.Any(t => t.Text == "PC") && conflict.Texts.Any(t => t.Text == "Laptop") && conflict.Analysis!.Reviews.Count > 0, "동시 수정 손실");
            bool wrong = false; laptop.Id = "different"; try { LiveMerge.Apply(pc, basis, laptop, "pc"); } catch (InvalidDataException) { wrong = true; } assert(wrong, "다른 Board 혼입");
        });
        test("공동 편집: 과거 근거 별도 저장·재수신 중복 방지", () =>
        {
            var store = new BoardStore(Path.Combine(root, "live-evidence")); var board = Sample(); store.Save(board);
            var evidence = DataJson.Clone(board); evidence.Texts[0].Text = "공유 기기 과거 원본";
            long first = store.StoreEvidence(board.Id, evidence); evidence.Revision = 999; evidence.Analysis!.SourceRevision = 999;
            long second = store.StoreEvidence(board.Id, evidence);
            assert(first == second && first < 0, "동일 근거 재수신 중복");
            assert(store.Load(board.Id)!.Texts[0].Text == "원본" && store.LoadRevision(board.Id, first)!.Texts[0].Text == "공유 기기 과거 원본", "근거 수신으로 현재 Board 손상");
        });
        test("공동 편집: 암호화·위변조·잘못된 Key 차단", () =>
        {
            var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32); var data = Encoding.UTF8.GetBytes("보존할 내용"); var packet = LiveSession.Encrypt(data, key);
            assert(LiveSession.Decrypt(packet, key).SequenceEqual(data), "암호화 왕복 실패"); packet[^1] ^= 1;
            bool rejected = false; try { LiveSession.Decrypt(packet, key); } catch (System.Security.Cryptography.CryptographicException) { rejected = true; } assert(rejected, "변조 허용");
        });
        test("공동 편집: 실제 Loopback 통신·재연결 왕복", () =>
        {
            using var host = LiveSession.Host(_ => Task.FromResult(new LiveReply(Sample())), true); using var client = LiveSession.Join(host.Code);
            for (int i = 0; i < 2; i++) { var response = Task.Run(() => client.Exchange(new(null, null), CancellationToken.None)).GetAwaiter().GetResult(); assert(response.Board!.Title == "동시 작업", "네트워크 왕복 실패"); }
        });
        test("통합 검색: 다른 Board·맥락·검토 기록·분류 Filter", () =>
        {
            var a = Sample(); var b = Sample(); b.Analysis!.Ideas[0].Topic = "다른 주제"; b.Analysis.Ideas[0].Status = "cancelled_or_deferred";
            var candidates = LibrarySearch.Candidates([new(a, "PC", null), new(b, "Drive", null)]);
            assert(LibrarySearch.Keyword(candidates, "함께", null, null).Count == 2, "맥락 검색 누락");
            assert(LibrarySearch.Keyword(candidates, "함께", "다른 주제", "규칙").Count == 1, "계층 Filter 오류");
            assert(LibrarySearch.Keyword(candidates, "원본", null, null).Count == 2, "분석 전 Text 검색 누락");
        });
        test("AI 관련 검색: 명시된 출처만 허용·전체 후보 Batch 처리", () =>
        {
            var candidates = Enumerable.Range(0, 45).Select(n => new SearchHit(new(Sample(), "PC", null), new() { Content = "퍼즐 " + n }, "Keyword")).ToList();
            var handler = new Handler((_, _) => JsonSerializer.Serialize(new { candidates = new[] { new { finishReason = "STOP", content = new { parts = new[] { new { text = "[{\"index\":0,\"reason\":\"연관된 규칙\"}]" } } } } } }));
            var result = Task.Run(() => LibrarySearch.Related(candidates, "협동", "fixture", Settings.DefaultModel, CancellationToken.None, new HttpClient(handler))).GetAwaiter().GetResult();
            assert(handler.Count == 2 && result.Count == 2 && result.All(x => x.Match == "AI 관련"), "전체 Batch 또는 결과 구분 오류");
            var invalid = new Handler((_, _) => JsonSerializer.Serialize(new { candidates = new[] { new { finishReason = "STOP", content = new { parts = new[] { new { text = "[{\"index\":999,\"reason\":\"오류\"}]" } } } } } }));
            bool rejected = false; try { Task.Run(() => LibrarySearch.Related(candidates, "q", "fixture", Settings.DefaultModel, CancellationToken.None, new HttpClient(invalid))).GetAwaiter().GetResult(); } catch (InvalidDataException) { rejected = true; } assert(rejected, "없는 출처 허용");
        });
        test("주제별 문서: 직접 편집 보존·취소 내용 제외·문단 병합", () =>
        {
            var b = Sample(); b.Analysis!.Ideas.Add(new() { Topic = "퍼즐", Content = "폐기 내용", Status = "cancelled_or_deferred" }); TopicDocuments.Refresh(b);
            assert(!b.TopicDocuments[0].Text.Contains("폐기 내용"), "취소 내용 혼입"); b.TopicDocuments[0].Edited = true; b.TopicDocuments[0].Text = "직접 수정"; TopicDocuments.Refresh(b);
            assert(b.TopicDocuments[0].Text == "직접 수정" && b.TopicDocuments[0].ProposedText != null, "사용자 문서 덮어쓰기");
            assert(TopicDocuments.MergeParagraphs("", ["A", "B"], false) == "A\n\nB", "문단 병합 오류");
        });
        test("Canvas 정돈: 공유 출처 중복 이동 방지·Undo", () =>
        {
            var b = Sample(); b.Texts.Add(new() { Id = "other", Text = "다른 내용", X = 20, Y = 20 });
            b.Analysis!.Ideas.Add(new() { Topic = "다른 주제", SourceIds = ["other"] });
            var arranged = CanvasLayout.Arrange(b); assert(!BoardSurface.TextBounds(arranged.Texts[0]).IntersectsWith(BoardSurface.TextBounds(arranged.Texts[1])), "주제 간 충돌");
            var surface = new BoardSurface(); surface.Load(b); surface.ApplyCanvas(arranged); surface.Undo(); assert(surface.Board.Texts[1].Y == 20, "정돈 Undo 실패");
        });
        test("Markdown 노트: 제목·목록·강조 표시 및 HTML 비실행", () =>
        {
            const string note = "## 육성\n- **기억의 조각**: 주옵 1개 + 부옵 4개\n  - 획득: 던전\n1. **상점**: 일부 장비 획득 불가\n<script>alert(1)</script>";
            var lines = NoteMarkdown.Parse(note).ToList(); assert(lines[0].Heading == 2 && lines[2].Indent == 2, "제목 또는 하위 항목 구조 유실");
            string html = NoteMarkdown.Html(note); assert(html.Contains("<strong>기억의 조각</strong>") && html.Contains("<ol start='1'") && !html.Contains("<script>") && html.Contains("&lt;script&gt;"), "강조·번호 또는 HTML escaping 오류");
            var doc = new System.Windows.Documents.FlowDocument(); NoteMarkdown.Append(doc, note);
            assert(doc.Blocks.Count == 5 && !new System.Windows.Documents.TextRange(doc.ContentStart, doc.ContentEnd).Text.Contains("**"), "읽기 보기에 Markdown 기호 잔존");
        });
        test("PDF: 한국어 문서·Canvas 구역 파일 생성", () =>
        {
            var path = Path.Combine(root, "pdf"); Directory.CreateDirectory(path); var board = Sample(); board.UserDocument = "## 캐릭터 육성 체계\n- **기억의 조각**: 주옵 1개 + 부옵 4개\n  - 획득: 에테르 소모 던전\n- **세이브데이터**: 장비 + 카드\n  - 카드: 복제 · 제거 · 변환(번뜩임)\n\n### 장비 획득처\n1. **상점**: 일부 핵심 장비 획득 불가\n2. **이벤트**: 핵심 장비의 주요 획득처";
            PdfExporter.Export(path, board, ExportService.RenderRegions(board));
            foreach (var file in new[] { "문서.pdf", "Canvas-구역.pdf" }) { var data = File.ReadAllBytes(Path.Combine(path, file)); assert(data.Length > 1000 && Encoding.ASCII.GetString(data).Contains("%%EOF"), "PDF 생성 실패"); }
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(root)!, "latest-pdf-path.txt"), path);
        });
        test("UI: 좁은 Window·문서 Panel 초기화 및 화면 렌더링", () =>
        {
            string? previous = Environment.GetEnvironmentVariable("THOUGHTCANVAS_DATA");
            string folder = Path.Combine(root, "ui"); var store = new BoardStore(folder);
            store.SaveSettings(new() { DriveSyncFolder = root, AutomaticAi = false, AutomaticFolderBackup = false });
            Environment.SetEnvironmentVariable("THOUGHTCANVAS_DATA", folder);
            try
            {
                var window = new MainWindow(); var content = (FrameworkElement)window.Content;
                content.Measure(new Size(1060, 760)); content.Arrange(new Rect(0, 0, 1060, 760)); content.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1060, 760, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); bitmap.Render(content);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                string path = Path.Combine(Path.GetDirectoryName(root)!, "ui-preview.png"); using (var file = File.Create(path)) encoder.Save(file);
                assert(content.ActualWidth == 1060, "최소 화면 배치 실패"); window.Close();
            }
            finally { Environment.SetEnvironmentVariable("THOUGHTCANVAS_DATA", previous); }
        });
        test("UI: 세로 화면 툴바 오른쪽 배치·가로 복원", () =>
        {
            string? previous = Environment.GetEnvironmentVariable("THOUGHTCANVAS_DATA");
            string folder = Path.Combine(root, "ui-vertical"); var store = new BoardStore(folder);
            store.SaveSettings(new() { DriveSyncFolder = root, AutomaticAi = false, AutomaticFolderBackup = false });
            Environment.SetEnvironmentVariable("THOUGHTCANVAS_DATA", folder);
            try
            {
                var window = new MainWindow(); var content = (FrameworkElement)window.Content;
                void Layout(double w, double h) { content.Measure(new Size(w, h)); content.Arrange(new Rect(0, 0, w, h)); content.UpdateLayout(); }
                window.ApplyToolbarLayout(true); Layout(720, 1200);
                assert(ReferenceEquals(window.ToolsBar.Parent, window.ContentGrid) && System.Windows.Controls.Grid.GetColumn(window.ToolsBar) == 1, "세로 툴바가 Canvas 오른쪽에 없음");
                assert(window.ToolsBar.ActualWidth > 60 && window.ToolsBar.ActualWidth < 260 && window.CanvasHost.ActualWidth > 400, "세로 툴바 폭 이상: " + window.ToolsBar.ActualWidth);
                assert(window.SizeSlider.Orientation == System.Windows.Controls.Orientation.Vertical, "굵기 슬라이더 세로 전환 실패");
                window.ApplyToolbarLayout(false); Layout(1060, 760);
                assert(ReferenceEquals(window.ToolsBar.Parent, window.RootGrid) && System.Windows.Controls.Grid.GetRow(window.ToolsBar) == 1 && window.CanvasHost.ActualWidth == 1060, "가로 툴바 복원 실패");
                window.Close();
            }
            finally { Environment.SetEnvironmentVariable("THOUGHTCANVAS_DATA", previous); }
        });
        test("Export: 별도 렌더 스레드 구역 PNG·Export 파일 동일", () =>
        {
            var board = new Board { Title = "렌더 스레드", Ink = [new InkItem { Points = Enumerable.Range(0, 60).Select(i => new Sample(i * 9, 40 + i % 7, .6f)).ToList() }],
                Texts = [new TextItem { X = 30, Y = 120, Text = "가나다 Text", FontSize = 22, Width = 160 }] };
            var onUi = ExportService.RenderRegions(board);
            var offUi = MainWindow.OnRenderThread(() => ExportService.RenderRegions(board)).GetAwaiter().GetResult();
            assert(onUi.Count == offUi.Count && onUi.Zip(offUi).All(p => p.First.Name == p.Second.Name && p.First.Png.SequenceEqual(p.Second.Png)), "렌더 스레드 PNG 불일치");
            string exports = Path.Combine(root, "render-thread");
            string a = ExportService.Export(board, Path.Combine(exports, "ui"), onUi), b = MainWindow.OnRenderThread(() => ExportService.Export(board, Path.Combine(exports, "thread"), offUi)).GetAwaiter().GetResult();
            string[] Files(string d) => Directory.GetFiles(d, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(d, f)).Order().ToArray();
            assert(Files(a).SequenceEqual(Files(b)) && Files(a).All(f => File.ReadAllBytes(Path.Combine(a, f)).SequenceEqual(File.ReadAllBytes(Path.Combine(b, f)))), "렌더 스레드 Export 파일 불일치");
        });
        test("Notion: 긴 문서·Unicode 경계·80개 Batch·구조 보존", () =>
        {
            var b = Sample(); b.UserDocument = string.Concat(Enumerable.Repeat("한글🧊", 1000));
            var blocks = NotionExporter.Blocks(b); assert(blocks.Count > 3, "긴 문서 분할 누락");
            b.UserDocument = string.Join("\n\n", Enumerable.Range(0, 90).Select(n => "문단 " + n));
            var handler = new Handler((_, _) => "{\"id\":\"11111111-1111-1111-1111-111111111111\",\"url\":\"https://www.notion.so/test\"}");
            string url = Task.Run(() => NotionExporter.Export(b, "fixture", Guid.NewGuid().ToString(), CancellationToken.None, new HttpClient(handler))).GetAwaiter().GetResult();
            assert(handler.Count == 2 && url.EndsWith("test"), "Notion Batch 전송 실패");
        });
        test("보관 Board: 통합 전·다른 기기 원본은 숨김 표시, 예전 저장본도 표시, 보관 해제 유지", () =>
        {
            var dir = Path.Combine(root, "archive"); var store = new BoardStore(dir);
            var main = Sample(); store.Save(main);
            var backup = SyncEngine.Backup(main); store.Save(backup);
            assert(backup.Archived && !main.Archived && backup.Id != main.Id, "통합 전 보관본에 보관 표시 누락");
            // v0.7.7 이전 저장본: Archived 값이 없는 행
            var legacy = new Board { Title = "에픽세븐BM" + BoardStore.RemoteBackupSuffix }; var kept = new Board { Title = "보관 해제한 BM" + BoardStore.RemoteBackupSuffix };
            store.Save(legacy); store.Save(kept);
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(dir, "boards.sqlite")))
            {
                c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "UPDATE boards SET json=json_remove(json,'$.Archived') WHERE id=$id"; cmd.Parameters.AddWithValue("$id", legacy.Id); cmd.ExecuteNonQuery();
            }
            var reopened = new BoardStore(dir); var list = reopened.ListEntries();
            assert(list.Single(x => x.Id == legacy.Id).Archived && reopened.Load(legacy.Id)!.Archived, "예전 보관본이 목록에 남음");
            assert(!list.Single(x => x.Id == kept.Id).Archived, "보관 해제(Archived=false)한 Board를 다시 숨김");
            assert(!list.Single(x => x.Id == main.Id).Archived && list.Single(x => x.Id == backup.Id).Archived, "일반 Board 숨김 또는 보관본 표시");
            assert(SyncEngine.Digest(reopened.Load(legacy.Id)!) == SyncEngine.Digest(legacy), "보관 표시가 내용 변경(동기화 충돌)으로 처리됨");
        });
        test("Board 삭제: 원본·복원 지점 삭제, 수정본 보관 이동 시 기록 유지", () =>
        {
            var store = new BoardStore(Path.Combine(root, "delete")); var a = Sample(); store.Save(a); a.Revision = 6; store.Save(a);
            var other = Sample(); other.Id = Guid.NewGuid().ToString("N"); store.Save(other);
            store.Delete(a.Id);
            assert(store.Load(a.Id) == null && store.LoadRevision(a.Id, 5) == null && store.LoadRevision(a.Id, 6) == null, "삭제 후 원본 또는 복원 지점이 남음");
            assert(store.Load(other.Id) != null, "다른 Board까지 삭제됨");
            string moved = Guid.NewGuid().ToString("N"); store.MoveToArchive(other.Id, moved, "수정본");
            var m = store.Load(moved)!;
            assert(store.Load(other.Id) == null && m.Id == moved && m.Archived && m.Title == "수정본" && store.LoadRevision(moved, other.Revision)!.Id == moved, "보관 이동 시 기록 손실");
        });
        test("Drive 삭제 기록: 저장본 제거·목록 제외·보관본 표시", () =>
        {
            var sync = Path.Combine(root, "tombstone"); Directory.CreateDirectory(sync);
            var b = Sample(); var keep = new Board { Title = "남길 Board" }; var legacy = new Board { Title = "옛 보관" + BoardStore.MergeBackupMarker + "11-23 19:18:39" };
            string Export(Board x) => ExportService.Export(x, Path.Combine(root, Guid.NewGuid().ToString("N")), []);
            DriveFolderService.Publish(sync, "pc", b, Export(b), CancellationToken.None); DriveFolderService.Publish(sync, "laptop", b, Export(b), CancellationToken.None);
            DriveFolderService.Publish(sync, "pc", keep, Export(keep), CancellationToken.None); DriveFolderService.Publish(sync, "pc", legacy, Export(legacy), CancellationToken.None);
            assert(DriveFolderService.Discover(sync, CancellationToken.None).Entries.Single(e => e.BoardId == legacy.Id).Archived, "예전 보관본이 Drive 목록에서 일반 Board로 보임");
            var at = DateTime.UtcNow; DriveFolderService.MarkDeleted(sync, new(b.Id, b.Title, at, "pc"));
            var boards = Path.Combine(sync, "ThoughtCanvas", "Boards");
            assert(!Directory.EnumerateDirectories(boards).Any(d => d.EndsWith("_" + b.Id)), "삭제한 Board 저장본이 남음");
            // 삭제를 모르는 다른 기기가 다시 올려도 목록에 나오지 않아야 합니다.
            DriveFolderService.Publish(sync, "laptop", b, Export(b), CancellationToken.None);
            var catalog = DriveFolderService.Discover(sync, CancellationToken.None);
            assert(catalog.Entries.All(e => e.BoardId != b.Id) && catalog.Entries.Any(e => e.BoardId == keep.Id), "삭제한 Board가 Drive 목록에서 되살아남");
            assert(catalog.Deleted != null && Math.Abs((catalog.Deleted[b.Id] - at).TotalSeconds) < 1, "삭제 기록 전달 실패");
        });
    }
    private sealed class Handler(Func<HttpRequestMessage, string, string> responder) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Count++; string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(token); return new(HttpStatusCode.OK) { Content = new StringContent(responder(request, body)) }; }
    }
}
