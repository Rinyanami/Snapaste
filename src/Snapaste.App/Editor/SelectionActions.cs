using Snapaste.UI;
namespace Snapaste.Editor;

// Shared by the desktop overlay and the history/long-image editor.
public sealed class SelectionActions : StackPanel
{
    private readonly EditorSurface surface;
    private readonly Button reverse;
    public SelectionActions(EditorSurface surface, Action? beforeChange = null)
    {
        this.surface = surface; Orientation = Orientation.Horizontal; Margin = new Thickness(6, 0, 0, 0);
        foreach (var (icon, label, action) in new (string, string, Action)[]
        {
            ("Rotate", "Rotate", () => surface.RotateSelection(90)),
            ("FlipH", "FlipH", () => surface.FlipSelection(true)),
            ("FlipV", "FlipV", () => surface.FlipSelection(false))
        })
        {
            var button = Ui.Tool(icon, Strings.T(label), () => { beforeChange?.Invoke(); action(); }); Compact(button); Children.Add(button);
        }
        reverse = Ui.Tool("Reverse", Strings.T("ReverseDirection"), () => { beforeChange?.Invoke(); surface.ReverseDirection(); }); Compact(reverse); Children.Add(reverse); Refresh();
    }
    private static void Compact(Button b) { b.Width = b.Height = b.MinHeight = 26; b.Padding = new Thickness(5); b.Margin = new Thickness(1); }
    public void Refresh(bool enabled = true)
    {
        Visibility = enabled && surface.HasSelection ? Visibility.Visible : Visibility.Collapsed;
        reverse.Visibility = surface.Selection?.Kind is Tool.Line or Tool.Arrow ? Visibility.Visible : Visibility.Collapsed;
    }
}
