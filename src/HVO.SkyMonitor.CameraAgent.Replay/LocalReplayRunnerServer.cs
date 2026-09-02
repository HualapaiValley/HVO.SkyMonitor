using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Security.Cryptography;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Replay;

public sealed class LocalReplayRunnerServer : IDisposable, IAsyncDisposable
{
    private readonly LocalReplayRunnerOptions _options;
    private readonly IProcessingRecipeExecutor _executor;
    private readonly byte[] _authenticationKey;
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _knownJobs = new();
    private readonly CancellationTokenSource _disposeCancellation = new();
    private long _lastActivityUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
    private int _activeConnections;
    private int _activeJobs;
    private int _runStarted;
    private int _disposed;

    public LocalReplayRunnerServer(
        LocalReplayRunnerOptions options,
        IProcessingRecipeExecutor? executor = null,
        ReplayRunnerCapabilities? capabilities = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _executor = executor ?? new ProcessingRecipeExecutor();
        _authenticationKey = options.LoadAuthenticationKey();
        Capabilities = capabilities ?? ReplayRunnerCapabilities.Create(options);
    }

    public ReplayRunnerCapabilities Capabilities { get; }

    [SuppressMessage("Reliability", "CA2025:Ensure tasks using IDisposable instances complete before disposal", Justification = "The pending socket accept is canceled before the listener is disposed.")]
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _runStarted, 1) != 0)
        {
            throw new InvalidOperationException("A replay runner server instance can only run once.");
        }

        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellation.Token);
        using var listener = ReplayProtocol.CreateSocket(_options.Transport);
        var ownsSocketPath = false;
        var connections = new List<Task>();
        try
        {
            if (_options.Transport == ReplayRunnerTransport.UnixDomainSocket)
            {
                PrepareUnixSocketPath(_options.SocketPath);
            }
            listener.Bind(ReplayProtocol.CreateEndPoint(_options));
            if (_options.Transport == ReplayRunnerTransport.UnixDomainSocket)
            {
                ownsSocketPath = true;
                SetOwnerOnlySocketPermissions(_options.SocketPath);
            }
            listener.Listen(Math.Max(4, _options.MaxConcurrency * 2));

            var accept = listener.AcceptAsync(runCancellation.Token).AsTask();
            while (!runCancellation.IsCancellationRequested)
            {
                var completed = await Task.WhenAny(
                    accept,
                    Task.Delay(TimeSpan.FromMilliseconds(250), runCancellation.Token)).ConfigureAwait(false);
                ObserveAndPrune(connections);
                if (completed != accept)
                {
                    if (ShouldIdleShutdown())
                    {
                        await runCancellation.CancelAsync().ConfigureAwait(false);
                    }
                    continue;
                }

                Socket socket;
                try
                {
                    socket = await accept.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
                {
                    break;
                }
                TouchActivity();
                if (Volatile.Read(ref _activeConnections) >= _options.MaxConcurrency + 4)
                {
                    await SendCapacityUnavailableAsync(socket, runCancellation.Token).ConfigureAwait(false);
                }
                else
                {
                    Interlocked.Increment(ref _activeConnections);
                    connections.Add(HandleTrackedConnectionAsync(socket, runCancellation.Token));
                }
                accept = listener.AcceptAsync(runCancellation.Token).AsTask();
            }
        }
        catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            await runCancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(connections).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsConnectionBoundaryException(exception))
            {
                // Connection handlers isolate protocol failures; shutdown must still release the listener and socket path.
            }
            if (ownsSocketPath)
            {
                DeleteOwnedSocketPath(_options.SocketPath);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _disposeCancellation.Cancel();
            _disposeCancellation.Dispose();
            CryptographicOperations.ZeroMemory(_authenticationKey);
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task HandleTrackedConnectionAsync(Socket socket, CancellationToken cancellationToken)
    {
        try
        {
            await HandleConnectionAsync(socket, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _activeConnections);
            TouchActivity();
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Untrusted replay connections are isolated at this server boundary.")]
    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "The await-using resource has no synchronization-context dependency.")]
    private async Task HandleConnectionAsync(Socket socket, CancellationToken cancellationToken)
    {
        using (socket)
        await using (var stream = new NetworkStream(socket, ownsSocket: false))
        {
            ReplayResponseCorrelation? correlation = null;
            var accepted = false;
            var jobSlot = false;
            try
            {
                using var handshakeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                handshakeCancellation.CancelAfter(_options.ConnectTimeout);
                var helloFrame = await ReplayProtocol.ReadFrameAsync(
                    stream,
                    _options.MaxMetadataBytes,
                    handshakeCancellation.Token).ConfigureAwait(false);
                if (helloFrame is null || helloFrame.Type != ReplayFrameType.ClientHello ||
                    helloFrame.Ordinal != ReplayProtocol.NoOrdinal)
                {
                    throw new LocalReplayRunnerProtocolException("The replay client did not begin with a handshake nonce.");
                }
                var hello = ReplayProtocol.DeserializeMetadata<ReplayClientHello>(
                    helloFrame.Payload,
                    _options.MaxMetadataBytes);
                var authenticatedCapabilities = ReplayPeerAuthentication.Create(
                    _authenticationKey,
                    hello.Nonce,
                    Capabilities,
                    _options.MaxMetadataBytes);
                var capabilities = ReplayProtocol.SerializeMetadata(
                    authenticatedCapabilities,
                    _options.MaxMetadataBytes);
                await ReplayProtocol.WriteFrameAsync(
                    stream,
                    ReplayFrameType.Capabilities,
                    ReplayProtocol.NoOrdinal,
                    capabilities,
                    handshakeCancellation.Token).ConfigureAwait(false);
                var requestFrame = await ReplayProtocol.ReadFrameAsync(
                    stream,
                    _options.MaxMetadataBytes,
                    handshakeCancellation.Token).ConfigureAwait(false);
                if (requestFrame is null)
                {
                    return;
                }
                if (requestFrame.Type != ReplayFrameType.RequestMetadata || requestFrame.Ordinal != ReplayProtocol.NoOrdinal)
                {
                    throw new LocalReplayRunnerProtocolException("The first replay frame must contain request metadata.");
                }

                var envelope = ReplayProtocol.DeserializeMetadata<ReplayRequestEnvelope>(
                    requestFrame.Payload,
                    _options.MaxMetadataBytes);
                var now = DateTimeOffset.UtcNow;
                var declarations = ReplayProjection.ValidateRequestEnvelope(
                    envelope,
                    _options,
                    now,
                    validateAuthorizationTime: true);
                if (requestFrame.Payload.LongLength + declarations.Sum(static item => item.Length) > _options.MaxTotalTransferBytes)
                {
                    throw new LocalReplayRunnerProtocolException("Replay request exceeds the configured total transfer limit.");
                }
                var requestSha256 = ReplayProtocol.ComputeSha256(
                    ReplayProtocol.SerializeMetadata(envelope.Request, _options.MaxMetadataBytes));
                if (!ReplayAuthorization.Validate(
                        _authenticationKey,
                        envelope.Authorization,
                        requestSha256,
                        now))
                {
                    throw new LocalReplayRunnerProtocolException("Replay job authorization is invalid or expired.");
                }
                if (Interlocked.Increment(ref _activeJobs) > _options.MaxConcurrency)
                {
                    Interlocked.Decrement(ref _activeJobs);
                    await SendCapacityUnavailableAsync(stream, cancellationToken).ConfigureAwait(false);
                    return;
                }
                jobSlot = true;
                CleanupKnownJobs(now);
                var context = envelope.Authorization.Context;
                var remaining = context.DeadlineUtc - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new LocalReplayRunnerProtocolException("Replay job authorization expired before payload transfer.");
                }
                using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                jobCancellation.CancelAfter(remaining);
                correlation = ReplayProjection.CreateCorrelation(context);
                if (_knownJobs.Count >= ReplayProtocolLimits.MaximumKnownJobs)
                {
                    throw new LocalReplayRunnerProtocolException("Replay job history is at its bounded capacity.");
                }
                if (!_knownJobs.TryAdd(context.JobId, context.DeadlineUtc))
                {
                    throw new LocalReplayRunnerProtocolException("Replay job ID is already active or completed.");
                }

                await ReplayProtocol.WriteFrameAsync(
                    stream,
                    ReplayFrameType.Accepted,
                    ReplayProtocol.NoOrdinal,
                    ReadOnlyMemory<byte>.Empty,
                    jobCancellation.Token,
                    _options.HeartbeatTimeout).ConfigureAwait(false);
                accepted = true;

                var payloads = new List<byte[]>(declarations.Count);
                foreach (var declaration in declarations)
                {
                    var payloadFrame = await ReplayProtocol.ReadFrameAsync(
                        stream,
                        declaration.Length,
                        jobCancellation.Token,
                        _options.HeartbeatTimeout).ConfigureAwait(false)
                        ?? throw new EndOfStreamException("Replay request ended inside the payload sequence.");
                    ReplayProjection.ValidatePayload(declaration, payloadFrame);
                    if (payloadFrame.Type != ReplayFrameType.RequestPayload)
                    {
                        throw new LocalReplayRunnerProtocolException("Replay request payload sequence contains an unexpected frame.");
                    }
                    payloads.Add(payloadFrame.Payload);
                }

                var request = ReplayProjection.ReconstructRequest(envelope.Request, payloads);
                await ExecuteAndRespondAsync(
                    stream,
                    context,
                    request,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ReplayJobCanceledException)
            {
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException)
            {
                if (accepted)
                {
                    await TrySendFailureAsync(stream, "request-timeout", "Replay request timed out.", correlation, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (ReplayIdleTimeoutException)
            {
                if (accepted)
                {
                    await TrySendFailureAsync(
                        stream,
                        "request-timeout",
                        "Replay request transfer stopped making progress.",
                        correlation,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (LocalReplayRunnerOutputValidationException exception)
            {
                await TrySendFailureAsync(stream, "output-invalid", exception.Message, correlation, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (LocalReplayRunnerProtocolException exception)
            {
                await TrySendFailureAsync(stream, "protocol-rejected", exception.Message, correlation, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsConnectionBoundaryException(exception))
            {
                if (exception is not (IOException or SocketException or EndOfStreamException or ObjectDisposedException))
                {
                    await TrySendFailureAsync(stream, "runner-failure", "Replay runner could not complete the request.", correlation, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                await TrySendFailureAsync(stream, "runner-failure", "Replay runner could not complete the request.", correlation, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (jobSlot)
                {
                    Interlocked.Decrement(ref _activeJobs);
                }
            }
        }
    }

    [SuppressMessage("Reliability", "CA2025:Ensure tasks using IDisposable instances complete before disposal", Justification = "Monitor and heartbeat tasks are awaited before their cancellation sources and send gate are disposed.")]
    private async Task ExecuteAndRespondAsync(
        Stream stream,
        ReplayRunnerJobContext context,
        ProcessingExecutionRequest request,
        CancellationToken serverCancellationToken)
    {
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(serverCancellationToken);
        var remaining = context.DeadlineUtc - DateTimeOffset.UtcNow;
        executionCancellation.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(serverCancellationToken);
        using var sendGate = new SemaphoreSlim(1, 1);
        var clientCanceled = 0;

        var execution = Task.Run(
            async () => await _executor.ExecuteAsync(request, executionCancellation.Token).ConfigureAwait(false),
            CancellationToken.None);
        var monitor = MonitorClientAsync(
            stream,
            executionCancellation,
            () => Interlocked.Exchange(ref clientCanceled, 1),
            monitorCancellation.Token);
        var heartbeat = SendHeartbeatsAsync(
            stream,
            sendGate,
            execution,
            executionCancellation,
            monitorCancellation.Token);

        ProcessingOutcome outcome;
        try
        {
            outcome = await execution.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (executionCancellation.IsCancellationRequested)
        {
            await monitorCancellation.CancelAsync().ConfigureAwait(false);
            await AwaitSupportTasksAsync(monitor, heartbeat).ConfigureAwait(false);
            if (!serverCancellationToken.IsCancellationRequested && Volatile.Read(ref clientCanceled) == 0)
            {
                await TrySendFailureAsync(
                    stream,
                    "deadline-exceeded",
                    "Replay job deadline expired.",
                    ReplayProjection.CreateCorrelation(context),
                    serverCancellationToken).ConfigureAwait(false);
            }
            return;
        }
        catch
        {
            await monitorCancellation.CancelAsync().ConfigureAwait(false);
            await AwaitSupportTasksAsync(monitor, heartbeat).ConfigureAwait(false);
            throw;
        }

        if (Volatile.Read(ref clientCanceled) != 0 || serverCancellationToken.IsCancellationRequested)
        {
            await monitorCancellation.CancelAsync().ConfigureAwait(false);
            await AwaitSupportTasksAsync(monitor, heartbeat).ConfigureAwait(false);
            return;
        }

        try
        {
            var projection = ReplayProjection.ProjectResponse(
                context,
                request,
                outcome,
                _options,
                executionCancellation.Token);
            var metadata = ReplayProtocol.SerializeMetadata(projection.Metadata, _options.MaxMetadataBytes);
            long transferBytes = metadata.Length;
            foreach (var payload in projection.Payloads)
            {
                transferBytes = checked(transferBytes + payload.Length);
                if (transferBytes > _options.MaxTotalTransferBytes)
                {
                    throw new LocalReplayRunnerOutputValidationException("Replay response exceeds the configured total transfer limit.");
                }
            }

            await sendGate.WaitAsync(executionCancellation.Token).ConfigureAwait(false);
            try
            {
                await ReplayProtocol.WriteFrameAsync(
                    stream,
                    ReplayFrameType.ResponseMetadata,
                    ReplayProtocol.NoOrdinal,
                    metadata,
                    executionCancellation.Token,
                    _options.HeartbeatTimeout).ConfigureAwait(false);
                for (var ordinal = 0; ordinal < projection.Payloads.Count; ordinal++)
                {
                    await ReplayProtocol.WriteFrameAsync(
                        stream,
                        ReplayFrameType.ResponsePayload,
                        ordinal,
                        projection.Payloads[ordinal],
                        executionCancellation.Token,
                        _options.HeartbeatTimeout).ConfigureAwait(false);
                }
            }
            finally
            {
                sendGate.Release();
            }
        }
        finally
        {
            await monitorCancellation.CancelAsync().ConfigureAwait(false);
            await AwaitSupportTasksAsync(monitor, heartbeat).ConfigureAwait(false);
        }
    }

    private async Task MonitorClientAsync(
        Stream stream,
        CancellationTokenSource executionCancellation,
        Action markClientCanceled,
        CancellationToken monitorCancellation)
    {
        try
        {
            var frame = await ReplayProtocol.ReadFrameAsync(
                stream,
                _options.MaxMetadataBytes,
                monitorCancellation).ConfigureAwait(false);
            if (frame is null || frame.Type == ReplayFrameType.Cancel)
            {
                if (frame is not null)
                {
                    ReplayProtocol.RequireEmptyControlFrame(frame, ReplayFrameType.Cancel);
                }
                markClientCanceled();
                await executionCancellation.CancelAsync().ConfigureAwait(false);
            }
            else
            {
                markClientCanceled();
                await executionCancellation.CancelAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (monitorCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or SocketException or EndOfStreamException or LocalReplayRunnerProtocolException)
        {
            markClientCanceled();
            await executionCancellation.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task SendHeartbeatsAsync(
        Stream stream,
        SemaphoreSlim sendGate,
        Task execution,
        CancellationTokenSource executionCancellation,
        CancellationToken monitorCancellation)
    {
        try
        {
            while (!execution.IsCompleted)
            {
                await Task.Delay(_options.HeartbeatInterval, monitorCancellation).ConfigureAwait(false);
                if (execution.IsCompleted)
                {
                    break;
                }
                await sendGate.WaitAsync(monitorCancellation).ConfigureAwait(false);
                try
                {
                    await ReplayProtocol.WriteFrameAsync(
                        stream,
                        ReplayFrameType.Heartbeat,
                        ReplayProtocol.NoOrdinal,
                        ReadOnlyMemory<byte>.Empty,
                        monitorCancellation,
                        _options.HeartbeatTimeout).ConfigureAwait(false);
                }
                finally
                {
                    sendGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (monitorCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or ReplayIdleTimeoutException)
        {
            await executionCancellation.CancelAsync().ConfigureAwait(false);
        }
    }

    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "The await-using resource has no synchronization-context dependency.")]
    private async Task SendCapacityUnavailableAsync(Socket socket, CancellationToken cancellationToken)
    {
        using (socket)
        await using (var stream = new NetworkStream(socket, ownsSocket: false))
        {
            await SendCapacityUnavailableAsync(stream, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendCapacityUnavailableAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ConnectTimeout);
        var envelope = new ReplayUnavailableEnvelope(
            "runner-at-capacity",
            "Replay runner is at its configured concurrency limit.",
            (int)Math.Min(_options.HeartbeatInterval.TotalMilliseconds, 300_000));
        var metadata = ReplayProtocol.SerializeMetadata(envelope, _options.MaxMetadataBytes);
        try
        {
            await ReplayProtocol.WriteFrameAsync(
                stream,
                ReplayFrameType.Unavailable,
                ReplayProtocol.NoOrdinal,
                metadata,
                timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private async Task TrySendFailureAsync(
        Stream stream,
        string code,
        string message,
        ReplayResponseCorrelation? correlation,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ConnectTimeout);
        var envelope = new ReplayFailureEnvelope(code, message, correlation);
        var metadata = ReplayProtocol.SerializeMetadata(envelope, _options.MaxMetadataBytes);
        try
        {
            await ReplayProtocol.WriteFrameAsync(
                stream,
                ReplayFrameType.Failure,
                ReplayProtocol.NoOrdinal,
                metadata,
                timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private void CleanupKnownJobs(DateTimeOffset now)
    {
        foreach (var known in _knownJobs)
        {
            if (known.Value <= now)
            {
                _knownJobs.TryRemove(known.Key, out _);
            }
        }
    }

    private bool ShouldIdleShutdown()
    {
        if (_options.IdleShutdownSeconds == 0 || Volatile.Read(ref _activeConnections) != 0)
        {
            return false;
        }
        var idle = DateTimeOffset.UtcNow - new DateTimeOffset(
            Interlocked.Read(ref _lastActivityUtcTicks),
            TimeSpan.Zero);
        return idle >= TimeSpan.FromSeconds(_options.IdleShutdownSeconds);
    }

    private void TouchActivity() => Interlocked.Exchange(ref _lastActivityUtcTicks, DateTimeOffset.UtcNow.UtcTicks);

    private static void ObserveAndPrune(List<Task> connections)
    {
        for (var index = connections.Count - 1; index >= 0; index--)
        {
            if (connections[index].IsCompleted)
            {
                _ = connections[index].Exception;
                connections.RemoveAt(index);
            }
        }
    }

    private static async Task AwaitSupportTasksAsync(Task monitor, Task heartbeat)
    {
        try
        {
            await Task.WhenAll(monitor, heartbeat).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsConnectionBoundaryException(exception))
        {
        }
    }

    private static bool IsConnectionBoundaryException(Exception exception) =>
        exception is IOException or SocketException or EndOfStreamException or ObjectDisposedException or
            OperationCanceledException or ReplayIdleTimeoutException or LocalReplayRunnerProtocolException or
            LocalReplayRunnerOutputValidationException or
            AggregateException;

    private static void SetOwnerOnlySocketPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.SetUnixFileMode(path, ownerOnly);
        if (File.GetUnixFileMode(path) != ownerOnly)
        {
            throw new InvalidOperationException("Unix replay socket permissions could not be restricted to the owner.");
        }
    }

    private static void PrepareUnixSocketPath(string path)
    {
        var parent = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The Unix replay socket has no parent directory.");
        if (!Directory.Exists(parent))
        {
            Directory.CreateDirectory(parent);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    parent,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        if (!Path.Exists(path))
        {
            return;
        }

        using var probe = ReplayProtocol.CreateSocket(ReplayRunnerTransport.UnixDomainSocket);
        try
        {
            probe.Connect(new UnixDomainSocketEndPoint(path));
            throw new InvalidOperationException("Another replay runner is already listening on the configured Unix socket.");
        }
        catch (SocketException exception) when (exception.SocketErrorCode is
            SocketError.ConnectionRefused or SocketError.AddressNotAvailable or SocketError.NotConnected)
        {
            File.Delete(path);
        }
    }

    private static void DeleteOwnedSocketPath(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
