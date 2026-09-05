using System.Text.Json;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.ProcessingRunner.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed class CentralProcessingRunnerRejectedException : Exception
{
    public CentralProcessingRunnerRejectedException()
    {
    }

    public CentralProcessingRunnerRejectedException(string message) : base(message)
    {
    }

    public CentralProcessingRunnerRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public static CentralProcessingRunnerRejectedException Create(string reasonCode, string message)
        => new(message) { ReasonCode = reasonCode };

    public string ReasonCode { get; init; } = ProcessingRunnerReasonCodes.InvalidCompletion;
}

/// <summary>A registered runner resolved for a protocol call: the row plus the recipes it may claim.</summary>
internal sealed record CentralProcessingRunnerContext(
    CentralProcessingRunner Runner,
    ProcessingRunnerCapabilities Capabilities,
    IReadOnlyList<string> EligibleRecipes);

internal interface ICentralProcessingRunnerRegistry
{
    Task<ProcessingRunnerRegistrationResponse> RegisterAsync(
        string clientSubject,
        ProcessingRunnerRegistrationRequest request,
        CancellationToken cancellationToken);

    Task<ProcessingRunnerHeartbeatResponse> HeartbeatAsync(
        string clientSubject,
        string runnerId,
        ProcessingRunnerHeartbeatRequest request,
        CancellationToken cancellationToken);

    Task RetireAsync(string clientSubject, string runnerId, CancellationToken cancellationToken);

    /// <summary>Non-mutating status for probes: effective status by heartbeat age, never refreshing the heartbeat.</summary>
    Task<ProcessingRunnerStatusResponse> GetStatusAsync(string clientSubject, string runnerId, CancellationToken cancellationToken);

    /// <summary>Marks one runner stale when its heartbeat is older than the staleness window; returns the effective status.</summary>
    Task<CentralProcessingRunnerStatus> EnforceStalenessAsync(CentralProcessingRunner runner, CancellationToken cancellationToken);

    /// <summary>Resolves an active runner owned by the subject; throws a rejection otherwise.</summary>
    Task<CentralProcessingRunnerContext> ResolveOwnedAsync(
        string clientSubject,
        string runnerId,
        CancellationToken cancellationToken);

    /// <summary>True when <paramref name="runnerId"/> is registered (any non-retired status) to <paramref name="clientSubject"/>.</summary>
    Task<bool> IsOwnedAsync(string clientSubject, string runnerId, CancellationToken cancellationToken);

    /// <summary>Marks runners whose heartbeat is older than the staleness window and returns the counts by status.</summary>
    Task<IReadOnlyDictionary<CentralProcessingRunnerStatus, int>> RefreshStatusesAsync(CancellationToken cancellationToken);
}

internal sealed partial class CentralProcessingRunnerRegistry(
    ApplicationDbContext dbContext,
    IOptions<CentralProcessingRunnerOptions> options,
    CentralProcessingRunnerTelemetry telemetry,
    TimeProvider timeProvider,
    ILogger<CentralProcessingRunnerRegistry> logger) : ICentralProcessingRunnerRegistry
{
    private readonly CentralProcessingRunnerOptions _options = options.Value;

    public async Task<ProcessingRunnerRegistrationResponse> RegisterAsync(
        string clientSubject,
        ProcessingRunnerRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSubject);
        ArgumentNullException.ThrowIfNull(request);
        EnsureEnabled();
        if (!ProcessingRunnerProtocol.IsValidRunnerId(request.RunnerId))
        {
            telemetry.RecordRegistration("rejected");
            throw CentralProcessingRunnerRejectedException.Create(
                ProcessingRunnerReasonCodes.InvalidRunnerId, "The runner id is invalid.");
        }
        if (string.IsNullOrWhiteSpace(request.DisplayName)
            || request.DisplayName.Length > ProcessingRunnerProtocol.MaximumDisplayNameLength)
        {
            telemetry.RecordRegistration("rejected");
            throw CentralProcessingRunnerRejectedException.Create(
                ProcessingRunnerReasonCodes.InvalidCapabilities, "The runner display name is invalid.");
        }
        try
        {
            request.Capabilities.Validate();
        }
        catch (ProcessingRunnerProtocolException exception)
        {
            telemetry.RecordRegistration("rejected");
            throw CentralProcessingRunnerRejectedException.Create(exception.ReasonCode, exception.Message);
        }
        var now = timeProvider.GetUtcNow();
        var eligible = _options.ResolveEligibleRecipes(request.Capabilities);
        // An elastic instance whose launching host is gone was abandoned by the autoscaler (#430); its runner must
        // stop rather than revive the retired registration and keep claiming unmanaged.
        if (await dbContext.CentralElasticRunnerInstances.AsNoTracking()
                .AnyAsync(instance => instance.RunnerId == request.RunnerId && instance.State == "Abandoned", cancellationToken)
                .ConfigureAwait(false))
        {
            telemetry.RecordRegistration("rejected");
            throw CentralProcessingRunnerRejectedException.Create(
                ProcessingRunnerReasonCodes.RegistrationDenied,
                "The runner was abandoned by its launching host and may not re-register.");
        }
        var runner = await dbContext.CentralProcessingRunners
            .SingleOrDefaultAsync(candidate => candidate.RunnerId == request.RunnerId, cancellationToken)
            .ConfigureAwait(false);
        var outcome = "registered";
        if (runner is null)
        {
            runner = new CentralProcessingRunner
            {
                RunnerId = request.RunnerId,
                ClientSubject = clientSubject,
                RegisteredAtUtc = now,
                Generation = 1
            };
            dbContext.CentralProcessingRunners.Add(runner);
        }
        else
        {
            if (!string.Equals(runner.ClientSubject, clientSubject, StringComparison.Ordinal))
            {
                telemetry.RecordRegistration("rejected");
                throw CentralProcessingRunnerRejectedException.Create(
                    ProcessingRunnerReasonCodes.RegistrationNotOwned,
                    "The runner id is registered to a different credential.");
            }
            runner.Generation = checked(runner.Generation + 1);
            runner.RetiredAtUtc = null;
            outcome = "re-registered";
        }
        Apply(runner, request, eligible, now);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        telemetry.RecordRegistration(outcome);
        Log.Registered(logger, runner.RunnerId, outcome, runner.ProcessArchitecture, runner.ResourceClass,
            runner.LatencyClass, runner.WarmState.ToString(), runner.MaxConcurrency, string.Join(',', eligible));
        return new ProcessingRunnerRegistrationResponse(
            runner.RunnerId,
            runner.Id,
            ProcessingRunnerRegistrationStatus.Active,
            _options.HeartbeatInterval,
            _options.LeaseDuration,
            _options.RenewalInterval,
            _options.ClaimBackoff,
            eligible,
            now);
    }

    public async Task<ProcessingRunnerHeartbeatResponse> HeartbeatAsync(
        string clientSubject,
        string runnerId,
        ProcessingRunnerHeartbeatRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEnabled();
        var context = await ResolveOwnedAsync(clientSubject, runnerId, cancellationToken).ConfigureAwait(false);
        var runner = context.Runner;
        if (request.AvailableSlots < 0 || request.AvailableSlots > runner.MaxConcurrency
            || request.ActiveJobIds.Count > ProcessingRunnerProtocol.MaximumActiveJobReport)
        {
            telemetry.RecordHeartbeat("rejected");
            throw CentralProcessingRunnerRejectedException.Create(
                ProcessingRunnerReasonCodes.InvalidCapabilities, "The heartbeat slot or job report is invalid.");
        }
        var now = timeProvider.GetUtcNow();
        runner.LastHeartbeatAtUtc = now;
        runner.UpdatedAtUtc = now;
        runner.AvailableSlots = request.AvailableSlots;
        runner.WarmState = (CentralProcessingRunnerWarmState)request.WarmState;
        // An abandoned elastic instance (#430) must not keep heartbeating: its registration is retired here and the
        // runner is told so; its re-registration is then denied and the runner exits.
        if (await dbContext.CentralElasticRunnerInstances.AsNoTracking()
                .AnyAsync(instance => instance.RunnerId == runnerId && instance.State == "Abandoned", cancellationToken)
                .ConfigureAwait(false))
        {
            runner.Status = CentralProcessingRunnerStatus.Retired;
            runner.RetiredAtUtc = now;
            runner.UpdatedAtUtc = now;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            throw CentralProcessingRunnerRejectedException.Create(
                ProcessingRunnerReasonCodes.RegistrationRetired, "The runner was abandoned by its launching host.");
        }
        var wasStale = runner.Status == CentralProcessingRunnerStatus.Stale;
        runner.Status = CentralProcessingRunnerStatus.Active;
        var capabilities = context.Capabilities;
        if (request.Warmup is not null)
        {
            capabilities = capabilities with { Warmup = request.Warmup, WarmState = request.WarmState };
            runner.CapabilitiesJson = JsonSerializer.Serialize(capabilities, ProcessingRunnerProtocol.SerializerOptions);
        }
        // Placement is host configuration and may change while a runner stays registered; re-resolve on every heartbeat.
        var eligible = _options.ResolveEligibleRecipes(capabilities);
        if (!eligible.SequenceEqual(context.EligibleRecipes, StringComparer.Ordinal))
        {
            runner.EligibleRecipesJson = JsonSerializer.Serialize(eligible);
            Log.EligibilityChanged(logger, runner.RunnerId, string.Join(',', eligible));
        }
        var activeJobIds = request.ActiveJobIds.Distinct().ToArray();
        var cancelRequested = new List<Guid>();
        var stale = new List<Guid>();
        if (activeJobIds.Length != 0)
        {
            var jobs = await dbContext.CentralDerivativeJobs.AsNoTracking()
                .Where(job => activeJobIds.Contains(job.Id))
                .Select(job => new
                {
                    job.Id,
                    job.Status,
                    job.LeaseOwner,
                    job.LeaseExpiresAtUtc,
                    job.CancellationRequestedAtUtc,
                    GraphStatus = job.GraphExecution != null ? job.GraphExecution.Status : (CentralProcessingGraphExecutionStatus?)null
                })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var found = jobs.ToDictionary(job => job.Id);
            foreach (var jobId in activeJobIds)
            {
                if (!found.TryGetValue(jobId, out var job)
                    || job.Status != CentralDerivativeJobStatus.Leased
                    || !string.Equals(job.LeaseOwner, runner.RunnerId, StringComparison.Ordinal)
                    || job.LeaseExpiresAtUtc <= now)
                {
                    stale.Add(jobId);
                }
                else if (job.CancellationRequestedAtUtc is not null
                    || job.GraphStatus is CentralProcessingGraphExecutionStatus.CancelRequested
                        or CentralProcessingGraphExecutionStatus.Canceled)
                {
                    cancelRequested.Add(jobId);
                }
            }
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        telemetry.RecordHeartbeat(wasStale ? "recovered" : "accepted");
        if (wasStale)
        {
            Log.Recovered(logger, runner.RunnerId);
        }
        return new ProcessingRunnerHeartbeatResponse(
            ProcessingRunnerRegistrationStatus.Active, cancelRequested, stale, eligible, now);
    }

    public async Task<ProcessingRunnerStatusResponse> GetStatusAsync(
        string clientSubject,
        string runnerId,
        CancellationToken cancellationToken)
    {
        var context = await ResolveOwnedAsync(clientSubject, runnerId, allowStale: true, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        var effective = context.Runner.Status == CentralProcessingRunnerStatus.Active
            && context.Runner.LastHeartbeatAtUtc < now - _options.StaleAfter
                ? CentralProcessingRunnerStatus.Stale
                : context.Runner.Status;
        var activeLeases = await dbContext.CentralDerivativeJobs.AsNoTracking().CountAsync(job =>
            job.Status == CentralDerivativeJobStatus.Leased
            && job.LeaseOwner == runnerId
            && job.LeaseExpiresAtUtc > now, cancellationToken).ConfigureAwait(false);
        return new ProcessingRunnerStatusResponse(
            runnerId,
            (ProcessingRunnerRegistrationStatus)effective,
            (ProcessingRunnerWarmState)context.Runner.WarmState,
            context.EligibleRecipes,
            activeLeases,
            context.Runner.LastHeartbeatAtUtc,
            now);
    }

    public async Task<CentralProcessingRunnerStatus> EnforceStalenessAsync(
        CentralProcessingRunner runner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runner);
        var now = timeProvider.GetUtcNow();
        if (runner.Status == CentralProcessingRunnerStatus.Active && runner.LastHeartbeatAtUtc < now - _options.StaleAfter)
        {
            runner.Status = CentralProcessingRunnerStatus.Stale;
            runner.AvailableSlots = 0;
            runner.UpdatedAtUtc = now;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            Log.Staled(logger, 1, _options.StaleAfter);
        }
        return runner.Status;
    }

    public async Task RetireAsync(string clientSubject, string runnerId, CancellationToken cancellationToken)
    {
        var context = await ResolveOwnedAsync(clientSubject, runnerId, allowStale: true, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        context.Runner.Status = CentralProcessingRunnerStatus.Retired;
        context.Runner.RetiredAtUtc = now;
        context.Runner.UpdatedAtUtc = now;
        context.Runner.AvailableSlots = 0;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        telemetry.RecordRegistration("retired");
        Log.Retired(logger, runnerId);
    }

    public Task<CentralProcessingRunnerContext> ResolveOwnedAsync(
        string clientSubject,
        string runnerId,
        CancellationToken cancellationToken)
        => ResolveOwnedAsync(clientSubject, runnerId, allowStale: true, cancellationToken);

    public async Task<bool> IsOwnedAsync(string clientSubject, string runnerId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientSubject) || !ProcessingRunnerProtocol.IsValidRunnerId(runnerId))
        {
            return false;
        }
        return await dbContext.CentralProcessingRunners.AsNoTracking().AnyAsync(runner =>
            runner.RunnerId == runnerId
            && runner.ClientSubject == clientSubject
            && runner.Status != CentralProcessingRunnerStatus.Retired, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<CentralProcessingRunnerStatus, int>> RefreshStatusesAsync(
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var cutoff = now - _options.StaleAfter;
        var staled = await dbContext.CentralProcessingRunners
            .Where(runner => runner.Status == CentralProcessingRunnerStatus.Active && runner.LastHeartbeatAtUtc < cutoff)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(runner => runner.Status, CentralProcessingRunnerStatus.Stale)
                .SetProperty(runner => runner.AvailableSlots, 0)
                .SetProperty(runner => runner.UpdatedAtUtc, now), cancellationToken)
            .ConfigureAwait(false);
        if (staled > 0)
        {
            Log.Staled(logger, staled, _options.StaleAfter);
        }
        var counts = await dbContext.CentralProcessingRunners.AsNoTracking()
            .GroupBy(runner => runner.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var result = Enum.GetValues<CentralProcessingRunnerStatus>()
            .ToDictionary(status => status, _ => 0);
        foreach (var entry in counts)
        {
            result[entry.Status] = entry.Count;
        }
        foreach (var pair in result)
        {
            telemetry.SetRegistered(pair.Key.ToString().ToLowerInvariant(), pair.Value);
        }
        return result;
    }

    private async Task<CentralProcessingRunnerContext> ResolveOwnedAsync(
        string clientSubject,
        string runnerId,
        bool allowStale,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSubject);
        if (!ProcessingRunnerProtocol.IsValidRunnerId(runnerId))
        {
            throw CentralProcessingRunnerRejectedException.Create(
                ProcessingRunnerReasonCodes.InvalidRunnerId, "The runner id is invalid.");
        }
        var runner = await dbContext.CentralProcessingRunners
            .SingleOrDefaultAsync(candidate => candidate.RunnerId == runnerId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw CentralProcessingRunnerRejectedException.Create(
                ProcessingRunnerReasonCodes.RegistrationRequired, "The runner is not registered.");
        if (!string.Equals(runner.ClientSubject, clientSubject, StringComparison.Ordinal))
        {
            throw CentralProcessingRunnerRejectedException.Create(
                ProcessingRunnerReasonCodes.RegistrationNotOwned, "The runner id is registered to a different credential.");
        }
        if (runner.Status == CentralProcessingRunnerStatus.Retired
            || (!allowStale && runner.Status == CentralProcessingRunnerStatus.Stale))
        {
            throw CentralProcessingRunnerRejectedException.Create(
                ProcessingRunnerReasonCodes.RegistrationRetired, "The runner registration is retired; register again.");
        }
        var capabilities = JsonSerializer.Deserialize<ProcessingRunnerCapabilities>(
            runner.CapabilitiesJson, ProcessingRunnerProtocol.SerializerOptions)
            ?? throw CentralProcessingRunnerRejectedException.Create(
                ProcessingRunnerReasonCodes.InvalidCapabilities, "The stored runner capabilities are unreadable.");
        var eligible = JsonSerializer.Deserialize<string[]>(runner.EligibleRecipesJson) ?? [];
        return new CentralProcessingRunnerContext(runner, capabilities, eligible);
    }

    private void EnsureEnabled()
    {
        if (!_options.Enabled)
        {
            throw CentralProcessingRunnerRejectedException.Create(
                ProcessingRunnerReasonCodes.RunnersDisabled, "Processing runners are disabled on this LogicHost.");
        }
    }

    private static void Apply(
        CentralProcessingRunner runner,
        ProcessingRunnerRegistrationRequest request,
        IReadOnlyList<string> eligible,
        DateTimeOffset now)
    {
        var capabilities = request.Capabilities;
        runner.DisplayName = request.DisplayName.Trim();
        runner.OperatingSystem = Truncate(capabilities.OperatingSystem, 256);
        runner.OsArchitecture = Truncate(capabilities.OsArchitecture, 32);
        runner.ProcessArchitecture = Truncate(capabilities.ProcessArchitecture, 32);
        runner.RuntimeIdentifier = Truncate(capabilities.RuntimeIdentifier, 64);
        runner.FrameworkDescription = Truncate(capabilities.FrameworkDescription, 128);
        runner.ProcessorCount = capabilities.ProcessorCount;
        runner.TotalMemoryBytes = capabilities.TotalMemoryBytes;
        runner.ResourceClass = capabilities.ResourceClass;
        runner.GpuAvailable = capabilities.GpuAvailable;
        runner.LatencyClass = capabilities.LatencyClass;
        runner.CapabilitiesJson = JsonSerializer.Serialize(capabilities, ProcessingRunnerProtocol.SerializerOptions);
        runner.EligibleRecipesJson = JsonSerializer.Serialize(eligible);
        runner.MaxConcurrency = capabilities.MaxConcurrency;
        runner.MaxTransferBytes = capabilities.MaxTransferBytes;
        runner.WarmState = (CentralProcessingRunnerWarmState)capabilities.WarmState;
        runner.Status = CentralProcessingRunnerStatus.Active;
        runner.ProcessId = request.ProcessId;
        runner.ProcessStartedUtc = request.ProcessStartedUtc;
        runner.AvailableSlots = capabilities.MaxConcurrency;
        runner.LastHeartbeatAtUtc = now;
        runner.UpdatedAtUtc = now;
    }

    private static string Truncate(string value, int maximumLength)
        => value.Length <= maximumLength ? value : value[..maximumLength];

    private static partial class Log
    {
        [LoggerMessage(2200, LogLevel.Information,
            "Processing runner {Outcome}: RunnerId={RunnerId}, Architecture={Architecture}, ResourceClass={ResourceClass}, LatencyClass={LatencyClass}, WarmState={WarmState}, Concurrency={Concurrency}, EligibleRecipes={EligibleRecipes}")]
        public static partial void Registered(
            ILogger logger, string runnerId, string outcome, string architecture, string resourceClass,
            string latencyClass, string warmState, int concurrency, string eligibleRecipes);

        [LoggerMessage(2201, LogLevel.Warning,
            "Processing runners marked stale: Count={Count}, StaleAfter={StaleAfter}")]
        public static partial void Staled(ILogger logger, int count, TimeSpan staleAfter);

        [LoggerMessage(2202, LogLevel.Information, "Processing runner recovered from stale: RunnerId={RunnerId}")]
        public static partial void Recovered(ILogger logger, string runnerId);

        [LoggerMessage(2203, LogLevel.Information, "Processing runner retired: RunnerId={RunnerId}")]
        public static partial void Retired(ILogger logger, string runnerId);

        [LoggerMessage(2211, LogLevel.Information,
            "Processing runner eligibility changed: RunnerId={RunnerId}, EligibleRecipes={EligibleRecipes}")]
        public static partial void EligibilityChanged(ILogger logger, string runnerId, string eligibleRecipes);
    }
}
