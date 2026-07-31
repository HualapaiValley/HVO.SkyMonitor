using System.Security.Claims;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.LogicHost.Components.Pages.Operations;

public partial class OperationsMembers : ComponentBase
{
    private readonly List<ObservatoryMembershipSummary> members = [];
    private readonly List<ObservatoryInvitationSummary> pendingInvitations = [];
    private string? actorUserId;
    [Parameter] public Guid ObservatoryId { get; set; }
    [Inject] internal IObservatoryMembershipService Memberships { get; set; } = default!;
    [Inject] internal IObservatoryInvitationService Invitations { get; set; } = default!;
    [CascadingParameter] internal Task<AuthenticationState> AuthenticationStateTask { get; set; } = default!;
    internal IReadOnlyList<ObservatoryMembershipSummary> Members => members;
    internal IReadOnlyList<ObservatoryInvitationSummary> PendingInvitations => pendingInvitations;
    internal string? MembersNextCursor { get; private set; }
    internal string? InvitationsNextCursor { get; private set; }
    internal bool IsLoadingMoreMembers { get; private set; }
    internal bool IsLoadingMoreInvitations { get; private set; }
    internal string TargetUserId { get; set; } = string.Empty;
    internal ObservatoryMembershipRole OfferedRole { get; set; } = ObservatoryMembershipRole.Viewer;
    internal bool IsBusy { get; private set; }
    internal string? ErrorMessage { get; private set; }
    internal string? AcceptanceToken { get; private set; }
    protected override async Task OnParametersSetAsync()
    {
        actorUserId = (await AuthenticationStateTask).User.FindFirstValue(ClaimTypes.NameIdentifier);
        await LoadAsync();
    }
    internal async Task IssueAsync()
    {
        if (actorUserId is null || string.IsNullOrWhiteSpace(TargetUserId) || IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        AcceptanceToken = null;
        var result = await Invitations.IssueAsync(
            ObservatoryId, actorUserId, TargetUserId.Trim(), OfferedRole, TimeSpan.FromDays(7));
        if (result.Outcome == ObservatoryInvitationMutationOutcome.Applied)
        {
            AcceptanceToken = result.AcceptanceToken;
            TargetUserId = string.Empty;
            await LoadAsync();
        }
        else
        {
            ErrorMessage = result.Outcome switch
            {
                ObservatoryInvitationMutationOutcome.Conflict => "That user is already a member or has an active invitation.",
                ObservatoryInvitationMutationOutcome.NotFoundOrDenied => "The user or observatory was not found, or Owner authority is required.",
                _ => "The invitation request is invalid."
            };
        }
        IsBusy = false;
    }

    internal async Task SetRoleAsync(string targetUserId, ObservatoryMembershipRole role)
    {
        if (actorUserId is null || IsBusy) return;
        IsBusy = true;
        var result = await Memberships.SetRoleAsync(ObservatoryId, actorUserId, targetUserId, role);
        ErrorMessage = MembershipMessage(result.Outcome);
        await LoadAsync();
        IsBusy = false;
    }

    internal async Task RemoveAsync(string targetUserId)
    {
        if (actorUserId is null || IsBusy) return;
        IsBusy = true;
        var result = await Memberships.RemoveAsync(ObservatoryId, actorUserId, targetUserId);
        ErrorMessage = MembershipMessage(result.Outcome);
        await LoadAsync();
        IsBusy = false;
    }

    internal async Task RevokeAsync(Guid invitationId)
    {
        if (actorUserId is null || IsBusy) return;
        IsBusy = true;
        var result = await Invitations.RevokeAsync(invitationId, actorUserId);
        ErrorMessage = result == ObservatoryInvitationMutationOutcome.Applied
            ? null
            : "The invitation was already completed, expired, or access was removed.";
        await LoadAsync();
        IsBusy = false;
    }

    internal async Task LoadMoreMembersAsync()
    {
        if (actorUserId is null || MembersNextCursor is null || IsLoadingMoreMembers) return;
        IsLoadingMoreMembers = true;
        var page = await Memberships.ListAsync(ObservatoryId, actorUserId, 50, MembersNextCursor);
        members.AddRange(page.Items);
        MembersNextCursor = page.NextCursor;
        IsLoadingMoreMembers = false;
    }

    internal async Task LoadMoreInvitationsAsync()
    {
        if (actorUserId is null || InvitationsNextCursor is null || IsLoadingMoreInvitations) return;
        IsLoadingMoreInvitations = true;
        var page = await Invitations.ListPendingAsync(ObservatoryId, actorUserId, 50, InvitationsNextCursor);
        pendingInvitations.AddRange(page.Items);
        InvitationsNextCursor = page.NextCursor;
        IsLoadingMoreInvitations = false;
    }

    private async Task LoadAsync()
    {
        members.Clear();
        pendingInvitations.Clear();
        MembersNextCursor = null;
        InvitationsNextCursor = null;
        if (actorUserId is null)
        {
            return;
        }
        var memberPage = await Memberships.ListAsync(ObservatoryId, actorUserId, 50, null);
        members.AddRange(memberPage.Items);
        MembersNextCursor = memberPage.NextCursor;
        var invitationPage = await Invitations.ListPendingAsync(ObservatoryId, actorUserId, 50, null);
        pendingInvitations.AddRange(invitationPage.Items);
        InvitationsNextCursor = invitationPage.NextCursor;
    }

    private static string? MembershipMessage(ObservatoryMembershipMutationOutcome outcome) => outcome switch
    {
        ObservatoryMembershipMutationOutcome.Applied or ObservatoryMembershipMutationOutcome.Unchanged => null,
        ObservatoryMembershipMutationOutcome.LastOwner => "The final Owner cannot be removed or downgraded.",
        ObservatoryMembershipMutationOutcome.TargetUserNotFound => "The registered user was not found.",
        ObservatoryMembershipMutationOutcome.MemberNotFound => "That user is no longer a member.",
        _ => "Owner authority is required or access was removed."
    };
}
