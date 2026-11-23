using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ThoughtCanvas;
public partial class MainWindow : Window
{
    private void ShowPanel(bool visible) { PanelColumn.Width = new GridLength(visible ? 470 : 0); SidePanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed; }
    private void TogglePanel(object sender, RoutedEventArgs e) => ShowPanel(SidePanel.Visibility != Visibility.Visible);
    private void UpdatePanels()
    {
        updating = true; var b = Surface.Board; DocumentEditor.Text = b.UserDocument; documentDirty = false;
        ArchiveEditor.Text = b.Analysis?.ArchivedDocument ?? "취소·보류한 아이디어는 AI 정리 후 맥락과 함께 표시됩니다.";
        AnalysisInfo.Text = b.Analysis == null ? "API 설정 후 ‘지금 정리’를 실행하세요." : $"AI 기준 v{b.Analysis.SourceRevision} · 현재 v{b.Revision} · {b.Analysis.CreatedUtc.ToLocalTime():MM/dd HH:mm}";        if (b.Analysis?.RequestMetrics is { } usage)
            AnalysisInfo.Text += $"\n입력 Token {usage.PromptTokens?.ToString("N0") ?? "미제공"} · 이미지 {usage.ImagesSent}장 / 재사용 {usage.ImagesReused}장\n전송 본문 {usage.InputCharacters:N0}자 · 과거 문서 {usage.HistorySnapshotsOmitted}개 전송 생략";
        ReviewList.ItemsSource = b.Analysis?.Reviews; int count = b.Analysis?.Reviews.Count(r => !r.Resolved) ?? 0; ReviewTab.Header = $"검증 {count}"; PanelToggle.Content = $"문서 · 검증 {count}";
        ProposalButton.Visibility = b.ProposedDocument == null ? Visibility.Collapsed : Visibility.Visible;
        TopicFilter.ItemsSource = new[] { "모든 대주제" }.Concat(SearchCandidates().Select(i => i.Idea.Topic).Distinct()); TopicFilter.SelectedIndex = 0;
        CategoryFilter.ItemsSource = new[] { "모든 분류" }; CategoryFilter.SelectedIndex = 0; updating = false; RefreshSearch();
    }
    private void DocumentChanged(object sender, TextChangedEventArgs e) { if (!updating && initialized) { documentDirty = true; lastEdit = DateTime.UtcNow; } }
    private void SaveDocumentIfNeeded()
    {
        if (!documentDirty) return; var b = Surface.Board; b.UserDocument = DocumentEditor.Text; b.DocumentEdited = true;
        b.DocumentEdits.Add(new() { Text = b.UserDocument, SourceRevision = b.Analysis?.SourceRevision ?? b.Revision }); documentDirty = false; MarkChanged(true);
    }
    private void SaveDocument(object sender, RoutedEventArgs e) { try { SaveDocumentIfNeeded(); SaveNow(); } catch (Exception ex) { Error(ex); } }
    private void CompareDocument(object sender, RoutedEventArgs e)
    {
        SaveDocumentIfNeeded(); var b = Surface.Board; if (b.ProposedDocument == null) return; string? merged = Dialogs.Compare(this, b.UserDocument, b.ProposedDocument);
        if (merged == null) return; b.UserDocument = merged; b.DocumentEdited = true; b.ProposedDocument = null;
        b.DocumentEdits.Add(new() { Text = merged, SourceRevision = b.Analysis?.SourceRevision ?? b.Revision }); MarkChanged(true); UpdatePanels();
    }
    private void ResolveReview(object sender, RoutedEventArgs e)
    {
        if (ReviewList.SelectedItem is not ReviewItem review) return;
        if (string.IsNullOrWhiteSpace(ReviewAnswer.Text)) { SetStatus("확인한 내용을 답변으로 남겨 주세요."); return; }
        review.Answer = ReviewAnswer.Text; review.Resolved = true; MarkChanged(true); SaveDocumentIfNeeded(); UpdatePanels(); SetStatus("검증 답변을 기록했습니다. 다음 AI 정리에 반영됩니다.");
    }
    private void ReviewSelectionChanged(object sender, SelectionChangedEventArgs e) { if (ReviewList.SelectedItem is ReviewItem review) ReviewAnswer.Text = review.Answer; }
    private void ReviewSource(object sender, RoutedEventArgs e) { if (ReviewList.SelectedItem is ReviewItem review) ShowSources(review.SourceIds, Surface.Board.Analysis?.SourceRevision ?? Surface.Board.Revision); }
    private void ShowSources(List<string> ids, long revision) => SourceViewer.Show(this, Surface.Board, v => store.LoadRevision(Surface.Board.Id, v), ids, revision);
    private void DocumentSourcesClick(object sender, RoutedEventArgs e) => SourceViewer.Show(this, Surface.Board, v => store.LoadRevision(Surface.Board.Id, v));
    private async void FullAnalyzeClick(object sender, RoutedEventArgs e) { try { await Analyze(true, true); } catch (Exception ex) { Error(ex); } }
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (initialized && !updating) RefreshSearch(); }
    private void SearchFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || updating) return;
        if (ReferenceEquals(sender, TopicFilter)) { updating = true; CategoryFilter.ItemsSource = new[] { "모든 분류" }.Concat(SearchCandidates().Where(i => TopicFilter.SelectedIndex == 0 || i.Idea.Topic == TopicFilter.SelectedItem?.ToString()).Select(i => i.Idea.Category).Distinct()); CategoryFilter.SelectedIndex = 0; updating = false; }
        RefreshSearch();
    }
    private void RefreshSearch()
    {
        if (!initialized) return;
        SearchResults.ItemsSource = LibrarySearch.Keyword(SearchCandidates(), SearchBox.Text.Trim(), TopicFilter.SelectedIndex <= 0 ? null : TopicFilter.SelectedItem?.ToString(), CategoryFilter.SelectedIndex <= 0 ? null : CategoryFilter.SelectedItem?.ToString());
    }
    private void SearchSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SearchResults.SelectedItem is not SearchHit hit) return;
        var idea = hit.Idea; EvidenceBox.Text = $"{hit.Match} · {hit.Explanation}\n상태: {idea.Status}\n근거: {idea.Evidence}\n맥락: {idea.Context}\n이유: {idea.Reason}\n관계: {string.Join(" / ", idea.Relations)}\n원본 v{idea.SourceRevision}";
        ShowHit(hit);
    }
}
