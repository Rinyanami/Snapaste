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
        if (rect.Width < 1 || rect.Height < 1 || rect.Width * rect.Height > 150_000_000) throw new ArgumentException("截图区域无效或超出内存保护限制。");
        using var bitmap = new Bitmap((int)rect.Width, (int)rect.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            var screenDc = Native.GetDC(0); var destinationDc = g.GetHdc();
            try
            {
                if (!Native.BitBlt(destinationDc, 0, 0, bitmap.Width, bitmap.Height, screenDc, (int)rect.X, (int)rect.Y, 0x40CC0020))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法读取桌面画面，请检查屏幕是否锁定或会话已断开。");
            }
            finally { g.ReleaseHdc(destinationDc); Native.ReleaseDC(0, screenDc); }
            if (cursor)
            {
                var ci = new Native.CURSORINFO { Size = Marshal.SizeOf<Native.CURSORINFO>() };
                if (Native.GetCursorInfo(ref ci) && ci.Flags == 1 && Native.GetIconInfo(ci.Cursor, out var ii))
                {
                    var dc = g.GetHdc();
                    try { Native.DrawIconEx(dc, ci.Position.X - (int)rect.X - ii.HotspotX, ci.Position.Y - (int)rect.Y - ii.HotspotY, ci.Cursor, 0, 0, 0, 0, 3); }
                    finally { g.ReleaseHdc(dc); if (ii.Mask != 0) Native.DeleteObject(ii.Mask); if (ii.Color != 0) Native.DeleteObject(ii.Color); }
                }
            }
        }
        DesktopSession.EnsureAvailable();
        var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try { var source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Bgr32, null, data.Scan0, data.Stride * bitmap.Height, data.Stride); source.Freeze(); return source; }
        finally { bitmap.UnlockBits(data); }
    }
}
