using Snapaste.Core;
using Snapaste.Platform;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Threading;
namespace Snapaste.UI;

public sealed class SettingsWindow : Window
{
    private readonly Settings draft;
    private readonly List<(Control Field, Action Read)> apply = [];
    private readonly Dictionary<string, (Button Navigation, StackPanel Content)> pages = [];
    private readonly ScrollViewer body = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly TextBlock heading = Ui.Label("", 24), feedback = Ui.Label("", 12, true), captureShortcut = Ui.Label("", 12, true);
    private readonly TextBlock hotkeyStatus = Ui.Label("", 12);
    private readonly Button save, clearHistory;
    private readonly (string Label, TextBox Editor)[] hotkeyFields;
    private readonly TextBox saveDirectory;
    private string? activePage;
    private bool dirty, ready;

    public SettingsWindow()
    {
        draft = System.Text.Json.JsonSerializer.Deserialize<Settings>(System.Text.Json.JsonSerializer.Serialize(App.Current.Settings))!;
        Title = "Snapaste · 设置";
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(900, workArea.Width); Height = Math.Min(700, workArea.Height);
        MinWidth = Math.Min(630, workArea.Width); MinHeight = Math.Min(360, workArea.Height);
        Ui.Setup(this); WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        Content = root;

        var sidebar = new StackPanel { Margin = new Thickness(12, 18, 12, 12) };
        var sidebarScroll = new ScrollViewer { Content = sidebar, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var sideBorder = new Border { Child = sidebarScroll, BorderThickness = new Thickness(0, 0, 1, 0) };
        sideBorder.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        sideBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        root.Children.Add(sideBorder);
        var wordmark = Ui.Label("Snapaste", 21);
        wordmark.FontWeight = FontWeights.SemiBold;
        wordmark.Margin = new Thickness(10, 0, 0, 2);
        sidebar.Children.Add(wordmark);
        var version = typeof(App).Assembly.GetName().Version?.ToString(3);
        var versionLabel = Ui.Label($"{version} · 开发预览", 11, true);
        versionLabel.Margin = new Thickness(10, 0, 0, 16);
        sidebar.Children.Add(versionLabel);
        var navigation = new StackPanel();
        sidebar.Children.Add(navigation);
        var historyLink = Ui.Action("History", "打开截图历史", App.Current.ShowHistory);
        historyLink.HorizontalContentAlignment = HorizontalAlignment.Left;
        historyLink.Margin = new Thickness(0, 14, 0, 0);
        sidebar.Children.Add(historyLink);

        var main = new Grid { Margin = new Thickness(22, 18, 22, 12) };
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition());
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(main, 1);
        root.Children.Add(main);
        heading.FontWeight = FontWeights.SemiBold;
        heading.Margin = new Thickness(0, 0, 0, 14);
        main.Children.Add(heading);
        Grid.SetRow(body, 1);
        main.Children.Add(body);
        var footer = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(footer, 2);
        main.Children.Add(footer);
        feedback.Margin = new Thickness(0, 0, 10, 0);
        feedback.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetLiveSetting(feedback, AutomationLiveSetting.Polite);
        footer.Children.Add(feedback);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);
        actions.Children.Add(Ui.Button("关闭", Close));
        save = Ui.Action("Done", "保存设置", Save, true, "Ctrl+S");
        AutomationProperties.SetAcceleratorKey(save, "Ctrl+S");
        save.IsEnabled = false;
        actions.Children.Add(save);

        StackPanel Page(string key, string? label = null)
        {
            var page = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
            var button = Ui.Action(key, label ?? Strings.T(key), () => SelectPage(key));
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Margin = new Thickness(0, 2, 0, 2);
            button.Padding = new Thickness(10, 8, 10, 8);
            button.SetResourceReference(StyleProperty, "QuietButton");
            navigation.Children.Add(button);
            pages.Add(key, (button, page));
            return page;
        }

        var general = Page("General");
        var quickStart = new StackPanel();
        var startRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        var start = Ui.Action("Capture", Strings.T("StartCapture"), () => { Hide(); App.Current.Capture(false); }, true);
        start.Margin = new Thickness(0, 0, 12, 0);
        startRow.Children.Add(start);
        captureShortcut.VerticalAlignment = VerticalAlignment.Center;
        startRow.Children.Add(captureShortcut);
        quickStart.Children.Add(startRow);
        quickStart.Children.Add(Ui.Label(Strings.T("SettingsQuickStart")));
        var trayHint = Ui.Label(Strings.T("SettingsTrayHint"), 12, true);
        trayHint.Margin = new Thickness(0, 6, 0, 0);
        quickStart.Children.Add(trayHint);
        var startCard = Ui.Card(quickStart, 16);
        startCard.Margin = new Thickness(0, 0, 0, 12);
        general.Children.Add(startCard);
        var g1 = Group(general, "外观与启动");
        Choice(g1, "主题", "选择应用窗口的配色", ["Light", "Dark", "System"], ["浅色", "深色", "跟随系统"], draft.Theme, v => draft.Theme = v);
        Check(g1, "开机启动", "登录 Windows 后在任务栏右下角显示图标", draft.StartWithWindows, v => draft.StartWithWindows = v);
        var g2 = Group(general, "图片保存");
        saveDirectory = Text(g2, "默认保存文件夹", "保存时仍可选择其他位置", draft.SaveDirectory, v => draft.SaveDirectory = v, 220);
        var browse = Ui.Action("Folder", "选择文件夹", () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择默认保存文件夹", InitialDirectory = Directory.Exists(saveDirectory.Text) ? saveDirectory.Text : "" };
            if (dialog.ShowDialog(this) == true) saveDirectory.Text = dialog.FolderName;
        });
        browse.HorizontalAlignment = HorizontalAlignment.Right;
        g2.Children.Add(browse);
        var local = Group(general, "只在本机"); local.Children.Add(Ui.Label("无需账号，离线也能完整使用。图片和历史不会上传。", 12, true));
        var path = Ui.Label("本地数据：" + Paths.Data, 11, true); path.Margin = new Thickness(0, 12, 0, 0); local.Children.Add(path);
        var hotkeys = Page("Hotkeys");
        Notice(hotkeys, "在输入框中填写按键名称，例如 F1、Ctrl+F1 或 Ctrl+Alt+F3。保存时会检查快捷键是否可用。");
        hotkeyStatus.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        hotkeyStatus.Margin = new Thickness(3, 2, 0, 10);
        hotkeys.Children.Add(hotkeyStatus);
        var keys = Group(hotkeys, "全局快捷键");
        (string, TextBox) Shortcut(string label, string hint, string value, Action<string> set)
            => (label, Text(keys, label, hint, value, set));
        hotkeyFields =
        [
            Shortcut("普通截图", "获取屏幕画面并选择区域", draft.CaptureKey, v => draft.CaptureKey = v),
            Shortcut("剪贴板贴图", "把复制的图片悬浮在屏幕上", draft.PinKey, v => draft.PinKey = v),
            Shortcut("滚动截图", "选择区域后连续捕获长内容", draft.ScrollKey, v => draft.ScrollKey = v),
            Shortcut("恢复贴图交互", "显示贴图，使其可以用鼠标操作", draft.RecoverKey, v => draft.RecoverKey = v),
        ];
        var capture = Page("NormalCapture");
        var detect = Group(capture, "选区辅助");
        Check(detect, "识别窗口", "鼠标移动时高亮候选窗口", draft.AutoWindow, v => draft.AutoWindow = v);
        Check(detect, "识别界面元素", "用 Tab 在控件、窗口和自由选区间切换", draft.UiElements, v => draft.UiElements = v);
        Check(detect, "放大镜", "查看像素、坐标和颜色；按 C 复制颜色", draft.Magnifier, v => draft.Magnifier = v);
        var output = Group(capture, "截图内容与输出");
        Check(output, "包含鼠标指针", "将捕获时的鼠标指针保留在图片中", draft.IncludeCursor, v => draft.IncludeCursor = v);
        Choice(output, "默认图片格式", "PNG 保留清晰文字，JPEG 适合照片", ["PNG", "JPEG", "BMP"], ["PNG", "JPEG", "BMP"], draft.DefaultFormat, v => draft.DefaultFormat = v);
        Notice(capture, Strings.T("CaptureFlowHint"));
        var pins = Page("Pins");
        var behavior = Group(pins, "窗口行为");
        Check(behavior, "恢复上次的贴图", "下次启动时恢复图片、位置和外观", draft.RestorePins, v => draft.RestorePins = v);
        Check(behavior, "默认置顶", "新贴图显示在其他窗口上方", draft.PinTopmost, v => draft.PinTopmost = v);
        var appearance = Group(pins, "大小与透明度");
        Number(appearance, "默认透明度", "0.15–1，1 表示完全不透明", draft.PinOpacity, .15, 1, v => draft.PinOpacity = v);
        Number(appearance, "默认缩放", "1 表示原始大小", draft.PinScale, .1, 8, v => draft.PinScale = v);
        Number(appearance, "最小缩放", "0.02–1", draft.MinPinScale, .02, 1, v => draft.MinPinScale = v);
        Number(appearance, "最大缩放", "1–16", draft.MaxPinScale, 1, 16, v => draft.MaxPinScale = v);
        Notice(pins, "滚轮缩放贴图，Ctrl+滚轮调整透明度，按 E 重新编辑。");
        var scroll = Page("Scroll");
        var flow = Group(scroll, "捕获方式");
        Choice(flow, "默认模式", "手动模式由你滚动页面，无需逐帧点击", ["Manual", "Auto"], ["手动滚动", "自动滚动"], draft.AutoScrollDefault ? "Auto" : "Manual", v => draft.AutoScrollDefault = v == "Auto");
        Check(flow, "实时长图预览", "每次拼接后显示新增内容", draft.ScrollPreview, v => draft.ScrollPreview = v);
        var timing = Group(scroll, "节奏与可靠性");
        Number(timing, "画面稳定等待", "毫秒 · 动画和图片加载较多时可增大", draft.StableMilliseconds, 200, 5000, v => draft.StableMilliseconds = (int)v, true);
        Number(timing, "匹配最低得分", "0.5–0.98 · 越高越谨慎", draft.MatchSensitivity, .5, .98, v => draft.MatchSensitivity = v);
        Number(timing, "自动滚动间隔", "毫秒 · 为页面加载留下时间", draft.AutoScrollDelay, 500, 10000, v => draft.AutoScrollDelay = (int)v, true);
        Number(timing, "最大捕获帧数", "达到上限后暂停，由你决定完成", draft.MaxScrollFrames, 2, 1000, v => draft.MaxScrollFrames = (int)v, true);
        Notice(scroll, "无法可靠匹配时会暂停。你可以手动对齐、忽略此帧或完成已有内容。");
        var history = Page("History", "历史设置");
        var retention = Group(history, "本地历史");
        Check(retention, "保存截图历史", "完成的普通截图和长截图自动归档", draft.HistoryEnabled, v => draft.HistoryEnabled = v);
        Number(retention, "最多保留项目", "超出上限后清理最早的截图", draft.HistoryCount, 1, 10000, v => draft.HistoryCount = (int)v, true);
        Number(retention, "磁盘空间上限", "MB · 贴图保留独立副本", draft.HistoryMegabytes, 10, 100000, v => draft.HistoryMegabytes = (int)v, true);
        var clean = Group(history, "历史管理"); clean.Children.Add(Ui.Label("清空历史不会关闭贴图，也不会删除已保存到其他目录的文件。", 12, true));
        clearHistory = Ui.Action("Delete", "清空截图历史", ClearHistory);
        clearHistory.Margin = new Thickness(0, 12, 0, 0);
        clearHistory.HorizontalAlignment = HorizontalAlignment.Left;
        clean.Children.Add(clearHistory);
        ready = true;
        UpdateHotkeyStatus();
        SelectPage("General");
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.S || Keyboard.Modifiers != ModifierKeys.Control) return;
            Save();
            e.Handled = true;
        };
        Closing += (_, e) =>
        {
            if (!dirty) return;
            var answer = MessageBox.Show(this, "保存修改后再关闭设置？选择“否”将放弃这次修改。", "设置尚未保存", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
            if (answer == MessageBoxResult.Cancel) e.Cancel = true;
            else if (answer == MessageBoxResult.Yes) { Save(); e.Cancel = dirty; }
        };
    }
    private void SelectPage(string key)
    {
        if (activePage == key) return;
        activePage = key;
        foreach (var (id, page) in pages)
        {
            page.Navigation.Tag = id == key ? "Selected" : null;
            AutomationProperties.SetItemStatus(page.Navigation, id == key ? "当前页面" : "");
        }
        var selected = pages[key];
        heading.Text = AutomationProperties.GetName(selected.Navigation);
        body.Content = selected.Content;
        body.ScrollToTop();
    }
    private static StackPanel Group(Panel page, string title)
    {
        var content = new StackPanel(); var label = Ui.Label(title, 13); label.FontWeight = FontWeights.SemiBold; label.Margin = new Thickness(0, 0, 0, 10); content.Children.Add(label);
        var card = Ui.Card(content); card.Margin = new Thickness(0, 0, 0, 14); page.Children.Add(card); return content;
    }
    private static void Notice(Panel p, string text) { var label = Ui.Label(text, 12, true); label.Margin = new Thickness(3, 2, 0, 10); p.Children.Add(label); }
    private static void Row(Panel p, string title, string hint, FrameworkElement control)
    {
        var row = new Grid { Margin = new Thickness(0, 7, 0, 7) }; row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labels = new StackPanel { Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        var label = Ui.Label(title, 12); labels.Children.Add(label);
        var detail = Ui.Label(hint, 11, true); detail.Margin = new Thickness(0, 4, 0, 0); labels.Children.Add(detail); row.Children.Add(labels);
        control.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(control, 1); row.Children.Add(control); p.Children.Add(row);
        AutomationProperties.SetLabeledBy(control, label);
        AutomationProperties.SetName(control, title);
        AutomationProperties.SetHelpText(control, hint);
    }
    private void ShowFeedback(string message, string brush)
    {
        feedback.SetResourceReference(TextBlock.ForegroundProperty, brush);
        if (feedback.Text == message) return;
        feedback.Text = message;
        UIElementAutomationPeer.FromElement(feedback)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
    private void Changed()
    {
        if (!ready) return;
        dirty = true;
        save.IsEnabled = true;
        ShowFeedback("有未保存的修改", "MutedBrush");
    }
    private void Check(Panel p, string title, string hint, bool value, Action<bool> set) { var c = new CheckBox { IsChecked = value }; c.SetResourceReference(StyleProperty, "Switch"); Row(p, title, hint, c); c.Checked += (_, _) => Changed(); c.Unchecked += (_, _) => Changed(); apply.Add((c, () => set(c.IsChecked == true))); }
    private TextBox Text(Panel p, string title, string hint, string value, Action<string> set, double width = 168)
    {
        var t = new TextBox { Text = value, Width = width }; Row(p, title, hint, t); t.TextChanged += (_, _) => Changed(); apply.Add((t, () => set(t.Text.Trim()))); return t;
    }
    private void Number(Panel p, string title, string hint, double value, double min, double max, Action<double> set, bool integer = false)
        => Text(p, title, hint, value.ToString(System.Globalization.CultureInfo.InvariantCulture), v => { if (!double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n) || n < min || n > max) throw new ArgumentException($"{title} 必须在 {min}–{max} 范围内。"); if (integer && n != Math.Truncate(n)) throw new ArgumentException($"{title} 请输入整数。"); set(n); });
    private void Choice(Panel p, string title, string hint, string[] values, string[] labels, string value, Action<string> set)
    {
        var c = new ComboBox { ItemsSource = labels, SelectedIndex = Math.Max(0, Array.IndexOf(values, value)), Width = 168 }; Row(p, title, hint, c); c.SelectionChanged += (_, _) => Changed(); apply.Add((c, () => set(values[c.SelectedIndex])));
    }
    private void FocusField(Control field)
    {
        for (DependencyObject? parent = field; parent != null; parent = LogicalTreeHelper.GetParent(parent))
        {
            var page = pages.FirstOrDefault(p => ReferenceEquals(p.Value.Content, parent));
            if (page.Key == null) continue;
            SelectPage(page.Key);
            break;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!IsVisible) return;
            field.Focus();
            field.BringIntoView();
            if (field is TextBox editor) editor.SelectAll();
        }));
    }
    private bool ValidateHotkeys()
    {
        if (!App.Current.HotkeysChanged(hotkeyFields[0].Editor.Text, hotkeyFields[1].Editor.Text,
                hotkeyFields[2].Editor.Text, hotkeyFields[3].Editor.Text)) return true;
        var used = new Dictionary<Hotkeys.Gesture, string>();
        foreach (var (label, editor) in hotkeyFields)
        {
            Hotkeys.Gesture key;
            try { key = Hotkeys.Parse(editor.Text.Trim()); }
            catch (HotkeyRegistrationException)
            {
                ShowFeedback($"请填写“{label}”的快捷键，例如 F1 或 Ctrl+Alt+F3。", "WarningBrush");
                FocusField(editor);
                return false;
            }
            if (used.TryGetValue(key, out var other))
            {
                ShowFeedback($"“{other}”和“{label}”的快捷键重复，请更改其中一项。", "WarningBrush");
                FocusField(editor);
                return false;
            }
            used.Add(key, label);
        }
        return true;
    }
    private void UpdateHotkeyStatus()
    {
        var app = App.Current;
        captureShortcut.Text = app.HotkeyUnavailable(app.Settings.CaptureKey) ? "截图快捷键未注册" : $"快捷键  {app.Settings.CaptureKey}";
        hotkeyStatus.Visibility = app.HotkeyConflicts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var gestures = string.Join("、", app.HotkeyConflicts.Select(conflict => string.IsNullOrWhiteSpace(conflict.Gesture) ? "空白" : conflict.Gesture));
        hotkeyStatus.Text = $"未注册的快捷键：{gestures}。可从托盘菜单截图、贴图或恢复贴图交互。";
    }
    private async void ClearHistory()
    {
        if (MessageBox.Show(this, "清空本机的全部截图历史？此操作无法撤销。", "清空截图历史", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        clearHistory.IsEnabled = false;
        ShowFeedback("正在清空截图历史…", "MutedBrush");
        try
        {
            await App.Current.ClearHistoryAsync();
            ShowFeedback("截图历史已清空", "SuccessBrush");
        }
        catch (Exception e)
        {
            Log.Error("Clear history", e);
            ShowFeedback("暂时无法清空截图历史，请重试。", "WarningBrush");
        }
        finally { clearHistory.IsEnabled = true; }
    }
    private void Save()
    {
        if (!dirty || !ValidateHotkeys()) return;
        Control? field = null;
        try
        {
            foreach (var input in apply)
            {
                field = input.Field;
                input.Read();
            }
            field = null;
            if (draft.PinScale < draft.MinPinScale || draft.PinScale > draft.MaxPinScale) throw new ArgumentException("默认缩放必须在最小与最大缩放之间。");
            if (draft.SaveDirectory.Length > 0 &&
                !string.Equals(draft.SaveDirectory, App.Current.Settings.SaveDirectory, StringComparison.OrdinalIgnoreCase))
            {
                field = saveDirectory;
                Directory.CreateDirectory(draft.SaveDirectory);
                field = null;
            }
            var snapshot = System.Text.Json.JsonSerializer.Deserialize<Settings>(System.Text.Json.JsonSerializer.Serialize(draft))!;
            App.Current.ApplySettings(snapshot);
            dirty = false;
            save.IsEnabled = false;
            UpdateHotkeyStatus();
            ShowFeedback("设置已保存", "SuccessBrush");
        }
        catch (HotkeyRegistrationException e)
        {
            ShowFeedback(e.Message, "WarningBrush");
            var failed = hotkeyFields.FirstOrDefault(item => string.Equals(item.Editor.Text.Trim(), e.Gesture, StringComparison.OrdinalIgnoreCase)).Editor;
            if (failed != null) FocusField(failed);
        }
        catch (Exception e)
        {
            if (field == saveDirectory)
            {
                Log.Error("Settings save folder", e);
                ShowFeedback("无法使用这个保存文件夹，请选择其他位置。", "WarningBrush");
            }
            else ShowFeedback(e.Message, "WarningBrush");
            if (field != null) FocusField(field);
        }
    }
}
