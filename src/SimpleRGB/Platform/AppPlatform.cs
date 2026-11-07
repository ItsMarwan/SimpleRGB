using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using SimpleRGB.Core.Diagnostics;

namespace SimpleRGB.Platform;

/// <summary>Only one SimpleRGB may drive the hardware. A second launch just brings the first one forward.</summary>
internal sealed class SingleInstance : IDisposable
{
    private static string _suffix = "";
    private static string MutexName => @"Local\SimpleRGB.SingleInstance" + _suffix;
    private static string ShowEventName => @"Local\SimpleRGB.ShowWindow" + _suffix;

    /// <summary>Preview mode uses its own lock so it can run next to the real app.</summary>
    public static void UseSuffix(string suffix) => _suffix = suffix;
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _registration;

    public bool TryAcquire()
    {
        try
        {
            _mutex = new Mutex(true, MutexName, out var createdNew);
            if (!createdNew)
            {
                _mutex.Dispose();
                _mutex = null;
                return false;
            }
        }
        catch (UnauthorizedAccessException)
        {
            return false; // held by an elevated instance
        }

        // Everyone may signal "show window", so a normal launch can wake an elevated instance.
        var security = new EventWaitHandleSecurity();
        security.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify, AccessControlType.Allow));
        _showEvent = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, ShowEventName, out _, security);
        return true;
    }

    public void ListenForShow(Action show)
    {
        if (_showEvent == null) return;
        _registration = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => show(), null, Timeout.Infinite, false);
    }

    public static void SignalExistingInstance()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(ShowEventName);
            ev.Set();
        }
        catch (Exception ex)
        {
            Log.Warn("App", $"Could not reach the running instance: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _showEvent?.Dispose();
        if (_mutex != null)
        {
            try { _mutex.ReleaseMutex(); } catch { /* not owned */ }
            _mutex.Dispose();
            _mutex = null;
        }
    }
}

internal static class Elevation
{
    /// <summary>Starts an elevated copy of SimpleRGB. Returns false if the user declined the UAC prompt.</summary>
    public static bool RestartElevated()
    {
        try
        {
            var exe = Environment.ProcessPath!;
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"--wait-for-pid {Environment.ProcessId}",
            });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // cancelled by the user
        {
            return false;
        }
    }

    /// <summary>Runs a program elevated (UAC prompt) and waits for it. Returns its exit code or null if declined.</summary>
    public static int? RunElevated(string file, string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (p == null) return null;
            p.WaitForExit(30_000);
            return p.HasExited ? p.ExitCode : null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return null;
        }
    }
}

/// <summary>
/// "Start with Windows" through a Task Scheduler logon task with highest privileges, so RAM lighting
/// works at login without a UAC prompt. The task never times out and runs on battery too.
/// </summary>
internal static class Autostart
{
    private const string TaskName = "SimpleRGB";

    public static bool IsEnabled()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{TaskName}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p == null) return false;
            p.WaitForExit(10_000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    public static bool SetEnabled(bool enabled)
    {
        string args;
        string? xmlPath = null;
        if (enabled)
        {
            xmlPath = Path.Combine(Path.GetTempPath(), $"SimpleRGB-task-{Guid.NewGuid():N}.xml");
            File.WriteAllText(xmlPath, BuildTaskXml(), Encoding.Unicode);
            args = $"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F";
        }
        else
        {
            args = $"/Delete /TN \"{TaskName}\" /F";
        }

        try
        {
            int? exit;
            if (Core.Platform.SystemInfo.IsElevated)
            {
                using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false });
                p!.WaitForExit(15_000);
                exit = p.ExitCode;
            }
            else
            {
                exit = Elevation.RunElevated("schtasks.exe", args);
            }
            var ok = exit == 0 || (!enabled && !IsEnabled());
            Log.Info("App", $"Start with Windows {(enabled ? "enable" : "disable")}: {(ok ? "ok" : $"failed ({exit?.ToString() ?? "declined"})")}");
            return ok;
        }
        finally
        {
            if (xmlPath != null) try { File.Delete(xmlPath); } catch { /* ignore */ }
        }
    }

    private static string BuildTaskXml()
    {
        var user = WindowsIdentity.GetCurrent().Name;
        var exe = Environment.ProcessPath!;
        string Esc(string s) => System.Security.SecurityElement.Escape(s)!;
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts SimpleRGB at login to restore your lighting.</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{Esc(user)}</UserId></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author"><UserId>{Esc(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>{Esc(exe)}</Command><Arguments>--autostart</Arguments></Exec>
              </Actions>
            </Task>
            """;
    }
}

internal static class WindowTheme
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Dark title bar (Windows 10 2004+ / 11) and Mica-style backdrop on Windows 11.</summary>
    public static void ApplyDark(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var on = 1;
        _ = DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        var round = 2;
        _ = DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE = round
    }
}
