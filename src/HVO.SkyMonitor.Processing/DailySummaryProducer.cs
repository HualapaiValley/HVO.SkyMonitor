using System.Text.Json;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Processing;

/// <summary>A retained, admitted preview and its capture identity. No image payload is needed to summarize it.</summary>
public sealed record DailySummaryCaptureV1(Guid ArtifactId, Guid CaptureId, string OutputIdentitySha256,
    DateTimeOffset ObservationStartedUtc);

/// <summary>An immutable still product of the exact same reporting period and source window.</summary>
public sealed record DailySummaryProductV1(Guid ProductId, string Kind, string OutputIdentitySha256,
    DateTimeOffset ObservationStartedUtc);

/// <summary>
/// A snapshot, not a prediction: coverage counts five-minute bins containing an admitted exposure start, not exposure
/// duration or scheduled captures. Null event counts mean unavailable. Links name immutable products, never a latest URL.
/// </summary>
public sealed record DailySummaryV1(string SchemaVersion, DateOnly ObservingDate, string ReportingPeriodSha256,
    DateTimeOffset StartUtc, DateTimeOffset EndUtc, int CandidateCount,
    IReadOnlyList<DailySummaryCaptureV1> Captures, IReadOnlyList<DailySummaryProductV1> Products,
    IReadOnlyDictionary<string, int> Exclusions, Guid RepresentativeArtifactId, int SampledBins, int TotalBins)
{
    public const string CurrentSchemaVersion = "hvo-daily-summary-v1";
    public const int CoverageBinSeconds = 300;
    public int CaptureCount => Captures.Select(static source => source.CaptureId).Distinct().Count();
    public int? EventCount { get; init; }
    public bool Partial => SampledBins < TotalBins || Exclusions.Count > 0;
}

/// <summary>
/// Host-neutral daily-summary producer. The canonical facts live in recipe parameters in the provenance JSON; its
/// image is the actual preview nearest the window midpoint. Identity binds every source fact and the retained occurrence.
/// Only that representative image is restored, keeping pixel memory independent of the number of captures.
/// </summary>
public static class DailySummaryProducer
{
    public const string RecipeName = "daily-summary";
    public const string Variant = "daily-summary-v1";
    public const int MaximumCaptures = 4096;
    public const int MaximumProducts = 1024;
    private static readonly ProcessingRecipeDefinition Definition = new(RecipeName, "1.0.0", "daily-summary-v1",
        ProcessingOperationKind.Window);

    public static DailySummaryV1 Summarize(DateOnly observingDate, string reportingPeriodSha256,
        DateTimeOffset startUtc, DateTimeOffset endUtc, int candidateCount,
        IReadOnlyList<DailySummaryCaptureV1> captures, IReadOnlyList<DailySummaryProductV1> products,
        IReadOnlyDictionary<string, int> exclusions)
    {
        ArgumentNullException.ThrowIfNull(captures);
        ArgumentNullException.ThrowIfNull(products);
        ArgumentNullException.ThrowIfNull(exclusions);
        if (startUtc.Offset != TimeSpan.Zero || endUtc.Offset != TimeSpan.Zero || endUtc <= startUtc ||
            endUtc - startUtc > TimeSpan.FromDays(2) || !IsSha256(reportingPeriodSha256) ||
            captures.Count is < 1 or > MaximumCaptures || products.Count > MaximumProducts ||
            candidateCount < captures.Count || candidateCount > MaximumCaptures ||
            exclusions.Any(static pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value <= 0) ||
            exclusions.Values.Sum() != candidateCount - captures.Count ||
            captures.Any(source => source.ArtifactId == Guid.Empty || source.CaptureId == Guid.Empty ||
                !IsSha256(source.OutputIdentitySha256) || source.ObservationStartedUtc.Offset != TimeSpan.Zero ||
                source.ObservationStartedUtc < startUtc || source.ObservationStartedUtc >= endUtc) ||
            products.Any(source => source.ProductId == Guid.Empty || !IsSha256(source.OutputIdentitySha256) ||
                string.IsNullOrWhiteSpace(source.Kind) || source.ObservationStartedUtc.Offset != TimeSpan.Zero ||
                source.ObservationStartedUtc < startUtc || source.ObservationStartedUtc >= endUtc) ||
            captures.Select(static source => source.ArtifactId).Concat(products.Select(static source => source.ProductId))
                .Distinct().Count() != captures.Count + products.Count)
            throw new ArgumentException("Daily-summary sources or window are invalid or exceed their bounds.");
        var ordered = captures.OrderBy(static source => source.ObservationStartedUtc).ThenBy(static source => source.ArtifactId).ToArray();
        var midpoint = startUtc + (endUtc - startUtc) / 2;
        var representative = ordered.OrderBy(source => (source.ObservationStartedUtc - midpoint).Duration())
            .ThenBy(static source => source.ArtifactId).First();
        var bins = ordered.Select(source => (long)((source.ObservationStartedUtc - startUtc).TotalSeconds /
            DailySummaryV1.CoverageBinSeconds)).Distinct().Count();
        return new(DailySummaryV1.CurrentSchemaVersion, observingDate, reportingPeriodSha256, startUtc, endUtc,
            candidateCount, ordered,
            products.OrderBy(static product => product.Kind, StringComparer.Ordinal).ThenBy(static product => product.ProductId).ToArray(),
            new SortedDictionary<string, int>(exclusions.ToDictionary(), StringComparer.Ordinal), representative.ArtifactId,
            bins, (int)Math.Ceiling((endUtc - startUtc).TotalSeconds / DailySummaryV1.CoverageBinSeconds));
    }

    public static ProcessingRecipeIdentity CreateIdentity(DailySummaryV1 summary, ProcessingInputSelector selector,
        IReadOnlyList<ProcessingAuxiliaryInput> auxiliaryInputs)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(auxiliaryInputs);
        return ProcessingIdentity.CreateRecipeIdentity(Definition,
            ProcessingIdentity.BindExecutionInputs(ProcessingRecipeSupport.Normalize(summary), selector, null, auxiliaryInputs));
    }

    public static ProcessingProduct Produce(DailySummaryV1 summary, ProcessingArtifact representative,
        ProcessingInputSelector selector, IReadOnlyList<ProcessingAuxiliaryInput> auxiliaryInputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(representative);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(auxiliaryInputs);
        cancellationToken.ThrowIfCancellationRequested();
        if (summary.SchemaVersion != DailySummaryV1.CurrentSchemaVersion || representative.ArtifactId != summary.RepresentativeArtifactId ||
            representative.Role != FrameArtifactRole.Preview || !ProcessingRecipeSupport.Matches(representative, selector) ||
            !ProcessingRecipeSupport.TryValidateFrame(representative, out var layout, out _) ||
            layout.PixelFormat is not (CameraPixelFormat.Mono8 or CameraPixelFormat.Rgb24))
            throw new ArgumentException("A daily summary requires its selected representative preview.");
        // Recompute derived facts so callers cannot supply fabricated coverage or a different representative.
        var verified = Summarize(summary.ObservingDate, summary.ReportingPeriodSha256, summary.StartUtc, summary.EndUtc,
            summary.CandidateCount, summary.Captures, summary.Products, summary.Exclusions);
        if (!JsonSerializer.SerializeToElement(verified).GetRawText().Equals(
                JsonSerializer.SerializeToElement(summary).GetRawText(), StringComparison.Ordinal))
            throw new ArgumentException("Daily-summary facts are not canonical.");
        var recipe = CreateIdentity(summary, selector, auxiliaryInputs);
        Guid[] sources = [.. summary.Captures.Select(static source => source.ArtifactId), .. summary.Products.Select(static source => source.ProductId)];
        var pixels = ProcessingRecipeSupport.PackRows(layout, representative.Payload, cancellationToken);
        return new(FrameArtifactRole.Preview, Variant,
            ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Preview, Variant, recipe.IdentitySha256, sources),
            "application/x-hvo-packed-image", ProcessingRecipeSupport.CreatePackedLayout(layout), pixels,
            ProcessingIdentity.ComputePayloadSha256(pixels), recipe, [new(RecipeName, "daily-summary-v1")], sources,
            representative.Integration, representative.Compatibility);
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
