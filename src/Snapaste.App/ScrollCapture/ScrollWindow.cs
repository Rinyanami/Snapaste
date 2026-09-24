using Snapaste.Core;
using Snapaste.Platform;
using Snapaste.UI;
namespace Snapaste.ScrollCapture;

public sealed class ScrollWindow : Window
{
    private readonly ScrollSession session;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(9), MinHeight = 55 };
    private readonly StackPanel thumbnails = new();
    private readonly ScrollViewer preview;
    private readonly List<ImageSegment> rendered = [];
    private bool completed;
    public ScrollWindow(ScrollSession session)
    {
        this.session = session; Title = "Snapaste · 滚动截图"; Width = 302; Height = 610; MinWidth = 280; MinHeight = 350; Topmost = true; ShowActivated = false; Ui.Setup(this);
        var root = new DockPanel(); Content = root;
        var head = new StackPanel(); DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
        head.Children.Add(new TextBlock { Text = "滚动截图", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 10, 12, 0) }); head.Children.Add(status);
        head.Children.Add(new TextBlock { Text = Strings.T("ScrollHint"), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(10, 0, 10, 8) });
        var buttons = new WrapPanel { Margin = new Thickness(6) }; head.Children.Add(buttons);
        buttons.Children.Add(Ui.Button(Strings.T("Pause"), () => session.Pause()));
        buttons.Children.Add(Ui.Button(Strings.T("Resume"), session.Resume));
        buttons.Children.Add(Ui.Button(Strings.T("Auto"), () => session.SetAutomatic(!session.Automatic)));
        buttons.Children.Add(Ui.Button(Strings.T("LastFrame"), session.Undo));
        buttons.Children.Add(Ui.Button(Strings.T("Correction"), Correct));
        buttons.Children.Add(Ui.Button(Strings.T("Ignore"), session.Ignore));
        buttons.Children.Add(Ui.Button(Strings.T("Done"), () => { completed = true; session.Complete(); Close(); }));
        buttons.Children.Add(Ui.Button(Strings.T("Cancel"), Close));
        var fixedPanel = new WrapPanel { Margin = new Thickness(8) }; head.Children.Add(fixedPanel);
        fixedPanel.Children.Add(new TextBlock { Text = "固定顶部 / 底部(px，-1 自动)", Margin = new Thickness(2), VerticalAlignment = VerticalAlignment.Center });
        var top = new TextBox { Text = "-1", Width = 46, Margin = new Thickness(3) }; var bottom = new TextBox { Text = "-1", Width = 46, Margin = new Thickness(3) };
        top.TextChanged += (_, _) => { if (int.TryParse(top.Text, out int v)) session.TopOverride = Math.Clamp(v, -1, (int)session.Region.Height / 3); };
        bottom.TextChanged += (_, _) => { if (int.TryParse(bottom.Text, out int v)) session.BottomOverride = Math.Clamp(v, -1, (int)session.Region.Height / 3); };
        fixedPanel.Children.Add(top); fixedPanel.Children.Add(bottom);
        preview = new ScrollViewer { Content = thumbnails, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.DimGray, Margin = new Thickness(10, 0, 10, 10) }; root.Children.Add(preview);
        if (!App.Current.Settings.ScrollPreview) preview.Visibility = Visibility.Collapsed;
        session.Updated += OnUpdated;
        SourceInitialized += (_, _) =>
        {
            session.ControlHandle = Native.Handle(this);
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)session.Region.X, (int)session.Region.Y)).WorkingArea;
            double dpi = Native.GetDpiForWindow(Native.Handle(this)) / 96d; double w = Width * dpi, h = Math.Min(Height * dpi, screen.Height);
            double x = session.Region.Right + 8; if (x + w > screen.Right) x = session.Region.Left - w - 8;
            if (x < screen.Left) x = screen.Right - w;
            Native.PlacePhysical(this, new Rect(x, Math.Clamp(session.Region.Top, screen.Top, screen.Bottom - h), w, h));
            Native.SetWindowDisplayAffinity(Native.Handle(this), Native.Bounds(Native.Handle(this)).IntersectsWith(session.Region) ? 0x11u : 0u);
        };
        LocationChanged += (_, _) => { if (IsLoaded) Native.SetWindowDisplayAffinity(Native.Handle(this), Native.Bounds(Native.Handle(this)).IntersectsWith(session.Region) ? 0x11u : 0u); };
        Loaded += async (_, _) => await session.Start();
        Closed += (_, _) => { session.Updated -= OnUpdated; if (!completed) session.Cancel(); };
    }
    private void OnUpdated(string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnUpdated(message)); return; }
        status.Text = message;
        if (session.Frames.Count == 0 || !App.Current.Settings.ScrollPreview) return;
        var segments = session.BuildImage().Segments;
        // Each preview image represents one appended segment. Previous pixels are never recomposed.
        for (int i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (i < rendered.Count && rendered[i] == segment) continue;
            var source = ImageDocument.ReadBitmap(segment.Path);
            var crop = new CroppedBitmap(source, new Int32Rect(0, segment.SourceY, source.PixelWidth, segment.Height));
            var thumbnail = new TransformedBitmap(crop, new ScaleTransform(260d / source.PixelWidth, 260d / source.PixelWidth)); thumbnail.Freeze();
            var image = new Image { Source = ImageDocument.Detach(thumbnail), Width = 260, Height = Math.Max(1, segment.Height * 260d / source.PixelWidth), Stretch = Stretch.Fill };
            if (i < thumbnails.Children.Count) { thumbnails.Children.RemoveAt(i); thumbnails.Children.Insert(i, image); rendered[i] = segment; }
            else { thumbnails.Children.Add(image); rendered.Add(segment); }
        }
        while (thumbnails.Children.Count > segments.Count) { thumbnails.Children.RemoveAt(thumbnails.Children.Count - 1); rendered.RemoveAt(rendered.Count - 1); }
        if (session.State == ScrollState.WaitingForScroll) preview.ScrollToEnd();
    }
    private void Correct()
    {
        if (session.Pending == null || session.Frames.Count == 0) { status.Text = "出现低可信帧后可手动修正。"; return; }
        var dialog = new AlignmentWindow(session.Frames[^1].Bitmap, session.Pending, session.LastMatch?.Offset ?? (int)session.Region.Height / 2) { Owner = this };
        if (dialog.ShowDialog() == true) session.Manual(dialog.Offset);
    }
}
internal sealed class AlignmentWindow : Window
{
    private readonly Slider slider;
    public int Offset => (int)slider.Value;
    public AlignmentWindow(BitmapSource previous, BitmapSource current, int suggested)
    {
        Title = "手动对齐 · 拖动滑块，让两帧重叠文字重合"; Width = 800; Height = 750; Ui.Setup(this); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel(); Content = root;
        var options = new StackPanel { Margin = new Thickness(12) }; DockPanel.SetDock(options, Dock.Top); root.Children.Add(options);
        options.Children.Add(new TextBlock { Text = "半透明叠加预览：调整滚动位移，直到重叠内容完全对齐。方向键可微调 1px。", TextWrapping = TextWrapping.Wrap });
        var label = new TextBlock(); options.Children.Add(label);
        slider = new Slider { Minimum = 1, Maximum = previous.PixelHeight - 20, Value = Math.Clamp(suggested, 1, previous.PixelHeight - 20), TickFrequency = 1, IsSnapToTickEnabled = true, SmallChange = 1, LargeChange = 10, Margin = new Thickness(0, 8, 0, 8) }; options.Children.Add(slider);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; options.Children.Add(buttons);
        buttons.Children.Add(Ui.Button("−1 px", () => slider.Value--)); buttons.Children.Add(Ui.Button("+1 px", () => slider.Value++));
        buttons.Children.Add(Ui.Button(Strings.T("Ok"), () => DialogResult = true)); buttons.Children.Add(Ui.Button(Strings.T("Cancel"), () => DialogResult = false));
        var canvas = new Canvas { Background = Brushes.DimGray }; var scroll = new ScrollViewer { Content = canvas, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; root.Children.Add(scroll);
        double scale = Math.Min(1, 720d / previous.PixelWidth); canvas.Width = previous.PixelWidth * scale; canvas.Height = previous.PixelHeight * 2 * scale;
        var a = new Image { Source = previous, Width = previous.PixelWidth * scale, Height = previous.PixelHeight * scale };
        var b = new Image { Source = current, Width = current.PixelWidth * scale, Height = current.PixelHeight * scale, Opacity = .5 };
        canvas.Children.Add(a); canvas.Children.Add(b);
        void Update() { Canvas.SetTop(b, Offset * scale); label.Text = $"位移 {Offset}px · 重叠 {previous.PixelHeight - Offset}px"; }
        slider.ValueChanged += (_, _) => Update(); Update();
    }
}
