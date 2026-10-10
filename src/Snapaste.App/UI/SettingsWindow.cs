using Snapaste.Core;
using System.Windows.Automation;
namespace Snapaste.UI;

public sealed class SettingsWindow : Window
{
    private readonly Settings draft;
    private readonly List<Action> apply = [];
    private readonly Dictionary<Button, (StackPanel Page, string Title, string Hint)> pages = [];
    private readonly ScrollViewer body = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly TextBlock heading = Ui.Label("", 26), subtitle = Ui.Label("", 12, true), feedback = Ui.Label("修改后点击保存生效", 12, true);
    private readonly Button save;
    private bool dirty, ready;

    public SettingsWindow()
    {
        draft = System.Text.Json.JsonSerializer.Deserialize<Settings>(System.Text.Json.JsonSerializer.Serialize(App.Current.Settings))!;
        Title = "Snapaste · 设置"; Width = 960; Height = 740; MinWidth = 820; MinHeight = 620; Ui.Setup(this); WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new Grid(); root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) }); root.ColumnDefinitions.Add(new ColumnDefinition()); Content = root;
        var sidebar = new DockPanel { Margin = new Thickness(14, 24, 14, 18) };
        var sideBorder = new Border { Child = sidebar, BorderThickness = new Thickness(0, 0, 1, 0) }; sideBorder.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); sideBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); root.Children.Add(sideBorder);
        var brand = new StackPanel { Margin = new Thickness(14, 0, 0, 30) }; DockPanel.SetDock(brand, Dock.Top); sidebar.Children.Add(brand);
        var wordmark = Ui.Label("Snapaste", 23); wordmark.FontWeight = FontWeights.SemiBold; brand.Children.Add(wordmark);
        brand.Children.Add(Ui.Label("留住眼前，轻松一点。", 11, true));
        var quick = new StackPanel(); DockPanel.SetDock(quick, Dock.Bottom); sidebar.Children.Add(quick);
        quick.Children.Add(Ui.Action("Capture", "开始截图", () => { Hide(); App.Current.Capture(false); }, hint: App.Current.Settings.CaptureKey));
        quick.Children.Add(Ui.Action("History", "截图历史", App.Current.ShowHistory));
        var version = Ui.Label("0.2 · 开发预览", 10, true); version.Margin = new Thickness(12, 18, 0, 0); quick.Children.Add(version);
        var navigation = new StackPanel(); sidebar.Children.Add(navigation);
        var main = new DockPanel { Margin = new Thickness(30, 26, 30, 0) }; Grid.SetColumn(main, 1); root.Children.Add(main);
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 22) }; DockPanel.SetDock(header, Dock.Top); main.Children.Add(header); heading.FontWeight = FontWeights.SemiBold; header.Children.Add(heading); subtitle.Margin = new Thickness(0, 8, 0, 0); header.Children.Add(subtitle);
        var footer = new Grid { Margin = new Thickness(0, 14, 0, 16) }; footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); DockPanel.SetDock(footer, Dock.Bottom); main.Children.Add(footer);
        feedback.VerticalAlignment = VerticalAlignment.Center; feedback.MaxWidth = 350; footer.Children.Add(feedback);
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; Grid.SetColumn(actions, 1); footer.Children.Add(actions);
        actions.Children.Add(Ui.Button("关闭", Close)); save = Ui.Action("Done", "保存设置", Save, true, "Ctrl+S"); save.IsEnabled = false; actions.Children.Add(save); main.Children.Add(body);
        StackPanel Page(string key, string hint)
        {
            var p = new StackPanel { Margin = new Thickness(0, 0, 10, 8) };
            var button = Ui.Action(key, Strings.T(key), () => { }); button.HorizontalContentAlignment = HorizontalAlignment.Left; button.Margin = new Thickness(0, 3, 0, 3); button.Padding = new Thickness(12, 10, 12, 10); button.SetResourceReference(StyleProperty, "QuietButton");
            button.Click += (_, _) => SelectPage(button); navigation.Children.Add(button); pages.Add(button, (p, Strings.T(key), hint)); return p;
        }
        var general = Page("General", "熟悉的工作方式，配上你喜欢的细节。");
        var g1 = Group(general, "外观与启动");
        Choice(g1, "主题", "选择应用窗口的配色", ["Light", "Dark", "System"], ["浅色 · 云雾", "深色 · 夜色", "跟随系统"], draft.Theme, v => draft.Theme = v);
        Check(g1, "开机启动", "登录 Windows 后驻留系统托盘", draft.StartWithWindows, v => draft.StartWithWindows = v);
        var g2 = Group(general, "图片保存");
        var directory = Text(g2, "默认保存目录", "保存时仍可选择其他位置", draft.SaveDirectory, v => draft.SaveDirectory = v, 270);
        var browse = Ui.Action("Folder", "选择目录", () => { var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择默认保存目录", InitialDirectory = Directory.Exists(directory.Text) ? directory.Text : "" }; if (dialog.ShowDialog(this) == true) directory.Text = dialog.FolderName; }); browse.HorizontalAlignment = HorizontalAlignment.Right; g2.Children.Add(browse);
        var local = Group(general, "只在本机"); local.Children.Add(Ui.Label("无需账号，离线也能完整使用。图片和历史不会上传。", 12, true));
        var path = Ui.Label("本地数据：" + Paths.Data, 11, true); path.Margin = new Thickness(0, 12, 0, 0); local.Children.Add(path);
        var hotkeys = Page("Hotkeys", "随时开始截图，也能随时找回鼠标穿透的贴图。");
        var keys = Group(hotkeys, "全局快捷键");
        Text(keys, "普通截图", "获取屏幕画面并选择区域", draft.CaptureKey, v => draft.CaptureKey = v);
        Text(keys, "剪贴板贴图", "把图片悬浮在其他窗口上方", draft.PinKey, v => draft.PinKey = v);
        Text(keys, "滚动截图", "选择区域后连续捕获长内容", draft.ScrollKey, v => draft.ScrollKey = v);
        Text(keys, "恢复贴图交互", "显示贴图并关闭鼠标穿透", draft.RecoverKey, v => draft.RecoverKey = v);
        Notice(hotkeys, "输入示例：F1、Ctrl+F1、Ctrl+Alt+F3。保存时检查重复与系统冲突。");
        var capture = Page("NormalCapture", "从选区到复制，尽量少一步操作。");
        var detect = Group(capture, "选区辅助");
        Check(detect, "识别窗口", "鼠标移动时高亮候选窗口", draft.AutoWindow, v => draft.AutoWindow = v);
        Check(detect, "识别界面元素", "用 Tab 在控件、窗口和自由选区间切换", draft.UiElements, v => draft.UiElements = v);
        Check(detect, "放大镜", "查看像素、坐标和颜色；按 C 复制颜色", draft.Magnifier, v => draft.Magnifier = v);
        var output = Group(capture, "截图内容与输出");
        Check(output, "包含鼠标指针", "将捕获时的鼠标指针保留在图片中", draft.IncludeCursor, v => draft.IncludeCursor = v);
        Choice(output, "默认图片格式", "PNG 保留清晰文字，JPEG 适合照片", ["PNG", "JPEG", "BMP"], ["PNG", "JPEG", "BMP"], draft.DefaultFormat, v => draft.DefaultFormat = v);
        Notice(capture, Strings.T("CaptureFlowHint"));
        var pins = Page("Pins", "让参考图留在手边，调整起来也轻松。");
        var behavior = Group(pins, "窗口行为");
        Check(behavior, "恢复上次的贴图", "下次启动时恢复图片、位置和外观", draft.RestorePins, v => draft.RestorePins = v);
        Check(behavior, "默认置顶", "新贴图显示在其他窗口上方", draft.PinTopmost, v => draft.PinTopmost = v);
        var appearance = Group(pins, "大小与透明度");
        Number(appearance, "默认透明度", "0.15–1，1 表示完全不透明", draft.PinOpacity, .15, 1, v => draft.PinOpacity = v);
        Number(appearance, "默认缩放", "1 表示原始大小", draft.PinScale, .1, 8, v => draft.PinScale = v);
        Number(appearance, "最小缩放", "0.02–1", draft.MinPinScale, .02, 1, v => draft.MinPinScale = v);
        Number(appearance, "最大缩放", "1–16", draft.MaxPinScale, 1, 16, v => draft.MaxPinScale = v);
        Notice(pins, "滚轮围绕鼠标缩放 · Ctrl+滚轮调整透明度 · 双击重新编辑。");
        var scroll = Page("Scroll", "框住滚动内容，慢慢向下滚动，停下后自动拼接。");
        var flow = Group(scroll, "捕获方式");
        Choice(flow, "默认模式", "手动模式由你滚动页面，无需逐帧点击", ["Manual", "Auto"], ["手动滚动", "自动滚动"], draft.AutoScrollDefault ? "Auto" : "Manual", v => draft.AutoScrollDefault = v == "Auto");
        Check(flow, "实时长图预览", "每次拼接后显示新增内容", draft.ScrollPreview, v => draft.ScrollPreview = v);
        var timing = Group(scroll, "节奏与可靠性");
        Number(timing, "画面稳定等待", "毫秒 · 动画和图片加载较多时可增大", draft.StableMilliseconds, 200, 5000, v => draft.StableMilliseconds = (int)v, true);
        Number(timing, "匹配最低得分", "0.5–0.98 · 越高越谨慎", draft.MatchSensitivity, .5, .98, v => draft.MatchSensitivity = v);
        Number(timing, "自动滚动间隔", "毫秒 · 为页面加载留下时间", draft.AutoScrollDelay, 500, 10000, v => draft.AutoScrollDelay = (int)v, true);
        Number(timing, "最大捕获帧数", "达到上限后暂停，由你决定完成", draft.MaxScrollFrames, 2, 1000, v => draft.MaxScrollFrames = (int)v, true);
        Notice(scroll, "无法可靠匹配时会暂停。你可以手动对齐、忽略此帧或完成已有内容。");
        var history = Page("History", "保存最近的截图，随时重新复制、贴图或编辑。");
        var retention = Group(history, "本地历史");
        Check(retention, "保存截图历史", "完成的普通截图和长截图自动归档", draft.HistoryEnabled, v => draft.HistoryEnabled = v);
        Number(retention, "最多保留项目", "超出上限后清理最早的截图", draft.HistoryCount, 1, 10000, v => draft.HistoryCount = (int)v, true);
        Number(retention, "磁盘空间上限", "MB · 贴图保留独立副本", draft.HistoryMegabytes, 10, 100000, v => draft.HistoryMegabytes = (int)v, true);
        var clean = Group(history, "历史管理"); clean.Children.Add(Ui.Label("清空历史不会关闭贴图，也不会删除已保存到其他目录的文件。", 12, true));
        var clear = Ui.Action("Delete", "清空截图历史", () => { if (MessageBox.Show(this, "清空全部截图历史？此操作无法撤销，贴图将保留。", "清空历史", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) { App.Current.History.Clean(true); feedback.Text = "截图历史已清空"; } }); clear.Margin = new Thickness(0, 14, 0, 0); clear.HorizontalAlignment = HorizontalAlignment.Left; clean.Children.Add(clear);
        ready = true; SelectPage(pages.Keys.First());
        PreviewKeyDown += (_, e) => { if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { Save(); e.Handled = true; } };
        Closing += (_, e) => { if (!dirty) return; var answer = MessageBox.Show(this, "设置尚未保存，要保存再关闭吗？", "Snapaste", MessageBoxButton.YesNoCancel, MessageBoxImage.Question); if (answer == MessageBoxResult.Cancel) e.Cancel = true; else if (answer == MessageBoxResult.Yes) { Save(); e.Cancel = dirty; } };
    }
    private void SelectPage(Button selected)
    {
        foreach (var pair in pages) pair.Key.Tag = pair.Key == selected ? "Selected" : null;
        var page = pages[selected]; heading.Text = page.Title; subtitle.Text = page.Hint; body.Content = page.Page; body.ScrollToTop();
    }
    private static StackPanel Group(Panel page, string title)
    {
        var content = new StackPanel(); var label = Ui.Label(title, 13); label.FontWeight = FontWeights.SemiBold; label.Margin = new Thickness(0, 0, 0, 10); content.Children.Add(label);
        var card = Ui.Card(content); card.Margin = new Thickness(0, 0, 0, 14); page.Children.Add(card); return content;
    }
    private static void Notice(Panel p, string text) { var label = Ui.Label(text, 11, true); label.Margin = new Thickness(3, 2, 0, 10); p.Children.Add(label); }
    private static void Row(Panel p, string title, string hint, FrameworkElement control)
    {
        var row = new Grid { Margin = new Thickness(0, 10, 0, 10) }; row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labels = new StackPanel { Margin = new Thickness(0, 0, 22, 0), VerticalAlignment = VerticalAlignment.Center }; labels.Children.Add(Ui.Label(title, 12)); var detail = Ui.Label(hint, 11, true); detail.Margin = new Thickness(0, 5, 0, 0); labels.Children.Add(detail); row.Children.Add(labels);
        control.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(control, 1); row.Children.Add(control); p.Children.Add(row); AutomationProperties.SetName(control, title); AutomationProperties.SetHelpText(control, hint);
    }
    private void Changed() { if (!ready) return; dirty = true; save.IsEnabled = true; feedback.Text = "有未保存的修改"; feedback.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); }
    private void Check(Panel p, string title, string hint, bool value, Action<bool> set) { var c = new CheckBox { IsChecked = value }; c.SetResourceReference(StyleProperty, "Switch"); Row(p, title, hint, c); c.Checked += (_, _) => Changed(); c.Unchecked += (_, _) => Changed(); apply.Add(() => set(c.IsChecked == true)); }
    private TextBox Text(Panel p, string title, string hint, string value, Action<string> set, double width = 168)
    {
        var t = new TextBox { Text = value, Width = width }; Row(p, title, hint, t); t.TextChanged += (_, _) => Changed(); apply.Add(() => set(t.Text.Trim())); return t;
    }
    private void Number(Panel p, string title, string hint, double value, double min, double max, Action<double> set, bool integer = false)
        => Text(p, title, hint, value.ToString(System.Globalization.CultureInfo.InvariantCulture), v => { if (!double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n) || n < min || n > max) throw new ArgumentException($"{title} 必须在 {min}–{max} 范围内。"); if (integer && n != Math.Truncate(n)) throw new ArgumentException($"{title} 请输入整数。"); set(n); });
    private void Choice(Panel p, string title, string hint, string[] values, string[] labels, string value, Action<string> set)
    {
        var c = new ComboBox { ItemsSource = labels, SelectedIndex = Math.Max(0, Array.IndexOf(values, value)), Width = 168 }; Row(p, title, hint, c); c.SelectionChanged += (_, _) => Changed(); apply.Add(() => set(values[c.SelectedIndex]));
    }
    private void Save()
    {
        try
        {
            foreach (var a in apply) a();
            var keys = new[] { draft.CaptureKey, draft.PinKey, draft.ScrollKey, draft.RecoverKey };
            if (keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != keys.Length) throw new ArgumentException("全局快捷键不能重复。");
            foreach (var key in keys) _ = new KeyGestureConverter().ConvertFromInvariantString(key);
            if (draft.PinScale < draft.MinPinScale || draft.PinScale > draft.MaxPinScale) throw new ArgumentException("默认缩放必须在最小与最大缩放之间。");
            if (!Directory.Exists(draft.SaveDirectory)) Directory.CreateDirectory(draft.SaveDirectory);
            var snapshot = System.Text.Json.JsonSerializer.Deserialize<Settings>(System.Text.Json.JsonSerializer.Serialize(draft))!;
            App.Current.ApplySettings(snapshot); dirty = false; save.IsEnabled = false; feedback.Text = "设置已保存"; feedback.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush");
        }
        catch (Exception e) { feedback.Text = e.Message; feedback.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush"); }
    }
}
