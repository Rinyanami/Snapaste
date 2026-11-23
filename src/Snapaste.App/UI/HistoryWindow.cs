using Snapaste.Core;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
namespace Snapaste.UI;

public sealed class HistoryWindow : Window
{
    private readonly HistoryStore store;
    private readonly StackPanel groups = new();
    private readonly ScrollViewer scroll;
    private readonly TextBlock summary = Ui.Label("", 12, true), feedback = Ui.Label("", 12, true);
    private readonly Dictionary<string, Button> filters = [];
    private readonly ComboBox dates = new() { ItemsSource = new[] { "全部时间", "今天", "最近 7 天", "最近 30 天" }, SelectedIndex = 0, Width = 132 };
    private readonly StackPanel pager;
    private readonly Button refresh, previous, next;
    private readonly TextBlock pageInfo = Ui.Label("", 12, true);
    private CancellationTokenSource? refreshRequest, previewRequest;
    private List<HistoryEntry> entries = [];
    private bool loaded;
    private string kind = "All";
    private const int PageSize = 40;
    private const int PreviewBatchSize = 8;
    private int pageIndex;

    public HistoryWindow(HistoryStore store)
    {
        this.store = store;
        Title = "Snapaste · 截图历史";
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(1100, workArea.Width); Height = Math.Min(720, workArea.Height);
        MinWidth = Math.Min(540, workArea.Width); MinHeight = Math.Min(340, workArea.Height);
        Ui.Setup(this); WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new Grid { Margin = new Thickness(22, 18, 22, 12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var head = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.Children.Add(head);
        var titles = new StackPanel();
        var title = Ui.Label(Strings.T("History"), 24);
        title.FontWeight = FontWeights.SemiBold;
        titles.Children.Add(title);
        summary.Margin = new Thickness(0, 6, 0, 0);
        titles.Children.Add(summary);
        head.Children.Add(titles);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(actions, 1);
        head.Children.Add(actions);
        refresh = Ui.Button("刷新", Reload, "刷新截图历史");
        AutomationProperties.SetName(refresh, "刷新截图历史");
        actions.Children.Add(refresh);
        actions.Children.Add(Ui.Action("Capture", Strings.T("StartCapture"), () => { Close(); App.Current.Capture(false); }, true));

        var filterRow = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        filterRow.ColumnDefinitions.Add(new ColumnDefinition());
        filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(filterRow, 1);
        root.Children.Add(filterRow);
        var tabs = new WrapPanel();
        filterRow.Children.Add(tabs);
        foreach (var value in new[] { ("All", "全部"), ("Capture", "普通截图"), ("Scroll", "长截图") })
        {
            string filter = value.Item1;
            var button = Ui.Button(value.Item2, () =>
            {
                if (kind == filter) return;
                kind = filter;
                pageIndex = 0;
                Render(true);
            });
            button.SetResourceReference(StyleProperty, "QuietButton");
            button.Margin = new Thickness(0, 0, 6, 0);
            filters.Add(filter, button);
            tabs.Children.Add(button);
        }
        Grid.SetColumn(dates, 1);
        filterRow.Children.Add(dates);
        AutomationProperties.SetName(dates, "历史时间范围");

        scroll = new ScrollViewer { Content = groups, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);
        var footer = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        feedback.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetLiveSetting(feedback, AutomationLiveSetting.Polite);
        footer.Children.Add(feedback);
        pager = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
        Grid.SetColumn(pager, 1);
        footer.Children.Add(pager);
        previous = Ui.Button("上一页", () => { pageIndex--; Render(true); });
        next = Ui.Button("下一页", () => { pageIndex++; Render(true); });
        pageInfo.VerticalAlignment = VerticalAlignment.Center;
        pageInfo.Margin = new Thickness(8, 0, 8, 0);
        pager.Children.Add(previous);
        pager.Children.Add(pageInfo);
        pager.Children.Add(next);
        dates.SelectionChanged += (_, _) => { pageIndex = 0; Render(true); };
        Closed += (_, _) => { refreshRequest?.Cancel(); previewRequest?.Cancel(); };
        Render(true);
        Reload();
    }

    public async void Reload()
    {
        refreshRequest?.Cancel();
        using var request = new CancellationTokenSource();
        refreshRequest = request;
        refresh.IsEnabled = false;
        summary.Text = Strings.T("HistoryRefreshing");
        try
        {
            var refreshed = await Task.Run(() => store.Entries(request.Token), request.Token);
            if (request.IsCancellationRequested) return;
            entries = refreshed;
            loaded = true;
            summary.Text = $"本机共 {entries.Count} 张截图";
            Render(false);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception e)
        {
            Log.Error("History refresh", e);
            if (!request.IsCancellationRequested) summary.Text = Strings.T("HistoryRefreshFailed");
        }
        finally
        {
            if (ReferenceEquals(refreshRequest, request))
            {
                refreshRequest = null;
                refresh.IsEnabled = true;
            }
        }
    }
    private void Render(bool resetScroll)
    {
        previewRequest?.Cancel();
        groups.Children.Clear();
        foreach (var (key, filter) in filters)
        {
            filter.Tag = key == kind ? "Selected" : null;
            AutomationProperties.SetItemStatus(filter, key == kind ? "当前筛选" : "");
        }
        if (!loaded) return;
        DateTime earliest = dates.SelectedIndex switch { 1 => DateTime.Today, 2 => DateTime.Today.AddDays(-6), 3 => DateTime.Today.AddDays(-29), _ => DateTime.MinValue };
        var visible = entries.Where(e => (kind == "All" || (kind == "Capture" ? e.Kind != "Scroll" : e.Kind == "Scroll")) && e.Created >= earliest).ToArray();
        ShowFeedback(visible.Length == entries.Count ? "" : $"筛选结果 {visible.Length} 张", "MutedBrush");
        int pageCount = Math.Max(1, (visible.Length + PageSize - 1) / PageSize);
        var clampedPage = Math.Clamp(pageIndex, 0, pageCount - 1);
        if (resetScroll || clampedPage != pageIndex) scroll.ScrollToTop();
        pageIndex = clampedPage;
        pager.Visibility = pageCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        previous.IsEnabled = pageIndex > 0;
        next.IsEnabled = pageIndex + 1 < pageCount;
        pageInfo.Text = $"{pageIndex + 1} / {pageCount}";
        var previews = new List<(string Path, Border Frame)>();
        foreach (var group in visible.Skip(pageIndex * PageSize).Take(PageSize).GroupBy(e => e.Created.Date))
        {
            var label = Ui.Label(group.Key == DateTime.Today ? "今天" : group.Key == DateTime.Today.AddDays(-1) ? "昨天" : group.Key.ToString("yyyy 年 M 月 d 日"), 13);
            label.FontWeight = FontWeights.SemiBold;
            label.Margin = new Thickness(3, 0, 0, 8);
            groups.Children.Add(label);
            var cards = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
            groups.Children.Add(cards);
            foreach (var entry in group) cards.Children.Add(Card(entry, previews));
        }
        if (previews.Count > 0) _ = LoadPreviewsAsync(previews);
        else groups.Children.Add(EmptyState());
    }
    private StackPanel EmptyState()
    {
        var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(16, 40, 16, 18) };
        var icon = Ui.Icon("History", 36);
        icon.Margin = new Thickness(0, 0, 0, 14);
        empty.Children.Add(icon);
        var title = Ui.Label(entries.Count == 0 ? "暂无截图历史" : "这个范围内还没有截图", 18);
        title.TextAlignment = TextAlignment.Center;
        empty.Children.Add(title);
        var message = "可以切换截图类型或时间范围。";
        if (entries.Count == 0)
        {
            var app = App.Current;
            var settings = app.Settings;
            var start = app.HotkeyUnavailable(settings.CaptureKey) ? "点击“开始截图”" : $"点击“开始截图”或按 {settings.CaptureKey}";
            message = settings.HistoryEnabled
                ? $"{start}，完成后会出现在这里。"
                : "在“设置 → 历史设置”中开启“保存截图历史”，之后完成的截图会出现在这里。";
        }
        var hint = Ui.Label(message, 12, true);
        hint.TextAlignment = TextAlignment.Center;
        hint.Margin = new Thickness(0, 8, 0, 0);
        empty.Children.Add(hint);
        return empty;
    }
    private Border Card(HistoryEntry entry, List<(string Path, Border Frame)> previews)
    {
        var content = new StackPanel { Width = 210 };
        var imageFrame = new Border { Height = 148, CornerRadius = new CornerRadius(8), ClipToBounds = true, Cursor = Cursors.Hand, ToolTip = "双击重新编辑图片", Child = PreviewMessage("HistoryPreviewLoading") };
        imageFrame.SetResourceReference(Border.BackgroundProperty, "CanvasBrush");
        imageFrame.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2) return;
            try { App.Current.Edit(entry.Open()); e.Handled = true; }
            catch (Exception error) { Ui.Error(error, this); }
        };
        content.Children.Add(imageFrame);
        previews.Add((entry.Thumbnail, imageFrame));
        var metadata = new DockPanel { Margin = new Thickness(0, 14, 0, 5), LastChildFill = false }; var time = Ui.Label(entry.Created.ToString("HH:mm:ss"), 13); time.FontWeight = FontWeights.SemiBold; metadata.Children.Add(time);
        var type = entry.Kind == "Scroll" ? "长截图" : "普通截图";
        var badge = Ui.Card(Ui.Label(type, 11), 4); badge.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush"); badge.BorderThickness = new Thickness(0); DockPanel.SetDock(badge, Dock.Right); metadata.Children.Add(badge); content.Children.Add(metadata);
        content.Children.Add(Ui.Label($"{entry.Width} × {entry.Height} 像素", 11, true));
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        content.Children.Add(actions);
        void AddAction(string label, string hint, Action invoke)
        {
            var button = Ui.Button(label, invoke, hint);
            button.Margin = new Thickness(0, 0, 4, 4);
            button.Padding = new Thickness(8, 6, 8, 6);
            AutomationProperties.SetName(button, $"{label} {entry.Created:M月d日 HH:mm:ss}的{type}");
            actions.Children.Add(button);
        }
        AddAction(Strings.T("Copy"), "复制图片到剪贴板", () => Copy(entry));
        AddAction(Strings.T("Pin"), "把图片悬浮在屏幕上", () => App.Current.PinDocument(new(entry.Open())));
        AddAction(Strings.T("Edit"), "重新编辑图片", () => App.Current.Edit(entry.Open()));
        AddAction(Strings.T("Save"), "将图片保存到文件", () => Save(entry));
        AddAction(Strings.T("Delete"), "从本机历史中删除这张截图", () => DeleteEntry(entry));
        var card = Ui.Card(content, 14);
        card.Margin = new Thickness(0, 0, 14, 14);
        return card;
    }
    private static TextBlock PreviewMessage(string key)
    {
        var label = Ui.Label(Strings.T(key), 12, true);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        return label;
    }
    private async Task LoadPreviewsAsync(List<(string Path, Border Frame)> previews)
    {
        using var request = new CancellationTokenSource();
        previewRequest = request;
        try
        {
            for (int offset = 0; offset < previews.Count; offset += PreviewBatchSize)
            {
                var paths = previews.Skip(offset).Take(PreviewBatchSize).Select(p => p.Path).ToArray();
                var images = await Task.Run(() => ReadPreviews(paths, request.Token), request.Token);
                if (request.IsCancellationRequested) return;
                for (int i = 0; i < images.Length; i++)
                {
                    previews[offset + i].Frame.Child = images[i] is BitmapSource image
                        ? new Image { Source = image, Stretch = Stretch.Uniform, Margin = new Thickness(8) }
                        : PreviewMessage("HistoryPreviewUnavailable");
                }
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception e)
        {
            Log.Error("History previews", e);
            if (!request.IsCancellationRequested)
                foreach (var (_, frame) in previews)
                    if (frame.Child is TextBlock) frame.Child = PreviewMessage("HistoryPreviewUnavailable");
        }
        finally
        {
            if (ReferenceEquals(previewRequest, request)) previewRequest = null;
        }
    }
    private static BitmapSource?[] ReadPreviews(string[] paths, CancellationToken token)
    {
        var images = new BitmapSource?[paths.Length];
        for (int i = 0; i < paths.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            try { images[i] = ImageDocument.ReadBitmap(paths[i]); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FileFormatException or NotSupportedException or ArgumentException) { }
        }
        return images;
    }
    private void ShowFeedback(string text, string brush)
    {
        feedback.SetResourceReference(TextBlock.ForegroundProperty, brush);
        if (feedback.Text == text) return;
        feedback.Text = text;
        UIElementAutomationPeer.FromElement(feedback)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
    private async void Copy(HistoryEntry entry)
    {
        try { await ImageExport.Copy(new(entry.Open())); ShowFeedback("已复制到剪贴板", "SuccessBrush"); }
        catch (Exception e) { Ui.Error(e, this); }
    }
    private async void Save(HistoryEntry entry)
    {
        try { if (await ImageExport.SaveDialogAsync(new(entry.Open()), App.Current.Settings, this)) ShowFeedback("图片已保存", "SuccessBrush"); }
        catch (Exception e) { Ui.Error(e, this); }
    }
    private async void DeleteEntry(HistoryEntry entry)
    {
        try
        {
            await Task.Run(() => store.Delete(entry.Id));
            entries.RemoveAll(item => item.Id == entry.Id);
            summary.Text = $"本机共 {entries.Count} 张截图";
            Render(false);
            ShowFeedback("已删除该条截图历史", "SuccessBrush");
        }
        catch (Exception e) { Ui.Error(e, this); }
    }
}
