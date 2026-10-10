using Snapaste.Core;
using Snapaste.UI;
namespace Snapaste.Editor;

public sealed class EditorWindow : Window
{
    public EditDocument Document { get; }
    private readonly EditorSurface surface;
    private readonly ScrollViewer viewport = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock status = Ui.Label("", 11, true), context = Ui.Label("", 11, true), zoomLabel = Ui.Label("100%", 11);
    private readonly Dictionary<Tool, Button> toolButtons = [];
    private readonly List<(Color Color, Button Button)> colors = [];
    private readonly StackPanel colorOptions = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel sizeOptions = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock sizeLabel = Ui.Label("线宽", 11, true);
    private readonly Button undo, redo, delete;
    private bool exporting;
    public event Action<EditDocument>? PinRequested;
    public event Action<EditDocument>? Completed;
    public EditorWindow(ImageDocument image, Tool tool = Tool.Select, EditDocument? document = null)
    {
        Document = document ?? new(image); Title = "Snapaste · 编辑"; Width = 1140; Height = 790; MinWidth = 800; MinHeight = 460; WindowStartupLocation = WindowStartupLocation.CenterScreen; Ui.Setup(this);
        var dock = new DockPanel(); Content = dock;
        var top = new StackPanel(); var topBorder = new Border { Child = top, BorderThickness = new Thickness(0, 0, 0, 1) }; topBorder.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); topBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); DockPanel.SetDock(topBorder, Dock.Top); dock.Children.Add(topBorder);
        var header = new DockPanel { Margin = new Thickness(18, 10, 18, 6), LastChildFill = false }; top.Children.Add(header);
        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; title.Children.Add(Ui.Label(image.Kind == "Scroll" ? "长截图编辑" : "截图编辑", 17)); var hint = Ui.Label("标注后，复制或留作参考。", 11, true); hint.Margin = new Thickness(0, 3, 0, 0); title.Children.Add(hint); header.Children.Add(title);
        var output = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(output, Dock.Right); header.Children.Add(output);
        output.Children.Add(Ui.Action("Pin", Strings.T("Pin"), () => { Completed?.Invoke(Document); PinRequested?.Invoke(Document); Close(); }));
        output.Children.Add(Ui.Action("Save", Strings.T("Save"), Save, hint: "保存图片 · Ctrl+S"));
        output.Children.Add(Ui.Tool("Copy", "仅复制", () => Copy(false), "复制并继续编辑 · Ctrl+C"));
        output.Children.Add(Ui.Action("Done", "复制并完成", () => Copy(true), true, "复制到剪贴板并关闭 · Enter"));
        surface = new EditorSurface(Document) { CurrentTool = tool }; viewport.Content = surface; viewport.SetResourceReference(BackgroundProperty, "CanvasBrush");
        var tools = new WrapPanel { Margin = new Thickness(16, 0, 16, 0) }; top.Children.Add(tools);
        foreach (Tool t in Enum.GetValues<Tool>())
        {
            if (t is Tool.Rectangle or Tool.Pen or Tool.Mosaic or Tool.Crop) tools.Children.Add(Ui.Divider());
            var value = t; var b = Ui.Tool(t.ToString(), Strings.T(t.ToString()), () => Select(value), Strings.T(t.ToString()) + " · " + Shortcut(t)); toolButtons.Add(t, b); tools.Children.Add(b);
        }
        tools.Children.Add(Ui.Divider()); delete = Ui.Tool("Delete", "删除标注", () => { surface.Delete(); Update(); }, "删除选中的标注 · Delete"); tools.Children.Add(delete);
        undo = Ui.Tool("Undo", "撤销", () => { Document.Undo(); Update(); }, "撤销 · Ctrl+Z"); redo = Ui.Tool("Redo", "重做", () => { Document.Redo(); Update(); }, "重做 · Ctrl+Y"); tools.Children.Add(undo); tools.Children.Add(redo);
        var options = new WrapPanel { Margin = new Thickness(18, 7, 18, 10), MinHeight = 28 }; top.Children.Add(options); options.Children.Add(colorOptions);
        foreach (var value in new[] { "#E95D75", "#EFA25E", "#EAC858", "#50A78B", "#5D93D8", "#9571CA", "#FFFFFF", "#302C38" })
        {
            var color = (Color)ColorConverter.ConvertFromString(value); var b = Ui.Button("颜色 " + value, () => { surface.Ink = color; UpdateStatus(); }); b.Content = new System.Windows.Shapes.Ellipse { Width = 15, Height = 15, Fill = new SolidColorBrush(color), Stroke = Ui.Brush("BorderBrush"), StrokeThickness = 1 }; b.Padding = new Thickness(5); b.MinHeight = 26; b.Width = 26; b.Height = 26; b.Margin = new Thickness(2); colors.Add((color, b)); colorOptions.Children.Add(b);
        }
        sizeLabel.Margin = new Thickness(14, 0, 6, 0); sizeLabel.VerticalAlignment = VerticalAlignment.Center; sizeOptions.Children.Add(sizeLabel);
        var size = new Slider { Minimum = 2, Maximum = 40, Value = 5, Width = 105, VerticalAlignment = VerticalAlignment.Center }; size.ValueChanged += (_, _) => surface.InkSize = size.Value; sizeOptions.Children.Add(size); options.Children.Add(sizeOptions);
        context.VerticalAlignment = VerticalAlignment.Center; context.Margin = new Thickness(18, 0, 0, 0); options.Children.Add(context);
        var footer = new DockPanel { Margin = new Thickness(18, 7, 18, 7), LastChildFill = false }; DockPanel.SetDock(footer, Dock.Bottom); dock.Children.Add(footer); status.VerticalAlignment = VerticalAlignment.Center; footer.Children.Add(status);
        var zoom = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(zoom, Dock.Right); footer.Children.Add(zoom);
        zoom.Children.Add(Ui.Tool("Minus", "缩小", () => Zoom(surface.Zoom / 1.25))); zoomLabel.VerticalAlignment = VerticalAlignment.Center; zoomLabel.Width = 48; zoomLabel.TextAlignment = TextAlignment.Center; zoom.Children.Add(zoomLabel); zoom.Children.Add(Ui.Tool("Plus", "放大", () => Zoom(surface.Zoom * 1.25))); zoom.Children.Add(Ui.Button("100%", () => Zoom(1))); zoom.Children.Add(Ui.Tool("Fit", "适合宽度", Fit));
        dock.Children.Add(viewport);
        viewport.ScrollChanged += (_, _) => RefreshViewport(); surface.Changed += Update;
        PreviewKeyDown += (_, e) =>
        {
            if (e.OriginalSource is TextBox or ComboBox) return;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                if (e.Key == Key.Z) { Document.Undo(); Update(); e.Handled = true; }
                if (e.Key == Key.Y) { Document.Redo(); Update(); e.Handled = true; }
                if (e.Key == Key.S) { Save(); e.Handled = true; }
                if (e.Key == Key.C) { Copy(false); e.Handled = true; }
            }
            else if (Keyboard.Modifiers == ModifierKeys.None)
            {
                if (e.Key == Key.Delete) { surface.Delete(); Update(); e.Handled = true; }
                else if (e.Key == Key.Enter) { Copy(true); e.Handled = true; }
                else if (KeyTool(e.Key) is Tool selected) { Select(selected); e.Handled = true; }
            }
        };
        Loaded += (_, _) => { Fit(); Select(tool); }; Update();
    }
    private static string Shortcut(Tool tool) => tool switch { Tool.Select => "V", Tool.Rectangle => "R", Tool.Ellipse => "E", Tool.Line => "L", Tool.Arrow => "A", Tool.Pen => "P", Tool.Highlighter => "H", Tool.Text => "T", Tool.Mosaic => "M", Tool.Blur => "B", Tool.Crop => "X", Tool.Color => "I", _ => "" };
    private static Tool? KeyTool(Key key) => key switch { Key.V => Tool.Select, Key.R => Tool.Rectangle, Key.E => Tool.Ellipse, Key.L => Tool.Line, Key.A => Tool.Arrow, Key.P => Tool.Pen, Key.H => Tool.Highlighter, Key.T => Tool.Text, Key.M => Tool.Mosaic, Key.B => Tool.Blur, Key.X => Tool.Crop, Key.I => Tool.Color, _ => null };
    private void Select(Tool tool) { surface.CurrentTool = tool; surface.Cursor = tool == Tool.Select ? Cursors.Arrow : Cursors.Cross; surface.Focus(); UpdateStatus(); }
    private async void Copy(bool close)
    {
        if (exporting) return; exporting = true;
        try { await ImageExport.Copy(Document); Completed?.Invoke(Document); status.Text = "已复制到剪贴板 · 可以继续编辑"; status.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush"); if (close) Close(); } catch (Exception e) { Ui.Error(e); }
        finally { exporting = false; }
    }
    private async void Save()
    {
        if (exporting) return; exporting = true;
        try { status.Text = "正在导出图片…"; if (await ImageExport.SaveDialogAsync(Document, App.Current.Settings, this)) { Completed?.Invoke(Document); status.Text = "图片已保存"; status.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush"); } else UpdateStatus(); }
        catch (Exception e) { Ui.Error(e); UpdateStatus(); } finally { exporting = false; }
    }
    private void Fit() => Zoom(Math.Min(1, Math.Max(.05, (viewport.ActualWidth - 40) / Document.Crop.Width)));
    private void Zoom(double value) { surface.Zoom = Math.Clamp(value, .02, 8); Update(); }
    private void RefreshViewport() { surface.Visible = new Rect(Math.Max(0, viewport.HorizontalOffset - 20) / surface.Zoom + Document.Crop.X, Math.Max(0, viewport.VerticalOffset - 20) / surface.Zoom + Document.Crop.Y, Math.Max(1, viewport.ViewportWidth) / surface.Zoom, Math.Max(1, viewport.ViewportHeight) / surface.Zoom); surface.InvalidateVisual(); }
    private void Update() { surface.Width = Document.Crop.Width * surface.Zoom; surface.Height = Document.Crop.Height * surface.Zoom; surface.Margin = new Thickness(20); RefreshViewport(); UpdateStatus(); }
    private void UpdateStatus()
    {
        foreach (var pair in toolButtons) pair.Value.Tag = surface.CurrentTool == pair.Key ? "Selected" : null;
        foreach (var color in colors) color.Button.BorderBrush = color.Color == surface.Ink ? Ui.Accent : Brushes.Transparent;
        undo.IsEnabled = Document.CanUndo; redo.IsEnabled = Document.CanRedo; delete.IsEnabled = surface.HasSelection;
        var tool = surface.CurrentTool; colorOptions.Visibility = tool is Tool.Select or Tool.Crop or Tool.Mosaic or Tool.Blur ? Visibility.Collapsed : Visibility.Visible; sizeOptions.Visibility = tool is Tool.Select or Tool.Crop or Tool.Text or Tool.Color ? Visibility.Collapsed : Visibility.Visible;
        sizeLabel.Text = tool == Tool.Mosaic ? "块大小" : tool == Tool.Blur ? "模糊强度" : "线宽";
        context.Text = tool switch { Tool.Select => "点击选择 · 拖动移动 · 双击文字修改", Tool.Crop => "拖动保留区域 · Ctrl+Z 撤销裁剪", Tool.Text => "点击画面输入文字", Tool.Color => "点击像素复制 HEX 颜色", Tool.Mosaic => "拖动覆盖需要隐藏的内容", Tool.Blur => "拖动选择局部模糊范围", _ => "拖动绘制 · Ctrl+Z 撤销" };
        status.Text = $"{Document.Crop.Width:0} × {Document.Crop.Height:0} 像素   ·   {Document.Annotations.Count} 个标注"; status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); zoomLabel.Text = $"{surface.Zoom:P0}";
    }
}
public sealed class EditorSurface : FrameworkElement
{
    private readonly EditDocument document;
    public Tool CurrentTool { get; set; }
    public Color Ink { get; set; } = Color.FromRgb(233, 93, 117);
    public double InkSize { get; set; } = 5;
    public double Zoom { get; set; } = 1;
    public Rect Visible { get; set; } = new(0, 0, 2000, 2000);
    // The desktop overlay supplies its own frozen background; editing remains shared.
    public bool DrawBaseImage { get; set; } = true;
    public Annotation? HiddenAnnotation { get; set; }
    public string TextFont { get; set; } = "Microsoft YaHei UI";
    public double TextSize { get; set; } = 24;
    public bool TextBold { get; set; }
    public bool TextBackground { get; set; }
    private Annotation? active, selected;
    private Point start, last;
    private bool resize;
    private Rect cropDrag = Rect.Empty;
    public bool HasSelection => selected != null && document.Annotations.Contains(selected);
    public event Action? Changed;
    public event Action<Annotation, bool>? TextRequested;
    public event Action<MouseButtonEventArgs>? EmptySelectionDown;
    public EditorSurface(EditDocument document)
    {
        this.document = document; Focusable = true; Cursor = Cursors.Cross;
        MouseLeftButtonDown += Down; MouseMove += Move; MouseLeftButtonUp += Up;
    }
    private Point Pixel(MouseEventArgs e) { var p = e.GetPosition(this); return new(Math.Clamp(p.X / Zoom + document.Crop.X, document.Crop.Left, document.Crop.Right), Math.Clamp(p.Y / Zoom + document.Crop.Y, document.Crop.Top, document.Crop.Bottom)); }
    public void Delete() { if (selected != null && document.Annotations.Contains(selected)) { document.Checkpoint(); document.Annotations.Remove(selected); selected = null; InvalidateVisual(); Changed?.Invoke(); } }
    public void ClearSelection() { active = selected = null; InvalidateVisual(); }
    public bool MoveSelection(Vector delta)
    {
        if (!HasSelection) return false;
        document.Checkpoint(); selected!.Translate(delta); InvalidateVisual(); Changed?.Invoke(); return true;
    }
    private void Down(object sender, MouseButtonEventArgs e)
    {
        Focus(); start = last = Pixel(e); cropDrag = Rect.Empty;
        if (CurrentTool == Tool.Color)
        {
            var b = document.Raster(new Rect(Math.Clamp((int)start.X, 0, document.Image.Width - 1), Math.Clamp((int)start.Y, 0, document.Image.Height - 1), 1, 1)); var bytes = new byte[4]; b.CopyPixels(bytes, 4, 0); Ink = Color.FromRgb(bytes[2], bytes[1], bytes[0]); Clipboard.SetText($"#{Ink.R:X2}{Ink.G:X2}{Ink.B:X2}"); Changed?.Invoke(); return;
        }
        if (CurrentTool == Tool.Select)
        {
            resize = HasSelection && (selected!.Bounds.BottomRight - start).Length < 12 / Zoom;
            if (!resize) selected = document.Annotations.LastOrDefault(a => { var r = a.Bounds; r.Inflate(8 / Zoom, 8 / Zoom); return r.Contains(start); });
            if (selected != null)
            {
                if (e.ClickCount == 2 && selected.Kind == Tool.Text) { EditText(selected, false); return; }
                document.Checkpoint(); active = selected;
            }
            else if (EmptySelectionDown != null) { EmptySelectionDown(e); Changed?.Invoke(); return; }
        }
        else if (CurrentTool == Tool.Text)
        {
            var a = NewAnnotation(); EditText(a, true); return;
        }
        else if (CurrentTool != Tool.Crop)
        {
            document.Checkpoint(); active = NewAnnotation(); document.Annotations.Add(active); selected = active;
        }
        CaptureMouse(); InvalidateVisual();
    }
    private Annotation NewAnnotation() => new() { Kind = CurrentTool, Color = Ink, Stroke = Math.Max(1, InkSize / 2), EffectSize = (int)InkSize * 2, Bounds = new Rect(start, new Size(1, 1)), Points = [start], Font = TextFont, FontSize = TextSize, Bold = TextBold, Background = TextBackground };
    private void Move(object sender, MouseEventArgs e)
    {
        if (!IsMouseCaptured) return; var p = Pixel(e);
        if (CurrentTool == Tool.Crop) cropDrag = new Rect(start, p);
        else if (active != null)
        {
            if (CurrentTool == Tool.Select)
            {
                if (resize) active.Resize(new Rect(active.Bounds.TopLeft, new Size(Math.Max(2, p.X - active.Bounds.Left), Math.Max(2, p.Y - active.Bounds.Top)))); else active.Translate(p - last);
            }
            else
            {
                if (CurrentTool is Tool.Pen or Tool.Highlighter) { active.Points.Add(p); var bounds = active.Bounds; bounds.Union(p); active.Bounds = bounds; }
                else { active.Bounds = new Rect(start, p); active.Points = [start, p]; }
            }
        }
        last = p; InvalidateVisual();
    }
    private void Up(object sender, MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return; ReleaseMouseCapture();
        if (CurrentTool == Tool.Crop && !cropDrag.IsEmpty && cropDrag.Width >= 2 && cropDrag.Height >= 2)
        {
            document.Checkpoint(); document.Crop = new Rect(Math.Floor(cropDrag.X), Math.Floor(cropDrag.Y), Math.Floor(cropDrag.Width), Math.Floor(cropDrag.Height)); cropDrag = Rect.Empty;
        }
        active = null; Changed?.Invoke(); InvalidateVisual();
    }
    private void EditText(Annotation annotation, bool add)
    {
        if (TextRequested != null) { TextRequested(annotation, add); return; }
        var dialog = new TextDialog(annotation) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        var draft = annotation.Clone(); draft.Text = dialog.Value; draft.FontSize = dialog.FontSizeValue; draft.Font = dialog.FontName; draft.Bold = dialog.IsBold; draft.Background = dialog.HasBackground;
        ApplyText(annotation, draft, add);
    }
    public void ApplyText(Annotation annotation, Annotation draft, bool add)
    {
        if (add && string.IsNullOrWhiteSpace(draft.Text)) return;
        document.Checkpoint(); annotation.Text = draft.Text; annotation.FontSize = draft.FontSize; annotation.Font = draft.Font; annotation.Color = draft.Color; annotation.Bold = draft.Bold; annotation.Background = draft.Background;
        var formatted = new FormattedText(annotation.Text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily(annotation.Font), FontStyles.Normal, annotation.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal), annotation.FontSize, Brushes.Black, 1);
        annotation.Bounds = new Rect(annotation.Bounds.Location, new Size(Math.Max(1, formatted.WidthIncludingTrailingWhitespace), Math.Max(1, formatted.Height)));
        if (add) document.Annotations.Add(annotation);
        else if (string.IsNullOrWhiteSpace(annotation.Text)) document.Annotations.Remove(annotation);
        selected = annotation; Changed?.Invoke(); InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        dc.PushTransform(new ScaleTransform(Zoom, Zoom)); dc.PushTransform(new TranslateTransform(-document.Crop.X, -document.Crop.Y));
        if (DrawBaseImage) document.Draw(dc, Visible); else document.DrawAnnotations(dc, Visible, HiddenAnnotation);
        if (selected != null && document.Annotations.Contains(selected))
        {
            var pen = new System.Windows.Media.Pen(Ui.Accent, 1 / Zoom) { DashStyle = DashStyles.Dash }; dc.DrawRectangle(null, pen, selected.Bounds);
            dc.DrawRectangle(Brushes.White, pen, new Rect(selected.Bounds.Right - 4 / Zoom, selected.Bounds.Bottom - 4 / Zoom, 8 / Zoom, 8 / Zoom));
        }
        if (!cropDrag.IsEmpty) dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(30, 0, 100, 255)), new System.Windows.Media.Pen(Ui.Accent, 1 / Zoom), cropDrag);
        dc.Pop(); dc.Pop();
    }
}

internal sealed class TextDialog : Window
{
    private readonly TextBox text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ComboBox font = new() { Width = 200, IsEditable = true };
    private readonly TextBox size = new() { Width = 50 };
    private readonly CheckBox bold = new() { Content = Strings.T("Bold"), Margin = new Thickness(8) }, background = new() { Content = Strings.T("Background"), Margin = new Thickness(8) };
    public string Value => text.Text;
    public string FontName => string.IsNullOrWhiteSpace(font.Text) ? "Microsoft YaHei UI" : font.Text;
    public double FontSizeValue => double.TryParse(size.Text, out var s) ? Math.Clamp(s, 8, 300) : 24;
    public bool IsBold => bold.IsChecked == true;
    public bool HasBackground => background.IsChecked == true;
    public TextDialog(Annotation annotation)
    {
        Title = "Snapaste · 文字标注"; Width = 560; Height = 360; MinWidth = 480; MinHeight = 310; WindowStartupLocation = WindowStartupLocation.CenterOwner; Ui.Setup(this);
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var hint = Ui.Label("输入文字，再调整字体与样式。", 12, true); hint.Margin = new Thickness(0, 0, 0, 14); DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
        var options = new WrapPanel(); DockPanel.SetDock(options, Dock.Top); root.Children.Add(options);
        font.ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).Order(); font.Text = annotation.Font; size.Text = annotation.FontSize.ToString(); bold.IsChecked = annotation.Bold; background.IsChecked = annotation.Background;
        options.Children.Add(font); options.Children.Add(size); options.Children.Add(bold); options.Children.Add(background);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        buttons.Margin = new Thickness(0, 12, 0, 0); buttons.Children.Add(Ui.Button(Strings.T("Cancel"), () => DialogResult = false)); buttons.Children.Add(Ui.Action("Done", "应用文字", () => DialogResult = !string.IsNullOrWhiteSpace(text.Text), true));
        text.Text = annotation.Text; text.FontSize = annotation.FontSize; root.Children.Add(text); Loaded += (_, _) => { text.Focus(); text.CaretIndex = text.Text.Length; };
    }
}
