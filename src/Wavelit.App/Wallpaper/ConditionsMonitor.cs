using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Wavelit.Core.Interop;
using Wavelit.Core.Playback;

namespace Wavelit.App.Wallpaper;

/// <summary>
/// Watches everything that decides whether wallpapers should render: windows covering displays,
/// exclusive fullscreen games, lock screen, display power, battery and remote sessions.
/// Event driven (foreground/minimize hooks, power notifications) with a 1 s safety-net poll.
/// Must be created on the wallpaper UI thread.
/// </summary>
internal sealed class ConditionsMonitor : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;
    private static Guid GuidConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    private readonly Dispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _debounce;
    private readonly NativeMethods.WinEventProc _winEventProc; // must stay referenced while hooked
    private readonly List<nint> _hooks = [];
    private readonly HwndSource _messageWindow;
    private readonly nint _powerNotification;
    private bool _locked;
    private bool _displayOff;
    private bool _disposed;

    public ConditionsMonitor(Dispatcher dispatcher, ILogger logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
        _poll = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick?.Invoke(this, EventArgs.Empty), dispatcher);
        _debounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(120) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Tick?.Invoke(this, EventArgs.Empty);
        };

        _winEventProc = (_, _, _, _, _, _, _) => RequestEvaluation();
        foreach (var (min, max) in new[] { (EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND), (EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MOVESIZEEND), (EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND) })
        {
            nint hook = NativeMethods.SetWinEventHook(min, max, 0, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
            if (hook != 0) _hooks.Add(hook);
        }

        // Hidden top-level window for power notifications.
        _messageWindow = new HwndSource(new HwndSourceParameters("WavelitMessages")
        {
            WindowStyle = unchecked((int)NativeMethods.WS_POPUP),
            ExtendedWindowStyle = (int)NativeMethods.WS_EX_TOOLWINDOW,
            Width = 0,
            Height = 0,
        });
        _messageWindow.AddHook(WndProc);
        _powerNotification = NativeMethods.RegisterPowerSettingNotification(_messageWindow.Handle, ref GuidConsoleDisplayState, 0);

        SystemEvents.SessionSwitch += OnSessionSwitch;
        _poll.Start();
    }

    /// <summary>Raised on the wallpaper thread whenever conditions should be re-evaluated.</summary>
    public event EventHandler? Tick;

    public bool UserPaused { get; set; }

    public SystemConditions Capture(IReadOnlyList<Core.Desktop.MonitorInfo> monitors)
    {
        var (exclusive, presentation) = SystemProbe.QueryNotificationState();
        var (onBattery, saver) = SystemProbe.QueryPower();
        return new SystemConditions
        {
            Occlusion = OcclusionEvaluator.Evaluate(WindowScanner.Capture(), monitors),
            ExclusiveFullscreen = exclusive,
            PresentationMode = presentation,
            SessionLocked = _locked,
            DisplayOff = _displayOff,
            OnBattery = onBattery,
            BatterySaver = saver,
            RemoteSession = SystemProbe.IsRemoteSession(),
            UserPaused = UserPaused,
        };
    }

    public void RequestEvaluation()
    {
        if (_disposed) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        // SystemEvents fire on their own thread.
        _dispatcher.BeginInvoke(() =>
        {
            if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect) _locked = true;
            else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect) _locked = false;
            _logger.LogInformation("Session {Reason}", e.Reason);
            RequestEvaluation();
        });
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_POWERBROADCAST && wParam == PBT_POWERSETTINGCHANGE && lParam != 0)
        {
            // POWERBROADCAST_SETTING { GUID PowerSetting; DWORD DataLength; UCHAR Data[1]; }
            var setting = Marshal.PtrToStructure<Guid>(lParam);
            if (setting == GuidConsoleDisplayState)
            {
                int state = Marshal.ReadInt32(lParam, 20);
                _displayOff = state == 0; // 0 = off, 1 = on, 2 = dimmed
                _logger.LogInformation("Console display state: {State}", state switch { 0 => "off", 1 => "on", 2 => "dimmed", _ => state.ToString() });
                RequestEvaluation();
            }
        }
        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _poll.Stop();
        _debounce.Stop();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        foreach (var hook in _hooks) NativeMethods.UnhookWinEvent(hook);
        _hooks.Clear();
        if (_powerNotification != 0) NativeMethods.UnregisterPowerSettingNotification(_powerNotification);
        _messageWindow.RemoveHook(WndProc);
        _messageWindow.Dispose();
    }
}
