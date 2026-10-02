using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Security;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class ProductDetail : ComponentBase, IAsyncDisposable
{
    private CancellationTokenSource? _loadCancellation;
    private CameraAgentProductDetail? _detail;
    private string? _errorMessage;
    private bool _isLoading = true;
    private bool _redirecting;
    private bool _notFound;
    private bool _mediaFailed;
    private bool _nativeSize;
    private bool _fullscreen;
    private bool _disposed;
    private long _generation;
    private ElementReference _viewer;
    private ElementReference _fullscreenTrigger;
    private IJSObjectReference? _module;
    private DotNetObjectReference<ProductDetail>? _self;
    private Guid? _boundArtifact;
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "The managed semaphore remains valid for render callbacks already queued during asynchronous disposal.")]
    private readonly SemaphoreSlim _interopGate = new(1, 1);

    [Inject] internal ICameraAgentOperatorUiService OperatorService { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;
    [Inject] internal IJSRuntime JSRuntime { get; set; } = default!;
    [Parameter] public Guid ArtifactId { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "returnUrl")]
    [SuppressMessage("Design", "CA1056:Uri properties should not be strings", Justification = "Query input is restricted to an application-local product library return URL.")]
    public string? ReturnUrl { get; set; }

    private string BackUrl
    {
        get
        {
            var value = ReturnUrlHelper.NormalizeReturnUrl(ReturnUrl);
            return value == "/archive/products" || value.StartsWith("/archive/products?", StringComparison.Ordinal)
                ? value : "/archive/products";
        }
    }

    private string CurrentUrl => $"/archive/products/{ArtifactId:D}?returnUrl={Uri.EscapeDataString(BackUrl)}";
    private string? ContentUrl => _detail is null ? null : $"/api/v1/operations/artifacts/{_detail.Product.ArtifactId:D}/content";
    private string ProductType => _detail?.Product is not { } product ? "Retained output" : product.IsMaterialization ? "Saved layer stack"
        : product.Recipe.Name switch { "keogram" => "Keogram", "star-trail" => "Star trail", "time-lapse" => "Time-lapse", _ => "Retained output" };
    private string ProductTitle => _detail is null ? "Product detail" : $"{OperationsPage.SplitWords(_detail.Product.Role.ToString())}{(_detail.Product.Variant is { } variant ? $" · {variant}" : "")}";
    private bool IsKeogram => _detail?.Product.Recipe.Name == "keogram";
    private bool GenerationRunning => _detail?.Node?.Status is "Running" or "Pending" or "RetryableFailure";
    private string GenerationLabel => _detail?.Node is not { } node ? "Generation state not retained" : node.Status;
    private string ZoneLabel => _detail?.ObservingDay is { } day ? day.TimeZoneFallback ? "UTC fallback" : day.TimeZoneId : "UTC";
    private string DayUrl => _detail is null ? "/archive/calendar" : $"/archive/day/{_detail.ObservingDay.Date:yyyy-MM-dd}";
    private bool IsVideo => _detail?.Product.MediaType is "video/mp4" or "video/webm";
    private bool UsesOriginalImage => _detail?.Product.MediaType is "image/jpeg" or "image/png" or "image/webp";
    private string? MediaUrl
    {
        get
        {
            if (_detail?.Product is not { Availability: "Available" } product) return null;
            if (UsesOriginalImage || IsVideo) return $"{ContentUrl}?inline=true";
            return product.Role is FrameArtifactRole.Calibrated or FrameArtifactRole.Combined or FrameArtifactRole.Preview or FrameArtifactRole.AnnotatedPreview &&
                product.MediaType is "application/x-hvo-packed-image" or "application/x-hvo-linear-frame"
                ? $"/api/v1/operations/artifacts/{product.ArtifactId:D}/preview" : null;
        }
    }
    private string MediaCaption => GenerationRunning ? "Exact retained working output; generation in progress"
        : IsKeogram ? "Exact keogram pixels / axis metadata unavailable" : $"Exact artifact / {ProductType}";
    private string MediaUnavailableReason => _mediaFailed ? "The selected artifact could not be loaded or decoded. Its evidence remains available."
        : _detail?.Product is { Availability: not "Available" } product ? product.AvailabilityReason ?? $"Selected content is {product.Availability}."
        : "This retained media type has no supported inline display. Download the original when available.";
    private string EvidenceDescription => _detail?.Product.IsMaterialization == true ? "A saved presentation tied to this capture's retained layered evidence."
        : ProductType == "Retained output" ? "A retained processing artifact; it is not automatically a nightly derivative." : "A product identified by its recorded recipe. Unknown provenance stays explicitly unavailable.";

    private string FormatTime(DateTimeOffset instant)
    {
        var zone = TimeZoneInfo.Utc;
        if (_detail?.ObservingDay is { TimeZoneFallback: false } day)
        {
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(day.TimeZoneId); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.ConvertTime(instant, zone).ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
    }

    protected override Task OnParametersSetAsync() => _redirecting || _disposed ? Task.CompletedTask : LoadAsync();
    private async Task LoadAsync()
    {
        var generation = Interlocked.Increment(ref _generation);
        var requestedArtifact = ArtifactId;
        var cancellation = new CancellationTokenSource();
        var prior = Interlocked.Exchange(ref _loadCancellation, cancellation);
        if (prior is not null) { await prior.CancelAsync(); prior.Dispose(); }
        await _interopGate.WaitAsync();
        try
        {
            if (_module is not null && _boundArtifact is not null)
            {
                try { await _module.InvokeVoidAsync("dispose", _viewer); }
                catch (JSDisconnectedException) { }
                catch (JSException) { }
            }
            _boundArtifact = null;
        }
        finally { _interopGate.Release(); }
        if (_disposed || generation != Volatile.Read(ref _generation)) return;
        _isLoading = true;
        _errorMessage = null;
        _detail = null;
        _redirecting = false;
        _notFound = false;
        _mediaFailed = false;
        _nativeSize = false;
        _fullscreen = false;
        try
        {
            var result = await OperatorService.GetProductDetailAsync(requestedArtifact, cancellation.Token);
            if (_disposed || generation != Volatile.Read(ref _generation) || requestedArtifact != ArtifactId || cancellation.IsCancellationRequested) return;
            if (result.Kind == OperatorUiResultKind.Unauthorized) { _redirecting = true; NavigationManager.NavigateTo("/Account/AccessDenied"); }
            else if (result.IsSuccess && result.Value?.Product.ArtifactId == requestedArtifact) _detail = result.Value;
            else { _notFound = result.Kind == OperatorUiResultKind.NotFound; _errorMessage = result.Message ?? "The exact requested artifact is unavailable."; }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { if (!_disposed && generation == Volatile.Read(ref _generation)) _isLoading = false; }
    }

    private void MarkMediaFailed(Guid artifactId)
    {
        if (!_disposed && artifactId == ArtifactId && artifactId == _detail?.Product.ArtifactId) _mediaFailed = true;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        var artifact = _detail?.Product.ArtifactId;
        if (_disposed || MediaUrl is null || _mediaFailed || artifact == _boundArtifact) return;
        await _interopGate.WaitAsync();
        try
        {
            if (_disposed || artifact != _detail?.Product.ArtifactId || artifact == _boundArtifact) return;
            _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/ProductDetail.razor.js");
            if (_disposed || artifact != _detail?.Product.ArtifactId) return;
            _self ??= DotNetObjectReference.Create(this);
            await _module.InvokeVoidAsync("bind", _viewer, _fullscreenTrigger, artifact!.Value.ToString("D"), _self);
            if (!_disposed && artifact == _detail?.Product.ArtifactId) _boundArtifact = artifact;
        }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
        catch (TaskCanceledException) { }
        finally { _interopGate.Release(); }
    }

    private async Task ToggleFullscreenAsync()
    {
        if (_module is null || _mediaFailed || _boundArtifact != ArtifactId) return;
        try { await _module.InvokeVoidAsync("toggle", _viewer); }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
    }

    [JSInvokable]
    public Task FullscreenChanged(string artifactId, bool fullscreen)
    {
        if (!_disposed && Guid.TryParse(artifactId, out var id) && id == ArtifactId && id == _detail?.Product.ArtifactId)
        { _fullscreen = fullscreen; return InvokeAsync(StateHasChanged); }
        return Task.CompletedTask;
    }

    private static string FormatIntegration(TimeSpan value) => value.TotalSeconds >= 1
        ? FormattableString.Invariant($"{value.TotalSeconds:0.###} s") : FormattableString.Invariant($"{value.TotalMilliseconds:0.#} ms");

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        Interlocked.Increment(ref _generation);
        var cancellation = Interlocked.Exchange(ref _loadCancellation, null);
        if (cancellation is not null) { await cancellation.CancelAsync().ConfigureAwait(false); cancellation.Dispose(); }
        await _interopGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_module is not null) { await _module.InvokeVoidAsync("dispose", _viewer); await _module.DisposeAsync(); }
        }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
        finally { _self?.Dispose(); _interopGate.Release(); }
    }
}
