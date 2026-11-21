using System.Collections.Immutable;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed record CenteredProductSettlement(
    ExposureKey Exposure,
    string Producer,
    string SchemaVersion,
    string Variant,
    string? OutputIdentity,
    string? PayloadSha256,
    ExposureProductAvailability Availability,
    string? Reason,
    LinearInputProvenanceV1? Linear)
{
    internal Guid? ArtifactId { get; init; }
    internal string? ContentIdentitySha256 { get; init; }
}

internal sealed class CenteredExposureProductReader(
    RawCaptureIngress ingress, SqliteCaptureProcessingStore store) : IExposureProductReader
{
    internal const string LinearSchemaVersion = "exposure-linear-v1";

    public async ValueTask<ExposureInputV1?> ReadExposureAsync(ExposureKey key, CancellationToken cancellationToken)
    {
        await ingress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await ReadExposureCoreAsync(key, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ExposureInputV1?> ReadExposureCoreAsync(ExposureKey key, CancellationToken cancellationToken)
    {
        var raw = await ingress.ReadCenteredRawAsync(key, cancellationToken).ConfigureAwait(false);
        return raw is null ? null : CreateExposure(key, raw);
    }

    internal static ExposureInputV1 CreateExposure(ExposureKey key, CenteredRawEvidence raw)
    {
        var descriptor = raw.Manifest.Descriptor;
        var rig = raw.Run.Configuration.Rig;
        return new(key, descriptor, new(descriptor.Layout, rig.Orientation, descriptor.Profiles.Rig.Sha256,
            rig.Optics.CalibrationVersion, EstimateCenterScale(rig, descriptor.Layout)));
    }

    private static double? EstimateCenterScale(CameraRigConfig rig, FrameLayoutDescriptor layout)
    {
        if (layout.Readout is null && rig.Readout is not null)
            return null;
        try
        {
            var native = RigProjectionContextFactory.CreateNative(rig);
            var projection = layout.Readout is { } readout
                ? RigProjectionContextFactory.CreateReadoutView(native, readout)
                : native;
            if (projection.WidthPixels != layout.Width || projection.HeightPixels != layout.Height ||
                projection.FocalLengthXPixels != projection.FocalLengthYPixels)
                return null;

            // Angular scale at the optical axis, where each supported projection has derivative f.
            return (180 * 3600 / Math.PI) / projection.FocalLengthXPixels;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public async ValueTask<LinearExposureLookup?> ReadLinearAsync(ExposureKey key, CancellationToken cancellationToken)
    {
        await ingress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await ReadLinearCoreAsync(key, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<LinearExposureLookup?> ReadLinearCoreAsync(ExposureKey key, CancellationToken cancellationToken)
    {
        var raw = await ingress.ReadCenteredRawAsync(key, cancellationToken).ConfigureAwait(false);
        if (raw is null) return null;
        var exposure = CreateExposure(key, raw);
        var (product, provenance) = await store.ReadCenteredLinearAsync(key, raw, cancellationToken)
            .ConfigureAwait(false);
        var disposition = product?.Availability switch
        {
            ExposureProductAvailability.Available when provenance is not null => provenance.Disposition,
            ExposureProductAvailability.Expired => LinearExposureDisposition.Expired,
            ExposureProductAvailability.Incompatible => LinearExposureDisposition.Unsupported,
            ExposureProductAvailability.Failed => LinearExposureDisposition.Failed,
            ExposureProductAvailability.Unavailable or ExposureProductAvailability.MissingEvidence =>
                LinearExposureDisposition.Unavailable,
            _ => LinearExposureDisposition.Pending
        };
        return new(exposure, disposition, product?.Revision, product?.Product, provenance, product?.Reason);
    }

    public async ValueTask<ExposureProductLookup?> ReadProductAsync(
        ExposureKey key, ExposureProductSlot slot, CancellationToken cancellationToken)
    {
        await ingress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await store.ReadCenteredProductAsync(key, slot, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<ExposureProductLookup>> ReadProductsAfterAsync(
        CameraKey camera, long afterRevision, int maximumCount, CancellationToken cancellationToken)
    {
        await ingress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await store.ReadCenteredProductsAfterAsync(camera, afterRevision, maximumCount, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static IReadOnlyList<CenteredProductSettlement> CreateSettlements(
        ArtifactManifestV2 manifest,
        CaptureProcessingGraphNode node,
        DurableProcessingNodeStatus status,
        string? reason,
        IReadOnlyList<DurableProcessingOutput> outputs,
        IReadOnlyList<DurableProcessingNodeInput> inputs)
    {
        if (manifest.CenteredExposure is not { } centered ||
            status is not (DurableProcessingNodeStatus.Completed or DurableProcessingNodeStatus.Skipped or
                DurableProcessingNodeStatus.TerminalFailure))
            return [];

        var raw = manifest.Descriptor;
        var key = new ExposureKey(centered.Source.Camera, centered.Source.RunId, raw.Capture.CaptureId);
        if (node.Step is CalibrationCaptureProcessingStep)
            return [CreateLinearSettlement(key, raw, node, status, reason, outputs.SingleOrDefault(), inputs)];

        return outputs.Select(output => new CenteredProductSettlement(key, node.Id,
            output.ProductSchemaVersion ?? ArtifactManifestV2.CurrentSchemaVersion, output.Artifact.Variant,
            output.OutputIdentitySha256, output.Artifact.ChecksumSha256, ExposureProductAvailability.Available, null, null)
        {
            ArtifactId = output.ArtifactId,
            ContentIdentitySha256 = output.ContentIdentitySha256
        }).ToArray();
    }

    private static CenteredProductSettlement CreateLinearSettlement(
        ExposureKey key,
        ReconstructionDescriptor raw,
        CaptureProcessingGraphNode node,
        DurableProcessingNodeStatus status,
        string? reason,
        DurableProcessingOutput? output,
        IReadOnlyList<DurableProcessingNodeInput> inputs)
    {
        var variant = output?.Artifact.Variant ?? node.OutputVariant ?? "linear";
        if (!CenteredLinearQualification.TryResolveLevels(raw.Layout, out _, out var rawWhite, out var unsupported))
            return new(key, node.Id, LinearSchemaVersion, variant, null, null, ExposureProductAvailability.Incompatible,
                unsupported, null);

        if (output?.Descriptor is not { } linear)
            return new(key, node.Id, LinearSchemaVersion, variant, null, null,
                status == DurableProcessingNodeStatus.TerminalFailure ? ExposureProductAvailability.Failed :
                    ExposureProductAvailability.Unavailable, reason ?? "linear.no-output", null);

        var recipe = ProcessingIdentity.CreateRecipeIdentity(linear.Artifact.Recipe);
        var sourceIds = linear.Artifact.SourceArtifactIds;
        var disposition = recipe.Descriptor switch
        {
            { Name: BuiltInProcessingRecipes.LinearNormalization, ImplementationVersion: "linear-normalization-none-v1" }
                when sourceIds.Count == 1 => LinearExposureDisposition.QualifiedNormalizationOnly,
            { Name: BuiltInProcessingRecipes.ReferenceCalibration } when sourceIds.Count > 1 =>
                LinearExposureDisposition.Calibrated,
            _ => LinearExposureDisposition.Unsupported
        };
        if (disposition == LinearExposureDisposition.Unsupported ||
            sourceIds[0] != raw.Artifact.ArtifactId ||
            !CenteredLinearQualification.TryResolveLevels(linear.Layout, out _, out var linearWhite, out _) ||
            !HasSamePixelGeometry(raw.Layout, linear.Layout))
            return new(key, node.Id, LinearSchemaVersion, variant, null, null,
                ExposureProductAvailability.Incompatible, "linear.unqualified-output", null);

        var masterHashes = disposition == LinearExposureDisposition.Calibrated
            ? ReadMasterHashes(sourceIds, inputs)
            : [];
        if (masterHashes.IsDefault)
            return new(key, node.Id, LinearSchemaVersion, variant, null, null,
                ExposureProductAvailability.MissingEvidence, "linear.calibration-master-evidence-missing", null);

        var provenance = new LinearInputProvenanceV1(disposition, raw.Artifact.ArtifactId,
            CaptureContractJson.ComputeDescriptorSha256(raw), raw.Artifact.ChecksumSha256,
            linear.Artifact.ArtifactId, CaptureContractJson.ComputeDescriptorSha256(linear),
            linear.Artifact.ChecksumSha256, output.OutputIdentitySha256, recipe,
            sourceIds.Skip(1).ToImmutableArray(), new(raw.Artifact.ArtifactId, rawWhite),
            new(linear.Artifact.ArtifactId, linearWhite))
        {
            Reason = disposition == LinearExposureDisposition.QualifiedNormalizationOnly ? "linear.explicit-no-correction" : null,
            CalibrationBundleIdentitySha256 = inputs.SingleOrDefault(input => input.Name == "centered-calibration-bundle")
                ?.IdentitySha256,
            CalibrationPayloadHashes = masterHashes
        };
        return new(key, node.Id, LinearSchemaVersion, variant, output.OutputIdentitySha256, linear.Artifact.ChecksumSha256,
            ExposureProductAvailability.Available, null, provenance)
        {
            ArtifactId = output.ArtifactId,
            ContentIdentitySha256 = output.ContentIdentitySha256
        };
    }

    private static ImmutableArray<string> ReadMasterHashes(
        IReadOnlyList<Guid> sources, IReadOnlyList<DurableProcessingNodeInput> inputs)
    {
        var kinds = CalibrationReferenceKinds.All;
        if (sources.Count != kinds.Count + 1)
            return default;

        var hashes = ImmutableArray.CreateBuilder<string>(kinds.Count);
        for (var index = 0; index < kinds.Count; index++)
        {
            var name = $"centered-master-{kinds[index]}";
            var input = inputs.SingleOrDefault(input => input.Name == name);
            if (input?.ArtifactId != sources[index + 1] || input.IdentitySha256 is not { Length: 64 } hash)
                return default;
            hashes.Add(hash);
        }
        return hashes.MoveToImmutable();
    }

    private static bool HasSamePixelGeometry(FrameLayoutDescriptor raw, FrameLayoutDescriptor output) =>
        raw.Width == output.Width && raw.Height == output.Height && raw.PixelFormat == output.PixelFormat &&
        raw.Readout == output.Readout && raw.CfaPattern == output.CfaPattern;
}
