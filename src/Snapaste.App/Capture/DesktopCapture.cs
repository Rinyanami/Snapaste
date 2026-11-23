using System.Runtime.InteropServices;
using Snapaste.Platform;
namespace Snapaste.Capture;

public interface ICaptureProvider { BitmapSource Capture(Rect physicalRectangle, bool cursor = false); }

// Screen DC reads the composited, visible desktop including popups and transparent windows.
public sealed class DesktopCapture : ICaptureProvider, IDisposable
{
    private const uint SrcCopy = 0x00CC0020;
    private readonly object gate = new();
    private int cachedWidth, cachedHeight;
    private nint memoryDc, dib, previous, pixels;

    public BitmapSource Capture(Rect rect, bool cursor = false)
    {
        DesktopSession.EnsureAvailable();
        if (rect.Width < 1 || rect.Height < 1 || !double.IsFinite(rect.X) || !double.IsFinite(rect.Y) ||
            !double.IsFinite(rect.Width * rect.Height) || rect.Width * rect.Height > 150_000_000)
            throw new ArgumentException("截图区域无效或超出内存保护限制。", nameof(rect));

        int x = checked((int)rect.X), y = checked((int)rect.Y);
        int width = (int)rect.Width, height = (int)rect.Height;
        var screenDc = Native.GetDC(0);
        if (screenDc == 0) throw new InvalidOperationException("无法访问当前桌面。");
        try
        {
            lock (gate) return ReadSurface(screenDc, x, y, width, height, cursor);
        }
        finally { Native.ReleaseDC(0, screenDc); }
    }

    private BitmapSource ReadSurface(nint screenDc, int x, int y, int width, int height, bool cursor)
    {
        int stride = checked(width * 4);
        EnsureBuffer(screenDc, width, height);
        try
        {
            if (!Native.BitBlt(memoryDc, 0, 0, width, height, screenDc, x, y, SrcCopy))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法读取桌面画面，请检查屏幕是否锁定或会话已断开。");
            if (cursor) DrawCursor(memoryDc, x, y);
            GdiFlush();
            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, checked(stride * height), stride);
            source.Freeze();
            return source;
        }
        catch
        {
            ReleaseBuffer();
            throw;
        }
    }

    private void EnsureBuffer(nint screenDc, int width, int height)
    {
        if (width == cachedWidth && height == cachedHeight && memoryDc != 0 && dib != 0 && pixels != 0) return;
        ReleaseBuffer();
        var header = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height,
            Planes = 1, BitCount = 32
        };
        try
        {
            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            dib = CreateDIBSection(screenDc, ref header, 0, out pixels, 0, 0);
            if (dib == 0 || pixels == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            previous = SelectObject(memoryDc, dib);
            if (previous == 0 || previous == -1) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            cachedWidth = width;
            cachedHeight = height;
        }
        catch
        {
            ReleaseBuffer();
            throw;
        }
    }

    private void ReleaseBuffer()
    {
        if (previous != 0 && previous != -1 && memoryDc != 0) SelectObject(memoryDc, previous);
        if (memoryDc != 0) DeleteDC(memoryDc);
        if (dib != 0) Native.DeleteObject(dib);
        memoryDc = dib = previous = pixels = 0;
        cachedWidth = cachedHeight = 0;
    }

    public void Dispose()
    {
        lock (gate) ReleaseBuffer();
    }

    // A top-down BI_RGB DIB gives BitBlt and WPF the same native BGR pixel layout.
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateDIBSection(nint dc, ref BitmapInfoHeader info, uint usage, out nint pixels, nint section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")]
    private static extern bool GdiFlush();

    private static void DrawCursor(nint dc, int x, int y)
    {
        var cursor = new Native.CURSORINFO { Size = Marshal.SizeOf<Native.CURSORINFO>() };
        if (!Native.GetCursorInfo(ref cursor) || cursor.Flags != 1 || !Native.GetIconInfo(cursor.Cursor, out var icon)) return;
        try { Native.DrawIconEx(dc, cursor.Position.X - x - icon.HotspotX, cursor.Position.Y - y - icon.HotspotY, cursor.Cursor, 0, 0, 0, 0, 3); }
        finally
        {
            if (icon.Mask != 0) Native.DeleteObject(icon.Mask);
            if (icon.Color != 0) Native.DeleteObject(icon.Color);
        }
    }
}
