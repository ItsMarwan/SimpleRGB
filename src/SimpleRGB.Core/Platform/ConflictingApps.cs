using System.Diagnostics;
using Microsoft.Win32;
using SimpleRGB.Core.Diagnostics;

namespace SimpleRGB.Core.Platform;

/// <summary>A program's "start with Windows" entry in a Run key, saved so it can be put back exactly.</summary>
public sealed record StartupEntry(string Hive, string KeyPath, string Name, string Command);

/// <summary>Helps with other programs that drive the same hardware (e.g. TRCC for Thermalright displays).</summary>
public static class ConflictingApps
{
    private static readonly (RegistryKey Root, string Hive, string Path)[] RunKeys =
    {
        (Registry.CurrentUser, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run"),
        (Registry.LocalMachine, "HKLM", @"Software\Microsoft\Windows\CurrentVersion\Run"),
        (Registry.LocalMachine, "HKLM", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
    };

    public static string ProcessName(string exe) => exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;

    public static bool IsRunning(string exe)
    {
        var procs = Process.GetProcessesByName(ProcessName(exe));
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }

    /// <summary>Asks the program to close, then ends it if it doesn't. Returns true when it's gone.</summary>
    public static bool Close(string exe)
    {
        var ok = true;
        foreach (var p in Process.GetProcessesByName(ProcessName(exe)))
        {
            try
            {
                if (p.CloseMainWindow() && p.WaitForExit(3000)) continue;
                p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
            }
            catch (Exception ex)
            {
                ok = false;
                Log.Warn("Conflicts", $"Could not close {exe}: {ex.Message}");
            }
            finally { p.Dispose(); }
        }
        Log.Info("Conflicts", $"Closed {exe}: {ok}");
        return ok && !IsRunning(exe);
    }

    public static StartupEntry? FindStartupEntry(string exe)
    {
        foreach (var (root, hive, path) in RunKeys)
        {
            try
            {
                using var key = root.OpenSubKey(path);
                if (key == null) continue;
                foreach (var name in key.GetValueNames())
                    if (key.GetValue(name) is string cmd && cmd.Contains(exe, StringComparison.OrdinalIgnoreCase))
                        return new StartupEntry(hive, path, name, cmd);
            }
            catch { /* not readable */ }
        }
        return null;
    }

    /// <summary>Removes the Run entry (needs admin for HKLM). The caller keeps the returned entry to restore it.</summary>
    public static bool DisableStartup(StartupEntry entry)
    {
        try
        {
            using var key = Root(entry.Hive).OpenSubKey(entry.KeyPath, writable: true);
            if (key == null) return false;
            key.DeleteValue(entry.Name, throwOnMissingValue: false);
            Log.Info("Conflicts", $"Removed startup entry {entry.Hive}\\{entry.KeyPath}\\{entry.Name}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Conflicts", $"Could not change startup entry: {ex.Message}");
            return false;
        }
    }

    public static bool RestoreStartup(StartupEntry entry)
    {
        try
        {
            using var key = Root(entry.Hive).CreateSubKey(entry.KeyPath, writable: true);
            key.SetValue(entry.Name, entry.Command, RegistryValueKind.String);
            Log.Info("Conflicts", $"Restored startup entry {entry.Name}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Conflicts", $"Could not restore startup entry: {ex.Message}");
            return false;
        }
    }

    private static RegistryKey Root(string hive) => hive == "HKCU" ? Registry.CurrentUser : Registry.LocalMachine;
}
