using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.TimeLapses;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>
/// The operator-configured stand-in for a time-lapse that no night has produced yet (#1138). It is never a product:
/// the page labels it as a sample, and nothing counts it.
/// </summary>
internal sealed record CameraAgentTimeLapseSampleView(string Url, string MediaType, int Width, int Height);

/// <summary>A report date's recorded nightly evaluations and products, and the time-lapse sample when configured.</summary>
internal sealed record CameraAgentNightlyDayView(NightlyProductDay Day, CameraAgentTimeLapseSampleView? TimeLapseSample)
{
    internal CameraAgentTimeLapseDay? TimeLapses { get; init; }
    internal bool TimeLapseGenerationEnabled { get; init; }
}

internal sealed record CameraAgentTimeLapsePresentation(CameraAgentTimeLapseProduct Product, bool VerifiedAvailable,
    LocalAutomationSourceWindow? Window = null);

/// <summary>Authorized, sanitized reads of the nightly product catalog for the archive pages.</summary>
internal interface ICameraAgentNightlyProductUiService
{
    ValueTask<OperatorUiResult<IReadOnlyList<NightlyProductDateSummary>>> SummarizeAsync(
        DateOnly firstDate, DateOnly lastDate, CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<CameraAgentNightlyDayView>> GetDayAsync(DateOnly observingDate, CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<NightlyProductPresentation>> GetPresentationAsync(Guid productId, CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<NightlyProductLibraryPage>> ListLibraryAsync(
        NightlyProductLibraryQuery query, CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<CameraAgentTimeLapsePresentation>> GetTimeLapseAsync(Guid productId, CancellationToken cancellationToken)
        => ValueTask.FromResult(OperatorUiResult<CameraAgentTimeLapsePresentation>.Failure(OperatorUiResultKind.Unavailable, "Time-lapses are unavailable."));

    ValueTask<OperatorUiResult<IReadOnlyList<CameraAgentTimeLapseSummary>>> ListTimeLapsesAsync(DateOnly? before, CancellationToken cancellationToken)
        => ValueTask.FromResult(OperatorUiResult<IReadOnlyList<CameraAgentTimeLapseSummary>>.Success([]));

    ValueTask<OperatorUiResult<bool>> RetryTimeLapseAsync(Guid jobId, long expectedRevision, Guid requestId, CancellationToken cancellationToken)
        => ValueTask.FromResult(OperatorUiResult<bool>.Failure(OperatorUiResultKind.Unavailable, "Video retry is unavailable."));

    ValueTask<OperatorUiResult<IReadOnlyList<CameraAgentTimeLapseDateSummary>>> SummarizeTimeLapsesAsync(DateOnly firstDate, DateOnly lastDate, CancellationToken cancellationToken)
        => ValueTask.FromResult(OperatorUiResult<IReadOnlyList<CameraAgentTimeLapseDateSummary>>.Success([]));
}

internal sealed class CameraAgentNightlyProductUiService(
    AuthenticationStateProvider authenticationStateProvider,
    IAuthorizationService authorizationService,
    INightlyProductCatalog catalog,
    IOptions<CameraAgentHostOptions> options,
    ILogger<CameraAgentNightlyProductUiService> logger,
    ICameraAgentTimeLapseCatalog? timeLapses = null,
    ICameraAgentTimeLapseCommands? videoCommands = null) : ICameraAgentNightlyProductUiService
{
    private const string UnavailableMessage = "Nightly products are temporarily unavailable.";
    private const string SummaryBoundMessage = "The requested date range is outside the nightly product summary bound.";

    public ValueTask<OperatorUiResult<IReadOnlyList<CameraAgentTimeLapseDateSummary>>> SummarizeTimeLapsesAsync(DateOnly firstDate, DateOnly lastDate, CancellationToken cancellationToken)
        => ReadAsync(async () => OperatorUiResult<IReadOnlyList<CameraAgentTimeLapseDateSummary>>.Success(timeLapses is null ? [] :
            await timeLapses.SummarizeAsync(firstDate, lastDate, cancellationToken).ConfigureAwait(false)), cancellationToken);

    public ValueTask<OperatorUiResult<bool>> RetryTimeLapseAsync(Guid jobId, long expectedRevision, Guid requestId, CancellationToken cancellationToken)
        => ReadAsync(async () =>
        {
            var state = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
            var actor = CameraAgentCredentialAccess.GetOwnerId(state.User);
            if (actor is null || !(await authorizationService.AuthorizeAsync(state.User, null,
                    CameraAgentAuthorizationPolicyNames.OperationsMutateV1).ConfigureAwait(false)).Succeeded)
                return OperatorUiResult<bool>.Failure(OperatorUiResultKind.Unauthorized, "Local operator access is required.");
            if (videoCommands is null) return OperatorUiResult<bool>.Failure(OperatorUiResultKind.Unavailable, "Video retry is unavailable.");
            return await videoCommands.RetryAsync(jobId, expectedRevision, requestId, actor, "Retry generation using retained inputs.", cancellationToken).ConfigureAwait(false)
                ? OperatorUiResult<bool>.Success(true)
                : OperatorUiResult<bool>.Failure(OperatorUiResultKind.Invalid, "The job changed, its retry limit was reached, or the queue is full. Refresh this day.");
        }, cancellationToken);

    public ValueTask<OperatorUiResult<IReadOnlyList<NightlyProductDateSummary>>> SummarizeAsync(
        DateOnly firstDate, DateOnly lastDate, CancellationToken cancellationToken)
        => ReadAsync(async () => OperatorUiResult<IReadOnlyList<NightlyProductDateSummary>>.Success(
            await catalog.SummarizeDatesAsync(firstDate, lastDate, cancellationToken).ConfigureAwait(false)), cancellationToken);

    public ValueTask<OperatorUiResult<CameraAgentNightlyDayView>> GetDayAsync(DateOnly observingDate, CancellationToken cancellationToken)
        => ReadAsync(async () => OperatorUiResult<CameraAgentNightlyDayView>.Success(new CameraAgentNightlyDayView(
            await catalog.GetDayAsync(observingDate, cancellationToken).ConfigureAwait(false), TimeLapseSample())
        {
            TimeLapses = timeLapses is null ? null : await timeLapses.GetDayAsync(observingDate, cancellationToken).ConfigureAwait(false),
            TimeLapseGenerationEnabled = options.Value.TimeLapses.Enabled
        }), cancellationToken);

    public ValueTask<OperatorUiResult<CameraAgentTimeLapsePresentation>> GetTimeLapseAsync(Guid productId, CancellationToken cancellationToken)
        => ReadAsync(async () =>
        {
            var product = timeLapses is null ? null : await timeLapses.GetAsync(productId, cancellationToken).ConfigureAwait(false);
            if (product is null || product.IsGapFiller)
                return OperatorUiResult<CameraAgentTimeLapsePresentation>.Failure(OperatorUiResultKind.NotFound, "The requested time-lapse was not found.");
            var window = await timeLapses!.GetWindowAsync(productId, cancellationToken).ConfigureAwait(false);
            try
            {
                using var stream = await timeLapses!.OpenVideoAsync(productId, cancellationToken).ConfigureAwait(false);
                return OperatorUiResult<CameraAgentTimeLapsePresentation>.Success(new(product, stream is not null, window));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                return OperatorUiResult<CameraAgentTimeLapsePresentation>.Success(new(product, false, window));
            }
        }, cancellationToken);

    public ValueTask<OperatorUiResult<IReadOnlyList<CameraAgentTimeLapseSummary>>> ListTimeLapsesAsync(DateOnly? before, CancellationToken cancellationToken)
        => ReadAsync(async () => OperatorUiResult<IReadOnlyList<CameraAgentTimeLapseSummary>>.Success(timeLapses is null ? [] :
            await timeLapses.ListDailyAsync(before, 31, cancellationToken).ConfigureAwait(false)), cancellationToken);

    public ValueTask<OperatorUiResult<NightlyProductPresentation>> GetPresentationAsync(Guid productId, CancellationToken cancellationToken)
        => ReadAsync(async () => await catalog.GetPresentationAsync(productId, cancellationToken).ConfigureAwait(false) is { } presentation
            ? OperatorUiResult<NightlyProductPresentation>.Success(presentation)
            : OperatorUiResult<NightlyProductPresentation>.Failure(OperatorUiResultKind.NotFound, "The requested nightly product was not found."),
            cancellationToken);

    public ValueTask<OperatorUiResult<NightlyProductLibraryPage>> ListLibraryAsync(
        NightlyProductLibraryQuery query, CancellationToken cancellationToken)
        => ReadAsync(async () => OperatorUiResult<NightlyProductLibraryPage>.Success(
            await catalog.ListLibraryAsync(query, cancellationToken).ConfigureAwait(false)), cancellationToken,
            "The requested products page is outside the generated products library bound.");

    // A sample is offered only while its file is present, so an enabled but missing sample reads as absent rather than
    // as a broken player.
    private CameraAgentTimeLapseSampleView? TimeLapseSample()
    {
        var sample = options.Value.TimeLapseSample;
        return sample is { Enabled: true, FilePath: { } path } && File.Exists(path)
            ? new CameraAgentTimeLapseSampleView(CameraAgentTimeLapseSampleEndpoints.Path, sample.MediaType, sample.Width, sample.Height)
            : null;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The operator boundary logs internal failures and returns only fixed, sanitized states.")]
    private async ValueTask<OperatorUiResult<T>> ReadAsync<T>(Func<Task<OperatorUiResult<T>>> read, CancellationToken cancellationToken,
        string invalidMessage = SummaryBoundMessage)
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        if (!(await authorizationService.AuthorizeAsync(state.User, resource: null,
                CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false)).Succeeded)
        {
            return OperatorUiResult<T>.Failure(OperatorUiResultKind.Unauthorized, "Local operator access is required.");
        }
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentOutOfRangeException)
        {
            return OperatorUiResult<T>.Failure(OperatorUiResultKind.Invalid, invalidMessage);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent nightly product read failed.");
            return OperatorUiResult<T>.Failure(OperatorUiResultKind.Unavailable, UnavailableMessage);
        }
    }
}

/// <summary>Stable links to nightly product pages and media.</summary>
internal static class NightlyProductLinks
{
    internal static string Detail(Guid productId) => FormattableString.Invariant($"/archive/products/nightly/{productId:D}");

    internal static string Preview(Guid productId) => FormattableString.Invariant($"/api/v1/operations/still-products/{productId:D}/preview");

    internal static string PreviewDownload(Guid productId) => Preview(productId) + "?download=1";

    internal static string ProvenanceDownload(Guid productId) =>
        FormattableString.Invariant($"/api/v1/operations/still-products/{productId:D}/provenance?download=1");

    internal static string KindLabel(NightlyProductKind kind) => kind switch
    {
        NightlyProductKind.StarTrail => "Star trail",
        NightlyProductKind.Keogram => "Keogram",
        _ => kind.ToString()
    };

    internal static string KindNoun(NightlyProductKind kind) => kind switch
    {
        NightlyProductKind.StarTrail => "star trail",
        NightlyProductKind.Keogram => "keogram",
        _ => kind.ToString()
    };
}
