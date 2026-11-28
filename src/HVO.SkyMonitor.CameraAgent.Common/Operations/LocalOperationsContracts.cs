using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Operations;

public enum QuarantineOperationAction
{
    Replay,
    Abandon
}

public sealed class QuarantineOperationCollisionException : InvalidOperationException
{
    public QuarantineOperationCollisionException()
    {
    }

    public QuarantineOperationCollisionException(string message)
        : base(message)
    {
    }

    public QuarantineOperationCollisionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed record CameraAgentStorageLocation(string Alias, string Root);

public interface ICameraAgentStorageResolver
{
    ValueTask<IReadOnlyList<CameraAgentStorageLocation>> GetStorageLocationsAsync(CancellationToken cancellationToken);

    ValueTask<CameraAgentStorageLocation?> ResolveAliasAsync(string storageAlias, CancellationToken cancellationToken);
}

public sealed class CameraAgentStorageResolver(
    ICameraAgentConfigurationAccessor configurationAccessor,
    IOptions<CameraAgentHostOptions> hostOptions) : ICameraAgentStorageResolver
{
    public async ValueTask<IReadOnlyList<CameraAgentStorageLocation>> GetStorageLocationsAsync(
        CancellationToken cancellationToken)
    {
        var configuration = await configurationAccessor.WaitForConfigurationAsync(cancellationToken).ConfigureAwait(false);
        return Resolve(configuration, hostOptions.Value);
    }

    public async ValueTask<CameraAgentStorageLocation?> ResolveAliasAsync(
        string storageAlias,
        CancellationToken cancellationToken)
        => (await GetStorageLocationsAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(location => string.Equals(location.Alias, storageAlias, StringComparison.Ordinal));

    public static IReadOnlyList<CameraAgentStorageLocation> Resolve(
        CameraModuleConfig configuration,
        CameraAgentHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);

        return ResolveLocations(
            ResolveLocalStorageRoots(configuration, options),
            options.RawIngressRoot);
    }

    private static readonly System.Text.Json.JsonSerializerOptions SerializerOptions = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static List<string> ResolveLocalStorageRoots(
        CameraModuleConfig config,
        CameraAgentHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(options);
        var roots = new List<string> { Path.GetFullPath(options.RawIngressRoot) };
        foreach (var step in config.Pipeline.Steps)
        {
            if (!IsStorageStep(step.Type) || step.Enabled == false)
            {
                continue;
            }

            var parsed = ParseStorageOptions(step.Options);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.StorageRoot))
            {
                continue;
            }

            var root = Path.GetFullPath(parsed.StorageRoot);
            if (!roots.Any(existing => PathsEqual(existing, root)))
            {
                roots.Add(root);
            }
        }
        return roots;
    }

    private static bool IsStorageStep(string typeName)
        => string.Equals(typeName, FileStorageCaptureProcessingStep.StableAlias, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(typeName.Split(',', 2)[0].Trim(), nameof(FileStorageCaptureProcessingStep), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(typeName.Split(',', 2)[0].Trim(), typeof(FileStorageCaptureProcessingStep).FullName, StringComparison.OrdinalIgnoreCase);

    private static FileStorageCaptureProcessingStepOptions? ParseStorageOptions(System.Text.Json.JsonElement? options)
    {
        if (options is null)
        {
            return new FileStorageCaptureProcessingStepOptions();
        }
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<FileStorageCaptureProcessingStepOptions>(
                options.Value.GetRawText(), SerializerOptions);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }


    private static List<CameraAgentStorageLocation> ResolveLocations(
        List<string> roots,
        string rawIngressRoot)
    {
        var rawRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rawIngressRoot));
        var locations = new List<CameraAgentStorageLocation>(roots.Count);
        var nextStorage = 1;
        foreach (var configuredRoot in roots)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredRoot));
            var alias = PathsEqual(root, rawRoot) ? "raw-ingress" : $"storage-{nextStorage++}";
            locations.Add(new CameraAgentStorageLocation(alias, root));
        }

        return locations;
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            left,
            right,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

public static class QuarantineOperationReasonCodes
{
    private static readonly HashSet<string> ReplayCodes = new(StringComparer.Ordinal)
    {
        "configuration-corrected",
        "evidence-restored",
        "upstream-recovered"
    };

    private static readonly HashSet<string> AbandonCodes = new(StringComparer.Ordinal)
    {
        "invalid-source",
        "irrecoverable-evidence",
        "operator-approved-loss",
        "processing-failure-acknowledged"
    };

    public static bool IsAllowed(QuarantineOperationAction action, string? reasonCode)
        => reasonCode is not null && action switch
        {
            QuarantineOperationAction.Replay => ReplayCodes.Contains(reasonCode),
            QuarantineOperationAction.Abandon => AbandonCodes.Contains(reasonCode),
            _ => false
        };

    /// <summary>Reduces a reason code to a bounded operator-safe token.</summary>
    public static string Sanitize(string? reasonCode)
    {
        if (string.IsNullOrWhiteSpace(reasonCode) || reasonCode.Length > 128 ||
            reasonCode[0] is < 'a' or > 'z' ||
            reasonCode.Any(static character =>
                character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.')))
        {
            return "unspecified";
        }

        return reasonCode;
    }
}
