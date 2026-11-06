using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>Immutable local evidence with an exact snapshot reference or embedded records, and actual model time bytes.</summary>
public sealed record CaptureSatelliteInputV1(string SchemaVersion, ReconstructionDescriptor Descriptor,
    CameraRigConfig Rig, ObserverLocation? Observer, ReadOnlyMemory<byte>? Plan, ReadOnlyMemory<byte>? SnapshotManifest, ReadOnlyMemory<byte>? SnapshotRecords,
    ReadOnlyMemory<byte>? EarthOrientation, ReadOnlyMemory<byte>? LeapSeconds, string TimeSource, DateTimeOffset? TimeAvailableUtc,
    string? UnavailableReason)
{
    public JsonElement? TimeProvenance { get; init; }
    public JsonElement? RequestOptions { get; init; }
}

/// <summary>Query availability is independent of whether any exposure segment falls in the image.</summary>
public sealed record CaptureSatelliteStatusV1(string SchemaVersion, string InputIdentitySha256,
    string Status, string Reason, int TrackCount, string GeometryBasis, OrbitalUtcInterval Exposure,
    OrbitalPopulationCoverage Coverage, DateTimeOffset? SourceRetrievedUtc, double? MaximumEpochAgeHours,
    string TimeSource, string PhysicalVisibility, bool Renderable, string ElementFreshness);

/// <summary>Shared reconstruction/publication composition; no host, storage, scheduler or current pointers.</summary>
public static class CaptureSatelliteProducts
{
    public const string InputSchema = "hvo-capture-satellite-evidence-v1";
    public const string StatusSchema = "hvo-capture-satellite-status-v1";
    public const string SourceSchema = "hvo-predicted-satellite-source-v1";
    public const string InputMediaType = "application/vnd.hvo.capture-satellite-evidence+json";
    public const string StatusMediaType = "application/vnd.hvo.capture-satellite-status+json";
    public const string SourceMediaType = "application/vnd.hvo.predicted-satellite-source+json";
    public const string Version = "capture-satellite-products-v1";
    public const int MaximumSnapshotBytes = 16 * 1024 * 1024;
    public const int MaximumInputBytes = 40 * 1024 * 1024;

    public static byte[] SerializeInput(CaptureSatelliteInputV1 input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Validate(input);
        var bytes = OrbitalContract.SerializeCanonical(input);
        Require(bytes.Length <= MaximumInputBytes, "satellite-input-byte-bound");
        return bytes;
    }

    public static CaptureSatelliteInputV1 ParseInput(ReadOnlySpan<byte> bytes)
    {
        Require(bytes.Length is > 0 and <= MaximumInputBytes, "satellite-input-byte-bound");
        var value = CaptureOrbitalPredictionJson.Deserialize<CaptureSatelliteInputV1>(bytes);
        Require(bytes.SequenceEqual(SerializeInput(value)), "satellite-input-noncanonical");
        return value;
    }

    public static CaptureOrbitalPredictionInput Restore(CaptureSatelliteInputV1 input, OrbitalSnapshotReadResult? exactSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        Require(input.Plan is not null, "satellite-plan-unavailable");
        OrbitalSnapshotReadResult snapshot;
        if (input.SnapshotManifest is null)
            snapshot = OrbitalSnapshotReadResult.Unavailable(
                CaptureOrbitalPredictionJson.Deserialize<CaptureOrbitalPredictionPlanV1>(input.Plan!.Value.Span).SnapshotUnavailableReason
                ?? throw new ArgumentException("satellite-snapshot-reason"));
        else if (input.SnapshotRecords is { } records)
            snapshot = OrbitalSnapshotReadResult.Available(OrbitalSnapshot.Parse(input.SnapshotManifest.Value,
                records).Snapshot ?? throw new ArgumentException("satellite-snapshot-corrupt"));
        else
        {
            ArgumentNullException.ThrowIfNull(exactSnapshot);
            snapshot = exactSnapshot;
        }
        ArgumentNullException.ThrowIfNull(snapshot);
        Require(input.SnapshotManifest is null || snapshot.Snapshot is { } resolved &&
            input.SnapshotManifest.Value.Span.SequenceEqual(resolved.SerializeManifest()), "satellite-exact-snapshot-mismatch");
        return CaptureOrbitalPredictionInput.Restore(input.Plan!.Value.Span, snapshot);
    }

    public static OrbitalSnapshotManifestV1? ReadSnapshotManifest(CaptureSatelliteInputV1 input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.SnapshotManifest is not { } bytes) return null;
        Require(bytes.Length <= OrbitalContract.MaximumManifestBytes, "satellite-manifest-bound");
        var manifest = CaptureOrbitalPredictionJson.Deserialize<OrbitalSnapshotManifestV1>(bytes.Span);
        Require(manifest.ManifestSha256 == OrbitalSnapshot.ComputeManifestIdentity(manifest) &&
            bytes.Span.SequenceEqual(OrbitalContract.SerializeCanonical(manifest)), "satellite-manifest-identity");
        return manifest;
    }

    public static OrbitalTimeData RestoreTime(CaptureSatelliteInputV1 input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return OrbitalTimeData.FromFinals2000A(input.EarthOrientation!.Value.Span, input.LeapSeconds!.Value.Span);
    }

    private static void Validate(CaptureSatelliteInputV1 input)
    {
        ArgumentNullException.ThrowIfNull(input.Rig);
        Require(input.SchemaVersion == InputSchema && input.Descriptor.Validate().IsValid &&
            input.Descriptor.Artifact.Role == FrameArtifactRole.Raw &&
            RigProjectionContextFactory.CreateProfileHashSha256(input.Rig) == input.Descriptor.Profiles.Rig.Sha256 &&
            input.TimeSource is "Bundled" or "Managed" or "Override" &&
            input.UnavailableReason is null or { Length: > 0 and <= 128 }, "satellite-evidence-binding");
        input.Observer?.Validate();
        Require((input.SnapshotRecords is null || input.SnapshotManifest is not null) &&
            input.SnapshotRecords is null or { Length: <= MaximumSnapshotBytes } &&
            input.EarthOrientation is null or { Length: <= 8 * 1024 * 1024 } &&
            input.LeapSeconds is null or { Length: <= 64 * 1024 }, "satellite-evidence-bound");
        if (input.Plan is null)
        {
            Require(input.UnavailableReason is not null, "satellite-unavailable-reason");
            return;
        }
        var plan = CaptureOrbitalPredictionJson.Deserialize<CaptureOrbitalPredictionPlanV1>(input.Plan.Value.Span);
        Require(input.Plan.Value.Span.SequenceEqual(OrbitalContract.SerializeCanonical(plan)) &&
            input.UnavailableReason is null && plan.DescriptorSha256 == CaptureContractJson.ComputeDescriptorSha256(input.Descriptor) &&
            plan.Observer == input.Observer && plan.Availability.TimeDataAvailableUtc == input.TimeAvailableUtc &&
            plan.Geometry.Projection == RigProjectionContextFactory.Create(input.Rig) &&
            (plan.SnapshotManifest is null ? input.SnapshotManifest is null :
                input.SnapshotManifest is { } manifestBytes && manifestBytes.Span.SequenceEqual(OrbitalContract.SerializeCanonical(plan.SnapshotManifest))) &&
            plan.Model == OrbitalPredictionModel.Create(RestoreTime(input), plan.Model.ObserverHeightDatum == "wgs84-ellipsoidal-assumed"),
            "satellite-plan-binding");
        _ = ReadSnapshotManifest(input);
        // Compact carriers defer complete selection validation to Restore with an explicit exact snapshot.
        if (input.SnapshotRecords is not null || input.SnapshotManifest is null)
            _ = new CaptureOrbitalPredictionRecipe(Restore(input), RestoreTime(input));
    }

    /// <summary>Executes only the supplied pinned inputs. Hosts preserve the first completed bytes before publication.</summary>
    public static async ValueTask<ProcessingProduct?> PredictAsync(CaptureSatelliteInputV1 input, ProcessingArtifact raw,
        string outputVariant, CancellationToken cancellationToken, TimeProvider? clock = null,
        OrbitalSnapshotReadResult? exactSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(raw);
        Validate(input);
        if (input.Plan is null) return null;
        var recipe = new CaptureOrbitalPredictionRecipe(Restore(input, exactSnapshot), RestoreTime(input), clock);
        var outcome = await new ProcessingRecipeExecutor([recipe]).ExecuteAsync(new(
            CaptureOrbitalPredictionRecipe.Name, JsonSerializer.SerializeToElement(new { }),
            ProcessingInputSelector.Raw(raw.Variant), [raw], outputVariant, InputArtifactId: raw.ArtifactId),
            cancellationToken).ConfigureAwait(false);
        if (outcome.Status != ProcessingOutcomeStatus.Produced)
            throw new InvalidDataException($"Capture satellite recipe rejected its raw evidence ({outcome.ReasonCode}).");
        return outcome.Products.Single();
    }

    /// <summary>Rebuilds typed products from retained result bytes, including a separate truthful query status.</summary>
    public static IReadOnlyList<ProcessingProduct> Create(CaptureSatelliteInputV1 input, ProcessingArtifact raw,
        string variant, ReadOnlyMemory<byte>? resultBytes, OrbitalSnapshotReadResult? exactSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(raw);
        var inputBytes = SerializeInput(input);
        Require(raw.ArtifactId == input.Descriptor.Artifact.ArtifactId && raw.CaptureId == input.Descriptor.Capture.CaptureId,
            "satellite-raw-lineage");
        var restored = input.Plan is null ? null : Restore(input, exactSnapshot);
        Require(restored is null || restored.Matches(raw), "satellite-raw-evidence");
        var evidence = Metadata("capture-satellite-evidence", variant + "-inputs", InputSchema, InputMediaType,
            inputBytes, [raw]);
        var products = new List<ProcessingProduct> { evidence };
        CaptureOrbitalPredictionV1? prediction = null;
        ProcessingProduct? result = null;
        if (resultBytes is { } bytes)
        {
            Require(restored is not null, "satellite-result-requires-plan");
            prediction = CaptureOrbitalPredictionJson.Parse(bytes.Span, restored!);
            var recipe = new CaptureOrbitalPredictionRecipe(restored!, RestoreTime(input));
            var selector = ProcessingInputSelector.Raw(raw.Variant);
            var identity = ProcessingIdentity.CreateRecipeIdentity(recipe.Definition,
                ProcessingIdentity.BindExecutionInputs(recipe.NormalizeOptions(JsonSerializer.SerializeToElement(new { })), selector, null));
            result = ProcessingRecipeSupport.CreateProduct(FrameArtifactRole.Metadata, variant,
                CaptureOrbitalPredictionJson.MediaType, null, bytes, identity,
                [new(CaptureOrbitalPredictionRecipe.Name, CaptureOrbitalPredictionRecipe.AlgorithmVersion),
                 new(OrbitalPredictionModel.Propagator.Name, OrbitalPredictionModel.Propagator.Version),
                 new("teme-conversion", OrbitalTimeData.ConversionVersion), new("orbital-selection", OrbitalSelectionPolicy.CurrentVersion)],
                [raw], raw.Integration, raw.Compatibility, ProcessingProductKind.Metadata,
                CaptureOrbitalPredictionJson.SchemaVersion, ProcessingIdentity.ComputePayloadSha256(bytes));
            products.Add(result);
        }
        else Require(input.Plan is null, "satellite-result-required");
        var status = Status(input, prediction, ProcessingIdentity.ComputePayloadSha256(inputBytes));
        PresentationLayerPayloadV1? trackPayload = null;
        byte[]? sourceFacts = null;
        if (prediction is { Tracks.Count: > 0 })
        {
            try
            {
                (trackPayload, sourceFacts) = OrbitalTrackPresentation.CreatePayloadAndSourceFacts(
                    input.Descriptor.Layout.Width, input.Descriptor.Layout.Height, prediction.Tracks,
                    displayNames: null, queryIncomplete: prediction.Status == OrbitalExecutionStatus.Partial);
                status = status with { Renderable = true };
            }
            catch (ArgumentException)
            {
                status = status with { Reason = "PresentationWithheld:" + status.Reason };
            }
        }
        var statusProduct = Metadata("capture-satellite-status", variant + "-status", StatusSchema, StatusMediaType,
            OrbitalContract.SerializeCanonical(status), [Artifact(evidence, raw)]);
        products.Add(statusProduct);
        if (trackPayload is not null && sourceFacts is not null && result is not null)
        {
            // Query-level partial state stays in status even if the useful prefix contains only complete tracks.
            var source = Metadata("capture-satellite-source", variant + "-source", SourceSchema, SourceMediaType,
                sourceFacts, [Artifact(result, raw), Artifact(statusProduct, raw)]);
            products.Add(source);
            products.Add(PresentationProcessingProducts.CreateLayerProduct(trackPayload, variant + "-layer",
                [Artifact(source, raw)], OrbitalTrackPresentation.ProducerVersion));
        }
        else
        {
            // This empty payload carries availability through the existing manifest, never fabricated geometry.
            var empty = PresentationLayerPayloadJson.Create(statusProduct.ContentIdentitySha256!, input.Descriptor.Layout.Width,
                input.Descriptor.Layout.Height);
            products.Add(PresentationProcessingProducts.CreateLayerProduct(empty, variant + "-layer",
                [Artifact(statusProduct, raw)], OrbitalTrackPresentation.ProducerVersion));
        }
        return products;
    }

    public static CaptureSatelliteStatusV1 ParseStatus(ReadOnlySpan<byte> bytes)
    {
        var value = CaptureOrbitalPredictionJson.Deserialize<CaptureSatelliteStatusV1>(bytes);
        Require(value.SchemaVersion == StatusSchema && value.GeometryBasis == "ConfiguredRig" && value.PhysicalVisibility == "Unknown" &&
            bytes.SequenceEqual(OrbitalContract.SerializeCanonical(value)), "satellite-status");
        return value;
    }

    private static CaptureSatelliteStatusV1 Status(CaptureSatelliteInputV1 input, CaptureOrbitalPredictionV1? output, string identity)
    {
        var selections = output?.Input.Selections ?? [];
        var ages = selections.SelectMany(static s => s.Candidates)
            .SelectMany(static c => new[] { Math.Abs(c.StartEpochAgeHours), Math.Abs(c.EndEpochAgeHours) }).ToArray();
        var manifest = ReadSnapshotManifest(input);
        return new(StatusSchema, identity, output?.Status.ToString() ?? "Unavailable",
            input.UnavailableReason ?? (output!.Input.SnapshotUnavailableReason?.ToString() ??
                (output.Tracks.Count == 0 && output.Status == OrbitalExecutionStatus.Complete ? "NoExposureSegments" : output.Reason.ToString())),
            output?.Tracks.Count ?? 0, "ConfiguredRig", new(input.Descriptor.Timing.ExposureStartedUtc, input.Descriptor.Timing.ExposureEndedUtc),
            output?.Input.PopulationCoverage ?? manifest?.Coverage ?? new("explicit-requested-object-identities", OrbitalPopulationCompleteness.Unknown,
                ["Prediction inputs unavailable; population coverage unknown."]),
            (output?.Input.SnapshotManifest ?? manifest)?.Sources.Max(static s => (DateTimeOffset?)s.RetrievedUtc),
            ages.Length == 0 ? null : ages.Max(), input.TimeSource, "Unknown", false,
            selections.Count == 0 ? "Unknown" : string.Join(", ", selections.SelectMany(static s => s.Candidates)
                .Select(static c => c.Freshness.ToString()).Distinct().Order(StringComparer.Ordinal)));
    }

    private static ProcessingProduct Metadata(string recipe, string variant, string schema, string mediaType,
        ReadOnlyMemory<byte> bytes, IReadOnlyList<ProcessingArtifact> sources)
    {
        var hash = ProcessingIdentity.ComputePayloadSha256(bytes);
        var definition = new ProcessingRecipeDefinition(recipe, "1.0.0", Version,
            recipe == "capture-satellite-source" ? ProcessingOperationKind.Transform : ProcessingOperationKind.Analyzer);
        var identity = ProcessingIdentity.CreateRecipeIdentity(definition,
            CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(new { schema, hash })));
        return ProcessingRecipeSupport.CreateProduct(FrameArtifactRole.Metadata, variant, mediaType, null, bytes,
            identity, [new(recipe, Version)], sources, sources[0].Integration, sources[0].Compatibility,
            ProcessingProductKind.Metadata, schema, hash);
    }

    private static ProcessingArtifact Artifact(ProcessingProduct product, ProcessingArtifact raw) => raw with
    {
        ArtifactId = ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256),
        Role = product.Role,
        Variant = product.Variant,
        RecipeIdentitySha256 = product.Recipe.IdentitySha256,
        MediaType = product.MediaType,
        Layout = product.Layout,
        Payload = product.Payload,
        ProductKind = product.Kind,
        SchemaVersion = product.SchemaVersion,
        ContentIdentitySha256 = product.ContentIdentitySha256,
        SourceArtifactIds = product.SourceArtifactIds,
        DescriptorIdentitySha256 = null,
        RecipeDescriptor = product.Recipe.Descriptor
    };

    private static void Require(bool condition, string reason) => CaptureOrbitalPredictionInput.Require(condition, reason);
}
