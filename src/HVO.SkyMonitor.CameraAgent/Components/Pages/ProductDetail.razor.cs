using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Security;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class ProductDetail : ComponentBase, IAsyncDisposable
{
    private CancellationTokenSource? _loadCancellation;
    private CameraAgentProductDetail? _detail;
    private string? _errorMessage;
    private bool _isLoading = true;
    private bool _redirecting;
    private long _generation;

    [Inject] internal ICameraAgentOperatorUiService OperatorService { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;
    [Parameter] public Guid ArtifactId { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "returnUrl")]
    [SuppressMessage("Design", "CA1056:Uri properties should not be strings", Justification = "The query value is validated as an application-local return URL before use.")]
    public string? ReturnUrl { get; set; }

    private string BackUrl
    {
        get
        {
            var value = ReturnUrlHelper.NormalizeReturnUrl(ReturnUrl);
            return string.Equals(value, "/archive/products", StringComparison.Ordinal) ||
                value.StartsWith("/archive/products?", StringComparison.Ordinal)
                    ? value
                    : "/archive/products";
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
            var result = await OperatorService.GetProductDetailAsync(ArtifactId, cancellation.Token);
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                _redirecting = true;
                NavigationManager.NavigateTo("/Account/AccessDenied");
            }
            else if (result.IsSuccess && result.Value is not null)
            {
                _detail = result.Value;
            }
            else
            {
                _errorMessage = result.Message ?? "The product is unavailable.";
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

    private static string FormatIntegration(TimeSpan value) => value.TotalSeconds >= 1
        ? FormattableString.Invariant($"{value.TotalSeconds:0.###} s")
        : FormattableString.Invariant($"{value.TotalMilliseconds:0.#} ms");

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
