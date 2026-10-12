using System.Runtime.InteropServices;
using Snapaste.UI;
namespace Snapaste.Platform;

public enum DesktopAvailability { Unknown, Available, Locked, Disconnected }

public static class DesktopSession
{
    // WTSINFOEX_LEVEL1 contains LARGE_INTEGER members: its union has 8-byte
    // alignment on Windows x64. Only the public status header is marshalled;
    // account names and the rest of the session data are never read or logged.
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct SessionDataHeader
    {
        [FieldOffset(0)] public uint SessionId;
        [FieldOffset(4)] public int ConnectionState;
        [FieldOffset(8)] public int SessionFlags;
        [FieldOffset(0)] private long alignment;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SessionInfoHeader { public uint Level; public SessionDataHeader Data; }
    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Query(nint server, uint session, int information, out nint buffer, out uint bytes);
    [DllImport("wtsapi32.dll", EntryPoint = "WTSFreeMemory")]
    private static extern void Free(nint buffer);

    public static DesktopAvailability Availability
    {
        get
        {
            // WTSSessionInfoEx = 25, WTS_CURRENT_SESSION = 0xffffffff.
            if (!Query(0, uint.MaxValue, 25, out var buffer, out var bytes)) return DesktopAvailability.Unknown;
            try
            {
                if (bytes < Marshal.SizeOf<SessionInfoHeader>()) return DesktopAvailability.Unknown;
                var header = Marshal.PtrToStructure<SessionInfoHeader>(buffer);
                if (header.Level != 1) return DesktopAvailability.Unknown;
                if (header.Data.ConnectionState is 4 or 8) return DesktopAvailability.Disconnected;
                return header.Data.SessionFlags switch { 0 => DesktopAvailability.Locked, 1 => DesktopAvailability.Available, _ => DesktopAvailability.Unknown };
            }
            finally { Free(buffer); }
        }
    }
    public static void EnsureAvailable()
    {
        var state = Availability;
        if (state is DesktopAvailability.Locked or DesktopAvailability.Disconnected) throw new DesktopUnavailableException(Strings.T(state == DesktopAvailability.Locked ? "DesktopLocked" : "DesktopDisconnected"));
    }
}
public sealed class DesktopUnavailableException(string message) : InvalidOperationException(message);
