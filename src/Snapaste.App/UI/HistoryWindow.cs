using Snapaste.Core;
namespace Snapaste.UI;

public sealed class HistoryWindow : Window
{
    private readonly HistoryStore store;
    private readonly StackPanel groups = new();
    private readonly TextBlock summary = Ui.Label("", 12, true), feedback = Ui.Label("", 11, true);
    private readonly Dictionary<string, Button> filters = [];
    private readonly ComboBox dates = new() { ItemsSource = new[] { "全部时间", "今天", "最近 7 天", "最近 30 天" }, SelectedIndex = 0, Width = 140 };
    private List<HistoryEntry> entries = [];
    private string kind = "All";
    private const int PageSize = 40;
    private int pageIndex;
    private readonly TextBlock pageInfo = Ui.Label("", 11, true);
    private readonly Button previous, next;
    public HistoryWindow(HistoryStore store)
    {
        this.store = store; Title = "Snapaste · 截图历史"; Width = 1100; Height = 750; MinWidth = 780; MinHeight = 500; Ui.Setup(this); WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new DockPanel { Margin = new Thickness(28, 24, 28, 14) }; Content = root;
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 20), LastChildFill = false }; DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
        var titles = new StackPanel(); var title = Ui.Label("截图历史", 26); title.FontWeight = FontWeights.SemiBold; titles.Children.Add(title); summary.Margin = new Thickness(0, 8, 0, 0); titles.Children.Add(summary); head.Children.Add(titles);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(actions, Dock.Right); head.Children.Add(actions); actions.Children.Add(Ui.Tool("History", "刷新历史", Reload)); actions.Children.Add(Ui.Action("Capture", "新的截图", () => { Close(); App.Current.Capture(false); }, true));
        var filterRow = new DockPanel { Margin = new Thickness(0, 0, 0, 20), LastChildFill = false }; DockPanel.SetDock(filterRow, Dock.Top); root.Children.Add(filterRow);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal }; filterRow.Children.Add(tabs);
        foreach (var value in new[] { ("All", "全部"), ("Capture", "普通截图"), ("Scroll", "长截图") })
        {
            string filter = value.Item1; var button = Ui.Button(value.Item2, () => { kind = filter; pageIndex = 0; Render(); }); button.SetResourceReference(StyleProperty, "QuietButton"); button.Margin = new Thickness(0, 0, 6, 0); filters.Add(filter, button); tabs.Children.Add(button);
        }
        DockPanel.SetDock(dates, Dock.Right); filterRow.Children.Add(dates); dates.SelectionChanged += (_, _) => { pageIndex = 0; Render(); }; System.Windows.Automation.AutomationProperties.SetName(dates, "历史时间范围");
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); feedback.VerticalAlignment = VerticalAlignment.Center; footer.Children.Add(feedback);
        var pager = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(pager, Dock.Right); footer.Children.Add(pager); previous = Ui.Tool("Previous", "上一页", () => { pageIndex--; Render(); }); next = Ui.Tool("Next", "下一页", () => { pageIndex++; Render(); }); pageInfo.VerticalAlignment = VerticalAlignment.Center; pageInfo.Margin = new Thickness(8, 0, 8, 0); pager.Children.Add(previous); pager.Children.Add(pageInfo); pager.Children.Add(next);
        root.Children.Add(new ScrollViewer { Content = groups, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }); Reload();
    }
    public void Reload() { entries = store.Entries(); summary.Text = $"{entries.Count} 张截图保存在本机 · 随时重新使用"; Render(); }
    private void Render()
    {
        groups.Children.Clear(); foreach (var pair in filters) pair.Value.Tag = pair.Key == kind ? "Selected" : null;
        DateTime earliest = dates.SelectedIndex switch { 1 => DateTime.Today, 2 => DateTime.Today.AddDays(-6), 3 => DateTime.Today.AddDays(-29), _ => DateTime.MinValue };
        var visible = entries.Where(e => (kind == "All" || (kind == "Capture" ? e.Kind != "Scroll" : e.Kind == "Scroll")) && e.Created >= earliest).ToArray(); feedback.Text = $"共 {visible.Length} 项 · 复制、贴图或重新编辑"; feedback.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        int pageCount = Math.Max(1, (visible.Length + PageSize - 1) / PageSize); pageIndex = Math.Clamp(pageIndex, 0, pageCount - 1); previous.IsEnabled = pageIndex > 0; next.IsEnabled = pageIndex + 1 < pageCount; pageInfo.Text = $"{pageIndex + 1} / {pageCount}";
        foreach (var group in visible.Skip(pageIndex * PageSize).Take(PageSize).GroupBy(e => e.Created.Date))
        {
            var label = Ui.Label(group.Key == DateTime.Today ? "今天" : group.Key == DateTime.Today.AddDays(-1) ? "昨天" : group.Key.ToString("yyyy 年 M 月 d 日"), 12, true); label.Margin = new Thickness(3, 0, 0, 10); groups.Children.Add(label);
            var cards = new WrapPanel { Margin = new Thickness(0, 0, 0, 22) }; groups.Children.Add(cards);
            foreach (var entry in group) cards.Children.Add(Card(entry));
        }
        if (visible.Length != 0) return;
        var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24, 85, 24, 24) }; var icon = Ui.Icon("History", 40); icon.Margin = new Thickness(0, 0, 0, 20); empty.Children.Add(icon);
        var title = Ui.Label(entries.Count == 0 ? "第一张截图，从这里开始" : "这个范围内还没有截图", 18); title.TextAlignment = TextAlignment.Center; empty.Children.Add(title);
        var hint = Ui.Label(entries.Count == 0 ? "按 " + App.Current.Settings.CaptureKey + " 截图，完成后会出现在这里。" : "试试其他类型或时间范围。", 12, true); hint.TextAlignment = TextAlignment.Center; hint.Margin = new Thickness(0, 10, 0, 0); empty.Children.Add(hint); groups.Children.Add(empty);
    }
    private Border Card(HistoryEntry entry)
    {
        var content = new StackPanel { Width = 210 };
        var imageFrame = new Border { Height = 148, CornerRadius = new CornerRadius(8), ClipToBounds = true }; imageFrame.SetResourceReference(Border.BackgroundProperty, "CanvasBrush"); content.Children.Add(imageFrame);
        try { imageFrame.Child = new Image { Source = ImageDocument.ReadBitmap(entry.Thumbnail), Stretch = Stretch.Uniform, Margin = new Thickness(8) }; }
        catch (Exception e) when (e is IOException or FileFormatException or NotSupportedException) { imageFrame.Child = Ui.Label("预览不可用", 11, true); }
        var metadata = new DockPanel { Margin = new Thickness(0, 14, 0, 5), LastChildFill = false }; var time = Ui.Label(entry.Created.ToString("HH:mm:ss"), 13); time.FontWeight = FontWeights.SemiBold; metadata.Children.Add(time);
        var type = Ui.Label(entry.Kind == "Scroll" ? "长截图" : "截图", 10); var badge = Ui.Card(type, 4); badge.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush"); badge.BorderThickness = new Thickness(0); DockPanel.SetDock(badge, Dock.Right); metadata.Children.Add(badge); content.Children.Add(metadata);
        content.Children.Add(Ui.Label($"{entry.Width} × {entry.Height} 像素", 11, true));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-3, 12, -3, 0) }; content.Children.Add(actions);
        actions.Children.Add(Ui.Action("Copy", "复制", () => Copy(entry))); actions.Children.Add(Ui.Tool("Pin", "贴图", () => App.Current.PinDocument(new(entry.Open())))); actions.Children.Add(Ui.Tool("Edit", "重新编辑", () => App.Current.Edit(entry.Open()))); actions.Children.Add(Ui.Tool("Save", "保存", () => Save(entry)));
        var card = Ui.Card(content, 14); card.Margin = new Thickness(0, 0, 14, 14); return card;
    }
    private async void Copy(HistoryEntry entry)
    {
        try { await ImageExport.Copy(new(entry.Open())); feedback.Text = "已复制到剪贴板"; feedback.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush"); }
        catch (Exception e) { Ui.Error(e); }
    }
    private async void Save(HistoryEntry entry)
    {
        try { if (await ImageExport.SaveDialogAsync(new(entry.Open()), App.Current.Settings, this)) { feedback.Text = "图片已保存"; feedback.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush"); } }
        catch (Exception e) { Ui.Error(e); }
    }
}
