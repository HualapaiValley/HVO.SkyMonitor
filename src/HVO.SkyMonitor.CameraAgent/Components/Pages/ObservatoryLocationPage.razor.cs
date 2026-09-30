using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.SiteProfile;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;
using HVO.SkyMonitor.CameraAgent.Components.Layout;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

/// <summary>
/// The Operations "Observatory &amp; location" section: the active deployment location with an optional
/// browser-loaded map, the local site profile, the LogicHost assignment, the active catalog and rig, and a
/// paged preview of the sky the active rig sees at one instant.
/// </summary>
public sealed partial class ObservatoryLocationPage : ComponentBase, IAsyncDisposable
{
    internal const string LocationTriggerId = "site-create-draft";
    internal const string ProfileTriggerId = "site-edit-profile";
    internal const int ScenePageSize = 25;
    private const int TileSize = 256;
    private const double MercatorLatitudeLimit = 85.0511287798;

    private static readonly int[] s_magnitudeLimits = [1, 2, 3, 4, 5, 6];

    private CameraAgentSkyMapProjectionResult? _state;
    private ManualDeploymentLocationState? _manual;
    private CameraAgentSiteView? _site;
    private NamedRigUiCatalog? _rigCatalog;
    private NamedRigInventory? _rigInventory;
    private DateTime? _instantInput;
    private string? _message;
    private bool _invalid;
    private bool _loading = true;
    private bool _busy;
    private string? _notice;
    private bool _noticeIsError;

    private SiteDialog _dialog;
    private ElementReference _dialogElement;
    private bool _showDialog;
    private bool _restoreFocus;
    private string _restoreFocusId = LocationTriggerId;

    private string _latitudeInput = string.Empty;
    private string _longitudeInput = string.Empty;
    private string _elevationInput = string.Empty;
    private string _timeZoneInput = string.Empty;
    private string _locationReasonInput = string.Empty;
    private bool _locationDirty;
    private string? _locationKey;
    private string? _locationPayload;
    private long _locationExpectedVersion;
    private long _locationExpectedSequence;

    private string _observatoryNameInput = string.Empty;
    private string _cameraNameInput = string.Empty;
    private string _ownerNameInput = string.Empty;
    private string _ownerContactInput = string.Empty;
    private bool _profileDirty;
    private string? _profileExpectedVersion;

    private string _objectLimitInput = string.Empty;
    private bool _objectLimitDirty;
    private string? _objectLimitExpectedVersion;

    private string _search = string.Empty;
    private string _magnitudeFilter = string.Empty;
    private SceneSort _sort = SceneSort.Magnitude;
    private bool _sortDescending;
    private int _scenePage;
    private string? _selectedId;
    private readonly HashSet<string> _figures = new(StringComparer.Ordinal);
    private ElementReference _sceneWrap;
    private int? _revealRow;

    private ElementReference _mapElement;
    private string? _watchedTiles;
    private IJSObjectReference? _module;

    [Inject] internal ICameraAgentSkyMapUiService SkyMapService { get; set; } = default!;

    [Inject] internal ICameraAgentNamedRigUiService RigService { get; set; } = default!;

    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    [CascadingParameter] internal OperationsLayout? Layout { get; set; }

    [Inject] internal IJSRuntime JSRuntime { get; set; } = default!;

    private enum SiteDialog
    {
        None,
        Location,
        Profile
    }

    private enum SceneSort
    {
        Name,
        Magnitude,
        Altitude,
        Azimuth
    }

    /// <summary>
    /// One tile of the mosaic. <see cref="Column"/> is the grid column before wrapping, so it names the position
    /// uniquely even when a low zoom repeats one tile address across the antimeridian.
    /// </summary>
    internal sealed record MapTile(string Source, int Column, int Row, double Left, double Top);

    private sealed record SceneView(
        IReadOnlyList<CameraAgentSkyMapObject> Items,
        IReadOnlySet<string> OnPage,
        int Total,
        int Pages,
        int First,
        int Last);

    private sealed record Banner(string Icon, string Title, string Text);

    private sealed record ConstellationChoice(string Id, bool InImage);

    protected override async Task OnInitializedAsync() => await LoadAsync().ConfigureAwait(false);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_showDialog && _dialog != SiteDialog.None)
        {
            _showDialog = false;
            await InvokeModuleAsync("showModal", _dialogElement).ConfigureAwait(false);
        }
        else if (_restoreFocus && _dialog == SiteDialog.None)
        {
            _restoreFocus = false;
            await InvokeModuleAsync("focusById", _restoreFocusId, "site-heading").ConfigureAwait(false);
        }
        // A selection made on the plot pages the table to the object; this brings its row into view inside the
        // table's own scroll area without moving the page.
        if (_revealRow is { } row)
        {
            _revealRow = null;
            await InvokeModuleAsync("revealRow", _sceneWrap, row).ConfigureAwait(false);
        }
        // Tiles are watched once per rendered set: a failed load flips the map to its offline schematic,
        // and a new location or zoom produces a new set that is given a fresh chance to load. Loading can
        // finish while an earlier render's callback is still running, so a set is only marked watched once
        // the map element it belongs to has been rendered; until then the next render tries again.
        var tiles = TileSignature;
        if (!string.Equals(tiles, _watchedTiles, StringComparison.Ordinal) && (tiles is null || _mapElement.Id is not null))
        {
            _watchedTiles = tiles;
            if (tiles is not null)
            {
                await InvokeModuleAsync("watchTiles", _mapElement).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask InvokeModuleAsync(string identifier, params object?[] arguments)
    {
        _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
            "import", "./Components/Pages/ObservatoryLocationPage.razor.js").ConfigureAwait(false);
        await _module.InvokeVoidAsync(identifier, arguments).ConfigureAwait(false);
    }

    private async Task ProjectAsync()
    {
        _scenePage = 0;
        await LoadAsync().ConfigureAwait(false);
    }

    private async Task UseNowAsync()
    {
        _instantInput = null;
        await ProjectAsync().ConfigureAwait(false);
    }

    private async Task LoadAsync()
    {
        _loading = true;
        _invalid = false;
        try
        {
            var atUtc = _instantInput is { } local
                ? new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Utc))
                : (DateTimeOffset?)null;
            var projection = await SkyMapService.GetSkyMapAsync(atUtc, CancellationToken.None).ConfigureAwait(false);
            var manual = await SkyMapService.GetManualLocationAsync(CancellationToken.None).ConfigureAwait(false);
            var site = await SkyMapService.GetSiteAsync(CancellationToken.None).ConfigureAwait(false);
            var rigCatalog = await RigService.GetAsync(CancellationToken.None).ConfigureAwait(false);
            var rigInventory = await RigService.GetInventoryAsync(CancellationToken.None).ConfigureAwait(false);
            if (projection.Kind == OperatorUiResultKind.Unauthorized
                || manual.Kind == OperatorUiResultKind.Unauthorized
                || site.Kind == OperatorUiResultKind.Unauthorized
                || rigCatalog.Kind == OperatorUiResultKind.Unauthorized
                || rigInventory.Kind == OperatorUiResultKind.Unauthorized)
            {
                NavigationManager.NavigateTo("/Account/AccessDenied");
                return;
            }
            if (projection.IsSuccess && projection.Value is not null)
            {
                _state = projection.Value;
                _message = null;
                KeepSelectionsInScene(projection.Value);
            }
            else
            {
                _state = null;
                _invalid = projection.Kind == OperatorUiResultKind.Invalid;
                _message = projection.Message ?? "The location and projection could not be read.";
            }
            _manual = manual.IsSuccess ? manual.Value : null;
            _site = site.IsSuccess ? site.Value : null;
            _rigCatalog = rigCatalog.IsSuccess ? rigCatalog.Value : null;
            _rigInventory = rigInventory.IsSuccess ? rigInventory.Value : null;
            SeedLocationForm();
            SeedProfileForm();
            SeedObjectLimitForm();
        }
        finally
        {
            _loading = false;
        }
    }

    private void OpenLocationDialog()
    {
        if (_manual is not { Supported: true } || _busy)
        {
            return;
        }
        // A stale outcome must not be announced inside a dialog for a command that has not been issued.
        _notice = null;
        SeedLocationForm();
        _dialog = SiteDialog.Location;
        _showDialog = true;
        _restoreFocusId = LocationTriggerId;
    }

    private void OpenProfileDialog()
    {
        if (_site is null || _busy)
        {
            return;
        }
        _notice = null;
        SeedProfileForm();
        _dialog = SiteDialog.Profile;
        _showDialog = true;
        _restoreFocusId = ProfileTriggerId;
    }

    private void CloseDialog()
    {
        if (_busy)
        {
            return;
        }
        _dialog = SiteDialog.None;
        _notice = null;
        _restoreFocus = true;
    }

    private bool ProfileSet => _site is not null && _site.Profile.Profile != SiteProfileValues.Empty;

    private (string Tone, string Label) SettingsFileState => _site?.Profile switch
    {
        null => ("pending", "Unknown"),
        { SettingsFilePath: null } => ("pending", "Not loaded"),
        { Problem: not null } => ("failure", "Needs correction"),
        _ => ("success", "Active")
    };

    private string DialogTitle => _dialog == SiteDialog.Location ? "Create local location draft" : "Edit site profile";

    /// <summary>Fills the location fields from durable state while the operator has not edited them.</summary>
    private void SeedLocationForm()
    {
        if (_locationDirty || _dialog == SiteDialog.Location)
        {
            return;
        }
        if (_manual?.Override is { } pending)
        {
            _latitudeInput = Number(pending.LatitudeDegrees, 6);
            _longitudeInput = Number(pending.LongitudeDegrees, 6);
            _elevationInput = Number(pending.ElevationMeters, 2);
            _timeZoneInput = pending.TimeZoneId;
            return;
        }
        if (_state?.Observer is { } observer)
        {
            _latitudeInput = Number(observer.LatitudeDegrees, 6);
            _longitudeInput = Number(observer.LongitudeDegrees, 6);
            _elevationInput = Number(observer.ElevationMeters, 2);
            _timeZoneInput = observer.TimeZoneId;
        }
    }

    /// <summary>
    /// Fills the profile fields from the saved profile while the operator has not edited them, and remembers the
    /// settings file version they came from, so a save never overwrites a change made after they were read.
    /// </summary>
    private void SeedProfileForm()
    {
        if (_profileDirty || _dialog == SiteDialog.Profile || _site is null)
        {
            return;
        }
        var profile = _site.Profile.Profile;
        _observatoryNameInput = profile.ObservatoryName ?? string.Empty;
        _cameraNameInput = profile.CameraName ?? string.Empty;
        _ownerNameInput = profile.OwnerName ?? string.Empty;
        _ownerContactInput = profile.OwnerContact ?? string.Empty;
        _profileExpectedVersion = _site.Profile.Version;
    }

    private void SeedObjectLimitForm()
    {
        if (_objectLimitDirty || _site?.ObjectLimit is not { } limit)
        {
            return;
        }
        _objectLimitInput = limit.SavedValue ?? string.Empty;
        _objectLimitExpectedVersion = limit.Version;
    }

    private void ObjectLimitFormChanged()
    {
        _objectLimitDirty = true;
        _notice = null;
    }

    private void LocationFormChanged()
    {
        _locationDirty = true;
        _notice = null;
    }

    private void ProfileFormChanged()
    {
        _profileDirty = true;
        _notice = null;
    }

    private void UseOwnerEmail(string email)
    {
        _ownerContactInput = email;
        ProfileFormChanged();
    }

    private async Task SaveLocationAsync()
    {
        if (_busy || _manual is not { Supported: true } manual)
        {
            return;
        }
        if (!TryParseLocation(out var latitude, out var longitude, out var elevation, out var timeZoneId))
        {
            return;
        }
        var reason = Optional(_locationReasonInput);
        var signature = string.Join(
            '|',
            latitude.ToString("R", CultureInfo.InvariantCulture),
            longitude.ToString("R", CultureInfo.InvariantCulture),
            elevation.ToString("R", CultureInfo.InvariantCulture),
            timeZoneId,
            reason ?? string.Empty);
        // A retry after an unavailable command reuses the same key and expected version so the store
        // recognizes the replay instead of appending a second version.
        if (!string.Equals(_locationPayload, signature, StringComparison.Ordinal))
        {
            _locationPayload = signature;
            _locationKey = NewKey();
            _locationExpectedVersion = manual.KnownVersion;
            _locationExpectedSequence = manual.ManualSequence;
        }
        _busy = true;
        OperatorUiResult<ManualDeploymentLocationResult> result;
        try
        {
            result = await SkyMapService.ApplyManualLocationAsync(
                latitude,
                longitude,
                elevation,
                timeZoneId,
                _locationExpectedVersion,
                _locationExpectedSequence,
                _locationKey!,
                reason,
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _busy = false;
        }
        if (result.Kind != OperatorUiResultKind.Unavailable)
        {
            _locationKey = null;
            _locationPayload = null;
        }
        if (result.Kind == OperatorUiResultKind.Unauthorized)
        {
            NavigationManager.NavigateTo("/Account/AccessDenied");
            return;
        }
        if (result.IsSuccess && result.Value is { } applied)
        {
            _locationDirty = false;
            _locationReasonInput = string.Empty;
            _dialog = SiteDialog.None;
            _restoreFocus = true;
            await LoadAsync().ConfigureAwait(false);
            SetNotice(DescribeLocation(applied), error: false);
            return;
        }
        if (result.Kind == OperatorUiResultKind.Conflict)
        {
            // Re-read durable state so the next attempt carries the current expected version.
            await LoadAsync().ConfigureAwait(false);
        }
        SetNotice(result.Message ?? "The location draft could not be recorded.", error: true);
    }

    private async Task SaveProfileAsync()
    {
        if (_busy || _site is null || _profileExpectedVersion is not { } expectedVersion)
        {
            return;
        }
        var profile = new SiteProfileValues(
            Optional(_observatoryNameInput),
            Optional(_cameraNameInput),
            Optional(_ownerNameInput),
            Optional(_ownerContactInput));
        _busy = true;
        OperatorUiResult<SiteProfileResult> result;
        try
        {
            result = await SkyMapService.SaveSiteProfileAsync(profile, expectedVersion, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            _busy = false;
        }
        if (result.Kind == OperatorUiResultKind.Unauthorized)
        {
            NavigationManager.NavigateTo("/Account/AccessDenied");
            return;
        }
        if (result.IsSuccess && result.Value is { } saved)
        {
            _profileDirty = false;
            _dialog = SiteDialog.None;
            _restoreFocus = true;
            await LoadAsync().ConfigureAwait(false);
            SetNotice(DescribeProfile(saved), error: false);
            // The sidebar shows the camera name, which this save may have changed.
            Layout?.Refresh();
            return;
        }
        if (result.Kind == OperatorUiResultKind.Conflict)
        {
            // The operator's edits stay in the form; the next save is checked against the file as it is now.
            await LoadAsync().ConfigureAwait(false);
            _profileExpectedVersion = _site?.Profile.Version;
        }
        SetNotice(result.Message ?? "The site profile could not be saved.", error: true);
    }

    private async Task SaveObjectLimitAsync(bool useDefault)
    {
        if (_busy || _objectLimitExpectedVersion is not { } expectedVersion)
        {
            return;
        }
        int? limit = null;
        if (!useDefault && !string.IsNullOrWhiteSpace(_objectLimitInput))
        {
            if (!int.TryParse(_objectLimitInput.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ||
                parsed is < 1 or > CameraAgentSkyMapProjection.MaximumConfigurableObjects)
            {
                SetNotice(ObjectLimitRule, error: true);
                return;
            }
            limit = parsed;
        }
        _busy = true;
        OperatorUiResult<CameraAgentObjectLimitSetting> result;
        try
        {
            result = await SkyMapService.SaveObjectLimitAsync(limit, expectedVersion, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            _busy = false;
        }
        if (result.Kind == OperatorUiResultKind.Unauthorized)
        {
            NavigationManager.NavigateTo("/Account/AccessDenied");
            return;
        }
        if (result.IsSuccess && result.Value is { } saved)
        {
            _objectLimitDirty = false;
            // Projecting again shows the scene under the new bound.
            await LoadAsync().ConfigureAwait(false);
            SetNotice(
                (limit is null ? "The object limit is back to its default." : $"Saved an object limit of {limit:N0}.")
                + (saved.Overridden ? " Another setting source still supplies the limit, so it has no effect yet." : string.Empty),
                error: false);
            return;
        }
        if (result.Kind == OperatorUiResultKind.Conflict)
        {
            await LoadAsync().ConfigureAwait(false);
            _objectLimitExpectedVersion = _site?.ObjectLimit?.Version;
        }
        SetNotice(result.Message ?? "The object limit could not be saved.", error: true);
    }

    private static string ObjectLimitRule { get; } = string.Create(
        CultureInfo.InvariantCulture,
        $"The object limit must be a whole number from 1 to {CameraAgentSkyMapProjection.MaximumConfigurableObjects:N0}.");

    private static string DescribeLocation(ManualDeploymentLocationResult result) => result.Status switch
    {
        ManualDeploymentLocationStatus.Applied =>
            $"Recorded the location draft. The next CameraAgent start appends it as location version "
            + $"{result.State.NextVersion}; captures already recorded keep version {result.State.ActiveVersion}."
            + (result.State.CentralAcknowledgementRequired
                ? " Central integration is on, so the new version is proposed to LogicHost first "
                  + "and activates only after LogicHost acknowledges it."
                : string.Empty),
        ManualDeploymentLocationStatus.Replayed =>
            "This location draft was already recorded. No additional location version was created.",
        _ => "These coordinates already govern this deployment, so no new version was created."
    };

    private static string DescribeProfile(SiteProfileResult result) => result.Status == SiteProfileStatus.Applied
        ? "Saved the site profile to the operator settings file."
        : "The site profile already has these values, so nothing changed.";

    private bool TryParseLocation(
        out double latitude,
        out double longitude,
        out double elevation,
        out string timeZoneId)
    {
        longitude = 0;
        elevation = 0;
        timeZoneId = _timeZoneInput?.Trim() ?? string.Empty;
        if (!TryParseNumber(_latitudeInput, -90, 90, out latitude))
        {
            SetNotice("Latitude must be a number between -90 and 90 degrees.", error: true);
            return false;
        }
        if (!TryParseNumber(_longitudeInput, -180, 180, out longitude))
        {
            SetNotice("Longitude must be a number between -180 and 180 degrees, east positive.", error: true);
            return false;
        }
        if (!TryParseNumber(
                _elevationInput,
                ManualDeploymentLocationContract.MinimumElevationMeters,
                ManualDeploymentLocationContract.MaximumElevationMeters,
                out elevation))
        {
            SetNotice(
                $"Elevation must be a number between {ManualDeploymentLocationContract.MinimumElevationMeters:F0} "
                + $"and {ManualDeploymentLocationContract.MaximumElevationMeters:F0} metres.",
                error: true);
            return false;
        }
        if (timeZoneId.Length == 0)
        {
            SetNotice(
                "Time zone must be an IANA identifier this host can resolve, such as America/Phoenix or UTC.",
                error: true);
            return false;
        }
        return true;
    }

    /// <summary>Parses within the same bounds the message advertises, so the guidance is truthful.</summary>
    private static bool TryParseNumber(string value, double minimum, double maximum, out double parsed)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
           && double.IsFinite(parsed)
           && parsed >= minimum
           && parsed <= maximum;

    private void SetNotice(string message, bool error)
    {
        _notice = message;
        _noticeIsError = error;
    }

    private Banner LocationBanner
    {
        get
        {
            var observer = _state!.Observer;
            if (_manual is { CandidateAwaitingAcknowledgement: true, PendingVersion: { } proposed } candidate)
            {
                return new("warning", "Central review pending.",
                    $"LogicHost has not yet acknowledged location version {proposed}. Local geometry version "
                    + $"{candidate.ActiveVersion} remains active and capture continues normally.");
            }
            if (_manual is { StagedAcknowledgementPending: true } || observer.StagedAcknowledgementPending)
            {
                var staged = _manual?.PendingVersion is { } version ? $"Location version {version}" : "A new location version";
                return new("running", "Acknowledged version staged.",
                    $"{staged} was acknowledged by LogicHost and activates at the next CameraAgent start. "
                    + $"Version {_manual?.ActiveVersion ?? observer.Version} governs until then.");
            }
            if (_manual is { Override.PendingRestart: true, PendingVersion: null } waiting)
            {
                return new("running", "Local draft awaiting restart.",
                    $"Location version {waiting.NextVersion} was recorded {Timestamp(waiting.Override!.RecordedAtUtc)} "
                    + $"and activates at the next CameraAgent start. Version {waiting.ActiveVersion} governs until then.");
            }
            if (!observer.EffectiveAtInstant)
            {
                return new("warning", "Outside the effective window.",
                    $"Location version {observer.Version} did not govern at the chosen instant, so the sky preview "
                    + "below uses coordinates the camera was not using then.");
            }
            return new("success", $"Location version {observer.Version} is active.",
                "It governs schedules, the sky projection, and capture evidence on this camera.");
        }
    }

    private string? SiteName
        => _site?.Profile.Profile.ObservatoryName ?? _site?.Profile.EffectiveCameraName;

    private string SourceText
    {
        get
        {
            var observer = _state!.Observer;
            var kind = observer.SourceKind switch
            {
                DeploymentLocationSourceKind.Gps => "GPS receiver",
                DeploymentLocationSourceKind.Manual => "Operator entered",
                DeploymentLocationSourceKind.Inherited => "Observatory fallback",
                _ => string.IsNullOrWhiteSpace(observer.Source) ? "Not classified" : observer.Source.Trim()
            };
            return observer.HorizontalAccuracyMeters is { } accuracy
                ? $"{kind} / ± {Number(accuracy, 0)} m"
                : kind;
        }
    }

    // The online map is a browser-side Web Mercator tile mosaic centred on the active coordinates. The
    // server only computes tile addresses from configuration; it never fetches a tile itself.
    internal IReadOnlyList<MapTile>? MapTiles
    {
        get
        {
            if (_state is null || _site?.Map is not { Enabled: true } map)
            {
                return null;
            }
            return ComputeTiles(_state.Observer.LatitudeDegrees, _state.Observer.LongitudeDegrees, map.TileTemplate, map.Zoom);
        }
    }

    internal static IReadOnlyList<MapTile> ComputeTiles(double latitude, double longitude, string template, int zoom)
    {
        var scale = Math.Pow(2, zoom);
        var worldPixels = scale * TileSize;
        var phi = Math.Clamp(latitude, -MercatorLatitudeLimit, MercatorLatitudeLimit) * Math.PI / 180d;
        var pixelX = (longitude + 180d) / 360d * worldPixels;
        var pixelY = (1d - Math.Log(Math.Tan(phi) + 1d / Math.Cos(phi)) / Math.PI) / 2d * worldPixels;
        var centreX = (int)Math.Floor(pixelX / TileSize);
        var centreY = (int)Math.Floor(pixelY / TileSize);
        var tileCount = (int)scale;
        var tiles = new List<MapTile>(15);
        for (var row = centreY - 1; row <= centreY + 1; row++)
        {
            if (row < 0 || row >= tileCount)
            {
                continue;
            }
            for (var column = centreX - 2; column <= centreX + 2; column++)
            {
                var wrapped = ((column % tileCount) + tileCount) % tileCount;
                var source = template
                    .Replace("{z}", zoom.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    .Replace("{x}", wrapped.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    .Replace("{y}", row.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
                tiles.Add(new MapTile(source, column, row, column * TileSize - pixelX, row * TileSize - pixelY));
            }
        }
        return tiles;
    }

    private string? TileSignature => MapTiles is { Count: > 0 } tiles
        ? string.Join('\n', tiles.Select(tile => tile.Source))
        : null;

    private string TileHost => MapTiles is { Count: > 0 } tiles && Uri.TryCreate(tiles[0].Source, UriKind.Absolute, out var uri)
        ? uri.Host
        : "the configured tile server";

    private static string TileStyle(MapTile tile)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"left: calc(50% + {tile.Left:F1}px); top: calc(50% + {tile.Top:F1}px)");

    private (string Css, string Text) AssignmentChip => _site?.Assignment switch
    {
        null => ("warning", "Unavailable"),
        { State: CameraAgentSiteAssignmentState.Standalone } => ("pending", "Standalone"),
        { State: CameraAgentSiteAssignmentState.NotRegistered } => ("warning", "Not registered"),
        { State: CameraAgentSiteAssignmentState.Unavailable } => ("warning", "Unavailable"),
        { ProposedVersion: not null } => ("warning", "Review"),
        { LocationReview: DeploymentLocationResolutionStatus.Acknowledged } => ("success", "Location acknowledged"),
        { LocationReview: DeploymentLocationResolutionStatus.Rejected } => ("failure", "Location rejected"),
        _ => ("running", "Pending")
    };

    private string AssignmentLead => _site?.Assignment.State switch
    {
        CameraAgentSiteAssignmentState.Standalone => "This camera runs without LogicHost.",
        CameraAgentSiteAssignmentState.Registered => "Received during device registration.",
        _ => "Read-only view of what LogicHost has told this camera."
    };

    private static string LocationReviewText(DeploymentLocationResolutionStatus? status) => status switch
    {
        DeploymentLocationResolutionStatus.Acknowledged => "Acknowledged by LogicHost",
        DeploymentLocationResolutionStatus.Rejected => "Rejected by LogicHost",
        DeploymentLocationResolutionStatus.Pending => "Awaiting LogicHost review",
        _ => "Not reported"
    };

    private static string RegistrationHref
        => OperationsSectionCatalog.Sections.First(section => section.Slug == "registration").Href;

    private NamedRigRevision? ActiveRig => _rigCatalog?.Revisions.FirstOrDefault(
        revision => revision.RevisionId == _rigCatalog.Selection.ActiveRevisionId);

    private string RigTitle => _rigCatalog is null
        ? "Rig catalog unavailable"
        : ActiveRig is { } rig
            ? $"{_rigInventory?.Profiles.FirstOrDefault(profile => profile.ProfileId == rig.ProfileId)?.DisplayName ?? "Named rig"} / r{rig.RevisionNumber}"
            : "No named rig active";

    private (string Icon, string Text) Provenance
    {
        get
        {
            if (_state!.LatestCaptureScene is not { } scene)
            {
                return ("pending", "No retained capture carries scene provenance yet, so there is no capture to compare this preview with.");
            }
            var when = scene.SceneUtc is { } sceneUtc ? $"captured {Timestamp(sceneUtc)}" : "with no recorded scene time";
            var sameCatalog = string.Equals(scene.CatalogName, _state.Catalog.Name, StringComparison.Ordinal)
                && string.Equals(scene.CatalogVersion, _state.Catalog.Version, StringComparison.Ordinal)
                && string.Equals(scene.CatalogChecksumSha256, _state.Catalog.ChecksumSha256, StringComparison.OrdinalIgnoreCase);
            var sameRig = string.Equals(scene.RigProfileVersion, _state.Geometry.RigProfileVersion, StringComparison.Ordinal)
                && (scene.RigProfileHashSha256 is null
                    || string.Equals(scene.RigProfileHashSha256, _state.Geometry.RigProfileHashSha256, StringComparison.OrdinalIgnoreCase));
            if (sameCatalog && sameRig)
            {
                return ("success", $"The newest capture, {when}, was projected with this same catalog and rig geometry.");
            }
            var differences = new List<string>(2);
            if (!sameCatalog)
            {
                differences.Add($"the {scene.CatalogName} {scene.CatalogVersion} catalog");
            }
            if (!sameRig)
            {
                differences.Add("an earlier rig geometry");
            }
            return ("warning", $"The newest capture, {when}, was projected with {string.Join(" and ", differences)}, so it may not match this preview.");
        }
    }

    private static IEnumerable<int> MagnitudeLimits => s_magnitudeLimits;

    private SceneView ScenePage
    {
        get
        {
            var matches = SceneMatches();
            var pages = (matches.Count + ScenePageSize - 1) / ScenePageSize;
            _scenePage = pages == 0 ? 0 : Math.Clamp(_scenePage, 0, pages - 1);
            var items = matches.Skip(_scenePage * ScenePageSize).Take(ScenePageSize).ToList();
            var first = items.Count == 0 ? 0 : _scenePage * ScenePageSize + 1;
            return new SceneView(
                items,
                items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal),
                matches.Count,
                pages,
                first,
                first == 0 ? 0 : first + items.Count - 1);
        }
    }

    /// <summary>The visible objects that pass the table's search and brightness filters, in table order.</summary>
    private List<CameraAgentSkyMapObject> SceneMatches()
    {
        IEnumerable<CameraAgentSkyMapObject> query = _state?.Objects ?? [];
        var search = _search.Trim();
        if (search.Length > 0)
        {
            query = query.Where(item => item.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase));
        }
        if (int.TryParse(_magnitudeFilter, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit))
        {
            query = query.Where(item => item.Magnitude <= limit);
        }
        IOrderedEnumerable<CameraAgentSkyMapObject> ordered = (_sort, _sortDescending) switch
        {
            (SceneSort.Name, false) => query.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase),
            (SceneSort.Name, true) => query.OrderByDescending(item => item.DisplayName, StringComparer.OrdinalIgnoreCase),
            (SceneSort.Altitude, false) => query.OrderBy(item => item.AltitudeDegrees),
            (SceneSort.Altitude, true) => query.OrderByDescending(item => item.AltitudeDegrees),
            (SceneSort.Azimuth, false) => query.OrderBy(item => item.AzimuthDegrees),
            (SceneSort.Azimuth, true) => query.OrderByDescending(item => item.AzimuthDegrees),
            (_, false) => query.OrderBy(item => item.Magnitude),
            (_, true) => query.OrderByDescending(item => item.Magnitude)
        };
        return ordered.ThenBy(item => item.Id, StringComparer.Ordinal).ToList();
    }

    private CameraAgentSkyMapObject? SelectedObject
        => _selectedId is null ? null : _state?.Objects.FirstOrDefault(item => item.Id == _selectedId);

    /// <summary>
    /// Every installed constellation figure, those crossing the image first. The topology has no full names,
    /// so figures are offered by their catalog abbreviation.
    /// </summary>
    private IReadOnlyList<ConstellationChoice> ConstellationChoices
    {
        get
        {
            if (_state is null)
            {
                return [];
            }
            var inImage = _state.Constellations.Select(static item => item.ConstellationId).ToHashSet(StringComparer.Ordinal);
            return _state.InstalledConstellationIds
                .Union(inImage, StringComparer.Ordinal)
                .Select(id => new ConstellationChoice(id, inImage.Contains(id)))
                .OrderBy(static choice => choice.InImage ? 0 : 1)
                .ThenBy(static choice => choice.Id, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>Labels an object from its table row, or clears the label when the row is already selected.</summary>
    private void SelectObject(string id) => _selectedId = _selectedId == id ? null : id;

    private void ClearSelection() => _selectedId = null;

    /// <summary>
    /// Labels an object chosen on the plot and pages the table to its row. An object the table filters hide
    /// clears those filters first, so the chosen row is always the one shown.
    /// </summary>
    private void RevealObject(string id)
    {
        _selectedId = id;
        var index = SceneMatches().FindIndex(item => item.Id == id);
        if (index < 0 && (_search.Length > 0 || _magnitudeFilter.Length > 0))
        {
            _search = string.Empty;
            _magnitudeFilter = string.Empty;
            index = SceneMatches().FindIndex(item => item.Id == id);
        }
        if (index >= 0)
        {
            _scenePage = index / ScenePageSize;
            _revealRow = index % ScenePageSize;
        }
    }

    private void ToggleFigure(string id)
    {
        if (!_figures.Remove(id))
        {
            _figures.Add(id);
        }
    }

    private void ClearFigures() => _figures.Clear();

    /// <summary>A new projection keeps the label and the drawn figures that are still in the image.</summary>
    private void KeepSelectionsInScene(CameraAgentSkyMapProjectionResult projection)
    {
        if (_selectedId is not null && !projection.Objects.Any(item => item.Id == _selectedId))
        {
            _selectedId = null;
        }
        _figures.IntersectWith(projection.Constellations.Select(static item => item.ConstellationId));
    }

    private void ResetScenePage() => _scenePage = 0;

    private void PreviousScenePage() => _scenePage = Math.Max(0, _scenePage - 1);

    private void NextScenePage() => _scenePage++;

    private void SortBy(SceneSort sort)
    {
        _sortDescending = _sort == sort && !_sortDescending;
        _sort = sort;
        _scenePage = 0;
    }

    private string? AriaSort(SceneSort sort)
        => _sort != sort ? null : _sortDescending ? "descending" : "ascending";

    private string SortDescription
        => $"sorted by {_sort switch
        {
            SceneSort.Name => "name",
            SceneSort.Altitude => "altitude",
            SceneSort.Azimuth => "azimuth",
            _ => "magnitude"
        }}, {(_sortDescending ? "descending" : "ascending")}";

    private string ActorName(string? actor)
    {
        if (string.IsNullOrWhiteSpace(actor))
        {
            return "Not recorded";
        }
        return _site?.ActorNames.TryGetValue(actor, out var name) == true ? name : "Unrecognized account";
    }

    private static string Compass(double azimuthDegrees)
    {
        string[] points = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
        var normalized = ((azimuthDegrees % 360d) + 360d) % 360d;
        return points[(int)Math.Round(normalized / 45d, MidpointRounding.AwayFromZero) % points.Length];
    }

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NewKey() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    private static string Timestamp(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    private static string Latitude(double degrees)
        => string.Create(CultureInfo.InvariantCulture, $"{Math.Abs(degrees):F6}° {(degrees < 0 ? "S" : "N")}");

    private static string Longitude(double degrees)
        => string.Create(CultureInfo.InvariantCulture, $"{Math.Abs(degrees):F6}° {(degrees < 0 ? "W" : "E")}");

    private static string Elevation(double meters)
        => string.Create(CultureInfo.InvariantCulture, $"{meters:N0} m");

    private static string Number(double value, int decimals)
        => value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static string Split(string value) => OperationsPage.SplitWords(value);

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync().ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
            }
        }
    }
}
