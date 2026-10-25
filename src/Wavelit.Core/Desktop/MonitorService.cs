using System.Runtime.InteropServices;
using Wavelit.Core.Interop;

namespace Wavelit.Core.Desktop;

/// <summary>A physical display. All rectangles are in physical (per-monitor-DPI-aware) screen pixels.</summary>
public sealed record MonitorInfo(
    string Id,
    string DeviceName,
    int Index,
    RECT Bounds,
    RECT WorkArea,
    bool IsPrimary)
{
    public string DisplayName => $"Display {Index + 1}{(IsPrimary ? " (Primary)" : "")}";
    public string Resolution => $"{Bounds.Width} × {Bounds.Height}";
}

public static class MonitorService
{
    /// <summary>Enumerates monitors ordered left-to-right, top-to-bottom so numbering matches what users see.</summary>
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var raw = new List<(MONITORINFOEX info, string id)>();
        NativeMethods.EnumDisplayMonitors(0, 0, (nint hMon, nint _, ref RECT _, nint _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (NativeMethods.GetMonitorInfo(hMon, ref mi))
                raw.Add((mi, ResolveStableId(mi.szDevice)));
            return true;
        }, 0);

        return raw
            .OrderBy(r => r.info.rcMonitor.Left)
            .ThenBy(r => r.info.rcMonitor.Top)
            .Select((r, i) => new MonitorInfo(
                r.id,
                r.info.szDevice,
                i,
                r.info.rcMonitor,
                r.info.rcWork,
                (r.info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0))
            .ToList();
    }

    /// <summary>Bounding box of all monitors (the "virtual screen").</summary>
    public static RECT GetVirtualBounds(IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0) return default;
        return new RECT(
            monitors.Min(m => m.Bounds.Left),
            monitors.Min(m => m.Bounds.Top),
            monitors.Max(m => m.Bounds.Right),
            monitors.Max(m => m.Bounds.Bottom));
    }

    // \\.\DISPLAY1 can be renumbered across reboots/driver updates; the monitor's
    // device interface path is tied to the physical panel, so per-monitor
    // wallpaper assignments survive.
    private static string ResolveStableId(string deviceName)
    {
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        if (NativeMethods.EnumDisplayDevices(deviceName, 0, ref dd, NativeMethods.EDD_GET_DEVICE_INTERFACE_NAME)
            && !string.IsNullOrWhiteSpace(dd.DeviceID))
        {
            return dd.DeviceID;
        }
        return deviceName;
    }
}
