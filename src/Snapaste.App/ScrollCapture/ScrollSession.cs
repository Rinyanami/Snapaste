using System.Text.Json;
using Snapaste.Capture;
using Snapaste.Core;
using Snapaste.Platform;
namespace Snapaste.ScrollCapture;

public enum ScrollState { Idle, SelectingRegion, Ready, WaitingForScroll, Scrolling, WaitingForStableFrame, Matching, Stitching, Paused, Completed, Failed, Cancelled }
public sealed class ScrollSession : IDisposable
{
    private readonly ICaptureProvider capture;
    private readonly Settings settings;
    private readonly CancellationTokenSource cancel = new();
    private readonly string folder = Path.Combine(Paths.Sessions, Guid.NewGuid().ToString("N"));
    private readonly nint target;
    private readonly Rect targetBounds;
    private readonly Rect desktopBounds;
    private readonly List<ScrollFrame> frames = [];
    private FrameSample? reference, previous;
    private BitmapSource? pending;
    private FrameSample? pendingSample;
    private DateTime stableSince = DateTime.UtcNow, lastChange = DateTime.UtcNow, nextWheel = DateTime.MaxValue;
    private bool changed;
    private int noMotion, epoch;
    private double wheelUnits = 3;
    public ScrollState State { get; private set; } = ScrollState.Idle;
    public Rect Region { get; }
    public bool Automatic { get; private set; }
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
        Region = region; this.target = target; targetBounds = target == 0 ? Rect.Empty : Native.Bounds(target); desktopBounds = Native.Desktop; this.settings = settings; this.capture = capture;
    }
    private void SetState(ScrollState state, string message) { State = state; Updated?.Invoke(message); }
    public async Task Start()
    {
        Directory.CreateDirectory(folder); SetState(ScrollState.Ready, "正在捕获第一帧");
        try
        {
            await Task.Delay(350, cancel.Token); var first = capture.Capture(Region); reference = previous = await Task.Run(() => new FrameSample(first), cancel.Token); Store(first, reference, null, false);
            SetState(ScrollState.WaitingForScroll, "滚动页面继续截图");
            if (settings.AutoScrollDefault) SetAutomatic(true);
            while (!cancel.IsCancellationRequested)
            {
                await Task.Delay(150, cancel.Token);
                if (State is ScrollState.Paused or ScrollState.Failed) continue;
                if (!ValidateTarget()) { Pause("目标窗口、显示器或分辨率已改变。请完成当前截图或取消后重新框选。"); continue; }
                int generation = epoch;
                var bitmap = capture.Capture(Region); var sample = await Task.Run(() => new FrameSample(bitmap), cancel.Token);
                if (generation != epoch || State == ScrollState.Paused) continue;
                var now = DateTime.UtcNow; double motion = sample.Difference(previous!); previous = sample;
                if (motion > .004)
                {
                    if (Automatic && (now - lastChange).TotalMilliseconds < 140) nextWheel = now.AddMilliseconds(settings.AutoScrollDelay);
                    lastChange = now; stableSince = now; changed = true; SetState(ScrollState.WaitingForStableFrame, "等待画面稳定");
                }
                if (changed && (now - stableSince).TotalMilliseconds >= settings.StableMilliseconds)
                {
                    changed = false;
                    if (sample.Difference(reference!) < .004) { SetState(ScrollState.WaitingForScroll, "等待有效滚动"); }
                    else
                    {
                        SetState(ScrollState.Matching, "正在分析重叠区域");
                        var match = await Task.Run(() => ScrollMatcher.Match(reference!, sample, settings.MatchSensitivity, cancel.Token, TopOverride, BottomOverride), cancel.Token);
                        if (generation != epoch || State == ScrollState.Paused) continue;
                        LastMatch = match;
                        if (match.Confidence == Confidence.Low)
                        {
                            pending = bitmap; pendingSample = sample; Automatic = false; SetState(ScrollState.Paused, UI.Strings.T("LowMatch")); Log.Error($"Scroll low match: offset={match.Offset} score={match.Score:F3} uniqueness={match.Uniqueness:F3}");
                        }
                        else Accept(bitmap, sample, match, false);
                    }
                }
                if (Automatic && State == ScrollState.WaitingForScroll && now >= nextWheel)
                {
                    if (noMotion >= 4) { Automatic = false; SetState(ScrollState.Paused, "连续滚动后没有有效位移。可能已到底或正在加载；可继续或完成。"); continue; }
                    var cursor = Native.Cursor;
                    // Never move the user's cursor or scroll an unrelated application.
                    if (!Region.Contains(cursor) || Native.GetAncestor(Native.WindowFromPoint(new((int)cursor.X, (int)cursor.Y)), 2) != target || (Native.GetAsyncKeyState(1) & 0x8000) != 0)
                    { Pause("自动滚动已暂停：请将鼠标放回选区，点击自动滚动后移回页面。"); continue; }
                    SetState(ScrollState.Scrolling, "自动滚动中"); Native.Wheel(-(int)Math.Round(wheelUnits) * 120); noMotion++; nextWheel = now.AddMilliseconds(Math.Max(1600, settings.AutoScrollDelay + settings.StableMilliseconds)); SetState(ScrollState.WaitingForScroll, "等待页面滚动");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Log.Error("Scroll session", e); Automatic = false; SetState(ScrollState.Failed, "滚动截图已停止：" + e.Message); }
    }
    private bool ValidateTarget() => Native.Desktop == desktopBounds && (target == 0 || Native.IsWindow(target) && Native.IsWindowVisible(target) && !Native.IsIconic(target) && Native.Bounds(target) == targetBounds);
    private void Accept(BitmapSource bitmap, FrameSample sample, MatchMetadata match, bool manual)
    {
        SetState(ScrollState.Stitching, "正在拼接"); FixedTop = match.FixedTop; FixedBottom = match.FixedBottom;
        Store(bitmap, sample, match, manual); reference = previous = sample; pending = null; pendingSample = null; noMotion = 0;
        double ideal = (Region.Height - FixedTop - FixedBottom) * .42;
        wheelUnits = Math.Clamp(wheelUnits * ideal / Math.Max(1, match.Offset), 1, 10);
        nextWheel = DateTime.UtcNow.AddMilliseconds(settings.AutoScrollDelay); lastChange = DateTime.UtcNow;
        SetState(ScrollState.WaitingForScroll, $"已拼接 {frames.Count} 帧 · +{match.Offset}px · {match.Confidence} {match.Score:P0}");
        if (frames.Count >= settings.MaxScrollFrames) Pause("已达到连续截图数量上限，请完成保存。");
    }
    private void Store(BitmapSource bitmap, FrameSample sample, MatchMetadata? match, bool manual)
    {
        var path = Path.Combine(folder, $"frame-{frames.Count:D5}.png"); ImageDocument.SaveBitmap(bitmap, path);
        var frame = new ScrollFrame { BitmapPath = path, CaptureRect = Region, FrameIndex = frames.Count, PerceptualHash = sample.Hash, MatchMetadata = match };
        frames.Add(frame); frame.StitchMetadata = new(match?.Offset ?? bitmap.PixelHeight, BuildImage().Height, manual);
        AtomicFile.Write(Path.Combine(folder, "session.json"), JsonSerializer.Serialize(new { Region, FixedTop, FixedBottom, Frames = frames.Select(f => new { f.BitmapPath, f.Timestamp, f.CaptureRect, f.FrameIndex, f.PerceptualHash, f.MatchMetadata, f.StitchMetadata }) }, Settings.Json));
    }
    public ImageDocument BuildImage()
    {
        if (frames.Count == 0) throw new InvalidOperationException("尚未获取第一帧。");
        int h = (int)Region.Height, end = 0; var segments = new List<ImageSegment>();
        segments.Add(new(frames[0].BitmapPath, 0, h - FixedBottom, 0)); end = h - FixedBottom;
        foreach (var frame in frames.Skip(1))
        {
            int rows = frame.MatchMetadata!.Offset; int start = h - FixedBottom - rows;
            segments.Add(new(frame.BitmapPath, start, rows, end)); end += rows;
        }
        if (FixedBottom > 0) segments.Add(new(frames[^1].BitmapPath, h - FixedBottom, FixedBottom, end));
        return new ImageDocument((int)Region.Width, segments);
    }
    public void Pause(string message = "已暂停") { epoch++; Automatic = false; SetState(ScrollState.Paused, message); }
    public void Resume()
    {
        if (pending != null) { Updated?.Invoke("请先手动修正或忽略待确认的帧。"); return; }
        epoch++; changed = false; stableSince = DateTime.UtcNow; SetState(ScrollState.WaitingForScroll, "继续滚动页面");
    }
    public void SetAutomatic(bool enabled)
    {
        if (!enabled) { Pause(); return; }
        if (pending != null) { Updated?.Invoke("请先处理待确认帧。"); return; }
        Resume(); Automatic = true; noMotion = 0; nextWheel = DateTime.UtcNow.AddSeconds(2.5); Updated?.Invoke("2.5 秒后自动滚动，请将鼠标移到选区内。移出选区会暂停。");
    }
    public void Ignore() { epoch++; pending = null; pendingSample = null; Resume(); }
    public void Manual(int offset)
    {
        if (pending == null || pendingSample == null) return;
        offset = Math.Clamp(offset, 1, (int)Region.Height - FixedBottom - Math.Max(1, FixedTop));
        var match = new MatchMetadata(offset, 1, 0, 1, 1, Confidence.High, Math.Max(0, TopOverride), Math.Max(0, BottomOverride));
        epoch++; Accept(pending, pendingSample, match, true);
    }
    public void Undo()
    {
        if (frames.Count < 2) return;
        Pause("已撤销上一段。将页面滚回上一帧附近再继续。"); frames.RemoveAt(frames.Count - 1);
        reference = previous = new FrameSample(frames[^1].Bitmap); pending = null; pendingSample = null; LastMatch = frames[^1].MatchMetadata; Updated?.Invoke("已撤销上一段；请将页面滚回对应位置后继续。");
    }
    public void Complete() { if (frames.Count == 0) return; epoch++; Automatic = false; cancel.Cancel(); SetState(ScrollState.Completed, "完成"); Finished?.Invoke(BuildImage()); }
    public void Cancel() { epoch++; Automatic = false; cancel.Cancel(); SetState(ScrollState.Cancelled, "已取消"); }
    public void Dispose() { Cancel(); }
}
