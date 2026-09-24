namespace Snapaste.UI;

// All reusable UI labels live here; keys are stable for future satellite resources.
public static class Strings
{
    private static readonly Dictionary<string, string> Zh = new()
    {
        ["App"] = "Snapaste", ["Capture"] = "截图", ["Scroll"] = "滚动截图", ["Pin"] = "贴图",
        ["History"] = "截图历史", ["Settings"] = "设置", ["Exit"] = "退出", ["ShowPins"] = "显示全部贴图",
        ["HidePins"] = "隐藏全部贴图", ["ClosePins"] = "关闭全部贴图", ["RecoverPins"] = "恢复全部贴图交互",
        ["Copy"] = "复制", ["Save"] = "保存", ["Done"] = "完成", ["Cancel"] = "取消", ["Edit"] = "编辑",
        ["Select"] = "选择", ["Rectangle"] = "矩形", ["Ellipse"] = "椭圆", ["Line"] = "直线", ["Arrow"] = "箭头",
        ["Pen"] = "画笔", ["Highlighter"] = "马克笔", ["Text"] = "文字", ["Mosaic"] = "马赛克", ["Blur"] = "模糊",
        ["Crop"] = "裁剪", ["Delete"] = "删除", ["Undo"] = "撤销", ["Redo"] = "重做", ["Color"] = "取色",
        ["Pause"] = "暂停", ["Resume"] = "继续", ["Auto"] = "自动滚动", ["Manual"] = "手动滚动",
        ["Correction"] = "手动修正", ["Ignore"] = "忽略此帧", ["LastFrame"] = "撤销上一段",
        ["ScrollHint"] = "滚动页面继续截图 · 保留至少约 1/3 重叠 · 完成后进入编辑",
        ["LowMatch"] = "没有找到可靠的拼接位置。请修正、忽略此帧或撤销上一段。",
        ["Waiting"] = "等待页面滚动", ["Stable"] = "等待画面稳定", ["Matching"] = "分析重叠区域",
        ["NoImage"] = "剪贴板中没有可用图片。", ["Error"] = "操作未完成", ["Ok"] = "确定",
        ["OverlayHint"] = "拖动框选 / 点击选窗口 · Tab 切换窗口/控件/自由 · 方向键微调 · Shift 调大小 · C 取色 · Enter 复制 · 双击编辑 · Esc 取消",
        ["General"] = "常规", ["Hotkeys"] = "快捷键", ["NormalCapture"] = "普通截图", ["Pins"] = "贴图",
        ["Opacity"] = "透明度", ["Rotate"] = "旋转 90°", ["FlipH"] = "水平翻转", ["FlipV"] = "垂直翻转",
        ["ClickThrough"] = "鼠标穿透", ["Close"] = "关闭", ["Reset"] = "原始大小", ["AlwaysTop"] = "置顶",
        ["Font"] = "字体", ["FontSize"] = "字号", ["Bold"] = "粗体", ["Background"] = "文字背景",
    };
    public static string T(string key) => Zh.GetValueOrDefault(key, key);
}
public static class Ui
{
    public static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(29, 121, 224));
    public static Button Button(string label, Action action, string? hint = null)
    {
        var b = new Button { Content = label, Margin = new Thickness(3), Padding = new Thickness(9, 5, 9, 5), MinHeight = 30, ToolTip = hint ?? label };
        b.Click += (_, _) => { try { action(); } catch (Exception e) { Error(e); } };
        return b;
    }
    public static void Error(Exception e) { Core.Log.Error("UI", e); MessageBox.Show(e.Message, Strings.T("Error"), MessageBoxButton.OK, MessageBoxImage.Warning); }
    public static void Setup(Window window)
    {
        window.FontFamily = new FontFamily("Microsoft YaHei UI"); window.FontSize = 12;
        window.SetResourceReference(Window.BackgroundProperty, "SurfaceBrush");
        window.SetResourceReference(Window.ForegroundProperty, "TextBrush");
        window.UseLayoutRounding = true;
    }
    public static void Theme(string value)
    {
        bool dark = value == "Dark";
        if (value == "System") { using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); dark = key?.GetValue("AppsUseLightTheme") is int n && n == 0; }
        var colors = dark ? new[] { "#20252D", "#2B323D", "#F0F3F8", "#455268", "#304967" } : new[] { "#F6F8FB", "#FFFFFF", "#202B3C", "#D7DFEA", "#E8F1FD" };
        var names = new[] { "SurfaceBrush", "PanelBrush", "TextBrush", "BorderBrush", "HoverBrush" };
        for (int i = 0; i < names.Length; i++) Application.Current.Resources[names[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
    }
}
