using System.Security.Claims;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.LogicHost.Components.Pages.Operations;

public partial class OperationsEvents : ComponentBase
{
    private readonly List<CentralTransientEventSummary> items = [];
    private readonly Dictionary<Guid, List<CentralTransientPublicationAuthority>> publicationAuthorities = [];
    private ClaimsPrincipal? principal;
    [Inject] internal ICentralTransientEventReadService Events { get; set; } = default!;
    [Inject] internal ICentralTransientReviewService Reviews { get; set; } = default!;
    [Inject] internal IPublicRecordPublicationService RecordPublication { get; set; } = default!;
    [CascadingParameter] internal Task<AuthenticationState> AuthenticationStateTask { get; set; } = default!;
    internal IReadOnlyList<CentralTransientEventSummary> Items => items;
    internal string? NextCursor { get; private set; }
    internal bool IsLoading { get; private set; } = true;
    internal bool IsBusy { get; private set; }
    internal string? StatusMessage { get; private set; }
    internal IReadOnlySet<Guid> ReviewableEventIds { get; private set; } = new HashSet<Guid>();
    protected override async Task OnInitializedAsync()
    {
        principal = (await AuthenticationStateTask).User;
        await LoadAsync(null);
        IsLoading = false;
    }
    internal Task LoadMoreAsync() => LoadAsync(NextCursor);
    internal Task ConfirmAsync(CentralTransientEventSummary item)
        => ReviewAsync(item, TransientReviewDisposition.Confirmed, "operator-confirmed");

    internal Task RejectAsync(CentralTransientEventSummary item)
        => ReviewAsync(item, TransientReviewDisposition.Rejected, "operator-rejected");

    internal IReadOnlyList<CentralTransientPublicationAuthority> PublicationAuthoritiesFor(Guid eventId)
        => publicationAuthorities.TryGetValue(eventId, out var authorities) ? authorities : [];

    internal async Task SetEventReleaseAsync(
        CentralTransientEventSummary item,
        CentralTransientPublicationAuthority authority,
        bool release)
    {
        var actorUserId = principal is null ? null : CentralArtifactCredentialAccess.GetOwnerId(principal);
        if (actorUserId is null || IsBusy) return;
        IsBusy = true;
        try
        {
            var result = await RecordPublication.DecideAsync(
                authority.ObservatoryId,
                actorUserId,
                new PublicRecordSubject(
                    PublicRecordSubjectKind.TransientEvent,
                    item.CentralTransientEventId,
                    item.LatestEventVersionId),
                release ? PublicationDecisionState.Released : PublicationDecisionState.Withdrawn,
                "public-event-v1",
                release ? "owner-ui-release" : "owner-ui-withdrawal");
            StatusMessage = result.Outcome switch
            {
                PublicRecordPublicationOutcome.Applied => release
                    ? $"{authority.ObservatoryName} now contributes to the public event."
                    : $"{authority.ObservatoryName}'s public event contribution was withdrawn.",
                PublicRecordPublicationOutcome.Unchanged => "The event publication state was already current.",
                PublicRecordPublicationOutcome.NotFoundOrDenied => "Owner access or event eligibility changed.",
                PublicRecordPublicationOutcome.Conflict => "The event publication state changed. Reload and try again.",
                _ => "The event publication request was invalid."
            };
            if (result.Outcome is PublicRecordPublicationOutcome.Applied or PublicRecordPublicationOutcome.Unchanged)
            {
                await ReloadAsync();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReviewAsync(
        CentralTransientEventSummary item,
        TransientReviewDisposition disposition,
        string reasonCode)
    {
        if (principal is null || IsBusy || !CentralTransientEventEtag.TryParse(item.ETag, out var rowVersion)) return;
        IsBusy = true;
        var result = await Reviews.ReviewAsync(
            principal,
            item.CentralTransientEventId,
            rowVersion,
            Guid.NewGuid().ToString("N"),
            new CentralTransientReviewRequest(item.ActiveAssessmentId, disposition, null, [reasonCode]),
            CancellationToken.None);
        StatusMessage = result.Status switch
        {
            CentralTransientReviewMutationStatus.Applied => "The event review was recorded.",
            CentralTransientReviewMutationStatus.PreconditionFailed => "The event changed; reload and review the current assessment.",
            CentralTransientReviewMutationStatus.NotFound => "Manager authority is required for every contributing observatory.",
            _ => "The event is not eligible for that review action."
        };
        items.Clear();
        publicationAuthorities.Clear();
        NextCursor = null;
        await LoadAsync(null);
        IsBusy = false;
    }
    private async Task LoadAsync(string? cursor)
    {
        if (principal is null || (cursor is null && items.Count > 0)) return;
        var page = await Events.ListAsync(principal, 50, cursor, CancellationToken.None);
        items.AddRange(page.Items);
        NextCursor = page.NextCursor;
        var reviewable = new HashSet<Guid>(ReviewableEventIds);
        reviewable.UnionWith(await Reviews.ListReviewableEventIdsAsync(
            principal,
            page.Items.Select(item => item.CentralTransientEventId).ToArray(),
            CancellationToken.None));
        ReviewableEventIds = reviewable;
        var authorities = await Events.ListPublicationAuthoritiesAsync(
            principal,
            page.Items.Select(item => item.CentralTransientEventId).ToArray(),
            CancellationToken.None);
        foreach (var authority in authorities)
        {
            if (!publicationAuthorities.TryGetValue(authority.CentralTransientEventId, out var values))
            {
                values = [];
                publicationAuthorities.Add(authority.CentralTransientEventId, values);
            }
            values.Add(authority);
        }
    }

    private async Task ReloadAsync()
    {
        items.Clear();
        publicationAuthorities.Clear();
        ReviewableEventIds = new HashSet<Guid>();
        NextCursor = null;
        await LoadAsync(null);
    }
}
