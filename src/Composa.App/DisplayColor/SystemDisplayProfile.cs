using System.Runtime.InteropServices;
using System.Text;
using Composa.ColorManagement;
using Composa.Model;

namespace Composa.App.DisplayColor;

internal sealed record DisplayProfileInfo(RgbProfile Profile, string Description, string? Path = null);

/// <summary>Reads the system's legacy SDR destination; never installs profiles or calibration ramps.
/// Windows ACM returns no legacy ICC (interpreted as sRGB), avoiding a second display transform.</summary>
internal static class SystemDisplayProfile
{
    internal static DisplayProfileInfo Read(int x, int y, int width, int height)
    {
        if (!OperatingSystem.IsWindows()) return new(RgbProfile.Srgb, "System / sRGB · a custom display ICC can be selected");
        var bounds = new Rectangle { Left = x, Top = y, Right = x + Math.Max(1, width), Bottom = y + Math.Max(1, height) };
        var monitor = MonitorFromRect(ref bounds, 2); var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), DeviceName = "" };
        if (monitor == 0 || !GetMonitorInfoW(monitor, ref info)) return Fallback();
        var dc = CreateDCW("DISPLAY", info.DeviceName, 0, 0);
        if (dc == 0) return Fallback();
        try
        {
            uint length = 0; GetICMProfileW(dc, ref length, null);
            const int maxPathChars = 32768;
            if (length is 0 or > maxPathChars) return Fallback();
            var path = new StringBuilder((int)length);
            if (!GetICMProfileW(dc, ref length, path)) return Fallback();
            var profile = Load(path.ToString());
            return new(profile, "System display · " + profile.Name, path.ToString());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return new(RgbProfile.Srgb, "System display ICC unavailable · using sRGB"); }
        finally { DeleteDC(dc); }
    }

    internal static RgbProfile Load(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length > DocumentLimits.MaxIccBytes) throw new InvalidDataException("The display ICC exceeds the profile size limit.");
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes); return RgbProfile.FromIcc(bytes);
    }
    private static DisplayProfileInfo Fallback() => new(RgbProfile.Srgb, "System display · sRGB / automatic system color management");

    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size; public Rectangle Monitor, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }
    [DllImport("user32.dll")] private static extern nint MonitorFromRect(ref Rectangle bounds, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern nint CreateDCW(string driver, string device, nint output, nint mode);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetICMProfileW(nint dc, ref uint size, StringBuilder? path);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
}
