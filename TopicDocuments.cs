namespace ThoughtCanvas;
public static class TopicDocuments
{
    public static void Refresh(Board board)
    {
        var activeTopics = (board.Analysis?.Ideas ?? []).Where(i => i.Status != "cancelled_or_deferred").Select(i => i.Topic).ToHashSet();
        foreach (var document in board.TopicDocuments) document.Archived = !activeTopics.Contains(document.Topic);
        foreach (var group in (board.Analysis?.Ideas ?? []).Where(i => i.Status != "cancelled_or_deferred").GroupBy(i => i.Topic))
        {
            string text = "## " + group.Key + "\n\n" + string.Join("\n\n", group.GroupBy(i => i.Category).Select(c => "### " + c.Key + "\n" + string.Join("\n", c.Select(i => "- " + (i.Status == "uncertain" ? "(미확인) " : "") + i.Content.Replace("\n", "\n  ")))));
            var document = board.TopicDocuments.FirstOrDefault(d => d.Topic == group.Key);
            if (document == null) board.TopicDocuments.Add(new() { Topic = group.Key, Text = text });
            else if (document.Edited) document.ProposedText = text;
            else document.Text = text;
        }
    }
    public static List<string> Paragraphs(string text) => System.Text.RegularExpressions.Regex.Split(text.Replace("\r\n", "\n"), @"\n\s*\n").Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
    public static string MergeParagraphs(string current, IEnumerable<string> selected, bool append) => append
        ? current.TrimEnd() + "\n\n" + string.Join("\n\n", selected) : string.Join("\n\n", selected);
}
