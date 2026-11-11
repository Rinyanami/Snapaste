using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Snapaste.Platform;
namespace Snapaste.Capture;

public interface ICaptureProvider { BitmapSource Capture(Rect physicalRectangle, bool cursor = false); }

// Screen DC reads the composited, visible desktop including popups and transparent windows.
public sealed class DesktopCapture : ICaptureProvider
{
    public BitmapSource Capture(Rect rect, bool cursor = false)
    {
        DesktopSession.EnsureAvailable();
        if (rect.Width < 1 || rect.Height < 1 || !double.IsFinite(rect.X) || !double.IsFinite(rect.Y) ||
            !double.IsFinite(rect.Width * rect.Height) || rect.Width * rect.Height > 150_000_000)
            throw new ArgumentException("截图区域无效或超出内存保护限制。", nameof(rect));

        int x = checked((int)rect.X), y = checked((int)rect.Y);
        using var bitmap = new Bitmap((int)rect.Width, (int)rect.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var screenDc = Native.GetDC(0);
            if (screenDc == 0) throw new InvalidOperationException("无法访问当前桌面，请检查屏幕是否已锁定或会话已断开。");
            try
            {
                var destinationDc = graphics.GetHdc();
                try
                {
                    if (!Native.BitBlt(destinationDc, 0, 0, bitmap.Width, bitmap.Height, screenDc, x, y, 0x40CC0020))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法读取桌面画面，请检查屏幕是否锁定或会话已断开。");
                    if (cursor) DrawCursor(destinationDc, x, y);
                }
                finally { graphics.ReleaseHdc(destinationDc); }
            }
            finally { Native.ReleaseDC(0, screenDc); }
        }

        DesktopSession.EnsureAvailable();
        var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Bgr32, null, data.Scan0, checked(data.Stride * bitmap.Height), data.Stride);
            source.Freeze();
            return source;
        }
        finally { bitmap.UnlockBits(data); }
    }

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
