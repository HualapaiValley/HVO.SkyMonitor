using System.Data;
using System.Diagnostics;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal enum CentralProcessingPolicyMutationOutcome
{
    Applied,
    Unchanged,
    NotFoundOrDenied,
    Invalid
}

internal sealed record CentralProcessingPolicyRequest(
    int? CloudTransmissionThresholdMillionths,
    bool? CentralValidationEnabled,
    string ReasonCode);

internal sealed record CentralProcessingPolicySummary(
    Guid ObservatoryId,
    string ObservatoryName,
    ObservatoryMembershipRole EffectiveRole,
    int GlobalCloudTransmissionThresholdMillionths,
    int EffectiveCloudTransmissionThresholdMillionths,
    int? OverrideCloudTransmissionThresholdMillionths,
    bool GlobalCentralValidationEnabled,
    bool EffectiveCentralValidationEnabled,
    bool? OverrideCentralValidationEnabled,
    int? Version);

internal interface ICentralProcessingPolicyService
{
    Task<CentralProcessingPolicySummary?> GetAsync(
        Guid observatoryId,
        string actorUserId,
        CancellationToken cancellationToken = default);

    Task<CentralProcessingPolicyMutationOutcome> SetAsync(
        Guid observatoryId,
        string actorUserId,
        CentralProcessingPolicyRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CentralDerivativeRecipe>> ResolveRequiredRecipesAsync(
        Guid observatoryId,
        FrameArtifactRole sourceRole,
        CancellationToken cancellationToken);

    Task<CentralDerivativeRecipe?> ResolveTransientRecipeAsync(
        Guid observatoryId,
        FrameArtifactRole sourceRole,
        CancellationToken cancellationToken);
}

internal sealed class CentralProcessingPolicyService(
    ApplicationDbContext dbContext,
    ICentralDerivativeRecipeCatalog recipeCatalog,
    TimeProvider timeProvider,
    ILogger<CentralProcessingPolicyService> logger,
    OperatorUiTelemetry telemetry) : ICentralProcessingPolicyService
{
    private static readonly int GlobalCloudThreshold = new CloudAssessmentOptions()
        .TransmissionThresholdMillionths;

    public async Task<CentralProcessingPolicySummary?> GetAsync(
        Guid observatoryId,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        var membership = await ObservatoryMembershipAccess.ForUser(dbContext, actorUserId)
            .Where(item => item.ObservatoryId == observatoryId)
            .Select(item => new { item.Role, ObservatoryName = item.Observatory!.Name })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (membership is null) return null;
        var current = await Current(observatoryId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var globalValidation = GlobalValidationEnabled();
        return new(
            observatoryId,
            membership.ObservatoryName,
            membership.Role,
            GlobalCloudThreshold,
            current?.CloudTransmissionThresholdMillionths ?? GlobalCloudThreshold,
            current?.CloudTransmissionThresholdMillionths,
            globalValidation,
            globalValidation && current?.CentralValidationEnabled is not false,
            current?.CentralValidationEnabled,
            current?.Version);
    }

    public async Task<CentralProcessingPolicyMutationOutcome> SetAsync(
        Guid observatoryId,
        string actorUserId,
        CentralProcessingPolicyRequest request,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        using var activity = OperatorUiTelemetry.StartMutation("processing-policy");
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        ArgumentNullException.ThrowIfNull(request);
        var reasonCode = request.ReasonCode.Trim();
        if (reasonCode.Length is < 1 or > 128
            || request.CloudTransmissionThresholdMillionths is <= 0 or >= 1_000_000
            || request.CentralValidationEnabled is true && !GlobalValidationEnabled())
        {
            telemetry.RecordMutation("processing-policy", "invalid", "owner", Stopwatch.GetElapsedTime(started));
            return CentralProcessingPolicyMutationOutcome.Invalid;
        }
        var isRelational = dbContext.Database.IsRelational();
        await using var transaction = isRelational
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false)
            : null;
        if (!await LockOwnedObservatoryAsync(observatoryId, actorUserId, isRelational, cancellationToken)
            .ConfigureAwait(false))
        {
            telemetry.RecordMutation("processing-policy", "denied", "owner", Stopwatch.GetElapsedTime(started));
            return CentralProcessingPolicyMutationOutcome.NotFoundOrDenied;
        }
        var current = await Current(observatoryId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (current?.CloudTransmissionThresholdMillionths == request.CloudTransmissionThresholdMillionths
            && current?.CentralValidationEnabled == request.CentralValidationEnabled
            || current is null && request.CloudTransmissionThresholdMillionths is null
                && request.CentralValidationEnabled is null)
        {
            telemetry.RecordMutation("processing-policy", "unchanged", "owner", Stopwatch.GetElapsedTime(started));
            return CentralProcessingPolicyMutationOutcome.Unchanged;
        }
        var now = timeProvider.GetUtcNow();
        if (current is not null) current.SupersededAtUtc = now;
        dbContext.CentralProcessingOverrideVersions.Add(new CentralProcessingOverrideVersion
        {
            ObservatoryId = observatoryId,
            Version = (current?.Version ?? 0) + 1,
            CloudTransmissionThresholdMillionths = request.CloudTransmissionThresholdMillionths,
            CentralValidationEnabled = request.CentralValidationEnabled,
            EffectiveFromUtc = now,
            ActorUserId = actorUserId,
            ReasonCode = reasonCode
        });
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        telemetry.RecordMutation("processing-policy", "applied", "owner", Stopwatch.GetElapsedTime(started));
        OperatorUiAuditLog.CentralOverride(logger, "processing-policy", "applied");
        return CentralProcessingPolicyMutationOutcome.Applied;
    }

    public async Task<IReadOnlyList<CentralDerivativeRecipe>> ResolveRequiredRecipesAsync(
        Guid observatoryId,
        FrameArtifactRole sourceRole,
        CancellationToken cancellationToken)
    {
        var recipes = recipeCatalog.GetRequiredRecipes(sourceRole);
        var current = await Current(observatoryId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (current is null) return recipes;
        IEnumerable<CentralDerivativeRecipe> resolved = recipes;
        if (current.CentralValidationEnabled is false)
        {
            resolved = resolved.Where(recipe => recipe.Transient is null);
        }
        if (current.CloudTransmissionThresholdMillionths is { } threshold)
        {
            resolved = resolved.Select(recipe => ApplyCloudThreshold(recipe, threshold));
        }
        return resolved.ToArray();
    }

    public async Task<CentralDerivativeRecipe?> ResolveTransientRecipeAsync(
        Guid observatoryId,
        FrameArtifactRole sourceRole,
        CancellationToken cancellationToken)
    {
        var current = await Current(observatoryId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return current?.CentralValidationEnabled is false ? null : recipeCatalog.GetTransientRecipe(sourceRole);
    }

    private static CentralDerivativeRecipe ApplyCloudThreshold(CentralDerivativeRecipe recipe, int threshold)
    {
        if (!string.Equals(recipe.RecipeName, BuiltInProcessingRecipes.CloudAssessment, StringComparison.Ordinal))
        {
            return recipe;
        }
        var options = CaptureContractJson.SerializeToElement(new CloudAssessmentOptions(
            TransmissionThresholdMillionths: threshold));
        var identity = BuiltInProcessingRecipes.CreateRequestedIdentity(
            recipe.RecipeName,
            options,
            recipe.InputSelector);
        return recipe with { Options = options, RequestedRecipeIdentitySha256 = identity.IdentitySha256 };
    }

    private bool GlobalValidationEnabled()
        => recipeCatalog.GetTransientRecipe(FrameArtifactRole.Raw) is not null
            || recipeCatalog.GetTransientRecipe(FrameArtifactRole.Calibrated) is not null;

    private IQueryable<CentralProcessingOverrideVersion> Current(Guid observatoryId)
        => dbContext.CentralProcessingOverrideVersions.Where(item =>
            item.ObservatoryId == observatoryId && item.SupersededAtUtc == null);

    private async Task<bool> LockOwnedObservatoryAsync(
        Guid observatoryId,
        string actorUserId,
        bool isRelational,
        CancellationToken cancellationToken)
    {
        var observatoryExists = isRelational
            ? await dbContext.Observatories.FromSqlInterpolated($"""
                    SELECT * FROM [Observatories] WITH (UPDLOCK, HOLDLOCK)
                    WHERE [Id] = {observatoryId}
                    """)
                .AsNoTracking().AnyAsync(cancellationToken).ConfigureAwait(false)
            : await dbContext.Observatories.AnyAsync(item => item.Id == observatoryId, cancellationToken)
                .ConfigureAwait(false);
        return observatoryExists && await ObservatoryMembershipAccess.ForOwner(dbContext, actorUserId)
            .AnyAsync(item => item.ObservatoryId == observatoryId, cancellationToken).ConfigureAwait(false);
    }
}
