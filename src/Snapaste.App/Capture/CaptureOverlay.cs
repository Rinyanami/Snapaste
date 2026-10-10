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
    private readonly Canvas root = new();
    private readonly Border toolbar;
    private readonly WrapPanel toolbarItems = new() { MaxWidth = 740 };
    private Rect selected = Rect.Empty, candidate = Rect.Empty, initial;
    private Point start, pointer;
    private bool dragging;
    private int handle = -2, detectionMode;
    private nint target;
    private DateTime lastDetection;
    private readonly bool scrollMode;
    public event Action<Rect, BitmapSource, string, nint>? Accepted;
    public CaptureOverlay(BitmapSource desktop, Rect screen, Settings settings, bool scroll)
    {
        this.desktop = desktop; this.screen = screen; this.settings = settings; scrollMode = scroll;
        detectionMode = settings.UiElements ? 0 : settings.AutoWindow ? 1 : 2;
        Title = "Snapaste · 选区"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true; Background = Brushes.Black; Cursor = Cursors.Cross;
        Ui.Setup(this); Content = root; surface = new OverlaySurface(this); toolbar = Ui.Card(toolbarItems, 5, true); root.Children.Add(surface); root.Children.Add(toolbar); toolbar.Visibility = Visibility.Collapsed;
        foreach (var tool in new[] { "Rectangle", "Ellipse", "Line", "Arrow", "Pen", "Highlighter", "Text", "Mosaic", "Blur", "Crop", "Color" }) { string t = tool; toolbarItems.Children.Add(Ui.Tool(t, Strings.T(t), () => Finish(t), "进入编辑 · " + Strings.T(t))); }
        toolbarItems.Children.Add(Ui.Divider());
        toolbarItems.Children.Add(Ui.Action("Scroll", Strings.T("Scroll"), () => Finish("Scroll")));
        toolbarItems.Children.Add(Ui.Tool("Pin", Strings.T("Pin"), () => Finish("Pin")));
        toolbarItems.Children.Add(Ui.Tool("Save", Strings.T("Save"), () => Finish("Save"), "保存 · Ctrl+S"));
        toolbarItems.Children.Add(Ui.Tool("Edit", Strings.T("Edit"), () => Finish("Edit"), "进入编辑 · 双击选区"));
        toolbarItems.Children.Add(Ui.Action("Copy", Strings.T("Copy"), () => Finish("Copy"), true, "复制并完成 · Enter"));
        foreach (var button in toolbarItems.Children.OfType<Button>().Where(b => b.Content is Viewbox)) { button.Width = 32; button.Height = 32; button.MinHeight = 32; button.Padding = new Thickness(6); button.Margin = new Thickness(1); }
        SourceInitialized += (_, _) => Native.PlacePhysical(this, screen);
        SizeChanged += (_, _) => { surface.Width = ActualWidth; surface.Height = ActualHeight; surface.InvalidateVisual(); };
        surface.MouseLeftButtonDown += Down; surface.MouseLeftButtonUp += Up; surface.MouseMove += Move;
        surface.MouseLeave += (_, _) => surface.InvalidateVisual();
        surface.MouseRightButtonDown += (_, _) => { if (selected.IsEmpty) Close(); else { selected = Rect.Empty; toolbar.Visibility = Visibility.Collapsed; surface.InvalidateVisual(); } };
        PreviewKeyDown += Key;
        Loaded += (_, _) => { Activate(); Focus(); };
    }
    private Point Physical(MouseEventArgs e) { var p = PointToScreen(e.GetPosition(this)); return new(Math.Clamp(Math.Round(p.X), screen.Left, screen.Right - 1), Math.Clamp(Math.Round(p.Y), screen.Top, screen.Bottom - 1)); }
    private Rect Local(Rect r)
    {
        var p = PointFromScreen(r.TopLeft); var q = PointFromScreen(r.BottomRight); return new(p, q);
    }
    private Point[] Handles(Rect r) => [r.TopLeft, new(r.Left + r.Width / 2, r.Top), r.TopRight, new(r.Right, r.Top + r.Height / 2), r.BottomRight, new(r.Left + r.Width / 2, r.Bottom), r.BottomLeft, new(r.Left, r.Top + r.Height / 2)];
    private int Hit(Point p)
    {
        if (selected.IsEmpty) return -2;
        var handles = Handles(selected); for (int i = 0; i < handles.Length; i++) if ((handles[i] - p).Length < 9 * VisualTreeHelper.GetDpi(this).DpiScaleX) return i;
        return selected.Contains(p) ? -1 : -2;
    }
    private void Down(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && !selected.IsEmpty) { Finish("Edit"); return; }
        start = pointer = Physical(e); initial = selected; handle = Hit(start); dragging = true; surface.CaptureMouse(); toolbar.Visibility = Visibility.Collapsed;
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
                if (handle is 0 or 6 or 7) l += delta.X;
                if (handle is 0 or 1 or 2) t += delta.Y;
                if (handle is 2 or 3 or 4) r += delta.X;
                if (handle is 4 or 5 or 6) b += delta.Y;
                selected = Normalize(new(l, t), new(r, b));
            }
        }
        else if (selected.IsEmpty && detectionMode < 2 && (DateTime.UtcNow - lastDetection).TotalMilliseconds > 70)
        {
            lastDetection = DateTime.UtcNow; (candidate, target) = detector.Find(pointer, detectionMode == 0); candidate.Intersect(screen);
        }
        int hover = Hit(pointer);
        surface.Cursor = hover switch { -1 => Cursors.SizeAll, 0 or 4 => Cursors.SizeNWSE, 2 or 6 => Cursors.SizeNESW, 1 or 5 => Cursors.SizeNS, 3 or 7 => Cursors.SizeWE, _ => Cursors.Cross };
        surface.InvalidateVisual();
    }
    private Rect Normalize(Point p, Point q) => Rect.Intersect(new Rect(new Point(Math.Min(p.X, q.X), Math.Min(p.Y, q.Y)), new Point(Math.Max(p.X, q.X), Math.Max(p.Y, q.Y))), screen);
    private Rect Clamp(Rect r) => new(Math.Clamp(r.X, screen.Left, screen.Right - r.Width), Math.Clamp(r.Y, screen.Top, screen.Bottom - r.Height), r.Width, r.Height);
    private void Up(object sender, MouseButtonEventArgs e)
    {
        if (!dragging) return; dragging = false; surface.ReleaseMouseCapture();
        if ((pointer - start).Length < 4 && handle == -2) selected = candidate;
        if (selected.IsEmpty || selected.Width < 2 || selected.Height < 2) { selected = Rect.Empty; return; }
        if (scrollMode) { Finish("Scroll"); return; }
        PositionToolbar(); surface.InvalidateVisual();
    }
    private void PositionToolbar()
    {
        if (selected.IsEmpty) return;
        toolbar.Visibility = Visibility.Visible; toolbar.Measure(new Size(ActualWidth, 200)); var size = toolbar.DesiredSize;
        var r = Local(selected); Canvas.SetLeft(toolbar, Math.Clamp(r.Right - size.Width, 0, Math.Max(0, ActualWidth - size.Width)));
        Canvas.SetTop(toolbar, r.Bottom + size.Height + 14 < ActualHeight ? r.Bottom + 10 : Math.Max(0, r.Top - size.Height - 10));
    }
    private void Key(object sender, KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape) { Close(); return; }
        if (e.Key == System.Windows.Input.Key.Tab) { detectionMode = (detectionMode + 1) % 3; candidate = Rect.Empty; e.Handled = true; surface.InvalidateVisual(); return; }
        if (e.Key == System.Windows.Input.Key.C && Keyboard.Modifiers == ModifierKeys.None) { try { var c = PixelColor(pointer); Clipboard.SetText($"#{c.R:X2}{c.G:X2}{c.B:X2}"); } catch (Exception ex) { Ui.Error(ex); } return; }
        if (selected.IsEmpty) return;
        if (e.Key == System.Windows.Input.Key.Enter || e.Key == System.Windows.Input.Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { Finish("Copy"); return; }
        if (e.Key == System.Windows.Input.Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { Finish("Save"); return; }
        if (e.Key is System.Windows.Input.Key.Left or System.Windows.Input.Key.Right or System.Windows.Input.Key.Up or System.Windows.Input.Key.Down)
        {
            int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 10 : 1; double dx = e.Key == System.Windows.Input.Key.Left ? -step : e.Key == System.Windows.Input.Key.Right ? step : 0, dy = e.Key == System.Windows.Input.Key.Up ? -step : e.Key == System.Windows.Input.Key.Down ? step : 0;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) selected = Rect.Intersect(new Rect(selected.X, selected.Y, Math.Max(1, selected.Width + dx), Math.Max(1, selected.Height + dy)), screen);
            else { selected.Offset(dx, dy); selected = Clamp(selected); }
            PositionToolbar(); surface.InvalidateVisual(); e.Handled = true;
        }
    }
    private Color PixelColor(Point p)
    {
        var bytes = new byte[4]; desktop.CopyPixels(new Int32Rect(Math.Clamp((int)(p.X - screen.X), 0, desktop.PixelWidth - 1), Math.Clamp((int)(p.Y - screen.Y), 0, desktop.PixelHeight - 1), 1, 1), bytes, 4, 0); return Color.FromRgb(bytes[2], bytes[1], bytes[0]);
    }
    private void Finish(string action)
    {
        if (selected.IsEmpty) return;
        var crop = new CroppedBitmap(desktop, new Int32Rect((int)(selected.X - screen.X), (int)(selected.Y - screen.Y), (int)selected.Width, (int)selected.Height)); crop.Freeze();
        var result = ImageDocument.Detach(crop);
        var rect = selected; Close(); Accepted?.Invoke(rect, result, action, target);
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
            if (!selection.IsEmpty)
            {
                var r = o.Local(selection); dc.DrawRectangle(null, new System.Windows.Media.Pen(Ui.Accent, 1), r);
                if (!o.selected.IsEmpty) foreach (var p in o.Handles(r)) dc.DrawRectangle(Brushes.White, new System.Windows.Media.Pen(Ui.Accent, 1), new Rect(p.X - 3, p.Y - 3, 6, 6));
                Label(dc, $"{selection.Width:0} × {selection.Height:0}", new Point(r.Left, Math.Max(0, r.Top - 26)), 12);
            }
            var hint = o.selected.IsEmpty ? (o.scrollMode ? "框选滚动内容，松开开始捕获" : "拖动框选 · 点击选窗口") + "   Tab 切换识别   Esc 取消" : "拖动边界微调   Enter 复制   双击编辑   C 取色   Esc 取消";
            Label(dc, hint + "  ·  " + new[] { "控件", "窗口", "自由" }[o.detectionMode], new Point(22, 20), 12);
            if (o.settings.Magnifier && o.surface.IsMouseOver && !o.pointer.Equals(default(Point)))
            {
                var p = o.PointFromScreen(o.pointer); var color = o.PixelColor(o.pointer); double x = p.X + 28, y = p.Y + 28;
                if (x + 184 > ActualWidth) x = p.X - 204; if (y + 170 > ActualHeight) y = p.Y - 184;
                x = Math.Max(0, x); y = Math.Max(0, y);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(25, 29, 36)), null, new Rect(x, y, 184, 160), 6, 6);
                int px = Math.Clamp((int)(o.pointer.X - o.screen.X) - 9, 0, o.desktop.PixelWidth - 19), py = Math.Clamp((int)(o.pointer.Y - o.screen.Y) - 9, 0, o.desktop.PixelHeight - 19);
                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
                dc.DrawImage(new CroppedBitmap(o.desktop, new Int32Rect(px, py, 19, 19)), new Rect(x + 44, y + 8, 95, 95));
                dc.DrawRectangle(null, new System.Windows.Media.Pen(Brushes.Red, 1), new Rect(x + 89, y + 53, 5, 5));
                Label(dc, $"X {o.pointer.X:0}  Y {o.pointer.Y:0}\nRGB {color.R}, {color.G}, {color.B}  #{color.R:X2}{color.G:X2}{color.B:X2}", new Point(x + 7, y + 108), 11);
            }
        }
        private void Label(DrawingContext dc, string value, Point point, double size)
        {
            var t = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), size, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(220, 22, 27, 34)), null, new Rect(point.X - 4, point.Y - 2, t.Width + 8, t.Height + 4), 3, 3); dc.DrawText(t, point);
        }
    }
}
