using HVO.SkyMonitor.Astronomy;
using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Imaging;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

/// <summary>
/// One published nightly product by its exact identity (#1138). It presents only retained facts: the verified preview,
/// the keogram axis and gaps as composed, the recorded lineage, the other outputs recorded for the same window and part,
/// and the checksums and provenance. It offers no regeneration, and it does not claim any output replaced another.
/// </summary>
public sealed partial class NightlyProductDetailPage : ComponentBase, IAsyncDisposable
{
    /// <summary>The most ordered sources listed; the provenance document carries the rest.</summary>
    internal const int MaximumShownSources = 60;

    /// <summary>The most keogram gaps listed as text; the axis draws every one.</summary>
    internal const int MaximumListedGaps = 24;

    /// <summary>The most labels one keogram axis carries, so they stay legible at phone width.</summary>
    internal const int MaximumTicks = 8;

    private static readonly int[] TickStepMinutes = [1, 2, 5, 10, 15, 30, 60, 120, 180, 240, 360];

    internal sealed record AxisTick(double Percent, string Label);

    private CancellationTokenSource? _loadCancellation;
    private NightlyProductPresentation? _presentation;
    private string? _errorMessage;
    private bool _notFound;
    private bool _isLoading = true;
    private bool _previewFailed;
    private long _generation;

    [Inject] internal ICameraAgentNightlyProductUiService NightlyProducts { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;
    [Inject] internal TimeProvider Clock { get; set; } = default!;
    [Parameter] public Guid ProductId { get; set; }

    private NightlyProductDetail? Detail => _presentation?.Detail;

    private SunriseReportingPeriod? Period => Detail?.Occurrence.SourceWindow?.ReportingPeriod;

    private string TimeZoneId => Period?.Site.TimeZoneId ?? TimeZoneInfo.Utc.Id;

    private string PageTitleText => Detail is { } detail ? $"{Title(detail)} {detail.Summary.ObservingDate:yyyy-MM-dd}" : "Nightly product";

    private string DayUrl => Detail is { } detail
        ? ArchiveCalendarPage.DayUrl(detail.Summary.ObservingDate) + "?calendar=" + Uri.EscapeDataString(SunriseReportingPeriod.CurrentVersion)
        : "/archive/calendar?calendar=" + Uri.EscapeDataString(SunriseReportingPeriod.CurrentVersion);

    /// <summary>What part of the night a product covers, from its scope and the retained window kind.</summary>
    internal static string Scope(NightlyProductDetail detail) => (detail.Summary.Scope, detail.Occurrence.SourceWindow?.Policy.Kind) switch
    {
        (NightlyProductScope.Final, LocalAutomationSourceWindowKind.SunriseDay) => "Nightly",
        (NightlyProductScope.Final, LocalAutomationSourceWindowKind.CompletedCivilHour) => "Hourly",
        (NightlyProductScope.Final, _) => "Final",
        (NightlyProductScope.Rollup, _) => "Rollup",
        _ => "Segment"
    };

    internal static string Title(NightlyProductDetail detail) => detail.Summary.Scope == NightlyProductScope.Final
        ? $"{Scope(detail)} {NightlyProductLinks.KindNoun(detail.Summary.Kind)}"
        : FormattableString.Invariant(
            $"{NightlyProductLinks.KindLabel(detail.Summary.Kind)} {(detail.Summary.Scope == NightlyProductScope.Rollup ? "rollup" : "segment")} part {detail.Summary.PartOrdinal + 1}");

    private static string ScopeDescription(NightlyProductDetail detail) => Scope(detail) switch
    {
        "Nightly" => "Final product of the whole sunrise period",
        "Hourly" => "Final product of one completed civil hour",
        "Rollup" => "Intermediate rollup; lineage of a final product, not a final product itself",
        "Segment" => "Ordered segment composed directly from preview frames; lineage of a final product",
        _ => "Final product of its recorded window"
    };

    private bool PeriodOpen => Period is { } period && Clock.GetUtcNow() < period.EndUtc;

    private string Finality(NightlyProductDetail detail) => detail.Summary.Scope != NightlyProductScope.Final
        ? "Lineage of a final product; not shown on the calendar"
        : PeriodOpen
            ? "Final for its window; the sunrise period is still open, so later hours may still be evaluated"
            : "Final; the sunrise period has closed";

    /// <summary>
    /// Whether a current pointer names this product. A product without one is only "not current": a later evaluation
    /// can record no product, and a publication can precede its evaluation, so another current output is claimed only
    /// when one is listed, and no succession between them is ever claimed.
    /// </summary>
    internal static string Currency(NightlyProductPresentation presentation) => presentation.Detail.Summary.IsCurrent
        ? "Current; a recorded evaluation names it as its product of this window and part."
        : presentation.OtherOutputs.Any(static other => other.IsCurrent)
            ? "Not current; another output of this window and part is current, listed below. No succession between them is recorded."
            : "Not current; no recorded evaluation names it, and no other output of this window and part is current.";

    private static string Selection(LocalAutomationSourceSelection selection) => selection switch
    {
        LocalAutomationSourceSelection.DarkNightActualSources => "Dark-night frames only (Sun at or below −18°)",
        _ => "Every retained frame in the window"
    };

    // A civil hour cut by sunrise is planned as a shorter window, and the product covers only that part.
    private static bool PartialHour(NightlyProductDetail detail) =>
        detail.Occurrence.SourceWindow is { Policy.Kind: LocalAutomationSourceWindowKind.CompletedCivilHour } window &&
        window.EndUtc - window.StartUtc < TimeSpan.FromHours(1);

    private string GapLabel(KeogramGap gap) => ObservingDayPage.LocalSpan(gap.StartUtc, gap.EndUtc, TimeZoneId) + " " +
        FormattableString.Invariant($"({gap.ColumnCount:N0} {(gap.ColumnCount == 1 ? "column" : "columns")})");

    private static string Algorithms(NightlyProductPresentation presentation) => presentation.Algorithms.Count == 0
        ? "None recorded"
        : string.Join(", ", presentation.Algorithms.Select(static algorithm => $"{algorithm.Name} {algorithm.Version}"));

    private string LocalTime(DateTimeOffset utc) => Local(utc).ToString("HH:mm", CultureInfo.InvariantCulture);

    private string LocalDateTime(DateTimeOffset utc) => Local(utc).ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    private DateTimeOffset Local(DateTimeOffset utc)
    {
        try
        {
            return TimeZoneInfo.ConvertTimeBySystemTimeZoneId(utc, TimeZoneId);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return utc;
        }
    }

    private string Span(DateTimeOffset startUtc, DateTimeOffset endUtc) => $"{LocalDateTime(startUtc)} – {LocalDateTime(endUtc)}";

    // The frame reserves the encoded aspect so the axis below it shares the image's width exactly.
    private static string FrameStyle(NightlyProductSummary product) => FormattableString.Invariant(
        $"--nightly-aspect: {product.Width} / {product.Height}; --nightly-width-per-height: {(double)product.Width / product.Height:0.####}");

    /// <summary>
    /// Axis labels that never claim a time the image does not encode. A planned axis maps time linearly onto its fixed
    /// UTC bins, so labels sit at round local times placed exactly; the last bin may be partial, so the drawn width spans
    /// <c>Width × ColumnSeconds</c>. An actual axis has no time between frames, so labels sit on frame columns alone.
    /// </summary>
    internal static IReadOnlyList<AxisTick> AxisTicks(NightlyProductTimeAxis axis, Func<DateTimeOffset, DateTimeOffset> toLocal)
    {
        var showOffset = toLocal(axis.StartUtc).Offset != toLocal(axis.EndUtc).Offset;
        if (axis.Planned)
        {
            var drawnSeconds = axis.Width * axis.ColumnSeconds;
            var span = axis.EndUtc - axis.StartUtc;
            var minutes = Array.Find(TickStepMinutes, candidate => span.TotalMinutes / candidate <= MaximumTicks);
            var step = TimeSpan.FromMinutes(minutes > 0 ? minutes : TickStepMinutes[^1]);
            var sinceMidnight = toLocal(axis.StartUtc).TimeOfDay;
            var rounded = TimeSpan.FromTicks((sinceMidnight.Ticks + step.Ticks - 1) / step.Ticks * step.Ticks);
            var ticks = new List<AxisTick>();
            for (var tick = axis.StartUtc + (rounded - sinceMidnight); tick <= axis.EndUtc; tick += step)
            {
                ticks.Add(new AxisTick(100.0 * (tick - axis.StartUtc).TotalSeconds / drawnSeconds, TickLabel(toLocal(tick), showOffset)));
            }
            return ticks;
        }
        if (axis.Frames.Count == 0)
        {
            return [];
        }
        return [.. Enumerable.Range(0, MaximumTicks)
            .Select(index => (axis.Frames.Count - 1) * index / (MaximumTicks - 1))
            .Distinct()
            .Select(index => axis.Frames[index])
            .Select(frame => new AxisTick(100.0 * (frame.Column + 0.5) / axis.Width, TickLabel(toLocal(frame.ObservationStartedUtc), showOffset)))];
    }

    private static string TickLabel(DateTimeOffset local, bool showOffset) => local.ToString(showOffset ? "HH:mm zzz" : "HH:mm", CultureInfo.InvariantCulture);

    // Labels near an edge align inward so they stay inside the image's width.
    private static string TickClass(AxisTick tick) => tick.Percent switch
    {
        < 6 => "keogram-axis__tick keogram-axis__tick--start",
        > 94 => "keogram-axis__tick keogram-axis__tick--end",
        _ => "keogram-axis__tick"
    };

    internal static string GapStyle(KeogramGap gap, int width) => FormattableString.Invariant(
        $"left: {100.0 * gap.FirstColumn / width:0.###}%; width: {Math.Max(0.2, 100.0 * gap.ColumnCount / width):0.###}%");

    private string AxisSummary(NightlyProductTimeAxis axis) => axis.Planned
        ? FormattableString.Invariant($"Planned axis: {FormatColumn(axis.ColumnSeconds)} per column from ") +
          AxisSpan(axis.StartUtc, axis.EndUtc) + "; " +
          FormattableString.Invariant($"{axis.SampledColumns:N0} of {axis.Width:N0} columns hold frames, {axis.Gaps.Count:N0} {(axis.Gaps.Count == 1 ? "gap" : "gaps")}.")
        : FormattableString.Invariant(
            $"Actual axis: one column per frame at a {FormatColumn(axis.ColumnSeconds)} cadence, {axis.Frames.Count:N0} frames from ") +
          AxisSpan(axis.StartUtc, axis.EndUtc) + "; " +
          FormattableString.Invariant($"{axis.Gaps.Count:N0} rendered {(axis.Gaps.Count == 1 ? "gap" : "gaps")}.") +
          (axis.Gaps.Count > 0 ? " Time is not linear across gap columns." : "");

    // A sunrise period spans two local dates, and a bare clock time would read as a one-minute axis.
    private string AxisSpan(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        var start = Local(startUtc);
        var end = Local(endUtc);
        if (start.Offset != end.Offset)
        {
            return ObservingDayPage.LocalSpan(startUtc, endUtc, TimeZoneId).Replace("–", " to ", StringComparison.Ordinal);
        }
        return start.Date == end.Date
            ? $"{LocalTime(startUtc)} to {LocalTime(endUtc)}"
            : string.Create(CultureInfo.InvariantCulture, $"{start:HH:mm dd MMM} to {end:HH:mm dd MMM}");
    }

    internal static string FormatColumn(double seconds) => seconds >= 60 && seconds % 60 == 0
        ? FormattableString.Invariant($"{seconds / 60:0} min")
        : FormattableString.Invariant($"{seconds:0.##} s");

    // Daylight exposures last milliseconds, so a short total keeps its precision instead of rounding to zero.
    private static string FormatIntegration(TimeSpan value) => value.TotalSeconds switch
    {
        < 1 => FormattableString.Invariant($"{value.TotalMilliseconds:0.#} ms"),
        < 60 => FormattableString.Invariant($"{value.TotalSeconds:0.###} s"),
        _ => ObservingDayPage.FormatDuration(value)
    };

    private static string FormatBytes(long bytes) => FormattableString.Invariant($"{bytes:N0} bytes");

    protected override Task OnParametersSetAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        var generation = Interlocked.Increment(ref _generation);
        var cancellation = new CancellationTokenSource();
        var prior = Interlocked.Exchange(ref _loadCancellation, cancellation);
        if (prior is not null)
        {
            await prior.CancelAsync();
            prior.Dispose();
        }
        _isLoading = true;
        _errorMessage = null;
        _notFound = false;
        _previewFailed = false;
        try
        {
            var result = await NightlyProducts.GetPresentationAsync(ProductId, cancellation.Token);
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                _presentation = null;
                NavigationManager.NavigateTo("/Account/AccessDenied");
            }
            else if (result.IsSuccess && result.Value is not null)
            {
                _presentation = result.Value;
            }
            else
            {
                _presentation = null;
                _notFound = result.Kind == OperatorUiResultKind.NotFound;
                _errorMessage = result.Message ?? "The nightly product is unavailable.";
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (generation == Volatile.Read(ref _generation))
            {
                _isLoading = false;
            }
        }
    }

    private void MarkPreviewFailed() => _previewFailed = true;

    public async ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _generation);
        var cancellation = Interlocked.Exchange(ref _loadCancellation, null);
        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            cancellation.Dispose();
        }
    }
}
