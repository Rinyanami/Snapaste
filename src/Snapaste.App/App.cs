using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Microsoft.Win32;
using Snapaste.Capture;
using Snapaste.Core;
using Snapaste.Editor;
using Snapaste.Pin;
using Snapaste.Platform;
using Snapaste.ScrollCapture;
using Snapaste.UI;
using Forms = System.Windows.Forms;
namespace Snapaste;

public sealed class App : Application
{
    private const string MutexName = "Local\\Snapaste.Personal.Instance";
    private const string PipeName = "Snapaste.Personal.Activate";
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };
    private Mutex? instance;
    private bool ownsInstance;
    private readonly CancellationTokenSource lifetime = new();
    private Forms.NotifyIcon? tray;
    private Forms.ContextMenuStrip? trayMenu;
    private System.Drawing.Icon? trayIcon;
    private Hotkeys? hotkeys;
    private bool exiting;
    private bool pasting;
    private Window? settingsWindow;
    private HistoryWindow? historyWindow;
    private CaptureOverlay? overlay;
    private ScrollWindow? scrollWindow;
    private readonly DesktopCapture captureProvider = new();
    private PinManager? pins;
    private readonly ConditionalWeakTable<EditDocument, SavedRevision> historyRevisions = new();
    private readonly SemaphoreSlim historyQueue = new(1, 1);
    private int historyJobs;
    private sealed class SavedRevision { public string Signature = ""; public int Version; }
    public Settings Settings { get; private set; } = Settings.Load();
    public HistoryStore History { get; } = new();
    public IReadOnlyList<HotkeyRegistrationException> HotkeyConflicts { get; private set; } = [];
    public static new App Current => (App)Application.Current;

    [STAThread] public static void Main(string[] args)
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var activationPipe = PipeName + "." + process.SessionId;
        instance = new Mutex(false, MutexName);
        try { ownsInstance = instance.WaitOne(0); }
        catch (AbandonedMutexException) { ownsInstance = true; }
        if (!ownsInstance)
        {
            if (!TryActivate(activationPipe, 800)) TryActivate(PipeName, 200);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) => { Log.Error("Unhandled UI", args.Exception); if (args.Exception is not OutOfMemoryException) { args.Handled = true; Ui.Error(args.Exception); } };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error("Background", args.Exception); args.SetObserved(); };
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Snapaste;component/UI/Theme.xaml", UriKind.Relative) });
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _ = Listen(activationPipe, lifetime.Token);
        bool firstRun = !Core.Settings.Exists;
        if (firstRun)
        {
            try { Settings.Save(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Log.Error("Initial settings save", error); }
        }
        SetupTray();
        pins = new PinManager();
        hotkeys = new Hotkeys();
        ConfigureHotkeys();
        if (Settings.RestorePins) pins.Restore();
        PruneSessions();
        _ = TrimHistoryAsync(Settings);
        if (firstRun || e.Args.Contains("--settings")) ShowSettings();
    }

    private static bool TryActivate(string name, int timeout)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(timeout);
            return true;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException) { return false; }
    }

    private async Task Listen(string name, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using (var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                _ = Dispatcher.BeginInvoke(ShowSettings);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Error("Single instance", e);
                try { await Task.Delay(500, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private void PruneSessions()
    {
        try
        {
            var directories = new DirectoryInfo(Paths.Sessions).EnumerateDirectories()
                .Where(d => Guid.TryParseExact(d.Name, "N", out _) && (d.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderBy(d => d.LastWriteTimeUtc)
                .Select(d => (Directory: d, Bytes: d.EnumerateFiles().Sum(f => f.Length))).ToArray();
            long total = directories.Sum(d => d.Bytes);
            foreach (var (directory, bytes) in directories)
            {
                if (directory.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-7) && total <= (long)Settings.HistoryMegabytes * 1024 * 1024) continue;
                directory.Delete(true);
                total -= bytes;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error("Session cache cleanup", e); }
    }

    private void SetupTray()
    {
        trayMenu = new Forms.ContextMenuStrip();
        void Add(string key, Action action) => trayMenu.Items.Add(Strings.T(key), null, (_, _) => Dispatcher.BeginInvoke(action));
        Add("Capture", () => Capture(false)); Add("Scroll", () => Capture(true)); Add("Pin", PinClipboard);
        Add("History", ShowHistory); trayMenu.Items.Add(new Forms.ToolStripSeparator());
        Add("ShowPins", ShowPins); Add("HidePins", HidePins); Add("RecoverPins", RecoverPins);
        Add("ClosePins", () => pins?.CloseAll());
        Add("Settings", ShowSettings); Add("Exit", ExitSafely);
        trayIcon = BrandIcon.Tray();
        tray = new Forms.NotifyIcon { Text = TrayText(), Icon = trayIcon, ContextMenuStrip = trayMenu, Visible = true };
        tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(() => Capture(false));
    }

    private string TrayText()
    {
        if (HotkeyConflicts.Count > 0) return "Snapaste · 有快捷键未注册 · 右键打开设置";
        var text = $"Snapaste · {Settings.CaptureKey} 截图 · {Settings.PinKey} 贴图";
        return text.Length <= 63 ? text : "Snapaste · 截图和贴图";
    }

    private (string Key, Action Action)[] HotkeyBindings(Settings settings) =>
        [(settings.CaptureKey, () => Capture(false)), (settings.PinKey, PinClipboard), (settings.ScrollKey, () => Capture(true)), (settings.RecoverKey, RecoverPins)];

    public void ConfigureHotkeys()
    {
        HotkeyConflicts = hotkeys?.ConfigureAvailable(HotkeyBindings(Settings)) ?? [];
        if (tray != null) tray.Text = TrayText();
        if (HotkeyConflicts.Count == 0) return;
        foreach (var conflict in HotkeyConflicts) Log.Error("Hotkey registration", conflict);
        var gestures = string.Join("、", HotkeyConflicts.Select(conflict => conflict.Gesture));
        tray?.ShowBalloonTip(4500, "Snapaste", $"快捷键 {gestures} 不可用，请打开设置修改。也可从托盘菜单截图或贴图。", Forms.ToolTipIcon.Warning);
    }

    public void ShowSettings()
    {
        if (exiting) return;
        if (settingsWindow == null) { settingsWindow = new SettingsWindow(); settingsWindow.Closed += (_, _) => settingsWindow = null; }
        settingsWindow.Show(); if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal; settingsWindow.Activate();
    }

    public void ApplySettings(Settings settings)
    {
        if (hotkeys != null && HotkeysChanged(settings.CaptureKey, settings.PinKey, settings.ScrollKey, settings.RecoverKey))
        {
            hotkeys.Configure(HotkeyBindings(settings), () => SaveSettings(settings));
            HotkeyConflicts = [];
        }
        else SaveSettings(settings);
        Settings = settings;
        ApplyTheme();
        if (tray != null) tray.Text = TrayText();
        _ = TrimHistoryAsync(settings);
    }

    internal bool HotkeysChanged(string? capture, string? pin, string? scroll, string? recover) =>
        !SameHotkey(capture, Settings.CaptureKey) || !SameHotkey(pin, Settings.PinKey) ||
        !SameHotkey(scroll, Settings.ScrollKey) || !SameHotkey(recover, Settings.RecoverKey);

    internal bool HotkeyUnavailable(string? gesture) => HotkeyConflicts.Any(conflict => SameHotkey(gesture, conflict.Gesture));

    private static bool SameHotkey(string? first, string? second) =>
        string.Equals(first?.Trim() ?? "", second?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);

    private static void SaveSettings(Settings settings)
    {
        const string runPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string name = "Snapaste";
        using var run = Registry.CurrentUser.OpenSubKey(runPath, writable: true)
            ?? (settings.StartWithWindows ? Registry.CurrentUser.CreateSubKey(runPath) : null);
        var previous = run?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var next = settings.StartWithWindows ? $"\"{Environment.ProcessPath}\"" : null;
        if (run == null || Equals(previous, next))
        {
            settings.Save();
            return;
        }

        var previousKind = previous == null ? RegistryValueKind.None : run.GetValueKind(name);
        if (next == null) run.DeleteValue(name, false);
        else run.SetValue(name, next, RegistryValueKind.String);

        try { settings.Save(); }
        catch
        {
            try
            {
                if (previous == null) run.DeleteValue(name, false);
                else run.SetValue(name, previous, previousKind);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { Log.Error("Startup setting rollback", e); }
            throw;
        }
    }

    private void ApplyTheme()
    {
        try { Ui.Theme(Settings.Theme); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Error("System appearance", e);
            Ui.Theme("Light");
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        => Dispatcher.BeginInvoke(() => { if (Settings.Theme == "System") ApplyTheme(); });

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(() => pins?.EnsureReachable());

    public void Capture(bool scroll)
    {
        if (exiting || overlay != null || scrollWindow != null) return;
        CaptureOverlay? attempted = null;
        try
        {
            var bounds = Native.Desktop;
            var bitmap = captureProvider.Capture(bounds, Settings.IncludeCursor);
            var captureOverlay = new CaptureOverlay(bitmap, bounds, Settings, scroll);
            overlay = attempted = captureOverlay;
            captureOverlay.Closed += (_, _) => overlay = null;
            captureOverlay.Accepted += async (rect, doc, action, target) =>
            {
                try
                {
                    if (action == "Scroll")
                    {
                        if (rect.Height < 100 || rect.Width < 80) throw new InvalidOperationException("滚动选区至少需要 80 × 100 像素。");
                        if (target == 0) { var center = new Native.POINT((int)(rect.X + rect.Width / 2), (int)(rect.Y + rect.Height / 2)); target = Native.GetAncestor(Native.WindowFromPoint(center), 2); }
                        if (target != 0) Native.SetForegroundWindow(target);
                        var session = new ScrollSession(rect, target, Settings, captureProvider);
                        session.Finished += result => Edit(result);
                        try
                        {
                            scrollWindow = new ScrollWindow(session);
                            scrollWindow.Closed += (_, _) => scrollWindow = null;
                            scrollWindow.Show();
                            return true;
                        }
                        catch
                        {
                            scrollWindow = null;
                            session.Dispose();
                            throw;
                        }
                    }
                    if (action == "Copy") { await ImageExport.Copy(doc); Remember(doc); }
                    else if (action == "Save") { if (!await ImageExport.SaveDialogAsync(doc, Settings, captureOverlay)) return false; Remember(doc); }
                    else if (action == "Pin") { PinDocument(doc); Remember(doc); }
                    return true;
                }
                catch (Exception ex) { Ui.Error(ex, captureOverlay); return false; }
            };
            captureOverlay.Show();
        }
        catch (Exception e)
        {
            if (overlay == attempted) overlay = null;
            attempted?.Close();
            Log.Error("Capture failure", e);
            Ui.Error(e);
        }
    }

    public void Edit(ImageDocument image, Tool tool = Tool.Select)
    {
        var editor = new EditorWindow(image, tool); editor.PinRequested += PinDocument; editor.Completed += Remember; editor.Show();
    }

    public async void Remember(EditDocument doc)
    {
        try { await RememberAsync(doc); }
        catch (Exception e)
        {
            Log.Error("History save", e);
            Ui.Error(e);
        }
    }

    private async Task RememberAsync(EditDocument doc)
    {
        if (!Settings.HistoryEnabled) return;
        var signature = System.Text.Json.JsonSerializer.Serialize(new
        {
            doc.Crop,
            Objects = doc.Annotations.Select(a => new
            {
                a.Id, a.Kind, a.Bounds, a.Text, a.Color, a.Stroke, a.Font, a.FontSize, a.Bold,
                a.Italic, a.Underline, a.Alignment, a.TextIndent, a.Background, a.TextWidth,
                a.Rotation, a.FlipX, a.FlipY, a.EffectSize, a.Points
            })
        });
        var revision = historyRevisions.GetOrCreateValue(doc);
        if (revision.Signature == signature) return;
        var snapshot = doc.Fork();
        var configuration = Settings;
        revision.Signature = signature;
        int version = ++revision.Version;
        try
        {
            await RunHistoryAsync(() => History.Add(snapshot, configuration));
            if (!exiting) historyWindow?.Reload();
        }
        catch
        {
            if (revision.Version == version) revision.Signature = "";
            throw;
        }
    }

    private async Task RunHistoryAsync(Action action)
    {
        historyJobs++;
        await historyQueue.WaitAsync();
        try { await RenderWorker.Run(action); }
        finally { historyJobs--; historyQueue.Release(); }
    }

    public async Task ClearHistoryAsync()
    {
        await RunHistoryAsync(() => History.Clean(true));
        historyWindow?.Reload();
    }

    private async Task TrimHistoryAsync(Settings settings)
    {
        try
        {
            await RunHistoryAsync(() => History.Clean(false, settings));
            historyWindow?.Reload();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error("History retention", e); }
    }

    private async void ExitSafely()
    {
        if (exiting) return;
        exiting = true;
        if (historyJobs > 0) tray?.ShowBalloonTip(1500, "Snapaste", "正在保存本地状态…", Forms.ToolTipIcon.Info);
        while (historyJobs > 0) await Task.Delay(100);
        try { pins?.Exit(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            exiting = false;
            Log.Error("Pin state save on exit", e);
            Ui.Error(new IOException("无法保存贴图状态，Snapaste 仍在运行。请检查本地存储后重试退出。", e));
            return;
        }
        Shutdown();
    }

    public void PinDocument(EditDocument doc) => pins?.Add(doc.Raster(doc.Crop));

    public async void PinClipboard()
    {
        if (exiting || pasting || pins == null) return;
        pasting = true;
        var origin = Native.Cursor;
        try
        {
            BitmapSource? image = null;
            string? imageFile = null;
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    image = Clipboard.ContainsImage() ? Clipboard.GetImage() : null;
                    if (image == null && Clipboard.ContainsFileDropList())
                        imageFile = Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(path => ImageExtensions.Contains(Path.GetExtension(path)));
                    break;
                }
                catch (System.Runtime.InteropServices.COMException) when (attempt < 4) { await Task.Delay(60); }
            }
            if (imageFile != null)
                image = await RenderWorker.Run(() => ImageDocument.ReadBitmap(imageFile, maxPixels: PinManager.MaxImagePixels));
            if (image == null) { tray?.ShowBalloonTip(2000, "Snapaste", Strings.T("NoImage"), Forms.ToolTipIcon.Info); return; }
            if (!exiting) pins.Add(image, origin);
        }
        catch (Exception e) { Ui.Error(e); }
        finally { pasting = false; }
    }

    public void ShowHistory()
    {
        if (exiting) return;
        if (historyWindow == null) { historyWindow = new HistoryWindow(History); historyWindow.Closed += (_, _) => historyWindow = null; }
        else historyWindow.Reload();
        historyWindow.Show(); if (historyWindow.WindowState == WindowState.Minimized) historyWindow.WindowState = WindowState.Normal; historyWindow.Activate();
    }

    public void ShowPins() => pins?.ShowAll();
    public void HidePins() => pins?.HideAll();
    public void RecoverPins() => pins?.Recover();

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (e.Cancel) return;
        try { pins?.Save(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Log.Error("Pin state save on session end", error); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        lifetime.Cancel();
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        hotkeys?.Dispose();
        tray?.Dispose();
        trayMenu?.Dispose();
        trayIcon?.Dispose();
        if (ownsInstance) instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
    }
}
