using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed record CentralTransientDerivativeBundle(
    TransientEventV1 SourceEvent,
    Linear16TransientReconstructionResult Reconstruction,
    Linear16TransientDerivativeProducts Products,
    IReadOnlyList<TransientCandidateExtractionDescriptorV1> ExtractionReceipts);

internal interface ICentralTransientDerivativeBundleFactory
{
    Task<CentralTransientDerivativeBundle> CreateAsync(
        CentralDerivativeJobLease lease,
        CancellationToken cancellationToken);
}

internal sealed class CentralTransientDerivativeBundleFactory(
    ApplicationDbContext dbContext,
    ICentralDerivativeJobInputReader inputReader,
    ICentralTransientMaskFactory maskFactory) : ICentralTransientDerivativeBundleFactory
{
    public async Task<CentralTransientDerivativeBundle> CreateAsync(
        CentralDerivativeJobLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var persisted = await dbContext.CentralTransientDerivativeJobs.AsNoTracking()
            .Include(item => item.SourceEventVersion)
            .SingleOrDefaultAsync(item => item.CentralDerivativeJobId == lease.JobId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CentralDerivativeJobStateException("The transient derivative bundle job was not found.");
        ValidateJob(lease, persisted);
        var requestBytes = Encoding.UTF8.GetBytes(persisted.CanonicalRequestJson);
        if (requestBytes.Length != persisted.CanonicalRequestByteLength ||
            !EqualsSha256(requestBytes, persisted.CanonicalRequestSha256))
        {
            throw new CentralDerivativeJobStateException("The frozen transient derivative request is invalid.");
        }
        var sourceEventBytes = Encoding.UTF8.GetBytes(persisted.SourceEventVersion!.CanonicalEventJson);
        if (sourceEventBytes.Length != persisted.SourceEventVersion.CanonicalEventByteLength ||
            !EqualsSha256(sourceEventBytes, persisted.SourceEventVersion.CanonicalEventSha256))
        {
            throw new CentralDerivativeJobStateException("The frozen transient source event checksum is invalid.");
        }
        var parsed = TransientContractJson.ParseEvent(sourceEventBytes);
        var sourceEvent = parsed.Value is not null && parsed.Validation.IsValid
            ? parsed.Value
            : throw new CentralDerivativeJobStateException("The transient source event version is invalid.");
        if (sourceEvent.EventVersionId != persisted.SourceEventVersionId || sourceEvent.Observations.Count is < 1 or > 2)
        {
            throw new CentralDerivativeJobStateException("The transient derivative source version is inconsistent.");
        }

        var loaded = await inputReader.ReadAsync(lease, cancellationToken).ConfigureAwait(false);
        if (lease.Inputs is null || loaded.ProcessingInputs.Count != lease.Inputs.Count)
        {
            throw new CentralDerivativeJobStateException("The frozen transient derivative inputs are incomplete.");
        }
        var inputs = lease.Inputs.OrderBy(item => item.Ordinal)
            .Zip(loaded.ProcessingInputs, (identity, input) => new LoadedInput(identity, input))
            .ToDictionary(item => item.Identity.ArtifactId);
        var observations = new List<Linear16TransientReconstructionObservation>(sourceEvent.Observations.Count);
        var geometries = new List<Linear16TransientDerivativeGeometry>(sourceEvent.Observations.Count);
        var receipts = new List<TransientCandidateExtractionDescriptorV1>(sourceEvent.Observations.Count);
        foreach (var observation in sourceEvent.Observations.OrderBy(item => item.Ordinal))
        {
            var durableObservation = await dbContext.CentralTransientObservations.AsNoTracking()
                .SingleAsync(item => item.CentralTransientEventId == persisted.CentralTransientEventId &&
                    item.ObservationId == observation.ObservationId, cancellationToken).ConfigureAwait(false);
            var receiptRecord = await dbContext.CentralTransientExtractionReceipts.AsNoTracking()
                .Include(item => item.ValidationJob)
                .SingleAsync(item => item.ExtractionIdentitySha256 ==
                    durableObservation.ExtractionReceiptIdentitySha256, cancellationToken).ConfigureAwait(false);
            var receiptBytes = Encoding.UTF8.GetBytes(receiptRecord.CanonicalReceiptJson);
            if (receiptBytes.Length != receiptRecord.CanonicalReceiptByteLength ||
                !EqualsSha256(receiptBytes, receiptRecord.CanonicalReceiptSha256))
            {
                throw new CentralDerivativeJobStateException("The frozen transient extraction receipt is invalid.");
            }
            var receipt = TransientCandidateExtractionJson.Parse(receiptBytes);
            if (!string.Equals(receipt.ExtractionIdentitySha256,
                    durableObservation.ExtractionReceiptIdentitySha256, StringComparison.OrdinalIgnoreCase) ||
                observation.Extraction.OriginatingCandidateId is { } candidateId &&
                !receipt.Candidates.Any(item => item.CandidateId == candidateId))
            {
                throw new CentralDerivativeJobStateException("The transient observation does not match its extraction receipt.");
            }
            if (string.IsNullOrWhiteSpace(receiptRecord.ValidationJob!.ExecutionOptionsJson))
            {
                throw new CentralDerivativeJobStateException("The transient execution options are unavailable.");
            }
            var executionOptions = CentralTransientExecutionOptionsJson.Deserialize(
                receiptRecord.ValidationJob.ExecutionOptionsJson);
            var temporalSources = await CreateTemporalSourcesAsync(
                receipt, executionOptions, inputs, cancellationToken).ConfigureAwait(false);
            var byPosition = temporalSources.ToDictionary(item => item.Position);
            var target = byPosition[TransientTemporalPosition.N];
            var knownEventEvidenceIds = receipt.Background.Sources
                .Where(item => item.Disposition == TransientTemporalSourceDisposition.ExcludedKnownEvent)
                .Select(item => item.EvidenceId).ToArray();
            var background = TransientTemporalBackgroundFactory.Create(new TransientTemporalBackgroundRequest(
                receipt.Background.Kind,
                target,
                temporalSources.Where(item => item.Position != TransientTemporalPosition.N).ToArray(),
                knownEventEvidenceIds,
                TimeSpan.FromTicks(executionOptions.MaximumAdjacentStartIntervalTicks)), cancellationToken);
            if (background.Status != TransientTemporalBackgroundStatus.Produced || background.Product is null ||
                !SameBackground(receipt.Background, background.Product.Descriptor))
            {
                throw new CentralDerivativeJobStateException("The frozen transient background could not be reproduced.");
            }
            var persistentMasks = target.Masks.OrderBy(item => item.Kind)
                .ThenBy(item => item.MaskIdentitySha256, StringComparer.Ordinal)
                .Select(item => item.Mask).ToArray();
            var hardMask = Linear16TransientExtraction.CreateHardExclusionMask(
                background.Product.EffectiveMask,
                target.Input.SaturationMask,
                background.Product.NoSupportMask,
                persistentMasks,
                cancellationToken);
            if (!EqualsSha256(hardMask.Bits.Span, receipt.HardExclusionMaskChecksumSha256))
            {
                throw new CentralDerivativeJobStateException("The frozen transient hard mask could not be reproduced.");
            }
            if (observation.Geometry.CoordinateWidth != target.Input.Descriptor.Layout.Width ||
                observation.Geometry.CoordinateHeight != target.Input.Descriptor.Layout.Height)
            {
                throw new CentralDerivativeJobStateException("The transient geometry does not match the detector layout.");
            }
            var bounds = new Linear16TransientReconstructionBounds(
                observation.Geometry.Bounds.X,
                observation.Geometry.Bounds.Y,
                observation.Geometry.Bounds.Width,
                observation.Geometry.Bounds.Height);
            observations.Add(new(
                Frame(target.Input),
                new Linear16Frame(
                    background.Product.Descriptor.Layout.Width,
                    background.Product.Descriptor.Layout.Height,
                    background.Product.Descriptor.Layout.StrideBytes,
                    background.Product.Descriptor.Layout.PixelFormat,
                    background.Product.Pixels),
                hardMask,
                bounds));
            geometries.Add(new(bounds, observation.Geometry.Polyline.Select(item =>
                new HVO.SkyMonitor.Astronomy.PixelPoint(item.X, item.Y)).ToArray()));
            receipts.Add(receipt);
        }

        var reconstruction = Linear16TransientReconstruction.Reconstruct(observations, cancellationToken);
        var products = Linear16TransientDerivativeProductFactory.Create(
            reconstruction,
            geometries,
            new(CropPaddingPixels: 16, JpegQuality: 90),
            cancellationToken);
        return new(sourceEvent, reconstruction, products, receipts);
    }

    private async Task<IReadOnlyList<TransientTemporalSource>> CreateTemporalSourcesAsync(
        TransientCandidateExtractionDescriptorV1 receipt,
        CentralTransientExecutionOptionsV1 executionOptions,
        IReadOnlyDictionary<Guid, LoadedInput> inputs,
        CancellationToken cancellationToken)
    {
        var detectorSources = new List<DetectorSource>(receipt.OrderedSources.Count);
        foreach (var source in receipt.OrderedSources.OrderBy(item => item.Position))
        {
            if (!inputs.TryGetValue(source.Source.Locator.Artifact.ArtifactId, out var loaded) ||
                loaded.Input.Descriptor is null || loaded.Identity.CaptureSequence is not { } captureSequence)
            {
                throw new CentralDerivativeJobStateException("A frozen transient background source is unavailable.");
            }
            var artifact = CreateProcessingArtifact(loaded.Input);
            var created = TransientDetectorInputFactory.Create(
                artifact, source.Source, CreateLevels(artifact.Layout!), cancellationToken);
            if (!created.Validation.IsValid || created.Input is null ||
                !string.Equals(created.Input.Descriptor.InputIdentitySha256,
                    source.DetectorInputIdentitySha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new CentralDerivativeJobStateException("A frozen transient detector input could not be reproduced.");
            }
            detectorSources.Add(new(source.Position, loaded.Identity.CentralArtifactId,
                captureSequence, created.Input, loaded.Input.Descriptor));
        }
        var masks = await maskFactory.CreateAsync(detectorSources.Select(item => new CentralTransientMaskSource(
            item.Position, item.CentralArtifactId, item.Input, item.Descriptor)).ToArray(), executionOptions,
            cancellationToken).ConfigureAwait(false)
            ?? throw new CentralDerivativeJobStateException("The frozen transient masks could not be reproduced.");
        return detectorSources.Select(source =>
        {
            var lineage = receipt.Background.Sources.Single(item => item.Position == source.Position);
            return new TransientTemporalSource(
                source.Position,
                source.CaptureSequence,
                source.Input,
                lineage.Sensitivity,
                masks);
        }).ToArray();
    }

    private static void ValidateJob(CentralDerivativeJobLease lease, CentralTransientDerivativeJob persisted)
    {
        if (!string.Equals(lease.RecipeName, CentralTransientDerivativeRuntime.RecipeName, StringComparison.Ordinal) ||
            !string.Equals(persisted.RequestIdentitySha256, lease.RequestIdentitySha256, StringComparison.Ordinal) ||
            !string.Equals(persisted.RecipeIdentitySha256,
                CentralTransientDerivativeRuntime.RecipeIdentitySha256, StringComparison.Ordinal) ||
            !string.Equals(persisted.OptionsIdentitySha256,
                CentralTransientDerivativeRuntime.OptionsIdentitySha256, StringComparison.Ordinal) ||
            persisted.ExpectedOutputCount != 5 || persisted.SourceEventVersion is null)
        {
            throw new CentralDerivativeJobStateException("The transient derivative bundle identity is invalid.");
        }
    }

    private static bool SameBackground(
        TransientTemporalBackgroundDescriptorV1 expected,
        TransientTemporalBackgroundDescriptorV1 actual)
        => string.Equals(expected.BackgroundIdentitySha256, actual.BackgroundIdentitySha256,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(expected.BackgroundChecksumSha256, actual.BackgroundChecksumSha256,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(expected.EffectiveMaskChecksumSha256, actual.EffectiveMaskChecksumSha256,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(expected.NoSupportMaskChecksumSha256, actual.NoSupportMaskChecksumSha256,
                StringComparison.OrdinalIgnoreCase);

    private static Linear16Frame Frame(TransientDetectorInput input)
        => new(
            input.Descriptor.Layout.Width,
            input.Descriptor.Layout.Height,
            input.Descriptor.Layout.StrideBytes,
            input.Descriptor.Layout.PixelFormat,
            input.Pixels);

    private static ProcessingArtifact CreateProcessingArtifact(LogicHostProcessingInput input)
    {
        var descriptor = input.Descriptor!;
        var started = descriptor.Timing.ExposureStartedUtc;
        var ended = ProcessingArtifact.ResolveObservationEndedUtc(
            started, descriptor.Timing.ExposureEndedUtc, descriptor.Controls.EffectiveExposure);
        return new ProcessingArtifact(
            descriptor.Artifact.ArtifactId,
            descriptor.Artifact.Role,
            descriptor.Artifact.Variant,
            ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256,
            descriptor.Artifact.MediaType,
            descriptor.Layout,
            input.Payload,
            started,
            descriptor.Controls.EffectiveExposure,
            LogicHostRecipeExecutionAdapter.CreateCompatibility(descriptor),
            descriptor.Capture.CaptureSequence,
            descriptor.Artifact.SourceArtifactIds,
            started,
            ended);
    }

    private static TransientLinearLevelsV1 CreateLevels(HVO.SkyMonitor.AgentCore.FrameLayoutDescriptor layout)
    {
        if (layout.BlackLevel is not { } black || layout.WhiteLevel is not { } white ||
            black < 0 || white > ushort.MaxValue || black != Math.Truncate(black) || white != Math.Truncate(white))
        {
            throw new CentralDerivativeInputRejectedException("Transient detector levels are not exact 16-bit values.");
        }
        return new((ushort)black, (ushort)white, (ushort)white);
    }

    private static bool EqualsSha256(ReadOnlySpan<byte> bytes, string expected)
        => string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), expected, StringComparison.OrdinalIgnoreCase);

    private sealed record LoadedInput(CentralDerivativeJobLeaseInput Identity, LogicHostProcessingInput Input);
    private sealed record DetectorSource(
        TransientTemporalPosition Position,
        Guid CentralArtifactId,
        long CaptureSequence,
        TransientDetectorInput Input,
        HVO.SkyMonitor.AgentCore.ReconstructionDescriptor Descriptor);
}
