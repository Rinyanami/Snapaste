using System.Diagnostics;
using System.Text.Json;
using Snapaste.Capture;
using Snapaste.Core;
using Snapaste.Platform;
namespace Snapaste.ScrollCapture;

public enum ScrollState { Idle, SelectingRegion, Ready, WaitingForScroll, Scrolling, WaitingForStableFrame, Matching, Stitching, Paused, Completed, Failed, Cancelled }

public sealed class ScrollSession : IDisposable
{
    private const int PollMilliseconds = 150;
    private const int AutomaticStartDelay = 2500;
    private const double MotionThreshold = .004;
    private const string ManualInstructions = "向下滚动，每次保留约 1/3 重叠。停稳后会自动拼接。";
    private const string LimitMessage = "已达到捕获帧数上限。可以撤销上一段，或完成编辑。";

    private readonly ICaptureProvider capture;
    private readonly Settings settings;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource cancel = new();
    private readonly CancellationToken cancellation;
    private readonly string folder = Path.Combine(Paths.Sessions, Guid.NewGuid().ToString("N"));
    private readonly nint target;
    private readonly uint targetDpi;
    private readonly uint targetProcess;
    private readonly Rect targetBounds;
    private readonly Rect desktopBounds;
    private readonly List<ScrollFrame> frames = [];
    private FrameSample? reference, undoReference, observed, ignoredSample;
    private BitmapSource? pending;
    private FrameSample? pendingSample;
    private ScrollFrame? pendingFrame;
    private long stableSince, nextWheel, automaticReady;
    private long? unstableSince;
    private int wheelAttempts, epoch, fileIndex, totalAddedRows;
    private int wheelUnits = 1;
    private int appliedTopOverride = -1, appliedBottomOverride = -1;
    private bool disposed;
    private string lastMessage = "";

    private bool Terminal => State is ScrollState.Completed or ScrollState.Cancelled;
    public ScrollState State { get; private set; } = ScrollState.Idle;
    public Rect Region { get; }
    public nint ControlHandle { get; set; }
    internal bool ControlExcluded { get; set; }
    public bool Automatic { get; private set; }
    internal bool CanAutomate => target != 0;
    internal bool AtFrameLimit => frames.Count >= Math.Max(1, settings.MaxScrollFrames);
    internal bool ShowPreview => settings.ScrollPreview;
    public int FixedTop { get; private set; }
    public int FixedBottom { get; private set; }
    public int TopOverride { get; set; } = -1;
    public int BottomOverride { get; set; } = -1;
    public IReadOnlyList<ScrollFrame> Frames => frames;
    public BitmapSource? Pending => pending;
    public MatchMetadata? LastMatch { get; private set; }
    public event Action<string>? Updated;
    public event Action<ImageDocument>? Finished;

    public ScrollSession(Rect region, nint target, Settings settings, ICaptureProvider capture)
    {
        Region = region;
        this.target = target;
        targetBounds = target == 0 ? Rect.Empty : Native.Bounds(target);
        targetDpi = target == 0 ? 0 : Native.GetDpiForWindow(target);
        if (target != 0) Native.GetWindowThreadProcessId(target, out targetProcess);
        desktopBounds = Native.Desktop;
        this.settings = settings;
        this.capture = capture;
        cancellation = cancel.Token;
    }

    private void SetState(ScrollState state, string message)
    {
        if (State == state && lastMessage == message) return;
        State = state;
        lastMessage = message;
        Updated?.Invoke(message);
    }

    public async Task Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (State != ScrollState.Idle) throw new InvalidOperationException("滚动会话不能重复启动。");
        SetState(ScrollState.Ready, "请保持页面静止，正在获取第一帧。");
        try
        {
            await Task.Delay(350, cancellation);
            while (!cancellation.IsCancellationRequested)
            {
                if (State != ScrollState.Paused)
                {
                    try { await ObserveFrame(); }
                    catch (DesktopUnavailableException e) { Pause(e.Message); }
                }
                await Task.Delay(PollMilliseconds, cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception e)
        {
            Log.Error("Scroll session", e);
            if (!Terminal)
            {
                Automatic = false;
                SetState(ScrollState.Failed, "滚动截图已停止：" + e.Message);
            }
        }
        finally
        {
            if (State == ScrollState.Cancelled) await Task.Run(DeleteSessionFiles);
        }
    }

    private async Task ObserveFrame()
    {
        if (!ValidateTarget()) return;
        long now = clock.ElapsedMilliseconds;
        if (Automatic && now >= automaticReady && !CanSendWheel()) return;

        int generation = epoch;
        if (reference == null && frames.Count > 0)
        {
            var lastFrame = frames[^1];
            var restored = await Task.Run(() => new FrameSample(lastFrame.Bitmap), cancellation);
            if (generation != epoch) return;
            reference = restored;
        }

        var (bitmap, sample) = await Task.Run(() =>
        {
            var image = capture.Capture(Region);
            if (image.PixelWidth != (int)Region.Width || image.PixelHeight != (int)Region.Height)
                throw new InvalidOperationException("截取画面的尺寸已改变。");
            image.Freeze();
            return (image, new FrameSample(image));
        }, cancellation);
        if (generation != epoch || !ValidateTarget()) return;

        now = clock.ElapsedMilliseconds;
        int stableMilliseconds = Math.Max(PollMilliseconds, settings.StableMilliseconds);
        var anchor = reference;
        // A change confined to a narrow area must itself settle before matching.
        bool localChange = anchor != null && sample.Difference(anchor) <= MotionThreshold;
        if (observed == null || sample.Difference(observed, localChange) > MotionThreshold)
        {
            observed = sample;
            stableSince = now;
            unstableSince ??= now;
            if (now - unstableSince >= Math.Max(6000L, stableMilliseconds * 3L))
            {
                Pause("画面持续变化。请等页面停稳后继续，或完成已捕获的内容。");
            }
            else if (reference != null)
            {
                SetState(ScrollState.WaitingForStableFrame, "等待画面停稳。");
            }
            return;
        }
        if (now - stableSince < stableMilliseconds) return;
        unstableSince = null;

        if (anchor == null)
        {
            var first = await WriteFrame(bitmap, sample, generation);
            if (first == null) return;
            Commit(first, sample, null, false);
            if (!Terminal && !AtFrameLimit && settings.AutoScrollDefault && CanAutomate) SetAutomatic(true);
            return;
        }

        if (sample.Difference(anchor, includeLocalChanges: true) <= MotionThreshold)
        {
            ignoredSample = null;
            if (State != ScrollState.WaitingForScroll) SetState(ScrollState.WaitingForScroll, Automatic ? "等待页面滚动。" : ManualInstructions);
            if (Automatic && now >= Math.Max(nextWheel, automaticReady)) TryScroll(now);
            return;
        }
        if (ignoredSample != null && sample.Difference(ignoredSample, includeLocalChanges: true) <= MotionThreshold)
        {
            SetState(ScrollState.WaitingForScroll, "已忽略这帧。请滚回上一段附近，保留重叠后继续。");
            return;
        }

        int top = TopOverride, bottom = BottomOverride;
        int matchTop = top >= 0 || frames.Count == 1 || appliedTopOverride >= 0 ? top : FixedTop;
        int matchBottom = bottom >= 0 || frames.Count == 1 || appliedBottomOverride >= 0 ? bottom : FixedBottom;
        SetState(ScrollState.Matching, "正在分析重叠区域。");
        var match = await Task.Run(() => ScrollMatcher.Match(anchor, sample, settings.MatchSensitivity, cancellation, matchTop, matchBottom), cancellation);
        if (generation != epoch || top != TopOverride || bottom != BottomOverride) return;

        bool fits = CanStitch(match.Offset, match.FixedTop, match.FixedBottom);
        // Keep the crop used by accepted segments. Newly fixed rows can already
        // be in the preceding frame, where they first appeared as new content.
        bool fixedBandChanged = match.Confidence != Confidence.Low &&
            (top < 0 && matchTop >= 0 && ScrollMatcher.FixedBandExpanded(anchor, sample, matchTop, match.Offset, bottom: false) ||
             bottom < 0 && matchBottom >= 0 && ScrollMatcher.FixedBandExpanded(anchor, sample, matchBottom, match.Offset, bottom: true));
        var stored = await WriteFrame(bitmap, sample, generation);
        if (stored == null) return;
        if (top != TopOverride || bottom != BottomOverride)
        {
            DeleteFrameLater(stored);
            return;
        }
        if (match.Confidence == Confidence.Low || !fits || fixedBandChanged)
        {
            LastMatch = match with { Confidence = Confidence.Low };
            pending = bitmap;
            pendingSample = sample;
            pendingFrame = stored;
            string message = fixedBandChanged
                ? "检测到固定栏变高。请先撤销上一段，检查是否拼入了重复的固定栏。"
                : fits || match.Offset <= 0
                    ? UI.Strings.T("LowMatch")
                    : "固定栏与拼接位置需要确认。请检查固定栏高度，或忽略这一帧后滚回上一段附近。";
            Pause(message);
            Log.Error($"Scroll low match: offset={match.Offset} score={match.Score:F3} uniqueness={match.Uniqueness:F3} texture={match.Texture:F3} second={match.SecondBestScore:F3}");
        }
        else Commit(stored, sample, match, false);
    }

    private bool ValidateTarget()
    {
        if (Native.Desktop != desktopBounds || target != 0 && (!Native.IsWindow(target) || !Native.IsWindowVisible(target) ||
            Native.IsIconic(target) || Native.Bounds(target) != targetBounds || Native.GetDpiForWindow(target) != targetDpi ||
            Native.GetWindowThreadProcessId(target, out uint process) == 0 || process != targetProcess))
        {
            Pause("目标窗口或显示器已改变。请恢复原位置，或完成已捕获的内容。");
            return false;
        }
        if (ControlHandle != 0 && !ControlExcluded && Native.Bounds(ControlHandle).IntersectsWith(Region))
        {
            Pause("请将控制面板移出截图选区，然后继续。");
            return false;
        }
        if (target == 0) return true;
        ReadOnlySpan<double> grid = [.06, .25, .5, .75, .94];
        foreach (double y in grid) foreach (double x in grid)
        {
            var point = new Native.POINT((int)(Region.X + Region.Width * x), (int)(Region.Y + Region.Height * y));
            var window = Native.GetAncestor(Native.WindowFromPoint(point), 2);
            if (window == target || (ControlExcluded && window == ControlHandle)) continue;
            Pause("目标窗口被遮挡。请恢复原画面后继续，或完成已捕获的内容。");
            return false;
        }
        return true;
    }

    private bool CanSendWheel()
    {
        if (!Native.GetCursorPos(out var cursor) || cursor.X < Region.Left || cursor.X >= Region.Right ||
            cursor.Y < Region.Top || cursor.Y >= Region.Bottom ||
            Native.GetAncestor(Native.WindowFromPoint(cursor), 2) != target ||
            Native.GetAncestor(Native.GetForegroundWindow(), 2) != target)
        {
            Pause("自动滚动已暂停。重新启用后，将鼠标放在选区内，并保持目标窗口在前台。");
            return false;
        }
        return true;
    }

    private void TryScroll(long now)
    {
        if (wheelAttempts >= 4)
        {
            Pause("连续滚动后未检测到位移。页面可能已到底或仍在加载。");
            return;
        }
        if (!CanSendWheel()) return;
        // A held modifier can turn a wheel into a zoom, horizontal scroll or shortcut.
        ReadOnlySpan<int> keys = [0x10, 0x11, 0x12, 0x5B, 0x5C, 1, 2, 4, 5, 6];
        foreach (int key in keys)
        {
            if ((Native.GetAsyncKeyState(key) & 0x8000) == 0) continue;
            Pause("自动滚动已暂停。请松开鼠标按键和键盘修饰键后重新启用。");
            return;
        }
        try
        {
            DesktopSession.EnsureAvailable();
            Native.Wheel(-wheelUnits * 120);
        }
        catch (InvalidOperationException e)
        {
            Pause(e.Message);
            return;
        }
        wheelAttempts++;
        nextWheel = now + Math.Max(1600L, (long)settings.AutoScrollDelay + settings.StableMilliseconds);
        SetState(ScrollState.WaitingForScroll, "等待页面响应。");
    }

    private bool CanStitch(int offset, int top, int bottom)
    {
        long body = (long)Region.Height - top - bottom;
        return top >= 0 && bottom >= 0 && offset > 0 && offset < body &&
            (long)Region.Height + totalAddedRows + offset <= int.MaxValue &&
            frames.Skip(1).All(frame => frame.MatchMetadata!.Offset < body);
    }

    internal (int Top, int Bottom, int Maximum) ManualBounds()
    {
        int top = TopOverride >= 0 ? TopOverride : LastMatch?.FixedTop ?? FixedTop;
        int bottom = BottomOverride >= 0 ? BottomOverride : LastMatch?.FixedBottom ?? FixedBottom;
        int maximum = (int)Math.Clamp((long)Region.Height - top - bottom - 1, 0, int.MaxValue);
        if (maximum < 1 || frames.Skip(1).Any(frame => frame.MatchMetadata!.Offset > maximum)) maximum = 0;
        return (top, bottom, maximum);
    }

    private async Task<ScrollFrame?> WriteFrame(BitmapSource bitmap, FrameSample sample, int generation)
    {
        int index = frames.Count;
        string path = Path.Combine(folder, $"frame-{fileIndex++:D5}.png");
        SetState(ScrollState.Stitching, index == 0 ? "正在保存第一帧。" : "正在保存当前帧。");
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(folder);
                ImageDocument.SaveBitmap(bitmap, path);
            }, cancellation);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _ = Task.Run(() => DeleteFile(path));
            Log.Error("Scroll frame write", e);
            if (generation == epoch) Pause("无法写入截图缓存。请检查磁盘空间后继续，或完成已捕获的内容。");
            return null;
        }
        if (generation != epoch)
        {
            _ = Task.Run(() => DeleteFile(path));
            return null;
        }
        return new ScrollFrame { BitmapPath = path, CaptureRect = Region, FrameIndex = index, PerceptualHash = sample.Hash };
    }

    private void Commit(ScrollFrame frame, FrameSample sample, MatchMetadata? match, bool manual)
    {
        LastMatch = match;
        if (match != null)
        {
            FixedTop = match.FixedTop;
            FixedBottom = match.FixedBottom;
            appliedTopOverride = TopOverride;
            appliedBottomOverride = BottomOverride;
            totalAddedRows += match.Offset;
        }
        frame.MatchMetadata = match;
        frame.StitchMetadata = new(match?.Offset ?? (int)Region.Height, (int)Region.Height + totalAddedRows, manual);
        frames.Add(frame);

        undoReference = reference;
        reference = observed = sample;
        ignoredSample = null;
        pending = null;
        pendingSample = null;
        pendingFrame = null;
        if (Automatic && wheelAttempts > 0 && match != null)
        {
            double targetOffset = ((int)Region.Height - FixedTop - FixedBottom) * .35;
            int units = (int)(wheelUnits * targetOffset / match.Offset);
            wheelUnits = Math.Clamp(units, 1, Math.Min(wheelUnits + 1, 5));
        }
        wheelAttempts = 0;
        stableSince = clock.ElapsedMilliseconds;
        unstableSince = null;
        nextWheel = stableSince + settings.AutoScrollDelay;

        SaveManifest(AtFrameLimit ? ScrollState.Paused : ScrollState.WaitingForScroll);
        if (AtFrameLimit) Pause(LimitMessage);
        else SetState(ScrollState.WaitingForScroll, Automatic ? "自动滚动中。移出选区或切换窗口会暂停。" : ManualInstructions);
    }

    private void SaveManifest(ScrollState state)
    {
        try
        {
            AtomicFile.Write(Path.Combine(folder, "session.json"), JsonSerializer.Serialize(new
            {
                Region, State = state, FixedTop, FixedBottom,
                Frames = frames.Select(f => new { f.BitmapPath, f.Timestamp, f.CaptureRect, f.FrameIndex, f.PerceptualHash, f.MatchMetadata, f.StitchMetadata })
            }, Settings.Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error("Scroll manifest", e); }
    }

    public ImageDocument BuildImage()
    {
        if (frames.Count == 0) throw new InvalidOperationException("尚未获取第一帧。");
        int height = (int)Region.Height, end = height - FixedBottom;
        var segments = new List<ImageSegment>(frames.Count + 1) { new(frames[0].BitmapPath, 0, end, 0) };
        foreach (var frame in frames.Skip(1))
        {
            int rows = frame.MatchMetadata!.Offset;
            segments.Add(new(frame.BitmapPath, height - FixedBottom - rows, rows, end));
            end += rows;
        }
        if (FixedBottom > 0) segments.Add(new(frames[^1].BitmapPath, height - FixedBottom, FixedBottom, end));
        return new ImageDocument((int)Region.Width, segments);
    }

    public void Pause(string message = "已暂停。")
    {
        if (Terminal || State == ScrollState.Failed) return;
        epoch++;
        Automatic = false;
        SetState(ScrollState.Paused, message);
    }

    public void Resume()
    {
        if (Terminal || State is ScrollState.Idle or ScrollState.Failed) return;
        if (pending != null)
        {
            Updated?.Invoke("请先手动对齐或忽略待确认的帧。");
            return;
        }
        if (AtFrameLimit) { Pause(LimitMessage); return; }
        epoch++;
        observed = null;
        unstableSince = null;
        SetState(frames.Count == 0 ? ScrollState.Ready : ScrollState.WaitingForScroll, frames.Count == 0 ? "请保持页面静止，正在获取第一帧。" : ManualInstructions);
    }

    public void SetAutomatic(bool enabled)
    {
        if (Terminal || State is ScrollState.Idle or ScrollState.Failed) return;
        if (!enabled)
        {
            Automatic = false;
            if (State == ScrollState.WaitingForScroll) SetState(State, ManualInstructions);
            else Updated?.Invoke(lastMessage);
            return;
        }
        if (pending != null) { Updated?.Invoke("请先处理待确认帧。"); return; }
        if (AtFrameLimit) { Pause(LimitMessage); return; }
        if (!CanAutomate) { Updated?.Invoke("无法识别自动滚动的目标窗口，请手动滚动。"); return; }
        if (ignoredSample != null) { Updated?.Invoke("请先滚回上一段附近，再启用自动滚动。"); return; }

        Resume();
        if (Terminal || State == ScrollState.Paused) return;
        Automatic = true;
        wheelAttempts = 0;
        wheelUnits = 1;
        automaticReady = nextWheel = clock.ElapsedMilliseconds + AutomaticStartDelay;
        if (ControlHandle != 0 && Native.GetForegroundWindow() == ControlHandle) Native.SetForegroundWindow(target);
        SetState(State, "2.5 秒后开始自动滚动。请把鼠标移到选区内，保持目标窗口在前台。");
    }

    public void Ignore()
    {
        if (Terminal || pending == null) return;
        ignoredSample = pendingSample;
        DiscardPending();
        LastMatch = frames[^1].MatchMetadata;
        Resume();
        SetState(State, "已忽略这帧。请滚回上一段附近，保留重叠后继续。");
    }

    public void Manual(int offset)
    {
        if (Terminal || pendingFrame == null || pendingSample == null) return;
        var (top, bottom, maximum) = ManualBounds();
        if (maximum == 0 || AtFrameLimit)
        {
            Updated?.Invoke("固定栏过大，已有分段无法保留重叠。请减小固定栏高度后重试。");
            return;
        }
        offset = Math.Clamp(offset, 1, maximum);
        if (!CanStitch(offset, top, bottom))
        {
            Updated?.Invoke("此位移无法加入当前长图。请缩小位移或完成已捕获的内容。");
            return;
        }
        epoch++;
        Commit(pendingFrame, pendingSample, new MatchMetadata(offset, 1, 0, 1, 1, Confidence.High, top, bottom), true);
    }

    public void Undo()
    {
        if (Terminal || frames.Count < 2) return;
        bool failed = State == ScrollState.Failed;
        epoch++;
        Automatic = false;
        var removed = frames[^1];
        frames.RemoveAt(frames.Count - 1);
        totalAddedRows -= removed.MatchMetadata!.Offset;
        reference = undoReference;
        undoReference = null;
        observed = ignoredSample = null;
        DiscardPending();
        LastMatch = frames[^1].MatchMetadata;
        FixedTop = LastMatch?.FixedTop ?? 0;
        FixedBottom = LastMatch?.FixedBottom ?? 0;
        appliedTopOverride = appliedBottomOverride = 0;
        var state = failed ? ScrollState.Failed : ScrollState.Paused;
        SaveManifest(state);
        DeleteFrameLater(removed);
        SetState(state, failed ? "已撤销上一段。可以完成编辑。" : "已撤销上一段。请将页面滚回对应位置后继续。");
    }

    public void Complete()
    {
        if (Terminal || frames.Count == 0) return;
        var result = BuildImage();
        epoch++;
        Automatic = false;
        DiscardPending();
        cancel.Cancel();
        SaveManifest(ScrollState.Completed);
        SetState(ScrollState.Completed, "完成。");
        Finished?.Invoke(result);
    }

    public void Cancel()
    {
        if (Terminal) return;
        epoch++;
        Automatic = false;
        bool loopStopped = State is ScrollState.Idle or ScrollState.Failed;
        DiscardPending();
        cancel.Cancel();
        SetState(ScrollState.Cancelled, "已取消。");
        if (loopStopped) _ = Task.Run(DeleteSessionFiles);
    }

    private void DiscardPending()
    {
        if (pendingFrame != null) DeleteFrameLater(pendingFrame);
        pending = null;
        pendingSample = null;
        pendingFrame = null;
    }

    private static void DeleteFrameLater(ScrollFrame frame) => _ = Task.Run(() => DeleteFile(frame.BitmapPath));

    private static void DeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error("Scroll frame cleanup", e); }
    }

    private void DeleteSessionFiles()
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error("Scroll session cleanup", e); }
    }

    public void Dispose()
    {
        if (disposed) return;
        Cancel();
        disposed = true;
        cancel.Dispose();
    }
}
