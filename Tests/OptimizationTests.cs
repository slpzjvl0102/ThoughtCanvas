using System.Text.Json;
using System.Text.Json.Nodes;
namespace ThoughtCanvas;
public static class OptimizationTests
{
    public static void Run(string root, Action<string, Action> test, Action<bool, string> assert)
    {
        Board Sample() => new() { Title = "토큰 최적화", Revision = 7, UserDocument = "## 퍼즐\n### 규칙\n- 빙판: 함께 이동", Texts = [new() { Id = Guid.NewGuid().ToString(), Text = "빙판: 함께 이동", X = 100, Y = 100 }] };
        string Body(AiRequestContext c) => string.Join("\n", c.Parts.Select(p => JsonSerializer.SerializeToElement(p)).Where(p => p.TryGetProperty("text", out _)).Select(p => p.GetProperty("text").GetString()));
        test("AI 요청: UTF8·짧은 ID·현재 문서 보존·과거 전문 중복 제외", () =>
        {
            var b = Sample(); b.DocumentEdits.Add(new() { Text = "오래된 중복 문서" }); b.DocumentEdits.Add(new() { Origin = "remote_document", Text = "다른 기기 미통합 문서" });
            b.Analysis = new() { ArchivedDocument = "검토 맥락", Ideas = [new() { SourceIds = [b.Texts[0].Id], Context = "원래 목적", Content = "함께 이동" }], Reviews = [new() { Answer = "사용자 답변", Resolved = true }] };
            var c = AiRequestContext.Build(b, ExportService.RenderRegions(b)); string body = Body(c);
            assert(body.Contains("빙판") && !body.Contains("\\u") && !body.Contains(b.Texts[0].Id), "한글 또는 ID 압축 실패");
            assert(!body.Contains("오래된 중복 문서") && body.Contains("다른 기기 미통합 문서") && body.Contains("검토 맥락") && body.Contains("사용자 답변") && body.Contains("원래 목적"), "필요한 맥락 손실");
            assert(b.DocumentEdits.Count == 2 && c.Metrics.HistorySnapshotsOmitted == 1, "로컬 이력 변경");
        });
        test("AI 요청: 이미지 원본 유지·변경/검증/전체 재분석 때 재전송", () =>
        {
            var b = Sample(); var regions = ExportService.RenderRegions(b); var c = AiRequestContext.Build(b, regions);
            var image = JsonSerializer.SerializeToElement(c.Parts[2]);
            assert(Convert.FromBase64String(image.GetProperty("inlineData").GetProperty("data").GetString()!).SequenceEqual(regions[0].Png), "이미지 손실");
            b.Analysis = new() { RegionFingerprints = c.Fingerprints, Ideas = [new() { SourceIds = [b.Texts[0].Id] }] };
            assert(AiRequestContext.Build(b, regions).Metrics.ImagesReused == regions.Count, "변경 없는 이미지 중복 전송");
            assert(AiRequestContext.Build(b, regions, true).Metrics.ImagesSent == regions.Count, "전체 재분석 누락");
            b.Texts[0].Text += " 변경"; assert(AiRequestContext.Build(b, ExportService.RenderRegions(b)).Metrics.ImagesSent > 0, "변경 이미지 생략");
            b.Analysis.Reviews.Add(new() { SourceIds = [b.Texts[0].Id], Answer = "재확인", Resolved = true });
            assert(AiRequestContext.Build(b, regions).Metrics.ImagesSent > 0, "검증 대상 이미지 생략");
        });
        test("AI 응답: Source ID 복원·실측 usage·불완전 응답 보존", () =>
        {
            var b = Sample(); var c = AiRequestContext.Build(b, []);
            string payload = AiRequestContext.Json(new { candidates = new[] { new { finishReason = "STOP", content = new { parts = new[] { new { text = "{\"Ideas\":[{\"Content\":\"s0\",\"SourceIds\":[\"s0\"]}]}" } } } } }, usageMetadata = new { promptTokenCount = 123, candidatesTokenCount = 45, thoughtsTokenCount = 6 } });
            c.ReadUsage(payload); var decoded = JsonNode.Parse(c.DecodeResponse(payload))!;
            var result = JsonNode.Parse(decoded["candidates"]![0]!["content"]!["parts"]![0]!["text"]!.GetValue<string>())!;
            assert(result["Ideas"]![0]!["SourceIds"]![0]!.GetValue<string>() == b.Texts[0].Id && result["Ideas"]![0]!["Content"]!.GetValue<string>() == "s0", "출처 외 본문 치환 오류");
            assert(c.Metrics.PromptTokens == 123 && c.Metrics.ThoughtTokens == 6, "실측 usage 오류");
            c.DecodeResponse("{\"candidates\":[{\"finishReason\":\"MAX_TOKENS\",\"content\":{\"parts\":[{\"text\":\"{\"}]}}]}");
        });
        test("원본 보기: 문서 주제/분류·과거 원본·미연결 별도 표시", () =>
        {
            var b = Sample(); var old = DataJson.Clone(b); old.Revision = 3;
            b.Texts.Add(new() { Id = "new", Text = "미연결", X = 900, Y = 100 });
            b.Analysis = new() { Ideas = [new() { Topic = "퍼즐", Category = "규칙", Content = "함께 이동", SourceIds = [b.Texts[0].Id], SourceRevision = 3 }] };
            var sections = SourceSections.Build(b); assert(sections.Count == 2 && sections[0].Revision == 3 && sections[1].Key == "unmapped", "출처 구조 불일치");
            string export = ExportService.Export(b, root, ExportService.RenderRegions(b), [old]);
            string html = File.ReadAllText(Path.Combine(export, "구역별 보기", "index.html"));
            assert(html.Contains("section_0.png") && html.Contains("unmapped.png") && File.Exists(Path.Combine(export, "구역별 보기", "section_0.png")), "구조별 원본 Export 실패");
            PdfExporter.Export(export, b, ExportService.RenderRegions(b));
            assert(File.Exists(Path.Combine(export, "Canvas-구역.pdf")) && File.Exists(Path.Combine(export, "Canvas-전체격자.pdf")), "문서 구조 PDF 또는 전체 원본 PDF 누락");
            File.Copy(Path.Combine(export, "Canvas-구역.pdf"), Path.Combine(Path.GetDirectoryName(root)!, "v062-sections.pdf"), true);
            File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(root)!, "v062-source-preview.png"), SourceSections.Render(b, [b.Texts[0].Id]));
            old.Texts.Clear(); string missing = ExportService.Export(b, root, ExportService.RenderRegions(b), [old]);
            assert(!File.Exists(Path.Combine(missing, "구역별 보기", "section_0.png")), "없는 과거 원본을 현재 원본으로 대체");
        });
        var auditPath = Environment.GetEnvironmentVariable("THOUGHTCANVAS_AUDIT_INPUT");
        if (!string.IsNullOrWhiteSpace(auditPath)) test("실제 저장본: 네트워크 없는 전송량 비교", () =>
        {
            var board = DriveFolderService.Read(auditPath).Board;
            var regions = ExportService.RenderRegions(board); var context = AiRequestContext.Build(board, regions);
            // Compare text payload only. Images, schema and system instruction are not included in either count.
            var oldManifest = new { board.Title, board.Revision, Ink = board.Ink.Select(i => new { i.Id, i.ParentId, Bounds = new { X = i.Points.Min(p => p.X), Y = i.Points.Min(p => p.Y), Width = i.Points.Max(p => p.X) - i.Points.Min(p => p.X), Height = i.Points.Max(p => p.Y) - i.Points.Min(p => p.Y) }, i.Color }), board.Texts, PreviousAnalysis = board.Analysis, EditedDocument = board.UserDocument, board.DocumentEdits };
            int oldCharacters = DataJson.Write(oldManifest).Length + regions.Sum(r => DataJson.Write(new { r.Name, r.X, r.Y, r.Size, r.SourceIds }).Length);
            BoardStore.AtomicWrite(Path.Combine(Path.GetDirectoryName(root)!, "v062-request-audit.json"), DataJson.Write(new { Ink = board.Ink.Count, Images = regions.Count, OldTextCharactersApproximate = oldCharacters, NewTextCharacters = context.Metrics.InputCharacters, context.Metrics.HistorySnapshotsOmitted, CurrentDocumentCharacters = board.UserDocument.Length, HistoricalDocumentCharacters = board.DocumentEdits.Sum(e => e.Text.Length), ActualApiCalled = false }));
            assert(context.Metrics.InputCharacters < oldCharacters, "실제 저장본 전송량 감소 실패");
        });
    }
}

