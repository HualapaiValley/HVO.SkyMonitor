using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Security;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class TransientDetail : ComponentBase, IAsyncDisposable
{
    private CancellationTokenSource? _loadCancellation;
    private CameraAgentTransientOperatorDetail? _detail;
    private string? _errorMessage;
    private bool _isLoading;
    private long _generation;

    [Inject] internal ICameraAgentTransientUiService TransientService { get; set; } = default!;
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
        _detail = null;
        try
        {
            var result = await TransientService.GetCandidateAsync(CandidateId, cancellation.Token);
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                NavigationManager.NavigateTo("/Account/AccessDenied");
            }
            else if (result.IsSuccess && result.Value is not null)
            {
                _detail = result.Value;
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

    private static string FormatTime(DateTimeOffset? value)
        => value is null ? "Unavailable" : TransientPage.FormatTime(value.Value);
    private static string FormatGuid(Guid? value) => value?.ToString("D") ?? "Unavailable";
    private static string FormatList(IReadOnlyList<string>? values)
        => values is null || values.Count == 0 ? "None recorded" : string.Join(", ", values);
    private static string FormatConfidence(int? value)
        => value is null ? "Unavailable" : FormattableString.Invariant($"{value.Value / 10000d:0.##}%");
    private static string FormatBoolean(bool? value) => value is null ? "Unavailable" : value.Value ? "Yes" : "No";
    private static string FormatBounds(CameraAgentTransientGeometrySummary geometry)
        => FormattableString.Invariant($"x={geometry.X:0.##}, y={geometry.Y:0.##}, {geometry.Width:0.##} x {geometry.Height:0.##}");

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
