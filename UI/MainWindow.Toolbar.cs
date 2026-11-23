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
    private void ToolbarModeClick(object sender, RoutedEventArgs e)
    {
        settings.ToolbarLayout = settings.ToolbarLayout switch { "Auto" => "Horizontal", "Horizontal" => "Vertical", _ => "Auto" };
        store.SaveSettings(settings); UpdateToolbarLayout();
    }
    private void UpdateToolbarLayout()
    {
        ToolbarModeButton.Content = settings.ToolbarLayout switch { "Horizontal" => "툴바 가로", "Vertical" => "툴바 세로", _ => "툴바 자동" };
        bool vertical = settings.ToolbarLayout switch { "Horizontal" => false, "Vertical" => true, _ => ActualWidth > 0 && ActualHeight > ActualWidth };
        if (verticalTools != vertical) ApplyToolbarLayout(vertical);
        // 좁은 창에서는 상단 오른쪽 버튼들을 두 번째 줄로 내립니다.
        bool stacked = ActualWidth > 0 && ActualWidth < 920;
        if (stackedHeader != stacked)
        {
            stackedHeader = stacked; Grid.SetRow(HeaderActions, stacked ? 1 : 0); Grid.SetColumn(HeaderActions, stacked ? 0 : 1);
            HeaderActions.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Stretch; HeaderActions.Margin = new Thickness(0, stacked ? 8 : 0, 0, 0);
        }
    }
    internal void ApplyToolbarLayout(bool vertical)
    {
        verticalTools = vertical;
        ((Panel)ToolsBar.Parent).Children.Remove(ToolsBar);
        if (vertical) { ContentGrid.Children.Add(ToolsBar); Grid.SetRow(ToolsBar, 0); Grid.SetColumn(ToolsBar, 1); }
        else { RootGrid.Children.Add(ToolsBar); Grid.SetRow(ToolsBar, 1); Grid.SetColumn(ToolsBar, 0); }
        var flow = vertical ? Orientation.Vertical : Orientation.Horizontal;
        ToolsBar.BorderThickness = vertical ? new Thickness(1, 0, 0, 0) : new Thickness(0, 0, 0, 1);
        ToolsDock.Margin = vertical ? new Thickness(8, 10, 8, 10) : new Thickness(16, 8, 16, 8);
        DockPanel.SetDock(ToolsActions, vertical ? Dock.Bottom : Dock.Right);
        ToolsActions.Orientation = ToolsWrap.Orientation = SizeStack.Orientation = SizeSlider.Orientation = flow;
        ToolsActions.VerticalAlignment = vertical ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        ToolsWrap.ItemWidth = vertical ? 104 : double.NaN;
        SizeSlider.Width = vertical ? double.NaN : 95; SizeSlider.Height = vertical ? 120 : double.NaN;
        SizeSlider.HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        SizeCaption.Margin = vertical ? new Thickness(0, 6, 0, 4) : new Thickness(14, 0, 5, 0);
        SizeCaption.HorizontalAlignment = SizeLabel.HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        SizeLabel.Margin = vertical ? new Thickness(0, 4, 0, 0) : new Thickness(6, 0, 0, 0); SizeLabel.TextAlignment = vertical ? TextAlignment.Center : TextAlignment.Left;
    }
    private void SelectTool(object sender, RoutedEventArgs e) { Surface.CommitEditor(); Surface.EndGesture(); Surface.Tool = Enum.Parse<CanvasTool>((string)((Button)sender).Tag); UpdateTool(); }
    private void UpdateTool()
    {
        foreach (var button in new[] { PenButton, EraserButton, LassoButton, TextButton }) button.SetResourceReference(Control.BackgroundProperty, (string)button.Tag == Surface.Tool.ToString() ? "SelectedBrush" : "SurfaceBrush");
        // 선택 크기 버튼은 Lasso일 때만 보입니다 (v0.7.3).
        GrowButton.Visibility = ShrinkButton.Visibility = Surface.Tool == CanvasTool.Lasso ? Visibility.Visible : Visibility.Collapsed;
        bool text = Surface.SizeTargetsText;
        updating = true;
        SizeSlider.IsSnapToTickEnabled = text; SizeSlider.TickFrequency = 1;
        SizeSlider.Minimum = text ? 8 : 1; SizeSlider.Maximum = text ? 72 : Surface.Tool == CanvasTool.Eraser ? 100 : 12;
        SizeSlider.Value = text ? Surface.CurrentTextSize : Surface.Tool == CanvasTool.Eraser ? Surface.EraserSize : Surface.PenWidth;
        SizeLabel.Text = SizeSlider.Value.ToString("0.#"); updating = false; UpdateSizePreview();
    }
    private void ToolSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!initialized || updating) return;
        // Text Tool 또는 Text 편집 중에는 글자 크기를 바꿉니다 (새 Text 기본값 + 편집 중인 Text에 즉시 적용).
        if (Surface.SizeTargetsText) { double size = Math.Round(e.NewValue); Surface.TextSize = size; Surface.SetEditingFontSize(size); }
        else if (Surface.Tool == CanvasTool.Eraser) Surface.EraserSize = e.NewValue; else Surface.PenWidth = e.NewValue;
        SizeLabel.Text = e.NewValue.ToString("0.#"); UpdateSizePreview();
    }
    private void UpdateSizePreview()
    {
        bool text = Surface.SizeTargetsText;
        bool sized = text || Surface.Tool is CanvasTool.Pen or CanvasTool.Eraser;
        SizeControls.IsEnabled = sized;
        if (text)
        {
            double fontSize = Surface.CurrentTextSize;
            SizePreview.Visibility = Visibility.Collapsed; SizePreviewText.Visibility = Visibility.Visible; SizePreviewText.FontSize = Math.Min(26, fontSize);
            SizeControls.ToolTip = $"글자 크기 {fontSize:0} · 새 Text와 편집 중인 Text에 적용됩니다. 미리보기는 최대 26으로 축소 표시합니다.";
        }
        else
        {
            double size = Surface.Tool == CanvasTool.Eraser ? Surface.EraserSize : Surface.PenWidth;
            SizePreview.Visibility = Visibility.Visible; SizePreviewText.Visibility = Visibility.Collapsed;
            SizePreview.Width = SizePreview.Height = Math.Min(26, size);
            SizePreview.Fill = Surface.Tool == CanvasTool.Eraser ? Brushes.Transparent : (Brush)FindResource("TextBrush");
            SizeControls.ToolTip = $"Canvas 기준 지름 {size:0.#} · 화면에서는 Zoom에 따라 크기가 달라집니다. 미리보기는 최대 26으로 축소 표시합니다.";
        }
        Surface.UpdatePointer();
    }
    private void ToggleFocus(object sender, RoutedEventArgs e)
    {
        Surface.CommitEditor(); Surface.EndGesture(); settings.FocusMode = !settings.FocusMode; ApplyFocus(); store.SaveSettings(settings);
    }
    private void ApplyFocus()
    {
        HeaderBar.Visibility = ToolsBar.Visibility = StatusBar.Visibility = settings.FocusMode ? Visibility.Collapsed : Visibility.Visible;
        StatusRow.Height = new GridLength(settings.FocusMode ? 0 : 34);
        FocusButton.Content = settings.FocusMode ? "UI 표시 · F11" : "UI 숨기기 · F11";
    }
    private void ToggleTheme(object sender, RoutedEventArgs e)
    {
        settings.DarkMode = !settings.DarkMode; ApplyTheme(); UpdateTool(); store.SaveSettings(settings);
    }
    private void ApplyTheme()
    {
        string[] names = ["CanvasBrush", "SurfaceBrush", "ToolbarBrush", "TextBrush", "MutedBrush", "BorderBrush", "AccentBrush", "SelectedBrush"];
        string[] colors = settings.DarkMode
            ? ["#171B21", "#20262E", "#1B2229", "#FFFFFF", "#A3B3BC", "#38444D", "#267E77", "#315B56"]
            : ["#F7F9F8", "#FFFFFF", "#EEF3F1", "#223C36", "#667C74", "#DAE3DF", "#126C66", "#D3EADF"];
        for (int i = 0; i < names.Length; i++) Application.Current.Resources[names[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        Surface.SetTheme(settings.DarkMode); ThemeButton.Content = settings.DarkMode ? "Light mode" : "Dark mode";
    }
    private void UpdateCounts() { CountsLabel.Text = $"Stroke {Surface.Board.Ink.Count} · Text {Surface.Board.Texts.Count} · Undo {Surface.History.UndoCount}/50"; Welcome.Visibility = Surface.Board.Ink.Count + Surface.Board.Texts.Count == 0 ? Visibility.Visible : Visibility.Collapsed; }
    private void UndoClick(object sender, RoutedEventArgs e) => Surface.Undo();
    private void RedoClick(object sender, RoutedEventArgs e) => Surface.Redo();
    private void ShrinkClick(object sender, RoutedEventArgs e) => Surface.ResizeSelection(.9);
    private void GrowClick(object sender, RoutedEventArgs e) => Surface.ResizeSelection(1.1);
    private void FitClick(object sender, RoutedEventArgs e) => Surface.Fit();
    private void ZoomIn(object sender, RoutedEventArgs e) => Surface.ZoomAt(new(Surface.ActualWidth / 2, Surface.ActualHeight / 2), 1.2);
    private void ZoomOut(object sender, RoutedEventArgs e) => Surface.ZoomAt(new(Surface.ActualWidth / 2, Surface.ActualHeight / 2), 1 / 1.2);
    private void ResetZoom(object sender, RoutedEventArgs e) => Surface.ZoomAt(new(Surface.ActualWidth / 2, Surface.ActualHeight / 2), 1 / Surface.Board.Zoom);
    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11) { ToggleFocus(this, e); e.Handled = true; return; }
        if (Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is PasswordBox) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (e.Key == Key.Z) { Surface.Undo(); e.Handled = true; } if (e.Key == Key.Y) { Surface.Redo(); e.Handled = true; } if (e.Key == Key.S) { SaveClick(sender, e); e.Handled = true; }
        }
        else if (e.Key == Key.Delete) { Surface.DeleteSelection(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Surface.Selection.Clear(); Surface.Redraw(); }
    }
    private void OpenRadial(Point p) { radialOrigin = p; radialChoice = null; DrawRadial(); }
    private void MoveRadial(Point p)
    {
        Vector d = p - radialOrigin; radialChoice = d.Length < 20 ? null : Math.Abs(d.X) > Math.Abs(d.Y) ? (d.X > 0 ? CanvasTool.Eraser : CanvasTool.Text) : (d.Y > 0 ? CanvasTool.Lasso : CanvasTool.Pen); DrawRadial();
    }
    private void CloseRadial(Point p) { MoveRadial(p); if (radialChoice.HasValue) { Surface.Tool = radialChoice.Value; UpdateTool(); } RadialLayer.Children.Clear(); }
    private void DrawRadial()
    {
        RadialLayer.Children.Clear(); double radius = 88;
        foreach (var (tool, angle) in new[] { (CanvasTool.Pen, -90d), (CanvasTool.Eraser, 0d), (CanvasTool.Lasso, 90d), (CanvasTool.Text, 180d) })
        {
            Point P(double a, double r) => new(radialOrigin.X + r * Math.Cos(a * Math.PI / 180), radialOrigin.Y + r * Math.Sin(a * Math.PI / 180));
            var geometry = new StreamGeometry(); using (var c = geometry.Open()) { c.BeginFigure(radialOrigin, true, true); c.LineTo(P(angle - 45, radius), true, false); c.ArcTo(P(angle + 45, radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false); }
            RadialLayer.Children.Add(new System.Windows.Shapes.Path { Data = geometry, Fill = tool == radialChoice ? new SolidColorBrush(Color.FromRgb(190, 225, 210)) : Brushes.White, Stroke = Brushes.Teal, StrokeThickness = 1 });
            var label = new TextBlock { Text = tool.ToString(), FontWeight = FontWeights.SemiBold, Foreground = Brushes.DarkSlateGray, Width = 60, TextAlignment = TextAlignment.Center }; var p = P(angle, 54); Canvas.SetLeft(label, p.X - 30); Canvas.SetTop(label, p.Y - 10); RadialLayer.Children.Add(label);
        }
    }
}
