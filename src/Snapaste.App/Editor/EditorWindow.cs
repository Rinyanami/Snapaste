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
    private readonly Slider size = new() { Minimum = 2, Maximum = 40, Value = 5, Width = 105, VerticalAlignment = VerticalAlignment.Center };
    private readonly WrapPanel textOptions = new();
    private readonly ComboBox textFont = new() { Width = 160, IsEditable = true, MaxDropDownHeight = 220 }, textSize = new() { Width = 62, IsEditable = true };
    private readonly CheckBox bold = new() { Content = Strings.T("Bold"), Margin = new Thickness(6) }, background = new() { Content = Strings.T("Background"), Margin = new Thickness(6) };
    private readonly SelectionActions transforms;
    private bool updatingOptions;
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
        output.Children.Add(Ui.Action("Pin", Strings.T("Pin"), () => { if (exporting) return; PinRequested?.Invoke(Document); Completed?.Invoke(Document); Close(); }));
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
        undo = Ui.Tool("Undo", "撤销", () => History(false), "撤销 · Ctrl+Z"); redo = Ui.Tool("Redo", "重做", () => History(true), "重做 · Ctrl+Y"); tools.Children.Add(undo); tools.Children.Add(redo);
        var options = new WrapPanel { Margin = new Thickness(18, 7, 18, 10), MinHeight = 28 }; top.Children.Add(options); options.Children.Add(colorOptions);
        foreach (var value in new[] { "#E95D75", "#EFA25E", "#EAC858", "#50A78B", "#5D93D8", "#9571CA", "#FFFFFF", "#302C38" })
        {
            var color = (Color)ColorConverter.ConvertFromString(value); var b = Ui.Button("颜色 " + value, () => surface.SetColor(color)); b.Content = new System.Windows.Shapes.Ellipse { Width = 15, Height = 15, Fill = new SolidColorBrush(color), Stroke = Ui.Brush("BorderBrush"), StrokeThickness = 1 }; b.Padding = new Thickness(5); b.MinHeight = 26; b.Width = 26; b.Height = 26; b.Margin = new Thickness(2); colors.Add((color, b)); colorOptions.Children.Add(b);
        }
        sizeLabel.Margin = new Thickness(14, 0, 6, 0); sizeLabel.VerticalAlignment = VerticalAlignment.Center; sizeOptions.Children.Add(sizeLabel);
        size.ValueChanged += (_, _) => { if (!updatingOptions) surface.SetSize(size.Value); }; sizeOptions.Children.Add(size); options.Children.Add(sizeOptions);
        size.PreviewMouseLeftButtonDown += (_, _) => surface.BeginStyleEdit(); size.PreviewMouseLeftButtonUp += (_, _) => surface.EndStyleEdit(); size.LostMouseCapture += (_, _) => surface.EndStyleEdit();
        textFont.ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).Order().ToList(); textSize.ItemsSource = new[] { "12", "16", "20", "24", "32", "48", "64", "96" };
        foreach (var control in new FrameworkElement[] { textFont, textSize, bold, background }) textOptions.Children.Add(control);
        Ui.ObserveText(textFont, this, () => ApplyTextStyle()); Ui.ObserveText(textSize, this, () => ApplyTextStyle(true));
        foreach (var control in new[] { textFont, textSize })
        {
            control.GotKeyboardFocus += (_, _) => surface.BeginStyleEdit();
            control.LostKeyboardFocus += (_, _) => { surface.EndStyleEdit(); UpdateStatus(); };
        }
        System.Windows.Automation.AutomationProperties.SetName(textFont, Strings.T("Font"));
        System.Windows.Automation.AutomationProperties.SetName(textSize, Strings.T("FontSize"));
        textSize.ToolTip = Strings.T("FontSize") + " · 8–300";
        bold.Checked += (_, _) => ApplyTextStyle(); bold.Unchecked += (_, _) => ApplyTextStyle(); background.Checked += (_, _) => ApplyTextStyle(); background.Unchecked += (_, _) => ApplyTextStyle();
        options.Children.Add(textOptions); transforms = new SelectionActions(surface); options.Children.Add(transforms);
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
                if (e.Key == Key.Z) { History(false); e.Handled = true; }
                if (e.Key == Key.Y) { History(true); e.Handled = true; }
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
    private void Select(Tool tool) { surface.CancelPointer(); surface.CurrentTool = tool; if (tool is Tool.Crop or Tool.Color) surface.ClearSelection(); surface.Cursor = tool == Tool.Select ? Cursors.Arrow : Cursors.Cross; surface.Focus(); UpdateStatus(); }
    private void History(bool forward) { surface.CancelPointer(); surface.EndStyleEdit(); if (forward) Document.Redo(); else Document.Undo(); Update(); }
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
        foreach (var color in colors) color.Button.BorderBrush = color.Color == surface.DisplayColor ? Ui.Accent : Brushes.Transparent;
        undo.IsEnabled = Document.CanUndo; redo.IsEnabled = Document.CanRedo; delete.IsEnabled = surface.HasSelection;
        var tool = surface.EffectiveTool; colorOptions.Visibility = tool is Tool.Select or Tool.Crop or Tool.Mosaic or Tool.Blur ? Visibility.Collapsed : Visibility.Visible; sizeOptions.Visibility = tool is Tool.Select or Tool.Crop or Tool.Text or Tool.Color ? Visibility.Collapsed : Visibility.Visible;
        updatingOptions = true; size.Maximum = tool is Tool.Blur or Tool.Mosaic ? 32 : 40; size.Value = Math.Clamp(surface.DisplaySize, 2, size.Maximum);
        var a = surface.Selection;
        if (!textFont.IsKeyboardFocusWithin) textFont.Text = a?.Kind == Tool.Text ? a.Font : surface.TextFont;
        if (!textSize.IsKeyboardFocusWithin) textSize.Text = (a?.Kind == Tool.Text ? a.FontSize : surface.TextSize).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        bold.IsChecked = a?.Kind == Tool.Text ? a.Bold : surface.TextBold; background.IsChecked = a?.Kind == Tool.Text ? a.Background : surface.TextBackground;
        updatingOptions = false; transforms.Refresh(); textOptions.Visibility = tool == Tool.Text ? Visibility.Visible : Visibility.Collapsed;
        sizeLabel.Text = tool == Tool.Mosaic ? "块大小" : tool == Tool.Blur ? "模糊强度" : "线宽";
        context.Text = surface.HasSelection ? Strings.T("ObjectSelectedHint") : tool switch { Tool.Select => "点击选择 · 拖动移动 · 双击文字修改", Tool.Crop => "拖动保留区域 · Ctrl+Z 撤销裁剪", Tool.Text => Strings.T("OverlayTextHint"), Tool.Color => "点击像素复制 HEX 颜色", Tool.Mosaic => "拖动覆盖需要隐藏的内容", Tool.Blur => "拖动选择局部模糊范围", _ => "拖动绘制 · Ctrl+Z 撤销" };
        status.Text = $"{Document.Crop.Width:0} × {Document.Crop.Height:0} 像素   ·   {Document.Annotations.Count} 个标注"; status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); zoomLabel.Text = $"{surface.Zoom:P0}";
    }
    private void ApplyTextStyle(bool readSize = false)
    {
        double current = surface.Selection is { Kind: Tool.Text } a ? a.FontSize : surface.TextSize;
        double value = current;
        if (updatingOptions || readSize && !Ui.TryFontSize(textSize.Text, out value)) return;
        surface.SetTextStyle(string.IsNullOrWhiteSpace(textFont.Text) ? "Microsoft YaHei UI" : textFont.Text, value, bold.IsChecked == true, background.IsChecked == true);
    }
}
internal sealed class TextDialog : Window
{
    private readonly TextBox text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ComboBox font = new() { Width = 200, IsEditable = true };
    private readonly TextBox size = new() { Width = 50 };
    private readonly CheckBox bold = new() { Content = Strings.T("Bold"), Margin = new Thickness(8) }, background = new() { Content = Strings.T("Background"), Margin = new Thickness(8) };
    private readonly double originalSize;
    public string Value => text.Text;
    public string FontName => string.IsNullOrWhiteSpace(font.Text) ? "Microsoft YaHei UI" : font.Text;
    public double FontSizeValue => Ui.TryFontSize(size.Text, out var value) ? value : originalSize;
    public bool IsBold => bold.IsChecked == true;
    public bool HasBackground => background.IsChecked == true;
    public TextDialog(Annotation annotation)
    {
        originalSize = annotation.FontSize;
        Title = "Snapaste · 文字标注"; Width = 560; Height = 360; MinWidth = 480; MinHeight = 310; WindowStartupLocation = WindowStartupLocation.CenterOwner; Ui.Setup(this);
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var hint = Ui.Label("输入文字，再调整字体与样式。", 12, true); hint.Margin = new Thickness(0, 0, 0, 14); DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
        var options = new WrapPanel(); DockPanel.SetDock(options, Dock.Top); root.Children.Add(options);
        font.ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).Order(); font.Text = annotation.Font; size.Text = annotation.FontSize.ToString(System.Globalization.CultureInfo.InvariantCulture); bold.IsChecked = annotation.Bold; background.IsChecked = annotation.Background;
        options.Children.Add(font); options.Children.Add(size); options.Children.Add(bold); options.Children.Add(background);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        void Apply()
        {
            if (!Ui.TryFontSize(size.Text, out _))
            {
                hint.Text = Strings.T("FontSize") + "：请输入 8–300 之间的数字。"; hint.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush"); size.Focus(); size.SelectAll(); return;
            }
            DialogResult = true;
        }
        buttons.Margin = new Thickness(0, 12, 0, 0); var cancel = Ui.Button(Strings.T("Cancel"), () => DialogResult = false); cancel.IsCancel = true; buttons.Children.Add(cancel); buttons.Children.Add(Ui.Action("Done", "应用文字", Apply, true));
        text.Text = annotation.Text; text.FontSize = annotation.FontSize; root.Children.Add(text); Loaded += (_, _) => { text.Focus(); text.CaretIndex = text.Text.Length; };
        void PreviewStyle()
        {
            text.FontFamily = new FontFamily(FontName); text.FontWeight = IsBold ? FontWeights.Bold : FontWeights.Normal;
            if (Ui.TryFontSize(size.Text, out var value)) text.FontSize = value;
        }
        Ui.ObserveText(font, this, PreviewStyle); size.TextChanged += (_, _) => PreviewStyle(); bold.Checked += (_, _) => PreviewStyle(); bold.Unchecked += (_, _) => PreviewStyle();
        System.Windows.Automation.AutomationProperties.SetName(font, Strings.T("Font")); System.Windows.Automation.AutomationProperties.SetName(size, Strings.T("FontSize")); System.Windows.Automation.AutomationProperties.SetName(text, Strings.T("Text"));
        InputMethod.SetIsInputMethodEnabled(text, true); PreviewStyle();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { Apply(); e.Handled = true; } };
    }
}
