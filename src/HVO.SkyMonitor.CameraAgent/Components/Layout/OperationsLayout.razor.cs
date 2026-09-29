using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace HVO.SkyMonitor.CameraAgent.Components.Layout;

/// <summary>
/// Nested layout for every Operations workspace page: the prototype section sidebar, which
/// collapses behind a toggle on narrow viewports, with the page body beside it. The sidebar
/// health chip and section badges come from the same <see cref="OperationsAttention"/> model
/// as the Overview's "Needs attention" panel, so they only ever count reported conditions.
/// </summary>
public sealed partial class OperationsLayout : LayoutComponentBase, IDisposable
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _lifetime = new();
    private string _currentPath = OperationsSectionCatalog.OverviewPath;
    private OperationsAttention? _attention;
    private string? _agentId;
    private string? _moduleType;
    private bool _readFailed;
    private bool _unauthorized;
    private int _readVersion;
    private bool _disposed;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    internal ICameraAgentOperatorUiService OperatorService { get; set; } = default!;

    internal OperationsSection? Current => OperationsSectionCatalog.Resolve(_currentPath);

    private string ScopeText => _agentId is null
        ? "CameraAgent"
        : _moduleType is null ? _agentId : $"{_agentId} / {OperationsPage.SplitWords(_moduleType)}";

    protected override async Task OnInitializedAsync()
    {
        UpdatePath(NavigationManager.Uri);
        NavigationManager.LocationChanged += OnLocationChanged;
        await RefreshAsync();
    }

    /// <summary>
    /// Replaces the sidebar attention state with a read the current page already made, so a
    /// polling page keeps the health chip and badges current without a second read.
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
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        UpdatePath(e.Location);
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
            _agentId = null;
            _moduleType = null;
            _readFailed = false;
            StateHasChanged();
            NavigationManager.NavigateTo("/Account/AccessDenied");
            return;
        }
        else
        {
            _attention = null;
            _readFailed = true;
        }
        StateHasChanged();
    }

    private void Apply(CameraAgentOperationsView view, bool refreshFailed)
    {
        _attention = OperationsAttention.From(view, refreshFailed);
        _readFailed = false;
        var configuration = view.Summary.Configuration.Value;
        _agentId = string.IsNullOrWhiteSpace(configuration.AgentId) ? null : configuration.AgentId;
        _moduleType = string.IsNullOrWhiteSpace(configuration.ModuleType) ? null : configuration.ModuleType;
    }

    private void UpdatePath(string location)
        => _currentPath = new Uri(location).AbsolutePath;

    private static string LinkClass(OperationsSection section, bool current)
        => (current, section.UnavailableReason is null) switch
        {
            (true, true) => "active",
            (true, false) => "active unavailable",
            (false, false) => "unavailable",
            _ => ""
        };

    private static string HealthTitle(OperationsAttention attention) => attention.Items.Count switch
    {
        0 => "No open attention items.",
        1 => "One open attention item.",
        var count => $"{count} open attention items."
    };

    private static string UnavailableId(OperationsSection section)
        => $"operations-unavailable-{section.Slug}";

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
