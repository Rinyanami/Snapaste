using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Threading;
using Snapaste.Core;
using Snapaste.Platform;
namespace Snapaste.Capture;

public readonly record struct DetectionCandidate(Rect Bounds, nint Handle, bool IsWindow);

public sealed class WindowDetector : IDisposable
{
    private static readonly Lazy<AutomationWorker> Worker = new(() => new());
    private readonly Dispatcher dispatcher;
    private readonly WindowSnapshot[] windows;
    private Query? request;
    private WindowSnapshot? resolvedWindow;
    private DetectionCandidate[]? resolved;
    private Point resolvedPixel;
    private bool disposed;

    public WindowDetector() : this(Native.Desktop, null) { }

    public WindowDetector(Rect screen, Dispatcher? ownerDispatcher = null)
    {
        dispatcher = ownerDispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        windows = CaptureWindows(screen);
    }

    // The callback is posted to the dispatcher that created this detector.
    public event Action? CandidatesChanged;

    public (Rect Bounds, nint Handle) Find(Point p, bool elements)
    {
        var candidates = Candidates(p, elements);
        return candidates.Count == 0 ? (Rect.Empty, 0) : (candidates[0].Bounds, candidates[0].Handle);
    }

    // Smallest element first, then its enclosing elements and the captured window.
    public IReadOnlyList<DetectionCandidate> Candidates(Point pointer, bool elements)
    {
        dispatcher.VerifyAccess();
        if (disposed) return [];
        var pixel = new Point(Math.Floor(pointer.X), Math.Floor(pointer.Y));
        var window = windows.FirstOrDefault(candidate => ContainsPixel(candidate.VisibleBounds, pixel));
        if (!elements || window == null)
        {
            Reset();
            return window?.Fallback ?? [];
        }

        if (resolvedWindow != window) ClearResolution();
        var candidates = AtPixel(window, pixel);
        if (request?.Window == window && request.Pixel == pixel) return candidates;

        CancelRequest();
        // Keep the known bounds while a new result is pending, and avoid requerying a
        // resolved element for minor cursor movement entirely inside its pixel bounds.
        if (resolved is { Length: > 1 } && candidates[0] == resolved[0] &&
            (pixel - resolvedPixel).LengthSquared <= 4) return candidates;

        request = new Query(this, window, pixel);
        try { Worker.Value.Enqueue(request); }
        catch (Exception e) when (e is InvalidOperationException or ThreadStateException) { Log.Error("UI Automation worker", e); }
        return candidates;
    }

    private IReadOnlyList<DetectionCandidate> AtPixel(WindowSnapshot window, Point pixel)
    {
        if (resolved == null) return window.Fallback;
        for (int i = 0; i < resolved.Length - 1; i++)
        {
            if (!ContainsPixel(resolved[i].Bounds, pixel)) continue;
            return i == 0 ? resolved : new ArraySegment<DetectionCandidate>(resolved, i, resolved.Length - i);
        }
        return window.Fallback;
    }

    private void CancelRequest()
    {
        if (request != null && Worker.IsValueCreated) Worker.Value.Cancel(request);
        request = null;
    }

    private void ClearResolution()
    {
        resolvedWindow = null;
        resolved = null;
    }

    public void Reset()
    {
        dispatcher.VerifyAccess();
        CancelRequest();
        ClearResolution();
    }

    public void Dispose()
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        Reset();
        disposed = true;
        CandidatesChanged = null;
    }

    private void Receive(Query query, DetectionCandidate[] candidates)
    {
        if (disposed || query != request || query.IsCancelled) return;
        resolvedWindow = query.Window;
        resolvedPixel = query.Pixel;
        resolved = candidates;
        CandidatesChanged?.Invoke();
    }

    private sealed class WindowSnapshot(nint handle, uint thread, uint process, Rect bounds, Rect visibleBounds)
    {
        public nint Handle { get; } = handle;
        public Rect VisibleBounds { get; } = visibleBounds;
        public DetectionCandidate[] Fallback { get; } = new[] { new DetectionCandidate(visibleBounds, handle, true) };

        public bool IsCurrent()
            => Native.IsWindowVisible(Handle) && !Native.IsIconic(Handle) && !IsCloaked(Handle) &&
               Native.GetWindowThreadProcessId(Handle, out uint currentProcess) == thread &&
               currentProcess == process && Native.Bounds(Handle) == bounds;
    }

    private static WindowSnapshot[] CaptureWindows(Rect screen)
    {
        var snapshots = new List<WindowSnapshot>();
        var className = new System.Text.StringBuilder(128);
        Native.EnumWindows((handle, _) =>
        {
            if (!Native.IsWindowVisible(handle) || Native.IsIconic(handle) || IsCloaked(handle)) return true;
            var bounds = Native.Bounds(handle);
            var visible = CoverPixels(bounds, screen);
            if (visible.IsEmpty) return true;
            Native.GetClassName(handle, className, className.Capacity);
            if (className.ToString() is "Progman" or "WorkerW") return true;
            uint thread = Native.GetWindowThreadProcessId(handle, out uint process);
            if (thread != 0) snapshots.Add(new WindowSnapshot(handle, thread, process, bounds, visible));
            return true;
        }, 0);
        return snapshots.ToArray();
    }

    private static bool IsCloaked(nint handle)
        => Native.DwmGetWindowAttribute(handle, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static bool HasBounds(Rect bounds)
        => !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0 &&
           double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top) &&
           double.IsFinite(bounds.Right) && double.IsFinite(bounds.Bottom);

    private static Rect CoverPixels(Rect bounds, Rect clip)
    {
        if (!HasBounds(bounds)) return Rect.Empty;
        var pixels = new Rect(new Point(Math.Floor(bounds.Left), Math.Floor(bounds.Top)),
                              new Point(Math.Ceiling(bounds.Right), Math.Ceiling(bounds.Bottom)));
        pixels.Intersect(clip);
        return pixels.Width >= 2 && pixels.Height >= 2 ? pixels : Rect.Empty;
    }

    private static bool ContainsPixel(Rect bounds, Point pixel)
        => pixel.X >= bounds.Left && pixel.X < bounds.Right && pixel.Y >= bounds.Top && pixel.Y < bounds.Bottom;

    private sealed class Query(WindowDetector detector, WindowSnapshot window, Point pixel)
    {
        private readonly WeakReference<WindowDetector> owner = new(detector);
        private readonly Dispatcher targetDispatcher = detector.dispatcher;
        private volatile bool cancelled;
        public WindowSnapshot Window { get; } = window;
        public Point Pixel { get; } = pixel;
        public long QueuedAt { get; } = Stopwatch.GetTimestamp();
        public bool IsCancelled => cancelled;
        public void Cancel() => cancelled = true;

        public void Publish(DetectionCandidate[] candidates)
        {
            if (cancelled || targetDispatcher.HasShutdownStarted) return;
            _ = targetDispatcher.InvokeAsync(() =>
            {
                if (owner.TryGetTarget(out var detector)) detector.Receive(this, candidates);
            }, DispatcherPriority.Background);
        }
    }

    private sealed class AutomationWorker
    {
        private readonly object gate = new();
        private readonly List<Query> pending = [];
        private readonly HashSet<nint> running = [];

        public AutomationWorker()
        {
            // A blocked provider leaves a second worker available for another window.
            for (int i = 0; i < 2; i++)
            {
                var thread = new Thread(Run) { IsBackground = true, Name = "Snapaste element detection" };
                thread.SetApartmentState(ApartmentState.MTA);
                thread.Start();
            }
        }

        public void Enqueue(Query query)
        {
            lock (gate)
            {
                pending.Add(query);
                Monitor.PulseAll(gate);
            }
        }

        public void Cancel(Query query)
        {
            query.Cancel();
            lock (gate) pending.Remove(query);
        }

        private Query TakeNext()
        {
            lock (gate)
            {
                while (true)
                {
                    int index = pending.FindLastIndex(query => !running.Contains(query.Window.Handle));
                    if (index < 0)
                    {
                        Monitor.Wait(gate);
                        continue;
                    }

                    var query = pending[index];
                    // Coalesce quick pointer moves before making a cross-process request.
                    int delay = (int)Math.Ceiling(30 - Stopwatch.GetElapsedTime(query.QueuedAt).TotalMilliseconds);
                    if (delay > 0)
                    {
                        Monitor.Wait(gate, delay);
                        continue;
                    }
                    pending.RemoveAt(index);
                    running.Add(query.Window.Handle);
                    return query;
                }
            }
        }

        private void Run()
        {
            while (true)
            {
                var query = TakeNext();
                var candidates = query.Window.Fallback;
                try
                {
                    if (!query.IsCancelled) candidates = FindElements(query);
                }
                catch (Exception e) when (e is ElementNotAvailableException or COMException or InvalidOperationException or ArgumentException) { }
                catch (Exception e) when (e is not OutOfMemoryException) { Log.Error("UI Automation provider", e); }
                finally
                {
                    lock (gate)
                    {
                        running.Remove(query.Window.Handle);
                        Monitor.PulseAll(gate);
                    }
                }
                query.Publish(candidates);
            }
        }
    }

    private static DetectionCandidate[] FindElements(Query query)
    {
        var window = query.Window;
        if (!window.IsCurrent()) return window.Fallback;

        // Hit testing the live desktop would return the screenshot overlay.
        // Walk only the captured window, using cached geometry on each navigation call.
        var root = AutomationElement.FromHandle(window.Handle);
        var walker = TreeWalker.RawViewWalker;
        var cache = new CacheRequest { TreeScope = TreeScope.Element, TreeFilter = Automation.RawViewCondition };
        cache.Add(AutomationElement.BoundingRectangleProperty);
        cache.Add(AutomationElement.IsOffscreenProperty);
        long started = Stopwatch.GetTimestamp();
        var bounds = FindHierarchy(root, query.Pixel, window.VisibleBounds,
            element => walker.GetFirstChild(element, cache),
            element => walker.GetNextSibling(element, cache),
            element => (element.Cached.BoundingRectangle, element.Cached.IsOffscreen),
            () => query.IsCancelled || Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 300);

        if (query.IsCancelled || bounds.Length == 0 || !window.IsCurrent()) return window.Fallback;
        var result = new DetectionCandidate[bounds.Length + 1];
        for (int i = 0; i < bounds.Length; i++) result[i] = new(bounds[i], window.Handle, false);
        result[^1] = window.Fallback[0];
        return result;
    }

    private static Rect[] FindHierarchy<T>(
        T root, Point point, Rect windowBounds, Func<T, T?> firstChild, Func<T, T?> nextSibling,
        Func<T, (Rect Bounds, bool IsOffscreen)> geometry, Func<bool> stop) where T : class
    {
        const int maximumDepth = 24, maximumNodes = 480;
        var path = new List<Rect>();
        Rect[] best = [];
        int visited = 0;
        double bestArea = double.PositiveInfinity;

        bool CanContinue() => visited < maximumNodes && !stop();

        void Visit(T parent, Rect clip, int depth)
        {
            if (depth >= maximumDepth || !CanContinue()) return;
            List<(T Node, Rect Covered, bool Narrows, double Area, double Distance)>? candidates = null;
            var child = firstChild(parent);
            while (child != null && CanContinue())
            {
                visited++;
                var (bounds, offscreen) = geometry(child);
                if (!offscreen)
                {
                    // Providers can return a layout container without a rectangle.
                    var covered = HasBounds(bounds) ? CoverPixels(bounds, clip) : clip;
                    if (ContainsPixel(covered, point))
                    {
                        bool narrows = covered != clip;
                        double area = covered.Width * covered.Height;
                        var center = new Point(covered.X + covered.Width * 0.5, covered.Y + covered.Height * 0.5);
                        double distance = (center - point).LengthSquared;
                        (candidates ??= []).Add((child, covered, narrows, area, distance));
                    }
                }
                if (!CanContinue()) break;
                child = nextSibling(child);
            }
            if (candidates == null) return;
            if (candidates.Count > 1)
            {
                candidates.Sort(static (a, b) =>
                {
                    int narrowCmp = b.Narrows.CompareTo(a.Narrows);
                    if (narrowCmp != 0) return narrowCmp;
                    int areaCmp = a.Area.CompareTo(b.Area);
                    return areaCmp != 0 ? areaCmp : a.Distance.CompareTo(b.Distance);
                });
            }
            foreach (var item in candidates)
            {
                if (item.Narrows)
                {
                    path.Add(item.Covered);
                    if (item.Area < bestArea || item.Area == bestArea && path.Count > best.Length)
                    {
                        bestArea = item.Area;
                        best = path.ToArray();
                    }
                }
                if (CanContinue()) Visit(item.Node, item.Covered, depth + 1);
                if (item.Narrows) path.RemoveAt(path.Count - 1);
                if (!CanContinue()) break;
            }
        }

        Visit(root, windowBounds, 0);
        Array.Reverse(best);
        return best;
    }
}
