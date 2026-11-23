using System.Net.Http;
using System.Text;
using System.Text.Json;
namespace ThoughtCanvas;
public static class NotionExporter
{
    public static List<object> Blocks(Board b)
    {
        var blocks = new List<object>();
        void Add(string type, string text)
        {
            // Preserve Unicode scalar boundaries while staying below Notion's per-text limit.
            var chunk = new StringBuilder();
            void Flush() { if (chunk.Length == 0) return; blocks.Add(new Dictionary<string, object> { ["object"] = "block", ["type"] = type, [type] = new { rich_text = new[] { new { type = "text", text = new { content = chunk.ToString() } } } } }); chunk.Clear(); }
            foreach (var rune in text.EnumerateRunes()) { if (chunk.Length + rune.Utf16SequenceLength > 1900) Flush(); chunk.Append(rune.ToString()); } Flush();
        }
        Add("heading_1", b.Title);
        foreach (var paragraph in TopicDocuments.Paragraphs(b.UserDocument)) Add("paragraph", paragraph);
        foreach (var topic in b.TopicDocuments.Where(t => !t.Archived)) { Add("heading_2", topic.Topic); foreach (var paragraph in TopicDocuments.Paragraphs(topic.Text)) Add("paragraph", paragraph); }
        Add("heading_2", "검토했던 아이디어"); foreach (var paragraph in TopicDocuments.Paragraphs(b.Analysis?.ArchivedDocument ?? "없음")) Add("paragraph", paragraph);
        foreach (var topic in b.TopicDocuments.Where(t => t.Archived)) { Add("heading_2", "검토 보관 · " + topic.Topic); foreach (var paragraph in TopicDocuments.Paragraphs(topic.Text)) Add("paragraph", paragraph); }
        Add("paragraph", $"원본 Board: {b.Id} · v{b.Revision}"); return blocks;
    }
    public static async Task<string> Export(Board board, string token, string parent, CancellationToken ct, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(token) || !Guid.TryParse(parent, out var parentId)) throw new InvalidOperationException("설정에서 Notion Token과 상위 Page ID를 입력하고 해당 Page에 Connection을 연결하세요.");
        using var owned = http == null ? new HttpClient { Timeout = TimeSpan.FromMinutes(2) } : null; var client = http ?? owned!;
        async Task<JsonDocument> Send(HttpMethod method, string path, object body)
        {
            using var request = new HttpRequestMessage(method, "https://api.notion.com/v1/" + path);
            request.Headers.Authorization = new("Bearer", token); request.Headers.Add("Notion-Version", "2022-06-28");
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Notion 전송 실패 ({(int)response.StatusCode}). Token·Page 권한·요청 한도를 확인하세요.");
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        var blocks = Blocks(board);
        using var page = await Send(HttpMethod.Post, "pages", new { parent = new { page_id = parentId.ToString() }, properties = new { title = new { title = new[] { new { text = new { content = board.Title.Length > 100 ? board.Title[..100] : board.Title } } } } }, children = blocks.Take(80).ToArray() });
        string id = page.RootElement.GetProperty("id").GetString()!; string url = page.RootElement.GetProperty("url").GetString()!;
        try { foreach (var chunk in blocks.Skip(80).Chunk(80)) { await Task.Delay(350, ct); using var _ = await Send(HttpMethod.Patch, $"blocks/{id}/children", new { children = chunk }); } }
        catch (Exception ex) { throw new InvalidOperationException("Notion Page가 일부만 작성되었습니다. 재시도 전에 확인하세요: " + url, ex); }
        return url;
    }
}
