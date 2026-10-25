using System.Runtime.InteropServices;
using Wavelit.Core.Interop;

namespace Wavelit.Core.Playback;

/// <summary>Cheap polled system signals (each call is a few microseconds).</summary>
public static class SystemProbe
{
    private const int QUNS_BUSY = 2;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private const int QUNS_PRESENTATION_MODE = 4;

    public static (bool ExclusiveFullscreen, bool PresentationMode) QueryNotificationState()
    {
        if (NativeMethods.SHQueryUserNotificationState(out int state) != 0) return (false, false);
        return (state == QUNS_RUNNING_D3D_FULL_SCREEN, state == QUNS_PRESENTATION_MODE);
    }

    public static (bool OnBattery, bool BatterySaver) QueryPower()
    {
        if (!NativeMethods.GetSystemPowerStatus(out var s)) return (false, false);
        // ACLineStatus: 0 = offline; 255 = unknown (desktops report 1).
        return (s.ACLineStatus == 0, (s.SystemStatusFlag & 1) != 0);
    }

    public static bool IsRemoteSession() => NativeMethods.GetSystemMetrics(NativeMethods.SM_REMOTESESSION) != 0;

    public static bool IsBusyFullscreen()
        => NativeMethods.SHQueryUserNotificationState(out int state) == 0 && state == QUNS_BUSY;
}

/// <summary>Toggles Windows "Efficiency mode" (EcoQoS) for our process while everything is suspended.</summary>
public static class EcoQos
{
    private const int ProcessPowerThrottling = 4;
    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    public static void Set(bool enabled)
    {
        var state = new PROCESS_POWER_THROTTLING_STATE
        {
            Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            // ControlMask = 0 hands the decision back to the OS (rather than forcing high QoS).
            ControlMask = enabled ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
            StateMask = enabled ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
        };
        NativeMethods.SetProcessInformation(NativeMethods.GetCurrentProcess(), ProcessPowerThrottling,
            ref state, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
    }
}
