using System.Net;
using System.Net.Sockets;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;

using static Yaref92.Events.Abstractions.IInboundResilientConnection;

namespace Yaref92.Events.Connections;

public class ResilientInboundConnection : IInboundResilientConnection
{
    private readonly ResilientSessionOptions _options;
    private readonly object _runLock = new();
    private readonly string _sessionToken;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly SemaphoreSlim _transientConnectionAttachedSemaphore = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private CancellationTokenSource _incomingConnectionCts = new();
    private long _lastRemoteActivityTicks;
    private Task? _runInboundTask;
    private TcpClient? _transientConnection;
    private Task _transientReceiveLoop = Task.CompletedTask;

    public ResilientInboundConnection(ResilientSessionOptions options, SessionKey sessionKey, ResilientOutboundConnection outboundConnection)
    {
        _options = options!; // The SessionManager ensures options are valid
        SessionKey = sessionKey;
        OutboundConnection = outboundConnection;
        SessionKey = sessionKey;
        _sessionToken = SessionFrameContract.CreateSessionToken(SessionKey, _options, _options.AuthenticationToken);
        RecordRemoteActivity();
    }

    public bool IsPastTimeout => _lastRemoteActivityTicks < (DateTime.UtcNow - _options.HeartbeatTimeout).Ticks;

    public DnsEndPoint RemoteEndPoint => new(SessionKey.Host, SessionKey.Port);

    public SessionKey SessionKey { get; }
    public ResilientOutboundConnection OutboundConnection { get; }

    public string SessionToken => _sessionToken;

    private event SessionFrameReceivedHandler? FrameReceived;

    event SessionFrameReceivedHandler? IInboundResilientConnection.FrameReceived
    {
        add => FrameReceived += value;
        remove => FrameReceived -= value;
    }

    Task IInboundResilientConnection.InitAsync(CancellationToken cancellationToken)
    {
        StartRunInboundLoop();
        return Task.CompletedTask;
    }

    async Task IInboundResilientConnection.AttachTransientConnection(TcpClient transientConnection, CancellationTokenSource incomingConnectionCts)
    {
        await _incomingConnectionCts?.CancelAsync()!;
        _incomingConnectionCts = incomingConnectionCts;
        _transientConnection = transientConnection;
        _transientConnectionAttachedSemaphore.Release();
    }

    public static bool ShouldRecordRemoteActivity(SessionFrameKind kind) //touched
    {
        return kind switch
        {
            SessionFrameKind.Event => true,
            SessionFrameKind.Ack => true,
            SessionFrameKind.Ping => true,
            SessionFrameKind.Pong => true,
            SessionFrameKind.Auth => true,
            _ => false,
        };
    }

    public async ValueTask DisposeAsync()
    {
        await ResilientCompositSessionConnection.CancelAndDisposeTokenSource(_cts).ConfigureAwait(false);
        await ResilientCompositSessionConnection.CancelAndDisposeTokenSource(_incomingConnectionCts).ConfigureAwait(false);

        Task[] runTasks = new Task[2];
        lock (_runLock)
        {
            runTasks = [_runInboundTask ?? Task.CompletedTask];
        }

        if (runTasks is not null)
        {
            try
            {
                await Task.WhenAll(runTasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected during shutdown
            }
        }

        _stateLock.Dispose();
    }

    public void RecordRemoteActivity()
    {
        Volatile.Write(ref _lastRemoteActivityTicks, DateTime.UtcNow.Ticks);
    }

    public async Task<AcknowledgementState> WaitForAck(Guid eventId, CancellationToken cancellationToken)//touched
    {
        AcknowledgementState acknowledgementState;
        while (!_cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            if (OutboundConnection.AcknowledgedEventIds.TryGetValue(eventId, out acknowledgementState))
            {
                return acknowledgementState;
            }
            await Task.Delay(_options.HeartbeatInterval, cancellationToken);
        }
        return OutboundConnection.AcknowledgedEventIds.TryGetValue(eventId, out acknowledgementState)
            ? acknowledgementState
            : AcknowledgementState.SendingFailed;
    }

    private async Task HandleInboundFrameAsync(SessionFrame frame, CancellationToken cancellationToken) //touched
    {
        if (ShouldRecordRemoteActivity(frame.Kind))
        {
            RecordRemoteActivity();
        }

        await FrameReceived?.Invoke(frame, SessionKey, cancellationToken)!;
    }

    private async Task RunInboundAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _transientConnectionAttachedSemaphore.WaitAsync(cancellationToken); // wait until there is a valid (meaning also non null) _transientConnection
            await _transientReceiveLoop;
            _transientReceiveLoop = Task.Run(() => RunTransientConnectionReceiveLoopAsync(_transientConnection!, _incomingConnectionCts.Token), _incomingConnectionCts.Token);
        }
    }

    private async Task RunTransientConnectionReceiveLoopAsync(TcpClient client, CancellationToken incomingConnectionCancellation) //touched
    {
        var stream = client.GetStream();
        var lengthBuffer = new byte[4];

        while (!incomingConnectionCancellation.IsCancellationRequested)
        {
            if (!client.Connected)
            {
                break;
            }
            SessionFrameIO.FrameReadResult result;
            try
            {
                result = await SessionFrameIO.ReadFrameAsync(stream, lengthBuffer, incomingConnectionCancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (incomingConnectionCancellation.IsCancellationRequested)
            {
                break;
            }

            if (!result.IsSuccess || result.Frame is null)
            {
                _ = Console.Error.WriteLineAsync("Frame read failed or resulted in null frame");
                continue;
            }

            await HandleInboundFrameAsync(result.Frame, incomingConnectionCancellation).ConfigureAwait(false);
        }

        if (!client.Connected)
        {
            throw new TcpConnectionDisconnectedException();
        }
    }

    private void StartRunInboundLoop()
    {
        lock (_runLock)
        {
            _runInboundTask ??= Task.Run(() => RunInboundAsync(_cts.Token));
        }
    }
}
