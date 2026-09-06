using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIHub;

public sealed record AppEntry(string Id, string Name, string Monogram, string Target, string? Executable, ImageSource? Icon, bool ActivateExisting = true);

public static class AppCatalog
{
    public static IReadOnlyList<AppEntry> Discover(IEnumerable<CustomShortcut>? customShortcuts = null)
    {
        string? codexExe = FindRunningExecutable("ChatGPT");
        string? terminalExe = FindRunningExecutable("WindowsTerminal");
        var entries = new List<AppEntry>
        {
            new AppEntry("codex", "Codex / GPT", "C", "shell:AppsFolder\\OpenAI.Codex_2p2nqsd0c76g0!App", codexExe, LoadIcon(codexExe)),
            FromShortcut("cursor", "Cursor", "↗", "Cursor.lnk", "Cursor"),
            FromShortcut("hermes", "Hermes", "H", "Hermes.lnk"),
            FromShortcut("grok", "Grok Bot", "G", "Grok Bot.lnk"),
            new AppEntry("terminal", "Terminal", ">_", "shell:AppsFolder\\Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", terminalExe, LoadIcon(terminalExe), ActivateExisting: false)
        };
        foreach (var shortcut in customShortcuts ?? Array.Empty<CustomShortcut>())
        {
            if (shortcut == null || string.IsNullOrWhiteSpace(shortcut.Id) || string.IsNullOrWhiteSpace(shortcut.Path) || entries.Any(e => e.Id == shortcut.Id)) continue;
            try { entries.Add(FromFile(shortcut.Id, shortcut.Name, shortcut.Path)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or COMException)
            {
                // Keep missing shortcuts visible so the user can remove them or see an opening error.
                entries.Add(new AppEntry(shortcut.Id, shortcut.Name, "↗", ex is ArgumentException ? "" : shortcut.Path, null, null));
            }
        }
        return entries;
    }

    private static string? FindRunningExecutable(string processName)
    {
        string? executable = null;
        foreach (var process in Process.GetProcessesByName(processName))
            using (process)
                try { executable ??= process.MainModule?.FileName; }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        return executable;
    }

    private static AppEntry FromShortcut(string id, string name, string monogram, string file, string? subfolder = null)
    {
        string[] roots = { Environment.GetFolderPath(Environment.SpecialFolder.Programs), Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) };
        foreach (string root in roots)
        {
            foreach (string path in new[] { Path.Combine(root, file), Path.Combine(root, subfolder ?? name, file) })
            {
                if (!File.Exists(path)) continue;
                try { return FromFile(id, name, path) with { Monogram = monogram, ActivateExisting = true }; }
                catch (COMException) { return new AppEntry(id, name, monogram, path, null, null); }
            }
        }
        return new AppEntry(id, name, monogram, "", null, null);
    }

    public static AppEntry FromFile(string id, string name, string path)
    {
        path = Path.GetFullPath(path);
        string extension = Path.GetExtension(path);
        if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Escolha um aplicativo (.exe) ou um atalho do Windows (.lnk).");
        if (!File.Exists(path)) throw new FileNotFoundException("O aplicativo ou atalho não foi encontrado.", path);
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)) return new AppEntry(id, name, "↗", path, path, LoadIcon(path));
        object? shell = null, shortcut = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            shortcut = ((dynamic)shell!).CreateShortcut(path);
            string target = ((dynamic)shortcut).TargetPath;
            // Launch the original .lnk, preserving its arguments and working directory.
            // Custom shortcuts may intentionally open another profile or workspace.
            return new AppEntry(id, name, "↗", path, target, LoadIcon(target), ActivateExisting: false);
        }
        finally
        {
            if (shortcut != null) Marshal.FinalReleaseComObject(shortcut);
            if (shell != null) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static ImageSource? LoadIcon(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(40, 40));
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is ArgumentException or System.ComponentModel.Win32Exception or IOException) { return null; }
    }
}

public interface IAppLauncher { void Launch(AppEntry entry); }

public sealed class AppLauncher : IAppLauncher
{
    public void Launch(AppEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Target)) throw new FileNotFoundException($"Atalho de {entry.Name} não encontrado no menu Iniciar.");
        if (entry.ActivateExisting && entry.Executable != null)
        {
            bool activated = false;
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(entry.Executable)))
            {
                using (p)
                {
                    try
                    {
                        if (activated) continue; // Still dispose every Process returned by the enumeration.
                        if (p.MainWindowHandle == IntPtr.Zero || !string.Equals(p.MainModule?.FileName, entry.Executable, StringComparison.OrdinalIgnoreCase)) continue;
                        if (NativeMethods.IsIconic(p.MainWindowHandle)) NativeMethods.ShowWindow(p.MainWindowHandle, 9);
                        activated = NativeMethods.SetForegroundWindow(p.MainWindowHandle);
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                }
            }
            if (activated) return;
        }
        if (entry.Target.StartsWith("shell:AppsFolder\\", StringComparison.Ordinal))
        {
            // ShellExecute resolves the registered package independently of versioned WindowsApps paths.
            using var process = Process.Start(new ProcessStartInfo(entry.Target) { UseShellExecute = true });
        }
        else
        {
            if (!File.Exists(entry.Target)) throw new FileNotFoundException($"O atalho de {entry.Name} foi movido ou removido.");
            if (!string.IsNullOrEmpty(entry.Executable) && !File.Exists(entry.Executable)) throw new FileNotFoundException($"O executável de {entry.Name} não foi encontrado.");
            using var process = Process.Start(new ProcessStartInfo(entry.Target) { UseShellExecute = true });
        }
    }
}
