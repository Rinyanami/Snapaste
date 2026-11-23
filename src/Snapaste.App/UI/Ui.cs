using System.Collections.Concurrent;
using System.Windows.Automation;
using System.Windows.Data;
using System.Windows.Media.Effects;
using Snapaste.Platform;
namespace Snapaste.UI;

public static class Ui
{
    public static bool IsDark { get; private set; }
    public static Brush Accent => Brush("AccentBrush");
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
    public static TextBlock Label(string text, double size = 12, bool muted = false)
    {
        var label = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(TextBlock.ForegroundProperty, muted ? "MutedBrush" : "TextBrush"); return label;
    }
    public static Button Button(string label, Action action, string? hint = null)
    {
        var button = new Button { Content = label, Margin = new Thickness(3), ToolTip = hint ?? label };
        AutomationProperties.SetName(button, label);
        ToolTipService.SetInitialShowDelay(button, 450);
        button.Click += (_, _) => { try { action(); } catch (Exception e) { Error(e, Window.GetWindow(button)); } };
        return button;
    }
    public static Button Action(string icon, string label, Action action, bool primary = false, string? hint = null)
    {
        var button = Button(label, action, hint); var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(Icon(icon)); content.Children.Add(new TextBlock { Text = label, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }); button.Content = content;
        if (primary) button.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton"); return button;
    }
    private static readonly Lazy<string[]> FontNames = new(() =>
        Fonts.SystemFontFamilies.Select(f => f.Source).Order().ToArray());

    public static void LoadFontsOnOpen(ComboBox control)
    {
        control.DropDownOpened += (_, _) =>
        {
            if (control.ItemsSource != null) return;
            var currentFont = control.Text;
            control.ItemsSource = FontNames.Value;
            control.Text = currentFont;
        };
    }

    public static void ObserveText(ComboBox control, Window owner, Action changed)
    {
        // Observe the ComboBox value, not its template TextBox's routed event:
        // that event can arrive while the value is still being synchronized.
        var property = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(ComboBox.TextProperty, typeof(ComboBox));
        EventHandler handler = (_, _) => changed();
        property.AddValueChanged(control, handler);
        owner.Closed += (_, _) => property.RemoveValueChanged(control, handler);
    }
    public static bool TryFontSize(string text, out double value)
    {
        return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value) && value is >= 8 and <= 300;
    }
    public static Button Tool(string icon, string label, Action action, string? hint = null)
    {
        var button = Button(label, action, hint == null || hint.Contains(label, StringComparison.Ordinal) ? hint : label + " · " + hint); button.Content = Icon(icon, 18); button.Width = 36; button.Height = 36; button.Padding = new Thickness(8); button.Margin = new Thickness(2);
        button.SetResourceReference(FrameworkElement.StyleProperty, "QuietButton"); return button;
    }
    public static Border Card(UIElement child, double padding = 18, bool floating = false)
    {
        var card = new Border { Child = child, CornerRadius = new CornerRadius(12), Padding = new Thickness(padding), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        if (floating) card.Effect = new DropShadowEffect { Color = Colors.Black, Opacity = .14, BlurRadius = 16, ShadowDepth = 4 };
        return card;
    }
    public static Border Divider() { var line = new Border { Width = 1, Height = 22, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }; line.SetResourceReference(Border.BackgroundProperty, "BorderBrush"); return line; }
    public static FrameworkElement Icon(string key, double size = 18)
    {
        var path = new System.Windows.Shapes.Path { Data = Icons.Geometry(key), StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Control), 1), FallbackValue = Accent });
        var canvas = new Canvas { Width = 20, Height = 20 }; canvas.Children.Add(path); return new Viewbox { Child = canvas, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
    }
    public static void Error(Exception e, Window? owner = null)
    {
        Core.Log.Error("UI", e);
        if (owner?.IsVisible == true) MessageBox.Show(owner, e.Message, Strings.T("Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
        else MessageBox.Show(e.Message, Strings.T("Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    public static void Setup(Window window, bool setIcon = true)
    {
        window.FontFamily = new FontFamily("Microsoft YaHei UI"); window.FontSize = 12;
        if (setIcon) window.Icon = BrandIcon.Source;
        window.SetResourceReference(Window.BackgroundProperty, "SurfaceBrush"); window.SetResourceReference(Window.ForegroundProperty, "TextBrush");
        window.UseLayoutRounding = true; TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
        window.SourceInitialized += (_, _) => Native.ApplyDarkTitleBar(window, IsDark);
    }
    public static void Theme(string value)
    {
        bool dark = value == "Dark";
        if (value == "System") { using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); dark = key?.GetValue("AppsUseLightTheme") is int n && n == 0; }
        IsDark = dark;
        var colors = dark
            ? new[] { "#24212B", "#2E2A36", "#F3EFF8", "#AAA2B6", "#443D50", "#393242", "#B49BEF", "#453953", "#1D1A23", "#86C5AE", "#E4B47C", "#7960B7" }
            : new[] { "#F5F3F7", "#FFFFFF", "#302C38", "#68616F", "#E5E0EB", "#F0ECF6", "#7762BA", "#EEE8FA", "#E7E4ED", "#2E725F", "#92531D", "#7762BA" };
        var names = new[] { "SurfaceBrush", "PanelBrush", "TextBrush", "MutedBrush", "BorderBrush", "HoverBrush", "AccentBrush", "AccentSoftBrush", "CanvasBrush", "SuccessBrush", "WarningBrush", "PrimaryBrush" };
        for (int i = 0; i < names.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
            brush.Freeze();
            Application.Current.Resources[names[i]] = brush;
        }
        if (Application.Current?.Windows is { } windows)
            foreach (Window window in windows) Native.ApplyDarkTitleBar(window, dark);
    }
}

// Original small line drawings; no external icon assets or fonts required.
internal static class Icons
{
    private static readonly ConcurrentDictionary<string, Geometry> CachedGeometries = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> Paths = new()
    {
        ["Capture"] = "M3,7 V3 H7 M13,3 H17 V7 M17,13 V17 H13 M7,17 H3 V13 M6,6 H14 V14 H6 Z",
        ["General"] = "M4,5 H16 M4,10 H16 M4,15 H16 M7,3 V7 M13,8 V12 M8,13 V17",
        ["Hotkeys"] = "M3,5 H17 V15 H3 Z M6,8 H6.5 M10,8 H10.5 M14,8 H14.5 M6,12 H14",
        ["Select"] = "M4,3 L15,10 L10,11 L7,16 Z M10,11 L14,17",
        ["Rectangle"] = "M3,4 H17 V16 H3 Z", ["Ellipse"] = "M17,10 A7,6 0 1 1 3,10 A7,6 0 1 1 17,10",
        ["Line"] = "M4,16 L16,4", ["Arrow"] = "M3,17 L16,4 M8,4 H16 V12",
        ["Pen"] = "M4,14 L3,17 L7,16 L17,6 L14,3 Z M12,5 L15,8",
        ["Highlighter"] = "M4,12 L11,3 L17,8 L10,16 Z M5,13 L3,17 H9",
        ["Text"] = "M3,4 H17 M10,4 V17 M7,17 H13",
        ["AlignLeft"] = "M3,4 H17 M3,8 H12 M3,12 H17 M3,16 H12",
        ["AlignCenter"] = "M3,4 H17 M6,8 H14 M3,12 H17 M6,16 H14",
        ["AlignRight"] = "M3,4 H17 M8,8 H17 M3,12 H17 M8,16 H17",
        ["IndentLess"] = "M3,4 H17 M9,8 H17 M9,12 H17 M3,16 H17 M6,7 L3,10 L6,13",
        ["IndentMore"] = "M3,4 H17 M9,8 H17 M9,12 H17 M3,16 H17 M3,7 L6,10 L3,13",
        ["Ocr"] = "M6,2 H2 V6 M14,2 H18 V6 M18,14 V18 H14 M6,18 H2 V14 M5,6 H15 M10,6 V14 M7,14 H13",
        ["Mosaic"] = "M3,3 H8 V8 H3 Z M12,3 H17 V8 H12 Z M3,12 H8 V17 H3 Z M12,12 H17 V17 H12 Z",
        ["Blur"] = "M10,2 C8,6 4,8 4,12 A6,6 0 0 0 16,12 C16,8 12,6 10,2 M7,12 A3,3 0 0 0 10,15",
        ["Crop"] = "M5,2 V15 H18 M2,5 H15 V18",
        ["Color"] = "M12,3 L17,8 M14,5 L5,14 L3,17 L6,16 L15,7",
        ["Delete"] = "M3,5 H17 M7,5 V3 H13 V5 M5,5 L6,17 H14 L15,5 M8,8 V14 M12,8 V14",
        ["Undo"] = "M3,9 L7,5 M3,9 L7,13 M3,9 H12 C18,9 18,16 12,16",
        ["Redo"] = "M17,9 L13,5 M17,9 L13,13 M17,9 H8 C2,9 2,16 8,16",
        ["Rotate"] = "M15,7 A6,6 0 1 0 16,12 M15,3 V7 H11",
        ["FlipH"] = "M10,2 V18 M7,5 L3,15 H7 Z M13,5 L17,15 H13 Z",
        ["FlipV"] = "M2,10 H18 M5,7 L15,3 V7 Z M5,13 L15,17 V13 Z",
        ["Reverse"] = "M3,6 H17 M13,2 L17,6 L13,10 M17,14 H3 M7,10 L3,14 L7,18",
        ["Pin"] = "M7,3 H13 M8,3 V8 L15,12 H5 L8,8 M10,12 V18",
        ["Pins"] = "M7,3 H13 M8,3 V8 L15,12 H5 L8,8 M10,12 V18",
        ["Copy"] = "M7,7 H17 V17 H7 Z M3,13 V3 H13",
        ["Save"] = "M3,3 H14 L17,6 V17 H3 Z M6,3 V8 H13 V3 M6,17 V12 H14 V17",
        ["Done"] = "M4,10 L8,14 L16,6", ["Cancel"] = "M5,5 L15,15 M15,5 L5,15",
        ["Edit"] = "M3,14 V17 H6 L17,6 L14,3 Z M11,6 L14,9",
        ["Scroll"] = "M4,3 H16 V17 H4 Z M10,6 V14 M7,11 L10,14 L13,11",
        ["History"] = "M3,9 A7,7 0 1 1 4,14 M3,4 V9 H8 M10,6 V10 L13,12",
        ["Pause"] = "M6,4 V16 M14,4 V16", ["Resume"] = "M6,3 L16,10 L6,17 Z",
        ["Auto"] = "M9,2 L4,11 H10 L9,18 L16,8 H10 Z",
        ["Correction"] = "M3,6 H17 M3,14 H17 M7,3 V9 M13,11 V17",
        ["Ignore"] = "M4,10 H16 M12,6 L16,10 L12,14",
        ["Minus"] = "M4,10 H16", ["Plus"] = "M4,10 H16 M10,4 V16",
        ["Previous"] = "M13,4 L7,10 L13,16", ["Next"] = "M7,4 L13,10 L7,16",
        ["Fit"] = "M3,7 V3 H7 M13,3 H17 V7 M17,13 V17 H13 M7,17 H3 V13",
        ["Folder"] = "M3,5 H8 L10,7 H17 V16 H3 Z", ["Info"] = "M17,10 A7,7 0 1 1 3,10 A7,7 0 1 1 17,10 M10,9 V14 M10,6 V6.2",
    };
    public static string Get(string key) => Paths.GetValueOrDefault(key, Paths["Capture"]);
    public static Geometry Geometry(string key) => CachedGeometries.GetOrAdd(key, static k =>
    {
        var geometry = System.Windows.Media.Geometry.Parse(Get(k));
        geometry.Freeze();
        return geometry;
    });
}
