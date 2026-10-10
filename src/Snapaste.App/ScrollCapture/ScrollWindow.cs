using Snapaste.Core;
using Snapaste.Platform;
using Snapaste.UI;
namespace Snapaste.ScrollCapture;

public sealed class ScrollWindow : Window
{
    private readonly ScrollSession session;
    private readonly TextBlock stateTitle = Ui.Label("准备捕获", 18), status = Ui.Label("正在获取第一帧…", 12, true), progress = Ui.Label("", 11, true), confidence = Ui.Label("", 11), fixedHint = Ui.Label("", 11, true);
    private readonly StackPanel thumbnails = new();
    private readonly ScrollViewer preview;
    private readonly List<ImageSegment> rendered = [];
    private readonly Button pause, automatic, undo, finish;
    private readonly Border recovery;
    private bool completed, closed;
    public ScrollWindow(ScrollSession session)
    {
        this.session = session; Title = "Snapaste · 滚动截图"; Width = 326; Height = 680; MinWidth = 310; MinHeight = 560; Topmost = true; ShowActivated = false; Ui.Setup(this);
        var root = new DockPanel { Margin = new Thickness(16, 18, 16, 14) }; Content = root;
        var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) }; footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new ColumnDefinition()); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var cancel = Ui.Button("取消", Close); cancel.SetResourceReference(StyleProperty, "QuietButton"); footer.Children.Add(cancel);
        finish = Ui.Action("Done", "完成并编辑", () => { if (session.Frames.Count == 0) return; session.Complete(); completed = true; Close(); }, true); finish.IsEnabled = false; Grid.SetColumn(finish, 1); footer.Children.Add(finish);
        var head = new StackPanel(); DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
        var eyebrow = Ui.Label("滚动长截图", 11, true); eyebrow.Margin = new Thickness(0, 0, 0, 8); head.Children.Add(eyebrow); stateTitle.FontWeight = FontWeights.SemiBold; head.Children.Add(stateTitle);
        status.Margin = new Thickness(0, 8, 0, 12); status.MinHeight = 38; head.Children.Add(status);
        var buttons = new Grid(); buttons.ColumnDefinitions.Add(new ColumnDefinition()); buttons.ColumnDefinitions.Add(new ColumnDefinition()); head.Children.Add(buttons);
        pause = Ui.Action("Pause", "暂停", () => { if (session.State == ScrollState.Paused) session.Resume(); else session.Pause(); }); pause.IsEnabled = false; buttons.Children.Add(pause);
        automatic = Ui.Action("Auto", "自动滚动", () => { if (session.Automatic) { session.SetAutomatic(false); session.Resume(); } else session.SetAutomatic(true); }); automatic.IsEnabled = false; Grid.SetColumn(automatic, 1); buttons.Children.Add(automatic);
        var recoverContent = new StackPanel(); recoverContent.Children.Add(Ui.Label("这段内容需要你确认", 13)); var explanation = Ui.Label("匹配位置不确定，已暂停。对齐后可以继续滚动。", 11, true); explanation.Margin = new Thickness(0, 6, 0, 10); recoverContent.Children.Add(explanation);
        var recoverActions = new WrapPanel(); recoverContent.Children.Add(recoverActions); recoverActions.Children.Add(Ui.Action("Correction", "手动对齐", Correct, true)); recoverActions.Children.Add(Ui.Action("Ignore", "忽略此帧", session.Ignore));
        recovery = Ui.Card(recoverContent, 12); recovery.Margin = new Thickness(0, 10, 0, 6); recovery.Visibility = Visibility.Collapsed; head.Children.Add(recovery);
        var facts = new DockPanel { Margin = new Thickness(0, 10, 0, 2), LastChildFill = false }; progress.VerticalAlignment = VerticalAlignment.Center; facts.Children.Add(progress); undo = Ui.Tool("Undo", "撤销上一段", session.Undo, "撤销后将页面滚回对应位置，再继续"); undo.IsEnabled = false; DockPanel.SetDock(undo, Dock.Right); facts.Children.Add(undo); head.Children.Add(facts);
        confidence.Margin = new Thickness(0, 0, 0, 10); head.Children.Add(confidence);
        var advanced = new StackPanel(); advanced.Children.Add(Ui.Label("固定栏只保留一次。-1 自动识别，0 表示没有固定栏。", 11, true));
        var overrides = new Grid { Margin = new Thickness(0, 10, 0, 6) }; overrides.ColumnDefinitions.Add(new ColumnDefinition()); overrides.ColumnDefinitions.Add(new ColumnDefinition()); advanced.Children.Add(overrides);
        void FixedInput(string label, int column, Action<int> set)
        {
            var group = new StackPanel { Margin = new Thickness(column == 0 ? 0 : 8, 0, 0, 0) }; Grid.SetColumn(group, column); overrides.Children.Add(group); group.Children.Add(Ui.Label(label, 11, true)); var input = new TextBox { Text = "-1", Margin = new Thickness(0, 5, 0, 0) }; group.Children.Add(input); System.Windows.Automation.AutomationProperties.SetName(input, label);
            input.TextChanged += (_, _) => { if (int.TryParse(input.Text, out int v) && v >= -1 && v <= (int)session.Region.Height / 3) { set(v); input.ToolTip = "-1 自动 · 0 无固定栏"; input.SetResourceReference(Control.BorderBrushProperty, "BorderBrush"); } else { input.ToolTip = $"请输入 -1 至 {(int)session.Region.Height / 3} 的整数"; input.SetResourceReference(Control.BorderBrushProperty, "WarningBrush"); } };
        }
        FixedInput("固定顶部 / px", 0, v => session.TopOverride = v); FixedInput("固定底部 / px", 1, v => session.BottomOverride = v); advanced.Children.Add(fixedHint);
        var expander = new Expander { Header = "固定栏调整", Content = advanced, Margin = new Thickness(0, 0, 0, 12) }; head.Children.Add(expander);
        preview = new ScrollViewer { Content = thumbnails, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; preview.SetResourceReference(BackgroundProperty, "CanvasBrush"); var frame = Ui.Card(preview, 5); root.Children.Add(frame);
        thumbnails.Children.Add(Ui.Label("滚动后，新增内容会出现在这里。", 11, true));
        if (!App.Current.Settings.ScrollPreview) frame.Visibility = Visibility.Collapsed;
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
        Loaded += async (_, _) => await session.Start(); Closed += (_, _) => { closed = true; session.Updated -= OnUpdated; if (!completed) session.Cancel(); };
    }
    private void OnUpdated(string message)
    {
        if (closed) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnUpdated(message)); return; }
        bool pending = session.Pending != null, busy = session.State is ScrollState.Ready or ScrollState.Matching or ScrollState.Stitching or ScrollState.Idle;
        stateTitle.Text = pending ? "需要确认拼接位置" : session.State switch { ScrollState.WaitingForStableFrame => "等待画面停稳", ScrollState.Matching or ScrollState.Stitching => "正在拼接", ScrollState.Paused => "已暂停", ScrollState.Failed => "捕获已停止", ScrollState.Ready or ScrollState.Idle => "准备捕获", _ => session.Automatic ? "自动滚动中" : "向下滚动页面" };
        status.Text = session.State == ScrollState.WaitingForScroll
            ? session.Automatic ? message.StartsWith("2.5", StringComparison.Ordinal) ? message : "自动捕获中。移出选区会暂停，完成后进入编辑。" : "无需点击捕获。每次保留约 1/3 重叠，停下后自动拼接。"
            : message;
        pause.Content = session.State == ScrollState.Paused ? "继续手动" : "暂停"; pause.IsEnabled = !busy && !pending && session.State != ScrollState.Failed;
        automatic.Content = session.Automatic ? "切换手动" : "自动滚动"; automatic.IsEnabled = !busy && !pending && session.State != ScrollState.Failed;
        recovery.Visibility = pending ? Visibility.Visible : Visibility.Collapsed; undo.IsEnabled = session.Frames.Count > 1 && !busy; finish.IsEnabled = session.Frames.Count > 0 && session.State is not ScrollState.Matching and not ScrollState.Stitching;
        fixedHint.Text = $"当前识别：顶部 {session.FixedTop}px · 底部 {session.FixedBottom}px";
        if (session.Frames.Count == 0) return;
        var image = session.BuildImage(); progress.Text = $"{session.Frames.Count} 帧 · {image.Width} × {image.Height}px";
        confidence.Text = pending ? "待确认 · 当前帧尚未加入长图" : session.Frames[^1].StitchMetadata?.Manual == true ? "已手动确认" : session.LastMatch == null ? "首帧已捕获" : session.LastMatch.Confidence == Confidence.High ? "匹配可靠 · 已加入长图" : "匹配通过 · 已加入长图";
        confidence.SetResourceReference(TextBlock.ForegroundProperty, pending ? "WarningBrush" : "SuccessBrush");
        if (!App.Current.Settings.ScrollPreview) return;
        var segments = image.Segments; bool changed = false;
        if (rendered.Count == 0) thumbnails.Children.Clear();
        // Keep one small image per appended segment; never recompose the full long bitmap.
        for (int i = 0; i < segments.Count; i++)
        {
            var segment = segments[i]; if (i < rendered.Count && rendered[i] == segment) continue; changed = true;
            var source = ImageDocument.ReadBitmap(segment.Path); var crop = new CroppedBitmap(source, new Int32Rect(0, segment.SourceY, source.PixelWidth, segment.Height));
            var thumbnail = new TransformedBitmap(crop, new ScaleTransform(260d / source.PixelWidth, 260d / source.PixelWidth)); thumbnail.Freeze();
            var view = new Image { Source = ImageDocument.Detach(thumbnail), Width = 260, Height = Math.Max(1, segment.Height * 260d / source.PixelWidth), Stretch = Stretch.Fill };
            if (i < thumbnails.Children.Count) { thumbnails.Children.RemoveAt(i); thumbnails.Children.Insert(i, view); rendered[i] = segment; } else { thumbnails.Children.Add(view); rendered.Add(segment); }
        }
        while (thumbnails.Children.Count > segments.Count) { thumbnails.Children.RemoveAt(thumbnails.Children.Count - 1); rendered.RemoveAt(rendered.Count - 1); changed = true; }
        if (changed && session.State == ScrollState.WaitingForScroll) preview.ScrollToEnd();
    }
    private void Correct()
    {
        if (session.Pending == null || session.Frames.Count == 0) return;
        int maximum = (int)session.Region.Height - (session.BottomOverride >= 0 ? session.BottomOverride : session.LastMatch?.FixedBottom ?? session.FixedBottom) - Math.Max(1, session.TopOverride >= 0 ? session.TopOverride : session.LastMatch?.FixedTop ?? session.FixedTop);
        var dialog = new AlignmentWindow(session.Frames[^1].Bitmap, session.Pending, session.LastMatch?.Offset ?? (int)session.Region.Height / 2, maximum) { Owner = this };
        if (dialog.ShowDialog() == true) session.Manual(dialog.Offset);
    }
}
internal sealed class AlignmentWindow : Window
{
    private readonly Slider slider;
    public int Offset => (int)slider.Value;
    public AlignmentWindow(BitmapSource previous, BitmapSource current, int suggested, int maximum)
    {
        Title = "Snapaste · 手动对齐"; Width = 860; Height = 760; MinWidth = 640; MinHeight = 520; Ui.Setup(this); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var options = new StackPanel { Margin = new Thickness(0, 0, 0, 16) }; DockPanel.SetDock(options, Dock.Top); root.Children.Add(options); options.Children.Add(Ui.Label("让重叠文字重合", 21)); var hint = Ui.Label("新一帧以半透明叠加显示。调整位移，直到同一段内容对齐。", 12, true); hint.Margin = new Thickness(0, 8, 0, 10); options.Children.Add(hint);
        var label = Ui.Label("", 12); options.Children.Add(label);
        slider = new Slider { Minimum = 1, Maximum = Math.Max(1, maximum), Value = Math.Clamp(suggested, 1, Math.Max(1, maximum)), TickFrequency = 1, IsSnapToTickEnabled = true, SmallChange = 1, LargeChange = 10, Margin = new Thickness(0, 12, 0, 0) }; options.Children.Add(slider); System.Windows.Automation.AutomationProperties.SetName(slider, "拼接位移");
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var adjustments = new StackPanel { Orientation = Orientation.Horizontal }; footer.Children.Add(adjustments); adjustments.Children.Add(Ui.Button("−1 px", () => slider.Value--)); adjustments.Children.Add(Ui.Button("+1 px", () => slider.Value++));
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(actions, Dock.Right); footer.Children.Add(actions); actions.Children.Add(Ui.Button("取消", () => DialogResult = false)); actions.Children.Add(Ui.Action("Done", "确认拼接", () => DialogResult = true, true));
        var canvas = new Canvas(); canvas.SetResourceReference(BackgroundProperty, "CanvasBrush"); var scroll = new ScrollViewer { Content = canvas, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; root.Children.Add(scroll);
        var a = new Image { Source = previous }; var b = new Image { Source = current, Opacity = .5 }; canvas.Children.Add(a); canvas.Children.Add(b); double scale = Math.Min(1, 780d / previous.PixelWidth);
        void Update() { canvas.Width = previous.PixelWidth * scale; canvas.Height = (previous.PixelHeight + Offset) * scale; a.Width = b.Width = previous.PixelWidth * scale; a.Height = b.Height = previous.PixelHeight * scale; Canvas.SetTop(b, Offset * scale); label.Text = $"滚动位移 {Offset}px   ·   重叠 {previous.PixelHeight - Offset}px"; }
        adjustments.Children.Add(Ui.Button("100%", () => { scale = 1; Update(); })); adjustments.Children.Add(Ui.Tool("Fit", "适合宽度", () => { scale = Math.Min(1, Math.Max(1, scroll.ViewportWidth - 20) / previous.PixelWidth); Update(); })); slider.ValueChanged += (_, _) => Update(); Update();
        Loaded += (_, _) => slider.Focus();
    }
}
