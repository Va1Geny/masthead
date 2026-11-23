using System.IO.Pipes;
using Isle.Agents;
using Isle.Core;

namespace Isle.Modules.Agents;

/// <summary>
/// Named-pipe server for Isle.Hook.exe (current user, local clients only, any number of concurrent hook calls). One request line per
/// connection; for waiting calls the handler's reply is written back. The handler's token is cancelled when the hook
/// disconnects (its own timeout, Ctrl+C in the terminal) or after the hook's timeout + 5 s.
/// </summary>
public sealed class AgentPipeServer(string pipeName, Func<HookMessage, CancellationToken, Task<string?>> handle) : IDisposable
{
    CancellationTokenSource? _cts;

    public void Start()
    {
        if (_cts is not null) return;
        var first = Create(first: true);
        _cts = new CancellationTokenSource();
        _ = AcceptLoop(first, _cts.Token);
    }

    NamedPipeServerStream Create(bool first = false) =>
        new(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly |
            (first ? PipeOptions.FirstPipeInstance : PipeOptions.None));

    async Task AcceptLoop(NamedPipeServerStream listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await listener.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var connected = listener;
                // Keep a listener open before handing the connected instance to its request.
                listener = Create();
                _ = Serve(connected, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { Logger.Error("agent pipe accept", ex); }
        finally { listener.Dispose(); }
    }

    async Task Serve(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        {
            try
            {
                if (!IsLocal(pipe)) return;
                string? line;
                using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    readCts.CancelAfter(TimeSpan.FromSeconds(5));
                    line = await HookProtocol.ReadLineAsync(pipe, readCts.Token).ConfigureAwait(false);
                }
                var msg = HookMessage.Parse(line);
                if (msg is null) return;
                if (!msg.Wait)
                {
                    await handle(msg, ct).ConfigureAwait(false);
                    return;
                }
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                waitCts.CancelAfter(TimeSpan.FromSeconds(msg.TimeoutSeconds + 5));
                _ = WatchDisconnect(pipe, waitCts);
                var reply = await handle(msg, waitCts.Token).ConfigureAwait(false) ?? HookProtocol.Reply("none");
                if (pipe.IsConnected && !ct.IsCancellationRequested)
                    await HookProtocol.WriteLineAsync(pipe, reply, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { } // hook went away
            catch (Exception ex) { Logger.Error("agent pipe serve", ex); }
        }
    }

    /// <summary>
    /// CurrentUserOnly limits the pipe to this account but does not set PIPE_REJECT_REMOTE_CLIENTS, so the same (domain) account could
    /// still connect over SMB from another machine. Windows names the computer of such a client and fails with ERROR_PIPE_LOCAL for a local one.
    /// </summary>
    internal static bool IsLocal(NamedPipeServerStream pipe) =>
        !Native.GetNamedPipeClientComputerName(pipe.SafePipeHandle, new char[256], 512) &&
        System.Runtime.InteropServices.Marshal.GetLastPInvokeError() == Native.ERROR_PIPE_LOCAL;

    /// <summary>The hook sends nothing after its request; a completed read means it closed the pipe.</summary>
    static async Task WatchDisconnect(NamedPipeServerStream pipe, CancellationTokenSource cts)
    {
        try
        {
            var b = new byte[1];
            await pipe.ReadAsync(b, cts.Token).ConfigureAwait(false);
        }
        catch (Exception) { }
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
