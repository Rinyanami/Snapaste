using System.Text.Json;
namespace Snapaste.Core;

public sealed class Settings
{
    public string CaptureKey { get; set; } = "F1";
    public string PinKey { get; set; } = "F3";
    public string RecoverKey { get; set; } = "Ctrl+Alt+F3";
    public string ScrollKey { get; set; } = "Ctrl+F1";
    public bool AutoWindow { get; set; } = true;
    public bool UiElements { get; set; } = true;
    public bool Magnifier { get; set; } = true;
    public bool IncludeCursor { get; set; }
    public bool RestorePins { get; set; } = true;
    public bool PinTopmost { get; set; } = true;
    public double PinOpacity { get; set; } = 1;
    public double PinScale { get; set; } = 1;
    public double MinPinScale { get; set; } = .1;
    public double MaxPinScale { get; set; } = 8;
    public bool HistoryEnabled { get; set; } = true;
    public int HistoryCount { get; set; } = 200;
    public int HistoryMegabytes { get; set; } = 1024;
    public int StableMilliseconds { get; set; } = 200;
    public double MatchSensitivity { get; set; } = .78;
    public int MaxScrollFrames { get; set; } = 200;
    public int AutoScrollDelay { get; set; } = 420;
    public bool AutoScrollDefault { get; set; }
    public bool ScrollPreview { get; set; } = true;
    public string SaveDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    public string DefaultFormat { get; set; } = "PNG";
    public string CompletionAction { get; set; } = "Copy";
    public string Theme { get; set; } = "Light";
    public string Language { get; set; } = "zh-CN";
    public bool StartWithWindows { get; set; }
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static bool Exists => File.Exists(FilePath);
    private static string FilePath => Path.Combine(Paths.Data, "settings.json");
    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save() => AtomicFile.Write(FilePath, JsonSerializer.Serialize(this, Json));
}
public static class Paths
{
    public static string Data { get; } = Create(Environment.GetEnvironmentVariable("SNAPASTE_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snapaste"));
    public static string History => Create(Path.Combine(Data, "history"));
    public static string Pins => Create(Path.Combine(Data, "pins"));
    public static string Sessions => Create(Path.Combine(Data, "sessions"));
    private static string Create(string path) { Directory.CreateDirectory(path); return path; }
}
public static class AtomicFile
{
    public static void Write(string path, string data) => Write(path, stream =>
    {
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.Write(data);
    });

    public static void Write(string path, Action<Stream> write)
    {
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path) ?? throw new ArgumentException("无法确定文件保存目录。", nameof(path));
        var temp = Path.Combine(directory, $".snapaste-{Guid.NewGuid():N}.tmp");
        bool created = false;
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                write(stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
            created = false;
        }
        finally
        {
            if (created)
            {
                try { File.Delete(temp); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error("Temporary file cleanup", e); }
            }
        }
    }
}
public static class Log
{
    private static readonly object Gate = new();
    public static void Error(string category, Exception? error = null)
    {
        try { lock (Gate) { var p = Path.Combine(Paths.Data, "snapaste.log"); if (File.Exists(p) && new FileInfo(p).Length > 2_000_000) File.Move(p, p + ".old", true); File.AppendAllText(p, $"{DateTimeOffset.Now:O} {category} {error?.GetType().Name}: {error?.Message}\n{error?.StackTrace}\n"); } }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
