using System.Globalization;
using Snapaste.Core;
using Snapaste.Editor;
using Snapaste.Platform;
using Snapaste.UI;
namespace Snapaste.Capture;

public sealed class CaptureOverlay : Window
{
    private readonly BitmapSource desktop;
    private readonly Rect screen;
    private readonly Settings settings;
    private readonly WindowDetector detector = new();
    private readonly OverlaySurface surface;
    private readonly OverlayChrome chrome;
    private readonly EditorSurface annotations;
    private readonly Canvas root = new();
    private readonly Border toolbar;
    private readonly WrapPanel toolbarItems = new() { MaxWidth = 900 }, options = new();
    private readonly StackPanel palette = new() { Orientation = Orientation.Horizontal }, sizeOptions = new() { Orientation = Orientation.Horizontal };
    private readonly WrapPanel textOptions = new();
    private readonly Slider sizeSlider = new() { Minimum = 2, Maximum = 40, Value = 5, Width = 90, VerticalAlignment = VerticalAlignment.Center };
    private readonly SelectionActions transforms;
    private readonly TextBlock sizeLabel = Ui.Label("", 11, true), hint = Ui.Label("", 11, true);
    private readonly ComboBox font = new() { Width = 155, IsEditable = true, MaxDropDownHeight = 220 }, fontSize = new() { Width = 60, IsEditable = true };
    private readonly CheckBox bold = new() { Content = Strings.T("Bold"), Margin = new Thickness(6) }, textBackground = new() { Content = Strings.T("Background"), Margin = new Thickness(6) };
    private readonly Dictionary<Tool, Button> tools = [];
    private readonly List<(Color Color, Button Button)> colors = [];
    private readonly Button undo, redo, delete;
    private TextBox? textInput;
    private Annotation? textOriginal, textDraft;
    private bool addingText, updatingTextOptions, exporting, closed;
    private Rect selected = Rect.Empty, candidate = Rect.Empty, initial;
    private Point start, pointer;
    private bool dragging;
    private EditSnapshot? cropBefore;
    private Rect initialCrop;
    private int handle = -2, detectionMode;
    private nint target;
    private DateTime lastDetection;
    private readonly bool scrollMode;
    public EditDocument Document { get; }
    // A cancelled save or failed output keeps all work in the overlay.
    public event Func<Rect, EditDocument, string, nint, Task<bool>>? Accepted;

    public CaptureOverlay(BitmapSource desktop, Rect screen, Settings settings, bool scroll)
    {
        this.desktop = desktop; this.screen = screen; this.settings = settings; scrollMode = scroll;
        Document = new EditDocument(new ImageDocument(desktop));
        detectionMode = settings.UiElements ? 0 : settings.AutoWindow ? 1 : 2;
        Title = "Snapaste · " + Strings.T("Capture"); WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
        Ui.Setup(this); Content = root;
        surface = new OverlaySurface(this); chrome = new OverlayChrome(this) { IsHitTestVisible = false };
        annotations = new EditorSurface(Document) { DrawBaseImage = false, Visibility = Visibility.Collapsed, CurrentTool = Tool.Select };
        var toolbarContent = new StackPanel(); toolbarContent.Children.Add(toolbarItems); toolbarContent.Children.Add(options);
        toolbar = Ui.Card(toolbarContent, 5, true); toolbar.Visibility = Visibility.Collapsed;
        root.Children.Add(surface); root.Children.Add(annotations); root.Children.Add(chrome); root.Children.Add(toolbar);
        foreach (Tool tool in Enum.GetValues<Tool>())
        {
            var value = tool; var button = Ui.Tool(tool.ToString(), Strings.T(tool.ToString()), () => SelectTool(value));
            Compact(button); tools.Add(tool, button); toolbarItems.Children.Add(button);
        }
        toolbarItems.Children.Add(Ui.Divider());
        delete = Ui.Tool("Delete", Strings.T("Delete"), () => { CommitText(); annotations.Delete(); Refresh(); }, "Delete");
        undo = Ui.Tool("Undo", Strings.T("Undo"), () => History(false), "Ctrl+Z"); redo = Ui.Tool("Redo", Strings.T("Redo"), () => History(true), "Ctrl+Y");
        foreach (var button in new[] { delete, undo, redo }) { Compact(button); toolbarItems.Children.Add(button); }
        toolbarItems.Children.Add(Ui.Divider());
        toolbarItems.Children.Add(Ui.Action("Scroll", Strings.T("Scroll"), () => Finish("Scroll")));
        foreach (var action in new[] { "Pin", "Save", "Cancel" })
        {
            var value = action; var button = Ui.Tool(action, Strings.T(action), () => { if (value == "Cancel") Close(); else Finish(value); }); Compact(button); toolbarItems.Children.Add(button);
        }
        toolbarItems.Children.Add(Ui.Action("Copy", Strings.T("Copy"), () => Finish("Copy"), true, Strings.T("CopyFinishHint")));
        transforms = new SelectionActions(annotations, CommitText); BuildOptions();
        annotations.Changed += Refresh; annotations.TextRequested += BeginText;
        annotations.EmptySelectionDown += e => { if (e.ClickCount == 2) Finish("Copy"); else BeginDrag(e); e.Handled = true; };
        surface.MouseLeftButtonDown += (_, e) => BeginDrag(e);
        surface.MouseLeftButtonUp += Up; surface.MouseMove += Move;
        surface.LostMouseCapture += (_, _) => { if (dragging) CancelCropDrag(); };
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (Within(e.OriginalSource, toolbar) || Within(e.OriginalSource, textInput)) return;
            if (textInput != null)
            {
                CommitText(); annotations.ClearSelection(); annotations.Focus(); e.Handled = true; return;
            }
            if (!selected.IsEmpty && annotations.CurrentTool == Tool.Select && Hit(Physical(e)) >= 0 && !annotations.HitAnnotation(Physical(e) - new Vector(screen.X, screen.Y))) { BeginDrag(e); e.Handled = true; }
        };
        PreviewMouseMove += (_, e) =>
        {
            if (dragging) return;
            pointer = Physical(e);
            if (annotations.CurrentTool == Tool.Select) annotations.Cursor = Hit(pointer) switch { 0 or 4 => Cursors.SizeNWSE, 2 or 6 => Cursors.SizeNESW, 1 or 5 => Cursors.SizeNS, 3 or 7 => Cursors.SizeWE, _ => Cursors.Arrow };
            chrome.InvalidateVisual();
        };
        MouseRightButtonDown += (_, e) =>
        {
            if (exporting) return;
            if (Within(e.OriginalSource, toolbar) || textInput != null) return;
            if (selected.IsEmpty) Close();
            else if (annotations.CurrentTool != Tool.Select || annotations.HasSelection) { SelectTool(Tool.Select); annotations.ClearSelection(); }
            else { selected = candidate = Rect.Empty; annotations.Visibility = toolbar.Visibility = Visibility.Collapsed; RefreshVisuals(); }
            e.Handled = true;
        };
        SourceInitialized += (_, _) => Native.PlacePhysical(this, screen);
        root.SizeChanged += (_, _) => { surface.Width = chrome.Width = root.ActualWidth; surface.Height = chrome.Height = root.ActualHeight; Refresh(); };
        PreviewKeyDown += Key; Loaded += (_, _) => { Activate(); Focus(); }; Closed += (_, _) => closed = true;
    }

    private static void Compact(Button b) { b.Width = b.Height = b.MinHeight = 32; b.Padding = new Thickness(6); b.Margin = new Thickness(1); }
    private static bool Within(object? source, DependencyObject? ancestor)
    {
        if (ancestor == null) return false;
        for (var node = source as DependencyObject; node != null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node)) if (node == ancestor) return true;
        return false;
    }
    private void BuildOptions()
    {
        options.Margin = new Thickness(5, 3, 5, 2); options.Children.Add(palette); options.Children.Add(sizeOptions); options.Children.Add(textOptions); options.Children.Add(transforms); options.Children.Add(hint);
        foreach (var value in new[] { "#E95D75", "#EFA25E", "#EAC858", "#50A78B", "#5D93D8", "#9571CA", "#FFFFFF", "#302C38" })
        {
            var color = (Color)ColorConverter.ConvertFromString(value); var b = Ui.Button(value, () => { if (textInput != null) { annotations.Ink = color; ApplyTextOptions(); } else annotations.SetColor(color); Refresh(); });
            b.Content = new System.Windows.Shapes.Ellipse { Width = 13, Height = 13, Fill = new SolidColorBrush(color), Stroke = Ui.Brush("BorderBrush"), StrokeThickness = 1 };
            b.Width = b.Height = b.MinHeight = 24; b.Padding = new Thickness(4); b.Margin = new Thickness(1); colors.Add((color, b)); palette.Children.Add(b);
        }
        sizeLabel.Margin = new Thickness(10, 0, 5, 0); sizeLabel.VerticalAlignment = VerticalAlignment.Center; sizeOptions.Children.Add(sizeLabel);
        sizeSlider.ValueChanged += (_, _) => { if (!updatingTextOptions) annotations.SetSize(sizeSlider.Value); }; sizeOptions.Children.Add(sizeSlider);
        sizeSlider.PreviewMouseLeftButtonDown += (_, _) => annotations.BeginStyleEdit(); sizeSlider.PreviewMouseLeftButtonUp += (_, _) => annotations.EndStyleEdit(); sizeSlider.LostMouseCapture += (_, _) => annotations.EndStyleEdit();
        font.ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).Order().ToList(); font.Text = annotations.TextFont;
        fontSize.ItemsSource = new[] { "12", "16", "20", "24", "32", "48", "64", "96" }; fontSize.Text = "24";
        foreach (var control in new FrameworkElement[] { font, fontSize, bold, textBackground }) textOptions.Children.Add(control);
        Ui.ObserveText(font, this, () => ApplyTextOptions()); Ui.ObserveText(fontSize, this, () => ApplyTextOptions(true));
        foreach (var control in new[] { font, fontSize })
        {
            control.GotKeyboardFocus += (_, _) => annotations.BeginStyleEdit();
            control.LostKeyboardFocus += (_, _) => { annotations.EndStyleEdit(); UpdateOptions(); };
        }
        System.Windows.Automation.AutomationProperties.SetName(font, Strings.T("Font"));
        System.Windows.Automation.AutomationProperties.SetName(fontSize, Strings.T("FontSize"));
        fontSize.ToolTip = Strings.T("FontSize") + " · 8–300";
        bold.Checked += (_, _) => ApplyTextOptions(); bold.Unchecked += (_, _) => ApplyTextOptions();
        textBackground.Checked += (_, _) => ApplyTextOptions(); textBackground.Unchecked += (_, _) => ApplyTextOptions();
        hint.VerticalAlignment = VerticalAlignment.Center; hint.Margin = new Thickness(10, 3, 5, 3);
    }
    private Point Physical(MouseEventArgs e)
    {
        var p = PointToScreen(e.GetPosition(this));
        return new(Math.Clamp(Math.Round(p.X), screen.Left, screen.Right - 1), Math.Clamp(Math.Round(p.Y), screen.Top, screen.Bottom - 1));
    }
    private Point Local(Point p)
    {
        // A shown window always uses its HWND's real screen-to-DIP conversion.
        // Detached controls use a fallback only during pre-show layout/rendering.
        if (PresentationSource.FromVisual(this) != null) return PointFromScreen(p);
        var dpi = VisualTreeHelper.GetDpi(this); return new((p.X - screen.X) / dpi.DpiScaleX, (p.Y - screen.Y) / dpi.DpiScaleY);
    }
    private Rect Local(Rect r) => new(Local(r.TopLeft), Local(r.BottomRight));
    private static Point[] Handles(Rect r) => [r.TopLeft, new(r.Left + r.Width / 2, r.Top), r.TopRight, new(r.Right, r.Top + r.Height / 2), r.BottomRight, new(r.Left + r.Width / 2, r.Bottom), r.BottomLeft, new(r.Left, r.Top + r.Height / 2)];
    private int Hit(Point p)
    {
        if (selected.IsEmpty) return -2;
        var points = Handles(selected); for (int i = 0; i < points.Length; i++) if ((points[i] - p).Length < 9 * VisualTreeHelper.GetDpi(this).DpiScaleX) return i;
        return selected.Contains(p) ? -1 : -2;
    }
    private void BeginDrag(MouseButtonEventArgs e)
    {
        if (exporting) return;
        CommitText(); start = pointer = Physical(e); initial = selected; initialCrop = Document.Crop; handle = Hit(start);
        if (handle == -2) { target = 0; annotations.ClearSelection(); }
        cropBefore = null;
        dragging = true; surface.CaptureMouse(); toolbar.Visibility = Visibility.Collapsed; annotations.IsHitTestVisible = false;
    }
    private void Move(object sender, MouseEventArgs e)
    {
        pointer = Physical(e);
        if (dragging)
        {
            var delta = pointer - start;
            if (handle == -2) selected = Normalize(start, pointer);
            else if (handle == -1) { selected = initial; selected.Offset(delta); selected = Clamp(selected); }
            else
            {
                double l = initial.Left, t = initial.Top, r = initial.Right, b = initial.Bottom;
                if (handle is 0 or 6 or 7) l += delta.X; if (handle is 0 or 1 or 2) t += delta.Y;
                if (handle is 2 or 3 or 4) r += delta.X; if (handle is 4 or 5 or 6) b += delta.Y;
                selected = Normalize(new(l, t), new(r, b));
            }
            CheckpointCropChange(); UpdateCrop(); PlaceAnnotations();
        }
        else if (selected.IsEmpty && detectionMode < 2 && (DateTime.UtcNow - lastDetection).TotalMilliseconds > 70)
        {
            lastDetection = DateTime.UtcNow; (candidate, target) = detector.Find(pointer, detectionMode == 0); candidate.Intersect(screen);
        }
        surface.Cursor = Hit(pointer) switch { -1 => Cursors.SizeAll, 0 or 4 => Cursors.SizeNWSE, 2 or 6 => Cursors.SizeNESW, 1 or 5 => Cursors.SizeNS, 3 or 7 => Cursors.SizeWE, _ => Cursors.Cross };
        RefreshVisuals();
    }
    private Rect Normalize(Point p, Point q) => Rect.Intersect(new Rect(new Point(Math.Min(p.X, q.X), Math.Min(p.Y, q.Y)), new Point(Math.Max(p.X, q.X), Math.Max(p.Y, q.Y))), screen);
    private Rect Clamp(Rect r) => new(Math.Clamp(r.X, screen.Left, screen.Right - r.Width), Math.Clamp(r.Y, screen.Top, screen.Bottom - r.Height), r.Width, r.Height);
    private void Up(object sender, MouseButtonEventArgs e)
    {
        if (!dragging) return;
        dragging = false; surface.ReleaseMouseCapture(); annotations.IsHitTestVisible = true;
        if ((pointer - start).Length < 4 && handle == -2) selected = candidate;
        if (selected.IsEmpty || selected.Width < 2 || selected.Height < 2)
        {
            selected = initial; Document.Crop = initialCrop; cropBefore = null;
            if (selected.IsEmpty) { annotations.Visibility = toolbar.Visibility = Visibility.Collapsed; RefreshVisuals(); } else Refresh();
            return;
        }
        CheckpointCropChange(); UpdateCrop();
        if (cropBefore != null && cropBefore.Crop != Document.Crop) Document.CommitState(cropBefore);
        cropBefore = null;
        if (scrollMode) { Finish("Scroll"); return; } Refresh();
    }
    private void CheckpointCropChange()
    {
        if (cropBefore != null || selected.IsEmpty || selected.Width < 1 || selected.Height < 1 || initial.IsEmpty && Document.Annotations.Count == 0) return;
        var pixels = new Rect(new Point(Math.Floor(selected.Left) - screen.X, Math.Floor(selected.Top) - screen.Y), new Point(Math.Ceiling(selected.Right) - screen.X, Math.Ceiling(selected.Bottom) - screen.Y));
        if (pixels == Document.Crop) return;
        cropBefore = Document.CaptureState();
    }
    private void CancelCropDrag()
    {
        if (!dragging) return;
        dragging = false; surface.ReleaseMouseCapture(); annotations.IsHitTestVisible = true;
        selected = initial; Document.Crop = initialCrop; cropBefore = null; Refresh();
    }
    private void UpdateCrop()
    {
        if (selected.IsEmpty || selected.Width < 1 || selected.Height < 1) return;
        // UIA may return fractional bounds. The selected frame and every output
        // must describe the same integral set of desktop pixels.
        selected = Rect.Intersect(new Rect(new Point(Math.Floor(selected.Left), Math.Floor(selected.Top)), new Point(Math.Ceiling(selected.Right), Math.Ceiling(selected.Bottom))), screen);
        Document.Crop = new(selected.X - screen.X, selected.Y - screen.Y, selected.Width, selected.Height);
    }
    private void PlaceAnnotations()
    {
        if (selected.IsEmpty || selected.Width < 1 || selected.Height < 1) { annotations.Visibility = Visibility.Collapsed; return; }
        var r = Local(selected); annotations.Zoom = r.Width / selected.Width; annotations.Width = r.Width; annotations.Height = r.Height;
        annotations.Visible = Document.Crop; annotations.Visibility = Visibility.Visible;
        Canvas.SetLeft(annotations, r.Left); Canvas.SetTop(annotations, r.Top); annotations.InvalidateVisual();
    }
    private void RefreshVisuals() { surface.InvalidateVisual(); chrome.InvalidateVisual(); }
    private void Refresh()
    {
        if (closed) return;
        if (!selected.IsEmpty) selected = new(Document.Crop.X + screen.X, Document.Crop.Y + screen.Y, Document.Crop.Width, Document.Crop.Height);
        PlaceAnnotations(); RefreshVisuals(); UpdateOptions(); if (!dragging) PositionToolbar();
    }
    private void PositionToolbar()
    {
        if (selected.IsEmpty) return;
        double width = root.ActualWidth, height = root.ActualHeight;
        toolbar.Visibility = Visibility.Visible; toolbarItems.MaxWidth = options.MaxWidth = Math.Max(150, Math.Min(900, width - 24));
        toolbar.Measure(new Size(Math.Max(150, width - 12), Math.Max(200, height))); var size = toolbar.DesiredSize; var r = Local(selected);
        Canvas.SetLeft(toolbar, Math.Clamp(r.Right - size.Width, 6, Math.Max(6, width - size.Width - 6)));
        double top = r.Bottom + size.Height + 16 <= height ? r.Bottom + 10 : r.Top - size.Height - 10;
        Canvas.SetTop(toolbar, Math.Clamp(top, 6, Math.Max(6, height - size.Height - 6)));
    }
    private void SelectTool(Tool tool)
    {
        CancelCropDrag(); annotations.CancelPointer(); CommitText(); annotations.CurrentTool = tool; annotations.Cursor = tool == Tool.Select ? Cursors.Arrow : tool == Tool.Text ? Cursors.IBeam : Cursors.Cross;
        if (tool is Tool.Crop or Tool.Color) annotations.ClearSelection(); annotations.Focus(); Refresh();
    }
    private void UpdateOptions()
    {
        var tool = annotations.EffectiveTool;
        foreach (var pair in tools) pair.Value.Tag = pair.Key == annotations.CurrentTool ? "Selected" : null;
        foreach (var color in colors) color.Button.BorderBrush = color.Color == (textInput != null ? annotations.Ink : annotations.DisplayColor) ? Ui.Accent : Brushes.Transparent;
        updatingTextOptions = true;
        sizeSlider.Maximum = tool is Tool.Blur or Tool.Mosaic ? 32 : 40;
        sizeSlider.Value = Math.Clamp(annotations.DisplaySize, sizeSlider.Minimum, sizeSlider.Maximum);
        if (textInput == null)
        {
            var a = annotations.Selection;
            if (!font.IsKeyboardFocusWithin) font.Text = a?.Kind == Tool.Text ? a.Font : annotations.TextFont;
            if (!fontSize.IsKeyboardFocusWithin) fontSize.Text = (a?.Kind == Tool.Text ? a.FontSize : annotations.TextSize).ToString("0.###", CultureInfo.InvariantCulture);
            bold.IsChecked = a?.Kind == Tool.Text ? a.Bold : annotations.TextBold; textBackground.IsChecked = a?.Kind == Tool.Text ? a.Background : annotations.TextBackground;
        }
        updatingTextOptions = false; transforms.Refresh(textInput == null);
        undo.IsEnabled = textInput?.CanUndo ?? Document.CanUndo; redo.IsEnabled = textInput?.CanRedo ?? Document.CanRedo; delete.IsEnabled = annotations.HasSelection;
        palette.Visibility = textInput == null && (tool is Tool.Select or Tool.Crop or Tool.Mosaic or Tool.Blur) ? Visibility.Collapsed : Visibility.Visible;
        sizeOptions.Visibility = tool is Tool.Select or Tool.Crop or Tool.Text or Tool.Color ? Visibility.Collapsed : Visibility.Visible;
        textOptions.Visibility = tool == Tool.Text || textInput != null ? Visibility.Visible : Visibility.Collapsed;
        sizeLabel.Text = Strings.T(tool == Tool.Mosaic ? "BlockSize" : tool == Tool.Blur ? "BlurSize" : "StrokeSize");
        hint.Text = Strings.T(textInput != null ? "InlineTextHint" : annotations.HasSelection ? "ObjectSelectedHint" : tool == Tool.Select ? "OverlaySelectHint" : tool == Tool.Crop ? "OverlayCropHint" : tool == Tool.Color ? "OverlayColorHint" : tool == Tool.Text ? "OverlayTextHint" : "OverlayDrawHint");
    }
    private void History(bool forward)
    {
        if (textInput != null)
        {
            if (forward) textInput.Redo(); else textInput.Undo();
            textInput.Focus(); UpdateOptions(); return;
        }
        CancelCropDrag(); annotations.CancelPointer(); annotations.EndStyleEdit(); if (forward) Document.Redo(); else Document.Undo(); Refresh();
    }
    private static Tool? KeyTool(Key key) => key switch { System.Windows.Input.Key.V => Tool.Select, System.Windows.Input.Key.R => Tool.Rectangle, System.Windows.Input.Key.E => Tool.Ellipse, System.Windows.Input.Key.L => Tool.Line, System.Windows.Input.Key.A => Tool.Arrow, System.Windows.Input.Key.P => Tool.Pen, System.Windows.Input.Key.H => Tool.Highlighter, System.Windows.Input.Key.T => Tool.Text, System.Windows.Input.Key.M => Tool.Mosaic, System.Windows.Input.Key.B => Tool.Blur, System.Windows.Input.Key.X => Tool.Crop, System.Windows.Input.Key.I => Tool.Color, _ => null };
    private void Key(object sender, KeyEventArgs e)
    {
        if (exporting) return;
        if (Within(e.OriginalSource, textInput))
        {
            // ImeProcessed keys reach WPF text services unchanged.
            if (e.Key == System.Windows.Input.Key.Escape) { CancelText(); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { CommitText(); annotations.Focus(); e.Handled = true; }
            return;
        }
        if (e.OriginalSource is TextBox || Within(e.OriginalSource, font) || Within(e.OriginalSource, fontSize)) return;
        if (e.Key == System.Windows.Input.Key.Escape) { Close(); e.Handled = true; return; }
        if (selected.IsEmpty && e.Key == System.Windows.Input.Key.Tab) { detectionMode = (detectionMode + 1) % 3; candidate = Rect.Empty; e.Handled = true; RefreshVisuals(); return; }
        if (e.Key == System.Windows.Input.Key.C && Keyboard.Modifiers == ModifierKeys.None) { try { var c = PixelColor(pointer); Clipboard.SetText($"#{c.R:X2}{c.G:X2}{c.B:X2}"); } catch (Exception ex) { Ui.Error(ex, this); } e.Handled = true; return; }
        if (selected.IsEmpty) return;
        bool control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (control && e.Key is System.Windows.Input.Key.Z or System.Windows.Input.Key.Y) { History(e.Key == System.Windows.Input.Key.Y); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Enter || control && e.Key == System.Windows.Input.Key.C) { Finish("Copy"); e.Handled = true; }
        else if (control && e.Key == System.Windows.Input.Key.S) { Finish("Save"); e.Handled = true; }
        else if (control && e.Key == System.Windows.Input.Key.T) { Finish("Pin"); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Delete) { CommitText(); annotations.Delete(); Refresh(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.None && KeyTool(e.Key) is Tool tool) { SelectTool(tool); e.Handled = true; }
        else if (e.Key is System.Windows.Input.Key.Left or System.Windows.Input.Key.Right or System.Windows.Input.Key.Up or System.Windows.Input.Key.Down)
        {
            CancelCropDrag(); CommitText(); int step = control ? 10 : 1;
            double dx = e.Key == System.Windows.Input.Key.Left ? -step : e.Key == System.Windows.Input.Key.Right ? step : 0, dy = e.Key == System.Windows.Input.Key.Up ? -step : e.Key == System.Windows.Input.Key.Down ? step : 0;
            if (!annotations.MoveSelection(new(dx, dy)))
            {
                var next = selected;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) next = Rect.Intersect(new Rect(next.X, next.Y, Math.Max(2, next.Width + dx), Math.Max(2, next.Height + dy)), screen);
                else { next.Offset(dx, dy); next = Clamp(next); }
                if (next == selected) { e.Handled = true; return; }
                Document.Checkpoint(); selected = next;
                UpdateCrop(); Refresh();
            }
            e.Handled = true;
        }
    }

    private void BeginText(Annotation annotation, bool add)
    {
        CommitText(); textOriginal = annotation; textDraft = annotation.Clone(); addingText = add;
        annotations.HiddenAnnotation = add ? null : annotation;
        updatingTextOptions = true;
        font.Text = annotation.Font; fontSize.Text = annotation.FontSize.ToString(CultureInfo.InvariantCulture); bold.IsChecked = annotation.Bold; textBackground.IsChecked = annotation.Background; annotations.Ink = annotation.Color;
        updatingTextOptions = false;
        textInput = new TextBox { Text = annotation.Text, AcceptsReturn = true, TextWrapping = annotation.TextWidth > 0 ? TextWrapping.Wrap : TextWrapping.NoWrap, Padding = new Thickness(0), BorderThickness = new Thickness(1), MinWidth = 24, MinHeight = 24 };
        textInput.SetResourceReference(Control.BorderBrushProperty, "AccentBrush");
        InputMethod.SetIsInputMethodEnabled(textInput, true);
        System.Windows.Automation.AutomationProperties.SetName(textInput, Strings.T("Text"));
        textInput.TextChanged += (_, _) => UpdateOptions();
        root.Children.Add(textInput); Panel.SetZIndex(textInput, 2);
        ApplyTextOptions(); Refresh(); textInput.Focus(); textInput.CaretIndex = textInput.Text.Length;
    }
    private void ApplyTextOptions(bool readSize = false)
    {
        if (updatingTextOptions) return;
        annotations.TextFont = string.IsNullOrWhiteSpace(font.Text) ? "Microsoft YaHei UI" : font.Text;
        double current = textDraft?.FontSize ?? (annotations.Selection is { Kind: Tool.Text } a ? a.FontSize : annotations.TextSize);
        double size = current;
        if (readSize && !Ui.TryFontSize(fontSize.Text, out size)) return;
        annotations.TextSize = size;
        annotations.TextBold = bold.IsChecked == true; annotations.TextBackground = textBackground.IsChecked == true;
        if (textInput == null || textDraft == null) { annotations.SetTextStyle(annotations.TextFont, annotations.TextSize, annotations.TextBold, annotations.TextBackground); return; }
        textDraft.Font = annotations.TextFont; textDraft.FontSize = annotations.TextSize; textDraft.Bold = annotations.TextBold; textDraft.Background = annotations.TextBackground; textDraft.Color = annotations.Ink;
        textInput.FontFamily = new FontFamily(textDraft.Font); textInput.FontSize = textDraft.FontSize * annotations.Zoom; textInput.FontWeight = textDraft.Bold ? FontWeights.Bold : FontWeights.Normal;
        textInput.Foreground = new SolidColorBrush(textDraft.Color); textInput.Background = textDraft.Background ? new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)) : Brushes.Transparent;
        var origin = Local(new Point(textDraft.Bounds.X + screen.X, textDraft.Bounds.Y + screen.Y));
        Canvas.SetLeft(textInput, origin.X - 1); Canvas.SetTop(textInput, origin.Y - 1);
        double available = Math.Max(24, Local(selected).Right - origin.X);
        textInput.Width = textDraft.TextWidth > 0 ? Math.Max(24, textDraft.TextWidth * annotations.Zoom + 2) : double.NaN;
        textInput.MaxWidth = textDraft.TextWidth > 0 ? double.PositiveInfinity : available;
        // Match the annotation's transform while keeping native WPF text services/IME.
        var matrix = textDraft.Transform; var local = Matrix.Identity;
        local.Translate(textDraft.Bounds.X, textDraft.Bounds.Y); local.Append(matrix); local.Translate(-textDraft.Bounds.X, -textDraft.Bounds.Y);
        local.OffsetX *= annotations.Zoom; local.OffsetY *= annotations.Zoom;
        textInput.RenderTransform = new MatrixTransform(local);
    }
    private void CommitText()
    {
        if (textInput == null || textOriginal == null || textDraft == null) return;
        var original = textOriginal; var draft = textDraft; bool add = addingText; draft.Text = textInput.Text;
        RemoveTextInput(); annotations.ApplyText(original, draft, add); Refresh();
    }
    private void CancelText() { RemoveTextInput(); annotations.Focus(); Refresh(); }
    private void RemoveTextInput()
    {
        if (textInput != null) root.Children.Remove(textInput);
        textInput = null; textDraft = textOriginal = null; annotations.HiddenAnnotation = null; annotations.InvalidateVisual();
    }
    private Color PixelColor(Point p)
    {
        var bytes = new byte[4]; desktop.CopyPixels(new Int32Rect(Math.Clamp((int)(p.X - screen.X), 0, desktop.PixelWidth - 1), Math.Clamp((int)(p.Y - screen.Y), 0, desktop.PixelHeight - 1), 1, 1), bytes, 4, 0); return Color.FromRgb(bytes[2], bytes[1], bytes[0]);
    }
    private async void Finish(string action)
    {
        if (exporting || selected.IsEmpty || selected.Width < 1 || selected.Height < 1 || Accepted == null) return;
        CommitText();
        if (action == "Scroll")
        {
            if (selected.Width < 80 || selected.Height < 100) { Ui.Error(new InvalidOperationException(Strings.T("ScrollMinRegion")), this); return; }
            if (Document.Annotations.Count > 0 && MessageBox.Show(this, Strings.T("ScrollFreshCapture"), Strings.T("Scroll"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var rect = selected; var accept = Accepted; Close();
            try { await accept(rect, Document, action, target); } catch (Exception ex) { Ui.Error(ex); }
            return;
        }
        exporting = true; toolbar.IsEnabled = annotations.IsEnabled = false;
        try { if (await Accepted(selected, Document, action, target) && !closed) Close(); }
        catch (Exception ex) { Ui.Error(ex, this); }
        finally { exporting = false; toolbar.IsEnabled = annotations.IsEnabled = true; if (!closed) { Activate(); annotations.Focus(); Refresh(); } }
    }
    private sealed class OverlaySurface(CaptureOverlay o) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawImage(o.desktop, new Rect(0, 0, ActualWidth, ActualHeight));
            var selection = o.selected.IsEmpty ? o.candidate : o.selected;
            var full = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
            Geometry shade = selection.IsEmpty ? full : Geometry.Combine(full, new RectangleGeometry(o.Local(selection)), GeometryCombineMode.Exclude, null);
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(88, 0, 0, 0)), null, shade);
        }
    }
    private sealed class OverlayChrome(CaptureOverlay o) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc)
        {
            var selection = o.selected.IsEmpty ? o.candidate : o.selected;
            if (!selection.IsEmpty)
            {
                var r = o.Local(selection); dc.DrawRectangle(null, new System.Windows.Media.Pen(Ui.Accent, 1), r);
                if (!o.selected.IsEmpty) foreach (var p in Handles(r)) dc.DrawRectangle(Brushes.White, new System.Windows.Media.Pen(Ui.Accent, 1), new Rect(p.X - 3, p.Y - 3, 6, 6));
                Label(dc, $"{selection.Width:0} × {selection.Height:0}", new Point(r.Left, Math.Max(2, r.Top - 25)), 12);
            }
            if (o.selected.IsEmpty) Label(dc, Strings.T(o.scrollMode ? "ScrollSelectHint" : "CaptureSelectHint") + "  ·  " + Strings.T(new[] { "ElementMode", "WindowMode", "FreeMode" }[o.detectionMode]), new Point(22, 20), 12);
            bool inspecting = o.selected.IsEmpty || o.dragging || o.annotations.CurrentTool == Tool.Color;
            if (o.settings.Magnifier && inspecting && o.IsMouseOver && !Within(Mouse.DirectlyOver, o.toolbar) && !o.pointer.Equals(default(Point)))
            {
                var p = o.Local(o.pointer); var color = o.PixelColor(o.pointer); double x = p.X + 28, y = p.Y + 28;
                if (x + 184 > ActualWidth) x = p.X - 204; if (y + 160 > ActualHeight) y = p.Y - 180;
                x = Math.Max(0, x); y = Math.Max(0, y);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(25, 29, 36)), null, new Rect(x, y, 184, 160), 8, 8);
                int width = Math.Min(19, o.desktop.PixelWidth), height = Math.Min(19, o.desktop.PixelHeight);
                int px = Math.Clamp((int)(o.pointer.X - o.screen.X) - width / 2, 0, o.desktop.PixelWidth - width), py = Math.Clamp((int)(o.pointer.Y - o.screen.Y) - height / 2, 0, o.desktop.PixelHeight - height);
                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
                dc.DrawImage(new CroppedBitmap(o.desktop, new Int32Rect(px, py, width, height)), new Rect(x + 44, y + 8, 95, 95));
                dc.DrawRectangle(null, new System.Windows.Media.Pen(Brushes.Red, 1), new Rect(x + 44 + ((int)(o.pointer.X - o.screen.X) - px) * 95d / width, y + 8 + ((int)(o.pointer.Y - o.screen.Y) - py) * 95d / height, 95d / width, 95d / height));
                Label(dc, $"X {o.pointer.X:0}  Y {o.pointer.Y:0}\nRGB {color.R}, {color.G}, {color.B}  #{color.R:X2}{color.G:X2}{color.B:X2}", new Point(x + 7, y + 108), 11);
            }
        }
        private void Label(DrawingContext dc, string value, Point point, double size)
        {
            var t = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), size, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(220, 22, 27, 34)), null, new Rect(point.X - 4, point.Y - 2, t.Width + 8, t.Height + 4), 4, 4); dc.DrawText(t, point);
        }
    }
}
