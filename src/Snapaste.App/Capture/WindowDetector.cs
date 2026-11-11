using System.Collections.Concurrent;
using System.Windows.Automation;
using Snapaste.Core;
using Snapaste.Platform;
namespace Snapaste.Capture;

public sealed class WindowDetector
{
    private sealed record Query(WindowDetector Detector, nint Target, Point Point);
    private sealed record ElementResult(nint Target, Point Point, Rect Bounds);
    private static readonly Lazy<BlockingCollection<Query>> Requests = new(StartWorker);
    private static int busy;
    private ElementResult? element;

    public (Rect Bounds, nint Handle) Find(Point p, bool elements)
    {
        nint target = 0;
        Rect found = Rect.Empty;
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
            Native.GetWindowThreadProcessId(h, out var pid);
            if (pid == Environment.ProcessId) return true;
            Native.DwmGetWindowAttribute(h, 14, out int cloaked, 4);
            if (cloaked != 0) return true;
            var r = Native.Bounds(h);
            if (r.Width < 2 || r.Height < 2 || !r.Contains(p)) return true;
            var name = new System.Text.StringBuilder(128);
            Native.GetClassName(h, name, name.Capacity);
            if (name.ToString() is "Progman" or "WorkerW") return true;
            target = h;
            found = r;
            return false;
        }, 0);

        if (!elements || target == 0) return (found, target);
        var previous = Volatile.Read(ref element);
        if ((previous == null || previous.Target != target || (p - previous.Point).LengthSquared > 16) &&
            Interlocked.CompareExchange(ref busy, 1, 0) == 0)
        {
            try { Requests.Value.Add(new(this, target, p)); }
            catch (Exception e) when (e is InvalidOperationException or ThreadStateException)
            {
                Volatile.Write(ref busy, 0);
                Log.Error("UI Automation worker", e);
            }
        }
        return previous != null && previous.Target == target && previous.Bounds.Contains(p) && found.Contains(previous.Bounds)
            ? (previous.Bounds, target) : (found, target);
    }

    private static BlockingCollection<Query> StartWorker()
    {
        var requests = new BlockingCollection<Query>();
        // A hung provider can occupy one MTA thread for the process; window detection stays available.
        var thread = new Thread(() =>
        {
            foreach (var query in requests.GetConsumingEnumerable())
            {
                Rect result = Rect.Empty;
                try { result = FindElement(query.Target, query.Point); }
                catch (Exception e) when (e is ElementNotAvailableException or System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException) { }
                catch (Exception e) when (e is not OutOfMemoryException) { Log.Error("UI Automation provider", e); }
                finally
                {
                    Volatile.Write(ref query.Detector.element, new ElementResult(query.Target, query.Point, result));
                    Volatile.Write(ref busy, 0);
                }
            }
        }) { IsBackground = true, Name = "Snapaste element detection" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return requests;
    }

    private static Rect FindElement(nint target, Point point)
    {
        // The overlay covers the target, so search the target's own tree.
        var current = AutomationElement.FromHandle(target);
        var walker = TreeWalker.RawViewWalker;
        var result = Rect.Empty;
        int visited = 0;
        for (int depth = 0; depth < 15 && visited < 350; depth++)
        {
            AutomationElement? inside = null;
            for (var child = walker.GetFirstChild(current); child != null && visited++ < 350; child = walker.GetNextSibling(child))
            {
                var bounds = child.Current.BoundingRectangle;
                if (!bounds.IsEmpty && bounds.Contains(point) && bounds.Width > 3 && bounds.Height > 3)
                {
                    inside = child;
                    result = bounds;
                    break;
                }
            }
            if (inside == null) break;
            current = inside;
        }
        return result;
    }
}
