using System.Globalization;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.ProcessingRunner.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface ICentralProcessingRunnerJobService
{
    Task<ProcessingRunnerClaim?> ClaimAsync(
        CentralProcessingRunnerContext runner,
        ProcessingRunnerClaimRequest request,
        CancellationToken cancellationToken);

    Task<ProcessingRunnerLeaseRenewalResponse> RenewAsync(
        CentralProcessingRunnerContext runner,
        Guid jobId,
        Guid leaseToken,
        CancellationToken cancellationToken);

    Task<ProcessingRunnerCompletionResponse> CompleteAsync(
        CentralProcessingRunnerContext runner,
        Guid jobId,
        ProcessingRunnerCompletionRequest request,
        IReadOnlyList<ReadOnlyMemory<byte>> payloads,
        CancellationToken cancellationToken);

    Task FailAsync(
        CentralProcessingRunnerContext runner,
        Guid jobId,
        ProcessingRunnerFailureRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// The runner-facing job operations. LogicHost keeps every durable transition: the claim reuses the derivative lease
/// engine scoped to the runner's eligible recipes and runs the shared pre-execution checks; completion rebuilds the
/// outcome and publishes through the shared pipeline; stale, duplicate, and foreign completions are rejected by the
/// lease filters exactly as in-process ones are.
/// </summary>
internal sealed partial class CentralProcessingRunnerJobService(
    ApplicationDbContext dbContext,
    ICentralProcessingRunnerRegistry registry,
    ICentralDerivativeRunnerLeaseService leaseService,
    ICentralDerivativeJobService jobService,
    ICentralDerivativeExecutionPipeline pipeline,
    ICentralDerivativeJobInputDescriber inputDescriber,
    IOptions<CentralProcessingRunnerOptions> options,
    CentralProcessingRunnerTelemetry telemetry,
    TimeProvider timeProvider,
    ILogger<CentralProcessingRunnerJobService> logger) : ICentralProcessingRunnerJobService
{
    private readonly CentralProcessingRunnerOptions _options = options.Value;

    public async Task<ProcessingRunnerClaim?> ClaimAsync(
        CentralProcessingRunnerContext runner,
        ProcessingRunnerClaimRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(request);
        if (request.JobClass != ProcessingRunnerJobClass.CentralRecipe)
        {
            telemetry.RecordClaim("rejected", "none", TimeSpan.Zero);
            Log.ClaimRejected(logger, runner.Runner.RunnerId, request.JobClass.ToString());
            throw CentralProcessingRunnerRejectedException.Create(
                ProcessingRunnerReasonCodes.JobClassNotClaimable,
                $"Job class '{request.JobClass}' is not claimable through LogicHost.");
        }
        // Staleness is enforced on the claim path itself, not only when health is polled.
        if (await registry.EnforceStalenessAsync(runner.Runner, cancellationToken).ConfigureAwait(false)
            != CentralProcessingRunnerStatus.Active)
        {
            telemetry.RecordClaim("stale", "none", TimeSpan.Zero);
            throw CentralProcessingRunnerRejectedException.Create(
                ProcessingRunnerReasonCodes.RegistrationStale, "The runner must heartbeat before claiming.");
        }
        if (runner.EligibleRecipes.Count == 0)
        {
            telemetry.RecordClaim("ineligible", "none", TimeSpan.Zero);
            return null;
        }
        // Inputs larger than the runner's transfer limit are excluded before leasing so an incompatible runner never
        // consumes an attempt on work another runner could execute.
        var (pool, poolMode) = ResolvePool(runner.Capabilities.Labels);
        var scope = CentralDerivativeClaimScope.Only(
            runner.EligibleRecipes, runner.Capabilities.MaxTransferBytes, pool, poolMode);
        // A session lock per runner id makes the advertised concurrency an atomic bound across concurrent claim
        // requests, including two processes that reuse one runner id.
        await using var capacityLock = await CentralObjectApplicationLock.AcquireAsync(
            dbContext, $"processing-runner-claim/{runner.Runner.RunnerId}", cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        var activeLeases = await dbContext.CentralDerivativeJobs.AsNoTracking().CountAsync(job =>
            job.Status == CentralDerivativeJobStatus.Leased
            && job.LeaseOwner == runner.Runner.RunnerId
            && job.LeaseExpiresAtUtc > now, cancellationToken).ConfigureAwait(false);
        if (activeLeases >= runner.Runner.MaxConcurrency)
        {
            telemetry.RecordClaim("saturated", "none", TimeSpan.Zero);
            Log.Saturated(logger, runner.Runner.RunnerId, activeLeases, runner.Runner.MaxConcurrency);
            return null;
        }
        for (var candidate = 0; candidate < _options.MaximumClaimCandidatesPerRequest; candidate++)
        {
            var started = timeProvider.GetTimestamp();
            var lease = await leaseService.ClaimNextAsync(
                runner.Runner.RunnerId, _options.LeaseDuration, scope, cancellationToken).ConfigureAwait(false);
            if (lease is null)
            {
                telemetry.RecordClaim("empty", "none", timeProvider.GetElapsedTime(started));
                return null;
            }
            var claim = await PrepareClaimAsync(runner, lease, cancellationToken).ConfigureAwait(false);
            if (claim is null)
            {
                telemetry.RecordClaim("resolved", lease.RecipeName, timeProvider.GetElapsedTime(started));
                continue;
            }
            telemetry.RecordClaim("claimed", lease.RecipeName, timeProvider.GetElapsedTime(started));
            telemetry.RecordInputBytes(claim.Inputs.Sum(input => input.PayloadLength));
            Log.Claimed(logger, runner.Runner.RunnerId, lease.JobId, lease.AttemptCount, lease.RecipeName,
                claim.Inputs.Count, claim.Inputs.Sum(input => input.PayloadLength));
            return claim;
        }
        return null;
    }

    public async Task<ProcessingRunnerLeaseRenewalResponse> RenewAsync(
        CentralProcessingRunnerContext runner,
        Guid jobId,
        Guid leaseToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _ = await leaseService.GetLeaseAsync(jobId, leaseToken, runner.Runner.RunnerId, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var renewed = await jobService.RenewLeaseAsync(jobId, leaseToken, _options.LeaseDuration, cancellationToken)
                .ConfigureAwait(false);
            telemetry.RecordRenewal("renewed");
            return new ProcessingRunnerLeaseRenewalResponse(renewed.LeaseExpiresAtUtc, timeProvider.GetUtcNow());
        }
        catch (CentralDerivativeLeaseCanceledException)
        {
            telemetry.RecordRenewal("canceled");
            throw;
        }
        catch (CentralDerivativeJobStateException)
        {
            telemetry.RecordRenewal("stale");
            throw;
        }
    }

    public async Task<ProcessingRunnerCompletionResponse> CompleteAsync(
        CentralProcessingRunnerContext runner,
        Guid jobId,
        ProcessingRunnerCompletionRequest request,
        IReadOnlyList<ReadOnlyMemory<byte>> payloads,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(payloads);
        var started = timeProvider.GetTimestamp();
        var lease = await leaseService.GetLeaseAsync(jobId, request.LeaseToken, runner.Runner.RunnerId, cancellationToken)
            .ConfigureAwait(false);
        // The lease itself is the authority for completion: eligibility can shrink through a placement change while
        // a lease granted under the previous placement is still executing, and that work must still publish.
        long productBytes = 0;
        foreach (var payload in payloads)
        {
            productBytes = checked(productBytes + payload.Length);
        }
        if (productBytes > _options.MaximumProductBytes || request.InputBytes < 0
            || request.ExecutionDuration < TimeSpan.Zero)
        {
            throw CentralProcessingRunnerRejectedException.Create(
                ProcessingRunnerReasonCodes.TransferTooLarge, "The completion exceeds the product size limit.");
        }
        ProcessingOutcome outcome;
        try
        {
            outcome = ProcessingRunnerProjection.ReconstructOutcome(request, payloads);
        }
        catch (ProcessingRunnerProtocolException exception)
        {
            telemetry.RecordCompletion("invalid", lease.RecipeName, timeProvider.GetElapsedTime(started), productBytes);
            throw CentralProcessingRunnerRejectedException.Create(exception.ReasonCode, exception.Message);
        }
        if (outcome.Status is not ProcessingOutcomeStatus.Produced && outcome.Products.Count != 0)
        {
            throw CentralProcessingRunnerRejectedException.Create(
                ProcessingRunnerReasonCodes.InvalidCompletion, "Only a produced outcome may carry products.");
        }
        if (outcome.Status is not ProcessingOutcomeStatus.Produced && string.IsNullOrWhiteSpace(outcome.ReasonCode))
        {
            throw CentralProcessingRunnerRejectedException.Create(
                ProcessingRunnerReasonCodes.InvalidCompletion, "A non-produced outcome requires a reason code.");
        }
        if (outcome.Products.Count != 0)
        {
            // Bind every product to the execution identity LogicHost derives from the frozen lease (recipe, normalized
            // options, selector, frozen annotation, auxiliary inputs); a runner cannot substitute a different but
            // internally consistent recipe for the leased one.
            var expectedIdentity = await ResolveExpectedProductIdentityAsync(lease, cancellationToken).ConfigureAwait(false);
            if (outcome.Products.Any(product => !string.Equals(
                    product.Recipe.IdentitySha256, expectedIdentity, StringComparison.OrdinalIgnoreCase)))
            {
                telemetry.RecordCompletion("invalid", lease.RecipeName, timeProvider.GetElapsedTime(started), productBytes);
                throw CentralProcessingRunnerRejectedException.Create(
                    ProcessingRunnerReasonCodes.RecipeIdentityMismatch,
                    "A product recipe identity does not match the leased execution request.");
            }
        }
        var result = await pipeline.PublishAsync(
            lease, outcome, request.InputBytes, request.ExecutionDuration, cancellationToken).ConfigureAwait(false);
        var elapsed = timeProvider.GetElapsedTime(started);
        var label = result.Status.ToString().ToLowerInvariant();
        telemetry.RecordCompletion(label, lease.RecipeName, elapsed, productBytes);
        Log.Completed(logger, runner.Runner.RunnerId, lease.JobId, lease.AttemptCount, lease.RecipeName,
            label, result.ReasonCode, productBytes, request.ExecutionDuration.TotalMilliseconds);
        return new ProcessingRunnerCompletionResponse(
            result.Status,
            result.ArtifactId is { } artifactId ? [artifactId] : [],
            result.ReasonCode);
    }

    public async Task FailAsync(
        CentralProcessingRunnerContext runner,
        Guid jobId,
        ProcessingRunnerFailureRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ReasonCode) || request.ReasonCode.Length > 128)
        {
            throw CentralProcessingRunnerRejectedException.Create(
                ProcessingRunnerReasonCodes.InvalidCompletion, "A failure requires a bounded reason code.");
        }
        var lease = await leaseService.GetLeaseAsync(jobId, request.LeaseToken, runner.Runner.RunnerId, cancellationToken)
            .ConfigureAwait(false);
        if (request.UnavailableArtifactId is { } unavailableArtifactId
            && request.ReasonCode.StartsWith("object.", StringComparison.Ordinal))
        {
            var input = lease.Inputs?.FirstOrDefault(candidate => candidate.ArtifactId == unavailableArtifactId);
            var centralArtifactId = input?.CentralArtifactId;
            if (centralArtifactId is null && lease.SourceArtifactId == unavailableArtifactId)
            {
                centralArtifactId = await dbContext.CentralDerivativeJobs.AsNoTracking()
                    .Where(job => job.Id == lease.JobId)
                    .Select(job => (Guid?)job.SourceCentralArtifactId)
                    .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            }
            if (centralArtifactId is { } resolvedId && resolvedId != Guid.Empty)
            {
                var artifact = await dbContext.CentralArtifacts.AsNoTracking()
                    .Where(candidate => candidate.Id == resolvedId)
                    .Select(candidate => new { candidate.Id, candidate.RowVersion })
                    .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                if (artifact is not null)
                {
                    await jobService.MarkInputUnavailableAsync(
                        lease.JobId, lease.LeaseToken, artifact.Id, artifact.RowVersion, request.ReasonCode,
                        quarantine: !string.Equals(request.ReasonCode, "object.missing", StringComparison.Ordinal),
                        cancellationToken).ConfigureAwait(false);
                    telemetry.RecordFailure(request.ReasonCode, lease.RecipeName);
                    Log.Failed(logger, runner.Runner.RunnerId, lease.JobId, lease.AttemptCount, lease.RecipeName,
                        request.ReasonCode, "input-unavailable", request.Message);
                    return;
                }
            }
        }
        await jobService.FailAsync(
            lease.JobId, lease.LeaseToken, request.ReasonCode, request.Retryable, cancellationToken).ConfigureAwait(false);
        telemetry.RecordFailure(request.ReasonCode, lease.RecipeName);
        Log.Failed(logger, runner.Runner.RunnerId, lease.JobId, lease.AttemptCount, lease.RecipeName,
            request.ReasonCode, request.Retryable ? "retryable" : "terminal", request.Message);
    }

    /// <summary>
    /// A runner joins a pool with the <c>pool:&lt;name&gt;</c> label (dedicated unless <c>pool-mode:reserved</c> is
    /// also present); without a pool label it serves the shared pool only.
    /// </summary>
    internal static (string? Pool, CentralDerivativeClaimPoolMode Mode) ResolvePool(IReadOnlyList<string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        var pool = labels.FirstOrDefault(label => label.StartsWith("pool:", StringComparison.Ordinal))?["pool:".Length..];
        if (string.IsNullOrWhiteSpace(pool) || !CentralProcessingEntitlementOptions.IsValidPool(pool))
        {
            return (null, CentralDerivativeClaimPoolMode.Shared);
        }
        var reserved = labels.Contains("pool-mode:reserved", StringComparer.Ordinal);
        return (pool, reserved ? CentralDerivativeClaimPoolMode.Reserved : CentralDerivativeClaimPoolMode.Dedicated);
    }

    private async Task<string> ResolveExpectedProductIdentityAsync(
        CentralDerivativeJobLease lease,
        CancellationToken cancellationToken)
    {
        var (options, selector, annotation, canonicalInputs) = CentralDerivativeJobExecutor.CreateFrozenRequestInputs(lease);
        var descriptions = await inputDescriber.DescribeAsync(lease, cancellationToken).ConfigureAwait(false);
        var (request, failure) = LogicHostRecipeExecutionAdapter.CreateRequest(
            descriptions.ProcessingInputs, lease.RecipeName, options, selector, lease.TargetVariant, annotation,
            canonicalInputs, reconstructPayloads: false);
        if (failure is not null || request is null)
        {
            throw CentralProcessingRunnerRejectedException.Create(
                ProcessingRunnerReasonCodes.InvalidCompletion, "The leased execution request can no longer be rebuilt.");
        }
        return BuiltInProcessingRecipes.CreateExecutionIdentity(
            request.RecipeName, request.Options, request.Input, request.Annotation, request.AuxiliaryInputs).IdentitySha256;
    }

    private async Task<ProcessingRunnerClaim?> PrepareClaimAsync(
        CentralProcessingRunnerContext runner,
        CentralDerivativeJobLease lease,
        CancellationToken cancellationToken)
    {
        var preparation = await pipeline.PrepareAsync(lease, cancellationToken).ConfigureAwait(false);
        if (preparation.Resolved is not null)
        {
            Log.Resolved(logger, runner.Runner.RunnerId, lease.JobId, lease.RecipeName,
                preparation.Resolved.Status.ToString(), preparation.Resolved.ReasonCode);
            return null;
        }
        if (preparation.InProcessOnly)
        {
            throw new InvalidOperationException(
                $"Recipe '{lease.RecipeName}' requires in-process execution and must never be runner-placed.");
        }
        CentralDerivativeJobInputDescriptions descriptions;
        try
        {
            descriptions = await inputDescriber.DescribeAsync(lease, cancellationToken).ConfigureAwait(false);
        }
        catch (CentralDerivativeInputRejectedException exception)
        {
            await jobService.FailAsync(
                lease.JobId, lease.LeaseToken, ProcessingReasonCodes.InvalidInput, retryable: false, cancellationToken)
                .ConfigureAwait(false);
            Log.InputRejected(logger, exception, runner.Runner.RunnerId, lease.JobId, lease.RecipeName);
            return null;
        }
        catch (CentralDerivativeJobStateException exception)
        {
            Log.InputRejected(logger, exception, runner.Runner.RunnerId, lease.JobId, lease.RecipeName);
            return null;
        }
        if (descriptions.ByteLength > runner.Capabilities.MaxTransferBytes)
        {
            // Unreachable when the claim scope excluded oversized inputs; kept as a defensive invariant.
            await jobService.FailAsync(
                lease.JobId, lease.LeaseToken, ProcessingRunnerReasonCodes.TransferTooLarge, retryable: true,
                cancellationToken).ConfigureAwait(false);
            Log.TransferTooLarge(logger, runner.Runner.RunnerId, lease.JobId, lease.RecipeName,
                descriptions.ByteLength, runner.Capabilities.MaxTransferBytes);
            return null;
        }
        var (request, failure) = LogicHostRecipeExecutionAdapter.CreateRequest(
            descriptions.ProcessingInputs,
            lease.RecipeName,
            preparation.Options,
            preparation.Selector!,
            lease.TargetVariant,
            preparation.Annotation,
            preparation.CanonicalInputs,
            reconstructPayloads: false);
        if (failure is not null || request is null)
        {
            _ = await pipeline.PublishAsync(
                lease, failure ?? ProcessingOutcome.TerminalFailure(ProcessingReasonCodes.InvalidInput), 0,
                TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
            return null;
        }
        var references = descriptions.References.ToDictionary(reference => reference.ArtifactId);
        var inputs = request.Inputs.Select(artifact =>
        {
            var reference = references[artifact.ArtifactId];
            return ProcessingRunnerProjection.ProjectArtifact(
                artifact,
                reference.DevicePublicId,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"/api/v1.0/devices/{reference.DevicePublicId:D}/artifacts/{reference.ArtifactId:D}/content"),
                reference.ByteLength,
                reference.ChecksumSha256);
        }).ToArray();
        var auxiliaryInputs = request.AuxiliaryInputs?
            .Select(ProcessingRunnerProjection.ProjectAuxiliaryInput)
            .ToArray();
        return new ProcessingRunnerClaim(
            ProcessingRunnerProtocol.Version,
            lease.JobId,
            ProcessingRunnerJobClass.CentralRecipe,
            lease.LeaseToken,
            lease.LeaseExpiresAtUtc,
            _options.RenewalInterval,
            lease.AttemptCount,
            lease.MaxAttempts,
            request.RecipeName,
            request.Options,
            request.Input,
            inputs,
            request.OutputVariant,
            request.Annotation,
            auxiliaryInputs,
            request.InputArtifactId,
            lease.RequestedRecipeIdentitySha256,
            lease.ExpectedRecipeIdentitySha256,
            new ProcessingRunnerJobCorrelation(
                lease.GraphExecutionId, lease.GraphNodeId, lease.TraceParent, lease.TraceState));
    }

    private static partial class Log
    {
        [LoggerMessage(2204, LogLevel.Information,
            "Processing runner claimed job: RunnerId={RunnerId}, JobId={JobId}, Attempt={Attempt}, Recipe={Recipe}, Inputs={Inputs}, InputBytes={InputBytes}")]
        public static partial void Claimed(
            ILogger logger, string runnerId, Guid jobId, int attempt, string recipe, int inputs, long inputBytes);

        [LoggerMessage(2205, LogLevel.Warning,
            "Processing runner claim rejected: RunnerId={RunnerId}, JobClass={JobClass}")]
        public static partial void ClaimRejected(ILogger logger, string runnerId, string jobClass);

        [LoggerMessage(2206, LogLevel.Information,
            "Processing runner claim resolved before execution: RunnerId={RunnerId}, JobId={JobId}, Recipe={Recipe}, Status={Status}, Reason={Reason}")]
        public static partial void Resolved(
            ILogger logger, string runnerId, Guid jobId, string recipe, string status, string? reason);

        [LoggerMessage(2207, LogLevel.Warning,
            "Processing runner claim input rejected: RunnerId={RunnerId}, JobId={JobId}, Recipe={Recipe}")]
        public static partial void InputRejected(
            ILogger logger, Exception exception, string runnerId, Guid jobId, string recipe);

        [LoggerMessage(2208, LogLevel.Warning,
            "Processing runner claim exceeds transfer limit: RunnerId={RunnerId}, JobId={JobId}, Recipe={Recipe}, InputBytes={InputBytes}, Limit={Limit}")]
        public static partial void TransferTooLarge(
            ILogger logger, string runnerId, Guid jobId, string recipe, long inputBytes, long limit);

        [LoggerMessage(2209, LogLevel.Information,
            "Processing runner completion: RunnerId={RunnerId}, JobId={JobId}, Attempt={Attempt}, Recipe={Recipe}, Outcome={Outcome}, Reason={Reason}, ProductBytes={ProductBytes}, ExecutionMilliseconds={ExecutionMilliseconds}")]
        public static partial void Completed(
            ILogger logger, string runnerId, Guid jobId, int attempt, string recipe, string outcome, string? reason,
            long productBytes, double executionMilliseconds);

        [LoggerMessage(2212, LogLevel.Information,
            "Processing runner claim refused at capacity: RunnerId={RunnerId}, ActiveLeases={ActiveLeases}, MaxConcurrency={MaxConcurrency}")]
        public static partial void Saturated(ILogger logger, string runnerId, int activeLeases, int maxConcurrency);

        [LoggerMessage(2210, LogLevel.Warning,
            "Processing runner failure: RunnerId={RunnerId}, JobId={JobId}, Attempt={Attempt}, Recipe={Recipe}, Reason={Reason}, Disposition={Disposition}, Message={Message}")]
        public static partial void Failed(
            ILogger logger, string runnerId, Guid jobId, int attempt, string recipe, string reason, string disposition,
            string? message);
    }
}
