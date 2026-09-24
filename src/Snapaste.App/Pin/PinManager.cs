using System.Text.Json;
using Snapaste.Core;
using Snapaste.Editor;
using Snapaste.Platform;
using Snapaste.UI;
using System.Windows.Threading;
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
    private readonly List<PinWindow> pins = [];
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool exiting;
    public PinManager() { saveTimer.Tick += (_, _) => { saveTimer.Stop(); Save(); }; }
    public void Add(BitmapSource bitmap)
    {
        var settings = App.Current.Settings;
        var state = new PinState { X = Native.Cursor.X, Y = Native.Cursor.Y, Opacity = settings.PinOpacity, Scale = settings.PinScale, Topmost = settings.PinTopmost };
        state.ImagePath = Path.Combine(Paths.Pins, state.Id + ".png"); ImageDocument.SaveBitmap(bitmap, state.ImagePath); Open(state, bitmap); Save();
    }
    private void Open(PinState state, BitmapSource bitmap)
    {
        var pin = new PinWindow(state, bitmap); pins.Add(pin); pin.Changed += () => { saveTimer.Stop(); saveTimer.Start(); };
        pin.EditRequested += () =>
        {
            var editor = new EditorWindow(new ImageDocument(pin.Bitmap));
            editor.Completed += doc => { var result = doc.Raster(doc.Crop); ImageDocument.SaveBitmap(result, state.ImagePath); pin.Replace(result); Save(); };
            editor.Show();
        };
        pin.Closed += (_, _) => { pins.Remove(pin); if (!exiting) Save(); };
        pin.Show();
    }
    public void Restore()
    {
        try
        {
            var p = Path.Combine(Paths.Pins, "pins.json"); if (!File.Exists(p)) return;
            foreach (var state in JsonSerializer.Deserialize<List<PinState>>(File.ReadAllText(p)) ?? [])
            {
                try { if (File.Exists(state.ImagePath)) Open(state, ImageDocument.ReadBitmap(state.ImagePath)); }
                catch (Exception e) { Log.Error("Pin restore", e); }
            }
        }
        catch (Exception e) when (e is IOException or JsonException) { Log.Error("Pin manifest", e); }
    }
    public void Save()
    {
        AtomicFile.Write(Path.Combine(Paths.Pins, "pins.json"), JsonSerializer.Serialize(pins.Select(p => p.State), Settings.Json));
        var active = pins.Select(p => p.State.ImagePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(Paths.Pins, "*.png"))
            if (!active.Contains(path)) { try { File.Delete(path); } catch (IOException e) { Log.Error("Pin cache cleanup", e); } }
    }
    public void ShowAll() { foreach (var p in pins) p.Show(); }
    public void HideAll() { foreach (var p in pins) p.Hide(); }
    public void Recover() { foreach (var p in pins) { p.ClickThrough = false; p.Show(); p.KeepReachable(); } }
    public void CloseAll() { foreach (var p in pins.ToArray()) p.Close(); }
    public void Exit() { saveTimer.Stop(); Save(); exiting = true; foreach (var p in pins.ToArray()) p.Close(); }
}
public sealed class PinWindow : Window
{
    public PinState State { get; }
    public BitmapSource Bitmap { get; private set; }
    private readonly Image image = new() { Stretch = Stretch.Fill };
    private bool through;
    private bool positioned;
    public event Action? Changed;
    public event Action? EditRequested;
    public bool ClickThrough { get => through; set { through = value; Native.ClickThrough(this, value); } }
    public PinWindow(PinState state, BitmapSource bitmap)
    {
        State = state; Bitmap = bitmap; Title = "Snapaste · 贴图";
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; Topmost = state.Topmost; Opacity = Math.Clamp(state.Opacity, .15, 1); Content = image;
        UpdateImage();
        SourceInitialized += (_, _) => { Reposition(); positioned = true; KeepReachable(); };
        LocationChanged += (_, _) => { if (positioned) { var p = PointToScreen(new Point()); State.X = p.X; State.Y = p.Y; Changed?.Invoke(); } };
        MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { EditRequested?.Invoke(); return; } try { DragMove(); } catch (InvalidOperationException) { } Changed?.Invoke(); };
        MouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { State.Opacity = Opacity = Math.Clamp(Opacity + Math.Sign(e.Delta) * .05, .15, 1); }
            else
            {
                var point = e.GetPosition(this); var screenPoint = PointToScreen(point); double old = State.Scale;
                State.Scale = Math.Clamp(old * (e.Delta > 0 ? 1.1 : 1 / 1.1), App.Current.Settings.MinPinScale, App.Current.Settings.MaxPinScale);
                double ratio = State.Scale / old; State.X = screenPoint.X - (screenPoint.X - State.X) * ratio; State.Y = screenPoint.Y - (screenPoint.Y - State.Y) * ratio; UpdateImage(); Reposition();
            }
            Changed?.Invoke(); e.Handled = true;
        };
        KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape || e.Key == Key.Delete) Close();
            if (e.Key == Key.R) Rotate();
            if (e.Key == Key.E) EditRequested?.Invoke();
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.C) await ImageExport.Copy(new EditDocument(new ImageDocument(Transformed())));
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.S) ImageExport.SaveDialog(new EditDocument(new ImageDocument(Transformed())), App.Current.Settings, this);
        };
        var menu = new ContextMenu();
        void Item(string text, Action action) { var item = new MenuItem { Header = text }; item.Click += (_, _) => { try { action(); } catch (Exception e) { Ui.Error(e); } }; menu.Items.Add(item); }
        Item(Strings.T("Edit"), () => EditRequested?.Invoke());
        Item(Strings.T("Copy"), async () => await ImageExport.Copy(new EditDocument(new ImageDocument(Transformed()))));
        Item(Strings.T("Save"), () => ImageExport.SaveDialog(new EditDocument(new ImageDocument(Transformed())), App.Current.Settings, this));
        Item(Strings.T("Rotate"), Rotate);
        Item(Strings.T("FlipH"), () => { State.FlipH = !State.FlipH; UpdateImage(); Changed?.Invoke(); });
        Item(Strings.T("FlipV"), () => { State.FlipV = !State.FlipV; UpdateImage(); Changed?.Invoke(); });
        Item(Strings.T("Reset"), () => { State.Scale = 1; State.Rotation = 0; State.FlipH = State.FlipV = false; UpdateImage(); Reposition(); Changed?.Invoke(); });
        Item(Strings.T("AlwaysTop"), () => { State.Topmost = Topmost = !Topmost; Changed?.Invoke(); });
        Item("透明度 +10%", () => { State.Opacity = Opacity = Math.Clamp(Opacity + .1, .15, 1); Changed?.Invoke(); });
        Item("透明度 −10%", () => { State.Opacity = Opacity = Math.Clamp(Opacity - .1, .15, 1); Changed?.Invoke(); });
        Item(Strings.T("ClickThrough") + " · " + App.Current.Settings.RecoverKey + " 恢复", () => ClickThrough = !ClickThrough);
        Item(Strings.T("Close"), Close); ContextMenu = menu;
    }
    public void Replace(BitmapSource source) { Bitmap = source; UpdateImage(); Reposition(); }
    private BitmapSource Transformed()
    {
        var group = new TransformGroup(); group.Children.Add(new ScaleTransform(State.FlipH ? -1 : 1, State.FlipV ? -1 : 1)); group.Children.Add(new RotateTransform(State.Rotation));
        var result = new TransformedBitmap(Bitmap, group); result.Freeze(); return result;
    }
    private void Rotate() { State.Rotation = (State.Rotation + 90) % 360; UpdateImage(); Reposition(); Changed?.Invoke(); }
    private void UpdateImage()
    {
        image.Source = Transformed(); double dpi = IsLoaded ? VisualTreeHelper.GetDpi(this).DpiScaleX : 1;
        Width = Math.Max(16, (State.Rotation % 180 == 0 ? Bitmap.PixelWidth : Bitmap.PixelHeight) * State.Scale / dpi);
        Height = Math.Max(16, (State.Rotation % 180 == 0 ? Bitmap.PixelHeight : Bitmap.PixelWidth) * State.Scale / dpi);
    }
    private void Reposition()
    {
        int w = Math.Max(16, (int)((State.Rotation % 180 == 0 ? Bitmap.PixelWidth : Bitmap.PixelHeight) * State.Scale));
        int h = Math.Max(16, (int)((State.Rotation % 180 == 0 ? Bitmap.PixelHeight : Bitmap.PixelWidth) * State.Scale));
        Native.PlacePhysical(this, new Rect(State.X, State.Y, w, h));
    }
    public void KeepReachable()
    {
        var screens = System.Windows.Forms.Screen.AllScreens.Select(s => new Rect(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height));
        var bounds = Native.Bounds(Native.Handle(this));
        if (!screens.Any(r => { var intersection = Rect.Intersect(r, bounds); return !intersection.IsEmpty && intersection.Width > 30 && intersection.Height > 30; }))
        {
            var r = screens.First(); State.X = r.X + 40; State.Y = r.Y + 40; Reposition();
        }
    }
}
