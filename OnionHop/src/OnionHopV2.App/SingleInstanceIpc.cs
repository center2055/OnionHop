using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OnionHopV2.App;

internal static class SingleInstanceIpc
{
    public const string MutexName = "OnionHopV2.SingleInstance";
    private const string PipeName = "OnionHopV2.Ipc";

    public static Mutex AcquireMutex(out bool isPrimary)
    {
        return new Mutex(initiallyOwned: true, MutexName, out isPrimary);
    }

    public static async Task<bool> TrySendAsync(string message, int timeoutMs = 1200)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            await pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);

            var payload = Encoding.UTF8.GetBytes(message + "\n");
            await pipe.WriteAsync(payload, timeoutCts.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeoutCts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void StartServer(Func<string, Task> handler, CancellationToken token)
    {
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                    using var reader = new StreamReader(pipe, Encoding.UTF8);
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        await handler(line.Trim()).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    await Task.Delay(250, token).ConfigureAwait(false);
                }
            }
        }, token);
    }
}

