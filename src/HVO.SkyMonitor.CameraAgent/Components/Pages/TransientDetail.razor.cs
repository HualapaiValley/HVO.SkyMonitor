using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using Microsoft.AspNetCore.WebUtilities;
using HVO.SkyMonitor.CameraAgent.Security;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class TransientDetail : ComponentBase, IAsyncDisposable
{
    private CancellationTokenSource? _loadCancellation;
    private CameraAgentEventEvidenceView? _evidence;
    private CameraAgentTransientOperatorDetail? _detail => _evidence?.Detail;
    private ObservingDayCalendar _calendar = ObservingDayCalendar.Create(null);
    private string? _errorMessage;
    private bool _isLoading;
    private long _generation;

    [Inject] internal ICameraAgentEventEvidenceUiService EventEvidence { get; set; } = default!;
    [Inject] internal IObservingDayCalendarProvider ObservingDays { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;
    [Parameter] public Guid CandidateId { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "returnUrl")]
    [SuppressMessage("Design", "CA1056:Uri properties should not be strings",
        Justification = "The query value is validated as an application-local return URL before use.")]
    public string? ReturnUrl { get; set; }

    private string BackUrl
    {
        get
        {
            var value = ReturnUrlHelper.NormalizeReturnUrl(ReturnUrl);
            return string.Equals(value, "/transients", StringComparison.Ordinal) ||
                value.StartsWith("/transients?", StringComparison.Ordinal)
                    ? value
                    : "/transients";
        }
    }

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
        _evidence = null;
        _calendar = ObservingDays.Current;
        try
        {
            var result = await EventEvidence.GetAsync(CandidateId, includeContext: true, cancellation.Token);
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                NavigationManager.NavigateTo("/Account/AccessDenied");
            }
            else if (result.IsSuccess && result.Value is not null && result.Value.Detail.Candidate.CandidateId == CandidateId)
            {
                _evidence = result.Value;
            }
            else
            {
                _errorMessage = result.Message ?? "Transient evidence is unavailable.";
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

    private string TimeZoneLabel => _calendar.TimeZoneFallback ? "UTC (site time zone unavailable)" : _calendar.TimeZoneId;
    private string LocalTime(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, _calendar.TimeZone).ToString("d MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture);
    private static string UtcTime(DateTimeOffset value) => value.UtcDateTime.ToString("d MMM yyyy HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
    private string FormatTime(DateTimeOffset? value) => value is null ? "Not recorded" : $"{LocalTime(value.Value)} ({TimeZoneLabel}) · {UtcTime(value.Value)}";
    private static string FormatGuid(Guid? value) => value?.ToString("D") ?? "Not recorded";
    private static string FormatList(IReadOnlyList<string>? values) => values is null || values.Count == 0 ? "None recorded" : string.Join(", ", values);
    private static string FormatBoolean(bool? value) => value is null ? "Not recorded" : value.Value ? "Yes" : "No";
    private static string Number(double? value, string unit) => value is null || !double.IsFinite(value.Value) ? "Not recorded" : value.Value.ToString("0.##", CultureInfo.InvariantCulture) + " " + unit;
    private string ObservingDayUrl => _evidence is null ? "/archive/calendar" : "/archive/day/" + _calendar.Resolve(_evidence.RecordedUtc).Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private string NearbyUrl
    {
        get
        {
            if (_evidence is null)
            {
                return "/gallery";
            }
            var day = _calendar.Resolve(_evidence.RecordedUtc);
            return QueryHelpers.AddQueryString("/gallery", new Dictionary<string, string?>
            {
                ["from"] = day.StartUtc.ToString("O", CultureInfo.InvariantCulture),
                ["to"] = day.EndUtc.ToString("O", CultureInfo.InvariantCulture)
            });
        }
    }
    private string Offset(CameraAgentEventContextFrame frame) => frame.Source is null || _evidence?.Reference?.Source is not { } center
        ? "Unavailable" : (frame.Source.ObservationStartedUtc - center.ObservationStartedUtc).TotalSeconds.ToString("+0.###;-0.###;0", CultureInfo.InvariantCulture) + " s";
    private static string RunUrl(Guid id) => FormattableString.Invariant($"/operations/pipeline/executions/{id:D}");
    private static string FormatBounds(CameraAgentTransientGeometrySummary geometry)
        => FormattableString.Invariant($"x={geometry.X:0.##}, y={geometry.Y:0.##}, {geometry.Width:0.##} × {geometry.Height:0.##} px");

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
