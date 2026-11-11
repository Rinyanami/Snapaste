using System.Windows.Interop;
namespace Snapaste.Platform;

public sealed class HotkeyRegistrationException(string gesture, string message, Exception? cause = null) : InvalidOperationException(message, cause)
{
    public string Gesture { get; } = gesture;
}

public sealed class Hotkeys : IDisposable
{
    private const uint NoRepeat = 0x4000;
    internal readonly record struct Gesture(uint Modifiers, uint Key);
    private sealed record Registration(int Id, Gesture Gesture, Action Action);
    private readonly HwndSource source = new(new HwndSourceParameters("Snapaste.Hotkeys") { ParentWindow = new nint(-3) });
    private Dictionary<int, Registration> active = new();

    public Hotkeys() => source.AddHook(Hook);

    public void Configure(IEnumerable<(string Key, Action Action)> bindings, Action? beforeCommit = null)
        => Apply(bindings, false, beforeCommit);

    public IReadOnlyList<HotkeyRegistrationException> ConfigureAvailable(IEnumerable<(string Key, Action Action)> bindings)
        => Apply(bindings, true, null);

    private IReadOnlyList<HotkeyRegistrationException> Apply(IEnumerable<(string Key, Action Action)> bindings, bool allowPartial, Action? beforeCommit)
    {
        var existing = active.Values.ToDictionary(registration => registration.Gesture);
        var usedIds = active.Keys.ToHashSet();
        var usedGestures = new HashSet<Gesture>();
        var next = new Dictionary<int, Registration>();
        var staged = new List<int>();
        var failures = new List<HotkeyRegistrationException>();
        int id = 1;

        try
        {
            foreach (var (text, action) in bindings)
            {
                try
                {
                    var gesture = Parse(text);
                    if (!usedGestures.Add(gesture)) throw new HotkeyRegistrationException(text, $"快捷键 {text} 重复，请为每个操作设置不同的组合键。");
                    if (existing.TryGetValue(gesture, out var registration))
                    {
                        next.Add(registration.Id, registration with { Action = action });
                        continue;
                    }
                    while (!usedIds.Add(id)) id++;
                    if (!Native.RegisterHotKey(source.Handle, id, gesture.Modifiers | NoRepeat, gesture.Key))
                        throw new HotkeyRegistrationException(text, $"快捷键 {text} 注册失败，可能已被其他程序或 Windows 占用。请更改后重试。");
                    staged.Add(id);
                    next.Add(id, new Registration(id, gesture, action));
                }
                catch (HotkeyRegistrationException e) when (allowPartial) { failures.Add(e); }
            }
            beforeCommit?.Invoke();
        }
        catch
        {
            foreach (int added in staged) Native.UnregisterHotKey(source.Handle, added);
            throw;
        }

        foreach (int old in active.Keys)
            if (!next.ContainsKey(old)) Native.UnregisterHotKey(source.Handle, old);
        active = next;
        return failures;
    }

    internal static Gesture Parse(string text)
    {
        try
        {
            var parsed = (KeyGesture?)new KeyGestureConverter().ConvertFromInvariantString(text);
            if (parsed == null) throw new ArgumentException();
            int key = KeyInterop.VirtualKeyFromKey(parsed.Key);
            if (key == 0) throw new ArgumentException();
            uint mods = 0;
            if (parsed.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= 1;
            if (parsed.Modifiers.HasFlag(ModifierKeys.Control)) mods |= 2;
            if (parsed.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= 4;
            if (parsed.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= 8;
            return new(mods, (uint)key);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or FormatException)
        {
            throw new HotkeyRegistrationException(text, $"快捷键 {text} 无效，请使用类似 F1 或 Ctrl+Alt+F3 的格式。", e);
        }
    }

    private nint Hook(nint h, int msg, nint w, nint l, ref bool handled)
    {
        if (msg == 0x312 && active.TryGetValue((int)w, out var registration))
        {
            uint data = unchecked((uint)l.ToInt64());
            if (registration.Gesture.Key == data >> 16 && registration.Gesture.Modifiers == (data & 0xf))
            {
                handled = true;
                registration.Action();
            }
        }
        return 0;
    }

    public void Dispose()
    {
        foreach (int id in active.Keys) Native.UnregisterHotKey(source.Handle, id);
        active.Clear();
        source.Dispose();
    }
}
