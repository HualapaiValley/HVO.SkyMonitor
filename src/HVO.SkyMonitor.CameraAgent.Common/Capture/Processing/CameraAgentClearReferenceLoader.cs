using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class CameraAgentClearReferenceLoader(IOptions<CameraAgentHostOptions> options) : IProcessingRetentionHolds
{
    private readonly string _storageRoot = Path.GetFullPath(options.Value.RawIngressRoot);
    private readonly ConcurrentDictionary<string, byte> _configuredManifests = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    internal void RegisterRetentionHold(string? relativeManifestPath)
    {
        if (!string.IsNullOrWhiteSpace(relativeManifestPath))
        {
            _ = ResolveSafePath(relativeManifestPath);
            _configuredManifests.TryAdd(relativeManifestPath, 0);
        }
    }

    public async ValueTask<IReadOnlyList<ProcessingRetentionHold>> GetRetentionHoldsAsync(
        string storageRoot,
        CancellationToken cancellationToken)
    {
        if (!PathsEqual(_storageRoot, storageRoot))
        {
            return [];
        }
        var holds = new List<ProcessingRetentionHold>(_configuredManifests.Count);
        foreach (var relativeManifestPath in _configuredManifests.Keys.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestPath = ResolveSafePath(relativeManifestPath);
            var parsed = CaptureContractJson.ParseManifest(
                await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false));
            if (!parsed.IsValid || parsed.Document?.Manifest is not { } manifest)
            {
                throw new InvalidDataException("Configured clear-reference manifest is invalid.");
            }
            _ = ResolveSafePath(manifest.RelativeArtifactPath);
            holds.Add(new ProcessingRetentionHold(
                manifest.Descriptor.Artifact.ArtifactId,
                manifest.RelativeArtifactPath,
                relativeManifestPath));
        }
        return holds;
    }

    internal async ValueTask<ProcessingArtifact> LoadAsync(
        string relativeManifestPath,
        CancellationToken cancellationToken)
    {
        var manifestPath = ResolveSafePath(relativeManifestPath);
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var parsed = CaptureContractJson.ParseManifest(manifestBytes);
        if (!parsed.IsValid || parsed.Document?.Manifest is not { } manifest)
        {
            throw new InvalidDataException("Configured clear-reference manifest is invalid.");
        }
        var descriptor = manifest.Descriptor;
        if (descriptor.Artifact.Role is not (FrameArtifactRole.Raw or FrameArtifactRole.Calibrated or FrameArtifactRole.Combined) ||
            descriptor.Layout.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16))
        {
            throw new InvalidDataException("Configured clear reference is not a linear frame artifact.");
        }
        var payloadPath = ResolveSafePath(manifest.RelativeArtifactPath);
        var payload = await File.ReadAllBytesAsync(payloadPath, cancellationToken).ConfigureAwait(false);
        var reconstruction = FrameReconstructor.TryReconstruct(descriptor, payload, out _);
        if (!reconstruction.IsValid)
        {
            throw new InvalidDataException(
                $"Configured clear reference is not reconstructable ({reconstruction.ReasonCode}).");
        }
        return CreateArtifact(descriptor, payload);
    }

    internal static ProcessingArtifact CreateArtifact(
        ReconstructionDescriptor descriptor,
        ReadOnlyMemory<byte> payload)
        => new(
            descriptor.Artifact.ArtifactId,
            descriptor.Artifact.Role,
            descriptor.Artifact.Variant,
            ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256,
            descriptor.Artifact.MediaType,
            descriptor.Layout,
            payload,
            descriptor.Timing.ExposureStartedUtc,
            descriptor.Controls.EffectiveExposure,
            CreateCompatibility(descriptor),
            descriptor.Capture.CaptureSequence,
            descriptor.Artifact.SourceArtifactIds,
            descriptor.Timing.ExposureStartedUtc,
            ProcessingArtifact.ResolveObservationEndedUtc(
                descriptor.Timing.ExposureStartedUtc,
                descriptor.Timing.ExposureEndedUtc,
                descriptor.Controls.EffectiveExposure),
            new ProcessingCaptureConditions(
                descriptor.Controls.EffectiveGain,
                descriptor.Controls.EffectiveOffset,
                descriptor.Controls.EffectiveTemperatureC));

    private string ResolveSafePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Configured clear-reference path must be relative to CameraAgent storage.");
        }
        var path = Path.GetFullPath(Path.Combine(_storageRoot, relativePath));
        var rootPrefix = string.Concat(Path.TrimEndingDirectorySeparator(_storageRoot), Path.DirectorySeparatorChar);
        if (!path.StartsWith(
                rootPrefix,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException("Configured clear-reference path escapes CameraAgent storage.");
        }
        RawIngressFileStore.EnsureNoSymbolicLinks(_storageRoot, path);
        return path;
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static ProcessingCompatibilityIdentity CreateCompatibility(ReconstructionDescriptor descriptor)
        => new(
            descriptor.Profiles.Rig.Sha256,
            descriptor.Profiles.Rig.Sha256,
            descriptor.Profiles.Calibration.Sha256,
            descriptor.Profiles.Mask.Sha256,
            descriptor.Profiles.Sensor.Sha256,
            FormattableString.Invariant(
                $"exposure={descriptor.Controls.EffectiveExposure.TotalMilliseconds:R};gain={descriptor.Controls.EffectiveGain:R};offset={descriptor.Controls.EffectiveOffset:R};temperatureSetpoint={descriptor.Controls.TemperatureSetpointC:R}"),
            descriptor.Profiles.Processing.Sha256,
            descriptor.Location?.IdentitySha256);
}
