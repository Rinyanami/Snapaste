using System.Globalization;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Snapaste.Core;
using Snapaste.UI;
using Snapaste.Ocr;
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
    private readonly CheckBox italic = new() { Content = Strings.T("Italic"), Margin = new Thickness(6) }, underline = new() { Content = Strings.T("Underline"), Margin = new Thickness(6) };
    private readonly TextParagraphOptions paragraph;
    private readonly SelectionActions transforms;
    private bool updatingOptions;
    private readonly Button undo, redo, delete;
    private bool exporting;
    private readonly Grid contentRoot = new();
    private OcrPanel? ocrPanel;
    public event Action<EditDocument>? PinRequested;
    public event Action<EditDocument>? Completed;
    public EditorWindow(ImageDocument image, Tool tool = Tool.Select, EditDocument? document = null)
    {
        Document = document ?? new(image); Title = "Snapaste · 编辑"; Width = 1140; Height = 790; MinWidth = 800; MinHeight = 460; WindowStartupLocation = WindowStartupLocation.CenterScreen; Ui.Setup(this);
        var dock = new DockPanel(); contentRoot.Children.Add(dock); Content = contentRoot;
        var top = new StackPanel(); var topBorder = new Border { Child = top, BorderThickness = new Thickness(0, 0, 0, 1) }; topBorder.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); topBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); DockPanel.SetDock(topBorder, Dock.Top); dock.Children.Add(topBorder);
        var header = new DockPanel { Margin = new Thickness(18, 10, 18, 6), LastChildFill = false }; top.Children.Add(header);
        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; title.Children.Add(Ui.Label(image.Kind == "Scroll" ? "长截图编辑" : "截图编辑", 17)); var hint = Ui.Label("标注后，复制或留作参考。", 11, true); hint.Margin = new Thickness(0, 3, 0, 0); title.Children.Add(hint); header.Children.Add(title);
        var output = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(output, Dock.Right); header.Children.Add(output);
        output.Children.Add(Ui.Action("Ocr", Strings.T("Ocr"), ShowOcr, hint: Strings.T("OcrShortcut")));
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
        foreach (var swatch in CreateColorButtons(surface.SetColor))
        {
            colors.Add(swatch);
            colorOptions.Children.Add(swatch.Button);
        }
        sizeLabel.Margin = new Thickness(14, 0, 6, 0); sizeLabel.VerticalAlignment = VerticalAlignment.Center; sizeOptions.Children.Add(sizeLabel);
        size.ValueChanged += (_, _) => { if (!updatingOptions) surface.SetSize(size.Value); }; sizeOptions.Children.Add(size); options.Children.Add(sizeOptions);
        size.PreviewMouseLeftButtonDown += (_, _) => surface.BeginStyleEdit(); size.PreviewMouseLeftButtonUp += (_, _) => surface.EndStyleEdit(); size.LostMouseCapture += (_, _) => surface.EndStyleEdit();
        textFont.ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).Order().ToList(); textSize.ItemsSource = new[] { "12", "16", "20", "24", "32", "48", "64", "96" };
        paragraph = new TextParagraphOptions(() => surface.Selection is { Kind: Tool.Text } text ? text.FontSize : surface.TextSize);
        foreach (var control in new FrameworkElement[] { textFont, textSize, bold, italic, underline, background, paragraph }) textOptions.Children.Add(control);
        Ui.ObserveText(textFont, this, () => ApplyTextStyle()); Ui.ObserveText(textSize, this, () => ApplyTextStyle(true));
        foreach (var control in new[] { textFont, textSize })
        {
            control.GotKeyboardFocus += (_, _) => surface.BeginStyleEdit();
            control.LostKeyboardFocus += (_, _) => { surface.EndStyleEdit(); UpdateStatus(); };
        }
        AutomationProperties.SetName(textFont, Strings.T("Font"));
        AutomationProperties.SetName(textSize, Strings.T("FontSize"));
        textSize.ToolTip = Strings.T("FontSize") + " · 8–300";
        foreach (var check in new[] { bold, italic, underline, background })
        {
            check.Checked += (_, _) => ApplyTextStyle();
            check.Unchecked += (_, _) => ApplyTextStyle();
        }
        paragraph.Changed += () => ApplyTextStyle();
        options.Children.Add(textOptions); transforms = new SelectionActions(surface); options.Children.Add(transforms);
        context.VerticalAlignment = VerticalAlignment.Center; context.Margin = new Thickness(18, 0, 0, 0); options.Children.Add(context);
        var footer = new DockPanel { Margin = new Thickness(18, 7, 18, 7), LastChildFill = false }; DockPanel.SetDock(footer, Dock.Bottom); dock.Children.Add(footer); status.VerticalAlignment = VerticalAlignment.Center; footer.Children.Add(status);
        var zoom = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(zoom, Dock.Right); footer.Children.Add(zoom);
        zoom.Children.Add(Ui.Tool("Minus", "缩小", () => Zoom(surface.Zoom / 1.25))); zoomLabel.VerticalAlignment = VerticalAlignment.Center; zoomLabel.Width = 48; zoomLabel.TextAlignment = TextAlignment.Center; zoom.Children.Add(zoomLabel); zoom.Children.Add(Ui.Tool("Plus", "放大", () => Zoom(surface.Zoom * 1.25))); zoom.Children.Add(Ui.Button("100%", () => Zoom(1))); zoom.Children.Add(Ui.Tool("Fit", "适合宽度", Fit));
        dock.Children.Add(viewport);
        viewport.ScrollChanged += (_, _) => RefreshViewport(); surface.Changed += Update;
        PreviewKeyDown += (_, e) =>
        {
            if (ocrPanel != null) { if (e.Key == Key.Escape) { ocrPanel.Dismiss(); e.Handled = true; } return; }
            if (e.OriginalSource is TextBox or ComboBox) return;
            if (e.Key == Key.Enter && e.OriginalSource is ButtonBase) return;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                if (e.Key == Key.O && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { ShowOcr(); e.Handled = true; }
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
        Loaded += (_, _) => { Fit(); Select(tool); }; Closed += (_, _) => ocrPanel?.Dismiss();
        contentRoot.SizeChanged += (_, _) => { SizeOcr(); textOptions.MaxWidth = Math.Max(180, contentRoot.ActualWidth - 48); }; Update();
    }
    private void ShowOcr()
    {
        if (exporting || ocrPanel != null) return;
        surface.CancelPointer(); surface.EndStyleEdit();
        var panel = new OcrPanel(movable: false) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.CopyCompleted += panel.Dismiss;
        var shade = new Border { Background = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), Child = panel };
        ocrPanel = panel; contentRoot.Children.Add(shade);
        panel.CloseRequested += () => { contentRoot.Children.Remove(shade); if (ReferenceEquals(ocrPanel, panel)) ocrPanel = null; surface.Focus(); };
        SizeOcr(); panel.Focus(); _ = panel.RecognizeAsync(Document.Fork());
    }
    private void SizeOcr()
    {
        if (ocrPanel is not { } panel) return;
        panel.Width = Math.Max(280, Math.Min(600, contentRoot.ActualWidth - 24));
        panel.Height = Math.Max(280, Math.Min(550, contentRoot.ActualHeight - 24));
    }
    private static string Shortcut(Tool tool) => tool switch { Tool.Select => "V", Tool.Rectangle => "R", Tool.Ellipse => "E", Tool.Line => "L", Tool.Arrow => "A", Tool.Pen => "P", Tool.Highlighter => "H", Tool.Text => "T", Tool.Mosaic => "M", Tool.Blur => "B", Tool.Crop => "X", Tool.Color => "I", _ => "" };
    private static Tool? KeyTool(Key key) => key switch { Key.V => Tool.Select, Key.R => Tool.Rectangle, Key.E => Tool.Ellipse, Key.L => Tool.Line, Key.A => Tool.Arrow, Key.P => Tool.Pen, Key.H => Tool.Highlighter, Key.T => Tool.Text, Key.M => Tool.Mosaic, Key.B => Tool.Blur, Key.X => Tool.Crop, Key.I => Tool.Color, _ => null };
    private void Select(Tool tool)
    {
        surface.CancelPointer();
        surface.CurrentTool = tool;
        if (tool is Tool.Crop or Tool.Color || tool == Tool.Text && surface.Selection is { Kind: not Tool.Text })
            surface.ClearSelection();
        surface.Cursor = tool switch { Tool.Select => Cursors.Arrow, Tool.Text => Cursors.IBeam, _ => Cursors.Cross };
        surface.Focus();
        UpdateStatus();
    }
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
        var text = surface.Selection is { Kind: Tool.Text } selected ? selected : null;
        if (!textFont.IsKeyboardFocusWithin && !textFont.IsDropDownOpen) textFont.Text = text?.Font ?? surface.TextFont;
        if (!textSize.IsKeyboardFocusWithin && !textSize.IsDropDownOpen) textSize.Text = (text?.FontSize ?? surface.TextSize).ToString("0.###", CultureInfo.InvariantCulture);
        bold.IsChecked = text?.Bold ?? surface.TextBold;
        background.IsChecked = text?.Background ?? surface.TextBackground;
        italic.IsChecked = text?.Italic ?? surface.TextItalic;
        underline.IsChecked = text?.Underline ?? surface.TextUnderline;
        paragraph.Set(text?.Alignment ?? surface.TextAlignment, text?.TextIndent ?? surface.TextIndent);
        updatingOptions = false; transforms.Refresh(); textOptions.Visibility = tool == Tool.Text ? Visibility.Visible : Visibility.Collapsed;
        sizeLabel.Text = tool == Tool.Mosaic ? "块大小" : tool == Tool.Blur ? "模糊强度" : "线宽";
        context.Text = surface.HasSelection ? Strings.T("ObjectSelectedHint") : tool switch { Tool.Select => "点击选择 · 拖动移动 · 双击文字修改", Tool.Crop => "拖动保留区域 · Ctrl+Z 撤销裁剪", Tool.Text => Strings.T("OverlayTextHint"), Tool.Color => "点击像素复制 HEX 颜色", Tool.Mosaic => "拖动覆盖需要隐藏的内容", Tool.Blur => "拖动选择局部模糊范围", _ => "拖动绘制 · Ctrl+Z 撤销" };
        status.Text = $"{Document.Crop.Width:0} × {Document.Crop.Height:0} 像素   ·   {Document.Annotations.Count} 个标注"; status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); zoomLabel.Text = $"{surface.Zoom:P0}";
    }
    private void ApplyTextStyle(bool readSize = false)
    {
        if (updatingOptions) return;
        double value = surface.Selection is { Kind: Tool.Text } a ? a.FontSize : surface.TextSize;
        if (readSize && !Ui.TryFontSize(textSize.Text, out value)) return;
        surface.SetTextStyle(
            string.IsNullOrWhiteSpace(textFont.Text) ? "Microsoft YaHei UI" : textFont.Text,
            value, bold.IsChecked == true, background.IsChecked == true, italic.IsChecked == true, underline.IsChecked == true,
            paragraph.Alignment, paragraph.Indent);
    }

    internal static IEnumerable<(Color Color, Button Button)> CreateColorButtons(Action<Color> setColor)
    {
        foreach (var value in new[] { "#E95D75", "#EFA25E", "#EAC858", "#50A78B", "#5D93D8", "#9571CA", "#FFFFFF", "#302C38" })
        {
            var color = (Color)ColorConverter.ConvertFromString(value);
            var button = Ui.Button("颜色 " + value, () => setColor(color));
            button.Content = new System.Windows.Shapes.Ellipse
            {
                Width = 15, Height = 15, Fill = new SolidColorBrush(color),
                Stroke = Ui.Brush("BorderBrush"), StrokeThickness = 1
            };
            button.Padding = new Thickness(5);
            button.MinHeight = button.Width = button.Height = 26;
            button.Margin = new Thickness(2);
            yield return (color, button);
        }
    }
}

internal sealed class TextParagraphOptions : StackPanel
{
    private const double MaxIndent = 1000;
    private readonly ComboBox alignment = new() { Width = 86, SelectedValuePath = nameof(ComboBoxItem.Tag), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button less, more;
    private bool syncing;
    public TextAlignment Alignment => alignment.SelectedValue is TextAlignment value ? value : TextAlignment.Left;
    public double Indent { get; private set; }
    public bool CanReturnFromAlignment => alignment.IsKeyboardFocusWithin && !alignment.IsDropDownOpen;
    public event Action? Changed;
    public event Action? InteractionCompleted;

    public TextParagraphOptions(Func<double> fontSize)
    {
        Orientation = Orientation.Horizontal;
        alignment.Items.Add(new ComboBoxItem { Content = Strings.T("AlignLeft"), Tag = TextAlignment.Left });
        alignment.Items.Add(new ComboBoxItem { Content = Strings.T("AlignCenter"), Tag = TextAlignment.Center });
        alignment.Items.Add(new ComboBoxItem { Content = Strings.T("AlignRight"), Tag = TextAlignment.Right });
        alignment.SelectedIndex = 0;
        AutomationProperties.SetName(alignment, Strings.T("TextAlign"));
        alignment.ToolTip = Strings.T("TextAlign");
        Children.Add(alignment);
        less = Ui.Tool("IndentLess", Strings.T("IndentLess"), () => ChangeIndent(-fontSize() / 2));
        more = Ui.Tool("IndentMore", Strings.T("IndentMore"), () => ChangeIndent(fontSize() / 2));
        Children.Add(less);
        Children.Add(more);
        Set(TextAlignment.Left, 0);
        alignment.SelectionChanged += (_, _) => { if (!syncing) Changed?.Invoke(); };
        alignment.DropDownClosed += (_, _) => InteractionCompleted?.Invoke();
    }

    public void Set(TextAlignment value, double indent)
    {
        syncing = true;
        alignment.SelectedValue = value is TextAlignment.Center or TextAlignment.Right ? value : TextAlignment.Left;
        Indent = double.IsFinite(indent) ? Math.Clamp(indent, 0, MaxIndent) : 0;
        UpdateIndentButtons();
        syncing = false;
    }

    private void ChangeIndent(double delta)
    {
        Indent = Math.Clamp(Indent + Math.Round(delta), 0, MaxIndent);
        UpdateIndentButtons();
        Changed?.Invoke();
        InteractionCompleted?.Invoke();
    }

    private void UpdateIndentButtons()
    {
        less.IsEnabled = Indent > 0;
        more.IsEnabled = Indent < MaxIndent;
    }
}

internal sealed class TextDialog : Window
{
    private readonly TextBox text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ComboBox font = new() { Width = 200, IsEditable = true };
    private readonly TextBox size = new() { Width = 50 };
    private readonly CheckBox bold = new() { Content = Strings.T("Bold"), Margin = new Thickness(8) }, background = new() { Content = Strings.T("Background"), Margin = new Thickness(8) };
    private readonly CheckBox italic = new() { Content = Strings.T("Italic"), Margin = new Thickness(8) }, underline = new() { Content = Strings.T("Underline"), Margin = new Thickness(8) };
    private readonly TextParagraphOptions paragraph;
    private readonly double originalSize;
    public string Value => text.Text;
    public string FontName => string.IsNullOrWhiteSpace(font.Text) ? "Microsoft YaHei UI" : font.Text;
    public double FontSizeValue => Ui.TryFontSize(size.Text, out var value) ? value : originalSize;
    public bool IsBold => bold.IsChecked == true;
    public bool HasBackground => background.IsChecked == true;
    public bool IsItalic => italic.IsChecked == true;
    public bool IsUnderlined => underline.IsChecked == true;
    public TextAlignment Alignment => paragraph.Alignment;
    public double Indent => paragraph.Indent;
    public Color TextColor { get; private set; }
    public TextDialog(Annotation annotation)
    {
        originalSize = annotation.FontSize;
        TextColor = annotation.Color;
        var preview = annotation.Clone();
        Title = "Snapaste · 文字标注"; Width = 590; Height = 420; MinWidth = 470; MinHeight = 350; WindowStartupLocation = WindowStartupLocation.CenterOwner; Ui.Setup(this);
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        const string instructions = "输入文字，按 Ctrl+Enter 应用。";
        var hint = Ui.Label(instructions, 12, true); hint.Margin = new Thickness(0, 0, 0, 14); DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
        var options = new WrapPanel(); DockPanel.SetDock(options, Dock.Top); root.Children.Add(options);
        font.ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).Order().ToList();
        font.Text = annotation.Font;
        size.Text = annotation.FontSize.ToString(CultureInfo.InvariantCulture);
        bold.IsChecked = annotation.Bold;
        background.IsChecked = annotation.Background;
        italic.IsChecked = annotation.Italic;
        underline.IsChecked = annotation.Underline;
        paragraph = new TextParagraphOptions(() => FontSizeValue);
        paragraph.Set(annotation.Alignment, annotation.TextIndent);
        var checks = new[] { bold, italic, underline, background };
        foreach (var control in new FrameworkElement[] { font, size, bold, italic, underline, background, paragraph }) options.Children.Add(control);
        var palette = new WrapPanel { Margin = new Thickness(0, 8, 0, 10) };
        DockPanel.SetDock(palette, Dock.Top);
        root.Children.Add(palette);
        var swatches = new List<(Color Color, Button Button)>();
        foreach (var swatch in EditorWindow.CreateColorButtons(color =>
        {
            TextColor = color;
            PreviewStyle();
            ReturnToText();
        }))
        {
            swatches.Add(swatch);
            palette.Children.Add(swatch.Button);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        bool ValidateSize()
        {
            if (Ui.TryFontSize(size.Text, out _)) return true;
            hint.Text = "请输入 8–300 之间的字号。";
            hint.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            size.Focus();
            size.SelectAll();
            return false;
        }
        void Apply() { if (ValidateSize()) DialogResult = true; }
        buttons.Margin = new Thickness(0, 12, 0, 0); var cancel = Ui.Button(Strings.T("Cancel"), () => DialogResult = false); cancel.IsCancel = true; buttons.Children.Add(cancel); buttons.Children.Add(Ui.Action("Done", "应用文字", Apply, true));
        text.Text = annotation.Text;
        text.FontSize = annotation.FontSize;
        root.Children.Add(text);
        Loaded += (_, _) => { text.Focus(); text.CaretIndex = text.Text.Length; };
        void ReturnToText()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { if (IsVisible) text.Focus(); }));
        }
        void PreviewStyle()
        {
            text.FontFamily = new FontFamily(FontName);
            text.FontWeight = IsBold ? FontWeights.Bold : FontWeights.Normal;
            text.FontStyle = IsItalic ? FontStyles.Italic : FontStyles.Normal;
            text.TextDecorations = IsUnderlined ? TextDecorations.Underline : null;
            text.TextAlignment = Alignment;
            text.Foreground = new SolidColorBrush(TextColor);
            // Use a readable editing surface for both light and dark ink.
            text.Background = HasBackground || TextColor.R * .2126 + TextColor.G * .7152 + TextColor.B * .0722 < 155 ? Brushes.White : Brushes.Black;
            text.CaretBrush = text.Background == Brushes.Black ? Brushes.White : Brushes.Black;
            if (Ui.TryFontSize(size.Text, out var value)) text.FontSize = value;
            preview.TextIndent = Indent;
            double available = text.ActualWidth > 0 ? Math.Max(0, text.ActualWidth - 48) : Width - 88;
            text.Padding = new Thickness(10 + Math.Min(preview.TextInset, available), 8, 10, 8);
            foreach (var (color, button) in swatches) button.BorderBrush = color == TextColor ? Ui.Accent : Brushes.Transparent;
        }
        Ui.ObserveText(font, this, PreviewStyle);
        font.DropDownClosed += (_, _) => ReturnToText();
        size.TextChanged += (_, _) =>
        {
            if (Ui.TryFontSize(size.Text, out _))
            {
                hint.Text = instructions;
                hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            }
            PreviewStyle();
        };
        text.SizeChanged += (_, _) => PreviewStyle();
        foreach (var check in checks)
        {
            check.Checked += (_, _) => PreviewStyle();
            check.Unchecked += (_, _) => PreviewStyle();
            check.Click += (_, _) => ReturnToText();
        }
        paragraph.Changed += PreviewStyle;
        paragraph.InteractionCompleted += ReturnToText;
        AutomationProperties.SetName(font, Strings.T("Font"));
        AutomationProperties.SetName(size, Strings.T("FontSize"));
        AutomationProperties.SetName(text, Strings.T("Text"));
        InputMethod.SetIsInputMethodEnabled(text, true); PreviewStyle();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { Apply(); e.Handled = true; }
            else if (font.IsKeyboardFocusWithin && !font.IsDropDownOpen || size.IsKeyboardFocusWithin || paragraph.CanReturnFromAlignment)
            {
                if (!size.IsKeyboardFocusWithin || ValidateSize()) ReturnToText();
                e.Handled = true;
            }
        };
    }
}
