using System.Runtime.InteropServices;
using Wavelit.Core.Interop;

namespace Wavelit.Core.Desktop;

/// <summary>
/// A physical display. All rectangles are in physical (per-monitor-DPI-aware) screen pixels.
/// <see cref="WidthMm"/>/<see cref="HeightMm"/> are the panel's real size from its EDID (0 when unknown),
/// in the current orientation.
/// </summary>
public sealed record MonitorInfo(
    string Id,
    string DeviceName,
    int Index,
    RECT Bounds,
    RECT WorkArea,
    bool IsPrimary,
    int WidthMm = 0,
    int HeightMm = 0)
{
    public string DisplayName => $"Display {Index + 1}{(IsPrimary ? " (Primary)" : "")}";
    public string Resolution => $"{Bounds.Width} × {Bounds.Height}";

    /// <summary>Millimetres per pixel, or null when the panel size is unknown or implausible.</summary>
    public double? MmPerPixel => WidthMm is > 50 and < 3000 && Bounds.Width > 0 ? (double)WidthMm / Bounds.Width : null;

    public double? DiagonalInches => WidthMm > 0 && HeightMm > 0 ? Math.Sqrt(WidthMm * WidthMm + HeightMm * HeightMm) / 25.4 : null;
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
            .Select((r, i) =>
            {
                var (wmm, hmm) = PhysicalSize(r.info.szDevice);
                return new MonitorInfo(
                    r.id,
                    r.info.szDevice,
                    i,
                    r.info.rcMonitor,
                    r.info.rcWork,
                    (r.info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
                    wmm,
                    hmm);
            })
            .ToList();
    }

    // HORZSIZE/VERTSIZE on a display DC report the panel's EDID size, already rotated with the display.
    private static (int WidthMm, int HeightMm) PhysicalSize(string deviceName)
    {
        nint dc = CreateDC(null, deviceName, null, 0);
        if (dc == 0) return (0, 0);
        try
        {
            return (GetDeviceCaps(dc, 4 /* HORZSIZE */), GetDeviceCaps(dc, 6 /* VERTSIZE */));
        }
        finally
        {
            DeleteDC(dc);
        }
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateDC(string? driver, string device, string? output, nint initData);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(nint hdc, int index);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint hdc);

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
