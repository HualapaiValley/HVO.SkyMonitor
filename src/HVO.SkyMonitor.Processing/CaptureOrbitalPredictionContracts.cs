using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Processing;

public enum CaptureOrbitalAssessmentMode { OriginalAvailability, RetrospectiveReassessment }

/// <summary>Explicit knowledge cutoffs, independent of the exposure and of the execution wall clock.</summary>
public sealed record CaptureOrbitalAvailabilityV1(CaptureOrbitalAssessmentMode Mode,
    DateTimeOffset OriginalCutoffUtc, DateTimeOffset AssessmentCutoffUtc, DateTimeOffset TimeDataAvailableUtc);

public sealed record CaptureOrbitalSelectionPolicyV1(string Version, string IdentitySha256,
    bool AllowStaleDiagnostics, IReadOnlyList<OrbitalSourcePriority> Sources);

/// <summary>Portable, retained inputs. Selected records and rejected candidates survive even without tracks.</summary>
public sealed record CaptureOrbitalPredictionPlanV1(string SchemaVersion, ReconstructionDescriptor Descriptor,
    string DescriptorSha256, ObserverLocation Observer, OrbitalGeometryFacts Geometry, OrbitalTimingFacts Timing,
    OrbitalPredictionModelFacts Model, OrbitalQueryBounds Bounds, CaptureOrbitalAvailabilityV1 Availability,
    OrbitalSnapshotManifestV1? SnapshotManifest, OrbitalSnapshotUnavailableReason? SnapshotUnavailableReason,
    OrbitalPopulationCoverage PopulationCoverage,
    CaptureOrbitalSelectionPolicyV1 SelectionPolicy, IReadOnlyList<OrbitalObjectIdentity> Objects,
    IReadOnlyList<OrbitalRecordSelection> Selections);

/// <summary>
/// A bounded recipe input constructed from already resolved local evidence. No reader, resolver or current pointer.
/// The descriptor authenticates raw bytes and capture-time profiles; observer/optics remain configured predictions.
/// </summary>
public sealed class CaptureOrbitalPredictionInput
{
    public const string SchemaVersion = "hvo-capture-orbital-input-v1";
    public const int MaximumObjects = 256, MaximumSelectionCandidates = 4096, MaximumTracks = 32, MaximumSamples = 8192;
    public const int MaximumPlanBytes = 4 * 1024 * 1024;
    public CaptureOrbitalPredictionPlanV1 Plan { get; }
    public string IdentitySha256 { get; }
    public OrbitalExposureQuery? Query { get; }
    private readonly byte[] canonicalPlan;

    public CaptureOrbitalPredictionInput(ReconstructionDescriptor descriptor, ObserverLocation observer,
        OrbitalGeometryFacts geometry, OrbitalTimingFacts timing, OrbitalPredictionModelFacts model,
        OrbitalQueryBounds bounds, CaptureOrbitalAvailabilityV1 availability, OrbitalSnapshotReadResult resolvedSnapshot,
        OrbitalSelectionPolicy selectionPolicy, IEnumerable<OrbitalObjectIdentity> objects)
    {
        ArgumentNullException.ThrowIfNull(descriptor); ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(timing); ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(bounds); ArgumentNullException.ThrowIfNull(availability);
        ArgumentNullException.ThrowIfNull(resolvedSnapshot); ArgumentNullException.ThrowIfNull(selectionPolicy);
        ArgumentNullException.ThrowIfNull(objects);
        Require(descriptor.Validate().IsValid && descriptor.Artifact.Role == FrameArtifactRole.Raw, "raw-descriptor");
        observer.Validate(); geometry.Validate(); timing.Validate(); model.Validate(); bounds.Validate();
        Require(geometry.Basis == OrbitalGeometryBasis.ConfiguredRig && geometry.UncertaintyIdentitySha256 is null &&
            EqualHash(geometry.RigIdentitySha256, descriptor.Profiles.Rig.Sha256) &&
            EqualHash(geometry.ReadoutIdentitySha256, CaptureContractJson.ComputeCanonicalJsonSha256(geometry.Readout)) &&
            geometry.Readout == descriptor.Layout.Readout &&
            geometry.ImageTransform.OutputWidthPixels == descriptor.Layout.Width &&
            geometry.ImageTransform.OutputHeightPixels == descriptor.Layout.Height, "capture-configured-geometry");
        Require(timing.Exposure.StartUtc == descriptor.Timing.ExposureStartedUtc &&
            timing.Exposure.EndUtc == descriptor.Timing.ExposureEndedUtc &&
            (timing.ReadoutCompletedUtc is null || timing.ReadoutCompletedUtc == descriptor.Timing.ReadoutCompletedUtc) &&
            timing.ExposureBasis != OrbitalTimingBasis.Actual && timing.ReadoutBasis != OrbitalTimingBasis.Actual,
            "capture-exposure-or-reported-timing");
        Require(bounds.MaximumObjects <= MaximumObjects && bounds.MaximumTracks <= MaximumTracks &&
            bounds.MaximumSamples <= MaximumSamples && bounds.MaximumWindowSeconds <= 120 &&
            bounds.MaximumEvaluations <= 100_000 && bounds.MaximumElapsedMilliseconds <= 10_000 &&
            (timing.Exposure.EndUtc - timing.Exposure.StartUtc).TotalSeconds <= bounds.MaximumWindowSeconds,
            "capture-work-bounds");
        Require(Enum.IsDefined(availability.Mode) && Utc(availability.OriginalCutoffUtc) &&
            Utc(availability.AssessmentCutoffUtc) && Utc(availability.TimeDataAvailableUtc) &&
            availability.OriginalCutoffUtc == descriptor.Timing.DurableIngressUtc &&
            availability.TimeDataAvailableUtc <= availability.AssessmentCutoffUtc &&
            (availability.Mode == CaptureOrbitalAssessmentMode.OriginalAvailability
                ? availability.AssessmentCutoffUtc == availability.OriginalCutoffUtc
                : availability.AssessmentCutoffUtc >= availability.OriginalCutoffUtc), "availability-cutoff");
        var ids = objects.Take(bounds.MaximumObjects + 1).OrderBy(static o => o.CatalogNumber, StringComparer.Ordinal).ToArray();
        Require(ids.Length > 0 && ids.Length <= bounds.MaximumObjects && ids.Distinct().Count() == ids.Length &&
            ids.All(OrbitalContract.IsObjectIdentity), "capture-population");
        var snapshot = resolvedSnapshot.Snapshot;
        Require(snapshot is null || snapshot.Manifest.Sources.All(s => s.RetrievedUtc <= availability.AssessmentCutoffUtc &&
                (s.ProviderGeneratedUtc is null || s.ProviderGeneratedUtc <= availability.AssessmentCutoffUtc)), "snapshot-availability-or-bound");
        if (snapshot is not null)
            Query = new(new(OrbitalPredictionUse.Diagnostic, snapshot, selectionPolicy, timing.Exposure, ids,
                observer, model, bounds), geometry, timing);
        var selections = snapshot is null ? [] : ids.Select(id =>
            OrbitalRecordSelector.Select(snapshot, id, timing.Exposure, selectionPolicy)).ToArray();
        Require(selections.Sum(static s => (long)s.Candidates.Count) <= MaximumSelectionCandidates, "capture-selection-bound");
        var plan = new CaptureOrbitalPredictionPlanV1(SchemaVersion, descriptor,
            CaptureContractJson.ComputeDescriptorSha256(descriptor), observer, geometry, timing, model, bounds, availability,
            snapshot?.Manifest, resolvedSnapshot.UnavailableReason,
            snapshot?.Manifest.Coverage ?? new("explicit-requested-object-identities", OrbitalPopulationCompleteness.Unknown,
                ["Snapshot unavailable; population coverage unknown."]),
            new(OrbitalSelectionPolicy.CurrentVersion, selectionPolicy.IdentitySha256, selectionPolicy.AllowStaleDiagnostics, selectionPolicy.Sources),
            ids, selections);
        canonicalPlan = OrbitalContract.SerializeCanonical(plan);
        Require(canonicalPlan.Length <= MaximumPlanBytes, "capture-plan-byte-bound");
        // Freeze caller-owned descriptor lineage and all nested selection/population arrays.
        var frozen = CaptureOrbitalPredictionJson.Deserialize<CaptureOrbitalPredictionPlanV1>(canonicalPlan);
        Plan = Freeze(frozen);
        IdentitySha256 = ProcessingIdentity.ComputePayloadSha256(canonicalPlan);
    }

    private static CaptureOrbitalPredictionPlanV1 Freeze(CaptureOrbitalPredictionPlanV1 plan) => plan with
    {
        Descriptor = plan.Descriptor with
        {
            Artifact = plan.Descriptor.Artifact with
            { SourceArtifactIds = Array.AsReadOnly(plan.Descriptor.Artifact.SourceArtifactIds.ToArray()) }
        },
        Objects = Array.AsReadOnly(plan.Objects.ToArray()),
        PopulationCoverage = plan.PopulationCoverage with { KnownExclusions = Array.AsReadOnly(plan.PopulationCoverage.KnownExclusions.ToArray()) },
        SelectionPolicy = plan.SelectionPolicy with { Sources = Array.AsReadOnly(plan.SelectionPolicy.Sources.ToArray()) },
        Selections = Array.AsReadOnly(plan.Selections.Select(static s => s with
        { Candidates = Array.AsReadOnly(s.Candidates.ToArray()) }).ToArray()),
        SnapshotManifest = plan.SnapshotManifest is not { } manifest ? null : manifest with
        {
            Sources = Array.AsReadOnly(manifest.Sources.ToArray()),
            Coverage = manifest.Coverage with { KnownExclusions = Array.AsReadOnly(manifest.Coverage.KnownExclusions.ToArray()) }
        }
    };

    public byte[] SerializePlan() => canonicalPlan.ToArray();

    /// <summary>Reconstructs the exact retained input using an exact snapshot read, including its unavailable state.</summary>
    public static CaptureOrbitalPredictionInput Restore(ReadOnlySpan<byte> canonicalPlan, OrbitalSnapshotReadResult resolvedSnapshot)
    {
        Require(canonicalPlan.Length is > 0 and <= MaximumPlanBytes, "capture-plan-byte-bound");
        var plan = CaptureOrbitalPredictionJson.Deserialize<CaptureOrbitalPredictionPlanV1>(canonicalPlan);
        var restored = new CaptureOrbitalPredictionInput(plan.Descriptor, plan.Observer, plan.Geometry, plan.Timing,
            plan.Model, plan.Bounds, plan.Availability, resolvedSnapshot,
            new(plan.SelectionPolicy.Sources, plan.SelectionPolicy.AllowStaleDiagnostics), plan.Objects);
        Require(canonicalPlan.SequenceEqual(restored.canonicalPlan), "capture-plan-exact-evidence");
        return restored;
    }

    internal void ValidateTime(OrbitalTimeData time)
    {
        ArgumentNullException.ThrowIfNull(time);
        Require(Plan.Model == OrbitalPredictionModel.Create(time,
            Plan.Model.ObserverHeightDatum == "wgs84-ellipsoidal-assumed"), "capture-resolved-time-model");
        // Continuous EOP/leap coverage is evaluated by the engine and retained as a no-result reason.
    }

    internal bool Matches(ProcessingArtifact source) => source.ArtifactId == Plan.Descriptor.Artifact.ArtifactId &&
        source.CaptureId == Plan.Descriptor.Capture.CaptureId && source.CaptureSequence == Plan.Descriptor.Capture.CaptureSequence &&
        source.Role == FrameArtifactRole.Raw && source.Variant == Plan.Descriptor.Artifact.Variant &&
        source.MediaType == Plan.Descriptor.Artifact.MediaType && source.CreatedUtc == Plan.Descriptor.Artifact.CreatedUtc &&
        EqualHash(source.DescriptorIdentitySha256, Plan.DescriptorSha256) && source.Layout == Plan.Descriptor.Layout &&
        source.ObservationStartedUtc == Plan.Timing.Exposure.StartUtc && source.ObservationEndedUtc == Plan.Timing.Exposure.EndUtc &&
        source.Integration == Plan.Descriptor.Controls.EffectiveExposure &&
        EqualHash(source.RecipeIdentitySha256, ProcessingIdentity.CreateRecipeIdentity(Plan.Descriptor.Artifact.Recipe).IdentitySha256) &&
        source.Payload.Length == Plan.Descriptor.Layout.ByteLength &&
        EqualHash(ProcessingIdentity.ComputePayloadSha256(source.Payload), Plan.Descriptor.Artifact.ChecksumSha256);

    internal static bool EqualHash(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool Utc(DateTimeOffset value) => value.Offset == TimeSpan.Zero;
    internal static void Require(bool condition, string reason)
    {
        if (!condition) throw new ArgumentException(reason);
    }
}

/// <summary>Canonical retained result; elapsed execution time and process diagnostics are deliberately excluded.</summary>
public sealed record CaptureOrbitalPredictionV1(string SchemaVersion, string InputIdentitySha256,
    CaptureOrbitalPredictionPlanV1 Input, string PhysicalVisibility, OrbitalExecutionStatus Status,
    OrbitalPredictionReason Reason, int EvaluatedObjects, int PropagationEvaluations,
    IReadOnlyList<OrbitalExposureTrackV1> Tracks, IReadOnlyList<OrbitalObjectFailure> Failures);

public static class CaptureOrbitalPredictionJson
{
    public const string SchemaVersion = "hvo-capture-orbital-prediction-v1";
    public const string MediaType = "application/vnd.hvo.capture-orbital-prediction+json";
    public const int MaximumOutputBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    internal static T Deserialize<T>(ReadOnlySpan<byte> bytes) => JsonSerializer.Deserialize<T>(bytes, Options)
        ?? throw new ArgumentException("missing-capture-prediction");

    public static byte[] Serialize(CaptureOrbitalPredictionV1 value, CaptureOrbitalPredictionInput expected)
    {
        ArgumentNullException.ThrowIfNull(value); ArgumentNullException.ThrowIfNull(expected);
        CaptureOrbitalPredictionInput.Require(value.SchemaVersion == SchemaVersion && value.PhysicalVisibility == "Unknown" &&
            value.InputIdentitySha256 == expected.IdentitySha256 &&
            OrbitalContract.SerializeCanonical(value.Input).AsSpan().SequenceEqual(expected.SerializePlan()), "capture-prediction-binding");
        ArgumentNullException.ThrowIfNull(value.Tracks); ArgumentNullException.ThrowIfNull(value.Failures);
        if (expected.Query is { } query)
        {
            // Reuse the engine's result contract for track hashes, query, selection, model, geometry, sampling and budgets.
            _ = new OrbitalPredictionResult<OrbitalExposureTrackV1>(query, value.Status, value.Reason,
                value.EvaluatedObjects, value.PropagationEvaluations, 0, value.Tracks, value.Failures);
            // The general result contract validates provenance/bounds, but the retained capture envelope must
            // also preserve the engine's query-level state and every failed selection in the visited prefix.
            CaptureOrbitalPredictionInput.Require(value.Status == (value.Reason == OrbitalPredictionReason.None
                ? OrbitalExecutionStatus.Complete : value.Tracks.Count == 0
                    ? OrbitalExecutionStatus.Unavailable : OrbitalExecutionStatus.Partial) &&
                (value.Status != OrbitalExecutionStatus.Complete || value.Failures.Count == 0), "capture-result-state");
            var failures = value.Failures.ToDictionary(static f => f.ObjectIdentity);
            foreach (var selection in expected.Plan.Selections.Take(value.EvaluatedObjects))
                if (selection.SelectedRecord is null)
                    CaptureOrbitalPredictionInput.Require(failures.TryGetValue(selection.ObjectIdentity, out var failure) &&
                        failure.Reason == (selection.Eligibility == OrbitalSelectionEligibility.MissingObject
                            ? OrbitalPredictionReason.ObjectMissing : OrbitalPredictionReason.SelectionWithheld),
                        "capture-selection-failure");
        }
        else
            CaptureOrbitalPredictionInput.Require(value.Status == OrbitalExecutionStatus.Unavailable &&
                value.Reason == OrbitalPredictionReason.SelectionWithheld && value.EvaluatedObjects == 0 &&
                value.PropagationEvaluations == 0 && value.Tracks.Count == 0 && value.Failures.Count == 0, "unavailable-snapshot-result");
        var bytes = OrbitalContract.SerializeCanonical(value);
        CaptureOrbitalPredictionInput.Require(bytes.Length <= MaximumOutputBytes, "capture-prediction-byte-bound");
        return bytes;
    }

    /// <summary>Requires the exact retained input; never substitutes a current capture, snapshot, model or geometry.</summary>
    public static CaptureOrbitalPredictionV1 Parse(ReadOnlySpan<byte> bytes, CaptureOrbitalPredictionInput expected)
    {
        CaptureOrbitalPredictionInput.Require(bytes.Length is > 0 and <= MaximumOutputBytes, "capture-prediction-byte-bound");
        var value = Deserialize<CaptureOrbitalPredictionV1>(bytes);
        CaptureOrbitalPredictionInput.Require(bytes.SequenceEqual(Serialize(value, expected)), "noncanonical-capture-prediction");
        return value;
    }
}
