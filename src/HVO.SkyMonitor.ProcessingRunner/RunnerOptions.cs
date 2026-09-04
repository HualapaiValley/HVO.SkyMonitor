using System.Globalization;
using HVO.SkyMonitor.ProcessingRunner.Contracts;

namespace HVO.SkyMonitor.ProcessingRunner;

/// <summary>Runner configuration; environment-only so the container image needs no config file.</summary>
internal sealed class RunnerOptions
{
    public required Uri LogicHostBaseAddress { get; init; }

    public required string RunnerId { get; init; }

    public required string DisplayName { get; init; }

    public required string ClientId { get; init; }

    public required string ClientSecret { get; init; }

    public int MaxConcurrency { get; init; } = 1;

    public string ResourceClass { get; init; } = ProcessingRunnerCapabilities.DefaultResourceClass;

    public string LatencyClass { get; init; } = ProcessingRunnerCapabilities.DefaultLatencyClass;

    public IReadOnlyList<string> Labels { get; init; } = [];

    public bool GpuAvailable { get; init; }

    public long MaxTransferBytes { get; init; } = ProcessingRunnerProtocol.MaximumTransferBytes;

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan IdleShutdown { get; init; } = TimeSpan.Zero;

    public TimeSpan ShutdownGrace { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan RegistrationRetry { get; init; } = TimeSpan.FromSeconds(5);

    public bool AllowInsecureHttp { get; init; }

    public string LivenessFile { get; init; } = DefaultLivenessFile;

    public TimeSpan ProbeMaxAge { get; init; } = TimeSpan.FromSeconds(90);

    public const string DefaultLivenessFile = "/tmp/hvo-processing-runner.alive";

    public static RunnerOptions Load()
    {
        var address = ReadString("HVO_RUNNER_LOGICHOST_URL", null)
            ?? throw new InvalidOperationException("HVO_RUNNER_LOGICHOST_URL is required.");
        if (!Uri.TryCreate(address.EndsWith('/') ? address : address + "/", UriKind.Absolute, out var baseAddress))
        {
            throw new InvalidOperationException("HVO_RUNNER_LOGICHOST_URL must be an absolute URL.");
        }
        var secretFile = ReadString("HVO_RUNNER_CLIENT_SECRET_FILE", null);
        var secret = secretFile is null
            ? ReadString("HVO_RUNNER_CLIENT_SECRET", null)
            : File.ReadAllText(secretFile).Trim();
        var runnerId = ReadString("HVO_RUNNER_ID", null) ?? DefaultRunnerId();
        var options = new RunnerOptions
        {
            LogicHostBaseAddress = baseAddress,
            RunnerId = runnerId,
            DisplayName = ReadString("HVO_RUNNER_DISPLAY_NAME", null) ?? runnerId,
            ClientId = ReadString("HVO_RUNNER_CLIENT_ID", null)
                ?? throw new InvalidOperationException("HVO_RUNNER_CLIENT_ID is required."),
            ClientSecret = secret
                ?? throw new InvalidOperationException("HVO_RUNNER_CLIENT_SECRET or HVO_RUNNER_CLIENT_SECRET_FILE is required."),
            MaxConcurrency = ReadInt32("HVO_RUNNER_MAX_CONCURRENCY", 1),
            ResourceClass = ReadString("HVO_RUNNER_RESOURCE_CLASS", ProcessingRunnerCapabilities.DefaultResourceClass)!,
            LatencyClass = ReadString("HVO_RUNNER_LATENCY_CLASS", ProcessingRunnerCapabilities.DefaultLatencyClass)!,
            Labels = (ReadString("HVO_RUNNER_LABELS", string.Empty) ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            GpuAvailable = ReadBoolean("HVO_RUNNER_GPU_AVAILABLE", false),
            MaxTransferBytes = ReadInt64("HVO_RUNNER_MAX_TRANSFER_BYTES", ProcessingRunnerProtocol.MaximumTransferBytes),
            RequestTimeout = TimeSpan.FromSeconds(ReadDouble("HVO_RUNNER_REQUEST_TIMEOUT_SECONDS", 300)),
            IdleShutdown = TimeSpan.FromSeconds(ReadDouble("HVO_RUNNER_IDLE_SHUTDOWN_SECONDS", 0)),
            ShutdownGrace = TimeSpan.FromSeconds(ReadDouble("HVO_RUNNER_SHUTDOWN_GRACE_SECONDS", 30)),
            RegistrationRetry = TimeSpan.FromSeconds(ReadDouble("HVO_RUNNER_REGISTRATION_RETRY_SECONDS", 5)),
            AllowInsecureHttp = ReadBoolean("HVO_RUNNER_ALLOW_INSECURE_HTTP", false),
            LivenessFile = ReadString("HVO_RUNNER_LIVENESS_FILE", DefaultLivenessFile)!,
            ProbeMaxAge = TimeSpan.FromSeconds(ReadDouble("HVO_RUNNER_PROBE_MAX_AGE_SECONDS", 90))
        };
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (!ProcessingRunnerProtocol.IsValidRunnerId(RunnerId))
        {
            throw new InvalidOperationException("HVO_RUNNER_ID must be 1-128 characters of letters, digits, '.', '_' or '-'.");
        }
        if (string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > ProcessingRunnerProtocol.MaximumDisplayNameLength)
        {
            throw new InvalidOperationException("HVO_RUNNER_DISPLAY_NAME is invalid.");
        }
        if (MaxConcurrency < 1 || MaxConcurrency > ProcessingRunnerProtocol.MaximumConcurrency)
        {
            throw new InvalidOperationException(
                $"HVO_RUNNER_MAX_CONCURRENCY must be between 1 and {ProcessingRunnerProtocol.MaximumConcurrency}.");
        }
        if (!ProcessingRunnerProtocol.IsValidLabel(ResourceClass) || !ProcessingRunnerProtocol.IsValidLabel(LatencyClass)
            || Labels.Count > ProcessingRunnerProtocol.MaximumLabelCount
            || Labels.Any(static label => !ProcessingRunnerProtocol.IsValidLabel(label)))
        {
            throw new InvalidOperationException("Runner classes and labels must be lower-case labels of at most 64 characters.");
        }
        if (MaxTransferBytes < 1 || MaxTransferBytes > ProcessingRunnerProtocol.MaximumTransferBytes)
        {
            throw new InvalidOperationException("HVO_RUNNER_MAX_TRANSFER_BYTES is out of range.");
        }
        if (RequestTimeout <= TimeSpan.Zero || IdleShutdown < TimeSpan.Zero || ShutdownGrace <= TimeSpan.Zero
            || RegistrationRetry <= TimeSpan.Zero || ProbeMaxAge <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Runner timing values are invalid.");
        }
        if (!string.Equals(LogicHostBaseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !AllowInsecureHttp && !LogicHostBaseAddress.IsLoopback)
        {
            throw new InvalidOperationException(
                "HVO_RUNNER_LOGICHOST_URL must use https unless it is loopback or HVO_RUNNER_ALLOW_INSECURE_HTTP=true.");
        }
    }

    private static string DefaultRunnerId()
    {
        var host = new string(Environment.MachineName
            .Select(char.ToLowerInvariant)
            .Where(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_')
            .ToArray());
        var architecture = new string(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()
            .Select(char.ToLowerInvariant).ToArray());
        return string.IsNullOrWhiteSpace(host) ? $"runner-{Environment.ProcessId}" : $"{host}-{architecture}";
    }

    private static string? ReadString(string name, string? defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
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

    private static bool ReadBoolean(string name, bool defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }
        return bool.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"{name} must be true or false.");
    }
}
