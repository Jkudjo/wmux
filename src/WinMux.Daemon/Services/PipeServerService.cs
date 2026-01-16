using System.IO.Pipes;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WinMux.Core.Protocol;
using WinMux.Daemon.Sessions;

using System.Security.AccessControl;
using System.Security.Principal;

namespace WinMux.Daemon.Services;

public sealed class PipeServerService(ILogger<PipeServerService> logger) : BackgroundService
{
    private const string PipeName = "winmuxd";
    private readonly SessionManager _sessions = new(new LoggerFactory());

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("winmuxd starting on pipe {pipe}", PipeName);
        while (!stoppingToken.IsCancellationRequested)
        {
            var ps = new PipeSecurity();
            var sid = WindowsIdentity.GetCurrent().User;
            if (sid != null)
            {
                 ps.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
            }
            
            // ACL-secured pipe creation
            var server = NamedPipeServerStreamAcl.Create(
                PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                4096, 4096,
                ps);

            try
            {
                await server.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                server.Dispose();
                break;
            }
            _ = HandleClientAsync(server, stoppingToken);
        }
        logger.LogInformation("winmuxd stopping");
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var _ = pipe;
        var client = new ClientConnection(pipe, ct);
        try
        {
            while (pipe.IsConnected && !ct.IsCancellationRequested)
            {
                var req = await Serialization.ReadFramedAsync<RequestMessage>(pipe, ct).ConfigureAwait(false);
                if (req is null) break;

                switch (req)
                {
                    case PingRequest:
                        await client.SendAsync(new PongEvent(DateTimeOffset.UtcNow), ct);
                        break;

                    case ListRequest:
                        await client.SendAsync(new SessionsEvent(_sessions.List()), ct);
                        break;

                    case CreateSessionRequest create:
                    {
                        var (session, created) = _sessions.Create(create, ct);
                        await client.SendAsync(created, ct);
                        break;
                    }

                    case AttachRequest attach:
                    {
                        var s = _sessions.GetByIdOrName(attach.IdOrName);
                        if (s is null) { await client.SendAsync(new ErrorEvent(null, "NOT_FOUND", "Session not found"), ct); break; }
                        
                        Action<byte[]> listener = bytes => client.TrySend(new OutputEvent(s.Id, bytes));
                        s.AddListener(listener);
                        client.OnDispose(() => s.RemoveListener(listener));

                        await client.SendAsync(new AttachedEvent(s.Id), ct);
                        break;
                    }

                    case InputRequest input:
                    {
                        var s = _sessions.GetByIdOrName(input.SessionId);
                        if (s is null) { await client.SendAsync(new ErrorEvent(null, "NOT_FOUND", "Session not found"), ct); break; }
                        s.WriteInput(input.Data);
                        break;
                    }

                    case ResizeRequest resize:
                    {
                        var s = _sessions.GetByIdOrName(resize.SessionId);
                        if (s is null) { await client.SendAsync(new ErrorEvent(null, "NOT_FOUND", "Session not found"), ct); break; }
                        s.Resize(resize.Cols, resize.Rows);
                        break;
                    }

                    case KillRequest kill:
                    {
                        var s = _sessions.GetByIdOrName(kill.SessionId);
                        if (s is null) { await client.SendAsync(new ErrorEvent(null, "NOT_FOUND", "Session not found"), ct); break; }
                        s.Kill();
                        break;
                    }

                    case DetachRequest:
                        // no special action; client can just stop reading
                        break;

                    default:
                        await client.SendAsync(new ErrorEvent(null, "UNIMPLEMENTED", req.GetType().Name + " not implemented"), ct);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
        }
    }
}

internal sealed class ClientConnection : IDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly Channel<EventMessage> _outgoing = Channel.CreateUnbounded<EventMessage>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationToken _ct;
    private readonly List<Action> _disposeActions = new();
    private readonly Task _writerTask;

    public ClientConnection(NamedPipeServerStream pipe, CancellationToken ct)
    {
        _pipe = pipe;
        _ct = ct;
        _writerTask = Task.Run(WriterLoopAsync);
    }

    public void TrySend(EventMessage ev)
    {
        _outgoing.Writer.TryWrite(ev);
    }

    public Task SendAsync(EventMessage ev, CancellationToken ct) => _outgoing.Writer.WriteAsync(ev, ct).AsTask();

    private async Task WriterLoopAsync()
    {
        try
        {
            while (await _outgoing.Reader.WaitToReadAsync(_ct).ConfigureAwait(false))
            {
                while (_outgoing.Reader.TryRead(out var ev))
                {
                    await Serialization.WriteFramedAsync(_pipe, ev, _ct).ConfigureAwait(false);
                }
            }
        }
        catch { }
    }

    public void OnDispose(Action action)
    {
        _disposeActions.Add(action);
    }

    public void Dispose()
    {
        foreach (var a in _disposeActions) { try { a(); } catch { } }
        try { _pipe.Dispose(); } catch { }
        try { _outgoing.Writer.TryComplete(); } catch { }
        try { _writerTask.Wait(500); } catch { }
    }
}
