using System.Text;

namespace HVO.SkyMonitor.CameraAgent.Replay;

public enum ReplayRunnerTransport
{
    UnixDomainSocket,
    LoopbackTcp
}

public sealed class LocalReplayRunnerOptions
{
    public const string DefaultSocketPath = "/run/hvo-skymonitor/replay-runner.sock";
    public const long MaximumTransferBytes = 128L * 1024 * 1024;
    public const long MaximumAggregateTransferBytes = 512L * 1024 * 1024;

    public ReplayRunnerTransport Transport { get; init; } = ReplayRunnerTransport.UnixDomainSocket;

    public string SocketPath { get; init; } = DefaultSocketPath;

    public int LoopbackPort { get; init; }

    public ReadOnlyMemory<byte> PreSharedAuthKey { get; init; }

    public string? OwnerOnlyAuthKeyFile { get; init; }

    public int MaxConcurrency { get; init; } = 1;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public int MaxMetadataBytes { get; init; } = 1024 * 1024;

    public long MaxTotalTransferBytes { get; init; } = MaximumTransferBytes;

    public int IdleShutdownSeconds { get; init; }

    public void Validate(bool requireAuthentication = true)
    {
        if (!Enum.IsDefined(Transport))
        {
            throw new InvalidOperationException("Replay transport is invalid.");
        }
        if (Transport == ReplayRunnerTransport.UnixDomainSocket)
        {
            if (!Path.IsPathFullyQualified(SocketPath) || SocketPath.Contains('\0', StringComparison.Ordinal) ||
                Encoding.UTF8.GetByteCount(SocketPath) > 100)
            {
                throw new InvalidOperationException("The Unix socket path must be absolute and no more than 100 UTF-8 bytes.");
            }
        }
        else if (LoopbackPort is < 1 or > 65535)
        {
            throw new InvalidOperationException("A loopback TCP port is required.");
        }
        if (MaxConcurrency is < 1 or > 64)
        {
            throw new InvalidOperationException("Replay maximum concurrency must be between 1 and 64.");
        }
        if (ConnectTimeout < TimeSpan.FromMilliseconds(100) || ConnectTimeout > TimeSpan.FromMinutes(5))
        {
            throw new InvalidOperationException("Replay connect timeout is outside the supported range.");
        }
        if (HeartbeatInterval < TimeSpan.FromMilliseconds(100) || HeartbeatInterval > TimeSpan.FromMinutes(5))
        {
            throw new InvalidOperationException("Replay heartbeat interval is outside the supported range.");
        }
        if (HeartbeatTimeout <= HeartbeatInterval || HeartbeatTimeout > TimeSpan.FromMinutes(10))
        {
            throw new InvalidOperationException("Replay heartbeat timeout must be greater than the interval and no more than 10 minutes.");
        }
        if (MaxMetadataBytes is < 4096 or > 16 * 1024 * 1024)
        {
            throw new InvalidOperationException("Replay metadata limit is outside the supported range.");
        }
        if (MaxTotalTransferBytes < MaxMetadataBytes || MaxTotalTransferBytes > MaximumTransferBytes)
        {
            throw new InvalidOperationException("Replay transfer limit is outside the supported range.");
        }
        if (MaxTotalTransferBytes > MaximumAggregateTransferBytes / (MaxConcurrency * 2L))
        {
            throw new InvalidOperationException(
                "Replay concurrency and request/response transfer limits exceed the process memory reservation.");
        }
        if (IdleShutdownSeconds is < 0 or > 24 * 60 * 60)
        {
            throw new InvalidOperationException("Replay idle shutdown must be between zero and 24 hours.");
        }

        var hasDirectKey = !PreSharedAuthKey.IsEmpty;
        var hasKeyFile = !string.IsNullOrWhiteSpace(OwnerOnlyAuthKeyFile);
        if (hasDirectKey && hasKeyFile)
        {
            throw new ArgumentException("Configure either a direct authentication key or a key file, not both.");
        }
        if (requireAuthentication && !hasDirectKey && !hasKeyFile)
        {
            throw new ArgumentException("A replay runner authentication key is required.");
        }
        if (hasDirectKey && PreSharedAuthKey.Length is < ReplayProtocolLimits.MinimumAuthKeyBytes or > ReplayProtocolLimits.MaximumAuthKeyBytes)
        {
            throw new InvalidOperationException("The authentication key must contain between 32 and 4096 bytes.");
        }
    }

    internal byte[] LoadAuthenticationKey()
    {
        Validate();
        if (!PreSharedAuthKey.IsEmpty)
        {
            return PreSharedAuthKey.ToArray();
        }

        var path = Path.GetFullPath(OwnerOnlyAuthKeyFile!);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is < ReplayProtocolLimits.MinimumAuthKeyBytes or > ReplayProtocolLimits.MaximumAuthKeyBytes)
        {
            throw new InvalidOperationException("The authentication key file must contain between 32 and 4096 bytes.");
        }
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            const UnixFileMode disallowed = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((mode & UnixFileMode.UserRead) == 0 || (mode & disallowed) != 0)
            {
                throw new InvalidOperationException("The authentication key file must be readable only by its owner.");
            }
        }

        return File.ReadAllBytes(path);
    }
}

public sealed record ReplayRunnerJobContext(
    Guid JobId,
    string ReplayExecutionId,
    string GraphRevisionId,
    string LocalPlanIdentity,
    string NodeId,
    int DurableAttempt,
    string DurableClaim,
    DateTimeOffset DeadlineUtc)
{
    public static ReplayRunnerJobContext Create(
        string replayExecutionId,
        string graphRevisionId,
        string localPlanIdentity,
        string nodeId,
        int durableAttempt,
        string durableClaim,
        DateTimeOffset deadlineUtc)
        => new(
            Guid.NewGuid(),
            replayExecutionId,
            graphRevisionId,
            localPlanIdentity,
            nodeId,
            durableAttempt,
            durableClaim,
            deadlineUtc);
}
