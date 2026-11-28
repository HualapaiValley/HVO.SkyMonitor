using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
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

        return [new CameraAgentStorageLocation("raw-ingress", Path.GetFullPath(options.RawIngressRoot))];
    }
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
