using Snapaste.Core;
namespace Snapaste.UI;

public sealed class SettingsWindow : Window
{
    private readonly Settings draft;
    private readonly List<Action> apply = [];
    public SettingsWindow()
    {
        draft = System.Text.Json.JsonSerializer.Deserialize<Settings>(System.Text.Json.JsonSerializer.Serialize(App.Current.Settings))!;
        Title = "Snapaste · 设置"; Width = 680; Height = 640; MinWidth = 570; MinHeight = 500; Ui.Setup(this); WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new DockPanel { Margin = new Thickness(16) }; Content = root;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(Ui.Button(Strings.T("Cancel"), Close)); footer.Children.Add(Ui.Button("保存设置", Save));
        var tabs = new TabControl(); root.Children.Add(tabs);
        StackPanel Tab(string key) { var p = new StackPanel { Margin = new Thickness(18) }; tabs.Items.Add(new TabItem { Header = Strings.T(key), Content = new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } }); return p; }
        var general = Tab("General");
        general.Children.Add(new TextBlock { Text = "Snapaste 0.1 · 本地截图与滚动长图\n无账号、无遥测，图片保存在本机。\n启动后驻留托盘，关闭设置不会退出。", Margin = new Thickness(0, 0, 0, 18), TextWrapping = TextWrapping.Wrap });
        Check(general, "开机启动", draft.StartWithWindows, v => draft.StartWithWindows = v);
        Choice(general, "主题（Light / Dark / System）", ["Light", "Dark", "System"], draft.Theme, v => draft.Theme = v);
        Text(general, "默认保存目录", draft.SaveDirectory, v => draft.SaveDirectory = v);
        general.Children.Add(new TextBlock { Text = "配置与缓存：" + Paths.Data, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 0), Foreground = Brushes.DimGray });
        var hotkeys = Tab("Hotkeys");
        Text(hotkeys, "普通截图", draft.CaptureKey, v => draft.CaptureKey = v); Text(hotkeys, "贴图", draft.PinKey, v => draft.PinKey = v); Text(hotkeys, "滚动截图", draft.ScrollKey, v => draft.ScrollKey = v); Text(hotkeys, "恢复全部穿透贴图", draft.RecoverKey, v => draft.RecoverKey = v);
        hotkeys.Children.Add(new TextBlock { Text = "格式示例：F1、Ctrl+F1、Ctrl+Alt+F3。冲突会明确提示。", TextWrapping = TextWrapping.Wrap });
        var capture = Tab("NormalCapture");
        Check(capture, "自动窗口识别", draft.AutoWindow, v => draft.AutoWindow = v); Check(capture, "UI 元素识别（Tab 可临时切换）", draft.UiElements, v => draft.UiElements = v); Check(capture, "放大镜、坐标与颜色", draft.Magnifier, v => draft.Magnifier = v); Check(capture, "包含鼠标指针", draft.IncludeCursor, v => draft.IncludeCursor = v);
        Choice(capture, "默认图片格式", ["PNG", "JPEG", "BMP"], draft.DefaultFormat, v => draft.DefaultFormat = v);
        var pins = Tab("Pins");
        Check(pins, "启动时恢复贴图", draft.RestorePins, v => draft.RestorePins = v); Check(pins, "默认置顶", draft.PinTopmost, v => draft.PinTopmost = v);
        Number(pins, "默认透明度（0.15–1）", draft.PinOpacity, .15, 1, v => draft.PinOpacity = v); Number(pins, "默认缩放", draft.PinScale, .1, 8, v => draft.PinScale = v); Number(pins, "最小缩放", draft.MinPinScale, .02, 1, v => draft.MinPinScale = v); Number(pins, "最大缩放", draft.MaxPinScale, 1, 16, v => draft.MaxPinScale = v);
        pins.Children.Add(new TextBlock { Text = "滚轮围绕鼠标缩放，Ctrl+滚轮调透明度。\n穿透恢复快捷键在「快捷键」页设置。", TextWrapping = TextWrapping.Wrap });
        var scroll = Tab("Scroll");
        Check(scroll, "默认自动滚动", draft.AutoScrollDefault, v => draft.AutoScrollDefault = v); Check(scroll, "显示实时预览", draft.ScrollPreview, v => draft.ScrollPreview = v);
        Number(scroll, "画面稳定等待（ms）", draft.StableMilliseconds, 200, 5000, v => draft.StableMilliseconds = (int)v); Number(scroll, "匹配最低得分（0.5–0.98）", draft.MatchSensitivity, .5, .98, v => draft.MatchSensitivity = v); Number(scroll, "自动滚动间隔（ms）", draft.AutoScrollDelay, 500, 10000, v => draft.AutoScrollDelay = (int)v); Number(scroll, "最大帧数", draft.MaxScrollFrames, 2, 1000, v => draft.MaxScrollFrames = (int)v);
        scroll.Children.Add(new TextBlock { Text = "低可信匹配始终暂停，允许修正、忽略、撤销或完成。\n支持向下滚动；避免整页跳转，保留至少约 1/3 重叠。", TextWrapping = TextWrapping.Wrap });
        var history = Tab("History"); Check(history, "保存截图历史", draft.HistoryEnabled, v => draft.HistoryEnabled = v); Number(history, "最多保留项目", draft.HistoryCount, 1, 10000, v => draft.HistoryCount = (int)v); Number(history, "磁盘上限（MB）", draft.HistoryMegabytes, 10, 100000, v => draft.HistoryMegabytes = (int)v);
        history.Children.Add(new TextBlock { Text = "历史目录：" + Paths.History + "\n贴图使用独立图片副本，清理历史不会破坏贴图。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 16) });
        history.Children.Add(Ui.Button("清空历史", () => { if (MessageBox.Show("清空全部截图历史？贴图缓存将保留。", "Snapaste", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) App.Current.History.Clean(true); }));
    }
    private void Check(Panel p, string label, bool value, Action<bool> set) { var c = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 8, 0, 8) }; p.Children.Add(c); apply.Add(() => set(c.IsChecked == true)); }
    private void Text(Panel p, string label, string value, Action<string> set)
    {
        p.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 10, 0, 5) }); var t = new TextBox { Text = value, Padding = new Thickness(6), MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 240 }; p.Children.Add(t); apply.Add(() => set(t.Text.Trim()));
    }
    private void Number(Panel p, string label, double value, double min, double max, Action<double> set)
        => Text(p, label, value.ToString(System.Globalization.CultureInfo.InvariantCulture), v => { if (!double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number) || number < min || number > max) throw new ArgumentException($"{label} 必须在 {min}–{max} 范围内。"); set(number); });
    private void Choice(Panel p, string label, string[] values, string value, Action<string> set)
    {
        p.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 10, 0, 5) }); var c = new ComboBox { ItemsSource = values, SelectedItem = value, Width = 200, HorizontalAlignment = HorizontalAlignment.Left }; p.Children.Add(c); apply.Add(() => set((string)c.SelectedItem));
    }
    private void Save()
    {
        foreach (var a in apply) a();
        var keys = new[] { draft.CaptureKey, draft.PinKey, draft.ScrollKey, draft.RecoverKey };
        if (keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != keys.Length) throw new ArgumentException("全局快捷键不能重复。");
        foreach (var key in keys) _ = new KeyGestureConverter().ConvertFromInvariantString(key);
        if (!Directory.Exists(draft.SaveDirectory)) Directory.CreateDirectory(draft.SaveDirectory);
        App.Current.ApplySettings(draft); Close();
    }
}
