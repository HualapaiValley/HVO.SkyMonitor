using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface ICentralDerivativeWindowResolver
{
    Task ResolveAffectedAsync(CentralArtifact artifact, DateTimeOffset now, CancellationToken cancellationToken);

    Task ResolveWaitingAsync(DateTimeOffset now, CancellationToken cancellationToken);

    Task ResolveAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken);
}

internal sealed partial class CentralDerivativeWindowResolver(
    ApplicationDbContext dbContext,
    CentralDerivativeWorkerTelemetry telemetry,
    TimeProvider timeProvider,
    ILogger<CentralDerivativeWindowResolver> logger) : ICentralDerivativeWindowResolver
{
    private const int ResolutionBatchSize = 100;
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public async Task ResolveAffectedAsync(
        CentralArtifact artifact,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.Frame is not { CaptureSequence: { } captureSequence } frame)
        {
            return;
        }
        var jobIds = await dbContext.CentralDerivativeJobInputRequirements.AsNoTracking()
            .Where(requirement => requirement.Job!.Status == CentralDerivativeJobStatus.Waiting
                && requirement.ExpectedAgentId == frame.AgentId
                && requirement.ExpectedCaptureSequence == captureSequence)
            .Select(requirement => requirement.CentralDerivativeJobId)
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        telemetry.RecordWindowNotification(
            BuiltInProcessingRecipes.RollingMean, jobIds.Count == 0 ? "ignored" : "affected");
        foreach (var jobId in jobIds)
        {
            await ResolveJobAsync(jobId, now, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ResolveWaitingAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var jobIds = await dbContext.CentralDerivativeJobs.AsNoTracking()
            .Where(job => job.Status == CentralDerivativeJobStatus.Waiting)
            .OrderBy(job => job.UpdatedAtUtc)
            .ThenBy(job => job.ResolutionDeadlineUtc)
            .ThenBy(job => job.CreatedAtUtc)
            .Select(job => job.Id)
            .Take(ResolutionBatchSize)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var jobId in jobIds)
        {
            await ResolveJobAsync(jobId, now, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task ResolveAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken)
        => ResolveJobAsync(jobId, now, cancellationToken);

    private async Task ResolveJobAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        for (var retry = 0; retry < 5; retry++)
        {
            try
            {
                await ResolveJobCoreAsync(jobId, now, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (exception.GetBaseException() is SqlException { Number: 1205 })
            {
                dbContext.ChangeTracker.Clear();
                await Task.Delay(TimeSpan.FromMilliseconds(10 * (retry + 1)), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        throw new CentralDerivativeJobStateException(
            "Window resolution could not acquire its durable locks after bounded deadlock retry.");
    }

    private async Task ResolveJobCoreAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        IDbContextTransaction? ownedTransaction = null;
        try
        {
            if (dbContext.Database.CurrentTransaction is null)
            {
                ownedTransaction = await dbContext.Database.BeginTransactionAsync(
                    IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
            }
            _ = await CentralDerivativeJobLock.AcquireAsync(dbContext, jobId, cancellationToken).ConfigureAwait(false);
            // The scheduler may already track this job from before it waited on the SQL lock.
            dbContext.ChangeTracker.Clear();
            var job = await dbContext.CentralDerivativeJobs
                .Include(item => item.SourceArtifact)!.ThenInclude(artifact => artifact!.Frame)
                .Include(item => item.InputRequirements)
                .Include(item => item.Inputs)
                .AsSplitQuery()
                .SingleOrDefaultAsync(item => item.Id == jobId, cancellationToken).ConfigureAwait(false);
            if (job is null || job.Status != CentralDerivativeJobStatus.Waiting)
            {
                if (ownedTransaction is not null)
                {
                    await ownedTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                }
                return;
            }
            using var activity = telemetry.StartWindowResolution(job.RecipeName, job.Id);
            var initialStatus = job.Status;
            var initialReason = job.StateReasonCode;

            var candidates = new Dictionary<Guid, ResolvedCandidate>();
            foreach (var requirement in job.InputRequirements.OrderBy(item => item.Ordinal))
            {
                if (requirement.SourceKind != CentralDerivativeInputSourceKind.Artifact)
                {
                    requirement.ResolutionState = CentralDerivativeInputResolutionState.Waiting;
                    requirement.ResolutionReasonCode = CentralDerivativeWindowReasonCodes.EnvironmentUnavailable;
                    continue;
                }
                try
                {
                    var candidate = await FindCandidateAsync(
                        requirement, job.ResolutionDeadlineUtc, cancellationToken).ConfigureAwait(false);
                    if (candidate is not null)
                    {
                        candidates[requirement.Id] = candidate;
                    }
                }
                catch (CentralDerivativeWindowAmbiguousInputException)
                {
                    requirement.ResolutionState = CentralDerivativeInputResolutionState.Incompatible;
                    requirement.ResolutionReasonCode = CentralDerivativeWindowReasonCodes.AmbiguousInput;
                }
            }

            var anchorRequirement = job.InputRequirements
                .Where(item => item.SequenceOffset == 0)
                .OrderByDescending(item => candidates.TryGetValue(item.Id, out var candidate)
                    && candidate.Artifact.Id == job.SourceCentralArtifactId)
                .ThenBy(item => item.Ordinal)
                .FirstOrDefault();
            var anchor = anchorRequirement is null || !candidates.TryGetValue(anchorRequirement.Id, out var value)
                ? null
                : value;
            var incompatibleRequired = false;
            foreach (var requirement in job.InputRequirements.OrderBy(item => item.Ordinal))
            {
                if (!candidates.TryGetValue(requirement.Id, out var candidate))
                {
                    if (requirement.ResolutionState == CentralDerivativeInputResolutionState.Incompatible)
                    {
                        incompatibleRequired |= requirement.IsRequired;
                        continue;
                    }
                    requirement.ResolutionState = CentralDerivativeInputResolutionState.Waiting;
                    requirement.ResolutionReasonCode = CentralDerivativeWindowReasonCodes.WaitingRequiredInput;
                    continue;
                }
                if (requirement.CompatibilityMode == CentralDerivativeCompatibilityMode.Exact
                    && anchor is not null
                    && !candidate.IsCompatibleWith(anchor))
                {
                    requirement.ResolutionState = CentralDerivativeInputResolutionState.Incompatible;
                    requirement.ResolutionReasonCode = candidate.LayoutJson != anchor.LayoutJson
                        ? CentralDerivativeWindowReasonCodes.IncompatibleLayout
                        : CentralDerivativeWindowReasonCodes.IncompatibleProfile;
                    incompatibleRequired |= requirement.IsRequired;
                    telemetry.RecordWindowRejection(
                        job.RecipeName,
                        candidate.LayoutJson != anchor.LayoutJson ? "layout" : "profile",
                        job.MissingInputOutcome?.ToString().ToLowerInvariant() ?? "skip");
                    continue;
                }
                requirement.ResolutionState = CentralDerivativeInputResolutionState.Resolved;
                requirement.ResolutionReasonCode = null;
            }

            var missingRequired = job.InputRequirements.Any(item => item.IsRequired
                && item.ResolutionState != CentralDerivativeInputResolutionState.Resolved);
            var unresolvedOptional = job.InputRequirements.Any(item => !item.IsRequired
                && item.ResolutionState == CentralDerivativeInputResolutionState.Waiting);
            var deadlineExpired = job.ResolutionDeadlineUtc <= now;
            var inputsPersisted = await PersistResolvedInputsAsync(job, candidates, now, cancellationToken)
                .ConfigureAwait(false);
            if (!inputsPersisted)
            {
                job.StateReasonCode = CentralDerivativeWindowReasonCodes.ResolutionConflict;
            }
            else if (incompatibleRequired)
            {
                CompleteWithoutExecution(job, now, CentralDerivativeWindowReasonCodes.IncompatibleInput);
            }
            else if (missingRequired || unresolvedOptional)
            {
                if (deadlineExpired)
                {
                    foreach (var requirement in job.InputRequirements.Where(item =>
                        item.ResolutionState == CentralDerivativeInputResolutionState.Waiting))
                    {
                        requirement.ResolutionState = CentralDerivativeInputResolutionState.Missing;
                        requirement.ResolutionReasonCode = requirement.IsRequired
                            ? CentralDerivativeWindowReasonCodes.RequiredInputTimeout
                            : CentralDerivativeWindowReasonCodes.OptionalInputTimeout;
                    }
                    if (job.MissingInputOutcome == CentralDerivativeWindowOutcome.Run && candidates.Count > 0)
                    {
                        await FreezeInputsAsync(job, candidates, now, cancellationToken).ConfigureAwait(false);
                        job.StateReasonCode = missingRequired
                            ? CentralDerivativeWindowReasonCodes.RequiredInputTimeout
                            : CentralDerivativeWindowReasonCodes.OptionalInputTimeout;
                    }
                    else
                    {
                        ApplyDeadlineOutcome(job, now, missingRequired);
                    }
                    telemetry.RecordWindowDeadline(
                        job.RecipeName, job.Status.ToString().ToLowerInvariant());
                }
                else
                {
                    if (!string.Equals(
                        job.StateReasonCode,
                        CentralDerivativeWindowReasonCodes.WaitingRequiredInput,
                        StringComparison.Ordinal))
                    {
                        job.StateReasonCode = CentralDerivativeWindowReasonCodes.WaitingRequiredInput;
                        job.UpdatedAtUtc = now;
                    }
                    else
                    {
                        // Rotate durable polling fairly when more than one resolver batch is waiting.
                        job.UpdatedAtUtc = now;
                    }
                }
            }
            else
            {
                await FreezeInputsAsync(job, candidates, now, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException exception)
            {
                var entities = string.Join(", ", exception.Entries.Select(entry => entry.Metadata.ClrType.Name));
                throw new CentralDerivativeJobStateException(
                    $"Window resolution lost optimistic concurrency for: {entities}.", exception);
            }
            if (ownedTransaction is not null)
            {
                await ownedTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                dbContext.ChangeTracker.Clear();
            }
            if (job.Status != initialStatus || !string.Equals(job.StateReasonCode, initialReason, StringComparison.Ordinal))
            {
                Log.Resolution(logger, job.Id, job.RecipeName, initialStatus.ToString(), job.Status.ToString(),
                    job.Inputs.Count, job.StateReasonCode ?? "none");
            }
            if ((job.Status is CentralDerivativeJobStatus.Skipped
                or CentralDerivativeJobStatus.Quarantined
                or CentralDerivativeJobStatus.TerminalFailure)
                && job.Inputs.Count > 0)
            {
                telemetry.RecordWindowPinDuration(
                    job.RecipeName,
                    now - job.Inputs.Min(input => input.SelectedAtUtc),
                    job.Status.ToString().ToLowerInvariant());
            }
            telemetry.RecordWindowResolution(
                job.RecipeName,
                job.Status.ToString().ToLowerInvariant(),
                timeProvider.GetElapsedTime(started),
                job.Inputs.Count,
                job.InputRequirements.Count,
                job.InputRequirements.Count(requirement => requirement.ResolutionState
                    is CentralDerivativeInputResolutionState.Missing
                    or CentralDerivativeInputResolutionState.Waiting),
                job.Inputs.Sum(input => input.ByteLength),
                now - job.SourceArtifact!.ReceivedAtUtc);
        }
        catch
        {
            if (ownedTransaction is not null)
            {
                await TryRollbackAsync(ownedTransaction).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            if (ownedTransaction is not null)
            {
                await ownedTransaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task TryRollbackAsync(IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // SQL Server already rolled back a deadlock victim.
        }
    }

    private async Task<ResolvedCandidate?> FindCandidateAsync(
        CentralDerivativeJobInputRequirement requirement,
        DateTimeOffset? resolutionDeadlineUtc,
        CancellationToken cancellationToken)
    {
        if (!requirement.ExpectedCaptureSequence.HasValue)
        {
            return null;
        }
        var selector = JsonSerializer.Deserialize<ProcessingInputSelector>(
            requirement.SelectorJson, SerializerOptions)
            ?? throw new CentralDerivativeJobStateException("A derivative input requirement selector is invalid.");
        // The frame update lock fences artifact inserts for this selection range until the input set is frozen.
        var lockedArtifactIds = selector.Variant is null
            ? await dbContext.Database.SqlQuery<Guid>($"""
                SELECT artifact.[Id] AS [Value]
                FROM [CentralFrames] AS frame WITH (UPDLOCK, HOLDLOCK)
                LEFT JOIN [CentralArtifacts] AS artifact WITH (UPDLOCK, HOLDLOCK)
                    ON artifact.[CentralFrameId] = frame.[Id]
                    AND artifact.[Role] = {selector.Role.ToString()}
                WHERE frame.[AgentId] = {requirement.ExpectedAgentId}
                    AND frame.[CaptureSequence] = {requirement.ExpectedCaptureSequence.Value}
                    AND (frame.[RigId] = {requirement.ExpectedRigId}
                        OR (frame.[RigId] IS NULL AND {requirement.ExpectedRigId} IS NULL))
                    AND artifact.[Id] IS NOT NULL
                """).ToListAsync(cancellationToken).ConfigureAwait(false)
            : await dbContext.Database.SqlQuery<Guid>($"""
                SELECT artifact.[Id] AS [Value]
                FROM [CentralFrames] AS frame WITH (UPDLOCK, HOLDLOCK)
                LEFT JOIN [CentralArtifacts] AS artifact WITH (UPDLOCK, HOLDLOCK)
                    ON artifact.[CentralFrameId] = frame.[Id]
                    AND artifact.[Role] = {selector.Role.ToString()}
                    AND artifact.[Variant] = {selector.Variant}
                WHERE frame.[AgentId] = {requirement.ExpectedAgentId}
                    AND frame.[CaptureSequence] = {requirement.ExpectedCaptureSequence.Value}
                    AND (frame.[RigId] = {requirement.ExpectedRigId}
                        OR (frame.[RigId] IS NULL AND {requirement.ExpectedRigId} IS NULL))
                    AND artifact.[Id] IS NOT NULL
                """).ToListAsync(cancellationToken).ConfigureAwait(false);
        var query = dbContext.CentralArtifacts.AsNoTracking()
            .Where(artifact => lockedArtifactIds.Contains(artifact.Id)
                && artifact.Frame!.AgentId == requirement.ExpectedAgentId
                && artifact.Frame.CaptureSequence == requirement.ExpectedCaptureSequence
                && artifact.Frame.RigId == requirement.ExpectedRigId
                && artifact.Role == selector.Role
                && (selector.Variant == null || artifact.Variant == selector.Variant)
                && artifact.ObjectState == CentralArtifactObjectState.Available
                && artifact.ReconstructionState == CentralReconstructionState.Complete);
        if (resolutionDeadlineUtc.HasValue)
        {
            query = query.Where(artifact => artifact.ReceivedAtUtc <= resolutionDeadlineUtc.Value);
        }
        if (selector.RecipeIdentitySha256 is not null)
        {
            query = query.Where(artifact => dbContext.CentralArtifactProcessingEvidence.Any(evidence =>
                evidence.CentralArtifactId == artifact.Id
                && evidence.RecipeIdentitySha256 == selector.RecipeIdentitySha256));
        }
        var artifacts = await query
            .Include(artifact => artifact.Layout)
            .Include(artifact => artifact.Recipe)
            .Include(artifact => artifact.Sources)
            .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Timing)
            .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Control)
            .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Profiles)
            .AsSplitQuery()
            .OrderBy(artifact => artifact.Id)
            .Take(2)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (artifacts.Count > 1)
        {
            throw new CentralDerivativeWindowAmbiguousInputException();
        }
        return artifacts.Count == 0 ? null : ResolvedCandidate.Create(artifacts[0]);
    }

    private async Task FreezeInputsAsync(
        CentralDerivativeJob job,
        IReadOnlyDictionary<Guid, ResolvedCandidate> candidates,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!await PersistResolvedInputsAsync(job, candidates, now, cancellationToken).ConfigureAwait(false))
        {
            job.StateReasonCode = CentralDerivativeWindowReasonCodes.ResolutionConflict;
            return;
        }
        job.InputSetIdentitySha256 = CentralDerivativeWindowIdentity.CreateInputSetIdentity(job.Inputs);
        job.Status = CentralDerivativeJobStatus.Pending;
        job.AvailableAtUtc = now;
        job.ResolutionCompletedAtUtc = now;
        job.StateReasonCode = null;
        job.LastError = null;
        job.UpdatedAtUtc = now;
    }

    private async Task<bool> PersistResolvedInputsAsync(
        CentralDerivativeJob job,
        IReadOnlyDictionary<Guid, ResolvedCandidate> candidates,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var staleInputs = job.Inputs.Where(input =>
        {
            var requirement = job.InputRequirements.Single(item => item.Id
                == input.CentralDerivativeJobInputRequirementId);
            return requirement.ResolutionState != CentralDerivativeInputResolutionState.Resolved
                || !candidates.TryGetValue(requirement.Id, out var candidate)
                || candidate.Artifact.Id != input.CentralArtifactId;
        }).ToArray();
        if (staleInputs.Length > 0)
        {
            dbContext.CentralDerivativeJobInputs.RemoveRange(staleInputs);
            foreach (var staleInput in staleInputs)
            {
                job.Inputs.Remove(staleInput);
            }
        }
        var selected = job.InputRequirements
            .Where(item => item.ResolutionState == CentralDerivativeInputResolutionState.Resolved)
            .OrderBy(item => item.Ordinal)
            .Select(item => (Requirement: item, Candidate: candidates[item.Id]))
            .ToArray();
        foreach (var centralArtifactId in selected.Select(item => item.Candidate.Artifact.Id).Order())
        {
            _ = await CentralArtifactRetentionLock.AcquireAsync(dbContext, centralArtifactId, cancellationToken)
                .ConfigureAwait(false);
        }
        var selectedIds = selected.Select(item => item.Candidate.Artifact.Id).ToArray();
        var usableCount = await dbContext.CentralArtifacts.CountAsync(artifact => selectedIds.Contains(artifact.Id)
            && artifact.ObjectState == CentralArtifactObjectState.Available
            && artifact.ReconstructionState == CentralReconstructionState.Complete, cancellationToken).ConfigureAwait(false);
        if (usableCount != selected.Length)
        {
            return false;
        }
        foreach (var item in selected)
        {
            item.Requirement.ResolvedAtUtc = now;
            if (job.Inputs.Any(input => input.CentralDerivativeJobInputRequirementId == item.Requirement.Id))
            {
                continue;
            }
            var input = new CentralDerivativeJobInput
            {
                Job = job,
                CentralDerivativeJobId = job.Id,
                Requirement = item.Requirement,
                CentralDerivativeJobInputRequirementId = item.Requirement.Id,
                Ordinal = item.Requirement.Ordinal,
                CentralArtifactId = item.Candidate.Artifact.Id,
                CaptureSequence = item.Candidate.Artifact.Frame!.CaptureSequence,
                CompatibilityJson = item.Candidate.Compatibility.Json,
                CompatibilitySha256 = item.Candidate.Compatibility.Sha256,
                ByteLength = item.Candidate.Artifact.ByteLength,
                SelectedAtUtc = now
            };
            job.Inputs.Add(input);
            dbContext.Entry(input).State = EntityState.Added;
        }
        return true;
    }

    private static void ApplyDeadlineOutcome(CentralDerivativeJob job, DateTimeOffset now, bool missingRequired)
    {
        var reason = missingRequired
            ? CentralDerivativeWindowReasonCodes.RequiredInputTimeout
            : CentralDerivativeWindowReasonCodes.OptionalInputTimeout;
        CompleteWithoutExecution(job, now, reason);
    }

    private static void CompleteWithoutExecution(CentralDerivativeJob job, DateTimeOffset now, string reasonCode)
    {
        job.Status = job.MissingInputOutcome switch
        {
            CentralDerivativeWindowOutcome.Quarantine => CentralDerivativeJobStatus.Quarantined,
            CentralDerivativeWindowOutcome.Fail => CentralDerivativeJobStatus.TerminalFailure,
            _ => CentralDerivativeJobStatus.Skipped
        };
        job.AvailableAtUtc = null;
        job.ResolutionCompletedAtUtc = now;
        job.CompletedAtUtc = now;
        job.StateReasonCode = reasonCode;
        job.LastError = reasonCode;
        job.UpdatedAtUtc = now;
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }

    private sealed record ResolvedCandidate(
        CentralArtifact Artifact,
        CentralDerivativeCompatibilitySnapshot Compatibility,
        string LayoutJson)
    {
        public static ResolvedCandidate Create(CentralArtifact artifact)
        {
            var descriptor = CentralReconstructionDescriptorFactory.Create(artifact.Frame!, artifact);
            return new(artifact,
                CentralDerivativeWindowCompatibility.CreateSnapshot(descriptor),
                CaptureContractJson.Canonicalize(
                    CaptureContractJson.SerializeToElement(descriptor.Layout)).GetRawText());
        }

        public bool IsCompatibleWith(ResolvedCandidate other)
            => Compatibility.Sha256 == other.Compatibility.Sha256 && LayoutJson == other.LayoutJson;
    }

    private static partial class Log
    {
        [LoggerMessage(2140, LogLevel.Information,
            "Central derivative window resolution: JobId={JobId}, Recipe={Recipe}, From={FromStatus}, To={ToStatus}, Selected={SelectedCount}, Reason={Reason}")]
        public static partial void Resolution(
            ILogger logger, Guid jobId, string recipe, string fromStatus, string toStatus, int selectedCount, string reason);
    }
}

internal sealed record CentralDerivativeCompatibilitySnapshot(string Json, string Sha256);

internal static class CentralDerivativeWindowCompatibility
{
    public static CentralDerivativeCompatibilitySnapshot EmptySnapshot { get; } = CreateSnapshot("{}");

    public static CentralDerivativeCompatibilitySnapshot CreateSnapshot(CentralArtifact artifact)
        => CreateSnapshot(CentralReconstructionDescriptorFactory.Create(artifact.Frame!, artifact));

    public static CentralDerivativeCompatibilitySnapshot CreateSnapshot(ReconstructionDescriptor descriptor)
    {
        var json = CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(new
        {
            Processing = LogicHostRecipeExecutionAdapter.CreateCompatibility(descriptor),
            SourceRecipeIdentitySha256 = ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256
        })).GetRawText();
        return CreateSnapshot(json);
    }

    private static CentralDerivativeCompatibilitySnapshot CreateSnapshot(string json)
    {
        return new(json, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
    }
}

internal static class CentralDerivativeWindowIdentity
{
    public static string CreateInputSetIdentity(IEnumerable<CentralDerivativeJobInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var value = string.Join('\n', inputs.OrderBy(item => item.Ordinal).Select(item => string.Join(':',
            item.Ordinal,
            item.CentralArtifactId.ToString("N"),
            item.CaptureSequence?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none",
            item.CompatibilitySha256.ToUpperInvariant())));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"hvo-central-derivative-input-set-v1\n{value}")));
    }
}

internal static class CentralDerivativeWindowReasonCodes
{
    public const string WaitingRequiredInput = "window.waiting-required-input";
    public const string RequiredInputTimeout = "window.required-input-timeout";
    public const string OptionalInputTimeout = "window.optional-input-timeout";
    public const string IncompatibleInput = "window.incompatible-input";
    public const string IncompatibleLayout = "window.incompatible-layout";
    public const string IncompatibleProfile = "window.incompatible-profile";
    public const string ResolutionConflict = "window.resolution-conflict";
    public const string EnvironmentUnavailable = "window.environment-unavailable";
    public const string AmbiguousInput = "window.ambiguous-input";
}

internal sealed class CentralDerivativeWindowAmbiguousInputException : Exception
{
    public CentralDerivativeWindowAmbiguousInputException()
    {
    }

    public CentralDerivativeWindowAmbiguousInputException(string message) : base(message)
    {
    }

    public CentralDerivativeWindowAmbiguousInputException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
