using System.Windows.Interop;
using Snapaste.Core;
using Snapaste.Platform;
using Snapaste.UI;
namespace Snapaste.ScrollCapture;

public sealed class ScrollWindow : Window
{
    private const int HotkeyEscapeId = 9201, HotkeyEnterId = 9202, WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000, VkEscape = 0x1B, VkReturn = 0x0D;
    private readonly ScrollSession session;
    private readonly TextBlock stateTitle = Ui.Label("准备捕获", 18), status = Ui.Label("正在获取第一帧…", 12, true), progress = Ui.Label("", 11, true), confidence = Ui.Label("", 11), fixedHint = Ui.Label("", 11, true);
    private readonly StackPanel thumbnails = new();
    private readonly ScrollViewer preview;
    private readonly List<ImageSegment> rendered = [];
    private readonly Button pause, automatic, undo, editButton, finish;
    private readonly Border recovery;
    private CancellationTokenSource? previewWork;
    private string? lastPreviewPath;
    private int lastPreviewBottom;
    private bool closed, aligning, keysRegistered;
    private ScrollRegionIndicator? regionIndicator;
    public bool EditRequested { get; private set; }
    public ScrollWindow(ScrollSession session)
    {
        this.session = session; Title = "Snapaste · 滚动截图"; Width = 336; Height = 680; MinWidth = 316; MinHeight = 560; Topmost = true; ShowActivated = false; Ui.Setup(this);
        var root = new DockPanel { Margin = new Thickness(16, 18, 16, 14) }; Content = root;
        var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var cancel = Ui.Button("取消 (Esc)", CancelAndClose); cancel.ToolTip = "退出并放弃本次长截图 · Esc"; cancel.SetResourceReference(StyleProperty, "QuietButton"); footer.Children.Add(cancel);
        editButton = Ui.Button("编辑", () => FinishCapture(openEditor: true)); editButton.ToolTip = "完成拼接并进入编辑页面"; editButton.IsEnabled = false; Grid.SetColumn(editButton, 1); footer.Children.Add(editButton);
        finish = Ui.Action("Done", "完成保存 (Enter)", () => FinishCapture(openEditor: false), true, "直接完成并保存截图 · Enter"); finish.IsEnabled = false; Grid.SetColumn(finish, 2); footer.Children.Add(finish);
        var head = new StackPanel(); DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
        var eyebrow = Ui.Label("滚动长截图 · Enter 保存退出 · Esc 取消", 11, true); eyebrow.Margin = new Thickness(0, 0, 0, 8); head.Children.Add(eyebrow); stateTitle.FontWeight = FontWeights.SemiBold; head.Children.Add(stateTitle);
        status.Margin = new Thickness(0, 8, 0, 12); status.MinHeight = 38; head.Children.Add(status);
        var buttons = new Grid(); buttons.ColumnDefinitions.Add(new ColumnDefinition()); buttons.ColumnDefinitions.Add(new ColumnDefinition()); head.Children.Add(buttons);
        pause = Ui.Action("Pause", "暂停", () => { if (session.State == ScrollState.Paused) session.Resume(); else session.Pause(); }); pause.IsEnabled = false; buttons.Children.Add(pause);
        automatic = Ui.Action("Auto", "自动滚动", () => session.SetAutomatic(!session.Automatic)); automatic.IsEnabled = false; Grid.SetColumn(automatic, 1); buttons.Children.Add(automatic);
        var recoverContent = new StackPanel(); var explanation = Ui.Label("当前帧尚未加入。可以手动对齐、忽略，或按 Enter 完成已拼接的内容。", 11, true); explanation.Margin = new Thickness(0, 0, 0, 10); recoverContent.Children.Add(explanation);
        var recoverActions = new WrapPanel(); recoverContent.Children.Add(recoverActions); recoverActions.Children.Add(Ui.Action("Correction", "手动对齐", Correct, true)); recoverActions.Children.Add(Ui.Action("Ignore", "忽略此帧", session.Ignore));
        recovery = Ui.Card(recoverContent, 12); recovery.Margin = new Thickness(0, 10, 0, 6); recovery.Visibility = Visibility.Collapsed; head.Children.Add(recovery);
        var facts = new DockPanel { Margin = new Thickness(0, 10, 0, 2), LastChildFill = false }; progress.VerticalAlignment = VerticalAlignment.Center; facts.Children.Add(progress); undo = Ui.Tool("Undo", "撤销上一段", session.Undo, "撤销后将页面滚回对应位置，再继续"); undo.IsEnabled = false; DockPanel.SetDock(undo, Dock.Right); facts.Children.Add(undo); head.Children.Add(facts);
        confidence.Margin = new Thickness(0, 0, 0, 10); head.Children.Add(confidence);
        var advanced = new StackPanel(); advanced.Children.Add(Ui.Label("固定栏只保留一次。-1 自动识别，0 表示没有固定栏。下次拼接时，底部新值也会调整已有分段的裁切。", 11, true));
        var overrides = new Grid { Margin = new Thickness(0, 10, 0, 6) }; overrides.ColumnDefinitions.Add(new ColumnDefinition()); overrides.ColumnDefinitions.Add(new ColumnDefinition()); advanced.Children.Add(overrides);
        void FixedInput(string label, int column, Action<int> set)
        {
            var group = new StackPanel { Margin = new Thickness(column == 0 ? 0 : 8, 0, 0, 0) }; Grid.SetColumn(group, column); overrides.Children.Add(group); group.Children.Add(Ui.Label(label, 11, true)); var input = new TextBox { Text = "-1", Margin = new Thickness(0, 5, 0, 0) }; group.Children.Add(input); System.Windows.Automation.AutomationProperties.SetName(input, label);
            input.GotKeyboardFocus += (_, _) => UnregisterCaptureKeys();
            input.LostKeyboardFocus += (_, _) => RegisterCaptureKeys();
            input.TextChanged += (_, _) => { if (int.TryParse(input.Text, out int v) && v >= -1 && v <= (int)session.Region.Height / 3) { set(v); input.ToolTip = "-1 自动 · 0 无固定栏"; input.SetResourceReference(Control.BorderBrushProperty, "BorderBrush"); } else { input.ToolTip = $"请输入 -1 至 {(int)session.Region.Height / 3} 的整数"; input.SetResourceReference(Control.BorderBrushProperty, "WarningBrush"); } };
        }
        FixedInput("固定顶部 / px", 0, v => session.TopOverride = v); FixedInput("固定底部 / px", 1, v => session.BottomOverride = v); advanced.Children.Add(fixedHint);
        var expander = new Expander { Header = "固定栏调整", Content = advanced, Margin = new Thickness(0, 0, 0, 12) }; head.Children.Add(expander);
        preview = new ScrollViewer { Content = thumbnails, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; preview.SetResourceReference(BackgroundProperty, "CanvasBrush"); var frame = Ui.Card(preview, 5); root.Children.Add(frame);
        thumbnails.Children.Add(Ui.Label("滚动后，新增内容会出现在这里。", 11, true));
        if (!session.ShowPreview) frame.Visibility = Visibility.Collapsed;
        session.Updated += OnUpdated;
        SourceInitialized += (_, _) =>
        {
            session.ControlHandle = Native.Handle(this);
            if (PresentationSource.FromVisual(this) is HwndSource source) source.AddHook(WndProc);
            RegisterCaptureKeys();
            PositionPanel();
            UpdateCaptureExclusion();
            try
            {
                regionIndicator = new ScrollRegionIndicator(session.Region);
                regionIndicator.Show();
            }
            catch (Exception e) { Log.Error("Scroll region indicator", e); }
        };
        LocationChanged += (_, _) => UpdateCaptureExclusion();
        SizeChanged += (_, _) => UpdateCaptureExclusion();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CancelAndClose();
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && e.OriginalSource is not TextBox)
            {
                e.Handled = true;
                FinishCapture(openEditor: false);
            }
        };
        Loaded += async (_, _) =>
        {
            UpdateCaptureExclusion();
            if (session.State == ScrollState.Idle) await session.Start();
        };
        Closed += (_, _) =>
        {
            closed = true;
            UnregisterCaptureKeys();
            try { regionIndicator?.Close(); } catch { }
            regionIndicator = null;
            previewWork?.Cancel();
            session.Updated -= OnUpdated;
            session.Dispose();
        };
    }

    private void CancelAndClose()
    {
        if (closed) return;
        session.Cancel();
        Close();
    }

    private void FinishCapture(bool openEditor)
    {
        if (closed || session.Frames.Count == 0 || session.State is ScrollState.Completed or ScrollState.Cancelled) return;
        EditRequested = openEditor;
        session.Complete();
        Close();
    }

    private void RegisterCaptureKeys()
    {
        if (closed || aligning || keysRegistered || session.ControlHandle == 0) return;
        Native.RegisterHotKey(session.ControlHandle, HotkeyEscapeId, ModNoRepeat, VkEscape);
        Native.RegisterHotKey(session.ControlHandle, HotkeyEnterId, ModNoRepeat, VkReturn);
        keysRegistered = true;
    }

    private void UnregisterCaptureKeys()
    {
        if (!keysRegistered || session.ControlHandle == 0) return;
        Native.UnregisterHotKey(session.ControlHandle, HotkeyEscapeId);
        Native.UnregisterHotKey(session.ControlHandle, HotkeyEnterId);
        keysRegistered = false;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmHotkey && !closed && !aligning)
        {
            int id = (int)wParam;
            if (id == HotkeyEscapeId)
            {
                handled = true;
                CancelAndClose();
            }
            else if (id == HotkeyEnterId)
            {
                handled = true;
                if (session.Frames.Count > 0) FinishCapture(openEditor: false);
            }
        }
        return 0;
    }

    private void PositionPanel()
    {
        var selection = session.Region;
        var screens = System.Windows.Forms.Screen.AllScreens;
        var preferred = System.Windows.Forms.Screen.FromRectangle(new System.Drawing.Rectangle((int)selection.X, (int)selection.Y, (int)selection.Width, (int)selection.Height));
        double scale = Math.Max(96u, Native.GetDpiForWindow(session.ControlHandle)) / 96d;
        double bestOverlap = double.PositiveInfinity;
        Rect placement = Rect.Empty;
        foreach (var screen in screens.OrderByDescending(screen => screen.Equals(preferred)))
        {
            var area = screen.WorkingArea;
            double width = Math.Min(Width * scale, area.Width), height = Math.Min(Height * scale, area.Height);
            if (width <= 0 || height <= 0) continue;
            foreach (var point in new Point[]
            {
                new(selection.Right + 8, selection.Top), new(selection.Left - width - 8, selection.Top),
                new(selection.Left, selection.Bottom + 8), new(selection.Left, selection.Top - height - 8)
            })
            {
                var candidate = new Rect(Math.Clamp(point.X, area.Left, area.Right - width), Math.Clamp(point.Y, area.Top, area.Bottom - height), width, height);
                var overlap = Rect.Intersect(candidate, selection);
                double overlapArea = overlap.IsEmpty ? 0 : overlap.Width * overlap.Height;
                if (overlapArea >= bestOverlap) continue;
                placement = candidate;
                bestOverlap = overlapArea;
            }
        }
        if (placement.IsEmpty) return;
        MinWidth = Math.Min(MinWidth, placement.Width / scale);
        MinHeight = Math.Min(MinHeight, placement.Height / scale);
        Native.PlacePhysical(this, placement);
    }

    private void UpdateCaptureExclusion()
    {
        nint handle = session.ControlHandle;
        if (closed || handle == 0) return;
        bool overlaps = Native.Bounds(handle).IntersectsWith(session.Region);
        session.ControlExcluded = overlaps && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) && Native.SetWindowDisplayAffinity(handle, 0x11);
        if (!overlaps) Native.SetWindowDisplayAffinity(handle, 0);
    }

    private void OnUpdated(string message)
    {
        if (closed) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnUpdated(message)); return; }
        bool pending = session.Pending != null;
        bool stopped = session.State is ScrollState.Idle or ScrollState.Failed or ScrollState.Completed or ScrollState.Cancelled;
        bool busy = session.State is ScrollState.Ready or ScrollState.Matching or ScrollState.Stitching;
        stateTitle.Text = pending ? "需要确认拼接位置" : session.State switch
        {
            ScrollState.WaitingForStableFrame => "等待画面停稳",
            ScrollState.Matching or ScrollState.Stitching => "正在拼接",
            ScrollState.Paused => "已暂停",
            ScrollState.Failed => "捕获已停止",
            ScrollState.Ready or ScrollState.Idle => "准备捕获",
            ScrollState.Completed => "截图完成",
            ScrollState.Cancelled => "截图已取消",
            _ => session.Automatic ? "自动滚动中" : "向下滚动页面"
        };
        status.Text = message;
        SetButtonLabel(pause, session.State == ScrollState.Paused ? "继续手动" : "暂停");
        pause.IsEnabled = !pending && !stopped && !session.AtFrameLimit;
        SetButtonLabel(automatic, session.Automatic ? "切换手动" : "自动滚动");
        automatic.IsEnabled = session.CanAutomate && session.Frames.Count > 0 && (!busy || session.Automatic) && pause.IsEnabled;
        automatic.ToolTip = session.CanAutomate ? (session.Automatic ? "切换手动" : "自动滚动") : "无法识别目标窗口，请手动滚动。";
        recovery.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        undo.IsEnabled = session.Frames.Count > 1 && !busy && session.State is not ScrollState.Completed and not ScrollState.Cancelled;
        bool canFinish = session.Frames.Count > 0 && session.State is not ScrollState.Completed and not ScrollState.Cancelled;
        SetButtonLabel(finish, pending ? "完成已拼接内容" : "完成保存 (Enter)");
        finish.IsEnabled = canFinish;
        editButton.IsEnabled = canFinish;
        fixedHint.Text = $"当前识别：顶部 {session.FixedTop}px · 底部 {session.FixedBottom}px";
        if (session.Frames.Count == 0) return;
        var last = session.Frames[^1];
        confidence.Text = pending ? "待确认" : last.StitchMetadata?.Manual == true ? "已手动确认" : last.MatchMetadata == null ? "首帧已捕获" : last.MatchMetadata.Confidence == Confidence.High ? "匹配可靠 · 已加入长图" : "匹配通过 · 已加入长图";
        confidence.SetResourceReference(TextBlock.ForegroundProperty, pending ? "WarningBrush" : "SuccessBrush");
        if (last.BitmapPath == lastPreviewPath && session.FixedBottom == lastPreviewBottom) return;
        lastPreviewPath = last.BitmapPath;
        lastPreviewBottom = session.FixedBottom;
        var image = session.BuildImage();
        progress.Text = $"{session.Frames.Count} 帧 · {image.Width} × {image.Height}px";
        if (session.ShowPreview) RefreshPreview(image.Segments, image.Width, (int)session.Region.Height);
    }

    private static void SetButtonLabel(Button button, string label)
    {
        if (button.Content is StackPanel panel && panel.Children[^1] is TextBlock text) text.Text = label;
        else button.Content = label;
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        button.ToolTip = label;
    }

    private async void RefreshPreview(IReadOnlyList<ImageSegment> segments, int width, int frameHeight)
    {
        previewWork?.Cancel();
        using var work = new CancellationTokenSource();
        previewWork = work;
        var token = work.Token;
        var changes = Enumerable.Range(0, segments.Count).Where(i => i >= rendered.Count || rendered[i] != segments[i]).ToArray();
        try
        {
            var sources = await Task.Run(() => changes.Select(index =>
            {
                token.ThrowIfCancellationRequested();
                return MakeThumbnail(segments[index], width, frameHeight);
            }).ToArray(), token);
            if (closed || token.IsCancellationRequested) return;
            bool followTail = rendered.Count == 0 || preview.ScrollableHeight - preview.VerticalOffset < 24;
            if (rendered.Count == 0) thumbnails.Children.Clear();
            for (int i = 0; i < changes.Length; i++)
            {
                int index = changes[i];
                var segment = segments[index];
                var view = new Image { Source = sources[i], Width = 260, Height = Math.Max(1, segment.Height * 260d / width), Stretch = Stretch.Fill };
                if (index < rendered.Count)
                {
                    thumbnails.Children.RemoveAt(index);
                    thumbnails.Children.Insert(index, view);
                    rendered[index] = segment;
                }
                else
                {
                    thumbnails.Children.Add(view);
                    rendered.Add(segment);
                }
            }
            while (rendered.Count > segments.Count)
            {
                thumbnails.Children.RemoveAt(rendered.Count - 1);
                rendered.RemoveAt(rendered.Count - 1);
            }
            if (followTail) preview.ScrollToEnd();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) { if (!token.IsCancellationRequested) Log.Error("Scroll preview", e); }
        finally
        {
            if (previewWork == work) previewWork = null;
        }
    }

    private static BitmapSource MakeThumbnail(ImageSegment segment, int width, int frameHeight)
    {
        // Bound retained thumbnail pixels even when the capture is tall and narrow.
        double scale = Math.Min(1, Math.Min(260d / width, Math.Sqrt(32768d / (width * (double)segment.Height))));
        int decodeWidth = Math.Max(1, (int)(width * scale));
        var source = ImageDocument.ReadBitmap(segment.Path, decodeWidth == width ? 0 : decodeWidth);
        double yScale = source.PixelHeight / (double)frameHeight;
        int top = Math.Min(source.PixelHeight - 1, (int)(segment.SourceY * yScale));
        int end = Math.Clamp((int)Math.Ceiling((segment.SourceY + segment.Height) * yScale), top + 1, source.PixelHeight);
        return ImageDocument.Detach(new CroppedBitmap(source, new Int32Rect(0, top, source.PixelWidth, end - top)));
    }

    private async void Correct()
    {
        var pending = session.Pending;
        if (aligning || pending == null || session.Frames.Count == 0) return;
        var (top, bottom, maximum) = session.ManualBounds();
        if (maximum == 0) { status.Text = "固定栏过大，已有分段无法保留重叠。请减小固定栏高度后重试。"; return; }
        var previous = session.Frames[^1];
        var body = new Int32Rect(0, top, pending.PixelWidth, pending.PixelHeight - top - bottom);
        int suggestion = session.LastMatch?.Offset ?? maximum / 2;
        aligning = true;
        UnregisterCaptureKeys();
        try
        {
            var (a, b) = await Task.Run(() => (ImageDocument.Detach(new CroppedBitmap(previous.Bitmap, body)), ImageDocument.Detach(new CroppedBitmap(pending, body))));
            if (closed || session.Pending != pending) return;
            if (session.ManualBounds() != (top, bottom, maximum))
            {
                status.Text = "固定栏高度已改变，请重新打开对齐窗口。";
                return;
            }
            var dialog = new AlignmentWindow(a, b, suggestion, maximum) { Owner = this };
            if (dialog.ShowDialog() == true) session.Manual(dialog.Offset);
        }
        catch (Exception e) { if (!closed && session.Pending == pending) Ui.Error(e, this); }
        finally { aligning = false; RegisterCaptureKeys(); }
    }
}

internal sealed class ScrollRegionIndicator : Window
{
    public ScrollRegionIndicator(Rect region)
    {
        Title = "Snapaste · 滚动区域";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        var rect = new System.Windows.Shapes.Rectangle
        {
            Stroke = Ui.Accent,
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            RadiusX = 3,
            RadiusY = 3,
            IsHitTestVisible = false
        };
        Content = rect;
        var outer = new Rect(region.X - 4, region.Y - 4, region.Width + 8, region.Height + 8);
        SourceInitialized += (_, _) =>
        {
            Native.MakeClickThroughToolWindow(this);
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) Native.ExcludeFromCapture(this);
            Native.PlacePhysical(this, outer);
        };
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
