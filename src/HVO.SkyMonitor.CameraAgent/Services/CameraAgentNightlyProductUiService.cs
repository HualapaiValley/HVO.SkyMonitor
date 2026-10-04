using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Common.Options;
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
internal sealed record CameraAgentNightlyDayView(NightlyProductDay Day, CameraAgentTimeLapseSampleView? TimeLapseSample);

/// <summary>Authorized, sanitized reads of the nightly product catalog for the archive pages.</summary>
internal interface ICameraAgentNightlyProductUiService
{
    ValueTask<OperatorUiResult<IReadOnlyList<NightlyProductDateSummary>>> SummarizeAsync(
        DateOnly firstDate, DateOnly lastDate, CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<CameraAgentNightlyDayView>> GetDayAsync(DateOnly observingDate, CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<NightlyProductPresentation>> GetPresentationAsync(Guid productId, CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<NightlyProductLibraryPage>> ListLibraryAsync(
        NightlyProductLibraryQuery query, CancellationToken cancellationToken);
}

internal sealed class CameraAgentNightlyProductUiService(
    AuthenticationStateProvider authenticationStateProvider,
    IAuthorizationService authorizationService,
    INightlyProductCatalog catalog,
    IOptions<CameraAgentHostOptions> options,
    ILogger<CameraAgentNightlyProductUiService> logger) : ICameraAgentNightlyProductUiService
{
    private const string UnavailableMessage = "Nightly products are temporarily unavailable.";
    private const string SummaryBoundMessage = "The requested date range is outside the nightly product summary bound.";

    public ValueTask<OperatorUiResult<IReadOnlyList<NightlyProductDateSummary>>> SummarizeAsync(
        DateOnly firstDate, DateOnly lastDate, CancellationToken cancellationToken)
        => ReadAsync(async () => OperatorUiResult<IReadOnlyList<NightlyProductDateSummary>>.Success(
            await catalog.SummarizeDatesAsync(firstDate, lastDate, cancellationToken).ConfigureAwait(false)), cancellationToken);

    public ValueTask<OperatorUiResult<CameraAgentNightlyDayView>> GetDayAsync(DateOnly observingDate, CancellationToken cancellationToken)
        => ReadAsync(async () => OperatorUiResult<CameraAgentNightlyDayView>.Success(new CameraAgentNightlyDayView(
            await catalog.GetDayAsync(observingDate, cancellationToken).ConfigureAwait(false), TimeLapseSample())), cancellationToken);

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
