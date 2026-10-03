using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.CameraAgent.Common.Automation;

namespace HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;

/// <summary>
/// The captured facts admission needs about one published preview, read without loading its payload. The exposure
/// clock and location provenance are the capture's own, carried through its derivative descriptor.
/// </summary>
internal sealed record NightlyProductCandidate(
    Guid ArtifactId,
    Guid CaptureId,
    string OutputIdentitySha256,
    string Variant,
    string RecipeIdentitySha256,
    string MediaType,
    DateTimeOffset ExposureStartedUtc,
    string RigProfileSha256,
    CaptureLocationProvenance? Location)
{
    internal bool UsesFixedDisplayTransfer { get; init; }
    internal long PayloadBytes { get; init; }
}

/// <summary>Reads nightly candidates and restores admitted ones as verified recipe inputs.</summary>
internal interface INightlyProductSourceReader
{
    /// <summary>
    /// Reads at most <paramref name="maximumCount"/> + 1 candidates exposed inside the window, ordered by exposure
    /// start. Outputs that cannot be nightly sources are counted in <paramref name="unsupported"/>.
    /// </summary>
    ValueTask<IReadOnlyList<NightlyProductCandidate>> ReadCandidatesAsync(
        string nodeId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int maximumCount,
        Action<string> unsupported,
        CancellationToken cancellationToken);

    /// <summary>Restores candidates in order, verifying every payload against its durable identity.</summary>
    ValueTask<IReadOnlyList<ProcessingArtifact>> RestoreAsync(
        IReadOnlyList<NightlyProductCandidate> candidates,
        CancellationToken cancellationToken);
}

/// <summary>The journal-backed reader over the processing store's published preview outputs.</summary>
internal sealed class JournalNightlyProductSourceReader(
    SqliteCaptureProcessingStore store,
    CaptureProcessingPersistence persistence) : INightlyProductSourceReader
{
    internal const string PackedImageMediaType = "application/x-hvo-packed-image";

    public async ValueTask<IReadOnlyList<NightlyProductCandidate>> ReadCandidatesAsync(
        string nodeId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int maximumCount,
        Action<string> unsupported,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unsupported);
        var outputs = await store.ReadPublishedOutputsByExposureAsync(
            nodeId, FrameArtifactRole.Preview, startUtc, endUtc, maximumCount, cancellationToken).ConfigureAwait(false);
        var candidates = new List<NightlyProductCandidate>(outputs.Count);
        foreach (var output in outputs)
        {
            if (output.Descriptor is not { } descriptor ||
                !string.Equals(descriptor.Artifact.MediaType, PackedImageMediaType, StringComparison.Ordinal))
            {
                unsupported(NightlyProductContract.ExcludedUnsupportedSourceReasonCode);
                continue;
            }
            candidates.Add(new NightlyProductCandidate(
                output.ArtifactId,
                descriptor.Capture.CaptureId,
                output.OutputIdentitySha256,
                descriptor.Artifact.Variant,
                output.RecipeIdentitySha256,
                descriptor.Artifact.MediaType,
                descriptor.Timing.ExposureStartedUtc.ToUniversalTime(),
                output.Compatibility.Rig,
                descriptor.Location)
            {
                PayloadBytes = descriptor.Layout.ByteLength,
                UsesFixedDisplayTransfer = output.Algorithms.Any(static algorithm =>
                    algorithm.Name == "fixed-display-transfer" && algorithm.Version == FixedDisplayTransfer.AlgorithmVersion)
            });
        }
        return candidates;
    }

    public async ValueTask<IReadOnlyList<ProcessingArtifact>> RestoreAsync(
        IReadOnlyList<NightlyProductCandidate> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var outputs = new List<DurableProcessingOutput>(candidates.Count);
        foreach (var candidate in candidates)
        {
            // Re-read at restore time: an output expired or quarantined since selection is a failed run, not a
            // silently thinner product.
            var output = await store.ReadOutputByArtifactIdAsync(candidate.ArtifactId, cancellationToken)
                .ConfigureAwait(false);
            if (output is null || output.AvailabilityState != "Available" ||
                !string.Equals(output.OutputIdentitySha256, candidate.OutputIdentitySha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("A selected nightly product source is no longer available.");
            }
            outputs.Add(output);
        }
        return await persistence.RestoreWindowInputsAsync(outputs, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The admitted sources of one window and the per-reason count of every excluded candidate.</summary>
internal sealed record NightlyProductAdmissionResult(
    IReadOnlyList<NightlyProductCandidate> Admitted,
    IReadOnlyDictionary<string, int> Exclusions);

/// <summary>
/// Admission from captured facts alone: pinned fixed transfer and rig, retained site and exact half-open window.
/// Keograms admit all actual sources; star trails additionally require a geometric Sun altitude at or below −18°
/// at each source's own exposure start. An unresolved capture location is excluded.
/// </summary>
internal static class NightlyProductAdmission
{
    internal static NightlyProductAdmissionResult Admit(
        IReadOnlyList<NightlyProductCandidate> candidates,
        string currentRigProfileSha256,
        LocalAutomationSourceWindow window,
        string sourceRecipeIdentitySha256,
        Func<NightlyProductCandidate, ObservatoryLocation?> locate,
        IPlanetEphemeris ephemeris,
        IReadOnlyDictionary<string, int>? priorExclusions = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(locate);
        ArgumentNullException.ThrowIfNull(ephemeris);
        var exclusions = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var prior in priorExclusions ?? new Dictionary<string, int>())
        {
            exclusions[prior.Key] = prior.Value;
        }
        var admitted = new List<NightlyProductCandidate>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var reason = Classify(candidate, currentRigProfileSha256, window, sourceRecipeIdentitySha256, locate, ephemeris);
            if (reason is null)
            {
                admitted.Add(candidate);
            }
            else
            {
                exclusions[reason] = exclusions.GetValueOrDefault(reason) + 1;
            }
        }
        return new NightlyProductAdmissionResult(admitted, exclusions);
    }

    private static string? Classify(
        NightlyProductCandidate candidate,
        string currentRigProfileSha256,
        LocalAutomationSourceWindow window,
        string sourceRecipeIdentitySha256,
        Func<NightlyProductCandidate, ObservatoryLocation?> locate,
        IPlanetEphemeris ephemeris)
    {
        if (!string.Equals(candidate.RigProfileSha256, currentRigProfileSha256, StringComparison.OrdinalIgnoreCase))
        {
            return NightlyProductContract.ExcludedRigReasonCode;
        }
        if (!candidate.UsesFixedDisplayTransfer ||
            !string.Equals(candidate.RecipeIdentitySha256, sourceRecipeIdentitySha256, StringComparison.OrdinalIgnoreCase))
        {
            return NightlyProductContract.ExcludedTransferReasonCode;
        }
        if (locate(candidate) is not { } location)
        {
            return NightlyProductContract.ExcludedLocationReasonCode;
        }
        if (Math.Abs(location.LatitudeDegrees - window.ReportingPeriod.Site.LatitudeDegrees) > 1e-8 ||
            Math.Abs(location.LongitudeDegrees - window.ReportingPeriod.Site.LongitudeDegrees) > 1e-8)
        {
            return NightlyProductContract.ExcludedLocationReasonCode;
        }
        return window.AcceptsSource(candidate.ExposureStartedUtc, ephemeris)
            ? null : NightlyProductContract.ExcludedSolarAltitudeReasonCode;
    }
}
