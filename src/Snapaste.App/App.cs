using System.IO.Pipes;
using System.Windows.Threading;
using Snapaste.Core;
using Snapaste.Platform;
using Snapaste.UI;
using Snapaste.Capture;
using Snapaste.Editor;
using Snapaste.Pin;
using Snapaste.ScrollCapture;
using System.Runtime.CompilerServices;
using Forms = System.Windows.Forms;
namespace Snapaste;

public sealed class App : Application
{
    private Mutex? instance;
    private readonly CancellationTokenSource lifetime = new();
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private Hotkeys? hotkeys;
    public Settings Settings { get; private set; } = Settings.Load();
    public static new App Current => (App)Application.Current;
    private Window? settingsWindow;
    private HistoryWindow? historyWindow;
    private CaptureOverlay? overlay;
    private ScrollWindow? scrollWindow;
    private readonly DesktopCapture captureProvider = new();
    private PinManager? pins;
    public HistoryStore History { get; } = new();
    private readonly ConditionalWeakTable<EditDocument, SavedRevision> historyRevisions = new();
    private readonly SemaphoreSlim historyQueue = new(1, 1);
    private int historyJobs;
    private sealed class SavedRevision { public string Signature = ""; }
    [STAThread] public static void Main(string[] args)
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Run();
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Snapaste;component/UI/Theme.xaml", UriKind.Relative) });
        Ui.Theme(Settings.Theme);
        instance = new Mutex(true, "Local\\Snapaste.Personal.Instance", out var created);
        if (!created)
        {
            try { using var pipe = new NamedPipeClientStream(".", "Snapaste.Personal.Activate", PipeDirection.Out); pipe.Connect(800); using var writer = new StreamWriter(pipe); writer.WriteLine("activate"); }
            catch (IOException) { }
            Shutdown(); return;
        }
        DispatcherUnhandledException += (_, args) => { Log.Error("Unhandled UI", args.Exception); if (args.Exception is not OutOfMemoryException) { args.Handled = true; Ui.Error(args.Exception); } };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error("Background", args.Exception); args.SetObserved(); };
        PruneSessions(); SetupTray(); hotkeys = new Hotkeys(); ConfigureHotkeys(); pins = new PinManager();
        if (Settings.RestorePins) pins.Restore();
        _ = Listen();
        if (e.Args.Contains("--settings")) ShowSettings();
    }
    private async Task Listen()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try { using var pipe = new NamedPipeServerStream("Snapaste.Personal.Activate", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous); await pipe.WaitForConnectionAsync(lifetime.Token); await Dispatcher.InvokeAsync(ShowSettings); }
            catch (OperationCanceledException) { break; }
            catch (IOException e) { Log.Error("Single instance", e); await Task.Delay(500); }
        }
    }
    private void PruneSessions()
    {
        try
        {
            var directories = new DirectoryInfo(Paths.Sessions).EnumerateDirectories().OrderBy(d => d.LastWriteTimeUtc).ToArray();
            long total = directories.Sum(d => d.EnumerateFiles().Sum(f => f.Length));
            foreach (var directory in directories)
            {
                if (directory.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-7) && total <= (long)Settings.HistoryMegabytes * 1024 * 1024) continue;
                if (!Guid.TryParseExact(directory.Name, "N", out _)) continue;
                long bytes = directory.EnumerateFiles().Sum(f => f.Length); directory.Delete(true); total -= bytes;
            }
        }
        catch (IOException e) { Log.Error("Session cache cleanup", e); }
    }
    private void SetupTray()
    {
        var menu = new Forms.ContextMenuStrip();
        void Add(string key, Action action) => menu.Items.Add(Strings.T(key), null, (_, _) => Dispatcher.BeginInvoke(action));
        Add("Capture", () => Capture(false)); Add("Scroll", () => Capture(true)); Add("Pin", PinClipboard);
        Add("History", ShowHistory); menu.Items.Add(new Forms.ToolStripSeparator());
        Add("ShowPins", ShowPins); Add("HidePins", HidePins); Add("RecoverPins", RecoverPins);
        Add("ClosePins", () => pins?.CloseAll());
        Add("Settings", ShowSettings); Add("Exit", ExitSafely);
        trayIcon = BrandIcon.Tray();
        tray = new Forms.NotifyIcon { Text = $"Snapaste · {Settings.CaptureKey} 截图 · {Settings.PinKey} 贴图", Icon = trayIcon, ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(() => Capture(false));
    }
    public void ConfigureHotkeys()
    {
        try { hotkeys?.Configure([(Settings.CaptureKey, () => Capture(false)), (Settings.PinKey, PinClipboard), (Settings.ScrollKey, () => Capture(true)), (Settings.RecoverKey, RecoverPins)]); }
        catch (Exception e) { Dispatcher.BeginInvoke(() => Ui.Error(e)); }
    }
    public void ShowSettings()
    {
        if (settingsWindow == null) { settingsWindow = new SettingsWindow(); settingsWindow.Closed += (_, _) => settingsWindow = null; }
        settingsWindow.Show(); if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal; settingsWindow.Activate();
    }
    public void ApplySettings(Settings settings)
    {
        var previous = Settings;
        try
        {
            hotkeys?.Configure([(settings.CaptureKey, () => Capture(false)), (settings.PinKey, PinClipboard), (settings.ScrollKey, () => Capture(true)), (settings.RecoverKey, RecoverPins)]);
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (settings.StartWithWindows) key.SetValue("Snapaste", "\"" + Environment.ProcessPath + "\""); else key.DeleteValue("Snapaste", false);
            settings.Save(); Settings = settings; Ui.Theme(settings.Theme); History.Clean();
            if (tray != null) tray.Text = $"Snapaste · {settings.CaptureKey} 截图 · {settings.PinKey} 贴图";
        }
        catch { Settings = previous; ConfigureHotkeys(); throw; }
    }
    public void Capture(bool scroll)
    {
        if (overlay != null || scrollWindow != null) return;
        try
        {
            var bounds = Native.Desktop; var bitmap = captureProvider.Capture(bounds, Settings.IncludeCursor);
            overlay = new CaptureOverlay(bitmap, bounds, Settings, scroll);
            var captureOverlay = overlay;
            overlay.Closed += (_, _) => overlay = null;
            overlay.Accepted += async (rect, doc, action, target) =>
            {
                try
                {
                    if (action == "Scroll")
                    {
                        if (rect.Height < 100 || rect.Width < 80) throw new InvalidOperationException("滚动选区至少需要 80 × 100 像素。");
                        if (target == 0) { var center = new Native.POINT((int)(rect.X + rect.Width / 2), (int)(rect.Y + rect.Height / 2)); target = Native.GetAncestor(Native.WindowFromPoint(center), 2); }
                        if (target != 0) Native.SetForegroundWindow(target);
                        var session = new ScrollSession(rect, target, Settings, captureProvider); session.Finished += result => Edit(result);
                        scrollWindow = new ScrollWindow(session); scrollWindow.Closed += (_, _) => scrollWindow = null; scrollWindow.Show(); return true;
                    }
                    if (action == "Copy") { await ImageExport.Copy(doc); Remember(doc); }
                    else if (action == "Save") { if (!await ImageExport.SaveDialogAsync(doc, Settings, captureOverlay)) return false; Remember(doc); }
                    else if (action == "Pin") { PinDocument(doc); Remember(doc); }
                    return true;
                }
                catch (Exception ex) { Ui.Error(ex, captureOverlay); return false; }
            };
            overlay.Show();
        }
        catch (Exception e) { Log.Error("Capture failure", e); Ui.Error(e); }
    }
    public void Edit(ImageDocument image, Tool tool = Tool.Select)
    {
        var editor = new EditorWindow(image, tool); editor.PinRequested += PinDocument; editor.Completed += Remember; editor.Show();
    }
    public async void Remember(EditDocument doc)
    {
        var signature = System.Text.Json.JsonSerializer.Serialize(new { doc.Crop, Objects = doc.Annotations.Select(a => new { a.Id, a.Kind, a.Bounds, a.Text, a.Color, a.Stroke, a.Font, a.FontSize, a.Bold, a.Background, a.EffectSize, a.Points }) });
        var revision = historyRevisions.GetOrCreateValue(doc); if (revision.Signature == signature) return;
        var snapshot = doc.Fork(); var configuration = Settings; revision.Signature = signature; historyJobs++;
        await historyQueue.WaitAsync();
        try { await RenderWorker.Run(() => History.Add(snapshot, configuration)); }
        catch (Exception e) { revision.Signature = ""; Log.Error("History save", e); Ui.Error(e); }
        finally { historyJobs--; historyQueue.Release(); }
    }
    private async void ExitSafely()
    {
        tray?.ShowBalloonTip(1500, "Snapaste", "正在保存本地状态…", Forms.ToolTipIcon.Info);
        while (historyJobs > 0) await Task.Delay(100);
        Shutdown();
    }
    public void PinDocument(EditDocument doc) => pins?.Add(doc.Raster(doc.Crop));
    public async void PinClipboard()
    {
        try
        {
            BitmapSource? image = null;
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    image = Clipboard.ContainsImage() ? Clipboard.GetImage() : null;
                    if (image == null && Clipboard.ContainsFileDropList())
                        foreach (string? path in Clipboard.GetFileDropList()) { if (path == null || !new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" }.Contains(Path.GetExtension(path).ToLowerInvariant())) continue; image = ImageDocument.ReadBitmap(path); break; }
                    break;
                }
                catch (System.Runtime.InteropServices.COMException) when (attempt < 4) { await Task.Delay(60); }
            }
            if (image == null) { tray?.ShowBalloonTip(2000, "Snapaste", Strings.T("NoImage"), Forms.ToolTipIcon.Info); return; }
            image.Freeze(); pins?.Add(image);
        }
        catch (Exception e) { Ui.Error(e); }
    }
    public void ShowHistory()
    {
        if (historyWindow == null) { historyWindow = new HistoryWindow(History); historyWindow.Closed += (_, _) => historyWindow = null; }
        else historyWindow.Reload();
        historyWindow.Show(); if (historyWindow.WindowState == WindowState.Minimized) historyWindow.WindowState = WindowState.Normal; historyWindow.Activate();
    }
    public void ShowPins() => pins?.ShowAll();
    public void HidePins() => pins?.HideAll();
    public void RecoverPins() => pins?.Recover();
    protected override void OnExit(ExitEventArgs e)
    {
        lifetime.Cancel(); pins?.Exit(); hotkeys?.Dispose(); tray?.Dispose(); trayIcon?.Dispose(); instance?.Dispose(); base.OnExit(e);
    }
}
