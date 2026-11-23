namespace Snapaste.UI;

internal static class BrandIcon
{
    private static readonly Uri Resource = new("/Snapaste;component/Assets/Brand/App.ico", UriKind.Relative);
    private static BitmapSource? source;

    public static BitmapSource Source
    {
        get
        {
            if (source != null) return source;
            using var icon = Load(new System.Drawing.Size(32, 32));
            source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
    }

    public static System.Drawing.Icon Tray() => Load(System.Windows.Forms.SystemInformation.SmallIconSize);

    private static System.Drawing.Icon Load(System.Drawing.Size size)
    {
        using var stream = Application.GetResourceStream(Resource).Stream;
        using var icon = new System.Drawing.Icon(stream, size);
        return (System.Drawing.Icon)icon.Clone();
    }
}
