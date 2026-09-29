using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class CameraRigPage : ComponentBase
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    [Inject] internal ICameraAgentNamedRigUiService RigService { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    private NamedRigUiCatalog? _catalog;
    private NamedRigInventory? _inventory;
    private NamedRigHistoryPage? _history;
    private string? _historyProfileId;
    private long _historyRequestGeneration;
    private NamedRigPreview? _preview;
    private NamedRigRevision? _historicalRevision;
    private long _previewGeneration;
    private EquipmentFormModel _form = new();
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
            _profileId = _inventory!.Profiles.Any(p => p.ProfileId == _profileId)
                ? _profileId : _inventory.Profiles.Count > 0 ? _inventory.Profiles[0].ProfileId : string.Empty;
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

    private string SelectEquipment(string kind, string current)
        => _inventory!.Equipment.Any(e => e.Kind == kind && e.RevisionId == current)
            ? current : _inventory.Equipment.FirstOrDefault(e => e.Kind == kind)?.RevisionId ?? string.Empty;

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

    private async Task ChangeKindAsync()
    {
        _equipmentId = string.Empty;
        _duplicate = true;
        _customCameraModel = string.Empty;
        _acknowledgeCustomModel = false;
        await LoadEquipmentAsync();
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
}
