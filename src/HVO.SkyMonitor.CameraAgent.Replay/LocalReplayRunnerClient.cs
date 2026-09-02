using System.Net.Sockets;
using System.Security.Cryptography;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Replay;

public sealed class LocalReplayRunnerClient : IDisposable, IAsyncDisposable
{
    public const string MeterName = "HVO.SkyMonitor.CameraAgent.ReplayRunner";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Jobs = Meter.CreateCounter<long>(
        "camera_agent.replay_runner.jobs",
        "{job}");
    private static readonly Counter<long> TransferBytes = Meter.CreateCounter<long>(
        "camera_agent.replay_runner.transfer.bytes",
        "By");
    private static readonly Counter<long> PayloadFrames = Meter.CreateCounter<long>(
        "camera_agent.replay_runner.payload.frames",
        "{frame}");
    private static readonly Counter<long> Heartbeats = Meter.CreateCounter<long>(
        "camera_agent.replay_runner.heartbeats",
        "{heartbeat}");
    private static readonly Histogram<double> DispatchDuration = Meter.CreateHistogram<double>(
        "camera_agent.replay_runner.dispatch.duration",
        "s");
    private static readonly Histogram<double> ExecutionDuration = Meter.CreateHistogram<double>(
        "camera_agent.replay_runner.execution.duration",
        "s");

    private readonly LocalReplayRunnerOptions _options;
    private readonly byte[] _authenticationKey;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private ReplayRunnerCapabilities? _lastCapabilities;
    private LocalReplayRunnerExecutionEvidence? _lastExecutionEvidence;
    private int _disposed;

    public LocalReplayRunnerClient(LocalReplayRunnerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _authenticationKey = options.LoadAuthenticationKey();
    }

    public ReplayRunnerCapabilities? LastCapabilities => Volatile.Read(ref _lastCapabilities);

    public LocalReplayRunnerExecutionEvidence? LastExecutionEvidence => Volatile.Read(ref _lastExecutionEvidence);

    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "The await-using resource has no synchronization-context dependency.")]
    public async ValueTask<ReplayRunnerCapabilities> ProbeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellation.Token);
        probeCancellation.CancelAfter(_options.ConnectTimeout);
        using var socket = ReplayProtocol.CreateSocket(_options.Transport);
        await ConnectAsync(socket, probeCancellation.Token).ConfigureAwait(false);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        var capabilities = await AuthenticatePeerAsync(stream, probeCancellation.Token).ConfigureAwait(false);
        Volatile.Write(ref _lastCapabilities, capabilities);
        return capabilities;
    }

    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "The await-using resource has no synchronization-context dependency.")]
    public async Task<ProcessingOutcome> ExecuteAsync(
        ReplayRunnerJobContext jobContext,
        ProcessingExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var executionStarted = Stopwatch.GetTimestamp();
        var now = DateTimeOffset.UtcNow;
        ReplayProtocol.ValidateJobContext(jobContext, now);
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellation.Token);
        executionCancellation.CancelAfter(jobContext.DeadlineUtc - now);
        var projection = ReplayProjection.ProjectRequest(
            _authenticationKey,
            jobContext,
            request,
            _options,
            now,
            executionCancellation.Token);
        var metadata = ReplayProtocol.SerializeMetadata(projection.Metadata, _options.MaxMetadataBytes);
        ValidateTotalTransfer(metadata.Length, projection.Payloads);

        using var socket = ReplayProtocol.CreateSocket(_options.Transport);
        var dispatchStarted = Stopwatch.GetTimestamp();
        try
        {
            await ConnectAsync(socket, executionCancellation.Token).ConfigureAwait(false);
        }
        catch (LocalReplayRunnerUnavailableException)
        {
            RecordJob("unavailable", executionStarted);
            throw;
        }
        await using var stream = new NetworkStream(socket, ownsSocket: false);

        var accepted = false;
        var heartbeatCount = 0;
        var dispatchDuration = TimeSpan.Zero;
        try
        {
            var capabilities = await AuthenticatePeerAsync(stream, executionCancellation.Token).ConfigureAwait(false);
            ValidateCapabilities(capabilities, request, metadata.Length, projection.Payloads);
            Volatile.Write(ref _lastCapabilities, capabilities);

            await ReplayProtocol.WriteFrameAsync(
                stream,
                ReplayFrameType.RequestMetadata,
                ReplayProtocol.NoOrdinal,
                metadata,
                executionCancellation.Token,
                _options.HeartbeatTimeout).ConfigureAwait(false);
            var acknowledgement = await ReadWithTimeoutAsync(
                stream,
                _options.MaxMetadataBytes,
                _options.ConnectTimeout,
                "Replay runner did not acknowledge the request in time.",
                executionCancellation.Token).ConfigureAwait(false)
                ?? throw new LocalReplayRunnerUnavailableException("Replay runner closed before acknowledging the request.");
            if (acknowledgement.Type == ReplayFrameType.Unavailable)
            {
                ThrowUnavailable(acknowledgement);
            }
            if (acknowledgement.Type == ReplayFrameType.Failure)
            {
                ThrowFailure(acknowledgement, jobContext, requireCorrelation: false);
            }
            ReplayProtocol.RequireEmptyControlFrame(acknowledgement, ReplayFrameType.Accepted);
            accepted = true;
            dispatchDuration = Stopwatch.GetElapsedTime(dispatchStarted);
            DispatchDuration.Record(dispatchDuration.TotalSeconds);

            for (var ordinal = 0; ordinal < projection.Payloads.Count; ordinal++)
            {
                await ReplayProtocol.WriteFrameAsync(
                    stream,
                    ReplayFrameType.RequestPayload,
                    ordinal,
                    projection.Payloads[ordinal],
                    executionCancellation.Token,
                    _options.HeartbeatTimeout).ConfigureAwait(false);
            }
            RecordTransfer("request", metadata.Length, projection.Payloads);

            var responseFrame = await ReadResponseMetadataAsync(
                stream,
                jobContext,
                () => heartbeatCount++,
                executionCancellation.Token).ConfigureAwait(false);
            var response = ReplayProtocol.DeserializeMetadata<ReplayResponseEnvelope>(
                responseFrame.Payload,
                _options.MaxMetadataBytes);
            var declarations = ReplayProjection.GetResponseDeclarations(response, _options.MaxTotalTransferBytes);
            if (responseFrame.Payload.LongLength + declarations.Sum(static item => item.Length) > _options.MaxTotalTransferBytes)
            {
                throw new LocalReplayRunnerOutputValidationException("Replay response exceeds the configured total transfer limit.");
            }

            var payloads = new List<byte[]>(declarations.Count);
            foreach (var declaration in declarations)
            {
                var frame = await ReadWithIdleTimeoutAsync(
                    stream,
                    declaration.Length,
                    _options.HeartbeatTimeout,
                    "Replay runner timed out while sending response payloads.",
                    executionCancellation.Token).ConfigureAwait(false)
                    ?? throw new LocalReplayRunnerUnavailableException("Replay runner closed inside the response payload sequence.");
                if (frame.Type != ReplayFrameType.ResponsePayload)
                {
                    throw new LocalReplayRunnerOutputValidationException("Replay response payload sequence contains an unexpected frame.");
                }
                try
                {
                    ReplayProjection.ValidatePayload(declaration, frame, executionCancellation.Token);
                }
                catch (LocalReplayRunnerProtocolException exception)
                {
                    throw new LocalReplayRunnerOutputValidationException(exception.Message, exception);
                }
                payloads.Add(frame.Payload);
            }
            var outcome = ReplayProjection.ReconstructOutcome(
                response,
                payloads,
                jobContext,
                request,
                _options.MaxTotalTransferBytes,
                executionCancellation.Token);
            RecordTransfer("response", responseFrame.Payload.Length, payloads);
            var executionDuration = Stopwatch.GetElapsedTime(executionStarted);
            Volatile.Write(ref _lastExecutionEvidence, new LocalReplayRunnerExecutionEvidence(
                jobContext.JobId,
                request.RecipeName,
                dispatchDuration,
                executionDuration,
                metadata.Length,
                projection.Payloads.Sum(static payload => (long)payload.Length),
                projection.Payloads.Count,
                responseFrame.Payload.Length,
                payloads.Sum(static payload => (long)payload.Length),
                payloads.Count,
                heartbeatCount));
            RecordJob("completed", executionStarted);
            return outcome;
        }
        catch (OperationCanceledException) when (executionCancellation.IsCancellationRequested)
        {
            if (accepted)
            {
                await TrySendCancelAsync(stream).ConfigureAwait(false);
            }
            RecordJob("cancelled", executionStarted);
            throw;
        }
        catch (LocalReplayRunnerUnavailableException exception)
        {
            RecordJob("unavailable", executionStarted);
            if (accepted && !exception.RequestAccepted)
            {
                throw new LocalReplayRunnerUnavailableException(
                    exception.Message,
                    exception.RetryAfter,
                    exception,
                    requestAccepted: true);
            }
            throw;
        }
        catch (ReplayIdleTimeoutException exception)
        {
            RecordJob("unavailable", executionStarted);
            throw new LocalReplayRunnerUnavailableException(
                "Replay runner stopped accepting the request transfer.",
                innerException: exception,
                requestAccepted: accepted);
        }
        catch (LocalReplayRunnerOutputValidationException)
        {
            RecordJob("output-invalid", executionStarted);
            throw;
        }
        catch (LocalReplayRunnerProtocolException exception) when (!accepted)
        {
            RecordJob("unavailable", executionStarted);
            throw new LocalReplayRunnerUnavailableException(
                "Replay runner handshake or request acceptance failed.",
                innerException: exception);
        }
        catch (LocalReplayRunnerProtocolException)
        {
            RecordJob("protocol-failed", executionStarted);
            throw;
        }
        catch (Exception exception) when (exception is IOException or SocketException or EndOfStreamException or ObjectDisposedException)
        {
            RecordJob("disconnected", executionStarted);
            throw new LocalReplayRunnerUnavailableException(
                "Replay runner connection became unavailable.",
                innerException: exception,
                requestAccepted: accepted);
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

    private async Task ConnectAsync(Socket socket, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ConnectTimeout);
        try
        {
            await socket.ConnectAsync(ReplayProtocol.CreateEndPoint(_options), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LocalReplayRunnerUnavailableException("Replay runner connection timed out.", innerException: exception);
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
            throw new LocalReplayRunnerUnavailableException("Replay runner is unavailable.", innerException: exception);
        }
    }

    private async Task<ReplayFrame> ReadResponseMetadataAsync(
        Stream stream,
        ReplayRunnerJobContext context,
        Action recordHeartbeat,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var frame = await ReadWithTimeoutAsync(
                stream,
                _options.MaxMetadataBytes,
                _options.HeartbeatTimeout,
                "Replay runner heartbeat timed out.",
                cancellationToken).ConfigureAwait(false)
                ?? throw new LocalReplayRunnerUnavailableException("Replay runner closed before returning a final response.");
            if (frame.Type == ReplayFrameType.Heartbeat)
            {
                ReplayProtocol.RequireEmptyControlFrame(frame, ReplayFrameType.Heartbeat);
                Heartbeats.Add(1);
                recordHeartbeat();
                continue;
            }
            if (frame.Type == ReplayFrameType.Unavailable)
            {
                ThrowUnavailable(frame);
            }
            if (frame.Type == ReplayFrameType.Failure)
            {
                ThrowFailure(frame, context, requireCorrelation: true);
            }
            if (frame.Type != ReplayFrameType.ResponseMetadata || frame.Ordinal != ReplayProtocol.NoOrdinal)
            {
                throw new LocalReplayRunnerProtocolException("Replay runner returned an unexpected final frame.");
            }
            return frame;
        }
    }

    private static async Task<ReplayFrame?> ReadWithTimeoutAsync(
        Stream stream,
        long maximumPayloadBytes,
        TimeSpan timeoutValue,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutValue);
        try
        {
            return await ReplayProtocol.ReadFrameAsync(stream, maximumPayloadBytes, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LocalReplayRunnerUnavailableException(timeoutMessage, innerException: exception);
        }
    }

    private static async Task<ReplayFrame?> ReadWithIdleTimeoutAsync(
        Stream stream,
        long maximumPayloadBytes,
        TimeSpan idleTimeout,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReplayProtocol.ReadFrameAsync(
                stream,
                maximumPayloadBytes,
                cancellationToken,
                idleTimeout).ConfigureAwait(false);
        }
        catch (ReplayIdleTimeoutException exception)
        {
            throw new LocalReplayRunnerUnavailableException(timeoutMessage, innerException: exception);
        }
    }

    private void ValidateTotalTransfer(int metadataLength, IReadOnlyList<ReadOnlyMemory<byte>> payloads)
    {
        long total = metadataLength;
        foreach (var payload in payloads)
        {
            total = checked(total + payload.Length);
            if (total > _options.MaxTotalTransferBytes)
            {
                throw new LocalReplayRunnerProtocolException("Replay request exceeds the configured total transfer limit.");
            }
        }
    }

    private static void RecordTransfer(
        string direction,
        int metadataBytes,
        IEnumerable<ReadOnlyMemory<byte>> payloads)
    {
        long payloadBytes = 0;
        long payloadCount = 0;
        foreach (var payload in payloads)
        {
            payloadBytes += payload.Length;
            payloadCount++;
        }
        var tags = new TagList { { "direction", direction } };
        TransferBytes.Add(metadataBytes, new TagList { { "direction", direction }, { "kind", "metadata" } });
        TransferBytes.Add(payloadBytes, new TagList { { "direction", direction }, { "kind", "payload" } });
        PayloadFrames.Add(payloadCount, tags);
    }

    private static void RecordTransfer(string direction, int metadataBytes, IEnumerable<byte[]> payloads)
        => RecordTransfer(direction, metadataBytes, payloads.Select(static payload => (ReadOnlyMemory<byte>)payload));

    private static void RecordJob(string outcome, long startedTimestamp)
    {
        var tags = new TagList { { "outcome", outcome } };
        Jobs.Add(1, tags);
        ExecutionDuration.Record(Stopwatch.GetElapsedTime(startedTimestamp).TotalSeconds, tags);
    }

    private async Task<ReplayRunnerCapabilities> AuthenticatePeerAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var clientNonce = ReplayPeerAuthentication.CreateNonce();
        var hello = ReplayProtocol.SerializeMetadata(
            new ReplayClientHello(clientNonce),
            _options.MaxMetadataBytes);
        try
        {
            await ReplayProtocol.WriteFrameAsync(
                stream,
                ReplayFrameType.ClientHello,
                ReplayProtocol.NoOrdinal,
                hello,
                cancellationToken,
                _options.ConnectTimeout).ConfigureAwait(false);
        }
        catch (ReplayIdleTimeoutException exception)
        {
            throw new LocalReplayRunnerUnavailableException(
                "Replay runner stopped accepting the authentication handshake.",
                innerException: exception);
        }
        var capabilityFrame = await ReadWithTimeoutAsync(
            stream,
            _options.MaxMetadataBytes,
            _options.ConnectTimeout,
            "Replay runner did not declare its capabilities in time.",
            cancellationToken).ConfigureAwait(false)
            ?? throw new LocalReplayRunnerUnavailableException("Replay runner closed before declaring its capabilities.");
        if (capabilityFrame.Type == ReplayFrameType.Unavailable)
        {
            ThrowUnavailable(capabilityFrame);
        }
        if (capabilityFrame.Type == ReplayFrameType.Failure)
        {
            ThrowFailure(capabilityFrame, null, requireCorrelation: false);
        }
        if (capabilityFrame.Type != ReplayFrameType.Capabilities || capabilityFrame.Ordinal != ReplayProtocol.NoOrdinal)
        {
            throw new LocalReplayRunnerProtocolException("Replay runner did not begin with a capability declaration.");
        }
        var authenticatedCapabilities = ReplayProtocol.DeserializeMetadata<ReplayAuthenticatedCapabilities>(
            capabilityFrame.Payload,
            _options.MaxMetadataBytes);
        if (!ReplayPeerAuthentication.Validate(
                _authenticationKey,
                clientNonce,
                authenticatedCapabilities,
                _options.MaxMetadataBytes))
        {
            throw new LocalReplayRunnerProtocolException("Replay runner capability authentication failed.");
        }
        var capabilities = authenticatedCapabilities.Capabilities;
        if (capabilities is null || capabilities.ProtocolVersion != ReplayProtocol.Version || capabilities.ProcessId <= 0 ||
            capabilities.ProcessStartedUtc.Offset != TimeSpan.Zero || capabilities.MaxConcurrency is < 1 or > 64 ||
            capabilities.MaxTransferBytes < _options.MaxMetadataBytes || capabilities.BuiltInRecipes is null ||
            capabilities.BuiltInRecipes.Count is < 1 or > 256 ||
            capabilities.BuiltInRecipes.Any(static recipe => recipe is null ||
                !ReplayProtocol.IsBoundedIdentifier(recipe.Name) ||
                !ReplayProtocol.IsBoundedIdentifier(recipe.SemanticVersion) ||
                !ReplayProtocol.IsBoundedIdentifier(recipe.ImplementationVersion)) ||
            capabilities.BuiltInRecipes.Select(static recipe => recipe.Name)
                .Distinct(StringComparer.Ordinal).Count() != capabilities.BuiltInRecipes.Count)
        {
            throw new LocalReplayRunnerProtocolException("Replay runner capability metadata is invalid.");
        }
        return capabilities;
    }

    private static void ValidateCapabilities(
        ReplayRunnerCapabilities capabilities,
        ProcessingExecutionRequest request,
        int metadataLength,
        IReadOnlyList<ReadOnlyMemory<byte>> payloads)
    {
        if (!BuiltInProcessingRecipes.TryGetDefinition(request.RecipeName, out var expected) || expected is null ||
            capabilities.BuiltInRecipes.SingleOrDefault(recipe =>
                string.Equals(recipe.Name, request.RecipeName, StringComparison.Ordinal)) is not { } actual ||
            !string.Equals(actual.SemanticVersion, expected.SemanticVersion, StringComparison.Ordinal) ||
            !string.Equals(actual.ImplementationVersion, expected.ImplementationVersion, StringComparison.Ordinal))
        {
            throw new LocalReplayRunnerUnavailableException(
                $"Replay runner does not declare the required recipe capability '{request.RecipeName}'.");
        }

        long requiredBytes = metadataLength;
        foreach (var payload in payloads)
        {
            requiredBytes = checked(requiredBytes + payload.Length);
        }
        if (requiredBytes > capabilities.MaxTransferBytes)
        {
            throw new LocalReplayRunnerUnavailableException(
                "Replay runner transfer capacity is smaller than the immutable job inputs.");
        }
    }

    private void ThrowUnavailable(ReplayFrame frame)
    {
        if (frame.Ordinal != ReplayProtocol.NoOrdinal)
        {
            throw new LocalReplayRunnerProtocolException("Replay unavailable frame has an invalid ordinal.");
        }
        var unavailable = ReplayProtocol.DeserializeMetadata<ReplayUnavailableEnvelope>(frame.Payload, _options.MaxMetadataBytes);
        TimeSpan? retryAfter = unavailable.RetryAfterMilliseconds is >= 0 and <= 300_000
            ? TimeSpan.FromMilliseconds(unavailable.RetryAfterMilliseconds.Value)
            : null;
        throw new LocalReplayRunnerUnavailableException(unavailable.Message, retryAfter);
    }

    private void ThrowFailure(ReplayFrame frame, ReplayRunnerJobContext? context, bool requireCorrelation)
    {
        if (frame.Ordinal != ReplayProtocol.NoOrdinal)
        {
            throw new LocalReplayRunnerProtocolException("Replay failure frame has an invalid ordinal.");
        }
        var failure = ReplayProtocol.DeserializeMetadata<ReplayFailureEnvelope>(frame.Payload, _options.MaxMetadataBytes);
        if (requireCorrelation && (context is null || failure.Correlation != ReplayProjection.CreateCorrelation(context)))
        {
            throw new LocalReplayRunnerProtocolException("Replay failure response correlation is invalid.");
        }
        throw new LocalReplayRunnerProtocolException($"Replay runner rejected the job ({failure.Code}): {failure.Message}");
    }

    private static async Task TrySendCancelAsync(Stream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await ReplayProtocol.WriteFrameAsync(
                stream,
                ReplayFrameType.Cancel,
                ReplayProtocol.NoOrdinal,
                ReadOnlyMemory<byte>.Empty,
                timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The connection may already be gone; cancellation remains best effort.
        }
    }
}
