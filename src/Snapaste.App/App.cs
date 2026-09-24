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
    private Hotkeys? hotkeys;
    public Settings Settings { get; private set; } = Settings.Load();
    public static new App Current => (App)Application.Current;
    private Window? settingsWindow;
    private CaptureOverlay? overlay;
    private ScrollWindow? scrollWindow;
    private readonly DesktopCapture captureProvider = new();
    private PinManager? pins;
    public HistoryStore History { get; } = new();
    private readonly ConditionalWeakTable<EditDocument, SavedRevision> historyRevisions = new();
    private sealed class SavedRevision { public string Signature = ""; }
    [STAThread] public static void Main(string[] args)
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Run();
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
        SetupTray(); hotkeys = new Hotkeys(); ConfigureHotkeys(); pins = new PinManager();
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
    private void SetupTray()
    {
        var menu = new Forms.ContextMenuStrip();
        void Add(string key, Action action) => menu.Items.Add(Strings.T(key), null, (_, _) => Dispatcher.BeginInvoke(action));
        Add("Capture", () => Capture(false)); Add("Scroll", () => Capture(true)); Add("Pin", PinClipboard);
        Add("History", ShowHistory); menu.Items.Add(new Forms.ToolStripSeparator());
        Add("ShowPins", ShowPins); Add("HidePins", HidePins); Add("RecoverPins", RecoverPins);
        Add("ClosePins", () => pins?.CloseAll());
        Add("Settings", ShowSettings); Add("Exit", Shutdown);
        tray = new Forms.NotifyIcon { Text = "Snapaste · F1 截图 · F3 贴图", Icon = System.Drawing.SystemIcons.Application, ContextMenuStrip = menu, Visible = true };
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
        settingsWindow.Show(); settingsWindow.Activate();
    }
    public void ApplySettings(Settings settings)
    {
        var previous = Settings;
        try
        {
            hotkeys?.Configure([(settings.CaptureKey, () => Capture(false)), (settings.PinKey, PinClipboard), (settings.ScrollKey, () => Capture(true)), (settings.RecoverKey, RecoverPins)]);
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (settings.StartWithWindows) key.SetValue("Snapaste", "\"" + Environment.ProcessPath + "\""); else key.DeleteValue("Snapaste", false);
            settings.Save(); Settings = settings; History.Clean();
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
            overlay.Closed += (_, _) => overlay = null;
            overlay.Accepted += async (rect, image, action, target) =>
            {
                try
                {
                    if (action == "Scroll")
                    {
                        if (rect.Height < 100 || rect.Width < 80) throw new InvalidOperationException("滚动选区至少需要 80 × 100 像素。");
                        if (target == 0) { var center = new Native.POINT((int)(rect.X + rect.Width / 2), (int)(rect.Y + rect.Height / 2)); target = Native.GetAncestor(Native.WindowFromPoint(center), 2); }
                        var session = new ScrollSession(rect, target, Settings, captureProvider); session.Finished += result => Edit(result);
                        scrollWindow = new ScrollWindow(session); scrollWindow.Closed += (_, _) => scrollWindow = null; scrollWindow.Show(); return;
                    }
                    var doc = new EditDocument(new ImageDocument(image));
                    if (action == "Copy") { await ImageExport.Copy(doc); Remember(doc); }
                    else if (action == "Save") { if (ImageExport.SaveDialog(doc, Settings)) Remember(doc); }
                    else if (action == "Pin") { PinDocument(doc); Remember(doc); }
                    else Edit(doc.Image, Enum.TryParse<Tool>(action, out var t) ? t : Tool.Select);
                }
                catch (Exception ex) { Ui.Error(ex); }
            };
            overlay.Show();
        }
        catch (Exception e) { Log.Error("Capture failure", e); Ui.Error(e); }
    }
    public void Edit(ImageDocument image, Tool tool = Tool.Select)
    {
        var editor = new EditorWindow(image, tool); editor.PinRequested += PinDocument; editor.Completed += Remember; editor.Show();
    }
    public void Remember(EditDocument doc)
    {
        var signature = System.Text.Json.JsonSerializer.Serialize(new { doc.Crop, Objects = doc.Annotations.Select(a => new { a.Id, a.Kind, a.Bounds, a.Text, a.Color, a.Stroke, a.Font, a.FontSize, a.Bold, a.Background, a.EffectSize, a.Points }) });
        var revision = historyRevisions.GetOrCreateValue(doc); if (revision.Signature == signature) return;
        try { History.Add(doc); revision.Signature = signature; } catch (Exception e) { Log.Error("History save", e); Ui.Error(e); }
    }
    public void PinDocument(EditDocument doc) => pins?.Add(doc.Raster(doc.Crop));
    public void PinClipboard()
    {
        try
        {
            BitmapSource? image = Clipboard.ContainsImage() ? Clipboard.GetImage() : null;
            if (image == null && Clipboard.ContainsFileDropList())
                foreach (string? path in Clipboard.GetFileDropList()) { if (path == null || !new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" }.Contains(Path.GetExtension(path).ToLowerInvariant())) continue; image = ImageDocument.ReadBitmap(path); break; }
            if (image == null) { tray?.ShowBalloonTip(2000, "Snapaste", Strings.T("NoImage"), Forms.ToolTipIcon.Info); return; }
            image.Freeze(); pins?.Add(image);
        }
        catch (Exception e) { Ui.Error(e); }
    }
    public void ShowHistory() => new HistoryWindow(History).Show();
    public void ShowPins() => pins?.ShowAll();
    public void HidePins() => pins?.HideAll();
    public void RecoverPins() => pins?.Recover();
    protected override void OnExit(ExitEventArgs e)
    {
        lifetime.Cancel(); pins?.Exit(); hotkeys?.Dispose(); tray?.Dispose(); instance?.Dispose(); base.OnExit(e);
    }
}
