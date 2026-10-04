using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;

/// <summary>Bounds of the read-only nightly product projections.</summary>
public static class NightlyProductProjectionContract
{
    /// <summary>The most report dates one summary reads; a six-week calendar grid needs 42.</summary>
    public const int MaximumSummarizedDates = 62;

    /// <summary>The most recorded final evaluations one summary reads; larger ranges fail rather than truncate.</summary>
    public const int MaximumSummarizedWindows = 8192;

    /// <summary>The most preview frames one product's lineage may hold; one window admits no more.</summary>
    public const int MaximumLineageFrames = NightlyProductContract.MaximumWindowCandidates;
}

/// <summary>
/// The recorded final evaluations of one product kind on one report date. A daily product is the final of a whole
/// sunrise period; hourly products are finals of its completed civil hours. When more than one automation definition
/// or revision evaluated the same span, a produced evaluation is preferred and then the latest evaluated; nothing here
/// claims one replaced another.
/// </summary>
public sealed record NightlyProductDateSummary(
    DateOnly ObservingDate,
    NightlyProductKind Kind,
    Guid? DailyProductId,
    NightlyProductWindowDisposition? DailyDisposition,
    string? DailyReasonCode,
    int HourlyProduced,
    int HourlyWithoutProduct);

/// <summary>A recorded window evaluation and the products it currently points at, in part order.</summary>
public sealed record NightlyProductWindowRecord(
    NightlyProductWindowStatus Status,
    IReadOnlyList<NightlyProductSummary> CurrentProducts)
{
    /// <summary>Whether the evaluation covered a completed civil hour or a whole sunrise period.</summary>
    public LocalAutomationSourceWindowKind WindowKind => Status.Occurrence.SourceWindow!.Policy.Kind;

    /// <summary>The final product of a produced final evaluation; rollups and segments are lineage only.</summary>
    public NightlyProductSummary? FinalProduct => Status.Scope == NightlyProductScope.Final
        ? CurrentProducts.SingleOrDefault(static product => product.Scope == NightlyProductScope.Final)
        : null;
}

/// <summary>Every recorded evaluation and every published product of one report date.</summary>
public sealed record NightlyProductDay(
    DateOnly ObservingDate,
    IReadOnlyList<NightlyProductWindowRecord> Windows,
    IReadOnlyList<NightlyProductSummary> Products)
{
    /// <summary>The final evaluation of the whole sunrise period, chosen as <see cref="NightlyProductDateSummary"/> chooses it.</summary>
    public NightlyProductWindowRecord? Daily(NightlyProductKind kind)
        => Preferred(Finals(kind, LocalAutomationSourceWindowKind.SunriseDay));

    /// <summary>One final evaluation per completed civil hour, in hour order, chosen as the date summary chooses it.</summary>
    public IReadOnlyList<NightlyProductWindowRecord> Hours(NightlyProductKind kind)
        => [.. Finals(kind, LocalAutomationSourceWindowKind.CompletedCivilHour)
            .GroupBy(static window => window.Status.WindowStartUtc)
            .OrderBy(static hour => hour.Key)
            .Select(static hour => Preferred(hour)!)];

    private IEnumerable<NightlyProductWindowRecord> Finals(NightlyProductKind kind, LocalAutomationSourceWindowKind windowKind)
        => Windows.Where(window => window.Status.Kind == kind && window.Status.Scope == NightlyProductScope.Final &&
            window.WindowKind == windowKind);

    // A produced evaluation first, then the latest evaluated; the product ID only makes the choice stable.
    private static NightlyProductWindowRecord? Preferred(IEnumerable<NightlyProductWindowRecord> windows) => windows
        .OrderByDescending(static window => window.FinalProduct is not null)
        .ThenByDescending(static window => window.Status.EvaluatedUtc)
        .ThenBy(static window => window.FinalProduct?.ProductId)
        .FirstOrDefault();
}

/// <summary>
/// The time axis of a keogram as it was composed. A planned axis has fixed UTC bins over the whole retained window; an
/// actual axis has one column per frame plus rendered gap columns, with the column of every frame.
/// </summary>
public sealed record NightlyProductTimeAxis(
    bool Planned,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    int Width,
    double ColumnSeconds,
    int SampledColumns,
    IReadOnlyList<KeogramGap> Gaps,
    IReadOnlyList<KeogramSegmentFrameV1> Frames);

/// <summary>
/// A product with the retained facts a Product Detail page presents: its algorithms, the preview frames of its full
/// lineage, its keogram time axis, and other published outputs recorded for the same window and part.
/// </summary>
public sealed record NightlyProductPresentation(
    NightlyProductDetail Detail,
    IReadOnlyList<ProcessingAlgorithmIdentity> Algorithms,
    int LineageFrameCount,
    NightlyProductTimeAxis? TimeAxis,
    IReadOnlyList<NightlyProductSummary> OtherOutputs);
