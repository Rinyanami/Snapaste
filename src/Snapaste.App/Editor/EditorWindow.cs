using Snapaste.Core;
using Snapaste.UI;
namespace Snapaste.Editor;

public sealed class EditorWindow : Window
{
    public EditDocument Document { get; }
    private readonly EditorSurface surface;
    private readonly ScrollViewer viewport = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = new SolidColorBrush(Color.FromRgb(44, 49, 58)) };
    private readonly TextBlock status = new() { Margin = new Thickness(12, 5, 12, 5) };
    public event Action<EditDocument>? PinRequested;
    public event Action<EditDocument>? Completed;
    public EditorWindow(ImageDocument image, Tool tool = Tool.Select, EditDocument? document = null)
    {
        Document = document ?? new(image); Title = "Snapaste · 编辑"; Width = 1100; Height = 760; MinWidth = 640; MinHeight = 400; WindowStartupLocation = WindowStartupLocation.CenterScreen; Ui.Setup(this);
        var dock = new DockPanel(); Content = dock;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); dock.Children.Add(top);
        var tools = new WrapPanel { Margin = new Thickness(6) }; top.Children.Add(tools);
        surface = new EditorSurface(Document) { CurrentTool = tool }; viewport.Content = surface;
        foreach (Tool t in Enum.GetValues<Tool>()) { var value = t; tools.Children.Add(Ui.Button(Strings.T(t.ToString()), () => { surface.CurrentTool = value; surface.Focus(); UpdateStatus(); })); }
        tools.Children.Add(Ui.Button(Strings.T("Delete"), () => { surface.Delete(); Update(); }));
        tools.Children.Add(Ui.Button(Strings.T("Undo"), () => { Document.Undo(); Update(); })); tools.Children.Add(Ui.Button(Strings.T("Redo"), () => { Document.Redo(); Update(); }));
        var options = new WrapPanel { Margin = new Thickness(6, 0, 6, 5) }; top.Children.Add(options);
        foreach (var color in new[] { Colors.Red, Colors.Orange, Colors.Gold, Colors.LimeGreen, Colors.DodgerBlue, Colors.Magenta, Colors.White, Colors.Black })
        {
            var c = color; var b = Ui.Button("●", () => surface.Ink = c); b.Foreground = new SolidColorBrush(c); b.FontSize = 18; b.Padding = new Thickness(5, 0, 5, 0); options.Children.Add(b);
        }
        options.Children.Add(new TextBlock { Text = "线宽 / 块大小", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        var size = new Slider { Minimum = 2, Maximum = 40, Value = 5, Width = 105, VerticalAlignment = VerticalAlignment.Center }; size.ValueChanged += (_, _) => { surface.InkSize = size.Value; }; options.Children.Add(size);
        options.Children.Add(Ui.Button("−", () => Zoom(surface.Zoom / 1.25))); options.Children.Add(Ui.Button("+", () => Zoom(surface.Zoom * 1.25))); options.Children.Add(Ui.Button("100%", () => Zoom(1))); options.Children.Add(Ui.Button("适合宽度", Fit));
        options.Children.Add(Ui.Button(Strings.T("Pin"), () => { Completed?.Invoke(Document); PinRequested?.Invoke(Document); Close(); }));
        options.Children.Add(Ui.Button(Strings.T("Save"), () => { if (ImageExport.SaveDialog(Document, App.Current.Settings, this)) Completed?.Invoke(Document); }));
        options.Children.Add(Ui.Button(Strings.T("Copy"), () => Copy(false)));
        options.Children.Add(Ui.Button(Strings.T("Done"), () => Copy(true)));
        DockPanel.SetDock(status, Dock.Bottom); dock.Children.Add(status); dock.Children.Add(viewport);
        viewport.ScrollChanged += (_, _) => { surface.Visible = new Rect(viewport.HorizontalOffset / surface.Zoom + Document.Crop.X, viewport.VerticalOffset / surface.Zoom + Document.Crop.Y, Math.Max(1, viewport.ViewportWidth) / surface.Zoom, Math.Max(1, viewport.ViewportHeight) / surface.Zoom); surface.InvalidateVisual(); };
        surface.Changed += Update;
        PreviewKeyDown += (_, e) =>
        {
            if (e.OriginalSource is TextBox) return;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                if (e.Key == Key.Z) { Document.Undo(); Update(); e.Handled = true; }
                if (e.Key == Key.Y) { Document.Redo(); Update(); e.Handled = true; }
                if (e.Key == Key.S) { if (ImageExport.SaveDialog(Document, App.Current.Settings, this)) Completed?.Invoke(Document); e.Handled = true; }
                if (e.Key == Key.C) { Copy(false); e.Handled = true; }
            }
            else if (e.Key == Key.Delete) { surface.Delete(); Update(); }
            else if (e.Key == Key.Enter) Copy(true);
        };
        Loaded += (_, _) => { Fit(); surface.Focus(); }; Update();
    }
    private async void Copy(bool close)
    {
        try { await ImageExport.Copy(Document); Completed?.Invoke(Document); status.Text = "已复制到剪贴板"; if (close) Close(); } catch (Exception e) { Ui.Error(e); }
    }
    private void Fit() => Zoom(Math.Min(1, Math.Max(.05, (viewport.ActualWidth - 28) / Document.Crop.Width)));
    private void Zoom(double value) { surface.Zoom = Math.Clamp(value, .02, 8); Update(); }
    private void Update() { surface.Width = Document.Crop.Width * surface.Zoom; surface.Height = Document.Crop.Height * surface.Zoom; surface.InvalidateVisual(); UpdateStatus(); }
    private void UpdateStatus() => status.Text = $"{Document.Crop.Width:0} × {Document.Crop.Height:0} 像素   ·   {surface.Zoom:P0}   ·   {Strings.T(surface.CurrentTool.ToString())}   ·   Ctrl+Z / Ctrl+Y   ·   双击文字修改";
}

public sealed class EditorSurface : FrameworkElement
{
    private readonly EditDocument document;
    public Tool CurrentTool { get; set; }
    public Color Ink { get; set; } = Colors.Red;
    public double InkSize { get; set; } = 5;
    public double Zoom { get; set; } = 1;
    public Rect Visible { get; set; } = new(0, 0, 2000, 2000);
    private Annotation? active, selected;
    private Point start, last;
    private bool resize;
    private Rect cropDrag = Rect.Empty;
    public event Action? Changed;
    public EditorSurface(EditDocument document)
    {
        this.document = document; Focusable = true; Cursor = Cursors.Cross;
        MouseLeftButtonDown += Down; MouseMove += Move; MouseLeftButtonUp += Up;
    }
    private Point Pixel(MouseEventArgs e) { var p = e.GetPosition(this); return new(Math.Clamp(p.X / Zoom + document.Crop.X, document.Crop.Left, document.Crop.Right), Math.Clamp(p.Y / Zoom + document.Crop.Y, document.Crop.Top, document.Crop.Bottom)); }
    public void Delete() { if (selected != null && document.Annotations.Contains(selected)) { document.Checkpoint(); document.Annotations.Remove(selected); selected = null; InvalidateVisual(); } }
    private void Down(object sender, MouseButtonEventArgs e)
    {
        Focus(); start = last = Pixel(e); cropDrag = Rect.Empty;
        if (CurrentTool == Tool.Color)
        {
            var b = document.Raster(new Rect(Math.Clamp((int)start.X, 0, document.Image.Width - 1), Math.Clamp((int)start.Y, 0, document.Image.Height - 1), 1, 1)); var bytes = new byte[4]; b.CopyPixels(bytes, 4, 0); Ink = Color.FromRgb(bytes[2], bytes[1], bytes[0]); Clipboard.SetText($"#{Ink.R:X2}{Ink.G:X2}{Ink.B:X2}"); return;
        }
        if (CurrentTool == Tool.Select)
        {
            resize = selected != null && (selected.Bounds.BottomRight - start).Length < 12 / Zoom;
            if (!resize) selected = document.Annotations.LastOrDefault(a => { var r = a.Bounds; r.Inflate(8 / Zoom, 8 / Zoom); return r.Contains(start); });
            if (selected != null)
            {
                if (e.ClickCount == 2 && selected.Kind == Tool.Text) { EditText(selected, false); return; }
                document.Checkpoint(); active = selected;
            }
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
    private Annotation NewAnnotation() => new() { Kind = CurrentTool, Color = Ink, Stroke = Math.Max(1, InkSize / 2), EffectSize = (int)InkSize * 2, Bounds = new Rect(start, new Size(1, 1)), Points = [start] };
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
        var dialog = new TextDialog(annotation) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        document.Checkpoint(); annotation.Text = dialog.Value; annotation.FontSize = dialog.FontSizeValue; annotation.Font = dialog.FontName; annotation.Bold = dialog.IsBold; annotation.Background = dialog.HasBackground;
        var formatted = new System.Windows.Media.FormattedText(annotation.Text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily(annotation.Font), FontStyles.Normal, annotation.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal), annotation.FontSize, Brushes.Black, 1);
        annotation.Bounds = new Rect(annotation.Bounds.Location, new Size(Math.Max(1, formatted.Width), formatted.Height));
        if (add) document.Annotations.Add(annotation); selected = annotation; Changed?.Invoke();
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        dc.PushTransform(new ScaleTransform(Zoom, Zoom)); dc.PushTransform(new TranslateTransform(-document.Crop.X, -document.Crop.Y));
        document.Draw(dc, Visible);
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
        Title = "文字标注"; Width = 520; Height = 310; WindowStartupLocation = WindowStartupLocation.CenterOwner; Ui.Setup(this);
        var root = new DockPanel { Margin = new Thickness(14) }; Content = root;
        var options = new WrapPanel(); DockPanel.SetDock(options, Dock.Top); root.Children.Add(options);
        font.ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).Order(); font.Text = annotation.Font; size.Text = annotation.FontSize.ToString(); bold.IsChecked = annotation.Bold; background.IsChecked = annotation.Background;
        options.Children.Add(font); options.Children.Add(size); options.Children.Add(bold); options.Children.Add(background);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        buttons.Children.Add(Ui.Button(Strings.T("Cancel"), () => DialogResult = false)); buttons.Children.Add(Ui.Button(Strings.T("Ok"), () => DialogResult = !string.IsNullOrWhiteSpace(text.Text)));
        text.Text = annotation.Text; text.FontSize = annotation.FontSize; root.Children.Add(text); Loaded += (_, _) => { text.Focus(); text.CaretIndex = text.Text.Length; };
    }
}
