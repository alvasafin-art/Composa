using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Composa.App;

internal static class WindowActivation
{
    internal static void Show(Window window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show(); window.Activate();
        if (OperatingSystem.IsWindows() && window.TryGetPlatformHandle() is { Handle: var handle } && handle != IntPtr.Zero)
        { if (IsIconic(handle)) ShowWindow(handle, 9); SetForegroundWindow(handle); }
    }

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
}
