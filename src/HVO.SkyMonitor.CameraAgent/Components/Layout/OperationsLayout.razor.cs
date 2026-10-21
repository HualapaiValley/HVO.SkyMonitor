using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Components.Layout;

/// <summary>
/// Nested layout for every Operations workspace page: the prototype section sidebar, which
/// collapses behind a toggle on narrow viewports, with the page body beside it. The sidebar
/// health chip and section badges come from the same <see cref="OperationsAttention"/> model
/// as the Overview's "Needs attention" panel, so they only ever count reported conditions.
/// The layout also owns the one space-weather read shared with the Overview; the space-weather
/// service is optional, so hosts without it show no space-weather items.
/// </summary>
public sealed partial class OperationsLayout : LayoutComponentBase, IDisposable
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _lifetime = new();
    private string _currentPath = OperationsSectionCatalog.OverviewPath;
    private OperationsAttention? _attention;
    private string? _displayName;
    private string? _moduleType;
    private bool _haveRead;
    private bool _readFailed;
    private bool _unauthorized;
    private int _readVersion;
    private bool _disposed;
    private ICameraAgentSpaceWeatherUiService? _spaceWeatherService;
    private TimeProvider _clock = TimeProvider.System;
    private CameraAgentOperationsView? _operationsView;
    private bool _operationsRefreshFailed;
    private DateTimeOffset? _spaceWeatherReadUtc;
    private int _spaceWeatherReading;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    internal ICameraAgentOperatorUiService OperatorService { get; set; } = default!;

    [Inject]
    internal IServiceProvider ServiceProvider { get; set; } = default!;

    /// <summary>The latest space-weather projection, or null before the first read or after a failed one.</summary>
    internal SpaceWeatherUiProjection? SpaceWeather { get; private set; }

    /// <summary>The space-weather attention items included in the sidebar and Overview attention model.</summary>
    internal IReadOnlyList<OperationsAttentionItem> SpaceWeatherItems { get; private set; } = [];

    internal OperationsSection? Current => OperationsSectionCatalog.Resolve(_currentPath);

    // The camera's friendly name, never its agent identifier: the identifier is a GUID an operator cannot read.
    private string ScopeText
    {
        get
        {
            if (!_haveRead)
            {
                return "CameraAgent";
            }
            var name = _displayName ?? "This camera";
            return _moduleType is null ? name : $"{name} / {OperationsPage.SplitWords(_moduleType)}";
        }
    }

    protected override async Task OnInitializedAsync()
    {
        _spaceWeatherService = ServiceProvider.GetService<ICameraAgentSpaceWeatherUiService>();
        _clock = ServiceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
        UpdatePath(NavigationManager.Uri);
        NavigationManager.LocationChanged += OnLocationChanged;
        StartSpaceWeatherRead();
        await RefreshAsync();
    }

    /// <summary>
    /// Replaces the sidebar attention state with a read the current page already made, so a
    /// polling page keeps the health chip and badges current without a second read. Space
    /// weather is re-read in the background only once its last read is at least 30 seconds old.
    /// </summary>
    internal void Publish(CameraAgentOperationsView view, bool refreshFailed)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (_unauthorized)
        {
            return;
        }
        Interlocked.Increment(ref _readVersion);
        Apply(view, refreshFailed);
        _ = InvokeAsync(StateHasChanged);
        if (_spaceWeatherReadUtc is not { } readUtc || _clock.GetUtcNow() - readUtc >= SpaceWeatherUiBounds.LayoutRefreshAge)
        {
            StartSpaceWeatherRead();
        }
    }

    /// <summary>Re-reads the sidebar after a page changed something it shows, such as the camera's name.</summary>
    internal void Refresh() => _ = InvokeAsync(RefreshAsync);

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        UpdatePath(e.Location);
        StartSpaceWeatherRead();
        _ = InvokeAsync(async () =>
        {
            StateHasChanged();
            await RefreshAsync();
        });
    }

    private async Task RefreshAsync()
    {
        if (_disposed || _unauthorized)
        {
            return;
        }
        var version = Interlocked.Increment(ref _readVersion);
        OperatorUiResult<CameraAgentOperationsView> result;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(ReadTimeout);
            result = await OperatorService.GetOperationsAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException)
        {
            result = OperatorUiResult<CameraAgentOperationsView>.Failure(OperatorUiResultKind.Unavailable, "Operations read timed out.");
        }
        if (_disposed || version != Volatile.Read(ref _readVersion))
        {
            return;
        }
        if (result is { IsSuccess: true, Value: { } view })
        {
            Apply(view, refreshFailed: false);
        }
        else if (result.Kind == OperatorUiResultKind.Unauthorized)
        {
            // Revoked access: drop everything earlier reads rendered and leave, because a section
            // without a read of its own (an unavailable one, say) would otherwise keep showing it.
            _unauthorized = true;
            _attention = null;
            _operationsView = null;
            SpaceWeather = null;
            SpaceWeatherItems = [];
            _displayName = null;
            _moduleType = null;
            _haveRead = false;
            _readFailed = false;
            StateHasChanged();
            NavigationManager.NavigateTo("/Account/AccessDenied");
            return;
        }
        else
        {
            _attention = null;
            _operationsView = null;
            _readFailed = true;
        }
        StateHasChanged();
    }

    /// <summary>Starts one background space-weather read unless one is already in flight.</summary>
    private void StartSpaceWeatherRead()
    {
        if (_spaceWeatherService is null || _disposed || _unauthorized ||
            Interlocked.CompareExchange(ref _spaceWeatherReading, 1, 0) != 0)
        {
            return;
        }
        _ = InvokeAsync(ReadSpaceWeatherAsync);
    }

    private async Task ReadSpaceWeatherAsync()
    {
        try
        {
            if (_disposed || _unauthorized || _spaceWeatherService is null)
            {
                return;
            }
            OperatorUiResult<SpaceWeatherUiView> result;
            try
            {
                result = await _spaceWeatherService.GetAsync(_lifetime.Token);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            if (_disposed || _unauthorized)
            {
                return;
            }
            _spaceWeatherReadUtc = _clock.GetUtcNow();
            if (result is { IsSuccess: true, Value: { } view })
            {
                SpaceWeather = view.Projection;
                SpaceWeatherItems = SpaceWeatherAttention.From(view.Projection);
            }
            else
            {
                var enabled = SpaceWeather?.Enabled ?? false;
                SpaceWeather = null;
                SpaceWeatherItems = result.Kind == OperatorUiResultKind.Unauthorized ? [] : SpaceWeatherAttention.Unavailable(enabled);
            }
            if (_operationsView is { } operations)
            {
                _attention = OperationsAttention.From(operations, _operationsRefreshFailed, SpaceWeatherItems);
            }
            StateHasChanged();
        }
        finally
        {
            Interlocked.Exchange(ref _spaceWeatherReading, 0);
        }
    }

    private void Apply(CameraAgentOperationsView view, bool refreshFailed)
    {
        _operationsView = view;
        _operationsRefreshFailed = refreshFailed;
        _attention = OperationsAttention.From(view, refreshFailed, SpaceWeatherItems);
        _readFailed = false;
        var configuration = view.Summary.Configuration.Value;
        _displayName = string.IsNullOrWhiteSpace(view.DisplayName) ? null : view.DisplayName;
        _moduleType = string.IsNullOrWhiteSpace(configuration.ModuleType) ? null : configuration.ModuleType;
        _haveRead = true;
    }

    private void UpdatePath(string location)
        => _currentPath = new Uri(location).AbsolutePath;

    private static string HealthTitle(OperationsAttention attention) => attention.Items.Count switch
    {
        0 => "No open attention items.",
        1 => "One open attention item.",
        var count => $"{count} open attention items."
    };

    private static string NoteId(OperationsSection section)
        => $"operations-note-{section.Slug}";

    private static string GroupId(string group)
        => $"operations-group-{group}";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        NavigationManager.LocationChanged -= OnLocationChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
