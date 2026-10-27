using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Wavelit.App.Dashboard;
using Wavelit.App.Dashboard.ViewModels;
using Wavelit.App.Services;
using Wavelit.App.Tray;
using Wavelit.App.Wallpaper;
using Wavelit.Core.Create;
using Wavelit.Core.Create.Shaders;
using Wavelit.Core.Desktop;
using Wavelit.Core.Diagnostics;
using Wavelit.Core.Library;
using Wavelit.Core.Library.BuiltIn;
using Wavelit.Core.Library.Pexels;
using Wavelit.Core.Library.Shadertoy;
using Wavelit.Core.Library.Sources;
using Wavelit.Core.Storage;

namespace Wavelit.App;

public partial class App : Application
{
    private const string UserAgent = "Wavelit/0.1 (+https://github.com/NDNathanRocks/WaveLit)";
    private SingleInstance? _singleInstance;
    private ServiceProvider? _services;
    private TrayService? _tray;
    private DashboardWindow? _dashboard;
    private Timer? _soakTimer;
    private bool _shuttingDown;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Wavelit's own WPF surfaces are simple and mostly static, so they render in software: no VRAM
        // for the UI, and immune to the GPU-driver failures where WPF's Direct3D path silently draws
        // nothing. Wallpapers still render on the GPU inside WebView2's own processes.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        bool quitRequested = e.Args.Contains("--quit"); // used by installers/uninstallers
        bool snapshotRequested = e.Args.Contains("--snapshot"); // diagnostics: logs/snapshot.png of every wallpaper
        bool statsRequested = e.Args.Contains("--frame-stats"); // diagnostics: frame pacing of every wallpaper, to the log
        _singleInstance = new SingleInstance();
        if (!_singleInstance.IsFirst || quitRequested || snapshotRequested || statsRequested)
        {
            if (!_singleInstance.IsFirst)
                SingleInstance.SignalFirstInstance(quitRequested ? SingleInstance.QuitCommand
                    : snapshotRequested ? SingleInstance.SnapshotCommand
                    : statsRequested ? SingleInstance.FrameStatsCommand : SingleInstance.ActivateCommand);
            _singleInstance.Dispose();
            Shutdown();
            return;
        }

        var paths = new AppPaths();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(e.Args.Contains("--verbose") ? LogEventLevel.Debug : LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .MinimumLevel.Override("Polly", LogEventLevel.Warning)
            .WriteTo.File(Path.Combine(paths.Logs, "wavelit-.log"), rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7, fileSizeLimitBytes: 10 * 1024 * 1024)
            .CreateLogger();
        HookUnhandledExceptions();
        Log.Information("Wavelit starting (args: {Args})", string.Join(' ', e.Args));

        _services = ConfigureServices(paths);
        var settings = _services.GetRequiredService<SettingsStore>();

        bool firstRun = !settings.Current.FirstRunCompleted;
        if (firstRun)
        {
            // Something beautiful immediately, no keys required, at a quality that suits this PC's graphics.
            var preset = QualityPresets.ForGpu(Wavelit.Core.Create.GpuProbe.Best()?.DedicatedVideoMemory ?? 0);
            settings.Update(s => s with
            {
                ShaderFpsCap = preset.Fps,
                ShaderRenderScale = preset.Scale,
                FirstRunCompleted = true,
                Displays = new DisplayAssignments { SharedItemId = BuiltInShaders.DefaultItemId },
            });
        }
        StartupRegistration.Apply(settings.Current.StartWithWindows);
        settings.Changed += (_, s) => StartupRegistration.Apply(s.StartWithWindows);

        var wallpapers = _services.GetRequiredService<WallpaperService>();
        _tray = new TrayService(wallpapers, _services.GetRequiredService<LibraryStore>(), OpenDashboard, () => _ = QuitAsync());
        wallpapers.Error += (_, message) => _tray.Notify("Wavelit", message);
        if (!e.Args.Contains("--no-wallpaper")) await wallpapers.StartAsync(); // --no-wallpaper: dashboard-only diagnostics
        _services.GetRequiredService<TaskbarService>().Start();

        _singleInstance.Listen(command => Dispatcher.BeginInvoke(() =>
        {
            if (command == SingleInstance.QuitCommand) _ = QuitAsync();
            else if (command == SingleInstance.SnapshotCommand) _ = wallpapers.SaveSnapshotAsync();
            else if (command == SingleInstance.FrameStatsCommand) wallpapers.LogFrameStats();
            else OpenDashboard();
        }));
        _ = _services.GetRequiredService<LibraryActions>().TrimCacheAsync();

        if (e.Args.Contains("--soak")) StartSoakLog(paths, wallpapers);
        _services.GetRequiredService<CreateService>().ClearPending();
        _services.GetRequiredService<SetupMemoryService>();
        _services.GetRequiredService<DayNightService>().Start();
        _services.GetRequiredService<LocalAiService>().ResumeIfWanted();
        if (firstRun) ShowWelcome(paths);
        else if (!e.Args.Contains(StartupRegistration.BackgroundArg)) OpenDashboard();

        SessionEnding += (_, _) => _ = QuitAsync();
    }

    private static ServiceProvider ConfigureServices(AppPaths paths)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSerilog(dispose: false));
        services.AddSingleton(paths);
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<SecretStore>();
        services.AddSingleton<LibraryStore>();
        services.AddSingleton<ContentGuard>();
        services.AddSingleton<MediaCache>();
        services.AddSingleton<PexelsClient>();
        services.AddSingleton<ShadertoyClient>();
        // Online libraries, in the order their results are interleaved.
        services.AddSingleton<IWallpaperSource, WallhavenSource>();
        services.AddSingleton<IWallpaperSource, PixabaySource>();
        services.AddSingleton<IWallpaperSource, CommonsSource>();
        services.AddSingleton<IWallpaperSource, NasaSource>();
        services.AddSingleton<IWallpaperSource, OpenverseSource>();
        services.AddSingleton<IWallpaperSource, PexelsSource>();
        services.AddSingleton<WallpaperResolver>();
        services.AddSingleton<DesktopHost>();
        services.AddSingleton<WallpaperService>();
        services.AddSingleton<ThumbnailLoader>();
        services.AddSingleton<LibraryActions>();
        services.AddSingleton<ModelDownloader>();
        services.AddSingleton<LocalAiService>();
        services.AddSingleton<PollinationsGenerator>();
        services.AddSingleton<CloudflareGenerator>();
        services.AddSingleton<CreateService>();
        services.AddSingleton(sp => new ClaudeShaderAuthor(sp.GetRequiredService<SecretStore>(), () => sp.GetRequiredService<SettingsStore>().Current.StudioModel));
        services.AddSingleton<CloudflareShaderAuthor>();
        services.AddSingleton<StudioHistory>();
        services.AddSingleton<ShaderStudioServices>();
        services.AddSingleton<SetupMemoryService>();
        services.AddSingleton<DayNightService>();
        services.AddSingleton<TaskbarService>();

        // Dashboard view models are created per window and disposed by the window when it closes.
        // A factory (rather than a transient registration) keeps the container from also tracking and
        // disposing them a second time at shutdown.
        services.AddSingleton<Func<MainViewModel>>(sp => () =>
        {
            var wallpapers = sp.GetRequiredService<WallpaperService>();
            var ctx = new DashboardContext(wallpapers, sp.GetRequiredService<ThumbnailLoader>(),
                sp.GetRequiredService<LibraryActions>(), sp.GetRequiredService<SettingsStore>(), sp.GetRequiredService<LibraryStore>());
            var localAi = sp.GetRequiredService<LocalAiService>();
            return new MainViewModel(ctx,
                new DiscoverViewModel(ctx, sp.GetServices<IWallpaperSource>(), sp.GetRequiredService<ShadertoyClient>(), sp.GetRequiredService<CreateService>()),
                new StudioViewModel(ctx,
                    new CreateViewModel(ctx, sp.GetRequiredService<CreateService>(), new LocalAiViewModel(localAi, paths), sp.GetRequiredService<SecretStore>()),
                    sp.GetRequiredService<ShaderStudioServices>()),
                new LibraryViewModel(ctx, wallpapers),
                new DisplaysViewModel(ctx, wallpapers),
                new OverlaysViewModel(ctx, paths),
                new DesktopViewModel(ctx, sp.GetRequiredService<TaskbarService>(), paths),
                new SettingsViewModel(ctx, sp.GetRequiredService<SecretStore>(), sp.GetRequiredService<PexelsClient>(),
                    sp.GetRequiredService<ShadertoyClient>(), sp.GetRequiredService<MediaCache>(), paths,
                    sp.GetServices<IWallpaperSource>().OfType<PixabaySource>().Single(), sp.GetRequiredService<IHttpClientFactory>(),
                    new LocalAiViewModel(localAi, paths), sp.GetRequiredService<ClaudeShaderAuthor>()),
                new AboutViewModel(wallpapers),
                wallpapers);
        });

        // Request logging is removed everywhere: Shadertoy puts the API key in the query string.
        services.AddHttpClient(PexelsClient.HttpClientName, c =>
            {
                c.BaseAddress = new Uri("https://api.pexels.com/");
                c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            })
            .RemoveAllLoggers()
            .AddStandardResilienceHandler();
        services.AddHttpClient(ShadertoyClient.HttpClientName, c =>
            {
                c.BaseAddress = new Uri("https://www.shadertoy.com/");
                c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            })
            .RemoveAllLoggers()
            .AddStandardResilienceHandler();
        services.AddHttpClient(MediaCache.DownloadClient, c =>
            {
                c.Timeout = Timeout.InfiniteTimeSpan; // large files; cancellation is explicit
                c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            })
            .RemoveAllLoggers();
        services.AddHttpClient(JsonSource.HttpClientName, c =>
            {
                c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            })
            .RemoveAllLoggers()
            .AddStandardResilienceHandler();
        services.AddHttpClient(ModelDownloader.HttpClientName, c =>
            {
                c.Timeout = Timeout.InfiniteTimeSpan; // multi-GB files; pausing cancels explicitly
                c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            })
            .RemoveAllLoggers();
        services.AddHttpClient(PollinationsGenerator.HttpClientName, c =>
            {
                c.Timeout = TimeSpan.FromMinutes(3); // free generators can queue for a while
                c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            })
            .RemoveAllLoggers();
        services.AddHttpClient(ThumbnailLoader.HttpClientName, c =>
            {
                c.Timeout = TimeSpan.FromSeconds(20);
                c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            })
            .RemoveAllLoggers();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    // First run: three quick choices (starting wallpaper, multi-screen, optional Local AI), then the dashboard.
    private void ShowWelcome(AppPaths paths)
    {
        var sp = _services!;
        var vm = new WelcomeViewModel(sp.GetRequiredService<SettingsStore>(), sp.GetRequiredService<LibraryActions>(),
            sp.GetRequiredService<LocalAiService>(), sp.GetRequiredService<ThumbnailLoader>(), sp.GetRequiredService<WallpaperService>(), paths);
        var welcome = new WelcomeWindow(vm);
        welcome.Closed += (_, _) => OpenDashboard();
        welcome.Show();
    }

    private void OpenDashboard()
    {
        if (_shuttingDown || _services is null) return;
        if (_dashboard is null)
        {
            _dashboard = new DashboardWindow(_services.GetRequiredService<Func<MainViewModel>>()());
            // Closing the dashboard destroys it (and frees its memory); Wavelit keeps running in the tray.
            _dashboard.Closed += (_, _) =>
            {
                _dashboard = null;
                GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
            };
            _dashboard.Show();
        }
        if (_dashboard.WindowState == WindowState.Minimized) _dashboard.WindowState = WindowState.Normal;
        _dashboard.Activate();
    }

    private async Task QuitAsync()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        Log.Information("Wavelit shutting down");

        // Last resort: never leave a zombie process behind if a cleanup step hangs.
        _ = Task.Delay(TimeSpan.FromSeconds(15)).ContinueWith(_ => Environment.Exit(0), TaskScheduler.Default);

        await RunQuietly("dashboard", () => { _soakTimer?.Dispose(); _dashboard?.Close(); return Task.CompletedTask; });
        await RunQuietly("tray", () => { _tray?.Dispose(); return Task.CompletedTask; });
        if (_services is not null)
        {
            await RunQuietly("wallpapers", () => _services.GetRequiredService<WallpaperService>().DisposeAsync().AsTask());
            await RunQuietly("services", () => _services.DisposeAsync().AsTask());
        }
        await RunQuietly("single instance", () => { _singleInstance?.Dispose(); return Task.CompletedTask; });
        await Log.CloseAndFlushAsync();
        Shutdown();
    }

    private static async Task RunQuietly(string step, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Shutdown step '{Step}' failed", step);
        }
    }

    /// <summary>--soak: append resource usage to logs/resources.csv every minute (leak hunting).</summary>
    private void StartSoakLog(AppPaths paths, WallpaperService wallpapers)
    {
        string file = Path.Combine(paths.Logs, "resources.csv");
        if (!File.Exists(file)) File.WriteAllText(file, ResourceSampler.CsvHeader + Environment.NewLine);
        _soakTimer = new Timer(_ =>
        {
            try { File.AppendAllText(file, ResourceSampler.ToCsv(ResourceSampler.Take(wallpapers.BrowserProcessIds)) + Environment.NewLine); }
            catch (IOException) { }
        }, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
    }

    private void HookUnhandledExceptions()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            Log.Error(e.Exception, "Unhandled UI exception");
            e.Handled = true; // keep the wallpaper running; the error is logged
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Warning(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
