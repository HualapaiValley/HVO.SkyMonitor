using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

/// <summary>
/// The prototype Camera &amp; rig page: the active rig's module, readout, and optics, a composer whose
/// building blocks are named, versioned equipment selected from dropdowns and edited in a modal dialog,
/// the compare-and-stage flow, and the named rig's immutable revision history.
/// </summary>
public sealed partial class CameraRigPage : ComponentBase, IAsyncDisposable
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly (string Kind, string Label)[] EquipmentBlocks = [("camera", "Camera"), ("optics", "Optics"), ("mount", "Mount")];
    [Inject] internal ICameraAgentNamedRigUiService RigService { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;
    [Inject] internal IJSRuntime JSRuntime { get; set; } = default!;

    private enum RigDialog
    {
        None,
        Equipment,
        Profile,
        Starter,
        ActiveEdit
    }

    private sealed record CompareRow(string Label, string Active, string Selected, bool Changed);

    private IJSObjectReference? _module;
    private ElementReference _dialogElement;
    private RigDialog _dialog;
    private bool _showDialog;
    private bool _restoreFocus;
    private string _restoreFocusId = "rig-profile-select";
    private bool _createNew;

    private NamedRigUiCatalog? _catalog;
    private NamedRigInventory? _inventory;
    private NamedRigHistoryPage? _history;
    private string? _historyProfileId;
    private long _historyRequestGeneration;
    private NamedRigPreview? _preview;
    private NamedRigRevision? _historicalRevision;
    private long _previewGeneration;
    private EquipmentFormModel _form = new();
    private EquipmentFormModel _edit = new();
    private NamedRigRevision? _editBasis;
    private long _editVersion;
    private ActiveRigEditOutcome? _editOutcome;
    private NamedEquipmentDetail? _basis;
    private NamedEquipmentDetail? _activeCamera;
    private NamedEquipmentDetail? _selectedCamera;
    private string _kind = "camera";
    private string _equipmentId = string.Empty;
    private string _equipmentName = string.Empty;
    private string _profileId = string.Empty;
    private string _profileName = string.Empty;
    private string _cameraId = string.Empty;
    private string _opticsId = string.Empty;
    private string _mountId = string.Empty;
    private string _revisionId = string.Empty;
    private bool _duplicate = true;
    private bool _newProfile;
    private string _customCameraModel = string.Empty;
    private string _starterTemplate = "asi676mc";
    private string _starterName = "ASI676MC camera";
    private bool _acknowledgeCustomModel;
    private bool _acknowledge;
    private bool _busy;
    private bool _staging;
    private string? _message;
    private bool _error;
    private bool _virtualOnlyInventory;
    private (string Revision, long Version, string Key, string Schedule, string Hash)? _stageAttempt;
    private (string Revision, long Version, string Key)? _cancelAttempt;
    private string? _previewScheduleId;
    private string? _previewInputs;
    private NamedRigPreview? CurrentPreview => _previewInputs == PreviewInputs ? _preview : null;
    private string PreviewInputs => JsonSerializer.Serialize(new
    {
        _kind,
        _equipmentId,
        _equipmentName,
        _duplicate,
        _customCameraModel,
        _acknowledgeCustomModel,
        _form,
        _profileId,
        _profileName,
        _newProfile,
        _cameraId,
        _opticsId,
        _mountId,
        _revisionId
    }, WebJson);
    private bool InstalledBasis => _basis?.IsInstalled == true;
    private NamedRigRevision? ActiveRevision
        => _catalog?.Revisions.FirstOrDefault(r => r.RevisionId == _catalog.Selection.ActiveRevisionId);
    private bool CanEditActive => !_busy && ActiveRevision is not null && _catalog!.Selection.PendingRevisionId is null &&
        _stageAttempt is null && _cancelAttempt is null;
    private bool VirtualOnlyInventory => _virtualOnlyInventory;

    private async Task CreateZwoStarterAsync()
    {
        InvalidatePreview();
        _busy = true;
        try
        {
            var result = await RigService.CreateZwoStarterAsync(_starterTemplate, _starterName, CancellationToken.None);
            if (Handle(result)) return;
            var revisionId = result.Value!.RevisionId;
            _kind = "camera";
            await RefreshAsync();
            _equipmentId = revisionId;
            _cameraId = revisionId;
            _createNew = false;
            _dialog = RigDialog.Equipment;
            _restoreFocusId = SelectId("camera");
            await LoadEquipmentAsync();
            _message = "ZWO camera created. Review its typed settings, then compose a rig.";
            _error = false;
        }
        finally { _busy = false; }
    }

    private static string CameraModel(NamedEquipmentDetail? detail)
    {
        if (detail is null) return "Model unavailable";
        var definition = JsonSerializer.SerializeToElement(detail.Definition, WebJson);
        if (!definition.TryGetProperty("expectedModel", out var model) || model.ValueKind != JsonValueKind.String)
            return "Model unavailable";
        var name = model.GetString();
        if (string.IsNullOrWhiteSpace(name)) return "Model unavailable";
        var risk = definition.TryGetProperty("useUnvalidatedCameraAtOwnRisk", out var flag) && flag.ValueKind == JsonValueKind.True;
        return $"{name} ({(risk ? "unvalidated model" : "model not flagged unvalidated")})";
    }

    private async Task<NamedEquipmentDetail?> CameraDetailAsync(string? revisionId)
    {
        if (string.IsNullOrEmpty(revisionId)) return null;
        var result = await RigService.GetEquipmentAsync(revisionId, CancellationToken.None);
        return Handle(result) ? null : result.Value;
    }

    private void InvalidatePreview()
    {
        _previewGeneration++;
        _preview = null;
        _acknowledge = false;
        _previewScheduleId = null;
        _previewInputs = null;
    }

    protected override async Task OnInitializedAsync() => await RefreshAsync();

    private async Task RefreshAsync()
    {
        _busy = true;
        InvalidatePreview();
        _editOutcome = null;
        try
        {
            var previousProfileId = _profileId;
            var previousProfileName = _inventory?.Profiles.FirstOrDefault(p => p.ProfileId == _profileId)?.DisplayName;
            var catalog = await RigService.GetAsync(CancellationToken.None);
            if (Handle(catalog)) return;
            var inventory = await RigService.GetInventoryAsync(CancellationToken.None);
            if (Handle(inventory)) return;
            _catalog = catalog.Value;
            _inventory = inventory.Value;
            _virtualOnlyInventory = true;
            foreach (var camera in _inventory!.Equipment.Where(e => e.Kind == "camera"))
            {
                var detail = await RigService.GetEquipmentAsync(camera.RevisionId, CancellationToken.None);
                if (Handle(detail)) return;
                var definition = JsonSerializer.SerializeToElement(detail.Value!.Definition, WebJson);
                if (!definition.TryGetProperty("module", out var module) ||
                    !module.TryGetProperty("type", out var type) || type.GetString() != "VirtualSky")
                {
                    _virtualOnlyInventory = false;
                    break;
                }
            }
            var selectedRevisionId = _revisionId;
            _activeCamera = await CameraDetailAsync(_catalog!.Revisions.FirstOrDefault(r => r.RevisionId == _catalog.Selection.ActiveRevisionId)?.CameraRevisionId);
            var activeProfileId = ActiveRevision?.ProfileId;
            _profileId = _inventory!.Profiles.Any(p => p.ProfileId == _profileId)
                ? _profileId
                : _inventory.Profiles.Any(p => p.ProfileId == activeProfileId) ? activeProfileId!
                : _inventory.Profiles.Count > 0 ? _inventory.Profiles[0].ProfileId : string.Empty;
            if (_profileId != previousProfileId || _profileName == previousProfileName || previousProfileName is null)
                _profileName = _inventory.Profiles.FirstOrDefault(p => p.ProfileId == _profileId)?.DisplayName ?? string.Empty;
            await LoadHistoryAsync();
            while (!string.IsNullOrEmpty(selectedRevisionId) &&
                   !_catalog.Revisions.Any(r => r.RevisionId == selectedRevisionId && r.ProfileId == _profileId) &&
                   _history?.NextBeforeRevisionNumber is not null &&
                   !_history.Revisions.Any(r => r.RevisionId == selectedRevisionId))
            {
                var cursor = _history.NextBeforeRevisionNumber;
                await LoadHistoryAsync(true);
                if (_history?.NextBeforeRevisionNumber == cursor) break;
            }
            _revisionId = _catalog.Revisions.Any(r => r.RevisionId == selectedRevisionId && r.ProfileId == _profileId) ||
                _history?.Revisions.Any(r => r.RevisionId == selectedRevisionId) == true
                ? selectedRevisionId : LatestProfileRevisionId();
            await RevisionSelectionChangedAsync();
            _cameraId = SelectEquipment("camera", _cameraId);
            _opticsId = SelectEquipment("optics", _opticsId);
            _mountId = SelectEquipment("mount", _mountId);
            if (_stageAttempt is null && _cancelAttempt is null) { _message = null; _error = false; }
        }
        finally { _busy = false; }
    }

    /// <summary>
    /// Keeps a still-listed composer choice; otherwise defaults to the active rig's revision of that
    /// building block, so composing starts from what is running rather than from the first listed item.
    /// </summary>
    private string SelectEquipment(string kind, string current)
    {
        if (_inventory!.Equipment.Any(e => e.Kind == kind && e.RevisionId == current)) return current;
        var active = ActiveRevision;
        var preferred = active is null ? null : kind switch
        {
            "camera" => active.CameraRevisionId,
            "optics" => active.OpticsRevisionId,
            _ => active.MountRevisionId
        };
        return preferred is not null && _inventory.Equipment.Any(e => e.Kind == kind && e.RevisionId == preferred)
            ? preferred : _inventory.Equipment.FirstOrDefault(e => e.Kind == kind)?.RevisionId ?? string.Empty;
    }

    private string LatestProfileRevisionId()
    {
        var current = _catalog?.Revisions.Where(r => r.ProfileId == _profileId)
            .OrderByDescending(r => r.RevisionNumber).FirstOrDefault();
        var historical = _history?.Revisions.Where(r => r.ProfileId == _profileId)
            .OrderByDescending(r => r.RevisionNumber).FirstOrDefault();
        return historical is not null && (current is null || historical.RevisionNumber > current.RevisionNumber)
            ? historical.RevisionId : current?.RevisionId ?? string.Empty;
    }

    private bool Handle<T>(OperatorUiResult<T> result)
    {
        if (result.Kind == OperatorUiResultKind.Unauthorized)
        {
            NavigationManager.NavigateTo("/Account/AccessDenied");
            return true;
        }
        if (result.IsSuccess && result.Value is not null) return false;
        _message = result.Message ?? "Rig operation failed.";
        _error = true;
        return true;
    }

    private async Task LoadEquipmentAsync()
    {
        InvalidatePreview();
        _basis = null;
        _form = new();
        _equipmentName = string.Empty;
        _duplicate = true;
        _customCameraModel = string.Empty;
        _acknowledgeCustomModel = false;
        if (string.IsNullOrEmpty(_equipmentId)) return;
        _busy = true;
        try
        {
            var result = await RigService.GetEquipmentAsync(_equipmentId, CancellationToken.None);
            if (Handle(result)) return;
            _basis = result.Value;
            _duplicate = !_basis!.CanRevise;
            _equipmentName = _duplicate ? ProposeDuplicateName(_basis!.DisplayName) : _basis!.DisplayName;
            var json = JsonSerializer.SerializeToElement(_basis.Definition, WebJson);
            _form = _kind switch
            {
                "camera" => EquipmentFormModel.FromCamera(json.Deserialize<NamedCameraEquipment>(WebJson)!),
                "optics" => EquipmentFormModel.FromOptics(json.Deserialize<HVO.SkyMonitor.AgentCore.OpticsProfile>(WebJson)!),
                _ => EquipmentFormModel.FromMount(json.Deserialize<HVO.SkyMonitor.AgentCore.RigOrientation>(WebJson)!)
            };
        }
        finally { _busy = false; }
    }

    private string ProposeDuplicateName(string name)
    {
        var trimmed = name.Trim();
        for (var number = 1; ; number++)
        {
            var suffix = number == 1 ? " copy" : $" copy {number}";
            var candidate = string.Concat(trimmed[..Math.Min(trimmed.Length, 128 - suffix.Length)].TrimEnd(), suffix);
            if (_inventory?.Equipment.Any(e => e.Kind == _kind &&
                string.Equals(e.DisplayName, candidate, StringComparison.OrdinalIgnoreCase)) != true)
                return candidate;
        }
    }

    private async Task BasisChangedAsync()
    {
        await LoadEquipmentAsync();
        if (_createNew) ForceDuplicate();
    }

    // A bool bound to a select renders an empty value attribute, so the mode travels as a string.
    private string SaveMode => _duplicate ? "duplicate" : "revise";

    private void SetSaveMode(string? value)
    {
        if (_basis is null) return;
        var duplicate = value != "revise" || !_basis.CanRevise;
        if (duplicate == _duplicate) return;
        // Swap the proposed name with the mode unless the operator already typed their own.
        var proposed = _duplicate ? ProposeDuplicateName(_basis.DisplayName) : _basis.DisplayName;
        if (string.Equals(_equipmentName, proposed, StringComparison.Ordinal))
            _equipmentName = duplicate ? ProposeDuplicateName(_basis.DisplayName) : _basis.DisplayName;
        _duplicate = duplicate;
    }

    private void ForceDuplicate()
    {
        if (_basis is null || _duplicate) return;
        _duplicate = true;
        _equipmentName = ProposeDuplicateName(_basis.DisplayName);
    }

    private async Task SaveEquipmentAsync()
    {
        InvalidatePreview();
        if (_basis is null || string.IsNullOrWhiteSpace(_equipmentName))
        {
            _message = "Select a basis revision and enter a name."; _error = true; return;
        }
        if (!_basis.CanRevise && !_duplicate)
        {
            _message = "This equipment revision cannot be revised. Duplicate it as new equipment instead.";
            _error = true;
            return;
        }
        if (_equipmentName.Trim().Length > 128)
        {
            _message = "Equipment name must be 128 characters or fewer."; _error = true; return;
        }
        if (_duplicate && _inventory?.Equipment.Any(e => e.Kind == _kind &&
            string.Equals(e.DisplayName, _equipmentName.Trim(), StringComparison.OrdinalIgnoreCase)) == true)
        {
            _message = "Equipment with this name already exists. Enter a different name for the duplicate.";
            _error = true;
            return;
        }
        var json = JsonSerializer.SerializeToElement(_basis.Definition, WebJson);
        JsonElement definition;
        IReadOnlyList<string> errors;
        bool valid;
        switch (_kind)
        {
            case "camera":
                valid = _form.TryApplyCamera(json.Deserialize<NamedCameraEquipment>(WebJson)!, out definition, out errors);
                break;
            case "optics":
                valid = _form.TryApplyOptics(json.Deserialize<HVO.SkyMonitor.AgentCore.OpticsProfile>(WebJson)!, out var optics, out errors);
                definition = JsonSerializer.SerializeToElement(optics, WebJson);
                break;
            default:
                valid = _form.TryApplyMount(json.Deserialize<HVO.SkyMonitor.AgentCore.RigOrientation>(WebJson)!, out var mount, out errors);
                definition = JsonSerializer.SerializeToElement(mount, WebJson);
                break;
        }
        if (!valid) { _message = string.Join(' ', errors); _error = true; return; }
        if (_kind == "camera" && _duplicate && !string.IsNullOrWhiteSpace(_customCameraModel))
        {
            if (!_acknowledgeCustomModel)
            {
                _message = "A custom camera model requires explicit unvalidated-model acknowledgement.";
                _error = true;
                return;
            }
            var candidate = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(definition, WebJson)!;
            candidate["expectedModel"] = JsonSerializer.SerializeToElement(_customCameraModel.Trim());
            candidate["useUnvalidatedCameraAtOwnRisk"] = JsonSerializer.SerializeToElement(true);
            definition = JsonSerializer.SerializeToElement(candidate, WebJson);
        }
        _busy = true;
        try
        {
            var result = await RigService.SaveEquipmentAsync(_duplicate ? null : _basis.DefinitionId, _kind,
                _equipmentName, definition, _duplicate && _kind != "camera" ? null : _basis.RevisionId,
                _duplicate ? null : _basis.RevisionId, CancellationToken.None);
            if (result.Kind == OperatorUiResultKind.Conflict)
            {
                _message = "Equipment name or basis changed. Refresh the inventory and choose a different name before retrying.";
                _error = true;
                return;
            }
            if (Handle(result)) return;
            _equipmentId = result.Value!.RevisionId;
            switch (_kind)
            {
                case "camera": _cameraId = _equipmentId; break;
                case "optics": _opticsId = _equipmentId; break;
                case "mount": _mountId = _equipmentId; break;
            }
            await RefreshAsync();
            await LoadEquipmentAsync();
            FinishDialog(SelectId(_kind));
            _message = "Equipment revision saved. Compose a rig to use it."; _error = false;
        }
        finally { _busy = false; }
    }

    private async Task SaveProfileAsync()
    {
        InvalidatePreview();
        _busy = true;
        try
        {
            var result = await RigService.SaveProfileAsync(_newProfile ? null : _profileId, _profileName, CancellationToken.None);
            if (Handle(result)) return;
            _profileId = result.Value!.ProfileId;
            _profileName = result.Value.DisplayName;
            _newProfile = false;
            await RefreshAsync();
            FinishDialog(SelectId("profile"));
            _message = "Rig name saved."; _error = false;
        }
        finally { _busy = false; }
    }

    private async Task ComposeAsync()
    {
        InvalidatePreview();
        _busy = true;
        try
        {
            var result = await RigService.ComposeAsync(_profileId, _cameraId, _opticsId, _mountId, CancellationToken.None);
            if (Handle(result)) return;
            _revisionId = result.Value!.RevisionId;
            _preview = null;
            _acknowledge = false;
            await RefreshAsync();
            _message = "Immutable rig revision composed. Preview before staging."; _error = false;
        }
        finally { _busy = false; }
    }

    private void RevisionChanged() => InvalidatePreview();

    private async Task RevisionSelectionChangedAsync()
    {
        RevisionChanged();
        var generation = _previewGeneration;
        var revisionId = _revisionId;
        _historicalRevision = null;
        _selectedCamera = null;
        var current = _catalog?.Revisions.FirstOrDefault(r => r.RevisionId == revisionId);
        if (current is not null)
        {
            var currentCamera = await RigService.GetEquipmentAsync(current.CameraRevisionId, CancellationToken.None);
            if (generation != _previewGeneration || revisionId != _revisionId) return;
            if (!Handle(currentCamera)) _selectedCamera = currentCamera.Value;
            return;
        }
        var entry = _history?.Revisions.FirstOrDefault(r => r.RevisionId == revisionId);
        if (entry is null) return;
        var camera = await RigService.GetEquipmentAsync(entry.CameraRevisionId, CancellationToken.None);
        if (generation != _previewGeneration || revisionId != _revisionId) return;
        if (Handle(camera)) return;
        var optics = await RigService.GetEquipmentAsync(entry.OpticsRevisionId, CancellationToken.None);
        if (generation != _previewGeneration || revisionId != _revisionId) return;
        if (Handle(optics)) return;
        var mount = await RigService.GetEquipmentAsync(entry.MountRevisionId, CancellationToken.None);
        if (generation != _previewGeneration || revisionId != _revisionId) return;
        if (Handle(mount)) return;
        var module = JsonSerializer.SerializeToElement(camera.Value!.Definition, WebJson);
        var equipment = module.Deserialize<NamedCameraEquipment>(WebJson)!;
        var template = _catalog!.Revisions.Count > 0 ? _catalog.Revisions[0] : null;
        if (template is null) return;
        _selectedCamera = camera.Value;
        _historicalRevision = new NamedRigRevision(entry.RevisionId, entry.ProfileId, entry.RevisionNumber,
            entry.CameraRevisionId, entry.OpticsRevisionId, entry.MountRevisionId, equipment.Module,
            template.Rig with
            {
                Sensor = equipment.Sensor,
                Readout = equipment.Readout,
                Optics = JsonSerializer.SerializeToElement(optics.Value!.Definition, WebJson).Deserialize<HVO.SkyMonitor.AgentCore.OpticsProfile>(WebJson)!,
                Orientation = JsonSerializer.SerializeToElement(mount.Value!.Definition, WebJson).Deserialize<HVO.SkyMonitor.AgentCore.RigOrientation>(WebJson)!
            },
            entry.SourceScheduleRevisionId);
    }

    private async Task LoadHistoryAsync(bool more = false)
    {
        var generation = ++_historyRequestGeneration;
        if (string.IsNullOrEmpty(_profileId)) { _history = null; _historyProfileId = null; return; }
        var profileId = _profileId;
        var previous = more && _historyProfileId == profileId ? _history : null;
        if (more && previous?.NextBeforeRevisionNumber is null) return;
        var cursor = more ? previous?.NextBeforeRevisionNumber : null;
        var version = more ? previous?.Version : null;
        var result = await RigService.GetHistoryAsync(profileId, 50, cursor, version, CancellationToken.None);
        if (generation != _historyRequestGeneration || profileId != _profileId ||
            (more && (!ReferenceEquals(previous, _history) || _historyProfileId != profileId))) return;
        if (Handle(result)) return;
        var page = result.Value!;
        _history = previous is null ? page : page with { Revisions = previous.Revisions.Concat(page.Revisions).ToArray() };
        _historyProfileId = profileId;
    }

    private async Task ProfileChangedAsync()
    {
        InvalidatePreview();
        _profileName = _inventory?.Profiles.FirstOrDefault(p => p.ProfileId == _profileId)?.DisplayName ?? string.Empty;
        _newProfile = false;
        _revisionId = string.Empty;
        _historicalRevision = null;
        _selectedCamera = null;
        _history = null;
        _historyProfileId = null;
        var profileId = _profileId;
        var load = LoadHistoryAsync();
        var generation = _historyRequestGeneration;
        await load;
        if (profileId != _profileId || generation != _historyRequestGeneration) return;
        _revisionId = LatestProfileRevisionId();
        await RevisionSelectionChangedAsync();
    }

    private async Task PreviewAsync()
    {
        InvalidatePreview();
        var generation = _previewGeneration;
        var inputs = PreviewInputs;
        var revision = _revisionId;
        var result = await RigService.PreviewAsync(revision, CancellationToken.None);
        if (generation != _previewGeneration || inputs != PreviewInputs) return;
        if (!Handle(result)) { _preview = result.Value; _previewInputs = inputs; _previewScheduleId = result.Value?.ScheduleRevisionId; _message = null; }
    }

    private async Task StageAsync()
    {
        if (_staging || _busy || _catalog is null) return;
        _staging = true;
        _busy = true;
        try
        {
            if (_stageAttempt is not null && _stageAttempt.Value.Revision != _revisionId) return;
            var retry = _stageAttempt is { } previous && previous.Revision == _revisionId;
            if (_catalog.Selection.PendingRevisionId is { } pending && (!retry || pending != _revisionId)) return;
            if (!retry)
            {
                if (CurrentPreview is not { Valid: true, RuntimeVerified: false } preview || !_acknowledge ||
                    preview.RigRevisionId != _revisionId) return;
                var generation = _previewGeneration;
                var inputs = PreviewInputs;
                var revision = _revisionId;
                var latest = await RigService.PreviewAsync(revision, CancellationToken.None);
                if (generation != _previewGeneration || inputs != PreviewInputs || revision != _revisionId ||
                    !_acknowledge || !ReferenceEquals(CurrentPreview, preview) ||
                    _catalog.Selection.PendingRevisionId is not null) return;
                if (Handle(latest)) return;
                if (latest.Value is not { Valid: true, RuntimeVerified: false } current ||
                    current.ScheduleRevisionId != _previewScheduleId ||
                    current.ScheduleProfileSha256 != preview.ScheduleProfileSha256)
                {
                    InvalidatePreview();
                    _message = "The active schedule changed or the preview is no longer valid. Preview again.";
                    _error = true;
                    return;
                }
                _stageAttempt = (revision, _catalog.Selection.Version, Guid.NewGuid().ToString("N"),
                    preview.ScheduleRevisionId, preview.ScheduleProfileSha256);
            }
            var attempt = _stageAttempt!.Value;
            var result = await RigService.StageAsync(attempt.Revision, attempt.Version, attempt.Key, true,
                attempt.Schedule, attempt.Hash, CancellationToken.None);
            if (result.Kind != OperatorUiResultKind.Unavailable) _stageAttempt = null;
            if (Handle(result)) return;
            var previousCatalog = _catalog;
            await RefreshAsync();
            if (_error)
            {
                _message = "Stage request was recorded, but the current selection could not be verified. Refresh before acting.";
                return;
            }
            _preview = null; _acknowledge = false;
            var receipt = result.Value!;
            var selection = _catalog?.Selection;
            _message = !ReferenceEquals(previousCatalog, _catalog) &&
                receipt.RevisionId == attempt.Revision && receipt.Disposition == "restart_required" &&
                receipt.Version == attempt.Version + 1 && selection?.Version == receipt.Version &&
                selection.PendingRevisionId == attempt.Revision &&
                selection.ActiveRevisionId == previousCatalog?.Selection.ActiveRevisionId
                ? "Rig staged for restart. Runtime compatibility is not yet verified."
                : "Stage request was recorded, but the current selection does not match its receipt. Review the refreshed active and pending rig before acting.";
            _error = false;
        }
        finally { _busy = false; _staging = false; }
    }

    private async Task CancelAsync()
    {
        if (_catalog?.Selection.PendingRevisionId is not { } pending) return;
        if (_cancelAttempt is not null && _cancelAttempt.Value.Revision != pending) return;
        if (_cancelAttempt is not { } previous || previous.Revision != pending)
            _cancelAttempt = (pending, _catalog.Selection.Version, Guid.NewGuid().ToString("N"));
        var attempt = _cancelAttempt.Value;
        _busy = true;
        try
        {
            var result = await RigService.CancelAsync(attempt.Revision, attempt.Version, attempt.Key, CancellationToken.None);
            if (result.Kind != OperatorUiResultKind.Unavailable) _cancelAttempt = null;
            if (Handle(result)) return;
            var previousCatalog = _catalog;
            await RefreshAsync();
            if (_error)
            {
                _message = "Cancel request was recorded, but the current selection could not be verified. Refresh before acting.";
                return;
            }
            var receipt = result.Value!;
            var selection = _catalog?.Selection;
            _message = !ReferenceEquals(previousCatalog, _catalog) &&
                receipt.RevisionId == attempt.Revision && receipt.Disposition == "cancelled" &&
                receipt.Version == attempt.Version + 1 && selection?.Version == receipt.Version &&
                selection.PendingRevisionId is null &&
                selection.ActiveRevisionId == previousCatalog?.Selection.ActiveRevisionId
                ? "Pending restart cancelled."
                : "Cancel request was recorded, but the current selection does not match its receipt. Review the refreshed active and pending rig before acting.";
            _error = false;
        }
        finally { _busy = false; }
    }

    private void AbandonStageAttempt()
    {
        _stageAttempt = null;
        InvalidatePreview();
        _message = "Stage retry abandoned. Preview the current schedule and acknowledge again before staging.";
        _error = false;
    }

    private void AbandonCancelAttempt()
    {
        _cancelAttempt = null;
        _message = "Cancel retry abandoned. Check the pending selection before submitting a new cancellation.";
        _error = false;
    }

    private async Task InspectRevisionAsync(string revisionId)
    {
        _revisionId = revisionId;
        await RevisionSelectionChangedAsync();
        _restoreFocusId = "rig-revision-select";
        _restoreFocus = true;
    }

    private async Task OpenEquipmentEditorAsync(string kind, bool createNew)
    {
        _message = null;
        _error = false;
        _kind = kind;
        _createNew = createNew;
        _equipmentId = ComposerId(kind);
        _restoreFocusId = $"rig-{kind}-{(createNew ? "new" : "edit")}";
        OpenDialog(RigDialog.Equipment);
        await LoadEquipmentAsync();
        if (createNew) ForceDuplicate();
    }

    private void OpenProfileDialog(bool createNew)
    {
        _message = null;
        _error = false;
        _newProfile = createNew;
        _profileName = createNew ? string.Empty : ProfileNameOrNull(_profileId) ?? string.Empty;
        _restoreFocusId = createNew ? "rig-profile-new" : "rig-profile-rename";
        OpenDialog(RigDialog.Profile);
    }

    private void OpenActiveEditDialog()
    {
        if (!CanEditActive || ActiveRevision is not { } active) return;
        _message = null;
        _error = false;
        _editOutcome = null;
        var mount = EquipmentFormModel.FromMount(active.Rig.Orientation);
        _edit = EquipmentFormModel.FromOptics(active.Rig.Optics);
        _edit.BoresightAltitudeDegrees = mount.BoresightAltitudeDegrees;
        _edit.BoresightAzimuthDegrees = mount.BoresightAzimuthDegrees;
        _edit.RollAdjustmentDegrees = mount.RollAdjustmentDegrees;
        _editBasis = active;
        _editVersion = _catalog!.Selection.Version;
        _restoreFocusId = "rig-edit-active";
        OpenDialog(RigDialog.ActiveEdit);
    }

    /// <summary>
    /// Applies the quick edit as one server-side chain (save changed equipment, compose, preview, stage). The
    /// confirm button is the acknowledgement that the change is unvalidated at runtime and applies on restart.
    /// </summary>
    private async Task ApplyActiveEditAsync()
    {
        if (_busy || _editBasis is not { } basis) return;
        var opticsValid = _edit.TryApplyOptics(basis.Rig.Optics, out var optics, out var opticsErrors);
        var mountValid = _edit.TryApplyMount(basis.Rig.Orientation, out var mount, out var mountErrors);
        if (!opticsValid || !mountValid)
        {
            _message = string.Join(" ", opticsErrors.Concat(mountErrors));
            _error = true;
            return;
        }
        InvalidatePreview();
        _message = null;
        _error = false;
        _busy = true;
        try
        {
            var request = new ActiveRigEditRequest(basis.RevisionId, _editVersion, optics.HorizontalFlip,
                optics.FieldOfViewDegrees, optics.FocalLengthMillimeters, mount.BoresightAltitudeDegrees,
                mount.BoresightAzimuthDegrees, mount.RollAdjustmentDegrees);
            var result = await RigService.ApplyActiveRigEditAsync(request, CancellationToken.None);
            if (Handle(result)) return;
            var outcome = result.Value!;
            FinishDialog("rig-edit-active");
            if (outcome.ComposedRevisionId is { } composed)
            {
                _profileId = basis.ProfileId;
                _revisionId = composed;
            }
            await RefreshAsync();
            var refreshFailed = _error;
            switch (outcome.Status)
            {
                case ActiveRigEditStatus.Staged:
                    _editOutcome = outcome;
                    _message = refreshFailed
                        ? "Rig change staged for restart, but the page could not refresh. Refresh before acting."
                        : "Rig change staged. It applies when CameraAgent restarts.";
                    _error = refreshFailed;
                    break;
                case ActiveRigEditStatus.NoChanges:
                    _message = "Nothing changed; no revisions were recorded.";
                    _error = false;
                    break;
                default:
                    _editOutcome = outcome;
                    _message = $"The rig edit stopped: {outcome.Failure ?? "the change could not be completed."}" +
                        (outcome.ComposedRevisionId is not null && !refreshFailed
                            ? " The composed revision is selected in Compare & stage." : string.Empty);
                    _error = true;
                    break;
            }
        }
        finally { _busy = false; }
    }

    private void OpenStarterDialog()
    {
        _message = null;
        _error = false;
        _restoreFocusId = "rig-add-zwo";
        OpenDialog(RigDialog.Starter);
    }

    private void OpenDialog(RigDialog dialog)
    {
        _dialog = dialog;
        _showDialog = true;
    }

    /// <summary>
    /// Dismisses the dialog without saving, discarding any message and unsaved rig name. The dialog
    /// stays open while a request is in flight, so its outcome is always presented where it was asked.
    /// </summary>
    private void CloseDialog()
    {
        if (_busy) return;
        _message = null;
        _error = false;
        _newProfile = false;
        _profileName = ProfileNameOrNull(_profileId) ?? string.Empty;
        FinishDialog(_restoreFocusId);
    }

    /// <summary>Closes the dialog after a completed action, keeping its outcome message for the page.</summary>
    private void FinishDialog(string focusId)
    {
        _dialog = RigDialog.None;
        _showDialog = false;
        _restoreFocusId = focusId;
        _restoreFocus = true;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_showDialog && _dialog != RigDialog.None)
        {
            _showDialog = false;
            await InvokeModuleAsync("showModal", _dialogElement);
        }
        else if (_restoreFocus && _dialog == RigDialog.None)
        {
            _restoreFocus = false;
            await InvokeModuleAsync("focusById", _restoreFocusId, SelectId("profile"));
        }
    }

    private async ValueTask InvokeModuleAsync(string identifier, params object?[] arguments)
    {
        _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/CameraRigPage.razor.js");
        await _module.InvokeVoidAsync(identifier, arguments);
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }
    }

    private string DialogEyebrow => _dialog switch
    {
        RigDialog.Profile => "Named rig",
        RigDialog.Starter => "Physical camera starter",
        RigDialog.ActiveEdit => "Active rig / applies on restart",
        _ => "Immutable equipment version"
    };

    private string DialogTitle => _dialog switch
    {
        RigDialog.Profile => _newProfile ? "New named rig" : "Rename rig",
        RigDialog.Starter => "Add ZWO camera",
        RigDialog.ActiveEdit => "Edit active rig",
        _ => $"{(_createNew ? "New" : "Edit")} {_kind}"
    };

    private const string FlipHelp = "On for an upward-looking all-sky camera: with North up, East appears on the left, as seen " +
        "looking up at the sky. Off is the mirrored, map-style view with East on the right.";

    private static string SelectId(string kind) => $"rig-{kind}-select";

    private string ComposerId(string kind) => kind switch
    {
        "camera" => _cameraId,
        "optics" => _opticsId,
        _ => _mountId
    };

    private IEnumerable<NamedEquipmentDefinition> Equipment(string kind)
        => _inventory?.Equipment.Where(e => e.Kind == kind) ?? [];

    private string? ProfileNameOrNull(string profileId)
        => _inventory?.Profiles.FirstOrDefault(p => p.ProfileId == profileId)?.DisplayName;

    private string ProfileName(string profileId) => ProfileNameOrNull(profileId) ?? "Named rig";

    /// <summary>
    /// A rig installed from a capture schedule keeps its installed name; the service refuses a rename, so
    /// the dialog explains that instead of offering a save that cannot succeed. Only loaded revisions are
    /// known here, and the service remains the authority.
    /// </summary>
    private bool ProfileNameIsFixed(string profileId)
        => _catalog?.Revisions.Any(r => r.ProfileId == profileId && r.SourceScheduleRevisionId is not null) == true ||
           _history?.Revisions.Any(r => r.ProfileId == profileId && r.SourceScheduleRevisionId is not null) == true;

    private bool ProfileNameFixed => !_newProfile && ProfileNameIsFixed(_profileId);

    private List<CompareRow> CompareRows(NamedRigRevision? active, NamedRigRevision selected)
    {
        var rows = new List<CompareRow>(8);
        void Add(string label, string? activeValue, string selectedValue)
            => rows.Add(new CompareRow(label, activeValue ?? "Not active", selectedValue,
                activeValue is not null && !string.Equals(activeValue, selectedValue, StringComparison.Ordinal)));

        Add("Rig", active is null ? null : $"{ProfileName(active.ProfileId)} / revision {active.RevisionNumber}",
            $"{ProfileName(selected.ProfileId)} / revision {selected.RevisionNumber}");
        Add("Camera", active is null ? null : EquipmentLabel(_activeCamera), EquipmentLabel(_selectedCamera));
        Add("Model", active is null ? null : CameraModel(_activeCamera), CameraModel(_selectedCamera));
        Add("Module", active is null ? null : Words(active.Module.Type), Words(selected.Module.Type));
        Add("Sensor", active is null ? null : SensorSummary(active.Rig), SensorSummary(selected.Rig));
        Add("Readout", active is null ? null : ReadoutSummary(active.Rig), ReadoutSummary(selected.Rig));
        Add("Optics", active is null ? null : OpticsSummary(active.Rig), OpticsSummary(selected.Rig));
        Add("Mount", active is null ? null : MountSummary(active.Rig), MountSummary(selected.Rig));
        return rows;
    }

    private static string EquipmentLabel(NamedEquipmentDetail? detail)
        => detail is null ? "Equipment unavailable" : $"{detail.DisplayName} v{detail.RevisionNumber}";

    private static string SensorSummary(HVO.SkyMonitor.AgentCore.CameraRigConfig rig)
        => $"{rig.Sensor.Name} / {rig.Sensor.WidthPixels} x {rig.Sensor.HeightPixels} px";

    private static string ReadoutSummary(HVO.SkyMonitor.AgentCore.CameraRigConfig rig)
        => rig.Readout is { } readout ? $"{readout.PixelFormat} / {readout.BinX} x {readout.BinY} bin" : "Not configured";

    private static string OpticsSummary(HVO.SkyMonitor.AgentCore.CameraRigConfig rig)
        => $"{Words(rig.Optics.ProjectionModel)} / {Number(rig.Optics.FocalLengthMillimeters)} mm / {Number(rig.Optics.FieldOfViewDegrees)} deg";

    private static string MountSummary(HVO.SkyMonitor.AgentCore.CameraRigConfig rig)
        => $"Alt {Number(rig.Orientation.BoresightAltitudeDegrees)} / az {Number(rig.Orientation.BoresightAzimuthDegrees)} deg";

    private static string Words(string value) => OperationsPage.SplitWords(value);

    // The mappings Astronomy projects; a basis spelled another accepted way (for example Rectilinear) stays selected.
    private static string[] ProjectionModels(string? current)
    {
        var names = Enum.GetNames<HVO.SkyMonitor.Astronomy.ProjectionModel>();
        return string.IsNullOrWhiteSpace(current) || names.Contains(current, StringComparer.Ordinal) ? names : [current, .. names];
    }

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Stride(int? bytes) => bytes is { } value ? $"{value} bytes" : "Derived";

    private static string ByteOrder(HVO.SkyMonitor.AgentCore.SampleByteOrder order)
        => order == HVO.SkyMonitor.AgentCore.SampleByteOrder.LittleEndian ? "LE" : "BE";
}
