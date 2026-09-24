using System.Windows.Interop;
namespace Snapaste.Platform;

public sealed class Hotkeys : IDisposable
{
    private readonly HwndSource source = new(new HwndSourceParameters("Snapaste.Hotkeys") { ParentWindow = new nint(-3) });
    private readonly Dictionary<int, Action> callbacks = new();
    public Hotkeys() => source.AddHook(Hook);
    public void Configure(IEnumerable<(string Key, Action Action)> bindings)
    {
        foreach (var id in callbacks.Keys) Native.UnregisterHotKey(source.Handle, id);
        callbacks.Clear(); int i = 1;
        foreach (var (gesture, action) in bindings)
        {
            var g = (KeyGesture?)new KeyGestureConverter().ConvertFromInvariantString(gesture) ?? throw new ArgumentException("无效快捷键: " + gesture);
            uint mods = 0x4000;
            if (g.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= 1;
            if (g.Modifiers.HasFlag(ModifierKeys.Control)) mods |= 2;
            if (g.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= 4;
            if (g.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= 8;
            if (!Native.RegisterHotKey(source.Handle, i, mods, (uint)KeyInterop.VirtualKeyFromKey(g.Key)))
            {
                Core.Log.Error("Hotkey conflict: " + gesture);
                throw new InvalidOperationException($"快捷键 {gesture} 注册失败，可能已被其他程序占用。请在设置中更改。");
            }
            callbacks[i++] = action;
        }
    }
    private nint Hook(nint h, int msg, nint w, nint l, ref bool handled) { if (msg == 0x312 && callbacks.TryGetValue((int)w, out var a)) { handled = true; a(); } return 0; }
    public void Dispose() { foreach (var id in callbacks.Keys) Native.UnregisterHotKey(source.Handle, id); source.Dispose(); }
}
