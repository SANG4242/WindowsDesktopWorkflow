using System.IO.Pipes;
using System.Text;

namespace DesktopWorkflow.App;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = "Local\\DesktopWorkflow-WPF";
    private const string PipeName = "DesktopWorkflow-WPF";
    private readonly Mutex _mutex;
    private readonly bool _ownsMutex;
    private CancellationTokenSource? _cancellation;

    public SingleInstanceCoordinator()
    {
        _mutex = new Mutex(true, MutexName, out _ownsMutex);
    }

    public bool IsPrimary => _ownsMutex;

    public async Task SendShowAsync()
    {
        await using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        await using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
        await writer.WriteLineAsync("show");
    }

    public void StartServer(Func<Task> showHandler)
    {
        _cancellation = new CancellationTokenSource();
        _ = RunServerAsync(showHandler, _cancellation.Token);
    }

    private static async Task RunServerAsync(Func<Task> showHandler, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var command = await reader.ReadLineAsync(cancellationToken);
                if (string.Equals(command, "show", StringComparison.OrdinalIgnoreCase))
                {
                    await showHandler();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                await Task.Delay(250, cancellationToken);
            }
        }
    }

    public void Dispose()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
        }
        _mutex.Dispose();
    }
}
