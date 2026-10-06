using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
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

    /// <summary>The most report dates one library page presents.</summary>
    public const int MaximumLibraryDates = 31;

    /// <summary>
    /// The most report dates one library page examines while a status filter skips dates; a page that stops there says
    /// how far back it searched instead of reading further.
    /// </summary>
    public const int MaximumLibraryScannedDates = MaximumSummarizedDates;
}

/// <summary>
/// The recorded outcome of a whole retained period's daily final, as the archive calendar classifies it: a daily final
/// product, hourly products without one, or neither.
/// </summary>
public enum NightlyProductLibraryState
{
    Produced,
    Partial,
    NotProduced
}

/// <summary>
/// One page of the generated products library: daily final evaluations of whole retained sunrise periods, newest report
/// date first. A single observing day is never paged; otherwise <see cref="Before"/> continues below an earlier page.
/// </summary>
public sealed record NightlyProductLibraryQuery(
    int DateCount,
    NightlyProductKind? Kind = null,
    NightlyProductLibraryState? State = null,
    DateOnly? ObservingDate = null,
    DateOnly? Before = null);

/// <summary>
/// A recorded daily final evaluation of one kind and retained period, with the summary of that period's finals. Hourly
/// products and periods without a daily evaluation belong to the observing day, not the library.
/// </summary>
public sealed record NightlyProductLibraryEntry(
    NightlyProductDateSummary Summary,
    NightlyProductWindowRecord Daily,
    bool OtherPeriodRecorded)
{
    public NightlyProductLibraryState State => Classify(Summary);

    /// <summary>The daily final product, when the evaluation produced one.</summary>
    public NightlyProductSummary? Product => Daily.FinalProduct;

    internal static NightlyProductLibraryState Classify(NightlyProductDateSummary summary) =>
        summary.DailyProductId is not null ? NightlyProductLibraryState.Produced
        : summary.HourlyProduced > 0 ? NightlyProductLibraryState.Partial
        : NightlyProductLibraryState.NotProduced;
}

/// <summary>
/// The entries of one library page and where the next page starts. <see cref="SearchedThrough"/> is set when the page
/// stopped at its scan bound before filling, so older report dates were not examined.
/// </summary>
public sealed record NightlyProductLibraryPage(
    IReadOnlyList<NightlyProductLibraryEntry> Entries,
    DateOnly? NextBefore,
    DateOnly? SearchedThrough);

/// <summary>
/// The recorded final evaluations of one product kind in one retained sunrise period. A daily product is the final of
/// the whole period; hourly products are finals of its completed civil hours. A report date holds one summary per
/// retained period, so a period resolved for another site or time-zone rules is never merged into it. When more than
/// one automation definition or revision evaluated the same span, a produced evaluation is preferred and then the
/// latest evaluated; nothing here claims one replaced another.
/// </summary>
public sealed record NightlyProductDateSummary(
    DateOnly ObservingDate,
    NightlyProductKind Kind,
    string ReportingPeriodSha256,
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

    /// <summary>The retained sunrise period, with its site and time-zone rules, that the evaluation belongs to.</summary>
    public SunriseReportingPeriod ReportingPeriod => Status.Occurrence.SourceWindow!.ReportingPeriod;

    /// <summary>The final product of a produced final evaluation; rollups and segments are lineage only.</summary>
    public NightlyProductSummary? FinalProduct => Status.Scope == NightlyProductScope.Final
        ? CurrentProducts.SingleOrDefault(static product => product.Scope == NightlyProductScope.Final)
        : null;
}

/// <summary>
/// Every recorded evaluation and every published product of one report date. A report date can hold more than one
/// retained sunrise period, such as before and after a site change, so every selection names the period it reads.
/// </summary>
public sealed record NightlyProductDay(
    DateOnly ObservingDate,
    IReadOnlyList<NightlyProductWindowRecord> Windows,
    IReadOnlyList<NightlyProductSummary> Products)
{
    /// <summary>
    /// The final evaluation of the whole retained period, chosen as <see cref="NightlyProductDateSummary"/> chooses it.
    /// </summary>
    public NightlyProductWindowRecord? Daily(NightlyProductKind kind, string reportingPeriodSha256)
        => Preferred(Finals(kind, LocalAutomationSourceWindowKind.SunriseDay, reportingPeriodSha256));

    /// <summary>
    /// One final evaluation per completed civil hour of the retained period, in hour order, chosen as the date summary
    /// chooses it.
    /// </summary>
    public IReadOnlyList<NightlyProductWindowRecord> Hours(NightlyProductKind kind, string reportingPeriodSha256)
        => [.. Finals(kind, LocalAutomationSourceWindowKind.CompletedCivilHour, reportingPeriodSha256)
            .GroupBy(static window => window.Status.WindowStartUtc)
            .OrderBy(static hour => hour.Key)
            .Select(static hour => Preferred(hour)!)];

    /// <summary>
    /// The other retained periods of this report date that hold final evaluations, in period start order. Their
    /// products belong to those periods and are never presented as products of the named one.
    /// </summary>
    public IReadOnlyList<SunriseReportingPeriod> OtherPeriods(string reportingPeriodSha256)
        => [.. Windows.Where(window => window.Status.Scope == NightlyProductScope.Final &&
                !string.Equals(window.ReportingPeriod.IdentitySha256, reportingPeriodSha256, StringComparison.Ordinal))
            .Select(static window => window.ReportingPeriod)
            .DistinctBy(static period => period.IdentitySha256)
            .OrderBy(static period => period.StartUtc)
            .ThenBy(static period => period.IdentitySha256, StringComparer.Ordinal)];

    private IEnumerable<NightlyProductWindowRecord> Finals(
        NightlyProductKind kind,
        LocalAutomationSourceWindowKind windowKind,
        string reportingPeriodSha256)
        => Windows.Where(window => window.Status.Kind == kind && window.Status.Scope == NightlyProductScope.Final &&
            window.WindowKind == windowKind &&
            string.Equals(window.ReportingPeriod.IdentitySha256, reportingPeriodSha256, StringComparison.Ordinal));

    // A produced evaluation first, then the latest evaluated; the product ID only makes the choice stable.
    internal static NightlyProductWindowRecord? Preferred(IEnumerable<NightlyProductWindowRecord> windows) => windows
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
/// lineage, its keogram time axis, and every other published output of the same retained period, window and part,
/// current or not.
/// </summary>
public sealed record NightlyProductPresentation(
    NightlyProductDetail Detail,
    IReadOnlyList<ProcessingAlgorithmIdentity> Algorithms,
    int LineageFrameCount,
    NightlyProductTimeAxis? TimeAxis,
    IReadOnlyList<NightlyProductSummary> OtherOutputs);
