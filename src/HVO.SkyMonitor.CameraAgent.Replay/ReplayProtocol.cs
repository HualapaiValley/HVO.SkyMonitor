using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Replay;

internal static class ReplayProtocolLimits
{
    internal const int MinimumAuthKeyBytes = 32;
    internal const int MaximumAuthKeyBytes = 4096;
    internal const int MaximumInputs = 256;
    internal const int MaximumAuxiliaryInputs = 32;
    internal const int MaximumProducts = 64;
    internal const int MaximumSourceIds = 256;
    internal const int MaximumAlgorithms = 64;
    internal const int MaximumKnownJobs = 100_000;
    internal const int MaximumAnnotationItems = 4096;
    internal const int MaximumStringLength = 4096;
    internal const int MaximumIdentifierLength = 256;
    internal const int MaximumJsonArrayLength = 4096;
    internal const int MaximumJsonObjectProperties = 1024;
    internal static readonly TimeSpan AuthorizationLifetime = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan MaximumAuthorizationLifetime = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan MaximumJobDeadline = TimeSpan.FromHours(24);
}

internal enum ReplayFrameType : byte
{
    RequestMetadata = 1,
    Accepted = 2,
    RequestPayload = 3,
    Heartbeat = 4,
    Cancel = 5,
    ResponseMetadata = 6,
    ResponsePayload = 7,
    Unavailable = 8,
    Failure = 9,
    Capabilities = 10,
    ClientHello = 11
}

internal sealed record ReplayFrame(ReplayFrameType Type, int Ordinal, byte[] Payload);

[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Internal transport control-flow exception never crosses the assembly boundary.")]
internal sealed class ReplayIdleTimeoutException(string message, Exception innerException)
    : TimeoutException(message, innerException);

internal static class ReplayProtocol
{
    public const int Version = 1;

    private const uint Magic = 0x48565250;
    private const int HeaderLength = 20;
    internal const int NoOrdinal = -1;

    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 32
    };

    static ReplayProtocol()
    {
        SerializerOptions.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
    }

    internal static byte[] SerializeMetadata<T>(T value, int maximumBytes)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);
        if (bytes.Length > maximumBytes)
        {
            throw new LocalReplayRunnerProtocolException("Replay control metadata exceeds the configured limit.");
        }
        return bytes;
    }

    internal static T DeserializeMetadata<T>(ReadOnlyMemory<byte> bytes, int maximumBytes)
    {
        if (bytes.IsEmpty || bytes.Length > maximumBytes)
        {
            throw new LocalReplayRunnerProtocolException("Replay control metadata is empty or exceeds the configured limit.");
        }

        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = SerializerOptions.MaxDepth
            });
            ValidateJsonShape(document.RootElement, 0);
            return JsonSerializer.Deserialize<T>(bytes.Span, SerializerOptions)
                ?? throw new LocalReplayRunnerProtocolException("Replay control metadata is null.");
        }
        catch (JsonException exception)
        {
            throw new LocalReplayRunnerProtocolException("Replay control metadata is not strict JSON.", exception);
        }
    }

    internal static async ValueTask WriteFrameAsync(
        Stream stream,
        ReplayFrameType type,
        int ordinal,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken,
        TimeSpan? idleTimeout = null)
    {
        var header = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt32BigEndian(header, Magic);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), Version);
        header[6] = (byte)type;
        header[7] = 0;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), ordinal);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(12), payload.Length);
        await WriteWithProgressTimeoutAsync(stream, header, idleTimeout, cancellationToken).ConfigureAwait(false);
        const int chunkLength = 64 * 1024;
        for (var offset = 0; offset < payload.Length; offset += chunkLength)
        {
            await WriteWithProgressTimeoutAsync(
                stream,
                payload.Slice(offset, Math.Min(chunkLength, payload.Length - offset)),
                idleTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        await FlushWithProgressTimeoutAsync(stream, idleTimeout, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask WriteWithProgressTimeoutAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        TimeSpan? idleTimeout,
        CancellationToken cancellationToken)
    {
        using var progress = CreateProgressCancellation(idleTimeout, cancellationToken);
        try
        {
            await stream.WriteAsync(payload, progress?.Token ?? cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (progress is not null &&
            progress.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ReplayIdleTimeoutException("Replay frame write stopped making progress.", exception);
        }
    }

    private static async ValueTask FlushWithProgressTimeoutAsync(
        Stream stream,
        TimeSpan? idleTimeout,
        CancellationToken cancellationToken)
    {
        using var progress = CreateProgressCancellation(idleTimeout, cancellationToken);
        try
        {
            await stream.FlushAsync(progress?.Token ?? cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (progress is not null &&
            progress.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ReplayIdleTimeoutException("Replay frame flush stopped making progress.", exception);
        }
    }

    private static CancellationTokenSource? CreateProgressCancellation(
        TimeSpan? idleTimeout,
        CancellationToken cancellationToken)
    {
        if (idleTimeout is null)
        {
            return null;
        }
        var progress = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        progress.CancelAfter(idleTimeout.Value);
        return progress;
    }

    internal static async ValueTask<ReplayFrame?> ReadFrameAsync(
        Stream stream,
        long maximumPayloadBytes,
        CancellationToken cancellationToken,
        TimeSpan? idleTimeout = null)
    {
        var header = new byte[HeaderLength];
        if (!await ReadExactlyAsync(
                stream,
                header,
                allowEof: true,
                idleTimeout,
                cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        if (BinaryPrimitives.ReadUInt32BigEndian(header) != Magic ||
            BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4)) != Version ||
            header[7] != 0 ||
            !Enum.IsDefined((ReplayFrameType)header[6]))
        {
            throw new LocalReplayRunnerProtocolException("Replay frame header is invalid or uses an unsupported protocol version.");
        }

        var length = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(12));
        if (length < 0 || length > maximumPayloadBytes || length > Array.MaxLength)
        {
            throw new LocalReplayRunnerProtocolException("Replay frame payload length exceeds the configured limit.");
        }
        var payload = GC.AllocateUninitializedArray<byte>((int)length);
        if (payload.Length > 0)
        {
            await ReadExactlyAsync(
                stream,
                payload,
                allowEof: false,
                idleTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        return new ReplayFrame(
            (ReplayFrameType)header[6],
            BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8)),
            payload);
    }

    internal static void RequireEmptyControlFrame(ReplayFrame frame, ReplayFrameType expected)
    {
        if (frame.Type != expected || frame.Ordinal != NoOrdinal || frame.Payload.Length != 0)
        {
            throw new LocalReplayRunnerProtocolException($"Expected an empty {expected} frame.");
        }
    }

    internal static EndPoint CreateEndPoint(LocalReplayRunnerOptions options) => options.Transport switch
    {
        ReplayRunnerTransport.UnixDomainSocket => new UnixDomainSocketEndPoint(options.SocketPath),
        ReplayRunnerTransport.LoopbackTcp => new IPEndPoint(IPAddress.Loopback, options.LoopbackPort),
        _ => throw new ArgumentOutOfRangeException(nameof(options))
    };

    internal static Socket CreateSocket(ReplayRunnerTransport transport) => transport switch
    {
        ReplayRunnerTransport.UnixDomainSocket => new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified),
        ReplayRunnerTransport.LoopbackTcp => new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        },
        _ => throw new ArgumentOutOfRangeException(nameof(transport))
    };

    internal static string ComputeSha256(ReadOnlyMemory<byte> payload) =>
        Convert.ToHexString(SHA256.HashData(payload.Span));

    internal static bool IsUppercaseSha256(string? value) =>
        value is { Length: SHA256.HashSizeInBytes * 2 } &&
        value.All(static character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    internal static void ValidateJobContext(ReplayRunnerJobContext context, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.JobId == Guid.Empty ||
            !IsBoundedIdentifier(context.ReplayExecutionId) ||
            !IsBoundedIdentifier(context.GraphRevisionId) ||
            !IsBoundedIdentifier(context.LocalPlanIdentity) ||
            !IsBoundedIdentifier(context.NodeId) ||
            context.DurableAttempt < 1 ||
            !IsBoundedIdentifier(context.DurableClaim))
        {
            throw new LocalReplayRunnerProtocolException("Replay job context contains an empty or invalid identity.");
        }
        if (context.DeadlineUtc.Offset != TimeSpan.Zero || context.DeadlineUtc <= now ||
            context.DeadlineUtc - now > ReplayProtocolLimits.MaximumJobDeadline)
        {
            throw new LocalReplayRunnerProtocolException("Replay job deadline must be UTC, unexpired, and no more than 24 hours in the future.");
        }
    }

    internal static bool IsBoundedIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= ReplayProtocolLimits.MaximumIdentifierLength &&
        !value.Any(char.IsControl);

    private static async ValueTask<bool> ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        bool allowEof,
        TimeSpan? idleTimeout,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            int count;
            if (idleTimeout is null)
            {
                count = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            }
            else
            {
                using var idleCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idleCancellation.CancelAfter(idleTimeout.Value);
                try
                {
                    count = await stream.ReadAsync(buffer[read..], idleCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new ReplayIdleTimeoutException("Replay frame transfer stopped making progress.", exception);
                }
            }
            if (count == 0)
            {
                if (allowEof && read == 0)
                {
                    return false;
                }
                throw new EndOfStreamException("Replay connection ended inside a frame.");
            }
            read += count;
        }
        return true;
    }

    private static void ValidateJsonShape(JsonElement element, int depth)
    {
        if (depth > SerializerOptions.MaxDepth)
        {
            throw new LocalReplayRunnerProtocolException("Replay control metadata exceeds the maximum JSON depth.");
        }
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var propertyCount = 0;
            foreach (var property in element.EnumerateObject())
            {
                propertyCount++;
                if (propertyCount > ReplayProtocolLimits.MaximumJsonObjectProperties ||
                    property.Name.Length > ReplayProtocolLimits.MaximumIdentifierLength ||
                    !names.Add(property.Name))
                {
                    throw new LocalReplayRunnerProtocolException("Replay control metadata contains duplicate or excessive properties.");
                }
                ValidateJsonShape(property.Value, depth + 1);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var count = 0;
            foreach (var item in element.EnumerateArray())
            {
                count++;
                if (count > ReplayProtocolLimits.MaximumJsonArrayLength)
                {
                    throw new LocalReplayRunnerProtocolException("Replay control metadata contains an oversized list.");
                }
                ValidateJsonShape(item, depth + 1);
            }
        }
        else if (element.ValueKind == JsonValueKind.String &&
            (element.GetString()?.Length ?? 0) > ReplayProtocolLimits.MaximumStringLength)
        {
            throw new LocalReplayRunnerProtocolException("Replay control metadata contains an oversized string.");
        }
    }
}

internal sealed record ReplayJobAuthorization(
    ReplayRunnerJobContext Context,
    DateTimeOffset AuthorizationExpiresUtc,
    string RequestSha256,
    string TokenSha256);

internal sealed record ReplayClientHello(string Nonce);

internal sealed record ReplayAuthenticatedCapabilities(
    ReplayRunnerCapabilities Capabilities,
    string ClientNonce,
    string ServerNonce,
    string AuthenticationTagSha256);

internal sealed record ReplayRequestEnvelope(
    int ProtocolVersion,
    ReplayJobAuthorization Authorization,
    ReplayExecutionRequestMetadata Request);

internal sealed record ReplayExecutionRequestMetadata(
    string RecipeName,
    JsonElement Options,
    ProcessingInputSelector Input,
    IReadOnlyList<ReplayArtifactMetadata> Inputs,
    string OutputVariant,
    ProcessingAnnotationInput? Annotation,
    IReadOnlyList<ReplayAuxiliaryInputMetadata>? AuxiliaryInputs,
    Guid? InputArtifactId);

internal sealed record ReplayArtifactMetadata(
    Guid ArtifactId,
    FrameArtifactRole Role,
    string Variant,
    string RecipeIdentitySha256,
    string MediaType,
    FrameLayoutDescriptor? Layout,
    int? PayloadOrdinal,
    long PayloadLength,
    string PayloadSha256,
    DateTimeOffset CreatedUtc,
    TimeSpan Integration,
    ProcessingCompatibilityIdentity Compatibility,
    long? CaptureSequence,
    IReadOnlyList<Guid>? SourceArtifactIds,
    DateTimeOffset? ObservationStartedUtc,
    DateTimeOffset? ObservationEndedUtc,
    ProcessingCaptureConditions? Conditions,
    ProcessingProductKind ProductKind,
    string? SchemaVersion,
    string? ContentIdentitySha256,
    Guid? CaptureId,
    string? DescriptorIdentitySha256);

internal sealed record ReplayAuxiliaryInputMetadata(
    string Name,
    ProcessingAuxiliaryInputKind Kind,
    ProcessingInputSelector? Selector,
    string? SchemaVersion,
    string? IdentitySha256,
    int? PayloadOrdinal,
    long PayloadLength,
    string PayloadSha256,
    Guid? ArtifactId,
    string? ChecksumSha256);

internal sealed record ReplayResponseCorrelation(
    Guid JobId,
    string ReplayExecutionId,
    string GraphRevisionId,
    string LocalPlanIdentity,
    string NodeId,
    int DurableAttempt,
    string DurableClaim);

internal sealed record ReplayResponseEnvelope(
    int ProtocolVersion,
    ReplayResponseCorrelation Correlation,
    ProcessingOutcomeStatus Status,
    string? ReasonCode,
    string? Field,
    IReadOnlyList<ReplayProductMetadata> Products);

internal sealed record ReplayProductMetadata(
    FrameArtifactRole Role,
    string Variant,
    string OutputIdentitySha256,
    string MediaType,
    FrameLayoutDescriptor? Layout,
    int? PayloadOrdinal,
    long PayloadLength,
    string PayloadSha256,
    string ChecksumSha256,
    ProcessingRecipeIdentity Recipe,
    IReadOnlyList<ProcessingAlgorithmIdentity> Algorithms,
    IReadOnlyList<Guid> SourceArtifactIds,
    TimeSpan TotalIntegration,
    ProcessingCompatibilityIdentity Compatibility,
    ProcessingProductKind Kind,
    string? SchemaVersion,
    string? ContentIdentitySha256);

internal sealed record ReplayUnavailableEnvelope(string Code, string Message, int? RetryAfterMilliseconds);

internal sealed record ReplayFailureEnvelope(
    string Code,
    string Message,
    ReplayResponseCorrelation? Correlation);

internal static class ReplayAuthorization
{
    internal static ReplayJobAuthorization Create(
        byte[] key,
        ReplayRunnerJobContext context,
        string requestSha256,
        DateTimeOffset now)
    {
        ReplayProtocol.ValidateJobContext(context, now);
        if (!ReplayProtocol.IsUppercaseSha256(requestSha256))
        {
            throw new LocalReplayRunnerProtocolException("Replay request identity is invalid.");
        }
        var expires = now.Add(ReplayProtocolLimits.AuthorizationLifetime);
        if (expires > context.DeadlineUtc)
        {
            expires = context.DeadlineUtc;
        }
        return new ReplayJobAuthorization(
            context,
            expires,
            requestSha256,
            ComputeToken(key, context, expires, requestSha256));
    }

    internal static bool Validate(
        byte[] key,
        ReplayJobAuthorization authorization,
        string requestSha256,
        DateTimeOffset now)
    {
        if (authorization is null || authorization.AuthorizationExpiresUtc.Offset != TimeSpan.Zero ||
            authorization.AuthorizationExpiresUtc <= now ||
            authorization.AuthorizationExpiresUtc - now > ReplayProtocolLimits.MaximumAuthorizationLifetime ||
            authorization.AuthorizationExpiresUtc > authorization.Context.DeadlineUtc ||
            !ReplayProtocol.IsUppercaseSha256(authorization.RequestSha256) ||
            !ReplayProtocol.IsUppercaseSha256(requestSha256) ||
            !ReplayProtocol.IsUppercaseSha256(authorization.TokenSha256))
        {
            return false;
        }
        ReplayProtocol.ValidateJobContext(authorization.Context, now);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(authorization.RequestSha256),
                Convert.FromHexString(requestSha256)))
        {
            return false;
        }
        var expected = Convert.FromHexString(ComputeToken(
            key,
            authorization.Context,
            authorization.AuthorizationExpiresUtc,
            authorization.RequestSha256));
        var supplied = Convert.FromHexString(authorization.TokenSha256);
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    private static string ComputeToken(
        byte[] key,
        ReplayRunnerJobContext context,
        DateTimeOffset expiresUtc,
        string requestSha256)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        AppendInt32(hmac, ReplayProtocol.Version);
        hmac.AppendData(context.JobId.ToByteArray());
        AppendString(hmac, context.ReplayExecutionId);
        AppendString(hmac, context.GraphRevisionId);
        AppendString(hmac, context.LocalPlanIdentity);
        AppendString(hmac, context.NodeId);
        AppendInt32(hmac, context.DurableAttempt);
        AppendString(hmac, context.DurableClaim);
        AppendInt64(hmac, context.DeadlineUtc.UtcTicks);
        AppendInt64(hmac, expiresUtc.UtcTicks);
        hmac.AppendData(Convert.FromHexString(requestSha256));
        return Convert.ToHexString(hmac.GetHashAndReset());
    }

    private static void AppendString(IncrementalHash hmac, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        AppendInt32(hmac, bytes.Length);
        hmac.AppendData(bytes);
    }

    private static void AppendInt32(IncrementalHash hmac, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        hmac.AppendData(bytes);
    }

    private static void AppendInt64(IncrementalHash hmac, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hmac.AppendData(bytes);
    }
}

internal static class ReplayPeerAuthentication
{
    private const string Domain = "HVO.SkyMonitor.LocalReplayRunner.Capabilities.v1";

    internal static string CreateNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    internal static ReplayAuthenticatedCapabilities Create(
        byte[] key,
        string clientNonce,
        ReplayRunnerCapabilities capabilities,
        int maximumMetadataBytes)
    {
        ValidateNonce(clientNonce);
        var serverNonce = CreateNonce();
        var capabilitiesSha256 = ReplayProtocol.ComputeSha256(
            ReplayProtocol.SerializeMetadata(capabilities, maximumMetadataBytes));
        return new ReplayAuthenticatedCapabilities(
            capabilities,
            clientNonce,
            serverNonce,
            ComputeTag(key, clientNonce, serverNonce, capabilitiesSha256));
    }

    internal static bool Validate(
        byte[] key,
        string expectedClientNonce,
        ReplayAuthenticatedCapabilities envelope,
        int maximumMetadataBytes)
    {
        if (envelope?.Capabilities is null ||
            !string.Equals(envelope.ClientNonce, expectedClientNonce, StringComparison.Ordinal) ||
            !ReplayProtocol.IsUppercaseSha256(envelope.AuthenticationTagSha256))
        {
            return false;
        }
        ValidateNonce(envelope.ClientNonce);
        ValidateNonce(envelope.ServerNonce);
        var capabilitiesSha256 = ReplayProtocol.ComputeSha256(
            ReplayProtocol.SerializeMetadata(envelope.Capabilities, maximumMetadataBytes));
        var expected = Convert.FromHexString(ComputeTag(
            key,
            envelope.ClientNonce,
            envelope.ServerNonce,
            capabilitiesSha256));
        var supplied = Convert.FromHexString(envelope.AuthenticationTagSha256);
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    private static string ComputeTag(
        byte[] key,
        string clientNonce,
        string serverNonce,
        string capabilitiesSha256)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        Append(hmac, Domain);
        Append(hmac, clientNonce);
        Append(hmac, serverNonce);
        hmac.AppendData(Convert.FromHexString(capabilitiesSha256));
        return Convert.ToHexString(hmac.GetHashAndReset());
    }

    private static void ValidateNonce(string nonce)
    {
        if (!ReplayProtocol.IsUppercaseSha256(nonce))
        {
            throw new LocalReplayRunnerProtocolException("Replay handshake nonce is invalid.");
        }
    }

    private static void Append(IncrementalHash hmac, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hmac.AppendData(length);
        hmac.AppendData(bytes);
    }
}
