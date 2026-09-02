using System.Globalization;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Replay;

namespace HVO.SkyMonitor.CameraAgent.ReplayRunner;

internal static class RunnerConfiguration
{
    internal static LocalReplayRunnerOptions Load(bool requireAuthentication)
    {
        var transport = ParseTransport(Environment.GetEnvironmentVariable("HVO_REPLAY_TRANSPORT"));
        var directKey = Environment.GetEnvironmentVariable("HVO_REPLAY_AUTH_KEY");
        var options = new LocalReplayRunnerOptions
        {
            Transport = transport,
            SocketPath = ReadString("HVO_REPLAY_SOCKET_PATH", LocalReplayRunnerOptions.DefaultSocketPath),
            LoopbackPort = ReadInt32("HVO_REPLAY_LOOPBACK_PORT", 0),
            PreSharedAuthKey = directKey is null ? ReadOnlyMemory<byte>.Empty : Encoding.UTF8.GetBytes(directKey),
            OwnerOnlyAuthKeyFile = Environment.GetEnvironmentVariable("HVO_REPLAY_AUTH_KEY_FILE"),
            MaxConcurrency = ReadInt32("HVO_REPLAY_MAX_CONCURRENCY", 1),
            ConnectTimeout = TimeSpan.FromSeconds(ReadDouble("HVO_REPLAY_CONNECT_TIMEOUT_SECONDS", 10)),
            HeartbeatInterval = TimeSpan.FromSeconds(ReadDouble("HVO_REPLAY_HEARTBEAT_INTERVAL_SECONDS", 5)),
            HeartbeatTimeout = TimeSpan.FromSeconds(ReadDouble("HVO_REPLAY_HEARTBEAT_TIMEOUT_SECONDS", 20)),
            MaxMetadataBytes = ReadInt32("HVO_REPLAY_MAX_METADATA_BYTES", 1024 * 1024),
            MaxTotalTransferBytes = ReadInt64(
                "HVO_REPLAY_MAX_TRANSFER_BYTES",
                LocalReplayRunnerOptions.MaximumTransferBytes),
            IdleShutdownSeconds = ReadInt32("HVO_REPLAY_IDLE_SHUTDOWN_SECONDS", 0)
        };
        options.Validate(requireAuthentication);
        return options;
    }

    private static ReplayRunnerTransport ParseTransport(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        null or "" or "UDS" or "UNIX" or "UNIXDOMAINSOCKET" => ReplayRunnerTransport.UnixDomainSocket,
        "TCP" or "LOOPBACKTCP" => ReplayRunnerTransport.LoopbackTcp,
        _ => throw new InvalidOperationException("HVO_REPLAY_TRANSPORT must be 'unix' or 'tcp'.")
    };

    private static string ReadString(string name, string defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }

    private static int ReadInt32(string name, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"{name} must be an integer.");
    }

    private static long ReadInt64(string name, long defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"{name} must be an integer.");
    }

    private static double ReadDouble(string name, double defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed)
            ? parsed
            : throw new InvalidOperationException($"{name} must be a finite number.");
    }
}
