using System.Globalization;
using Snapaste.Core;
using Snapaste.Editor;
using Snapaste.Platform;
using Snapaste.UI;
using Snapaste.Ocr;
namespace Snapaste.Capture;

public sealed class CaptureOverlay : Window
{
    private readonly BitmapSource desktop;
    private readonly Rect screen;
    private readonly Rect[] monitors;
    private readonly Settings settings;
    private readonly WindowDetector detector;
    private readonly CancellationTokenSource detectionLifetime = new();
    private PixelRegionDetector? pixelDetector;
    private Task? pixelPreparation;
    private sealed record RegionCandidate(Rect Bounds, nint Handle, string Kind);
    private readonly List<RegionCandidate> candidates = [];
    private RegionCandidate? preferredCandidate;
    private int candidateIndex;
    private nint candidateTarget, pressedTarget, initialTarget;
    private Rect pressedCandidate = Rect.Empty;
    private readonly OverlaySurface surface;
    private readonly OverlayChrome chrome;
    private readonly EditorSurface annotations;
    private readonly Canvas root = new();
    private readonly Border toolbar;
    private readonly WrapPanel toolbarItems = new() { MaxWidth = 1024 }, options = new();
    private readonly StackPanel palette = new() { Orientation = Orientation.Horizontal }, sizeOptions = new() { Orientation = Orientation.Horizontal };
    private readonly WrapPanel textOptions = new();
    private readonly Slider sizeSlider = new() { Minimum = 2, Maximum = 40, Value = 5, Width = 90, VerticalAlignment = VerticalAlignment.Center };
    private readonly SelectionActions transforms;
    private readonly TextBlock sizeLabel = Ui.Label("", 11, true), hint = Ui.Label("", 11, true);
    private readonly ComboBox font = new() { Width = 155, IsEditable = true, MaxDropDownHeight = 220 }, fontSize = new() { Width = 60, IsEditable = true };
    private readonly CheckBox bold = new() { Content = Strings.T("Bold"), Margin = new Thickness(6) }, textBackground = new() { Content = Strings.T("Background"), Margin = new Thickness(6) };
    private readonly CheckBox italic = new() { Content = Strings.T("Italic"), Margin = new Thickness(6) }, underline = new() { Content = Strings.T("Underline"), Margin = new Thickness(6) };
    private readonly Dictionary<TextAlignment, Button> alignments = [];
    private readonly Button indentLess, indentMore;
    private readonly Dictionary<Tool, Button> tools = [];
    private readonly List<(Color Color, Button Button)> colors = [];
    private readonly Button undo, redo, delete;
    private TextBox? textInput;
    private OcrPanel? ocrPanel;
    private Annotation? textOriginal, textDraft;
    private bool addingText, updatingTextOptions, exporting, closed;
    private Rect selected = Rect.Empty, candidate = Rect.Empty, renderedSelection = Rect.Empty, initial;
    private Point start, pointer, pressedPointer;
    private bool dragging, dragMoved, pointerKnown;
    private EditSnapshot? cropBefore;
    private Rect initialCrop;
    private int handle = -2, detectionMode;
    private nint target;
    private readonly byte[] pixelBytes = new byte[4];
    private readonly bool scrollMode;
    public EditDocument Document { get; }
    // A cancelled save or failed output keeps all work in the overlay.
    public event Func<Rect, EditDocument, string, nint, Task<bool>>? Accepted;

    public CaptureOverlay(BitmapSource desktop, Rect screen, Settings settings, bool scroll)
    {
        this.desktop = desktop; this.screen = screen; this.settings = settings; scrollMode = scroll;
        monitors = System.Windows.Forms.Screen.AllScreens.Select(m => Rect.Intersect(new Rect(m.Bounds.X, m.Bounds.Y, m.Bounds.Width, m.Bounds.Height), screen))
            .Where(r => !r.IsEmpty && r.Width > 0 && r.Height > 0).ToArray();
        if (monitors.Length == 0) monitors = [screen];
        detector = new WindowDetector(screen);
        detector.CandidatesChanged += OnCandidatesChanged;
        Document = new EditDocument(new ImageDocument(desktop));
        detectionMode = settings.UiElements ? 0 : settings.AutoWindow ? 1 : 2;
        Title = "Snapaste · " + Strings.T("Capture"); WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
        Ui.Setup(this); Content = root;
        surface = new OverlaySurface(this); chrome = new OverlayChrome(this) { IsHitTestVisible = false };
        annotations = new EditorSurface(Document) { DrawBaseImage = false, Visibility = Visibility.Collapsed, CurrentTool = Tool.Select };
        var toolbarContent = new StackPanel(); toolbarContent.Children.Add(toolbarItems); toolbarContent.Children.Add(options);
        toolbar = Ui.Card(toolbarContent, 5, true); toolbar.Visibility = Visibility.Collapsed;
        toolbar.SizeChanged += (_, _) => { if (textInput != null && !closed) UpdateTextLayout(); };
        root.Children.Add(surface); root.Children.Add(annotations); root.Children.Add(chrome); root.Children.Add(toolbar); Panel.SetZIndex(toolbar, 5);
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
        toolbarItems.Children.Add(Ui.Action("Ocr", Strings.T("Ocr"), ShowOcr, hint: Strings.T("OcrShortcut")));
        toolbarItems.Children.Add(Ui.Action("Scroll", Strings.T("Scroll"), () => Finish("Scroll")));
        foreach (var action in new[] { "Pin", "Save", "Cancel" })
        {
            var value = action; var button = Ui.Tool(action, Strings.T(action), () => { if (value == "Cancel") Close(); else Finish(value); }); Compact(button); toolbarItems.Children.Add(button);
        }
        toolbarItems.Children.Add(Ui.Action("Copy", Strings.T("Copy"), () => Finish("Copy"), true, Strings.T("CopyFinishHint")));
        transforms = new SelectionActions(annotations, CommitText);
        indentLess = Ui.Tool("IndentLess", Strings.T("IndentLess"), () => ChangeIndent(-1));
        indentMore = Ui.Tool("IndentMore", Strings.T("IndentMore"), () => ChangeIndent(1));
        BuildOptions();
        annotations.Changed += Refresh; annotations.TextRequested += BeginText;
        annotations.EmptySelectionDown += e => { if (e.ClickCount == 2) Finish("Copy"); else BeginDrag(e); e.Handled = true; };
        surface.MouseLeftButtonDown += (_, e) => BeginDrag(e);
        surface.MouseLeftButtonUp += Up; surface.MouseMove += Move;
        PreviewMouseWheel += (_, e) =>
        {
            if (selected.IsEmpty && !dragging && detectionMode < 2)
            {
                CycleCandidate(e.Delta > 0 ? -1 : 1); e.Handled = true;
            }
        };
        surface.LostMouseCapture += (_, _) => { if (dragging) CancelCropDrag(); };
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (ocrPanel != null) { if (!Within(e.OriginalSource, ocrPanel)) e.Handled = true; return; }
            if (Within(e.OriginalSource, toolbar) || Within(e.OriginalSource, textInput)) return;
            if (textInput != null)
            {
                CommitText(); annotations.ClearSelection(); annotations.Focus(); e.Handled = true; return;
            }
            if (!selected.IsEmpty && annotations.CurrentTool == Tool.Select && Hit(Physical(e)) >= 0 && !annotations.HitAnnotation(Physical(e) - new Vector(screen.X, screen.Y))) { BeginDrag(e); e.Handled = true; }
        };
        PreviewMouseMove += (_, e) =>
        {
            if (dragging || ocrPanel != null) return;
            pointer = Physical(e); pointerKnown = true;
            if (annotations.CurrentTool == Tool.Select) annotations.Cursor = Hit(pointer) switch { 0 or 4 => Cursors.SizeNWSE, 2 or 6 => Cursors.SizeNESW, 1 or 5 => Cursors.SizeNS, 3 or 7 => Cursors.SizeWE, _ => Cursors.Arrow };
            chrome.InvalidateVisual();
        };
        MouseRightButtonDown += (_, e) =>
        {
            if (exporting || ocrPanel != null) return;
            if (Within(e.OriginalSource, toolbar) || textInput != null) return;
            if (dragging) { CancelCropDrag(); e.Handled = true; return; }
            if (selected.IsEmpty) Close();
            else if (annotations.CurrentTool != Tool.Select || annotations.HasSelection) { SelectTool(Tool.Select); annotations.ClearSelection(); }
            else
            {
                selected = candidate = Rect.Empty; target = 0; preferredCandidate = null;
                pointer = Physical(e); pointerKnown = true; detector.Reset();
                annotations.Visibility = toolbar.Visibility = Visibility.Collapsed; RefreshCandidates();
            }
            e.Handled = true;
        };
        SourceInitialized += (_, _) => Native.PlacePhysical(this, screen);
        root.SizeChanged += (_, _) => { surface.Width = chrome.Width = root.ActualWidth; surface.Height = chrome.Height = root.ActualHeight; Refresh(); PositionOcr(); };
        PreviewKeyDown += Key;
        Loaded += (_, _) =>
        {
            Activate(); Focus(); pointer = PixelPoint(Native.Cursor); pointerKnown = true;
            RefreshCandidates();
        };
        Closed += (_, _) =>
        {
            closed = true; ocrPanel?.Dismiss(); detector.CandidatesChanged -= OnCandidatesChanged; detector.Dispose();
            detectionLifetime.Cancel();
            if (pixelPreparation == null || pixelPreparation.IsCompleted) detectionLifetime.Dispose();
            else _ = pixelPreparation.ContinueWith(_ => detectionLifetime.Dispose(), TaskScheduler.Default);
        };
    }

    private static void Compact(Button b) { b.Width = b.Height = b.MinHeight = 32; b.Padding = new Thickness(6); b.Margin = new Thickness(1); }
    private static bool Within(object? source, DependencyObject? ancestor)
    {
        if (ancestor == null) return false;
        for (var node = source as DependencyObject; node != null;)
        {
            if (node == ancestor) return true;
            // A ComboBox popup has a separate visual root. Its item still
            // belongs to the toolbar; choosing a value must not commit text.
            if (node is ComboBoxItem item && ItemsControl.ItemsControlFromItemContainer(item) is { } owner) node = owner;
            else node = (node is Visual ? VisualTreeHelper.GetParent(node) : null)
                ?? LogicalTreeHelper.GetParent(node)
                ?? (node as FrameworkElement)?.TemplatedParent;
        }
        return false;
    }
    private void BuildOptions()
    {
        options.Margin = new Thickness(5, 3, 5, 2); options.Children.Add(palette); options.Children.Add(sizeOptions); options.Children.Add(textOptions); options.Children.Add(transforms); options.Children.Add(hint);
        foreach (var value in new[] { "#E95D75", "#EFA25E", "#EAC858", "#50A78B", "#5D93D8", "#9571CA", "#FFFFFF", "#302C38" })
        {
            var color = (Color)ColorConverter.ConvertFromString(value); var b = Ui.Button(value, () => { if (textInput != null) { annotations.Ink = color; ApplyTextOptions(); RestoreTextFocus(); } else annotations.SetColor(color); Refresh(); });
            b.Content = new System.Windows.Shapes.Ellipse { Width = 13, Height = 13, Fill = new SolidColorBrush(color), Stroke = Ui.Brush("BorderBrush"), StrokeThickness = 1 };
            b.Width = b.Height = b.MinHeight = 24; b.Padding = new Thickness(4); b.Margin = new Thickness(1); colors.Add((color, b)); palette.Children.Add(b);
        }
        sizeLabel.Margin = new Thickness(10, 0, 5, 0); sizeLabel.VerticalAlignment = VerticalAlignment.Center; sizeOptions.Children.Add(sizeLabel);
        sizeSlider.ValueChanged += (_, _) => { if (!updatingTextOptions) annotations.SetSize(sizeSlider.Value); }; sizeOptions.Children.Add(sizeSlider);
        sizeSlider.PreviewMouseLeftButtonDown += (_, _) => annotations.BeginStyleEdit(); sizeSlider.PreviewMouseLeftButtonUp += (_, _) => annotations.EndStyleEdit(); sizeSlider.LostMouseCapture += (_, _) => annotations.EndStyleEdit();
        font.ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).Order().ToList(); font.Text = annotations.TextFont;
        fontSize.ItemsSource = new[] { "12", "16", "20", "24", "32", "48", "64", "96" }; fontSize.Text = "24";
        foreach (var control in new FrameworkElement[] { font, fontSize, bold, italic, underline, textBackground }) textOptions.Children.Add(control);
        foreach (var (alignment, key) in new[] { (TextAlignment.Left, "AlignLeft"), (TextAlignment.Center, "AlignCenter"), (TextAlignment.Right, "AlignRight") })
        {
            var value = alignment; var button = Ui.Tool(key, Strings.T(key), () => ChangeAlignment(value));
            button.Width = button.Height = button.MinHeight = 26; button.Padding = new Thickness(5); button.Margin = new Thickness(1);
            alignments.Add(value, button); textOptions.Children.Add(button);
        }
        foreach (var button in new[] { indentLess, indentMore })
        {
            button.Width = button.Height = button.MinHeight = 26; button.Padding = new Thickness(5); button.Margin = new Thickness(1); textOptions.Children.Add(button);
        }
        Ui.ObserveText(font, this, () => ApplyTextOptions()); Ui.ObserveText(fontSize, this, () => ApplyTextOptions(true));
        foreach (var control in new[] { font, fontSize })
        {
            control.GotKeyboardFocus += (_, _) => annotations.BeginStyleEdit();
            control.LostKeyboardFocus += (_, _) => { annotations.EndStyleEdit(); UpdateOptions(); };
            control.DropDownClosed += (_, _) => { ApplyTextOptions(control == fontSize); RestoreTextFocus(); };
            control.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter) { ApplyTextOptions(control == fontSize); RestoreTextFocus(); e.Handled = true; }
            };
        }
        System.Windows.Automation.AutomationProperties.SetName(font, Strings.T("Font"));
        System.Windows.Automation.AutomationProperties.SetName(fontSize, Strings.T("FontSize"));
        fontSize.ToolTip = Strings.T("FontSize") + " · 8–300";
        bold.ToolTip = Strings.T("Bold") + " · Ctrl+B"; italic.ToolTip = Strings.T("Italic") + " · Ctrl+I"; underline.ToolTip = Strings.T("Underline") + " · Ctrl+U";
        foreach (var control in new[] { bold, italic, underline, textBackground })
        {
            void Changed(object sender, RoutedEventArgs e) { if (updatingTextOptions) return; ApplyTextOptions(); RestoreTextFocus(); }
            control.Checked += Changed; control.Unchecked += Changed;
        }
        hint.VerticalAlignment = VerticalAlignment.Center; hint.Margin = new Thickness(10, 3, 5, 3);
    }
    private Point Physical(MouseEventArgs e)
    {
        return PixelPoint(PointToScreen(e.GetPosition(this)));
    }
    private Point PixelPoint(Point p) => new(Math.Clamp(Math.Floor(p.X), screen.Left, screen.Right - 1), Math.Clamp(Math.Floor(p.Y), screen.Top, screen.Bottom - 1));
    // Pixel sampling is inclusive; rectangle edges are exclusive. The last
    // accessible screen pixel must still let a drag reach the outer edge.
    private Point BoundaryPoint(Point p)
    {
        var monitor = PhysicalMonitor(p);
        bool rightEdge = p.X >= monitor.Right - 1 && !monitors.Any(r => ContainsPixel(r, new Point(monitor.Right, p.Y)));
        bool bottomEdge = p.Y >= monitor.Bottom - 1 && !monitors.Any(r => ContainsPixel(r, new Point(p.X, monitor.Bottom)));
        return new(rightEdge ? monitor.Right : p.X, bottomEdge ? monitor.Bottom : p.Y);
    }
    private Rect PhysicalMonitor(Point p)
    {
        foreach (var monitor in monitors) if (ContainsPixel(monitor, p)) return monitor;
        return monitors.MinBy(r => (new Point(Math.Clamp(p.X, r.Left, r.Right - 1), Math.Clamp(p.Y, r.Top, r.Bottom - 1)) - p).LengthSquared);
    }
    private Point Local(Point p)
    {
        // A shown window always uses its HWND's real screen-to-DIP conversion.
        // Detached controls use a fallback only during pre-show layout/rendering.
        if (PresentationSource.FromVisual(this) != null) return PointFromScreen(p);
        var dpi = VisualTreeHelper.GetDpi(this); return new((p.X - screen.X) / dpi.DpiScaleX, (p.Y - screen.Y) / dpi.DpiScaleY);
    }
    private Rect Local(Rect r) => new(Local(r.TopLeft), Local(r.BottomRight));
    private static bool ContainsPixel(Rect r, Point p) => !r.IsEmpty && p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
    private Rect PixelBounds(Rect r)
    {
        if (r.IsEmpty || !double.IsFinite(r.Left + r.Top + r.Width + r.Height) || r.Width <= 0 || r.Height <= 0) return Rect.Empty;
        return Rect.Intersect(new Rect(new Point(Math.Floor(r.Left), Math.Floor(r.Top)), new Point(Math.Ceiling(r.Right), Math.Ceiling(r.Bottom))), screen);
    }
    private static bool SameBounds(Rect a, Rect b) => !a.IsEmpty && !b.IsEmpty && Math.Abs(a.Left - b.Left) <= 1 && Math.Abs(a.Top - b.Top) <= 1 && Math.Abs(a.Right - b.Right) <= 1 && Math.Abs(a.Bottom - b.Bottom) <= 1;
    private void OnCandidatesChanged()
    {
        if (!closed && selected.IsEmpty && !dragging && detectionMode == 0) RefreshCandidates();
    }
    private async Task PreparePixelDetection()
    {
        var token = detectionLifetime.Token;
        try
        {
            var result = await PixelRegionDetector.CreateAsync(desktop, screen, token).ConfigureAwait(false);
            if (token.IsCancellationRequested || Dispatcher.HasShutdownStarted) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (closed || token.IsCancellationRequested) return;
                pixelDetector = result;
                if (selected.IsEmpty && !dragging && detectionMode == 0) RefreshCandidates();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closed) Log.Error("Pixel boundary detection", error); }
    }
    private void RefreshCandidates()
    {
        if (closed || dragging || !selected.IsEmpty || !pointerKnown) return;
        candidates.Clear(); candidateIndex = 0; candidate = Rect.Empty; candidateTarget = 0;
        if (detectionMode == 2) { preferredCandidate = null; RefreshVisuals(); return; }
        if (detectionMode == 0 && pixelPreparation == null) pixelPreparation = PreparePixelDetection();
        var detected = detector.Candidates(pointer, detectionMode == 0);
        var owner = detected.LastOrDefault(value => value.IsWindow);
        void Add(Rect bounds, nint handle, string kind)
        {
            bounds = PixelBounds(bounds);
            if (!ContainsPixel(bounds, pointer) || bounds.Width < 2 || bounds.Height < 2) return;
            if (scrollMode && (bounds.Width < 80 || bounds.Height < 100)) return;
            if (candidates.Any(value => SameBounds(value.Bounds, bounds))) return;
            candidates.Add(new(bounds, handle, kind));
        }
        foreach (var item in detected) Add(item.Bounds, item.Handle, item.IsWindow ? "WindowMode" : "ControlRegion");
        if (detectionMode == 0 && pixelDetector != null)
            foreach (var bounds in pixelDetector.FindCandidates(pointer))
                if (owner.Handle == 0 || owner.Bounds.Contains(bounds)) Add(bounds, owner.Handle, "PixelRegion");
        if (preferredCandidate is { } preferred && ContainsPixel(preferred.Bounds, pointer) && preferred.Handle == owner.Handle)
            Add(preferred.Bounds, preferred.Handle, preferred.Kind);
        else preferredCandidate = null;
        candidates.Sort((a, b) => (a.Bounds.Width * a.Bounds.Height).CompareTo(b.Bounds.Width * b.Bounds.Height));
        if (preferredCandidate is { } choice) candidateIndex = Math.Max(0, candidates.FindIndex(item => SameBounds(item.Bounds, choice.Bounds)));
        if (candidates.Count > 0)
        {
            candidate = candidates[candidateIndex].Bounds; candidateTarget = candidates[candidateIndex].Handle;
        }
        RefreshVisuals();
    }
    private void CycleCandidate(int direction)
    {
        if (candidates.Count < 2) return;
        candidateIndex = Math.Clamp(candidateIndex + direction, 0, candidates.Count - 1);
        preferredCandidate = candidates[candidateIndex];
        candidate = preferredCandidate.Bounds; candidateTarget = preferredCandidate.Handle;
        RefreshVisuals();
    }
    private Rect MonitorArea(Point physical)
    {
        if (PresentationSource.FromVisual(this) == null) return new Rect(0, 0, root.ActualWidth, root.ActualHeight);
        return Rect.Intersect(Local(PhysicalMonitor(physical)), new Rect(0, 0, root.ActualWidth, root.ActualHeight));
    }
    private static Point[] Handles(Rect r) => [r.TopLeft, new(r.Left + r.Width / 2, r.Top), r.TopRight, new(r.Right, r.Top + r.Height / 2), r.BottomRight, new(r.Left + r.Width / 2, r.Bottom), r.BottomLeft, new(r.Left, r.Top + r.Height / 2)];
    private int Hit(Point p)
    {
        if (selected.IsEmpty) return -2;
        var points = Handles(selected); int nearest = -1;
        double distance = Math.Pow(9 * VisualTreeHelper.GetDpi(this).DpiScaleX, 2);
        for (int i = 0; i < points.Length; i++)
        {
            double next = (points[i] - p).LengthSquared;
            if (next < distance) { nearest = i; distance = next; }
        }
        if (nearest >= 0) return nearest;
        return selected.Contains(p) ? -1 : -2;
    }
    private void BeginDrag(MouseButtonEventArgs e)
    {
        if (exporting || ocrPanel != null) return;
        CommitText(); pressedPointer = pointer = Physical(e); pointerKnown = true;
        start = BoundaryPoint(pointer); initial = selected; initialTarget = target; initialCrop = Document.Crop; handle = Hit(pointer);
        // Confirm the candidate visible at mouse-down, even if recognition finishes during the click.
        pressedCandidate = selected.IsEmpty && ContainsPixel(candidate, pointer) ? candidate : Rect.Empty;
        pressedTarget = pressedCandidate.IsEmpty ? 0 : candidateTarget;
        if (handle == -2) { target = 0; annotations.ClearSelection(); }
        cropBefore = null;
        dragMoved = false; dragging = true;
        if (!surface.CaptureMouse()) { CancelCropDrag(); return; }
        toolbar.Visibility = Visibility.Collapsed; annotations.IsHitTestVisible = false;
    }
    private void Move(object sender, MouseEventArgs e)
    {
        pointer = Physical(e); pointerKnown = true;
        if (dragging) UpdateDrag(pointer);
        else if (selected.IsEmpty) RefreshCandidates();
        surface.Cursor = Hit(pointer) switch { -1 => Cursors.SizeAll, 0 or 4 => Cursors.SizeNWSE, 2 or 6 => Cursors.SizeNESW, 1 or 5 => Cursors.SizeNS, 3 or 7 => Cursors.SizeWE, _ => Cursors.Cross };
        RefreshVisuals();
    }
    private void UpdateDrag(Point current)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        if (!dragMoved)
        {
            var movement = current - pressedPointer;
            dragMoved = Math.Abs(movement.X) >= SystemParameters.MinimumHorizontalDragDistance * dpi.DpiScaleX ||
                        Math.Abs(movement.Y) >= SystemParameters.MinimumVerticalDragDistance * dpi.DpiScaleY;
        }
        if (handle == -2 && !dragMoved) return;
        var end = BoundaryPoint(current); var delta = end - start;
        if (handle == -2) selected = Normalize(start, end);
        else if (handle == -1) { selected = initial; selected.Offset(current - pressedPointer); selected = Clamp(selected); }
        else
        {
            double l = initial.Left, t = initial.Top, r = initial.Right, b = initial.Bottom;
            if (handle is 0 or 6 or 7) l += delta.X; if (handle is 0 or 1 or 2) t += delta.Y;
            if (handle is 2 or 3 or 4) r += delta.X; if (handle is 4 or 5 or 6) b += delta.Y;
            selected = Normalize(new(l, t), new(r, b));
        }
        target = selected == initial ? initialTarget : 0;
        CheckpointCropChange(); UpdateCrop(); PlaceAnnotations();
    }
    private Rect Normalize(Point p, Point q) => Rect.Intersect(new Rect(new Point(Math.Min(p.X, q.X), Math.Min(p.Y, q.Y)), new Point(Math.Max(p.X, q.X), Math.Max(p.Y, q.Y))), screen);
    private Rect Clamp(Rect r) => new(Math.Clamp(r.X, screen.Left, screen.Right - r.Width), Math.Clamp(r.Y, screen.Top, screen.Bottom - r.Height), r.Width, r.Height);
    private void Up(object sender, MouseButtonEventArgs e)
    {
        if (!dragging) return;
        pointer = Physical(e); pointerKnown = true; UpdateDrag(pointer);
        dragging = false; surface.ReleaseMouseCapture(); annotations.IsHitTestVisible = true;
        if (!dragMoved && handle == -2 && ContainsPixel(pressedCandidate, pointer)) { selected = pressedCandidate; target = pressedTarget; }
        else if (!dragMoved && handle == -2) { selected = initial; target = initialTarget; }
        if (selected.IsEmpty || selected.Width < 2 || selected.Height < 2)
        {
            selected = initial; target = initialTarget; Document.Crop = initialCrop; cropBefore = null;
            if (selected.IsEmpty) { annotations.Visibility = toolbar.Visibility = Visibility.Collapsed; RefreshCandidates(); } else Refresh();
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
        selected = initial; target = initialTarget; Document.Crop = initialCrop; cropBefore = null;
        if (selected.IsEmpty) RefreshCandidates(); else Refresh();
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
    private void RefreshVisuals()
    {
        var next = selected.IsEmpty ? candidate : selected;
        if (next != renderedSelection) { renderedSelection = next; surface.InvalidateVisual(); }
        chrome.InvalidateVisual();
    }
    private void Refresh()
    {
        if (closed) return;
        if (!selected.IsEmpty)
        {
            var next = new Rect(Document.Crop.X + screen.X, Document.Crop.Y + screen.Y, Document.Crop.Width, Document.Crop.Height);
            if (next != selected) target = 0;
            selected = next;
        }
        PlaceAnnotations(); RefreshVisuals(); UpdateOptions(); if (!dragging) PositionToolbar();
    }
    private void PositionToolbar()
    {
        if (selected.IsEmpty || ocrPanel != null) return;
        var area = MonitorArea(PixelPoint(selected.BottomRight - new Vector(1, 1)));
        double width = area.Width;
        toolbar.Visibility = Visibility.Visible; toolbarItems.MaxWidth = options.MaxWidth = Math.Max(150, Math.Min(1024, width - 24));
        textOptions.MaxWidth = Math.Max(150, Math.Min(1000, width - 40));
        toolbar.Measure(new Size(Math.Max(150, width - 12), Math.Max(200, area.Height))); var size = toolbar.DesiredSize; var r = Local(selected);
        Canvas.SetLeft(toolbar, Math.Clamp(r.Right - size.Width, area.Left + 6, Math.Max(area.Left + 6, area.Right - size.Width - 6)));
        double top = r.Bottom + size.Height + 16 <= area.Bottom ? r.Bottom + 10 : r.Top - size.Height - 10;
        Canvas.SetTop(toolbar, Math.Clamp(top, area.Top + 6, Math.Max(area.Top + 6, area.Bottom - size.Height - 6)));
        if (textInput != null) UpdateTextLayout();
    }
    private void SelectTool(Tool tool)
    {
        CancelCropDrag(); annotations.CancelPointer(); CommitText(); annotations.CurrentTool = tool; annotations.Cursor = tool == Tool.Select ? Cursors.Arrow : tool == Tool.Text ? Cursors.IBeam : Cursors.Cross;
        if (tool is Tool.Crop or Tool.Color || tool == Tool.Text && annotations.Selection is { Kind: not Tool.Text }) annotations.ClearSelection(); annotations.Focus(); Refresh();
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
            italic.IsChecked = a?.Kind == Tool.Text ? a.Italic : annotations.TextItalic; underline.IsChecked = a?.Kind == Tool.Text ? a.Underline : annotations.TextUnderline;
        }
        var styledText = textDraft ?? (annotations.Selection is { Kind: Tool.Text } selectedText ? selectedText : null);
        foreach (var item in alignments) item.Value.Tag = item.Key == (styledText?.Alignment ?? annotations.TextAlignment) ? "Selected" : null;
        indentLess.IsEnabled = (styledText?.TextIndent ?? annotations.TextIndent) > 0;
        indentMore.IsEnabled = (styledText?.TextIndent ?? annotations.TextIndent) < 1000;
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
    private void ShowOcr()
    {
        if (exporting || ocrPanel != null || selected.IsEmpty) return;
        CancelCropDrag(); annotations.CancelPointer(); annotations.EndStyleEdit(); CommitText();
        var panel = new OcrPanel(); ocrPanel = panel;
        panel.CopyCompleted += Close;
        root.Children.Add(panel); Panel.SetZIndex(panel, 10);
        toolbar.Visibility = Visibility.Collapsed;
        annotations.IsHitTestVisible = surface.IsHitTestVisible = false;
        panel.CloseRequested += () =>
        {
            root.Children.Remove(panel); if (ReferenceEquals(ocrPanel, panel)) ocrPanel = null;
            annotations.IsHitTestVisible = surface.IsHitTestVisible = true;
            if (!closed) { Refresh(); annotations.Focus(); }
        };
        panel.DragRequested += delta =>
        {
            Canvas.SetLeft(panel, Math.Clamp(Canvas.GetLeft(panel) + delta.X, 6, Math.Max(6, root.ActualWidth - panel.Width - 6)));
            Canvas.SetTop(panel, Math.Clamp(Canvas.GetTop(panel) + delta.Y, 6, Math.Max(6, root.ActualHeight - panel.Height - 6)));
        };
        PositionOcr(); panel.Focus(); _ = panel.RecognizeAsync(Document.Fork());
    }
    private void PositionOcr()
    {
        if (ocrPanel is not { } panel) return;
        panel.Width = Math.Max(180, Math.Min(560, root.ActualWidth - 24));
        panel.Height = Math.Max(180, Math.Min(530, root.ActualHeight - 24));
        var r = Local(selected);
        double x = r.Right + panel.Width + 18 <= root.ActualWidth ? r.Right + 12 : r.Left >= panel.Width + 18 ? r.Left - panel.Width - 12 : (root.ActualWidth - panel.Width) / 2;
        Canvas.SetLeft(panel, Math.Clamp(x, 6, Math.Max(6, root.ActualWidth - panel.Width - 6)));
        Canvas.SetTop(panel, Math.Clamp(r.Top, 6, Math.Max(6, root.ActualHeight - panel.Height - 6)));
    }
    private static Tool? KeyTool(Key key) => key switch { System.Windows.Input.Key.V => Tool.Select, System.Windows.Input.Key.R => Tool.Rectangle, System.Windows.Input.Key.E => Tool.Ellipse, System.Windows.Input.Key.L => Tool.Line, System.Windows.Input.Key.A => Tool.Arrow, System.Windows.Input.Key.P => Tool.Pen, System.Windows.Input.Key.H => Tool.Highlighter, System.Windows.Input.Key.T => Tool.Text, System.Windows.Input.Key.M => Tool.Mosaic, System.Windows.Input.Key.B => Tool.Blur, System.Windows.Input.Key.X => Tool.Crop, System.Windows.Input.Key.I => Tool.Color, _ => null };
    private void Key(object sender, KeyEventArgs e)
    {
        if (ocrPanel != null) { if (e.Key == System.Windows.Input.Key.Escape) { ocrPanel.Dismiss(); e.Handled = true; } return; }
        if (exporting) return;
        if (Within(e.OriginalSource, textInput))
        {
            // ImeProcessed keys reach WPF text services unchanged.
            if (e.Key == System.Windows.Input.Key.Escape) { CancelText(); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { CommitText(); annotations.Focus(); e.Handled = true; }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is System.Windows.Input.Key.B or System.Windows.Input.Key.I or System.Windows.Input.Key.U)
            {
                var toggle = e.Key == System.Windows.Input.Key.B ? bold : e.Key == System.Windows.Input.Key.I ? italic : underline;
                toggle.IsChecked = toggle.IsChecked != true; e.Handled = true;
            }
            return;
        }
        if (e.OriginalSource is TextBox || Within(e.OriginalSource, font) || Within(e.OriginalSource, fontSize)) return;
        if (textInput != null)
        {
            // Toolbar focus must never turn typing shortcuts into screenshot actions.
            if (e.Key == System.Windows.Input.Key.Escape) CancelText();
            else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key is System.Windows.Input.Key.Z or System.Windows.Input.Key.Y) History(e.Key == System.Windows.Input.Key.Y);
            else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == System.Windows.Input.Key.Enter) { CommitText(); annotations.Focus(); }
            else RestoreTextFocus();
            e.Handled = true; return;
        }
        if (e.Key == System.Windows.Input.Key.Escape) { Close(); e.Handled = true; return; }
        if (selected.IsEmpty && !dragging && e.Key == System.Windows.Input.Key.Tab)
        {
            detectionMode = (detectionMode + (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 2 : 1)) % 3;
            preferredCandidate = null; detector.Reset(); RefreshCandidates(); e.Handled = true; return;
        }
        if (selected.IsEmpty && !dragging && e.Key is System.Windows.Input.Key.PageUp or System.Windows.Input.Key.PageDown)
        {
            CycleCandidate(e.Key == System.Windows.Input.Key.PageUp ? -1 : 1); e.Handled = true; return;
        }
        if (e.Key == System.Windows.Input.Key.C && Keyboard.Modifiers == ModifierKeys.None) { try { var c = PixelColor(pointer); Clipboard.SetText($"#{c.R:X2}{c.G:X2}{c.B:X2}"); } catch (Exception ex) { Ui.Error(ex, this); } e.Handled = true; return; }
        if (selected.IsEmpty) return;
        bool control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (control && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && e.Key == System.Windows.Input.Key.O) { ShowOcr(); e.Handled = true; }
        else if (control && e.Key is System.Windows.Input.Key.Z or System.Windows.Input.Key.Y) { History(e.Key == System.Windows.Input.Key.Y); e.Handled = true; }
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
                Document.Checkpoint(); selected = next; target = 0;
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
        italic.IsChecked = annotation.Italic; underline.IsChecked = annotation.Underline;
        annotations.TextAlignment = annotation.Alignment; annotations.TextIndent = Math.Clamp(annotation.TextInset, 0, 1000);
        updatingTextOptions = false;
        textInput = new TextBox { Text = annotation.Text, AcceptsReturn = true, Padding = new Thickness(0), BorderThickness = new Thickness(1), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        textInput.SetResourceReference(Control.BorderBrushProperty, "AccentBrush");
        textInput.SetResourceReference(TextBox.CaretBrushProperty, "AccentBrush");
        TextOptions.SetTextFormattingMode(textInput, TextFormattingMode.Ideal);
        InputMethod.SetIsInputMethodEnabled(textInput, true);
        System.Windows.Automation.AutomationProperties.SetName(textInput, Strings.T("Text"));
        textInput.TextChanged += (_, _) => { UpdateTextLayout(); UpdateOptions(); };
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
        annotations.TextItalic = italic.IsChecked == true; annotations.TextUnderline = underline.IsChecked == true;
        if (textInput == null || textDraft == null) { annotations.SetTextStyle(annotations.TextFont, annotations.TextSize, annotations.TextBold, annotations.TextBackground, annotations.TextItalic, annotations.TextUnderline); return; }
        textDraft.Font = annotations.TextFont; textDraft.FontSize = annotations.TextSize; textDraft.Bold = annotations.TextBold; textDraft.Background = annotations.TextBackground; textDraft.Color = annotations.Ink;
        textDraft.Italic = annotations.TextItalic; textDraft.Underline = annotations.TextUnderline;
        UpdateTextLayout(); UpdateOptions();
    }
    private void UpdateTextLayout()
    {
        if (textInput == null || textDraft == null || textOriginal == null) return;
        textDraft.Text = textInput.Text;
        // A clicked note can temporarily wrap at the crop edge. Recompute
        // that width from the text so undo and shortening can restore auto size.
        textDraft.TextWidth = textOriginal.TextWidth;
        textDraft.MeasureText();
        double available = Math.Max(1, Document.Crop.Right - textDraft.Bounds.X);
        if (addingText && textDraft.TextWidth == 0 && textDraft.Bounds.Width > available)
        {
            textDraft.TextWidth = available; textDraft.MeasureText();
        }
        double zoom = annotations.Zoom;
        if (textInput.FontFamily.Source != textDraft.Font) textInput.FontFamily = new FontFamily(textDraft.Font);
        textInput.FontSize = textDraft.FontSize * zoom; textInput.FontWeight = textDraft.Bold ? FontWeights.Bold : FontWeights.Normal;
        textInput.FontStyle = textDraft.Italic ? FontStyles.Italic : FontStyles.Normal;
        textInput.TextDecorations = textDraft.Underline ? TextDecorations.Underline : null;
        textInput.TextAlignment = textDraft.Alignment;
        bool wrap = textDraft.TextWidth > 0;
        textInput.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        if (textInput.Foreground is not SolidColorBrush ink || ink.Color != textDraft.Color)
            textInput.Foreground = new SolidColorBrush(textDraft.Color);
        var background = textDraft.Background ? Color.FromArgb(230, 255, 255, 255) : Colors.Transparent;
        if (textInput.Background is not SolidColorBrush fill || fill.Color != background)
            textInput.Background = new SolidColorBrush(background);
        var origin = Local(new Point(textDraft.Bounds.X + screen.X, textDraft.Bounds.Y + screen.Y));
        // WPF TextBox reserves two more DIPs per side for its caret. Keep its
        // text content aligned with the annotation wherever the viewport fits.
        double inset = textDraft.TextInset * zoom;
        double glyphWidth = Math.Max(12, textInput.FontSize);
        double width = Math.Max(1, textDraft.Bounds.Width * zoom);
        if (textDraft.Text.Length == 0 || wrap && width - inset < glyphWidth)
            width = Math.Max(width, inset + Math.Max(24, glyphWidth));
        double lineHeight = Math.Max(1, textInput.FontFamily.LineSpacing * textInput.FontSize);
        double height = Math.Max(textDraft.Bounds.Height * zoom, lineHeight);
        // The native caret can occupy a trailing blank line that FormattedText
        // does not include in the exported height.
        if (textDraft.Text.Length > 0 && textDraft.Text[^1] is '\r' or '\n' or '\u2028' or '\u2029') height += lineHeight;
        var preferred = new Size(width + 6, Math.Ceiling(height) + 2);

        // Keep the annotation's orientation. Any translation or scrolling
        // needed at a screen edge belongs only to the live editor.
        var local = Matrix.Identity;
        double x = textDraft.Bounds.X - 3 / zoom, y = textDraft.Bounds.Y - 1 / zoom;
        local.Translate(x, y); local.Append(textDraft.Transform); local.Translate(-x, -y);
        local.OffsetX *= zoom; local.OffsetY *= zoom;
        var transform = new MatrixTransform(local);
        var anchor = textDraft.ToWorld(textDraft.Bounds.TopLeft) + new Vector(screen.X, screen.Y);
        var area = MonitorArea(anchor);
        if (area.IsEmpty || area.Width <= 0 || area.Height <= 0) area = Local(screen);
        var placement = PlaceTextEditor(preferred, area, transform, new Point(origin.X - 3, origin.Y - 1), glyphWidth + 6, lineHeight * 3 + 2);
        var viewport = placement.Size;
        bool clippedWidth = viewport.Width + .001 < preferred.Width;
        bool clippedHeight = viewport.Height + .001 < preferred.Height;
        textInput.Width = viewport.Width; textInput.Height = viewport.Height;
        textInput.Padding = new Thickness(Math.Min(inset, Math.Max(0, viewport.Width - glyphWidth - 6)), 0, 0, 0);
        // Hidden keeps native caret and wheel scrolling without reducing the
        // content width by the size of a visible scrollbar.
        textInput.HorizontalScrollBarVisibility = clippedWidth && !wrap ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
        textInput.VerticalScrollBarVisibility = clippedHeight || clippedWidth && wrap ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
        Canvas.SetLeft(textInput, placement.Position.X);
        Canvas.SetTop(textInput, placement.Position.Y);
        if (textInput.RenderTransform is not MatrixTransform current || current.Matrix != local)
            textInput.RenderTransform = transform;
    }
    private readonly record struct TextViewport(Size Size, Point Position, Rect Bounds);
    private TextViewport PlaceTextEditor(Size desired, Rect area, MatrixTransform transform, Point origin, double minWidth, double minHeight)
    {
        var normal = PlaceTextViewport(desired, area, transform, origin, minWidth, minHeight);
        double left = Canvas.GetLeft(toolbar), top = Canvas.GetTop(toolbar);
        var size = new Size(Math.Max(toolbar.DesiredSize.Width, toolbar.ActualWidth), Math.Max(toolbar.DesiredSize.Height, toolbar.ActualHeight));
        if (toolbar.Visibility != Visibility.Visible || !double.IsFinite(left + top) || size.Width <= 0 || size.Height <= 0)
            return normal;
        var blocked = new Rect(new Point(left, top), size);
        blocked.Inflate(4, 4);
        blocked.Intersect(area);
        if (blocked.IsEmpty || !normal.Bounds.IntersectsWith(blocked)) return normal;

        // A shifted, scrollable editor can still use its normal orientation
        // while leaving the whole toolbar accessible. Only move on overlap.
        Rect[] clear =
        [
            new(area.Left, area.Top, area.Width, blocked.Top - area.Top),
            new(area.Left, blocked.Bottom, area.Width, area.Bottom - blocked.Bottom),
            new(area.Left, area.Top, blocked.Left - area.Left, area.Height),
            new(blocked.Right, area.Top, area.Right - blocked.Right, area.Height)
        ];
        TextViewport? best = null;
        (double Readability, bool WidthClipped, double Distance) bestScore = default;
        double usefulWidth = Math.Min(desired.Width, minWidth), usefulHeight = Math.Min(desired.Height, minHeight);
        foreach (var free in clear)
        {
            if (free.Width < 1 || free.Height < 1) continue;
            var candidate = PlaceTextViewport(desired, free, transform, origin, minWidth, minHeight);
            double readability = Math.Min(1, Math.Min(candidate.Size.Width / usefulWidth, candidate.Size.Height / usefulHeight));
            var score = (-readability, candidate.Size.Width + .001 < desired.Width, (candidate.Position - origin).LengthSquared);
            if (best == null || score.CompareTo(bestScore) < 0) { best = candidate; bestScore = score; }
        }
        return best ?? normal;
    }
    private static TextViewport PlaceTextViewport(Size desired, Rect area, MatrixTransform transform, Point origin, double minWidth, double minHeight)
    {
        var size = FitTextViewport(desired, area.Size, transform.Matrix, minWidth, minHeight);
        var bounds = transform.TransformBounds(new Rect(new Point(), size));
        bounds.Offset(origin.X, origin.Y);
        var offset = new Vector(
            Math.Clamp(bounds.Left, area.Left, Math.Max(area.Left, area.Right - bounds.Width)) - bounds.Left,
            Math.Clamp(bounds.Top, area.Top, Math.Max(area.Top, area.Bottom - bounds.Height)) - bounds.Top);
        bounds.Offset(offset);
        return new TextViewport(size, origin + offset, bounds);
    }
    private static Size FitTextViewport(Size desired, Size area, Matrix transform, double minWidth, double minHeight)
    {
        double wx = Math.Abs(transform.M11), wy = Math.Abs(transform.M12);
        double hx = Math.Abs(transform.M21), hy = Math.Abs(transform.M22);
        if (wx * desired.Width + hx * desired.Height <= area.Width && wy * desired.Width + hy * desired.Height <= area.Height)
            return desired;

        // Account for both axes of rotated text. Hold room for one glyph
        // and a few lines where possible before fitting the natural width.
        minWidth = Math.Min(minWidth, desired.Width);
        minHeight = Math.Min(minHeight, desired.Height);
        double scale = Math.Min(1, Math.Min(area.Width / (wx * minWidth + hx * minHeight), area.Height / (wy * minWidth + hy * minHeight)));
        minHeight *= scale;
        double width = Math.Min(desired.Width, AxisLength(area.Width - hx * minHeight, area.Height - hy * minHeight, wx, wy));
        double height = Math.Min(desired.Height, AxisLength(area.Width - wx * width, area.Height - wy * width, hx, hy));
        return new Size(Math.Max(1, width), Math.Max(1, height));

        static double AxisLength(double x, double y, double weightX, double weightY) =>
            Math.Min(weightX > 1e-8 ? x / weightX : double.PositiveInfinity, weightY > 1e-8 ? y / weightY : double.PositiveInfinity);
    }
    private void RestoreTextFocus()
    {
        var input = textInput;
        if (input == null || updatingTextOptions || closed) return;
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (!closed && textInput == input) { input.Focus(); Keyboard.Focus(input); }
        }));
    }
    private void ChangeAlignment(TextAlignment alignment)
    {
        if (textDraft != null) { textDraft.Alignment = alignment; annotations.TextAlignment = alignment; UpdateTextLayout(); UpdateOptions(); RestoreTextFocus(); }
        else annotations.SetTextStyle(annotations.Selection?.Font ?? annotations.TextFont, annotations.Selection?.FontSize ?? annotations.TextSize, bold.IsChecked == true, textBackground.IsChecked == true,
            italic.IsChecked == true, underline.IsChecked == true, alignment);
    }
    private void ChangeIndent(int direction)
    {
        var current = textDraft ?? (annotations.Selection is { Kind: Tool.Text } value ? value : null);
        double indent = Math.Clamp((current?.TextIndent ?? annotations.TextIndent) + direction * (current?.FontSize ?? annotations.TextSize), 0, 1000);
        if (textDraft != null) { textDraft.TextIndent = indent; annotations.TextIndent = indent; UpdateTextLayout(); UpdateOptions(); RestoreTextFocus(); }
        else annotations.SetTextStyle(current?.Font ?? annotations.TextFont, current?.FontSize ?? annotations.TextSize, bold.IsChecked == true, textBackground.IsChecked == true,
            italic.IsChecked == true, underline.IsChecked == true, current?.Alignment ?? annotations.TextAlignment, indent);
    }
    private void CommitText()
    {
        if (textInput == null || textOriginal == null || textDraft == null) return;
        // Let native text services finish focus loss while the editor is still
        // attached. Removing it first can discard an outstanding IME result.
        if (textInput.IsKeyboardFocusWithin) Keyboard.Focus(annotations);
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
        desktop.CopyPixels(new Int32Rect(Math.Clamp((int)(p.X - screen.X), 0, desktop.PixelWidth - 1), Math.Clamp((int)(p.Y - screen.Y), 0, desktop.PixelHeight - 1), 1, 1), pixelBytes, 4, 0); return Color.FromRgb(pixelBytes[2], pixelBytes[1], pixelBytes[0]);
    }
    private async void Finish(string action)
    {
        if (exporting || dragging || selected.IsEmpty || selected.Width < 1 || selected.Height < 1 || Accepted == null) return;
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
            dc.DrawImage(o.desktop, o.Local(o.screen));
            var selection = o.selected.IsEmpty ? o.candidate : o.selected;
            var full = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
            Geometry shade = selection.IsEmpty ? full : Geometry.Combine(full, new RectangleGeometry(o.Local(selection)), GeometryCombineMode.Exclude, null);
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(88, 0, 0, 0)), null, shade);
        }
    }
    private sealed class OverlayChrome(CaptureOverlay o) : FrameworkElement
    {
        private static readonly Brush PanelFill = new SolidColorBrush(Color.FromRgb(25, 29, 36));
        private static readonly Brush GridBrush = new SolidColorBrush(Color.FromArgb(85, 110, 122, 140));
        private static readonly Brush FrameContrast = new SolidColorBrush(Color.FromArgb(170, 15, 19, 27));
        private Int32Rect cachedPixels;
        private BitmapSource? cachedImage;
        protected override void OnRender(DrawingContext dc)
        {
            var selection = o.selected.IsEmpty ? o.candidate : o.selected;
            if (!selection.IsEmpty)
            {
                var r = o.Local(selection);
                double thickness = o.selected.IsEmpty ? 1.5 : 2;
                dc.DrawRectangle(null, new System.Windows.Media.Pen(FrameContrast, thickness + 1.5), r);
                dc.DrawRectangle(null, new System.Windows.Media.Pen(Ui.Accent, thickness), r);
                if (!o.selected.IsEmpty)
                    foreach (var p in Handles(r)) dc.DrawRectangle(Brushes.White, new System.Windows.Media.Pen(Ui.Accent, 1.5), new Rect(p.X - 3.5, p.Y - 3.5, 7, 7));
                var badgeArea = o.MonitorArea(o.PixelPoint(selection.TopLeft));
                string size = string.Format(CultureInfo.CurrentCulture, Strings.T("PixelDimensions"), selection.Width, selection.Height);
                Label(dc, size, new Point(r.Left, r.Top - 31), 14, badgeArea, true);
                if (o.selected.IsEmpty && o.candidates.Count > 0)
                {
                    string detail = o.candidates.Count > 1 ? string.Format(CultureInfo.CurrentCulture, Strings.T("CandidateDetails"), Strings.T(o.candidates[o.candidateIndex].Kind), o.candidateIndex + 1, o.candidates.Count) : Strings.T(o.candidates[0].Kind);
                    Label(dc, detail, new Point(r.Left, r.Bottom + 8), 11, badgeArea);
                }
            }
            if (o.selected.IsEmpty)
            {
                var area = o.MonitorArea(o.pointer);
                double top = !selection.IsEmpty && o.Local(selection).Top < area.Top + 64 ? area.Top + 64 : area.Top + 20;
                string hintKey = o.detectionMode == 2 ? "FreeSelectHint" : o.scrollMode ? "ScrollSelectHint" : "CaptureSelectHint";
                Label(dc, Strings.T(hintKey) + "  ·  " + Strings.T(new[] { "ElementMode", "WindowMode", "FreeMode" }[o.detectionMode]), new Point(area.Left + 22, top), 12, area);
            }
            bool inspecting = o.selected.IsEmpty || o.dragging || o.annotations.CurrentTool == Tool.Color || o.annotations.CurrentTool == Tool.Select && o.Hit(o.pointer) >= 0;
            if (o.settings.Magnifier && inspecting && o.pointerKnown && (o.IsMouseOver || o.selected.IsEmpty) &&
                !o.exporting && o.ocrPanel == null && o.textInput == null && !Within(Mouse.DirectlyOver, o.toolbar)) DrawMagnifier(dc, selection);
        }
        private void DrawMagnifier(DrawingContext dc, Rect selection)
        {
            const int count = 17, cell = 8, side = count * cell;
            const double panelWidth = 208, panelHeight = 242;
            var pointer = o.Local(o.pointer); var area = o.MonitorArea(o.pointer);
            if (area.Width < panelWidth + 12 || area.Height < panelHeight + 12) return;
            var box = PlaceMagnifier(pointer, area, selection.IsEmpty ? Rect.Empty : o.Local(selection), panelWidth, panelHeight);
            dc.DrawRoundedRectangle(PanelFill, new System.Windows.Media.Pen(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), 1), box, 9, 9);
            Text(dc, Strings.T("PixelMagnifier"), new Point(box.X + 12, box.Y + 10), 12);
            var grid = new Rect(box.X + (panelWidth - side) / 2, box.Y + 34, side, side);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(49, 54, 64)), null, grid);
            int centerX = Math.Clamp((int)(o.pointer.X - o.screen.X), 0, o.desktop.PixelWidth - 1);
            int centerY = Math.Clamp((int)(o.pointer.Y - o.screen.Y), 0, o.desktop.PixelHeight - 1);
            int originX = centerX - count / 2, originY = centerY - count / 2;
            int left = Math.Max(0, originX), top = Math.Max(0, originY);
            var pixels = new Int32Rect(left, top, Math.Min(o.desktop.PixelWidth, originX + count) - left, Math.Min(o.desktop.PixelHeight, originY + count) - top);
            if (cachedImage == null || cachedPixels != pixels)
            {
                cachedPixels = pixels; cachedImage = new CroppedBitmap(o.desktop, pixels); cachedImage.Freeze();
            }
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
            dc.DrawImage(cachedImage, new Rect(grid.X + (left - originX) * cell, grid.Y + (top - originY) * cell, pixels.Width * cell, pixels.Height * cell));
            var gridPen = new System.Windows.Media.Pen(GridBrush, 1 / VisualTreeHelper.GetDpi(this).DpiScaleX);
            for (int i = 0; i <= count; i++)
            {
                dc.DrawLine(gridPen, new Point(grid.X + i * cell, grid.Top), new Point(grid.X + i * cell, grid.Bottom));
                dc.DrawLine(gridPen, new Point(grid.Left, grid.Y + i * cell), new Point(grid.Right, grid.Y + i * cell));
            }
            if (!selection.IsEmpty)
            {
                dc.PushClip(new RectangleGeometry(grid));
                dc.DrawRectangle(null, new System.Windows.Media.Pen(Ui.Accent, 2), new Rect(grid.X + (selection.Left - o.screen.X - originX) * cell, grid.Y + (selection.Top - o.screen.Y - originY) * cell, selection.Width * cell, selection.Height * cell));
                dc.Pop();
            }
            var center = new Rect(grid.X + count / 2 * cell, grid.Y + count / 2 * cell, cell, cell);
            dc.DrawRectangle(null, new System.Windows.Media.Pen(Brushes.White, 3), center);
            dc.DrawRectangle(null, new System.Windows.Media.Pen(Brushes.Crimson, 1.5), center);
            var color = o.PixelColor(o.pointer);
            Text(dc, $"X {o.pointer.X:0}  Y {o.pointer.Y:0} px", new Point(box.X + 12, box.Y + 180), 12, true);
            dc.DrawRectangle(new SolidColorBrush(color), new System.Windows.Media.Pen(Brushes.White, .7), new Rect(box.X + 12, box.Y + 202, 12, 12));
            Text(dc, $"#{color.R:X2}{color.G:X2}{color.B:X2}  RGB {color.R},{color.G},{color.B}", new Point(box.X + 30, box.Y + 200), 10.5, true);
            Text(dc, Strings.T("PixelMagnifierHint"), new Point(box.X + 12, box.Y + 221), 10);
        }
        private static Rect PlaceMagnifier(Point p, Rect area, Rect selection, double width, double height)
        {
            List<Point> positions = [new(p.X + 28, p.Y + 28), new(p.X - width - 28, p.Y + 28), new(p.X + 28, p.Y - height - 28), new(p.X - width - 28, p.Y - height - 28)];
            var avoid = selection;
            if (!selection.IsEmpty)
            {
                // Include the size and candidate badges around small controls.
                avoid = new Rect(selection.Left, selection.Top - 34, selection.Width, selection.Height + 72);
                if (selection.Bottom - p.Y < 110) positions.Add(new(p.X + 28, selection.Bottom + 40));
                if (selection.Right - p.X < 110) positions.Add(new(selection.Right + 16, p.Y + 28));
            }
            Rect best = Rect.Empty; double score = double.PositiveInfinity;
            foreach (var point in positions)
            {
                var bounds = new Rect(Math.Clamp(point.X, area.Left + 6, area.Right - width - 6), Math.Clamp(point.Y, area.Top + 6, area.Bottom - height - 6), width, height);
                var overlap = Rect.Intersect(bounds, avoid);
                double value = (overlap.IsEmpty ? 0 : overlap.Width * overlap.Height) + (bounds.TopLeft - point).LengthSquared;
                if (value < score) { best = bounds; score = value; }
            }
            return best;
        }
        private FormattedText Format(string value, double size, bool pixel = false)
            => new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(pixel ? "Consolas" : "Microsoft YaHei UI"), size, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        private void Text(DrawingContext dc, string value, Point point, double size, bool pixel = false) => dc.DrawText(Format(value, size, pixel), point);
        private void Label(DrawingContext dc, string value, Point point, double size, Rect area, bool pixel = false)
        {
            if (area.IsEmpty || area.Width < 24 || area.Height < 24) return;
            var text = Format(value, size, pixel); text.MaxTextWidth = Math.Max(1, area.Width - 24); text.Trimming = TextTrimming.CharacterEllipsis;
            double width = text.Width + 14, height = text.Height + 8;
            point.X = Math.Clamp(point.X, area.Left + 6, Math.Max(area.Left + 6, area.Right - width - 6));
            point.Y = Math.Clamp(point.Y, area.Top + 6, Math.Max(area.Top + 6, area.Bottom - height - 6));
            dc.DrawRoundedRectangle(PanelFill, new System.Windows.Media.Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1), new Rect(point, new Size(width, height)), 5, 5);
            dc.DrawText(text, point + new Vector(7, 4));
        }
    }
}
