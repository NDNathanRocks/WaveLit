using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Wavelit.Core.Interop;

namespace Wavelit.Core.Diagnostics;

public sealed record ResourceSample(
    DateTimeOffset At, long PrivateBytes, long WorkingSet, long ManagedBytes,
    uint Handles, uint GdiObjects, uint UserObjects, long BrowserPrivateBytes, int BrowserProcesses);

/// <summary>Measures our process plus its WebView2 children — the numbers a leak shows up in first.</summary>
public static class ResourceSampler
{
    private const uint GR_GDIOBJECTS = 0;
    private const uint GR_USEROBJECTS = 1;

    public static ResourceSample Take(IEnumerable<int>? browserProcessIds = null)
    {
        nint self = NativeMethods.GetCurrentProcess();
        var mem = new PROCESS_MEMORY_COUNTERS_EX { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX>() };
        NativeMethods.GetProcessMemoryInfo(self, out mem, mem.cb);
        NativeMethods.GetProcessHandleCount(self, out uint handles);

        long browserBytes = 0;
        int browserCount = 0;
        foreach (int pid in browserProcessIds ?? [])
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                browserBytes += p.PrivateMemorySize64;
                browserCount++;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }

        return new ResourceSample(
            DateTimeOffset.Now,
            (long)mem.PrivateUsage,
            (long)mem.WorkingSetSize,
            GC.GetTotalMemory(false),
            handles,
            NativeMethods.GetGuiResources(self, GR_GDIOBJECTS),
            NativeMethods.GetGuiResources(self, GR_USEROBJECTS),
            browserBytes,
            browserCount);
    }

    public const string CsvHeader = "time,private_mb,working_set_mb,managed_mb,handles,gdi,user,browser_private_mb,browser_processes";

    public static string ToCsv(ResourceSample s) => string.Join(',',
        s.At.ToString("O", CultureInfo.InvariantCulture),
        Mb(s.PrivateBytes), Mb(s.WorkingSet), Mb(s.ManagedBytes),
        s.Handles, s.GdiObjects, s.UserObjects, Mb(s.BrowserPrivateBytes), s.BrowserProcesses);

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture);
}
