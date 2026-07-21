using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Transients;

internal sealed class TransientDetectorRuntime(
    ICelestialCatalog catalog,
    IConstellationTopology? constellationTopology = null,
    IPlanetEphemeris? planetEphemeris = null)
{
    internal static readonly TransientDeterministicAssessmentOptionsV1 AssessmentOptions = new(
        5, 3, 8, 3, 1.8, 0.5, 10, 100_000, 3, 3, 3, 1_000, 30, 2);

    internal async ValueTask<IReadOnlyDictionary<int, TransientTemporalSource>> CreateSourcesAsync(
        CameraModuleConfig configuration,
        TransientDetectionOptions options,
        IReadOnlyDictionary<int, TransientLoadedFrame> frames,
        CancellationToken cancellationToken)
    {
        var inputs = new Dictionary<int, TransientDetectorInput>();
        foreach (var pair in frames)
        {
            var layout = pair.Value.Artifact.Layout
                ?? throw new TransientWorkerExecutionException("transient-runtime.input-layout-missing", retryable: false);
            if (layout.BlackLevel is not { } black || layout.WhiteLevel is not { } white ||
                black < ushort.MinValue || black > ushort.MaxValue || white < ushort.MinValue || white > ushort.MaxValue ||
                black != Math.Truncate(black) || white != Math.Truncate(white))
            {
                throw new TransientWorkerExecutionException("transient-runtime.input-levels-invalid", retryable: false);
            }
            var created = TransientDetectorInputFactory.Create(
                pair.Value.Artifact,
                pair.Value.Source,
                new TransientLinearLevelsV1((ushort)black, (ushort)white, (ushort)white),
                cancellationToken);
            if (!created.Validation.IsValid || created.Input is null)
            {
                throw new TransientWorkerExecutionException(
                    created.Validation.ReasonCode ?? TransientDetectorInputReasonCodes.InvalidSource,
                    retryable: false);
            }
            inputs.Add(pair.Key, created.Input);
        }

        var first = inputs.OrderBy(static pair => pair.Key).First().Value;
        var width = first.Descriptor.Layout.Width;
        var height = first.Descriptor.Layout.Height;
        var projection = RigProjectionContextFactory.Create(configuration.Rig);
        var projector = ProjectorFactory.Create(projection);
        var imageCircle = CreateProjectionMask(width, height, first, projector, static direction => direction is null);
        var horizon = CreateProjectionMask(
            width,
            height,
            first,
            projector,
            static direction => direction is { AltitudeDegrees: < 0 });
        var obstruction = CreateObstructionMask(
            width,
            height,
            first,
            configuration.Rig.ControlPolicy?.Metering?.ExcludedRegions);
        var empty = Linear16MaskOperations.Empty(width, height);
        var starRegions = new List<Linear16CircularMaskRegion>();
        foreach (var pair in frames.OrderBy(static pair => pair.Key))
        {
            var metadata = (catalog as ICelestialCatalogMetadataSource)?.Metadata ?? new CatalogMetadata(
                "runtime-catalog",
                "unversioned",
                new Uri("https://invalid.local/runtime-catalog"),
                new string('0', 64),
                "unspecified",
                "runtime-catalog-v1");
            var request = new VisibleSceneRequest(
                pair.Value.Source.ObservationStartedUtc,
                new ObserverLocation(
                    configuration.Observatory.LatitudeDegrees,
                    configuration.Observatory.LongitudeDegrees,
                    configuration.Observatory.ElevationMeters),
                projection,
                new CatalogQuery(options.StarMaximumMagnitude, options.StarMaximumResults),
                metadata,
                horizonPolicy: HorizonPolicy.GeometricHorizon,
                projectionVersion: configuration.Rig.Optics.CalibrationVersion,
                algorithmVersion: "visible-scene-iau1976-constellation-v2");
            var scene = await new VisibleSceneBuilder(catalog, constellationTopology, planetEphemeris)
                .BuildAsync(request, cancellationToken).ConfigureAwait(false);
            var transform = inputs[pair.Key].Descriptor.SourceToDetectorTransform;
            var radius = Math.Max(0.5, options.StarSupportRadiusSourcePixels * Math.Max(transform.ScaleX, transform.ScaleY));
            starRegions.AddRange(scene.Objects.Select(item => new Linear16CircularMaskRegion(
                item.Pixel.X * transform.ScaleX + transform.OffsetX,
                item.Pixel.Y * transform.ScaleY + transform.OffsetY,
                radius)));
        }
        var star = Linear16MaskOperations.CreateCircularSupportMask(width, height, starRegions, cancellationToken);
        var masks = new[]
        {
            TransientDetectorMask.Create(TransientDetectorMaskKind.Sky,
                new ProcessingAlgorithmIdentity("geometric-sky-mask", "v1"), empty),
            TransientDetectorMask.Create(TransientDetectorMaskKind.ImageCircle,
                new ProcessingAlgorithmIdentity("calibrated-image-circle-mask", "v1"), imageCircle),
            TransientDetectorMask.Create(TransientDetectorMaskKind.Horizon,
                new ProcessingAlgorithmIdentity("geometric-horizon-mask", "v1"), horizon),
            TransientDetectorMask.Create(TransientDetectorMaskKind.Obstruction,
                new ProcessingAlgorithmIdentity("configured-obstruction-mask", "v1"), obstruction),
            TransientDetectorMask.Create(TransientDetectorMaskKind.BadPixel,
                new ProcessingAlgorithmIdentity("configured-bad-pixel-mask", "v1"), empty),
            TransientDetectorMask.Create(TransientDetectorMaskKind.Star,
                new ProcessingAlgorithmIdentity("catalog-projected-star-mask", "v1"), star)
        };
        return inputs.ToDictionary(
            static pair => pair.Key,
            pair => new TransientTemporalSource(
                ToPosition(pair.Key),
                pair.Value.CaptureSequence!.Value,
                pair.Value,
                new TransientSensitivityV1(pair.Value.Descriptor.Compatibility.SetpointRegime, 1, 1),
                masks));
    }

    internal static TransientCandidateExtractionOutcome Extract(
        string agentId,
        DateTimeOffset createdUtc,
        TransientTemporalBackgroundProduct background,
        IReadOnlyDictionary<int, TransientTemporalSource> sources,
        IReadOnlyList<TransientCandidateIdentitySlot> slots,
        bool centered,
        CancellationToken cancellationToken)
    {
        var byEvidence = sources.Values.ToDictionary(static value => value.Input.Descriptor.Source.EvidenceId);
        var ordered = background.Descriptor.Sources.Select(value => byEvidence[value.EvidenceId]).ToArray();
        return TransientCandidateExtractionFactory.Create(new TransientCandidateExtractionRequest(
            agentId,
            createdUtc,
            sources[0],
            background,
            ordered,
            slots,
            TransientCandidateExtractionProfiles.EdgeV1,
            centered), cancellationToken);
    }

    private static Linear16PixelMask CreateProjectionMask(
        int width,
        int height,
        TransientDetectorInput input,
        IImageProjector projector,
        Func<AltAzPoint?, bool> excluded)
    {
        var bits = new byte[Linear16MaskOperations.RequiredByteLength(width, height)];
        var transform = input.Descriptor.SourceToDetectorTransform;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var source = new PixelPoint(
                    (x + 0.5 - transform.OffsetX) / transform.ScaleX,
                    (y + 0.5 - transform.OffsetY) / transform.ScaleY);
                if (excluded(projector.Unproject(source)))
                {
                    Set(bits, y * width + x);
                }
            }
        }
        return new Linear16PixelMask(width, height, bits);
    }

    private static Linear16PixelMask CreateObstructionMask(
        int width,
        int height,
        TransientDetectorInput input,
        IReadOnlyList<SensorCrop>? regions)
    {
        var bits = new byte[Linear16MaskOperations.RequiredByteLength(width, height)];
        if (regions is null)
        {
            return new Linear16PixelMask(width, height, bits);
        }
        var transform = input.Descriptor.SourceToDetectorTransform;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sourceX = (x + 0.5 - transform.OffsetX) / transform.ScaleX;
                var sourceY = (y + 0.5 - transform.OffsetY) / transform.ScaleY;
                if (regions.Any(region => sourceX >= region.X && sourceX < region.X + region.Width &&
                                          sourceY >= region.Y && sourceY < region.Y + region.Height))
                {
                    Set(bits, y * width + x);
                }
            }
        }
        return new Linear16PixelMask(width, height, bits);
    }

    private static void Set(byte[] bits, int index) => bits[index >> 3] |= (byte)(1 << (index & 7));

    private static TransientTemporalPosition ToPosition(int offset) => offset switch
    {
        -2 => TransientTemporalPosition.NMinus2,
        -1 => TransientTemporalPosition.NMinus1,
        0 => TransientTemporalPosition.N,
        1 => TransientTemporalPosition.NPlus1,
        2 => TransientTemporalPosition.NPlus2,
        _ => throw new ArgumentOutOfRangeException(nameof(offset))
    };
}

[Serializable]
public sealed class TransientWorkerExecutionException : InvalidOperationException
{
    public TransientWorkerExecutionException()
    {
    }

    public TransientWorkerExecutionException(string message) : base(message)
    {
    }

    public TransientWorkerExecutionException(string message, Exception innerException) : base(message, innerException)
    {
    }

    internal TransientWorkerExecutionException(string reasonCode, bool retryable) : base(reasonCode)
    {
        ReasonCode = reasonCode;
        Retryable = retryable;
    }

    public string? ReasonCode { get; }
    public bool Retryable { get; }
}
