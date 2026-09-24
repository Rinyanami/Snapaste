using System.Runtime.InteropServices;
using System.Windows.Interop;
namespace Snapaste.Platform;

public static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; public Rect Rect => new(Left, Top, Math.Max(0, Right - Left), Math.Max(0, Bottom - Top)); }
    [StructLayout(LayoutKind.Sequential)] public struct CURSORINFO { public int Size, Flags; public nint Cursor; public POINT Position; }
    [StructLayout(LayoutKind.Sequential)] public struct ICONINFO { public bool Icon; public int HotspotX, HotspotY; public nint Mask, Color; }
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern nint WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern nint GetAncestor(nint h, uint flags);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsWindow(nint h);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint h, out RECT rect);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint h, out uint process);
    public delegate bool EnumProc(nint h, nint p);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, nint p);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint h, int attribute, out RECT rect, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint h, int attribute, out int value, int size);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(nint h, int id, uint mods, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(nint h, int id);
    [DllImport("user32.dll")] public static extern nint GetWindowLongPtr(nint h, int index);
    [DllImport("user32.dll")] public static extern nint SetWindowLongPtr(nint h, int index, nint value);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(nint h, uint affinity);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint h);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint h);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int n);
    [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll")] public static extern bool GetIconInfo(nint icon, out ICONINFO info);
    [DllImport("user32.dll")] public static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(nint h);
    [DllImport("user32.dll")] public static extern nint GetDC(nint h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(nint h, nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] public static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] public static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint Type; public MOUSEINPUT Mouse; }
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    public static void Wheel(int delta) { var i = new INPUT { Type = 0, Mouse = new MOUSEINPUT { Data = unchecked((uint)delta), Flags = 0x800 } }; if (SendInput(1, [i], Marshal.SizeOf<INPUT>()) != 1) throw new InvalidOperationException("无法向目标程序发送滚轮；请使用手动滚动，或检查目标程序权限。"); }
    public static nint Handle(Window w) => new WindowInteropHelper(w).Handle;
    public static Rect Desktop => new(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
    public static Point Cursor { get { GetCursorPos(out var p); return new(p.X, p.Y); } }
    public static Rect Bounds(nint h) { if (DwmGetWindowAttribute(h, 9, out RECT r, Marshal.SizeOf<RECT>()) != 0) GetWindowRect(h, out r); return r.Rect; }
    public static void ExcludeFromCapture(Window w) => SetWindowDisplayAffinity(Handle(w), 0x11);
    public static void ClickThrough(Window w, bool enabled)
    {
        var h = Handle(w); var style = GetWindowLongPtr(h, -20).ToInt64();
        SetWindowLongPtr(h, -20, (nint)(enabled ? style | 0x20 | 0x80000 : style & ~0x20));
    }
    public static Point ToScreen(Visual v, Point dip) => v.PointToScreen(dip);
    public static Point ToWindow(Visual v, Point pixel) => v.PointFromScreen(pixel);
    public static Point PixelToDip(Point pixel, double dpi) => new(pixel.X * 96 / dpi, pixel.Y * 96 / dpi);
    public static void PlacePhysical(Window window, Rect pixels)
        => SetWindowPos(Handle(window), 0, (int)pixels.X, (int)pixels.Y, (int)pixels.Width, (int)pixels.Height, 0x14);
}
