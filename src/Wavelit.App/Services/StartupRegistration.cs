using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Wavelit.App.Services;

/// <summary>Start-with-Windows via the per-user Run key (unpackaged builds).</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Wavelit";
    public const string BackgroundArg = "--background";

    /// <summary>MSIX builds register a startup task in the manifest instead (registry writes are virtualized there).</summary>
    public static bool IsPackaged
    {
        get
        {
            int length = 0;
            return GetCurrentPackageFullName(ref length, null) != APPMODEL_ERROR_NO_PACKAGE;
        }
    }

    public static void Apply(bool enabled)
    {
        if (IsPackaged || Environment.ProcessPath is not { } exe) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(ValueName, $"\"{exe}\" {BackgroundArg}");
        else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private const int APPMODEL_ERROR_NO_PACKAGE = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);
}
