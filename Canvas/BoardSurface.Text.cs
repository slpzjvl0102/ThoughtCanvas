using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;

namespace ThoughtCanvas;

public partial class BoardSurface : Canvas
{
    public static FormattedText Format(TextItem t) => new(t.Text.Length == 0 ? " " : t.Text, CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight, new Typeface("Malgun Gothic"), t.FontSize, Brushes.DarkSlateGray, 1)
    { MaxTextWidth = t.Width, Trimming = TextTrimming.None };
    public static Rect TextBounds(TextItem t) => new(t.X, t.Y, t.Width, Math.Max(t.FontSize * 1.5, Format(t).Height));
    // v0.6.6 Text 줄바꿈: Enter로만 줄을 바꾸고 박스 너비는 내용에 맞춥니다.
    private const double TextWidthMargin = 4;
    private static FormattedText Unwrapped(string text, double fontSize) => new(text.Length == 0 ? " " : text, CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight, new Typeface("Malgun Gothic"), fontSize, Brushes.DarkSlateGray, 1);
    /// <summary>자동 줄바꿈 없이 그렸을 때 가장 긴 줄의 너비.</summary>
    public static double NaturalWidth(string text, double fontSize) => Unwrapped(text, fontSize).WidthIncludingTrailingWhitespace;
    /// <summary>현재 박스 너비 안에서 자동 줄바꿈 없이 그려지는지 (Enter로만 줄을 바꾼 Text인지).</summary>
    public static bool FitsWithoutWrap(TextItem t) => NaturalWidth(t.Text, t.FontSize) <= t.Width + .5;
    /// <summary>이전 버전에서 박스 너비로 자동 줄바꿈되던 Text를 같은 모양의 명시적 줄바꿈으로 바꿉니다.</summary>
    public static string HardWrap(TextItem t)
    {
        if (FitsWithoutWrap(t)) return t.Text;
        var lines = new List<string>();
        foreach (var paragraph in t.Text.Replace("\r\n", "\n").Split('\n'))
        {
            string rest = paragraph;
            while (rest.Length > 0 && NaturalWidth(rest, t.FontSize) > t.Width)
            {
                int fit = 1;
                for (int lo = 1, hi = rest.Length; lo <= hi;)
                {
                    int mid = (lo + hi) / 2;
                    if (NaturalWidth(rest[..mid], t.FontSize) <= t.Width) { fit = mid; lo = mid + 1; } else hi = mid - 1;
                }
                int cut = fit;
                // 라틴 문자 단어 중간이면 앞의 공백에서 끊습니다. 한글은 WPF처럼 글자 단위로 끊습니다.
                if (cut < rest.Length && rest[cut] != ' ' && !IsHangul(rest[cut - 1]))
                {
                    int space = rest.LastIndexOf(' ', cut - 1);
                    if (space > 0) cut = space + 1;
                }
                lines.Add(rest[..cut].TrimEnd()); rest = rest[cut..].TrimStart(' ');
            }
            lines.Add(rest);
        }
        return string.Join("\n", lines);
    }
    private static bool IsHangul(char c) => c is >= '\uAC00' and <= '\uD7A3' or >= '\u1100' and <= '\u11FF' or >= '\u3130' and <= '\u318F';
    private static void FitTextWidth(TextItem t) => t.Width = Math.Max(20, NaturalWidth(t.Text, t.FontSize) + TextWidthMargin);
    private TextItem DetachText(TextItem text)
    {
        int index = Board.Texts.IndexOf(text); if (index < 0) return text;
        var copy = DataJson.Clone(text); Board.Texts[index] = copy; return copy;
    }
    private void OpenEditor(TextItem text)
    {
        text = DetachText(text); editedText = text; var p = ToScreen(new(text.X, text.Y));
        // 자동 줄바꿈 없이 입력한 만큼 늘어납니다. 이전 버전의 자동 줄바꿈 Text는 보이던 모양대로 줄을 나눠 엽니다.
        editorInitialText = HardWrap(text); editorInitialFontSize = text.FontSize;
        editor = new TextBox { Text = editorInitialText, FontFamily = new FontFamily("Malgun Gothic"), FontSize = text.FontSize * Board.Zoom, MinWidth = 80, MinHeight = 35, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, BorderBrush = Brushes.Teal, BorderThickness = new Thickness(1), Padding = new Thickness(2), Background = Brushes.White };
        editor.Background = DarkMode ? new SolidColorBrush(Color.FromRgb(32, 38, 46)) : Brushes.White; editor.Foreground = DarkMode ? Brushes.White : Brushes.DarkSlateGray;
        // 입력 상자의 테두리·여백만큼 당겨서 입력 중 글자 위치가 완성된 Text와 겹치도록 합니다.
        SetLeft(editor, p.X - 5); SetTop(editor, p.Y - 3); Children.Add(editor);
        editor.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape || (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))) { CommitEditor(); e.Handled = true; } };
        editor.TextChanged += (_, _) => DraftChanged?.Invoke();
        editor.LostKeyboardFocus += (_, e) => { if (editor != null && KeepEditorOpen?.Invoke(e.NewFocus) != true) CommitEditor(); };
        editor.Focus(); editor.CaretIndex = editor.Text.Length; Redraw();
        ToolChanged?.Invoke(); // 크기 슬라이더가 편집 중인 Text 크기를 표시하도록 갱신
    }
    public void CommitEditor()
    {
        if (editor == null || editedText == null) return;
        var box = editor; var text = editedText; editor = null; editedText = null;
        // 내용·크기가 그대로면 원래 Text와 너비를 유지합니다 (열어 보기만 해도 Undo 기록이 생기지 않도록).
        if (box.Text != editorInitialText || text.FontSize != editorInitialFontSize) { text.Text = box.Text; FitTextWidth(text); }
        Children.Remove(box); if (string.IsNullOrWhiteSpace(text.Text)) Board.Texts.Remove(text);
        // Board 전체를 직렬화해 비교하지 않고, 편집한 Text의 변화만 봅니다 (새 Text를 비운 채 닫으면 기록 없음).
        bool isNew = before != null && !before.Texts.Any(t => t.Id == text.Id);
        bool changed = (box.Text != editorInitialText || text.FontSize != editorInitialFontSize) && !(isNew && string.IsNullOrWhiteSpace(text.Text));
        if (before != null && changed) { History.Push(before); Changed?.Invoke(); }
        before = null; Redraw(); ToolChanged?.Invoke();
    }
}
