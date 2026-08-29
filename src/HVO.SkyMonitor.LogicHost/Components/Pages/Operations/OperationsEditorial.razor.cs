using System.Security.Claims;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.LogicHost.Components.Pages.Operations;

public partial class OperationsEditorial : ComponentBase
{
    [Inject] internal IPublicNetworkReadService PublicNetwork { get; set; } = default!;
    [Inject] internal ICuratedPublicPlacementService Curation { get; set; } = default!;
    [CascadingParameter] internal Task<AuthenticationState> AuthenticationStateTask { get; set; } = default!;
    internal bool IsLoading { get; private set; } = true;
    internal string? StatusMessage { get; private set; }
    internal IReadOnlyList<PublicObservatorySummary> Observatories { get; private set; } = [];
    internal IReadOnlyList<PublicEventSummary> Events { get; private set; } = [];
    private string? actorUserId;

    protected override async Task OnInitializedAsync()
    {
        actorUserId = CentralArtifactCredentialAccess.GetOwnerId((await AuthenticationStateTask).User);
        Observatories = (await PublicNetwork.ListObservatoriesAsync(50, null)).Items;
        Events = (await PublicNetwork.ListEventsAsync(50, null)).Items;
        IsLoading = false;
    }

    internal async Task DecideObservatoryAsync(string slug, CuratedPlacementState state)
    {
        if (actorUserId is null) return;
        var outcome = await Curation.DecideObservatoryAsync(
            actorUserId, slug, state, state == CuratedPlacementState.Featured ? 0 : null, "platform-editor-ui");
        StatusMessage = Message(outcome);
    }

    internal async Task DecideEventAsync(Guid publicId, CuratedPlacementState state)
    {
        if (actorUserId is null) return;
        var outcome = await Curation.DecideEventAsync(
            actorUserId, publicId, state, state == CuratedPlacementState.Featured ? 0 : null, "platform-editor-ui");
        StatusMessage = Message(outcome);
    }

    private static string Message(CuratedPlacementOutcome outcome) => outcome switch
    {
        CuratedPlacementOutcome.Applied => "The public placement decision was recorded.",
        CuratedPlacementOutcome.Unchanged => "That public placement is already current.",
        CuratedPlacementOutcome.NotFoundOrDenied => "The record is no longer eligible or editor authority was removed.",
        _ => "The placement request was invalid."
    };
}
