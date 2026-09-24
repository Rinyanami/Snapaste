using System.Windows.Automation;
using Snapaste.Platform;
namespace Snapaste.Capture;

public sealed class WindowDetector
{
    private bool querying;
    private Rect element = Rect.Empty;
    private Point lastPoint;
    public (Rect Bounds, nint Handle) Find(Point p, bool elements)
    {
        nint target = 0; Rect found = Rect.Empty;
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
            Native.GetWindowThreadProcessId(h, out var pid);
            if (pid == Environment.ProcessId) return true;
            Native.DwmGetWindowAttribute(h, 14, out int cloaked, 4);
            if (cloaked != 0) return true;
            var r = Native.Bounds(h);
            if (r.Width < 2 || r.Height < 2 || !r.Contains(p) || r == Native.Desktop) return true;
            target = h; found = r; return false;
        }, 0);
        if (elements && !querying && (p - lastPoint).Length > 4)
        {
            querying = true; lastPoint = p;
            // UIA providers may hang. At most one background query is outstanding.
            _ = Task.Run(() =>
            {
                Rect result = Rect.Empty;
                try
                {
                    // Overlay is above the target. Search within the target tree instead of FromPoint.
                    if (target != 0)
                    {
                        var root = AutomationElement.FromHandle(target); var walker = TreeWalker.RawViewWalker;
                        var current = root; int visited = 0;
                        for (int depth = 0; depth < 15; depth++)
                        {
                            AutomationElement? inside = null;
                            for (var child = walker.GetFirstChild(current); child != null && visited++ < 350; child = walker.GetNextSibling(child))
                            {
                                var r = child.Current.BoundingRectangle;
                                if (!r.IsEmpty && r.Contains(p) && r.Width > 3 && r.Height > 3) { inside = child; result = r; break; }
                            }
                            if (inside == null) break;
                            current = inside;
                        }
                    }
                }
                catch (Exception e) when (e is ElementNotAvailableException or System.Runtime.InteropServices.COMException or InvalidOperationException) { }
                Application.Current.Dispatcher.BeginInvoke(() => { element = result; querying = false; });
            });
        }
        return (elements && !element.IsEmpty && element.Contains(p) && found.Contains(element) ? element : found, target);
    }
}
