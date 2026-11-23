namespace ThoughtCanvas;
public static class CanvasLayout
{
    public static Board Arrange(Board board)
    {
        if (board.Analysis?.Ideas.Count is not > 0) throw new InvalidOperationException("AI 정리 또는 분류 수정 후 정돈할 수 있습니다.");
        var result = DataJson.Clone(board); var valid = result.AllSourceIds();
        var groups = board.Analysis.Ideas.GroupBy(i => i.Topic).Select(g => g.SelectMany(i => i.SourceIds).Where(valid.Contains).ToHashSet()).Where(s => s.Count > 0).ToList();
        // Shared evidence belongs to one spatial unit, never move a shared stroke twice.
        for (int i = 0; i < groups.Count; i++) for (int j = i + 1; j < groups.Count; j++)
            if (groups[i].Overlaps(groups[j])) { groups[i].UnionWith(groups[j]); groups.RemoveAt(j); i = -1; break; }
        var unassigned = valid.Except(groups.SelectMany(x => x)).ToHashSet(); if (unassigned.Count > 0) groups.Add(unassigned);
        double y = 0;
        foreach (var group in groups)
        {
            var part = new Board { Ink = result.Ink.Where(i => group.Contains(i.Id)).ToList(), Texts = result.Texts.Where(t => group.Contains(t.Id)).ToList() };
            var bounds = SyncEngine.Bounds(part); if (bounds.IsEmpty) continue;
            SyncEngine.Offset(result, group, -bounds.Left, y - bounds.Top); y += bounds.Height + 180;
        }
        return result;
    }
}
