using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace ThoughtCanvas;

public static class SyncEngine
{
    public static string Digest(Board b)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(DataJson.Write(new
        { b.Title, b.Ink, b.Texts, b.Analysis, b.UserDocument, b.DocumentEdited, b.ProposedDocument, b.DocumentEdits, b.TopicDocuments }))!;
        // Local revision numbers are reindexed on import; they are not content changes.
        void Normalize(System.Text.Json.Nodes.JsonNode? node)
        {
            if (node is System.Text.Json.Nodes.JsonObject obj) { obj.Remove("SourceRevision"); foreach (var child in obj.ToList()) Normalize(child.Value); }
            else if (node is System.Text.Json.Nodes.JsonArray array) foreach (var child in array) Normalize(child);
        }
        Normalize(json); return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }
    public static void Stamp(Board b, string device)
    {
        var hash = Digest(b); if (hash == b.ContentHash) return;
        b.Clock[device] = b.Clock.GetValueOrDefault(device) + 1;
        b.ContentHash = hash; b.LastWriter = device;
    }
    public static bool Dominates(Board a, Board b) => b.Clock.Count > 0 &&
        b.Clock.All(x => a.Clock.GetValueOrDefault(x.Key) >= x.Value) &&
        a.Clock.Any(x => x.Value > b.Clock.GetValueOrDefault(x.Key));
    public static bool Equivalent(Board a, Board b) => Digest(a) == Digest(b);
    public static Rect Bounds(Board b)
    {
        var r = Rect.Empty;
        foreach (var i in b.Ink.Where(i => i.Points.Count > 0)) r.Union(i.ToStroke().GetBounds());
        foreach (var t in b.Texts) r.Union(BoardSurface.TextBounds(t));
        return r;
    }
    public static void Offset(Board b, IEnumerable<string> ids, double dx, double dy)
    {
        var set = ids.ToHashSet();
        foreach (var i in b.Ink.Where(i => set.Contains(i.Id))) i.Points = i.Points.Select(p => new Sample(p.X + dx, p.Y + dy, p.Pressure)).ToList();
        foreach (var t in b.Texts.Where(t => set.Contains(t.Id))) { t.X += dx; t.Y += dy; }
    }
    // Merge additions and alternative edits into a separate, non-overlapping area. Never infer deletions.
    public static Board Merge(Board local, Board remote, string device)
    {
        var result = DataJson.Clone(local); var delta = new Board();
        var map = new Dictionary<string, string>();
        foreach (var i in remote.Ink)
        {
            var same = local.Ink.FirstOrDefault(x => x.Id == i.Id);
            if (same != null && DataJson.Write(same) == DataJson.Write(i)) continue;
            var copy = DataJson.Clone(i); copy.ParentId = i.Id;
            if (local.AllSourceIds().Contains(copy.Id)) copy.Id = Guid.NewGuid().ToString("N");
            map[i.Id] = copy.Id; delta.Ink.Add(copy);
        }
        foreach (var t in remote.Texts)
        {
            var same = local.Texts.FirstOrDefault(x => x.Id == t.Id);
            if (same != null && DataJson.Write(same) == DataJson.Write(t)) continue;
            var copy = DataJson.Clone(t); if (local.AllSourceIds().Contains(copy.Id)) copy.Id = Guid.NewGuid().ToString("N");
            map[t.Id] = copy.Id; delta.Texts.Add(copy);
        }
        var bounds = Bounds(local); var changed = Bounds(delta);
        if (!changed.IsEmpty && !bounds.IsEmpty) Offset(delta, delta.AllSourceIds(), bounds.Right + 160 - changed.Left, bounds.Top - changed.Top);
        result.Ink.AddRange(delta.Ink); result.Texts.AddRange(delta.Texts);
        result.Analysis ??= new();
        var alternatives = new List<string>();
        foreach (var idea in remote.Analysis?.Ideas ?? [])
        {
            if (result.Analysis.Ideas.Any(x => DataJson.Write(x) == DataJson.Write(idea))) continue;
            var copy = DataJson.Clone(idea); copy.Id = Guid.NewGuid().ToString("N");
            copy.SourceIds = copy.SourceIds.Select(id => map.GetValueOrDefault(id, id)).ToList();
            // Only remapped/current sources can point at the merged snapshot.
            if (copy.SourceIds.All(result.AllSourceIds().Contains)) copy.SourceRevision = Math.Max(local.Revision, remote.Revision) + 1;
            result.Analysis.Ideas.Add(copy); alternatives.Add(copy.Content);
        }
        if (remote.UserDocument != local.UserDocument)
        {
            result.DocumentEdits.Add(new() { Origin = "remote_document", Text = remote.UserDocument, SourceRevision = remote.Revision });
            result.ProposedDocument = remote.UserDocument;
        }
        foreach (var doc in remote.TopicDocuments)
        {
            var existing = result.TopicDocuments.FirstOrDefault(x => x.Topic == doc.Topic);
            if (existing == null) result.TopicDocuments.Add(DataJson.Clone(doc));
            else if (existing.Text != doc.Text) existing.ProposedText = doc.Text;
        }
        result.Analysis.ArchivedDocument += "\n\n[다른 기기의 검토 기록]\n" + remote.Analysis?.ArchivedDocument;
        result.Analysis.Reviews.Add(new() { Kind = "classification", SourceIds = map.Values.ToList(),
            Question = $"다른 기기 변경 {map.Count}개를 빈 공간에 배치했습니다. 비슷한 내용과 대체안을 확인해 주세요. 상대 기기의 삭제는 자동 적용하지 않았습니다. 문서 차이는 갱신 제안에서 확인하세요." });
        foreach (var pair in remote.Clock) result.Clock[pair.Key] = Math.Max(result.Clock.GetValueOrDefault(pair.Key), pair.Value);
        result.Revision = Math.Max(local.Revision, remote.Revision) + 1; result.ModifiedUtc = DateTime.UtcNow;
        Stamp(result, device); return result;
    }
    public static Board Backup(Board board)
    {
        var copy = DataJson.Clone(board); copy.Id = Guid.NewGuid().ToString("N"); copy.Title += " · 통합 전 보관 " + DateTime.Now.ToString("MM-dd HH:mm:ss"); return copy;
    }
}
