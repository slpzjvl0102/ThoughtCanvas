using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ThoughtCanvas;
public static class Dialogs
{
    private static Window Window(Window owner, string title, double width = 520, double height = 340) => new()
    {
        Owner = owner, Title = title, Width = width, Height = height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Background = (Brush)Application.Current.FindResource("CanvasBrush"), Foreground = (Brush)Application.Current.FindResource("TextBrush"), FontFamily = new FontFamily("Malgun Gothic"), FontSize = 13
    };
    private static Button Button(string title, Action action)
    {
        var b = new Button { Content = title, Padding = new Thickness(15, 9, 15, 9), Margin = new Thickness(4) }; b.Click += (_, _) => action(); return b;
    }
    public static void Message(Window owner, string title, string message) => MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    public static void DiagnosticMessage(Window owner, string title, string message)
    {
        var window = Window(owner, title, 740, 700);
        var dock = new DockPanel { Margin = new Thickness(16) }; window.Content = dock;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(Button("진단 내용 저장", () =>
        {
            var picker = new Microsoft.Win32.SaveFileDialog { FileName = "ThoughtCanvas-Pen-v055.txt", Filter = "Text file|*.txt" };
            if (picker.ShowDialog(window) != true) return;
            try { File.WriteAllText(picker.FileName, message); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Message(window, "저장 실패", ex.Message); }
        }));
        buttons.Children.Add(Button("닫기", () => window.Close()));
        DockPanel.SetDock(buttons, Dock.Bottom); dock.Children.Add(buttons);
        dock.Children.Add(new TextBox { Text = message, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12) });
        window.ShowDialog();
    }
    public static string? Ask(Window owner, string title, string label, string value)
    {
        var window = Window(owner, title, 470, 220); var stack = new StackPanel { Margin = new Thickness(24) }; window.Content = stack;
        stack.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 12) }); var input = new TextBox { Text = value, Padding = new Thickness(8) }; stack.Children.Add(input);
        string? result = null; stack.Children.Add(Button("저장", () => { if (!string.IsNullOrWhiteSpace(input.Text)) { result = input.Text.Trim(); window.DialogResult = true; } }));
        window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); }; window.ShowDialog(); return result;
    }
    public static string? Choose(Window owner, string title, string[] values)
    {
        var window = Window(owner, title, 620, 420); var dock = new DockPanel { Margin = new Thickness(20) }; window.Content = dock;
        var list = new ListBox { ItemsSource = values }; string? result = null;
        var ok = Button("선택", () => { if (list.SelectedItem is string value) { result = value; window.DialogResult = true; } }); DockPanel.SetDock(ok, Dock.Bottom); dock.Children.Add(ok); dock.Children.Add(list); window.ShowDialog(); return result;
    }
    public static bool Settings(Window owner, Settings settings, bool firstRun = false)
    {
        var window = Window(owner, firstRun ? "ThoughtCanvas 처음 설정" : "연결 및 자동 실행 설정", 650, 720); var dock = new DockPanel { Margin = new Thickness(24) }; window.Content = dock;
        var stack = new StackPanel(); var scroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        void Label(string text) => stack.Children.Add(new TextBlock { Text = text, Margin = new Thickness(0, 12, 0, 5), TextWrapping = TextWrapping.Wrap });
        if (firstRun) Label("환영합니다. 이 기기에서 처음 실행하여 기본 설정을 확인합니다. 모든 항목은 나중에 작업 → 연결 설정에서 바꿀 수 있고, AI·Drive·Notion은 비워 두어도 필기와 Local 저장은 바로 사용할 수 있습니다.");
        Label("기기 이름"); var device = new TextBox { Text = settings.DeviceName, Padding = new Thickness(8) }; stack.Children.Add(device);
        Label($"현재 Key 출처: {GeminiCredentials.Resolve(settings).Source} · Key 값은 표시하지 않습니다.");
        Label("환경 변수 우선: GEMINI_API_KEY → GOOGLE_API_KEY → 앱에 저장한 Key. 각 변수는 User → Machine → Process 순서로 확인합니다. 환경 변수를 사용하면 아래 Key는 비워두셔도 됩니다.");
        Label("Gemini API key · 이 Windows 사용자에게 암호화하여 Local 저장"); var key = new PasswordBox { Password = Secrets.Unprotect(settings.ProtectedGeminiApiKey), Padding = new Thickness(8) }; stack.Children.Add(key);
        Label("Model ID (기본: gemini-3.8-flash) · 쉼표로 대체 Model을 이어 쓰면 사용량 한도(429) 시 순서대로 전환합니다. 예: gemini-3.8-flash, gemini-3.5-flash-lite");
        var model = new TextBox { Text = settings.Model, Padding = new Thickness(8) }; stack.Children.Add(model);
        Label("현재는 Image·Structured outputs·LOW thinking을 지원하는 Gemini 3 계열을 사용하세요. Model ID는 Google AI Studio 기준이며 계정의 사용 권한에 따릅니다. 원본 Image와 맥락을 Google로 전송합니다.");
        var autoAi = new CheckBox { Content = "자동 AI 문서화 사용 (Google API 한도·Credits 사용)", IsChecked = settings.AutomaticAi, Margin = new Thickness(0, 8, 0, 8) }; stack.Children.Add(autoAi);
        Label("Google AI Studio에서 발급한 API key를 입력하세요. Google AI Pro 구독만으로 API가 무제한 제공되지는 않습니다. 무료 한도·Cloud credits 적용은 Google Billing 설정에 따릅니다. Drive 폴더 저장에는 API Key가 필요 없습니다.");
        Label("AI 실행 간 저장 횟수 (기본 20)"); var count = new TextBox { Text = settings.AiEverySaves.ToString(), Padding = new Thickness(8) }; stack.Children.Add(count);
        Label("Google Drive 동기화 폴더 (이 안에 ThoughtCanvas 폴더를 만듭니다 · 쓰지 않으면 비워 두세요)");
        var folder = new TextBox { Text = settings.DriveSyncFolder, Padding = new Thickness(8) }; stack.Children.Add(folder);
        stack.Children.Add(Button("폴더 선택", () => { var picker = new Microsoft.Win32.OpenFolderDialog(); if (picker.ShowDialog(window) == true) folder.Text = picker.FolderName; }));
        var autoDrive = new CheckBox { Content = "변경된 현재 Board를 60초 간격으로 Drive 폴더에 저장", IsChecked = settings.AutomaticFolderBackup, Margin = new Thickness(0, 15, 0, 8) }; stack.Children.Add(autoDrive);
        Label("OAuth 설정은 필요 없습니다. 작업 원본은 Local에 저장하고 이 폴더에는 복사본을 만듭니다. Cloud 업로드 완료 여부는 Google Drive에서 확인하세요. 접근할 수 없으면 Local 원본을 유지하고 다음 주기에 다시 시도합니다.");
        Label("Notion (선택) · 문서 전송용 Token · 이 기기에 암호화하여 저장");
        var notionToken = new PasswordBox { Password = Secrets.Unprotect(settings.ProtectedNotionToken), Padding = new Thickness(8) }; stack.Children.Add(notionToken);
        Label("Notion 상위 Page ID · 해당 Page에 Connection 권한을 부여하세요.");
        var notionParent = new TextBox { Text = settings.NotionParentId, Padding = new Thickness(8) }; stack.Children.Add(notionParent);
        bool saved = false;
        var save = Button("설정 저장", () =>
        {
            if (!ThoughtCanvas.Settings.IsSupportedModel(model.Text.Trim())) { Message(window, "Model 확인", "gemini-3.8-flash와 같은 Gemini 3 계열 Model ID를 입력하세요."); return; }
            if (!int.TryParse(count.Text, out int n) || n < 1 || n > 1000) { Message(window, "입력 확인", "저장 횟수는 1~1000 사이로 입력하세요."); return; }
            if (folder.Text.Trim().Length > 0 && !Path.IsPathFullyQualified(folder.Text.Trim())) { Message(window, "폴더 확인", "Drive 폴더의 전체 경로를 입력하거나 비워 두세요."); return; }
            settings.Model = model.Text.Trim();
            if (!string.IsNullOrWhiteSpace(notionParent.Text) && !Guid.TryParse(notionParent.Text.Trim(), out _)) { Message(window, "Page ID 확인", "Notion Page ID를 입력하세요 (URL의 32자리 ID)."); return; }
            settings.ProtectedNotionToken = Secrets.Protect(notionToken.Password.Trim()); settings.NotionParentId = notionParent.Text.Trim();
            settings.DeviceName = string.IsNullOrWhiteSpace(device.Text) ? Environment.MachineName : device.Text.Trim(); settings.ProtectedGeminiApiKey = Secrets.Protect(key.Password.Trim()); settings.AutomaticAi = autoAi.IsChecked == true; settings.AiEverySaves = n;
            settings.DriveSyncFolder = folder.Text.Trim(); settings.AutomaticFolderBackup = autoDrive.IsChecked == true; saved = true; window.DialogResult = true;
        }); DockPanel.SetDock(save, Dock.Bottom); dock.Children.Add(save); dock.Children.Add(scroll); window.ShowDialog(); return saved;
    }
    public static string? Compare(Window owner, string current, string proposed)
    {
        var window = Window(owner, "사용자 문서와 AI 갱신 제안", 1080, 720); var dock = new DockPanel { Margin = new Thickness(18) }; window.Content = dock;
        var currentBox = new TextBox { Text = current, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12), Margin = new Thickness(4) };
        var proposedBox = new TextBox { Text = proposed, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12), Margin = new Thickness(4) };
        string? result = null; var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(Button("나중에", () => window.Close()));
        buttons.Children.Add(Button("AI 제안 적용", () => { result = proposed; window.DialogResult = true; }));
        buttons.Children.Add(Button("왼쪽 문서 유지·수정 저장", () => { result = currentBox.Text; window.DialogResult = true; }));
        DockPanel.SetDock(buttons, Dock.Bottom); dock.Children.Add(buttons);
        buttons.Children.Add(Button("문단별 선택", () =>
        {
            string? merged = SelectParagraphs(window, currentBox.Text, proposed);
            if (merged != null) currentBox.Text = merged;
        }));
        var note = new TextBlock { Text = "왼쪽: 현재 문서 (직접 수정 가능) / 오른쪽: AI 제안\n문단별 선택에서 필요한 문단만 모은 뒤 왼쪽 문서에 반영할 수 있습니다.", Margin = new Thickness(6, 0, 6, 14), TextWrapping = TextWrapping.Wrap }; DockPanel.SetDock(note, Dock.Top); dock.Children.Add(note);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new()); Grid.SetColumn(proposedBox, 1); grid.Children.Add(currentBox); grid.Children.Add(proposedBox); dock.Children.Add(grid); window.ShowDialog(); return result;
    }
    public static string? SelectParagraphs(Window owner, string current, string proposed)
    {
        var window = Window(owner, "문단별 선택 · 필요한 문단을 순서대로 선택", 900, 700);
        var dock = new DockPanel { Margin = new Thickness(16) }; window.Content = dock;
        var stack = new StackPanel(); var checks = new List<CheckBox>();
        foreach (var (name, text) in new[] { ("현재 문서", current), ("AI 제안", proposed) })
        {
            stack.Children.Add(new TextBlock { Text = name, FontSize = 20, Margin = new Thickness(8) });
            foreach (var paragraph in TopicDocuments.Paragraphs(text))
            {
                var check = new CheckBox { Content = new TextBlock { Text = paragraph, TextWrapping = TextWrapping.Wrap }, Tag = paragraph, Margin = new Thickness(8), IsChecked = name == "현재 문서" }; checks.Add(check); stack.Children.Add(check);
            }
        }
        string? result = null;
        var save = Button("선택한 문단을 왼쪽 문서에 반영", () => { result = TopicDocuments.MergeParagraphs("", checks.Where(c => c.IsChecked == true).Select(c => (string)c.Tag), false); window.DialogResult = true; });
        DockPanel.SetDock(save, Dock.Bottom); dock.Children.Add(save); dock.Children.Add(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); window.ShowDialog(); return result;
    }
    public static bool EditIdeas(Window owner, List<Idea> ideas)
    {
        var originals = ideas.ToDictionary(i => i.Id, DataJson.Write);
        var window = Window(owner, "분류·맥락 수정", 1100, 720); var dock = new DockPanel { Margin = new Thickness(16) }; window.Content = dock;
        var grid = new DataGrid { ItemsSource = ideas, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false };
        foreach (var (label, property) in new[] { ("대주제", "Topic"), ("분류", "Category"), ("내용", "Content"), ("맥락", "Context"), ("이유", "Reason") })
            grid.Columns.Add(new DataGridTextColumn { Header = label, Binding = new System.Windows.Data.Binding(property), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridComboBoxColumn { Header = "상태", ItemsSource = new[] { "active", "cancelled_or_deferred", "uncertain" }, SelectedItemBinding = new System.Windows.Data.Binding("Status") });
        var save = Button("수정 저장", () => { grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true); foreach (var idea in ideas.Where(i => DataJson.Write(i) != originals.GetValueOrDefault(i.Id))) idea.Evidence = "user_confirmed"; window.DialogResult = true; }); DockPanel.SetDock(save, Dock.Bottom); dock.Children.Add(save); dock.Children.Add(grid); return window.ShowDialog() == true;
    }
    public static bool PreviewBoard(Window owner, BoardSurface preview)
    {
        var window = Window(owner, "주제별 Canvas 정돈 미리보기", 1100, 760); var dock = new DockPanel(); window.Content = dock;
        var save = Button("이 배치 적용 (Undo 가능)", () => window.DialogResult = true); DockPanel.SetDock(save, Dock.Bottom); dock.Children.Add(save); dock.Children.Add(preview);
        window.Loaded += (_, _) => preview.Fit(); return window.ShowDialog() == true;
    }
    public static void ReadDocument(Window owner, string title, string text)
    {
        var window = Window(owner, title, 850, 760);
        var doc = new System.Windows.Documents.FlowDocument { FontFamily = new FontFamily("Malgun Gothic"), FontSize = 16, PagePadding = new Thickness(32), Foreground = (Brush)Application.Current.FindResource("TextBrush"), Background = (Brush)Application.Current.FindResource("CanvasBrush") };
        NoteMarkdown.Append(doc, text);
        window.Content = new FlowDocumentScrollViewer { Document = doc, IsToolBarVisible = false }; window.ShowDialog();
    }
}
