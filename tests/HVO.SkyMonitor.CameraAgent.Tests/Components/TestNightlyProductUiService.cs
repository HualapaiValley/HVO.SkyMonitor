using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

/// <summary>A nightly product read model whose answers each test supplies; by default nothing is recorded.</summary>
internal sealed class TestNightlyProductUiService : ICameraAgentNightlyProductUiService
{
    internal List<(DateOnly First, DateOnly Last)> Summaries { get; } = [];

    internal List<DateOnly> Days { get; } = [];

    internal Func<DateOnly, DateOnly, OperatorUiResult<IReadOnlyList<NightlyProductDateSummary>>> SummaryHandler { get; set; } =
        static (_, _) => OperatorUiResult<IReadOnlyList<NightlyProductDateSummary>>.Success([]);

    internal Func<DateOnly, OperatorUiResult<CameraAgentNightlyDayView>> DayHandler { get; set; } =
        static date => OperatorUiResult<CameraAgentNightlyDayView>.Success(new(new NightlyProductDay(date, [], []), null));

    internal Func<Guid, OperatorUiResult<NightlyProductPresentation>> PresentationHandler { get; set; } =
        static _ => OperatorUiResult<NightlyProductPresentation>.Failure(OperatorUiResultKind.NotFound, "The requested nightly product was not found.");

    public ValueTask<OperatorUiResult<IReadOnlyList<NightlyProductDateSummary>>> SummarizeAsync(
        DateOnly firstDate, DateOnly lastDate, CancellationToken cancellationToken)
    {
        Summaries.Add((firstDate, lastDate));
        return ValueTask.FromResult(SummaryHandler(firstDate, lastDate));
    }

    public ValueTask<OperatorUiResult<CameraAgentNightlyDayView>> GetDayAsync(DateOnly observingDate, CancellationToken cancellationToken)
    {
        Days.Add(observingDate);
        return ValueTask.FromResult(DayHandler(observingDate));
    }

    public ValueTask<OperatorUiResult<NightlyProductPresentation>> GetPresentationAsync(Guid productId, CancellationToken cancellationToken)
        => ValueTask.FromResult(PresentationHandler(productId));
}

/// <summary>Recorded evaluations of the nightly fixture's report date, built from real planned occurrences.</summary>
internal static class NightlyDayFixture
{
    internal static DateOnly Date => NightlyProductFixture.ObservingDate;

    internal static readonly DateTimeOffset FirstHourUtc = new(2026, 10, 2, 4, 0, 0, TimeSpan.Zero);

    internal static LocalAutomationOccurrence Daily(NightlyProductKind kind) => NightlyProductFixture.Occurrence(kind);

    internal static LocalAutomationOccurrence Hour(NightlyProductKind kind, DateTimeOffset startUtc) => NightlyProductFixture.Occurrence(
        kind, windowKind: LocalAutomationSourceWindowKind.CompletedCivilHour, hourStart: startUtc);

    internal static NightlyProductSummary Product(LocalAutomationOccurrence occurrence, NightlyProductKind kind, int sources = 12)
    {
        var window = occurrence.SourceWindow!;
        return new NightlyProductSummary(Guid.NewGuid(), kind, NightlyProductScope.Final, Date, window.StartUtc, window.EndUtc, 0,
            true, 640, 480, CameraPixelFormat.Mono8, sources, window.StartUtc.AddMinutes(4), window.EndUtc.AddMinutes(-4),
            TimeSpan.FromMinutes(sources), window.EndUtc.AddMinutes(10));
    }

    internal static NightlyProductWindowRecord Produced(LocalAutomationOccurrence occurrence, NightlyProductSummary product,
        int candidates, int admitted) =>
        Record(occurrence, product.Kind, NightlyProductWindowDisposition.Produced, null, candidates, admitted, [product]);

    internal static NightlyProductWindowRecord Without(LocalAutomationOccurrence occurrence, NightlyProductKind kind,
        NightlyProductWindowDisposition disposition, int candidates, string? reason = null) =>
        Record(occurrence, kind, disposition, reason, candidates, 0, []);

    private static NightlyProductWindowRecord Record(LocalAutomationOccurrence occurrence, NightlyProductKind kind,
        NightlyProductWindowDisposition disposition, string? reason, int candidates, int admitted,
        IReadOnlyList<NightlyProductSummary> products)
    {
        var window = occurrence.SourceWindow!;
        var status = new NightlyProductWindowStatus(kind, NightlyProductScope.Final, Date, window.StartUtc, window.EndUtc,
            disposition, reason, candidates, admitted, new Dictionary<string, int>(), window.EndUtc.AddMinutes(10))
        { Occurrence = occurrence };
        return new NightlyProductWindowRecord(status, products);
    }
}
