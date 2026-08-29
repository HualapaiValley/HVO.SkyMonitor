using System.Security.Claims;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.LogicHost.Components.Pages.Operations;

public partial class OperationsProcessing : ComponentBase
{
    private readonly List<OperationsJobSummary> items = [];
    private readonly List<OperationsObservatorySummary> observatories = [];
    private string? userId;
    [Inject] internal INetworkOperationsReadService Operations { get; set; } = default!;
    [Inject] internal INetworkOperationsMutationService Mutations { get; set; } = default!;
    [Inject] internal ICentralProcessingPolicyService ProcessingPolicy { get; set; } = default!;
    [CascadingParameter] internal Task<AuthenticationState> AuthenticationStateTask { get; set; } = default!;
    internal IReadOnlyList<OperationsJobSummary> Items => items;
    internal IReadOnlyList<OperationsObservatorySummary> Observatories => observatories;
    internal Guid? SelectedObservatoryId { get; set; }
    internal CentralProcessingPolicySummary? Policy { get; private set; }
    internal int? OverrideCloudThresholdMillionths { get; set; }
    internal string ValidationOverride { get; set; } = "inherit";
    internal string? NextCursor { get; private set; }
    internal bool IsBusy { get; private set; }
    internal string? StatusMessage { get; private set; }
    protected override async Task OnInitializedAsync()
    {
        userId = CentralArtifactCredentialAccess.GetOwnerId((await AuthenticationStateTask).User);
        if (userId is not null)
        {
            string? cursor = null;
            do
            {
                var page = await Operations.ListObservatoriesAsync(userId, 50, cursor);
                observatories.AddRange(page.Items);
                cursor = page.NextCursor;
            }
            while (cursor is not null);
            SelectedObservatoryId = observatories.FirstOrDefault()?.ObservatoryId;
            await ReloadScopeAsync();
        }
    }
    internal Task LoadMoreAsync() => LoadAsync(NextCursor);
    internal Task CancelAsync(Guid jobId) => MutateAsync(jobId, Mutations.CancelJobAsync);
    internal Task RequeueAsync(Guid jobId) => MutateAsync(jobId, Mutations.RequeueJobAsync);
    internal Task ReprocessAsync(Guid jobId) => MutateAsync(jobId, Mutations.ReprocessJobAsync);

    internal async Task SelectObservatoryAsync(ChangeEventArgs args)
    {
        SelectedObservatoryId = Guid.TryParse(args.Value?.ToString(), out var id) ? id : null;
        await ReloadScopeAsync();
    }

    internal async Task SavePolicyAsync()
    {
        if (userId is null || SelectedObservatoryId is null || IsBusy) return;
        IsBusy = true;
        try
        {
            var validation = ValidationOverride switch
            {
                "enabled" => true,
                "disabled" => false,
                _ => (bool?)null
            };
            var outcome = await ProcessingPolicy.SetAsync(
                SelectedObservatoryId.Value,
                userId,
                new CentralProcessingPolicyRequest(
                    OverrideCloudThresholdMillionths,
                    validation,
                    "owner-ui-processing-policy"));
            StatusMessage = outcome switch
            {
                CentralProcessingPolicyMutationOutcome.Applied => "The observatory processing override was activated for future jobs.",
                CentralProcessingPolicyMutationOutcome.Unchanged => "The processing policy was already current.",
                CentralProcessingPolicyMutationOutcome.NotFoundOrDenied => "Owner authority is required or access was removed.",
                _ => "The processing override is invalid for the current global configuration."
            };
            await LoadPolicyAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal async Task ClearPolicyAsync()
    {
        OverrideCloudThresholdMillionths = null;
        ValidationOverride = "inherit";
        await SavePolicyAsync();
    }

    private async Task MutateAsync(
        Guid jobId,
        Func<string, Guid, CancellationToken, Task<NetworkOperationsMutationOutcome>> mutation)
    {
        if (userId is null || IsBusy) return;
        IsBusy = true;
        var outcome = await mutation(userId, jobId, CancellationToken.None);
        StatusMessage = outcome switch
        {
            NetworkOperationsMutationOutcome.Applied => "The processing action was recorded.",
            NetworkOperationsMutationOutcome.InvalidState => "The job changed state or is not eligible for that action.",
            _ => "Manager authority is required or access was removed."
        };
        items.Clear();
        NextCursor = null;
        await LoadAsync(null);
        IsBusy = false;
    }
    private async Task LoadAsync(string? cursor)
    {
        if (userId is null || (cursor is null && items.Count > 0)) return;
        var page = await Operations.ListJobsAsync(userId, SelectedObservatoryId, 50, cursor);
        items.AddRange(page.Items);
        NextCursor = page.NextCursor;
    }

    private async Task ReloadScopeAsync()
    {
        items.Clear();
        NextCursor = null;
        Policy = null;
        await LoadPolicyAsync();
        await LoadAsync(null);
    }

    private async Task LoadPolicyAsync()
    {
        if (userId is null || SelectedObservatoryId is null) return;
        Policy = await ProcessingPolicy.GetAsync(SelectedObservatoryId.Value, userId);
        OverrideCloudThresholdMillionths = Policy?.OverrideCloudTransmissionThresholdMillionths;
        ValidationOverride = Policy?.OverrideCentralValidationEnabled switch
        {
            true => "enabled",
            false => "disabled",
            null => "inherit"
        };
    }
}
