using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace ThoughtCanvas;

public sealed class AiRequestContext
{
    public List<object> Parts { get; } = [];
    public Dictionary<string, string> Fingerprints { get; } = [];
    public Dictionary<string, string> DecodeIds { get; } = [];
    public HashSet<string> ReusedSourceIds { get; } = [];
    public AiRequestMetrics Metrics { get; } = new();
    private static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string Json(object? value) => JsonSerializer.Serialize(value, Compact);
    public static string Fingerprint(RegionData r) => Convert.ToHexString(SHA256.HashData(r.Png.Concat(Encoding.UTF8.GetBytes(Json(new { r.X, r.Y, r.Size, r.SourceIds }))).ToArray()));
    public static AiRequestContext Build(Board board, List<RegionData> regions, bool full = false)
    {
        var context = new AiRequestContext();
        var ids = board.AllSourceIds().Concat(board.Analysis?.Ideas.SelectMany(i => i.SourceIds) ?? []).Concat(board.Analysis?.Reviews.SelectMany(r => r.SourceIds) ?? []).Distinct().ToList();
        var alias = ids.Select((id, index) => (id, name: "s" + index)).ToDictionary(x => x.id, x => x.name);
        foreach (var pair in alias) context.DecodeIds[pair.Value] = pair.Key;
        string A(string id) => alias.GetValueOrDefault(id, id);
        var previous = board.Analysis == null ? null : JsonNode.Parse(Json(new
        {
            Ideas = board.Analysis.Ideas.Select(i => new { i.Id, i.Topic, i.Category, i.Content, i.Context, i.Reason, i.Status, i.Evidence, i.SourceIds, i.Relations, i.SourceRevision }),
            Reviews = board.Analysis.Reviews.DistinctBy(r => (r.Kind, r.Question, r.Answer, r.Resolved, string.Join(",", r.SourceIds.Order())))
                .Select(r => new { r.Kind, r.Question, r.Answer, r.Resolved, r.SourceIds }),
            board.Analysis.ArchivedDocument
        }));
        RewriteSources(previous, A);
        var alternatives = board.DocumentEdits.Where(e => e.Origin != "user_document" && e.Origin != "history_restore" && e.Text != board.UserDocument)
            .DistinctBy(e => (e.Origin, e.Text)).Select(e => new { e.Origin, e.Text });
        object Box(InkItem i)
        {
            if (i.Points.Count == 0) return new double[] { 0, 0, 0, 0 };
            return new[] { i.Points.Min(p => p.X), i.Points.Min(p => p.Y), i.Points.Max(p => p.X), i.Points.Max(p => p.Y) }.Select(x => Math.Round(x, 2)).ToArray();
        }
        var manifest = new
        {
            board.Title, board.Revision,
            CoordinateFormat = "Ink bounds: [left,top,right,bottom], global canvas coordinates. Source IDs are request-local aliases; return them exactly.",
            Ink = board.Ink.Select(i => new { Id = A(i.Id), ParentId = i.ParentId == null ? null : A(i.ParentId), Bounds = Box(i), i.Color }),
            Texts = board.Texts.Select(t => new { Id = A(t.Id), t.X, t.Y, t.Width, t.FontSize, t.Text }),
            PreviousStructuredContext = previous,
            CurrentDocument = string.IsNullOrEmpty(board.UserDocument) ? board.Analysis?.Document : board.UserDocument,
            UserEditedDocument = board.DocumentEdited,
            TopicDocuments = board.TopicDocuments.Where(d => d.Edited).Select(d => new { d.Topic, d.Text, d.Archived }),
            AlternativeDocuments = alternatives,
            PendingDocumentProposal = board.ProposedDocument == board.UserDocument || board.ProposedDocument == board.Analysis?.Document ? null : board.ProposedDocument
        };
        void AddText(object data) { string text = Json(data); context.Metrics.InputCharacters += text.Length; context.Parts.Add(new { text }); }
        AddText(manifest);
        context.Metrics.HistorySnapshotsOmitted = board.DocumentEdits.Count(e => e.Origin is "user_document" or "history_restore");
        var covered = (board.Analysis?.Ideas.SelectMany(i => i.SourceIds) ?? []).ToHashSet();
        var reviewSources = (board.Analysis?.Reviews.SelectMany(r => r.SourceIds) ?? []).ToHashSet();
        bool unresolvedGlobal = board.Analysis?.Reviews.Any(r => !r.Resolved && r.SourceIds.Count == 0) == true;
        foreach (var region in regions)
        {
            string hash = Fingerprint(region); context.Fingerprints[region.Name] = hash;
            bool reuse = !full && !unresolvedGlobal && region.SourceIds.All(covered.Contains) && !region.SourceIds.Any(reviewSources.Contains)
                && board.Analysis?.RegionFingerprints.GetValueOrDefault(region.Name) == hash;
            AddText(new { Region = region.Name, region.X, region.Y, region.Size, SourceIds = region.SourceIds.Select(A), Image = reuse ? "unchanged; use previous structured context" : "attached" });
            if (reuse) { context.Metrics.ImagesReused++; context.ReusedSourceIds.UnionWith(region.SourceIds); }
            else { context.Parts.Add(new { inlineData = new { mimeType = "image/png", data = Convert.ToBase64String(region.Png) } }); context.Metrics.ImagesSent++; }
        }
        return context;
    }
    private static void RewriteSources(JsonNode? node, Func<string, string> map)
    {
        if (node is JsonObject obj)
            foreach (var p in obj.ToList())
            {
                if (p.Key == "SourceIds" && p.Value is JsonArray array)
                { for (int i = 0; i < array.Count; i++) if (array[i] is JsonValue v && v.TryGetValue<string>(out var id)) array[i] = map(id); }
                else RewriteSources(p.Value, map);
            }
        else if (node is JsonArray list) foreach (var item in list) RewriteSources(item, map);
    }
    public string DecodeResponse(string payload)
    {
        var root = JsonNode.Parse(payload)!;
        if (root["candidates"] is not JsonArray candidates) return payload;
        foreach (var candidate in candidates)
        {
            if (candidate?["finishReason"]?.GetValue<string>() != "STOP") continue;
            if (candidate?["content"]?["parts"] is not JsonArray parts) continue;
            var textParts = parts.OfType<JsonObject>().Where(p => p["thought"]?.GetValue<bool>() != true && p["text"] != null).ToList();
            if (textParts.Count == 0) continue;
            var structured = JsonNode.Parse(string.Concat(textParts.Select(p => p["text"]!.GetValue<string>())));
            RewriteSources(structured, id => DecodeIds.GetValueOrDefault(id, id));
            textParts[0]["text"] = structured?.ToJsonString(); foreach (var part in textParts.Skip(1)) part["text"] = "";
        }
        return root.ToJsonString();
    }
    public void ReadUsage(string payload)
    {
        using var json = JsonDocument.Parse(payload);
        if (!json.RootElement.TryGetProperty("usageMetadata", out var usage)) return;
        int? Count(string name) => usage.TryGetProperty(name, out var n) && n.TryGetInt32(out int v) ? v : null;
        Metrics.PromptTokens = Count("promptTokenCount"); Metrics.OutputTokens = Count("candidatesTokenCount"); Metrics.ThoughtTokens = Count("thoughtsTokenCount");
    }
}
