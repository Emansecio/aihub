using System;
using System.Threading;
using System.Windows;

namespace AIHub;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        using var mutex = new Mutex(true, "Local\\AIHub.Desktop", out bool first);
        if (!first)
        {
            NativeMethods.PostMessage(NativeMethods.FindWindow(null, "AIHub"), NativeMethods.ShowHubMessage, IntPtr.Zero, IntPtr.Zero);
            return;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var settings = Settings.Load();
        app.Run(new HubWindow(AppCatalog.Discover(settings.CustomShortcuts), new AppLauncher(), settings));
        GC.KeepAlive(mutex);
    }
}
