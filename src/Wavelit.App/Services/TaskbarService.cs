using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Wavelit.Core.Desktop;
using Wavelit.Core.Storage;

namespace Wavelit.App.Services;

/// <summary>
/// The taskbar half of the Desktop page (plan §22.1): centred or left icons and auto-hide ("invisible until you
/// point at the screen edge"), through Windows' documented per-user settings only. No injection into Explorer,
/// no admin rights. The user's own values come back when a feature is turned off or Wavelit quits.
/// </summary>
public sealed class TaskbarService : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly ILogger<TaskbarService> _logger;
    private readonly TaskbarController _controller;
    private TaskbarSettings? _applied;
    private bool _disposed;

    public TaskbarService(SettingsStore settings, AppPaths paths, ILogger<TaskbarService> logger)
    {
        _settings = settings;
        _logger = logger;
        _paths = paths;
        _controller = new TaskbarController(new WindowsTaskbar(), Path.Combine(paths.Root, "taskbar-original.json"));
    }

    private readonly AppPaths _paths;

    /// <summary>TranslucentTB, found fresh each time (it can be installed or removed while Wavelit runs).</summary>
    public TranslucentTb TranslucentTb => new(
        TranslucentTb.FindSettings(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
        Path.Combine(_paths.Root, "translucenttb-original.json"));

    public static bool TranslucentTbRunning
    {
        get
        {
            var running = System.Diagnostics.Process.GetProcessesByName("TranslucentTB");
            foreach (var p in running) p.Dispose();
            return running.Length > 0;
        }
    }

    /// <summary>Starts TranslucentTB (Store build) through its package's app id; false if it couldn't.</summary>
    public bool StartTranslucentTb()
    {
        string? settings = TranslucentTb.FindSettings(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        if (settings is null) return false;
        string family = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(settings))!);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"shell:AppsFolder\\{family}!TranslucentTB") { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Couldn't start TranslucentTB");
            return false;
        }
    }

    /// <summary>The last error shown on the Desktop page (null = fine).</summary>
    public string? Problem { get; private set; }

    public event EventHandler? StatusChanged;

    public void Start()
    {
        _settings.Changed += OnSettingsChanged;
        Apply(_settings.Current.Desktop.Taskbar ?? new());
    }

    private void OnSettingsChanged(object? sender, AppSettings e) => Apply(e.Desktop.Taskbar ?? new());

    private void Apply(TaskbarSettings desired)
    {
        if (_disposed || desired == _applied) return;
        _applied = desired;
        try
        {
            _controller.Apply(desired);
            Problem = null;
            _logger.LogInformation("Taskbar: align {Align}, auto-hide {AutoHide}", desired.Align, desired.AutoHide);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ExternalException)
        {
            Problem = "Windows didn't accept the taskbar change. Try again, or set it in Settings → Personalisation → Taskbar.";
            _logger.LogWarning(ex, "Taskbar change failed");
        }
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        if (_settings.Current.Desktop.Taskbar?.KeepAfterQuit == true) return;
        try
        {
            _controller.Restore();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ExternalException)
        {
            _logger.LogWarning(ex, "Restoring the taskbar failed");
        }
    }

    /// <summary>TaskbarAl in the per-user Explorer settings, and the shell's own auto-hide state.</summary>
    private sealed class WindowsTaskbar : ITaskbarBackend
    {
        private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        private const uint ABM_GETSTATE = 4, ABM_SETSTATE = 10, ABS_AUTOHIDE = 1, ABS_ALWAYSONTOP = 2;
        private const int WM_SETTINGCHANGE = 0x001A, SMTO_ABORTIFHUNG = 2;
        private static readonly nint HWND_BROADCAST = 0xFFFF;

        public int? Alignment
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(AdvancedKey);
                return key?.GetValue("TaskbarAl") is int v ? v : null;
            }
            set
            {
                using var key = Registry.CurrentUser.CreateSubKey(AdvancedKey, writable: true);
                if (value is { } v) key.SetValue("TaskbarAl", v, RegistryValueKind.DWord);
                else key.DeleteValue("TaskbarAl", throwOnMissingValue: false);
                // Explorer watches this key; the broadcast is what Settings sends too.
                SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, 0, "TraySettings", SMTO_ABORTIFHUNG, 1000, out _);
            }
        }

        public bool AutoHide
        {
            get
            {
                var data = NewData();
                return ((uint)SHAppBarMessage(ABM_GETSTATE, ref data) & ABS_AUTOHIDE) != 0;
            }
            set
            {
                var data = NewData();
                data.lParam = value ? (nint)ABS_AUTOHIDE : (nint)ABS_ALWAYSONTOP;
                SHAppBarMessage(ABM_SETSTATE, ref data);
            }
        }

        private static APPBARDATA NewData() => new()
        {
            cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
            hWnd = FindWindow("Shell_TrayWnd", null),
        };

        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public uint cbSize;
            public nint hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public Core.Interop.RECT rc;
            public nint lParam;
        }

        [DllImport("shell32.dll")]
        private static extern nuint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern nint FindWindow(string lpClassName, string? lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern nint SendMessageTimeout(nint hWnd, int msg, nint wParam, string lParam, int flags, int timeout, out nint result);
    }
}
