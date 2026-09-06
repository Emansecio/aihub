using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace AIHub;

internal static class FullScreenDetector
{
    internal static bool CoversMonitor(IntPtr window, IntPtr hub, Rectangle monitor)
    {
        if (window == IntPtr.Zero || window == hub || !NativeMethods.IsWindowVisible(window) || NativeMethods.IsIconic(window)) return false;
        // A maximized window with a title bar is ordinary desktop use, even with an auto-hidden taskbar.
        if ((NativeMethods.GetWindowStyle(window, -16) & 0x00C00000) != 0) return false;
        var name = new StringBuilder(256);
        NativeMethods.GetClassName(window, name, name.Capacity);
        if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (NativeMethods.DwmGetWindowAttribute(window, 9, out var bounds, Marshal.SizeOf<NativeMethods.Rect>()) != 0
            && !NativeMethods.GetWindowRect(window, out bounds)) return false;
        return bounds.Left <= monitor.Left + 2 && bounds.Top <= monitor.Top + 2
            && bounds.Right >= monitor.Right - 2 && bounds.Bottom >= monitor.Bottom - 2;
    }
}
