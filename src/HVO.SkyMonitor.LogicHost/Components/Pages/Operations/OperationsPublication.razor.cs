using System.Security.Claims;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.LogicHost.Components.Pages.Operations;

public partial class OperationsPublication : ComponentBase
{
    [Parameter] public Guid ObservatoryId { get; set; }
    [Inject] internal IObservatoryPublicationService Publication { get; set; } = default!;
    [Inject] internal IPublicRecordPublicationService RecordPublication { get; set; } = default!;
    [Inject] internal ILogicalCameraService LogicalCameras { get; set; } = default!;
    [Inject] internal INetworkOperationsReadService Operations { get; set; } = default!;
    [CascadingParameter] internal Task<AuthenticationState> AuthenticationStateTask { get; set; } = default!;
    internal bool IsLoading { get; private set; } = true;
    internal bool IsBusy { get; private set; }
    internal bool IsLoadingMoreCameras { get; private set; }
    internal bool IsLoadingMoreRegistrations { get; private set; }
    internal string? StatusMessage { get; private set; }
    internal ObservatoryPublicationSettings? Settings { get; private set; }
    internal OperationsObservatoryDetail? Detail { get; private set; }
    internal string PublicSlug { get; set; } = string.Empty;
    internal string PublicDisplayName { get; set; } = string.Empty;
    internal string PublicDescription { get; set; } = string.Empty;
    internal bool IsPublic { get; set; }
    internal bool PublishEnvironmentalSummary { get; set; }
    internal bool AllowAutomaticEvents { get; set; }
    internal ObservatoryLocationDisclosureLevel DisclosureLevel { get; set; }
    internal string? RegionCode { get; set; }
    internal string? RegionLabel { get; set; }
    internal double PrecisionMeters { get; set; } = 25_000;
    internal string NewCameraSlug { get; set; } = string.Empty;
    internal string NewCameraName { get; set; } = string.Empty;
    internal string NewCameraDescription { get; set; } = string.Empty;
    private string? actorUserId;

    protected override async Task OnParametersSetAsync()
    {
        actorUserId = CentralArtifactCredentialAccess.GetOwnerId((await AuthenticationStateTask).User);
        await LoadAsync();
        IsLoading = false;
    }

    internal async Task SaveProfileAsync()
    {
        if (actorUserId is null || IsBusy) return;
        IsBusy = true;
        var result = await Publication.SetProfileAsync(ObservatoryId, actorUserId,
            new ObservatoryPublicationProfileRequest(
                PublicSlug,
                PublicDisplayName,
                PublicDescription,
                IsPublic ? ObservatoryProfileVisibility.Public : ObservatoryProfileVisibility.Private,
                PublishEnvironmentalSummary,
                AllowAutomaticEvents,
                "owner-ui"));
        StatusMessage = PublicationMessage(result.Outcome);
        await LoadAsync();
        IsBusy = false;
    }

    internal async Task SaveLocationAsync()
    {
        if (actorUserId is null || IsBusy) return;
        IsBusy = true;
        var result = await Publication.SetLocationDisclosureAsync(ObservatoryId, actorUserId,
            new ObservatoryLocationDisclosureRequest(
                DisclosureLevel,
                DisclosureLevel == ObservatoryLocationDisclosureLevel.Hidden ? null : RegionCode,
                DisclosureLevel == ObservatoryLocationDisclosureLevel.Hidden ? null : RegionLabel,
                null,
                null,
                DisclosureLevel == ObservatoryLocationDisclosureLevel.Approximate ? PrecisionMeters : null,
                "owner-ui"));
        StatusMessage = PublicationMessage(result.Outcome);
        await LoadAsync();
        IsBusy = false;
    }

    internal async Task CreateCameraAsync()
    {
        if (actorUserId is null || IsBusy) return;
        IsBusy = true;
        var result = await LogicalCameras.CreateAsync(
            ObservatoryId, actorUserId, NewCameraSlug, NewCameraName, NewCameraDescription);
        StatusMessage = CameraMessage(result.Outcome);
        if (result.Outcome == LogicalCameraMutationOutcome.Applied)
        {
            NewCameraSlug = NewCameraName = NewCameraDescription = string.Empty;
        }
        await LoadAsync();
        IsBusy = false;
    }

    internal async Task AssignAsync(Guid logicalCameraId, string? registrationValue)
    {
        if (actorUserId is null || IsBusy || !Guid.TryParse(registrationValue, out var registrationId)) return;
        IsBusy = true;
        var result = await LogicalCameras.AssignInstallationAsync(
            logicalCameraId, registrationId, actorUserId, "owner-ui-assignment");
        StatusMessage = CameraMessage(result.Outcome);
        await LoadAsync();
        IsBusy = false;
    }

    internal async Task LoadMoreCamerasAsync()
    {
        if (actorUserId is null || Detail?.CamerasNextCursor is not { } cursor || IsLoadingMoreCameras) return;
        IsLoadingMoreCameras = true;
        var page = await Operations.ListObservatoryCamerasAsync(actorUserId, ObservatoryId, 50, cursor);
        Detail = Detail with
        {
            Cameras = Detail.Cameras.Concat(page.Items).ToArray(),
            CamerasNextCursor = page.NextCursor
        };
        IsLoadingMoreCameras = false;
    }

    internal async Task LoadMoreRegistrationsAsync()
    {
        if (actorUserId is null || Detail?.RegistrationsNextCursor is not { } cursor || IsLoadingMoreRegistrations) return;
        IsLoadingMoreRegistrations = true;
        var page = await Operations.ListObservatoryRegistrationsAsync(actorUserId, ObservatoryId, 50, cursor);
        Detail = Detail with
        {
            Registrations = Detail.Registrations.Concat(page.Items).ToArray(),
            RegistrationsNextCursor = page.NextCursor
        };
        IsLoadingMoreRegistrations = false;
    }

    internal async Task SetCameraReleaseAsync(Guid logicalCameraId, bool release)
    {
        if (actorUserId is null || IsBusy) return;
        IsBusy = true;
        var result = await RecordPublication.DecideAsync(
            ObservatoryId,
            actorUserId,
            new PublicRecordSubject(PublicRecordSubjectKind.LogicalCamera, logicalCameraId),
            release ? PublicationDecisionState.Released : PublicationDecisionState.Withdrawn,
            "public-camera-v1",
            release ? "owner-ui-release" : "owner-ui-withdrawal");
        StatusMessage = result.Outcome switch
        {
            PublicRecordPublicationOutcome.Applied => "The camera publication decision was recorded.",
            PublicRecordPublicationOutcome.Unchanged => "That camera publication decision is already current.",
            _ => "The camera is no longer eligible or Owner authority was removed."
        };
        await LoadAsync();
        IsBusy = false;
    }

    private async Task LoadAsync()
    {
        if (actorUserId is null)
        {
            Settings = null;
            Detail = null;
            return;
        }
        Settings = await Publication.GetSettingsAsync(ObservatoryId, actorUserId);
        Detail = await Operations.GetPublicationAuthorityAsync(actorUserId, ObservatoryId);
        if (Settings is null) return;
        PublicSlug = Settings.PublicSlug;
        PublicDisplayName = Settings.PublicDisplayName;
        PublicDescription = Settings.PublicDescription;
        IsPublic = Settings.ProfileVisibility == ObservatoryProfileVisibility.Public;
        PublishEnvironmentalSummary = Settings.PublishEnvironmentalSummary;
        AllowAutomaticEvents = Settings.AllowAutomaticVerifiedEventInclusion;
        DisclosureLevel = Settings.DisclosureLevel;
        RegionCode = Settings.RegionCode;
        RegionLabel = Settings.RegionLabel;
        PrecisionMeters = Settings.PublicPrecisionMeters ?? 25_000;
    }

    private static string PublicationMessage(ObservatoryPublicationMutationOutcome outcome) => outcome switch
    {
        ObservatoryPublicationMutationOutcome.Applied => "The publication setting was recorded.",
        ObservatoryPublicationMutationOutcome.Unchanged => "That publication setting is already current.",
        ObservatoryPublicationMutationOutcome.Conflict => "That public slug is already in use.",
        ObservatoryPublicationMutationOutcome.NotFoundOrDenied => "Owner authority is required or access was removed.",
        _ => "The publication setting is invalid."
    };

    private static string CameraMessage(LogicalCameraMutationOutcome outcome) => outcome switch
    {
        LogicalCameraMutationOutcome.Applied => "The logical-camera change was recorded.",
        LogicalCameraMutationOutcome.Unchanged => "That logical-camera state is already current.",
        LogicalCameraMutationOutcome.Conflict => "The slug, registration, or installation is already assigned.",
        LogicalCameraMutationOutcome.NotFoundOrDenied => "Owner authority is required or the record was not found.",
        _ => "The logical-camera request is invalid."
    };
}
