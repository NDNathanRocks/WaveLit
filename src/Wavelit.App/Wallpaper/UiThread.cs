using System.Windows.Threading;

namespace Wavelit.App.Wallpaper;

/// <summary>
/// A dedicated STA thread with its own WPF dispatcher. Wallpaper windows live here because a window
/// parented into Explorer's desktop shares Explorer's input queue; keeping them off the main UI thread
/// means a busy Explorer can never freeze the dashboard (and vice versa).
/// </summary>
internal sealed class UiThread : IDisposable
{
    private readonly Thread _thread;

    public UiThread(string name, Action<Exception> onUnhandled)
    {
        var ready = new TaskCompletionSource<Dispatcher>();
        _thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.UnhandledException += (_, e) =>
            {
                onUnhandled(e.Exception);
                e.Handled = true;
            };
            ready.SetResult(dispatcher);
            Dispatcher.Run();
        })
        {
            Name = name,
            IsBackground = true,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        Dispatcher = ready.Task.GetAwaiter().GetResult();
    }

    public Dispatcher Dispatcher { get; }

    public Task InvokeAsync(Func<Task> action) => Dispatcher.InvokeAsync(action).Task.Unwrap();

    public Task<T> InvokeAsync<T>(Func<Task<T>> action) => Dispatcher.InvokeAsync(action).Task.Unwrap();

    public void Dispose()
    {
        if (!Dispatcher.HasShutdownStarted) Dispatcher.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(3));
    }
}
