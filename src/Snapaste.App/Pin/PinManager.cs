using System.Text.Json;
using System.Windows.Threading;
using Snapaste.Core;
using Snapaste.Editor;
using Snapaste.Platform;
using Snapaste.UI;
using Forms = System.Windows.Forms;
namespace Snapaste.Pin;

public sealed class PinState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public double X { get; set; } = 120;
    public double Y { get; set; } = 120;
    public double Scale { get; set; } = 1;
    public double Opacity { get; set; } = 1;
    public int Rotation { get; set; }
    public bool FlipH { get; set; }
    public bool FlipV { get; set; }
    public bool Topmost { get; set; } = true;
    public string ImagePath { get; set; } = "";
}

public sealed class PinManager
{
    public const int MaxImagePixels = 64_000_000;
    private readonly List<PinWindow> pins = [];
    private readonly List<PinState> inactive = [];
    private readonly HashSet<string> retired = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool exiting;
    private bool loaded;
    private static string Manifest => Path.Combine(Paths.Pins, "pins.json");

    public PinManager()
    {
        saveTimer.Tick += (_, _) =>
        {
            saveTimer.Stop();
            try { Save(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error("Pin state save", e); }
        };
    }

    public void Add(BitmapSource bitmap, Point? origin = null)
    {
        if ((long)bitmap.PixelWidth * bitmap.PixelHeight > MaxImagePixels)
            throw new InvalidOperationException("图片太大，请先缩小或裁剪，再贴图。");
        LoadState();
        bitmap.Freeze();
        var settings = App.Current.Settings;
        var position = origin ?? Native.Cursor;
        var state = new PinState { X = position.X, Y = position.Y, Opacity = settings.PinOpacity, Scale = settings.PinScale, Topmost = settings.PinTopmost };
        state.ImagePath = Path.Combine(Paths.Pins, state.Id + ".png");
        ImageDocument.SaveBitmap(bitmap, state.ImagePath);
        PinWindow? pin = null;
        try
        {
            pin = Open(state, bitmap, true);
            Save();
        }
        catch
        {
            if (pin != null) { pins.Remove(pin); pin.Close(); }
            DeleteCache(state.ImagePath);
            throw;
        }
    }

    private PinWindow Open(PinState state, BitmapSource bitmap, bool fit = false)
    {
        var pin = new PinWindow(state, bitmap, fit);
        pin.Changed += () => { saveTimer.Stop(); saveTimer.Start(); };
        pin.EditRequested += () =>
        {
            var editor = new EditorWindow(new ImageDocument(pin.Bitmap));
            editor.Completed += doc =>
            {
                if (!pins.Contains(pin)) return;
                var result = doc.Raster(doc.Crop);
                ImageDocument.SaveBitmap(result, state.ImagePath);
                pin.Replace(result);
                Save();
            };
            editor.Show();
        };
        pin.Closed += (_, _) =>
        {
            if (!pins.Remove(pin) || exiting) return;
            retired.Add(state.ImagePath);
            saveTimer.Stop();
            saveTimer.Start();
        };
        try { pin.Show(); }
        catch { pin.Close(); throw; }
        pins.Add(pin);
        return pin;
    }

    private void LoadState()
    {
        if (loaded) return;
        if (File.Exists(Manifest))
        {
            try
            {
                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var state in JsonSerializer.Deserialize<List<PinState>>(File.ReadAllText(Manifest)) ?? [])
                {
                    if (state == null || !Guid.TryParseExact(state.Id, "N", out _) || !ids.Add(state.Id)) continue;
                    state.ImagePath = Path.Combine(Paths.Pins, state.Id + ".png");
                    inactive.Add(state);
                }
            }
            catch (JsonException e)
            {
                Log.Error("Pin manifest", e);
                File.Copy(Manifest, Path.Combine(Paths.Pins, $"pins.corrupt-{Guid.NewGuid():N}.json"));
            }
        }
        loaded = true;
    }

    public void Restore()
    {
        try { LoadState(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error("Pin manifest", e); return; }
        foreach (var state in inactive.ToArray())
        {
            try
            {
                if (!File.Exists(state.ImagePath)) continue;
                Open(state, ImageDocument.ReadBitmap(state.ImagePath, maxPixels: MaxImagePixels));
                inactive.Remove(state);
            }
            catch (Exception e) when (e is not OutOfMemoryException) { Log.Error("Pin restore", e); }
        }
    }

    public void Save()
    {
        if (!loaded) return;
        AtomicFile.Write(Manifest, JsonSerializer.Serialize(pins.Select(pin => pin.State).Concat(inactive), Settings.Json));
        foreach (var path in retired.ToArray())
            if (DeleteCache(path)) retired.Remove(path);
    }

    private static bool DeleteCache(string path)
    {
        try { File.Delete(path); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error("Pin cache cleanup", e); return false; }
    }

    public void ShowAll() { foreach (var p in pins) p.Show(); }
    public void HideAll() { foreach (var p in pins) p.Hide(); }
    public void EnsureReachable() { foreach (var p in pins) p.KeepReachable(); }
    public void Recover() { foreach (var p in pins) { p.ClickThrough = false; p.Show(); p.KeepReachable(); } }
    public void CloseAll() { foreach (var p in pins.ToArray()) p.Close(); }

    public void Exit()
    {
        if (exiting) return;
        Save();
        exiting = true;
        saveTimer.Stop();
        foreach (var p in pins.ToArray()) p.Close();
    }
}

public sealed class PinWindow : Window
{
    public PinState State { get; }
    public BitmapSource Bitmap { get; private set; }
    private readonly Image image = new() { Stretch = Stretch.Fill };
    private bool through;
    private bool positioned;
    private bool repositioning;
    public event Action? Changed;
    public event Action? EditRequested;

    public bool ClickThrough
    {
        get => through;
        set
        {
            if (through == value) return;
            Native.ClickThrough(this, value);
            through = value;
        }
    }

    public PinWindow(PinState state, BitmapSource bitmap, bool fit = false)
    {
        State = state;
        Bitmap = bitmap;
        Title = "Snapaste · 贴图";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = state.Topmost;
        Opacity = state.Opacity = double.IsFinite(state.Opacity) ? Math.Clamp(state.Opacity, .15, 1) : 1;
        Content = image;
        if (!ValidCoordinate(state.X) || !ValidCoordinate(state.Y))
        {
            var position = Native.Cursor;
            state.X = position.X;
            state.Y = position.Y;
        }
        state.Rotation = (state.Rotation % 360 + 360) % 360 / 90 * 90;
        state.Scale = LimitScale(state.Scale, Position, fit);
        UpdateImage();
        SizeForDpi(1);

        SourceInitialized += (_, _) =>
        {
            Reposition();
            positioned = true;
            KeepReachable(fit);
        };
        DpiChanged += (_, _) =>
        {
            if (positioned && !repositioning)
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                {
                    if (!IsLoaded) return;
                    ReadPosition();
                    Reposition();
                    KeepReachable();
                });
        };
        LocationChanged += (_, _) => { if (positioned && !repositioning) { ReadPosition(); Changed?.Invoke(); } };
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { EditRequested?.Invoke(); return; }
            try { DragMove(); } catch (InvalidOperationException) { }
            KeepReachable();
            Changed?.Invoke();
        };
        MouseWheel += ZoomOrChangeOpacity;
        KeyDown += async (_, e) =>
        {
            try
            {
                if (e.Key is Key.Escape or Key.Delete) Close();
                else if (e.Key == Key.R) Rotate();
                else if (e.Key == Key.E) EditRequested?.Invoke();
                else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.C) await Copy();
                else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.S) await SaveImage();
            }
            catch (Exception error) { Ui.Error(error, this); }
        };

        var menu = new ContextMenu();
        MenuItem Item(string text, Action action)
        {
            var item = new MenuItem { Header = text };
            item.Click += (_, _) => { try { action(); } catch (Exception e) { Ui.Error(e, this); } };
            menu.Items.Add(item);
            return item;
        }
        void AsyncItem(string text, Func<Task> action)
        {
            var item = new MenuItem { Header = text };
            item.Click += async (_, _) => { try { await action(); } catch (Exception e) { Ui.Error(e, this); } };
            menu.Items.Add(item);
        }
        Item(Strings.T("Edit"), () => EditRequested?.Invoke());
        AsyncItem(Strings.T("Copy"), Copy);
        AsyncItem(Strings.T("Save"), SaveImage);
        Item(Strings.T("Rotate"), Rotate);
        Item(Strings.T("FlipH"), () => { State.FlipH = !State.FlipH; UpdateImage(); Changed?.Invoke(); });
        Item(Strings.T("FlipV"), () => { State.FlipV = !State.FlipV; UpdateImage(); Changed?.Invoke(); });
        Item(Strings.T("Reset"), () =>
        {
            State.Rotation = 0;
            State.FlipH = State.FlipV = false;
            State.Scale = LimitScale(1, Position, true);
            UpdateImage();
            Reposition();
            KeepReachable(true);
            Changed?.Invoke();
        });
        Item(Strings.T("AlwaysTop"), () => { State.Topmost = Topmost = !Topmost; Changed?.Invoke(); });
        Item("透明度 +10%", () => AdjustOpacity(.1));
        Item("透明度 −10%", () => AdjustOpacity(-.1));
        var passThrough = Item(Strings.T("ClickThrough"), () => ClickThrough = !ClickThrough);
        menu.Opened += (_, _) =>
        {
            var app = App.Current;
            var gesture = app.Settings.RecoverKey;
            passThrough.Header = Strings.T("ClickThrough") + (app.HotkeyUnavailable(gesture) ? " · 从托盘恢复" : $" · {gesture} 恢复");
        };
        Item(Strings.T("Close"), Close);
        ContextMenu = menu;
    }

    private static bool ValidCoordinate(double value) => double.IsFinite(value) && value is >= int.MinValue and <= int.MaxValue;
    private Point Position => new(State.X, State.Y);
    private int ImageWidth => State.Rotation % 180 == 0 ? Bitmap.PixelWidth : Bitmap.PixelHeight;
    private int ImageHeight => State.Rotation % 180 == 0 ? Bitmap.PixelHeight : Bitmap.PixelWidth;

    private static System.Drawing.Rectangle WorkArea(Point point)
        => Forms.Screen.FromPoint(new System.Drawing.Point((int)point.X, (int)point.Y)).WorkingArea;

    private double LimitScale(double scale, Point point, bool fit = false)
    {
        var settings = App.Current.Settings;
        var work = WorkArea(point);
        double physicalMax = Math.Min((double)Math.Max(1, work.Width) / ImageWidth, (double)Math.Max(1, work.Height) / ImageHeight);
        if (!fit)
        {
            double surfaceMax = Math.Sqrt((double)Math.Max(1, work.Width) * Math.Max(1, work.Height) * 2 / ((double)ImageWidth * ImageHeight));
            physicalMax = Math.Min(physicalMax * 2, surfaceMax);
        }
        double configuredMax = double.IsFinite(settings.MaxPinScale) && settings.MaxPinScale > 0 ? settings.MaxPinScale : 8;
        double configuredMin = double.IsFinite(settings.MinPinScale) && settings.MinPinScale > 0 ? settings.MinPinScale : .1;
        double max = Math.Min(configuredMax, physicalMax);
        return Math.Clamp(double.IsFinite(scale) ? scale : 1, Math.Min(configuredMin, max), max);
    }

    private (int Width, int Height) PhysicalSize() => (Math.Max(16, (int)Math.Round(ImageWidth * State.Scale)), Math.Max(16, (int)Math.Round(ImageHeight * State.Scale)));

    private void ZoomOrChangeOpacity(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) AdjustOpacity(Math.Sign(e.Delta) * .05);
        else
        {
            var anchor = PointToScreen(e.GetPosition(this));
            var previous = CurrentBounds();
            State.Scale = LimitScale(State.Scale * (e.Delta > 0 ? 1.1 : 1 / 1.1), anchor);
            var size = PhysicalSize();
            State.X = anchor.X - (anchor.X - previous.X) * size.Width / previous.Width;
            State.Y = anchor.Y - (anchor.Y - previous.Y) * size.Height / previous.Height;
            Reposition();
            Changed?.Invoke();
        }
        e.Handled = true;
    }

    private void AdjustOpacity(double delta)
    {
        State.Opacity = Opacity = Math.Clamp(Opacity + delta, .15, 1);
        Changed?.Invoke();
    }

    public void Replace(BitmapSource source)
    {
        Bitmap = source;
        State.Scale = LimitScale(State.Scale, Position);
        UpdateImage();
        Reposition();
        KeepReachable();
    }

    private BitmapSource Transformed()
    {
        if (!State.FlipH && !State.FlipV && State.Rotation == 0) return Bitmap;
        var group = new TransformGroup();
        group.Children.Add(new ScaleTransform(State.FlipH ? -1 : 1, State.FlipV ? -1 : 1));
        group.Children.Add(new RotateTransform(State.Rotation));
        var result = new TransformedBitmap(Bitmap, group);
        result.Freeze();
        return result;
    }

    private Task Copy() => ImageExport.Copy(new EditDocument(new ImageDocument(Transformed())));
    private async Task SaveImage() => await ImageExport.SaveDialogAsync(new EditDocument(new ImageDocument(Transformed())), App.Current.Settings, this);

    private void Rotate()
    {
        State.Rotation = (State.Rotation + 90) % 360;
        State.Scale = LimitScale(State.Scale, Position);
        UpdateImage();
        Reposition();
        KeepReachable();
        Changed?.Invoke();
    }

    private void UpdateImage() => image.Source = Transformed();

    private void SizeForDpi(double scale)
    {
        var (width, height) = PhysicalSize();
        Width = width / scale;
        Height = height / scale;
    }

    private Rect CurrentBounds()
    {
        if (Native.GetWindowRect(Native.Handle(this), out var rect) && rect.Right > rect.Left && rect.Bottom > rect.Top) return rect.Rect;
        var (width, height) = PhysicalSize();
        return new(State.X, State.Y, width, height);
    }

    private void ReadPosition()
    {
        var bounds = CurrentBounds();
        State.X = bounds.X;
        State.Y = bounds.Y;
    }

    private void Reposition()
    {
        var handle = Native.Handle(this);
        if (handle == 0) return;
        repositioning = true;
        try
        {
            double dpi = Math.Max(96u, Native.GetDpiForWindow(handle)) / 96d;
            SizeForDpi(dpi);
            var (width, height) = PhysicalSize();
            var bounds = new Rect(Math.Round(State.X), Math.Round(State.Y), width, height);
            Native.PlacePhysical(this, bounds);
            double current = Math.Max(96u, Native.GetDpiForWindow(handle)) / 96d;
            if (current != dpi)
            {
                SizeForDpi(current);
                Native.PlacePhysical(this, bounds);
            }
        }
        finally { repositioning = false; }
        ReadPosition();
    }

    public void KeepReachable(bool fit = false)
    {
        var bounds = CurrentBounds();
        if (!fit && Forms.Screen.AllScreens.Any(screen =>
        {
            var r = screen.WorkingArea;
            var visible = Rect.Intersect(bounds, new Rect(r.X, r.Y, r.Width, r.Height));
            return !visible.IsEmpty && visible.Width >= Math.Min(32, bounds.Width) && visible.Height >= Math.Min(32, bounds.Height);
        })) return;
        var work = WorkArea(Position);
        State.Scale = LimitScale(State.Scale, Position, fit);
        var (width, height) = PhysicalSize();
        State.X = Math.Clamp(State.X, work.Left, Math.Max(work.Left, work.Right - width));
        State.Y = Math.Clamp(State.Y, work.Top, Math.Max(work.Top, work.Bottom - height));
        Reposition();
        Changed?.Invoke();
    }
}
