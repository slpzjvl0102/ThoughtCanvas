namespace ThoughtCanvas;
public static class BoardTransfer
{
    public static void Import(BoardStore store, BoardBundle bundle, string id)
    {
        long next = (store.Load(id)?.Revision ?? -1) + 1;
        var versionMap = new Dictionary<long, long>();
        foreach (var source in bundle.Sources.OrderBy(b => b.Revision)) versionMap.TryAdd(source.Revision, next++);
        versionMap[bundle.Board.Revision] = next;
        void Remap(Board b)
        {
            b.Id = id; b.Revision = versionMap.GetValueOrDefault(b.Revision, next);
            if (b.Analysis != null)
            {
                b.Analysis.SourceRevision = versionMap.GetValueOrDefault(b.Analysis.SourceRevision, b.Analysis.SourceRevision);
                foreach (var idea in b.Analysis.Ideas) idea.SourceRevision = versionMap.GetValueOrDefault(idea.SourceRevision, idea.SourceRevision);
            }
            foreach (var edit in b.DocumentEdits) edit.SourceRevision = versionMap.GetValueOrDefault(edit.SourceRevision, edit.SourceRevision);
        }
        foreach (var source in bundle.Sources) { Remap(source); store.Save(source); }
        Remap(bundle.Board); bundle.Board.ContentHash = SyncEngine.Digest(bundle.Board); store.Save(bundle.Board);
    }
}
