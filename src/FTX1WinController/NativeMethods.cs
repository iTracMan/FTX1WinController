using System.Runtime.InteropServices;

namespace FTX1WinController;

/// Minimal user32 P/Invoke surface for enumerating monitors
/// (MainWindow.PositionOnSecondaryMonitorIfPresent) — deliberately not
/// System.Windows.Forms.Screen, since UseWindowsForms pulls in a parallel
/// set of Color/Point/Brush/Application/MouseEventArgs types that collide
/// with WPF's own across the whole project.
internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    public const int MONITORINFOF_PRIMARY = 0x1;

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
