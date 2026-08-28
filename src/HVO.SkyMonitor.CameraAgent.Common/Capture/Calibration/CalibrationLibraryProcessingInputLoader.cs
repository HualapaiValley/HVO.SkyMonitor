using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Imaging;
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
            var expectedRecipe = bundle.Source == CalibrationLibraryBundleSources.SyntheticReferencesV1
                ? SyntheticReferenceRecipe(bundle, envelope)
                : envelope.MasterBuildRecipe;
            if (!parsed.IsValid || parsed.Document?.Manifest is not { } manifest ||
                manifest.Descriptor.Artifact.ArtifactId != envelope.ArtifactId ||
                manifest.Descriptor.Artifact.Role != expectedRole ||
                !string.Equals(manifest.Descriptor.Artifact.Variant, kind, StringComparison.Ordinal) ||
                !string.Equals(
                    manifest.Descriptor.Artifact.ChecksumSha256,
                    envelope.PayloadSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                NormalizeCompleteLayout(manifest.Descriptor.Layout) !=
                NormalizeCompleteLayout(bundle.Applicability.OutputLayout) ||
                manifest.Descriptor.Controls.EffectiveExposure != envelope.Exposure ||
                manifest.Descriptor.Controls.EffectiveGain != envelope.Gain ||
                manifest.Descriptor.Controls.EffectiveOffset != envelope.Offset ||
                manifest.Descriptor.Controls.EffectiveTemperatureC != envelope.TemperatureC ||
                manifest.Descriptor.Controls.RequestedExposure != envelope.Exposure ||
                manifest.Descriptor.Controls.RequestedGain != envelope.Gain ||
                manifest.Descriptor.Controls.RequestedOffset != envelope.Offset ||
                manifest.Descriptor.Controls.TemperatureSetpointC != envelope.TemperatureC ||
                !string.Equals(
                    manifest.Descriptor.Capture.AgentId, bundle.Applicability.AgentId, StringComparison.Ordinal) ||
                !string.Equals(
                    manifest.Descriptor.Capture.RigId, bundle.Applicability.RigId, StringComparison.Ordinal) ||
                !string.Equals(
                    manifest.Descriptor.Profiles.Rig.Sha256,
                    bundle.Applicability.RigProfileSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    manifest.Descriptor.Profiles.Sensor.Sha256,
                    bundle.Applicability.SensorProfileSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !CalibrationProfileMatches(bundle, manifest.Descriptor.Profiles.Calibration) ||
                !string.Equals(
                    manifest.RelativeArtifactPath,
                    PayloadPathForManifest(envelope.ManifestRelativePath),
                    StringComparison.Ordinal) ||
                !CaptureSequenceMatches(bundle, envelope, manifest.Descriptor.Capture.CaptureSequence) ||
                !manifest.Descriptor.Artifact.SourceArtifactIds.SequenceEqual(envelope.OrderedSourceArtifactIds) ||
                !RecipeMatches(expectedRecipe, manifest.Descriptor.Artifact.Recipe))
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

    private static RecipeIdentityDescriptor SyntheticReferenceRecipe(
        CalibrationLibraryBundleV1 bundle,
        CalibrationLibraryArtifactV1 artifact)
        => RecipeIdentityDescriptor.Create(
            "synthetic-calibration-reference",
            "1.0.0",
            SyntheticCalibrationReferenceGenerator.AlgorithmVersion,
            CaptureContractJson.SerializeToElement(new
            {
                schemaVersion = SyntheticCalibrationModelV1.CurrentSchemaVersion,
                modelIdentitySha256 = bundle.AcquisitionModelIdentitySha256,
                referenceKind = artifact.Kind,
                generator = SyntheticCalibrationReferenceGenerator.AlgorithmVersion
            }));

    private static bool CalibrationProfileMatches(
        CalibrationLibraryBundleV1 bundle,
        ProfileIdentityDescriptor profile)
    {
        var expected = bundle.Source switch
        {
            CalibrationLibraryBundleSources.SyntheticReferencesV1 =>
                ("synthetic-calibration-model", SyntheticCalibrationModelV1.CurrentSchemaVersion),
            CalibrationLibraryBundleSources.VirtualAcquisitionV1 =>
                ("virtual-calibration-source-model", VirtualCalibrationSourceModelV1.CurrentSchemaVersion),
            _ => (string.Empty, string.Empty)
        };
        return string.Equals(profile.Name, expected.Item1, StringComparison.Ordinal) &&
               string.Equals(profile.Version, expected.Item2, StringComparison.Ordinal) &&
               string.Equals(profile.Sha256, bundle.AcquisitionModelIdentitySha256, StringComparison.OrdinalIgnoreCase);
    }

    private static bool CaptureSequenceMatches(
        CalibrationLibraryBundleV1 bundle,
        CalibrationLibraryArtifactV1 artifact,
        long actual)
    {
        if (bundle.Source == CalibrationLibraryBundleSources.SyntheticReferencesV1)
        {
            var segments = bundle.ProfileRelativePath.Split('/');
            var publicationIdentity = segments.Length >= 2 ? segments[^2] : null;
            if (publicationIdentity is not { Length: 64 } || !publicationIdentity.All(Uri.IsHexDigit))
            {
                return false;
            }
            var syntheticHash = SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{publicationIdentity}:sequence:{artifact.Kind}"));
            const ulong syntheticReservedStart = (ulong)long.MaxValue / 2;
            var offset = BinaryPrimitives.ReadUInt64BigEndian(syntheticHash) % syntheticReservedStart;
            return actual == checked((long)(syntheticReservedStart + offset));
        }
        if (bundle.Source != CalibrationLibraryBundleSources.VirtualAcquisitionV1 ||
            !bundle.BundleId.StartsWith("bundle-", StringComparison.Ordinal))
        {
            return false;
        }
        var kindIndex = -1;
        for (var index = 0; index < CalibrationReferenceKinds.All.Count; index++)
        {
            if (string.Equals(CalibrationReferenceKinds.All[index], artifact.Kind, StringComparison.Ordinal))
            {
                kindIndex = index;
                break;
            }
        }
        if (kindIndex < 0)
        {
            return false;
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{bundle.BundleId["bundle-".Length..]}:capture-sequence"));
        const ulong reservedStart = (ulong)long.MaxValue / 2;
        const ulong blockSize = 16;
        var blockCount = reservedStart / blockSize;
        var block = BinaryPrimitives.ReadUInt64BigEndian(hash) % blockCount;
        return actual == checked((long)(reservedStart + block * blockSize + (uint)(13 + kindIndex - 1)));
    }

    private static string PayloadPathForManifest(string manifestPath)
        => string.Concat(manifestPath.AsSpan(0, manifestPath.Length - ".json".Length), ".bin");

    private static FrameLayoutDescriptor NormalizeCompleteLayout(FrameLayoutDescriptor layout)
        => layout.SampleDepthBits == layout.ContainerDepthBits
            ? layout with
            {
                StoredCodeTransform = layout.StoredCodeTransform ?? FrameStoredCodeTransform.IdentityV1,
                LevelCodeSpace = layout.LevelCodeSpace ?? FrameLevelCodeSpace.StoredContainer
            }
            : layout;

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
