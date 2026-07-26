using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;

internal sealed record CalibrationLibraryProcessingInputs(
    IReadOnlyDictionary<string, ProcessingArtifact> References,
    IReadOnlyList<ProcessingAuxiliaryInput> AuxiliaryInputs);

internal sealed class CalibrationLibraryProcessingInputLoader(
    IOptions<CameraAgentHostOptions> options,
    CameraAgentClearReferenceLoader referenceLoader)
{
    private readonly string _root = Path.GetFullPath(options.Value.RawIngressRoot);
    private readonly CameraAgentClearReferenceLoader _referenceLoader = referenceLoader;

    internal async Task<CalibrationLibraryProcessingInputs> LoadAsync(
        CalibrationLibraryBundleSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var bundle = snapshot.Bundle;
        var profileJson = await File.ReadAllBytesAsync(
            ResolveSafePath(bundle.ProfileRelativePath), cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                PayloadChecksum.ComputeSha256(profileJson),
                bundle.ProfileIdentitySha256,
                StringComparison.OrdinalIgnoreCase) ||
            ReferenceCalibrationProfileJson.Parse(profileJson) is not { } profile)
        {
            throw new InvalidDataException("The selected calibration profile is corrupt.");
        }

        var masters = bundle.Artifacts
            .Where(static artifact => artifact.Role == CalibrationLibraryArtifactRoles.Master)
            .ToArray();
        if (masters.Length != CalibrationReferenceKinds.All.Count)
        {
            throw new InvalidDataException("The selected calibration bundle does not contain exactly four masters.");
        }
        var references = new Dictionary<string, ProcessingArtifact>(StringComparer.Ordinal);
        foreach (var kind in CalibrationReferenceKinds.All)
        {
            var envelope = masters.SingleOrDefault(artifact => artifact.Kind == kind)
                ?? throw new InvalidDataException("The selected calibration bundle is missing a master kind.");
            var parsed = CaptureContractJson.ParseManifest(await File.ReadAllBytesAsync(
                ResolveSafePath(envelope.ManifestRelativePath), cancellationToken).ConfigureAwait(false));
            var expectedRole = bundle.Source == CalibrationLibraryBundleSources.VirtualAcquisitionV1
                ? FrameArtifactRole.Combined
                : FrameArtifactRole.Raw;
            if (!parsed.IsValid || parsed.Document?.Manifest is not { } manifest ||
                manifest.Descriptor.Artifact.ArtifactId != envelope.ArtifactId ||
                manifest.Descriptor.Artifact.Role != expectedRole ||
                !string.Equals(manifest.Descriptor.Artifact.Variant, kind, StringComparison.Ordinal) ||
                !string.Equals(
                    manifest.Descriptor.Artifact.ChecksumSha256,
                    envelope.PayloadSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                manifest.Descriptor.Layout != bundle.Applicability.OutputLayout ||
                manifest.Descriptor.Controls.EffectiveExposure != envelope.Exposure ||
                manifest.Descriptor.Controls.EffectiveGain != envelope.Gain ||
                manifest.Descriptor.Controls.EffectiveOffset != envelope.Offset ||
                manifest.Descriptor.Controls.EffectiveTemperatureC != envelope.TemperatureC ||
                !manifest.Descriptor.Artifact.SourceArtifactIds.SequenceEqual(envelope.OrderedSourceArtifactIds) ||
                !RecipeMatches(envelope.MasterBuildRecipe, manifest.Descriptor.Artifact.Recipe))
            {
                throw new InvalidDataException("The selected calibration master conflicts with its library envelope.");
            }
            var reference = await _referenceLoader.LoadAsync(manifest, cancellationToken).ConfigureAwait(false);
            if (reference.ArtifactId != envelope.ArtifactId ||
                !string.Equals(
                    PayloadChecksum.ComputeSha256(reference.Payload.Span),
                    envelope.PayloadSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                reference.Integration != envelope.Exposure ||
                reference.Conditions is not { } conditions ||
                conditions.Gain != envelope.Gain || conditions.Offset != envelope.Offset ||
                conditions.TemperatureC != envelope.TemperatureC)
            {
                throw new InvalidDataException("The selected calibration master payload is corrupt.");
            }
            references.Add(kind, reference);
        }
        if (profile.References.Count != references.Count || profile.References.Any(reference =>
                !references.TryGetValue(reference.Kind, out var artifact) ||
                artifact.ArtifactId != reference.ArtifactId ||
                !string.Equals(
                    PayloadChecksum.ComputeSha256(artifact.Payload.Span),
                    reference.PayloadSha256,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("The selected calibration profile conflicts with its masters.");
        }

        var auxiliary = new List<ProcessingAuxiliaryInput>
        {
            new(
                "calibration-profile",
                ProcessingAuxiliaryInputKind.CanonicalJson,
                SchemaVersion: ReferenceCalibrationProfileV1.CurrentSchemaVersion,
                IdentitySha256: bundle.ProfileIdentitySha256,
                Payload: profileJson)
        };
        auxiliary.AddRange(CalibrationReferenceKinds.All.Select(kind => new ProcessingAuxiliaryInput(
            $"{kind}-reference",
            ProcessingAuxiliaryInputKind.Artifact,
            references[kind].Role == FrameArtifactRole.Raw
                ? ProcessingInputSelector.Raw(kind)
                : ProcessingInputSelector.Combined(kind),
            ArtifactId: references[kind].ArtifactId)));
        return new CalibrationLibraryProcessingInputs(references, auxiliary);
    }

    private static bool RecipeMatches(RecipeIdentityDescriptor? expected, RecipeIdentityDescriptor actual)
        => expected is null ||
           string.Equals(expected.Name, actual.Name, StringComparison.Ordinal) &&
           string.Equals(expected.SemanticVersion, actual.SemanticVersion, StringComparison.Ordinal) &&
           string.Equals(expected.ImplementationVersion, actual.ImplementationVersion, StringComparison.Ordinal) &&
           string.Equals(expected.OptionsSha256, actual.OptionsSha256, StringComparison.OrdinalIgnoreCase);

    private string ResolveSafePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Calibration library paths must be storage-relative.");
        }
        var path = Path.GetFullPath(Path.Combine(_root, relativePath));
        var prefix = string.Concat(Path.TrimEndingDirectorySeparator(_root), Path.DirectorySeparatorChar);
        if (!path.StartsWith(
                prefix,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException("Calibration library paths cannot escape storage.");
        }
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, path);
        return path;
    }
}
