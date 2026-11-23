using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace ThoughtCanvas;
public static class SourceViewer
{
    public static void Show(Window owner, Board document, Func<long, Board?> resolve, List<string>? initialIds = null, long? initialRevision = null)
    {
        var window = new Window { Owner = owner, Title = "문서 구조별 원본 보기", Background = Brushes.White, Width = 1180, Height = 780, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new GridLength(290) }); grid.ColumnDefinitions.Add(new()); window.Content = grid;
        var tree = new TreeView { Margin = new Thickness(8) }; grid.Children.Add(tree);
        var dock = new DockPanel { Margin = new Thickness(8) }; Grid.SetColumn(dock, 1); grid.Children.Add(dock);
        var heading = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10), FontSize = 16, Foreground = Brushes.Black }; DockPanel.SetDock(heading, Dock.Top); dock.Children.Add(heading);
        var image = new Image { Stretch = Stretch.Uniform }; var scroll = new ScrollViewer { Content = image, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto }; dock.Children.Add(scroll);
        var sections = SourceSections.Build(document);
        if (initialIds?.Count > 0) sections.Insert(0, new("selected", "선택한 항목", "원본 연결 범위", initialRevision ?? document.Revision, initialIds, "진하게 표시된 부분이 선택 항목의 원본입니다. 주변 원본은 맥락 확인을 위해 흐리게 표시합니다."));
        foreach (var topic in sections.GroupBy(s => (s.Topic, s.Archived)))
        {
            var parent = new TreeViewItem { Header = (topic.Key.Archived ? "[검토] " : "") + topic.Key.Topic, IsExpanded = true };
            foreach (var section in topic)
            {
                var child = new TreeViewItem { Header = section.Category, Tag = section };
                child.Selected += (_, e) =>
                {
                    e.Handled = true;
                    var source = section.Revision == document.Revision ? document : resolve(section.Revision);
                    if (source == null) { heading.Text = "해당 항목의 과거 원본을 찾지 못했습니다. Drive 원본 저장본을 가져와 주세요."; image.Source = null; return; }
                    try
                    {
                        var missing = section.SourceIds.Except(source.AllSourceIds()).Count();
                        heading.Text = section.Topic + " / " + section.Category + "\n" + section.Text + (missing > 0 ? $"\n연결 확인 필요: {missing}개" : "");
                        using var stream = new MemoryStream(SourceSections.Render(source, section.SourceIds));
                        image.Source = System.Windows.Media.Imaging.BitmapFrame.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    }
                    catch (InvalidDataException ex) { heading.Text = ex.Message; image.Source = null; }
                };
                parent.Items.Add(child);
            }
            tree.Items.Add(parent);
        }
        window.Loaded += (_, _) => { if (tree.Items.Count > 0 && tree.Items[0] is TreeViewItem first && first.Items.Count > 0) ((TreeViewItem)first.Items[0]).IsSelected = true; };
        window.ShowDialog();
    }
}
