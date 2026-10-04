using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Services;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

/// <summary>A nightly product read model whose answers each test supplies; by default nothing is recorded.</summary>
internal sealed class TestNightlyProductUiService : ICameraAgentNightlyProductUiService
{
    internal List<(DateOnly First, DateOnly Last)> Summaries { get; } = [];

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
        => ValueTask.FromResult(DayHandler(observingDate));

    public ValueTask<OperatorUiResult<NightlyProductPresentation>> GetPresentationAsync(Guid productId, CancellationToken cancellationToken)
        => ValueTask.FromResult(PresentationHandler(productId));
}
