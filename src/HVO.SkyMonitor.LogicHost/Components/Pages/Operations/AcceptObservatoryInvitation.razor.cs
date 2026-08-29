using System.Security.Claims;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.LogicHost.Components.Pages.Operations;

public partial class AcceptObservatoryInvitation : ComponentBase
{
    [Parameter] public Guid InvitationId { get; set; }
    [Inject] internal IObservatoryInvitationService Invitations { get; set; } = default!;
    [CascadingParameter] internal Task<AuthenticationState> AuthenticationStateTask { get; set; } = default!;
    internal string AcceptanceToken { get; set; } = string.Empty;
    internal string? Message { get; private set; }
    internal bool Succeeded { get; private set; }
    internal bool IsBusy { get; private set; }

    internal async Task AcceptAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(AcceptanceToken)) return;
        IsBusy = true;
        var userId = CentralArtifactCredentialAccess.GetOwnerId((await AuthenticationStateTask).User);
        var outcome = userId is null
            ? ObservatoryInvitationMutationOutcome.NotFoundOrDenied
            : await Invitations.AcceptAsync(InvitationId, userId, AcceptanceToken.Trim());
        Succeeded = outcome == ObservatoryInvitationMutationOutcome.Applied;
        Message = outcome switch
        {
            ObservatoryInvitationMutationOutcome.Applied => "Invitation accepted. The observatory is now available in Network Operations.",
            ObservatoryInvitationMutationOutcome.Expired => "This invitation expired. Ask an Owner to create a new invitation.",
            ObservatoryInvitationMutationOutcome.Conflict => "The account already has a membership or the invitation conflicts with current state.",
            _ => "The invitation or token was not found."
        };
        AcceptanceToken = string.Empty;
        IsBusy = false;
    }

    internal async Task DeclineAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        var userId = CentralArtifactCredentialAccess.GetOwnerId((await AuthenticationStateTask).User);
        var outcome = userId is null
            ? ObservatoryInvitationMutationOutcome.NotFoundOrDenied
            : await Invitations.DeclineAsync(InvitationId, userId);
        Succeeded = outcome == ObservatoryInvitationMutationOutcome.Applied;
        Message = Succeeded ? "Invitation declined." : "The invitation was not found or is no longer pending.";
        IsBusy = false;
    }
}
