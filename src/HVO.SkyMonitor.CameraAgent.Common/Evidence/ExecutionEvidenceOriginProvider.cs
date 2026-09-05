using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Evidence;

/// <summary>
/// The stable identity members of one export origin. The boot session is added by the exporter, because the contract
/// derives the origin identity from every member and therefore treats each boot session as its own sequence space.
/// </summary>
public sealed record ExecutionEvidenceOriginDescriptor(
    Guid OriginInstallationId,
    Guid AgentInstanceId,
    string SoftwareVersion,
    Guid? ObservatoryId = null,
    Guid? LogicalCameraInstallationId = null,
    Guid? InstallationPublicId = null);

/// <summary>
/// Supplies the export origin. The default implementation is entirely local so a standalone CameraAgent that has
/// never registered still produces a stable origin; a registered deployment can replace it with one that carries the
/// central observatory and installation identities.
/// </summary>
public interface IExecutionEvidenceOriginProvider
{
    ValueTask<ExecutionEvidenceOriginDescriptor> GetDescriptorAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Derives the origin installation and agent instance identities deterministically from the local installation root
/// and the configured agent identity, so they survive restarts without a central registration and without adding a
/// durable row that a baseline image would not understand.
/// </summary>
public sealed class LocalExecutionEvidenceOriginProvider(
    ICameraAgentConfigurationAccessor configurationAccessor,
    IOptions<CameraAgentHostOptions> options) : IExecutionEvidenceOriginProvider
{
    public async ValueTask<ExecutionEvidenceOriginDescriptor> GetDescriptorAsync(CancellationToken cancellationToken)
    {
        var configuration = await configurationAccessor.WaitForConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Value.RawIngressRoot));
        var agentId = string.IsNullOrWhiteSpace(configuration.AgentId) ? "cameraagent" : configuration.AgentId;
        return new(
            DeriveIdentity($"origin-installation\n{root}\n{agentId}"),
            DeriveIdentity($"agent-instance\n{agentId}"),
            SoftwareVersion());
    }

    /// <summary>
    /// Folds a namespaced string into a stable RFC 4122 variant-1 identifier. The value is an identity, not a secret:
    /// it must be identical across restarts of the same installation and different across installations.
    /// </summary>
    internal static Guid DeriveIdentity(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    internal static string SoftwareVersion()
    {
        var version = typeof(LocalExecutionEvidenceOriginProvider).Assembly
            .GetName().Version?.ToString() ?? "0.0.0.0";
        return version.Length <= GraphExecutionEvidenceLimits.MaximumSoftwareVersionLength
            ? version
            : version[..GraphExecutionEvidenceLimits.MaximumSoftwareVersionLength];
    }
}
