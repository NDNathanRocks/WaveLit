using System.IO;
using Microsoft.Win32;
using Serilog;
using Windows.ApplicationModel;

namespace Wavelit.App.Services;

/// <summary>Why start-with-Windows isn't on even though the user asked for it (null = it is on, or off by choice).</summary>
public enum StartupProblem { None, DisabledByWindows, Failed }

/// <summary>
/// Start-with-Windows. Unpackaged builds use the per-user Run key. The Store (MSIX) build uses the package's
/// StartupTask (packaging/Package.appxmanifest), which Windows lists under Settings → Apps → Startup and the user
/// can veto there; the Run key is write-virtualized for packaged apps, so it would never be read by Explorer.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Wavelit";
    /// <summary>Must match the TaskId in packaging/Package.appxmanifest.</summary>
    public const string TaskId = "WavelitStartup";
    public const string BackgroundArg = "--background";

    public static bool IsPackaged => Packaging.IsPackaged;

    /// <summary>Applies the user's choice; reports why it couldn't take effect, if it couldn't.</summary>
    public static async Task<StartupProblem> ApplyAsync(bool enabled)
    {
        try
        {
            return IsPackaged ? await ApplyStartupTaskAsync(enabled) : ApplyRunKey(enabled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                       or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            Log.Warning(ex, "Start with Windows could not be changed");
            return StartupProblem.Failed;
        }
    }

    private static StartupProblem ApplyRunKey(bool enabled)
    {
        string? exe = Environment.ProcessPath;
        if (exe is null) return StartupProblem.Failed;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(ValueName, $"\"{exe}\" {BackgroundArg}");
        else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName, throwOnMissingValue: false);
        return StartupProblem.None;
    }

    // The StartupTask starts the app with no arguments, so App.OnStartup treats a StartupTask activation as --background.
    private static async Task<StartupProblem> ApplyStartupTaskAsync(bool enabled)
    {
        var task = await StartupTask.GetAsync(TaskId);
        if (!enabled)
        {
            if (task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy) task.Disable();
            return StartupProblem.None;
        }
        var state = task.State == StartupTaskState.Disabled ? await task.RequestEnableAsync() : task.State;
        Log.Information("StartupTask state: {State}", state);
        return state switch
        {
            StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy => StartupProblem.None,
            StartupTaskState.DisabledByUser or StartupTaskState.DisabledByPolicy => StartupProblem.DisabledByWindows,
            _ => StartupProblem.Failed,
        };
    }
}
