using System.Text.Json;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;

namespace ThoughtCanvas;
public static class SelfTests
{
    public static int Run(string report)
    {
        var results = new List<string>(); int failed = 0;
        string root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!, "test-data", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        void Test(string name, Action test)
        {
            try { test(); results.Add("PASS " + name); }
            catch (Exception ex) { failed++; results.Add("FAIL " + name + "\n" + ex); }
        }
        void Assert(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }
        InkItem Line() => new() { Points = Enumerable.Range(0, 101).Select(i => new Sample(i * 2, 50)).ToList(), Width = 4 };
        Test("부분 지우기: 중심만 지우고 양쪽 Stroke 보존", () =>
        {
            var original = Line(); var stroke = original.ToStroke(); var fragments = stroke.GetEraseResult(new[] { new Point(100, 30), new Point(100, 70) }, new EllipseStylusShape(20, 20));
            Assert(fragments.Count == 2, "중앙 지우기는 두 조각을 남겨야 합니다.");
            Assert(fragments[0].GetBounds().Right < 100 && fragments[1].GetBounds().Left > 100, "지운 부분이 남아 있습니다.");
            var converted = fragments.Select(s => InkItem.FromStroke(s, original.Id)).ToArray();
            Assert(converted.All(s => s.ParentId == original.Id) && converted[0].Id != converted[1].Id, "출처 계보 오류");
        });
        Test("Lasso 완전 포함: 내부·경계 횡단·굵기 밖 제외", () =>
        {
            var lasso = BoardSurface.Polygon(new[] { new Point(0, 0), new Point(100, 0), new Point(100, 100), new Point(0, 100) });
            Assert(BoardSurface.FullyInside(lasso, new RectangleGeometry(new Rect(10, 10, 20, 20))), "내부 요소 선택 실패");
            Assert(!BoardSurface.FullyInside(lasso, new RectangleGeometry(new Rect(90, 10, 20, 20))), "일부 포함 요소 선택됨");
            var fat = new InkItem { Width = 20, Points = [new(5, 20), new(5, 80)] }.ToStroke();
            Assert(!BoardSurface.FullyInside(lasso, fat.GetGeometry()), "Stroke 중심만 검사하여 굵기 밖 요소 선택됨");
        });
        Test("Undo 50회 제한 및 Redo 새 편집 시 무효화", () =>
        {
            var b = new Board(); var history = new CanvasHistory();
            for (int i = 0; i < 60; i++) { history.Push(CanvasHistory.Capture(b)); b.Texts.Add(new() { Text = i.ToString() }); }
            Assert(history.UndoCount == 50, "50회 제한 실패"); for (int i = 0; i < 50; i++) Assert(history.Undo(b), "Undo 실패");
            Assert(b.Texts.Count == 10 && !history.Undo(b), "이력 범위 실패"); Assert(history.Redo(b) && b.Texts.Count == 11, "Redo 실패");
            history.Push(CanvasHistory.Capture(b)); b.Texts.Add(new() { Text = "새 작업" }); Assert(!history.Redo(b), "새 작업 후 Redo가 남음");
        });
        Test("SQLite 왕복: 원본·압력·맥락·사용자 수정·대기 상태", () =>
        {
            var s = new BoardStore(root); var b = new Board { Title = "덱빌딩 분석", Revision = 7, Ink = [Line()], SavesSinceAi = 12, UserDocument = "사용자 수정", DocumentEdited = true,
                Analysis = new() { Ideas = [new() { Id = "idea1", Content = "추가 Draw", Context = "선택지 부족", Status = "cancelled_or_deferred", Reason = "무한 반복 우려" }] } };
            s.Save(b); var read = s.Load(b.Id)!; Assert(DataJson.Write(b) == DataJson.Write(read), "저장 후 내용 손실");
            var stale = DataJson.Clone(b); stale.Revision = 6; stale.Title = "오래된 저장"; s.Save(stale); Assert(s.Load(b.Id)!.Title == b.Title, "지연된 저장이 최신 내용을 덮어씀");
        });
        Test("AI 갱신: 사용자 문서 보존, 별도 제안과 구조화 기록", () =>
        {
            var b = new Board { UserDocument = "공격 카드만 Draw", DocumentEdited = true };
            AiService.Apply(b, new() { SourceRevision = 10, Document = "Turn당 1회 Draw", Ideas = [new() { Context = "제약 조건" }] });
            Assert(b.UserDocument == "공격 카드만 Draw" && b.ProposedDocument == "Turn당 1회 Draw" && b.Analysis!.Ideas.Count == 1, "사용자 문서가 덮어써짐");
        });
        Test("AI 출처 검증: 임의 ID 거부, 누락 요소 검증 요청", () =>
        {
            var b = new Board { Revision = 9, Ink = [Line()] }; string Wrap(AnalysisResult a) => WrapResponse(a);
            bool rejected = false; try { AiService.Parse(Wrap(new() { Ideas = [new() { Id = "i", SourceIds = ["invented"] }] }), b); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "조작된 원본 ID 허용"); var result = AiService.Parse(Wrap(new() { Document = "초안" }), b); Assert(result.Reviews.Single().SourceIds.Contains(b.Ink[0].Id), "누락된 요소가 숨겨짐");
            rejected = false; try { AiService.Parse("{\"candidates\":[{\"finishReason\":\"MAX_TOKENS\"}]}", b); } catch (InvalidDataException) { rejected = true; } Assert(rejected, "불완전 응답 허용");
        });
        Test("DPAPI Local credential 왕복", () => { var encrypted = Secrets.Protect("test-only-not-a-real-key"); Assert(encrypted != "test-only-not-a-real-key" && Secrets.Unprotect(encrypted) == "test-only-not-a-real-key", "암호화 실패"); });
        Test("음수 좌표 Canvas·Viewport 역변환", () =>
        {
            var surface = new BoardSurface(); surface.Load(new Board { ViewX = -200, ViewY = 330, Zoom = .3 }); var point = new Point(-20000, 54321); var round = surface.ToWorld(surface.ToScreen(point)); Assert((round - point).Length < .0001, "좌표 변환 불일치");
        });
        Test("구역 Export: 원본 ID·PNG·HTML escaping", () =>
        {
            var b = new Board { Title = "Export <검증>", Ink = [Line()], Texts = [new() { X = -200, Y = -100, Text = "한글 <script>alert(1)</script>" }] };
            var regions = ExportService.RenderRegions(b); Assert(regions.Count >= 2 && regions.SelectMany(r => r.SourceIds).Contains(b.Ink[0].Id), "구역 원본 누락");
            var export = ExportService.Export(b, Path.Combine(root, "exports"), regions); Assert(File.Exists(Path.Combine(export, "AppData", "원본", "board.json")), "편집 원본 누락");
            Assert(ExportService.Html("title", "<script>", b).Contains("&lt;script&gt;"), "HTML escaping 실패");
        });
        Test("Canvas 원본 Snapshot 독립성", () =>
        {
            // 이력은 얕게 복사하므로, 크기 변경이 이력 속 원래 객체를 바꾸지 않는지 확인합니다.
            var line = Line(); var note = new TextItem { X = 10, Y = 10, Text = "원본", FontSize = 20, Width = 60 };
            var surface = new BoardSurface(); surface.Load(new Board { Ink = [line], Texts = [note] });
            string X(BoardSurface s) => $"{s.Board.Ink[0].Points[100].X:F1},{s.Board.Texts[0].X:F1},{s.Board.Texts[0].FontSize:F1}";
            string start = X(surface); surface.Selection.Add(line.Id); surface.Selection.Add(note.Id);
            surface.ResizeSelection(2); string once = X(surface); surface.Selection.Add(line.Id); surface.Selection.Add(note.Id); surface.ResizeSelection(2);
            Assert(line.Points[100].X == 200 && note.FontSize == 20, "이력과 원본이 같은 객체를 참조함");
            surface.Undo(); Assert(X(surface) == once, "1단계 Undo 불일치"); surface.Undo(); Assert(X(surface) == start, "원본 복원 실패");
            surface.Redo(); surface.Redo(); surface.Undo(); Assert(X(surface) == once, "Redo 후 Undo 불일치");
            var snapshot = CanvasHistory.Capture(surface.Board); Assert(ReferenceEquals(snapshot.Ink[0], surface.Board.Ink[0]) && snapshot.Ink != surface.Board.Ink, "이력 목록 얕은 복사 실패");
        });
        Test("이전 취소·보류 아이디어와 이유 재분석 시 보존", () =>
        {
            var b = new Board { Analysis = new() { Ideas = [new() { Id = "archive", Status = "cancelled_or_deferred", Content = "무한 Draw", Context = "선택지 확대", Reason = "Turn 종료 불가", SourceIds = ["old-stroke"], SourceRevision = 2 }] } };
            AiService.Apply(b, new() { Document = "현재 생각", SourceRevision = 10 });
            Assert(b.Analysis!.Ideas.Single().Reason == "Turn 종료 불가" && b.Analysis.ArchivedDocument.Contains("선택지 확대"), "이전 검토 맥락 유실");
        });
        Test("원본 Version 불변 보관 및 조회", () =>
        {
            var s = new BoardStore(root); var b = new Board { Revision = 1, Ink = [Line()] }; s.Save(b); b.Ink.Clear(); b.Revision = 2; s.Save(b);
            Assert(s.LoadRevision(b.Id, 1)!.Ink.Count == 1 && s.Load(b.Id)!.Ink.Count == 0, "삭제 전 원본 추적 실패");
        });
        Test("Gemini HTTP 계약: Google endpoint·Key header·Image·LOW·JSON Schema", () =>
        {
            var b = new Board { Revision = 5, Ink = [Line()] };
            var handler = new FixtureHandler(b.Ink[0].Id);
            var service = new AiService(new System.Net.Http.HttpClient(handler));
            var regions = ExportService.RenderRegions(b);
            var result = Task.Run(() => service.Analyze(b, regions, "fixture-key", Settings.DefaultModel, CancellationToken.None)).GetAwaiter().GetResult();
            using var request = JsonDocument.Parse(handler.RequestBody!); var json = request.RootElement;
            Assert(handler.Url == "https://generativelanguage.googleapis.com/v1beta/models/gemini-3.8-flash:generateContent", "Google endpoint 오류");
            Assert(handler.Key == "fixture-key" && !handler.HasAuthorization && !handler.Url!.Contains("fixture-key"), "Key header 오류");
            var config = json.GetProperty("generationConfig");
            Assert(config.GetProperty("thinkingConfig").GetProperty("thinkingLevel").GetString() == "LOW", "Thinking 설정 오류");
            Assert(config.GetProperty("responseMimeType").GetString() == "application/json" && config.GetProperty("responseJsonSchema").GetProperty("required").GetArrayLength() == 4, "Schema 누락");
            var parts = json.GetProperty("contents")[0].GetProperty("parts");
            Assert(parts.EnumerateArray().Count(p => p.TryGetProperty("inlineData", out _)) == regions.Count, "Image 누락");
            var image = parts.EnumerateArray().First(p => p.TryGetProperty("inlineData", out _)).GetProperty("inlineData");
            Assert(image.GetProperty("mimeType").GetString() == "image/png" && Convert.FromBase64String(image.GetProperty("data").GetString()!).SequenceEqual(regions[0].Png), "PNG 변형");
            Assert(result.Ideas.Single().SourceIds.Single() == b.Ink[0].Id && json.TryGetProperty("systemInstruction", out _), "맥락 또는 원본 연결 누락");
        });
        Test("Gemini 오류: 차단·빈 응답·부분 JSON·잘못된 분류 거부, thought 제외", () =>
        {
            var b = new Board();
            foreach (var payload in new[] { "{}", "{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}", "{\"candidates\":[{\"finishReason\":\"STOP\",\"content\":{\"parts\":[]}}]}", WrapResponse(new(), raw: "{}"), WrapResponse(new() { Ideas = [new() { Status = "invented" }] }) })
            {
                bool rejected = false; try { AiService.Parse(payload, b); } catch (InvalidDataException) { rejected = true; }
                Assert(rejected, "불완전 결과를 허용했습니다.");
            }
            Assert(AiService.Parse(WrapResponse(new() { Document = "검증 완료" }, includeThought: true), b).Document == "검증 완료", "thought가 문서에 섞임");
        });
        Test("Gemini 429·403 오류 안내와 Key 비노출", () =>
        {
            var b = new Board { Ink = [Line()] }; var regions = ExportService.RenderRegions(b);
            foreach (var status in new[] { System.Net.HttpStatusCode.TooManyRequests, System.Net.HttpStatusCode.Forbidden })
            {
                var handler = new FixtureHandler(b.Ink[0].Id) { Status = status };
                var service = new AiService(new System.Net.Http.HttpClient(handler));
                bool rejected = false;
                try { Task.Run(() => service.Analyze(b, regions, "fixture-key", Settings.DefaultModel, CancellationToken.None)).GetAwaiter().GetResult(); }
                catch (InvalidOperationException ex) { rejected = true; Assert(!ex.Message.Contains("fixture-key") && ex.Message.Contains(((int)status).ToString()), "오류 처리 중 Key 노출 또는 상태 유실"); }
                Assert(rejected, "HTTP 오류를 성공으로 처리함");
            }
        });
        Test("기존 설정 전환: OpenAI Key 격리·자동 AI 중지·Drive 설정 보존", () =>
        {
            var store = new BoardStore(Path.Combine(root, "migration"));
            store.SaveSettings(new() { Model = "gpt-6-astra", ProtectedApiKey = "legacy-encrypted-key", AutomaticAi = true, GoogleClientId = "drive-client" });
            var settings = store.LoadSettings();
            Assert(settings.Model == Settings.DefaultModel && settings.ProtectedGeminiApiKey == "" && !settings.AutomaticAi && settings.GoogleClientId == "drive-client", "설정 전환 오류");
            settings.ProtectedGeminiApiKey = Secrets.Protect("gemini-fixture"); settings.AutomaticAi = true; store.SaveSettings(settings);
            var reloaded = store.LoadSettings();
            Assert(Secrets.Unprotect(reloaded.ProtectedGeminiApiKey) == "gemini-fixture" && reloaded.AutomaticAi, "재실행 시 Gemini 설정 초기화");
        });
        Test("환경 변수 Key: 우선순위·시스템 범위·앱 Key fallback·값 비저장", () =>
        {
            var settings = new Settings { ProtectedGeminiApiKey = Secrets.Protect("saved-key") };
            var before = DataJson.Write(settings);
            string? Read(string name, EnvironmentVariableTarget scope) => (name, scope) switch
            {
                ("GEMINI_API_KEY", EnvironmentVariableTarget.Machine) => "  machine-key  ",
                ("GEMINI_API_KEY", EnvironmentVariableTarget.Process) => "stale-key",
                ("GOOGLE_API_KEY", EnvironmentVariableTarget.User) => "google-key",
                _ => null
            };
            var selected = GeminiCredentials.Resolve(settings, Read);
            Assert(selected.Key == "machine-key" && selected.Source == "GEMINI_API_KEY (Machine)", "환경 변수 우선순위 오류");
            Assert(GeminiCredentials.Resolve(settings, (_, _) => null).Key == "saved-key", "저장 Key fallback 오류");
            Assert(GeminiCredentials.Resolve(new(), (_, _) => " ").Key == "", "빈 Key 처리 오류");
            Assert(GeminiCredentials.Resolve(new(), (n, _) => n == "GOOGLE_API_KEY" ? "google-only" : null).Key == "google-only", "Google 변수 지원 실패");
            Assert(before == DataJson.Write(settings) && !selected.Source.Contains("machine-key"), "Key 저장 또는 표시 노출");
        });
        Test("Model 설정: 변경 유지·부적절한 ID 차단", () =>
        {
            var settings = new Settings { Model = "gemini-3.5-flash-lite" };
            Assert(!settings.MigrateAiProvider() && DataJson.Read<Settings>(DataJson.Write(settings)).Model == settings.Model, "Model 설정 보존 실패");
            Assert(Settings.IsSupportedModel(Settings.DefaultModel) && !Settings.IsSupportedModel("gemini-3/../../other") && !Settings.IsSupportedModel("gpt-6-astra"), "Model 검증 실패");
        });
        Test("Drive 폴더 저장: 원본·구역·문서·과거 맥락 복원 및 기기 분리", () =>
        {
            var sync = Path.Combine(root, "sync"); Directory.CreateDirectory(sync);
            var b = new Board { Title = "폴더 검증", Revision = 8, Ink = [Line()], UserDocument = "직접 수정",
                Analysis = new() { ArchivedDocument = "보류 맥락", Ideas = [new() { Content = "검토 내용", SourceRevision = 2 }] } };
            var previous = new Board { Id = b.Id, Revision = 2, Ink = [Line()] };
            var export = ExportService.Export(b, Path.Combine(root, "folder-export"), ExportService.RenderRegions(b), [previous]);
            var one = DriveFolderService.Publish(sync, "device-a", b, export, CancellationToken.None);
            var two = DriveFolderService.Publish(sync, "device-b", b, export, CancellationToken.None);
            Assert(one != two && File.Exists(Path.Combine(one, "문서", "정리.html")) && Directory.GetFiles(Path.Combine(one, "구역별 보기"), "*.png").Length > 0, "폴더 분리 또는 Export 손실");
            var restored = DriveFolderService.Read(Path.Combine(one, "snapshot.zip"));
            Assert(DataJson.Write(restored.Board) == DataJson.Write(b) && restored.Sources.Single().Revision == 2, "원본 또는 과거 출처 복원 실패");
            Assert(!Directory.GetFiles(one, "settings.json", SearchOption.AllDirectories).Any(), "설정 파일 포함");
        });
        Test("Drive 목록: 최신 기기별 저장본·미완성 제외·복사본 ID 안정성", () =>
        {
            var sync = Path.Combine(root, "catalog"); Directory.CreateDirectory(sync);
            var b = new Board { Title = "노트북 문서", Revision = 2, UserDocument = "노트북 수정 내용" };
            string Export() => ExportService.Export(b, Path.Combine(root, Guid.NewGuid().ToString("N")), []);
            DriveFolderService.Publish(sync, "laptop", b, Export(), CancellationToken.None);
            b.Revision = 3; b.Title = "변경된 제목";
            var latest = DriveFolderService.Publish(sync, "laptop", b, Export(), CancellationToken.None);
            b.Revision = 1; DriveFolderService.Publish(sync, "pc", b, Export(), CancellationToken.None);
            var bad = Path.Combine(sync, "ThoughtCanvas", "Boards", "bad", "device", "v0"); Directory.CreateDirectory(bad);
            File.WriteAllText(Path.Combine(bad, "snapshot.zip"), "incomplete");
            File.WriteAllText(Path.Combine(bad, "snapshot.pending"), "pending");
            var catalog = DriveFolderService.Discover(sync, CancellationToken.None);
            Assert(catalog.Entries.Count == 2 && catalog.Skipped == 1, "기기 분리 또는 손상 저장본 제외 실패");
            var laptop = catalog.Entries.Single(e => e.Device == "laptop");
            Assert(laptop.Revision == 3 && laptop.Title == "변경된 제목" && laptop.Path == Path.Combine(latest, "snapshot.zip"), "이름 변경 후 최신 저장본 선택 실패");
            Assert(DriveFolderService.Read(laptop.Path).Board.UserDocument == "노트북 수정 내용", "문서 복원 실패");
            Assert(laptop.LocalId != b.Id && laptop.LocalId == (laptop with { Path = Path.Combine("Z:\\", "v" , Path.GetFileName(Path.GetDirectoryName(laptop.Path))!, "snapshot.zip") }).LocalId, "PC 경로 변경 시 복사본 ID 불일치");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            bool stopped = false; try { DriveFolderService.Discover(sync, cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
            Assert(stopped, "목록 검색 취소 실패");
        });
        Test("Drive 폴더 실패: 미연결 경로·취소 시 완료 저장본 미게시", () =>
        {
            bool rejected = false;
            try { DriveFolderService.Root(Path.Combine(root, "disconnected")); } catch (IOException) { rejected = true; }
            Assert(rejected, "없는 경로 자동 생성");
            var sync = Path.Combine(root, "cancel-sync"); Directory.CreateDirectory(sync);
            var b = new Board(); var export = ExportService.Export(b, Path.Combine(root, "cancel-export"), []);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); rejected = false;
            try { DriveFolderService.Publish(sync, "device", b, export, cancelled.Token); } catch (OperationCanceledException) { rejected = true; }
            Assert(rejected && !Directory.GetFiles(sync, "snapshot.zip", SearchOption.AllDirectories).Any(), "미완료 저장본 게시됨");
        });
        Test("크기 원: Zoom 배율·Tool 전환·마지막 위치 유지", () =>
        {
            var surface = new BoardSurface { Tool = CanvasTool.Eraser, EraserSize = 40 };
            surface.Load(new Board { Zoom = 2 }); surface.UpdatePointer(new Point(120, 90));
            var ring = surface.Children.OfType<System.Windows.Shapes.Ellipse>().Single();
            Assert(ring.Width == 80 && System.Windows.Controls.Canvas.GetLeft(ring) == 80, "Eraser 크기 또는 중심 오류");
            surface.UpdatePointer(); surface.SetTheme(true);
            Assert(System.Windows.Controls.Canvas.GetTop(ring) == 50 && ring.Visibility == Visibility.Visible, "감지 중단 시 마지막 위치 유실");
            surface.Tool = CanvasTool.Pen; surface.PenWidth = 3; surface.UpdatePointer();
            Assert(ring.Width == 6 && System.Windows.Controls.Canvas.GetLeft(ring) == 117, "Pen Tool 크기 갱신 실패");
        });
        Test("Dark mode: 기존 Ink·Text 흰색 표시와 Light 원본 색상 복원", () =>
        {
            var b = new Board { ViewX = 0, ViewY = 0, Zoom = 1, Ink = [Line()], Texts = [new() { X = 20, Y = 80, Text = "기존 Text ABC", FontSize = 24 }] };
            var original = DataJson.Write(b); var surface = new BoardSurface(); surface.Load(b);
            byte[] Render(bool dark)
            {
                surface.SetTheme(dark); surface.Measure(new Size(320, 160)); surface.Arrange(new Rect(0, 0, 320, 160)); surface.UpdateLayout();
                var image = new System.Windows.Media.Imaging.RenderTargetBitmap(320, 160, 96, 96, PixelFormats.Pbgra32); image.Render(surface);
                var pixels = new byte[320 * 160 * 4]; image.CopyPixels(pixels, 320 * 4, 0); return pixels;
            }
            var light = Render(false); var dark = Render(true); var restored = Render(false);
            int at = (50 * 320 + 100) * 4;
            Assert(dark[at] == 255 && dark[at + 1] == 255 && dark[at + 2] == 255 && light[at] < 200, "기존 필기가 흰색으로 전환되지 않음");
            bool whiteText = false;
            for (int y = 82; y < 120; y++) for (int x = 20; x < 200; x++) { int p = (y * 320 + x) * 4; if (dark[p] > 245 && dark[p + 1] > 245 && dark[p + 2] > 245) whiteText = true; }
            Assert(whiteText && restored.SequenceEqual(light) && DataJson.Write(b) == original, "Text 전환 또는 원본 복원 실패");
        });
        Test("S Pen Button: 누름 1회·유지·해제·다음 누름", () =>
        {
            var gate = new PenButtonGate();
            Assert(gate.Update(true, false, 1000), "최초 누름 누락");
            Assert(!gate.Update(true, false, 1500) && !gate.Update(true, false, 2500), "길게 누르기 중 중복 전환");
            Assert(!gate.Update(false, false, 2600) && gate.Update(true, false, 2800), "해제 후 재입력 실패");
            gate.Reset(); Assert(gate.Update(true, false, 3200), "감지 범위 이탈 후 복구 실패");
        });
        Test("S Pen Button: Windows/WPF 중복 억제와 fallback", () =>
        {
            var gate = new PenButtonGate();
            Assert(gate.Update(true, false, 1000), "WPF 입력 누락");
            Assert(!gate.Update(true, true, 1010), "같은 Windows 입력 중복");
            Assert(!gate.Update(false, false, 1020) && !gate.Update(true, false, 1030), "지연된 WPF 입력 중복");
            gate.Update(false, true, 1400); Assert(gate.Update(true, true, 1600), "다음 Windows 입력 누락");
            gate.Update(false, false, 2200); Assert(gate.Update(true, false, 2400), "Windows 메시지 중단 시 WPF fallback 실패");
        });
        Test("Windows Pen interop: x64 구조체 정렬", () =>
        {
            var flags = System.Reflection.BindingFlags.NonPublic;
            Assert(System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativePenObserver).GetNestedType("PointerInfo", flags)!) == 96, "POINTER_INFO ABI 오류");
            Assert(System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativePenObserver).GetNestedType("PenInfo", flags)!) == 120, "POINTER_PEN_INFO ABI 오류");
        });
        Test("HID Raw Input: 복수 Report 분리·잘린 Report 차단", () =>
        {
            var data = new byte[] { 99, 99, 1, 0x44, 1, 0 };
            var reports = RawPenInput.SplitReports(data, 2, 2, 2);
            Assert(reports.Count == 2 && reports[0].SequenceEqual(new byte[] { 1, 0x44 }) && reports[1].SequenceEqual(new byte[] { 1, 0 }), "Report 경계 손상");
            foreach (var spec in new[] { (2, 3u, 2u), (2, 0u, 1u), (2, uint.MaxValue, 2u), (-1, 2u, 1u) })
            {
                bool rejected = false; try { RawPenInput.SplitReports(data, spec.Item1, spec.Item2, spec.Item3); } catch (InvalidDataException) { rejected = true; }
                Assert(rejected, "잘못된 HID 길이 허용");
            }
        });
        Test("HID Barrel: Primary·Secondary만 허용", () =>
        {
            Assert(RawPenInput.IsBarrelUsage(13, 0x44) && RawPenInput.IsBarrelUsage(13, 0x5A), "표준 Barrel 누락");
            foreach (ushort usage in new ushort[] { 0x32, 0x3C, 0x42, 0x45 })
                Assert(!RawPenInput.IsBarrelUsage(13, usage), "접촉·근접·지우개를 Button으로 오인");
            Assert(!RawPenInput.IsBarrelUsage(9, 0x44), "다른 Usage Page를 Barrel로 오인");
        });
        Test("S Pen erase 신호: Hover→Contact→Hover 중 1회 전환", () =>
        {
            var gate = new PenButtonGate();
            bool Feed(long time, params ushort[] usages) => gate.UpdateRaw(usages.Any(u => RawPenInput.IsToolSwitchUsage(13, u)), time);
            Assert(!Feed(1000, 0x32) && !Feed(1300, 0x32, 0x42), "일반 Hover·필기에 도구 전환");
            Assert(Feed(1600, 0x32, 0x3C), "Hover erase Button 누락");
            Assert(!Feed(1900, 0x32, 0x45) && !Feed(2200, 0x32, 0x3C, 0x45) && !Feed(2500, 0x32, 0x3C), "Contact 변경 중 중복 전환");
            Assert(!Feed(2800, 0x32) && Feed(3100, 0x32, 0x3C), "해제 후 재누름 누락");
            Assert(!RawPenInput.IsToolSwitchUsage(9, 0x45), "다른 Page를 erase Button으로 오인");
        });
        Test("Pen 우클릭: Mouse·Touch 서명 제외", () =>
        {
            Assert(RawPenInput.IsPenMouse(0xFF515700) && RawPenInput.IsPenMouse(0xFF515701), "Pen 서명 누락");
            Assert(!RawPenInput.IsPenMouse(0) && !RawPenInput.IsPenMouse(0xFF515780) && !RawPenInput.IsPenMouse(0x12345678), "일반 Mouse 또는 Touch를 Pen으로 오인");
        });
        Test("HID Raw Input: WPF·Pointer 중복 억제·해제 후 재누름", () =>
        {
            var gate = new PenButtonGate(); Assert(gate.UpdateRaw(true, 1000), "Raw 누름 누락");
            Assert(!gate.Update(false, true, 1010) && !gate.Update(true, false, 1020) && !gate.UpdateRaw(true, 1500), "여러 입력 경로의 중복 전환");
            gate.UpdateRaw(false, 1600); Assert(gate.UpdateRaw(true, 1900), "Raw 해제 후 재입력 누락");
            gate.Update(false, false, 2500); Assert(gate.Update(true, false, 2700), "Raw 중단 후 fallback 실패");
        });
        Test("HID Raw Input: Windows 등록·해제 및 HID ABI", () =>
        {
            Assert(System.Runtime.InteropServices.Marshal.SizeOf(typeof(RawPenInput).GetNestedType("ButtonCap", System.Reflection.BindingFlags.NonPublic)!) == 72, "HIDP_BUTTON_CAPS ABI 오류");
            Assert(System.Runtime.InteropServices.Marshal.SizeOf(typeof(RawPenInput).GetNestedType("Caps", System.Reflection.BindingFlags.NonPublic)!) == 64, "HIDP_CAPS ABI 오류");
            var window = new Window(); new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            try { using var input = new RawPenInput(window, (_, _, _) => { }); Assert(input.Summary.Contains("등록됨"), input.Summary); }
            finally { window.Close(); }
        });
        FeatureTests.Run(root, Test, Assert);
        OptimizationTests.Run(root, Test, Assert);
        File.WriteAllText(report, $"ThoughtCanvas 자동 검증 · {DateTime.Now:O}\n{results.Count - failed}/{results.Count} 통과\n\n" + string.Join("\n\n", results));
        return failed == 0 ? 0 : 1;
    }
    private static string WrapResponse(AnalysisResult result, string? raw = null, bool includeThought = false)
    {
        var parts = new List<object>();
        if (includeThought) parts.Add(new { thought = true, text = "not JSON: internal summary" });
        parts.Add(new { text = raw ?? DataJson.Write(result) });
        return JsonSerializer.Serialize(new { candidates = new[] { new { finishReason = "STOP", content = new { role = "model", parts } } } });
    }
    private class FixtureHandler(string id) : System.Net.Http.HttpMessageHandler
    {
        public string? RequestBody { get; private set; }
        public string? Url { get; private set; }
        public string? Key { get; private set; }
        public bool HasAuthorization { get; private set; }
        public System.Net.HttpStatusCode Status { get; set; } = System.Net.HttpStatusCode.OK;
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            Url = request.RequestUri!.ToString(); Key = request.Headers.GetValues("x-goog-api-key").Single(); HasAuthorization = request.Headers.Authorization != null;
            var result = new AnalysisResult { Document = "Draw 분석", Ideas = [new() { Id = "i1", Content = "Draw", SourceIds = [id] }] };
            var payload = Status == System.Net.HttpStatusCode.OK ? WrapResponse(result) : JsonSerializer.Serialize(new { error = new { message = "test error fixture-key" } });
            return new(Status) { Content = new System.Net.Http.StringContent(payload) };
        }
    }
}
