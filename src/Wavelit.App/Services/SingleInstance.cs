using System.IO;
using System.IO.Pipes;

namespace Wavelit.App.Services;

/// <summary>
/// Ensures one Wavelit per user session. A second launch asks the first instance (over a
/// current-user-only named pipe) to open its dashboard, then exits.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    public const string ActivateCommand = "activate";
    public const string QuitCommand = "quit";
    public const string SnapshotCommand = "snapshot";
    public const string FrameStatsCommand = "frame-stats";
    private readonly string _pipeName = $"Wavelit.{Environment.UserName}.{Environment.UserDomainName}";
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, $@"Local\Wavelit.SingleInstance.{Environment.UserName}", out bool created);
        IsFirst = created;
    }

    public bool IsFirst { get; }

    public void Listen(Action<string> onCommand)
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_cts.Token);
                    using var reader = new StreamReader(server);
                    var line = await reader.ReadLineAsync(_cts.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(2), _cts.Token);
                    if (line is ActivateCommand or QuitCommand or SnapshotCommand or FrameStatsCommand) onCommand(line);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) when (ex is IOException or TimeoutException) { }
            }
        });
    }

    public static bool SignalFirstInstance(string command = ActivateCommand)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", $"Wavelit.{Environment.UserName}.{Environment.UserDomainName}",
                PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(command);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        if (IsFirst) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
