using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.ProcessingRunner.Contracts;

namespace HVO.SkyMonitor.ProcessingRunner;

/// <summary>
/// The runner loop: register, heartbeat, and run one claim/execute slot per unit of concurrency. Every durable
/// decision stays on LogicHost; the runner only fetches inputs under the job lease, executes the recipe kernel,
/// renews the lease while it runs, and uploads products or reports a failure.
/// </summary>
internal sealed class RunnerHost(
    ProcessingRunnerClient client,
    IProcessingRecipeExecutor executor,
    RunnerHostOptions options,
    RunnerLog log,
    TimeProvider? timeProvider = null) : IDisposable
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeJobs = new();
    private readonly CancellationTokenSource _lifetime = new();
    private ProcessingRunnerRegistrationResponse? _registration;
    private long _lastWorkTimestamp;
    private int _availableSlots;

    public int ActiveJobCount => _activeJobs.Count;

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2025:Ensure tasks using IDisposable instances complete before the instances are disposed",
        Justification = "Every task that observes the linked source is awaited before the using scope ends.")]
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        _lastWorkTimestamp = _timeProvider.GetTimestamp();
        _availableSlots = options.MaxConcurrency;
        _registration = await RegisterUntilAcceptedAsync(cancellationToken).ConfigureAwait(false);
        if (_registration is null)
        {
            return 0;
        }
        TouchLiveness();
        // External cancellation (SIGTERM) only stops claiming. Heartbeats, renewals, and in-flight executions keep
        // the runner lifetime token until the grace period has drained active jobs, so a deployment restart does not
        // abandon work that could still complete.
        using var claimStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = HeartbeatLoopAsync(_lifetime.Token);
        var slots = Enumerable.Range(0, options.MaxConcurrency)
            .Select(slot => SlotLoopAsync(slot, claimStop.Token))
            .ToArray();
        var idle = IdleWatchAsync(claimStop);
        var slotsTask = Task.WhenAll(slots);
        try
        {
            await slotsTask.WaitAsync(claimStop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        await DrainAsync(slotsTask).ConfigureAwait(false);
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(heartbeat, idle).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        await RetireAsync().ConfigureAwait(false);
        return 0;
    }

    private async Task<ProcessingRunnerRegistrationResponse?> RegisterUntilAcceptedAsync(CancellationToken cancellationToken)
    {
        var request = new ProcessingRunnerRegistrationRequest(
            options.RunnerId,
            options.DisplayName,
            options.Capabilities,
            Environment.ProcessId,
            ProcessingRunnerProcessInfo.GetProcessStartedUtc());
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var registration = await client.RegisterAsync(request, cancellationToken).ConfigureAwait(false);
                log.Info("registered",
                    $"Registered with LogicHost; eligible recipes: {(registration.EligibleRecipes.Count == 0 ? "none" : string.Join(',', registration.EligibleRecipes))}; heartbeat {registration.HeartbeatInterval.TotalSeconds:0}s; lease {registration.LeaseDuration.TotalSeconds:0}s.");
                if (registration.EligibleRecipes.Count == 0)
                {
                    log.Warning("no-eligible-recipes",
                        "LogicHost placed no recipes on this runner; it will heartbeat and wait for a placement change.");
                }
                return registration;
            }
            catch (ProcessingRunnerClientException exception) when (!exception.IsAuthorizationFailure
                || options.RetryAuthorizationFailures)
            {
                log.Warning("registration-retry", $"Registration failed ({exception.Message}); retrying in {options.RegistrationRetry.TotalSeconds:0}s.");
            }
            catch (HttpRequestException exception)
            {
                log.Warning("registration-retry", $"LogicHost unreachable ({exception.Message}); retrying in {options.RegistrationRetry.TotalSeconds:0}s.");
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                log.Warning("registration-retry", "Registration timed out; retrying.");
            }
            try
            {
                await Task.Delay(options.RegistrationRetry, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
        return null;
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        var interval = _registration!.HeartbeatInterval;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                var response = await client.HeartbeatAsync(
                    new ProcessingRunnerHeartbeatRequest(
                        options.Capabilities.WarmState,
                        Volatile.Read(ref _availableSlots),
                        _activeJobs.Keys.Take(ProcessingRunnerProtocol.MaximumActiveJobReport).ToArray()),
                    cancellationToken).ConfigureAwait(false);
                if (!response.EligibleRecipes.SequenceEqual(_registration!.EligibleRecipes, StringComparer.Ordinal))
                {
                    log.Info("eligibility-changed",
                        $"LogicHost placement changed; eligible recipes: {(response.EligibleRecipes.Count == 0 ? "none" : string.Join(',', response.EligibleRecipes))}.");
                    _registration = _registration with { EligibleRecipes = response.EligibleRecipes };
                }
                TouchLiveness();
                foreach (var jobId in response.CancelRequestedJobIds.Concat(response.StaleJobIds))
                {
                    if (_activeJobs.TryGetValue(jobId, out var cancellation))
                    {
                        log.Info("job-cancel-requested", "LogicHost requested cancellation or reported a stale lease; stopping execution.", jobId);
                        await cancellation.CancelAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (ProcessingRunnerClientException exception) when (exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                log.Warning("registration-lost", $"LogicHost no longer recognizes this runner ({exception.Message}); re-registering.");
                var registration = await RegisterUntilAcceptedAsync(cancellationToken).ConfigureAwait(false);
                if (registration is not null)
                {
                    _registration = registration;
                    interval = registration.HeartbeatInterval;
                }
            }
            catch (ProcessingRunnerClientException exception)
            {
                log.Warning("heartbeat-failed", exception.Message);
            }
            catch (HttpRequestException exception)
            {
                log.Warning("heartbeat-failed", exception.Message);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                log.Warning("heartbeat-failed", "Heartbeat timed out.");
            }
        }
    }

    private async Task IdleWatchAsync(CancellationTokenSource claimStop)
    {
        if (options.IdleShutdown <= TimeSpan.Zero)
        {
            return;
        }
        while (!claimStop.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _timeProvider, _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (_activeJobs.IsEmpty
                && _timeProvider.GetElapsedTime(Volatile.Read(ref _lastWorkTimestamp)) >= options.IdleShutdown)
            {
                log.Info("idle-shutdown", $"No work for {options.IdleShutdown.TotalSeconds:0}s; shutting down.");
                await claimStop.CancelAsync().ConfigureAwait(false);
                return;
            }
        }
    }

    private async Task SlotLoopAsync(int slot, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var registration = _registration!;
            if (registration.EligibleRecipes.Count == 0)
            {
                await DelayAsync(registration.ClaimBackoff, cancellationToken).ConfigureAwait(false);
                continue;
            }
            ProcessingRunnerClaim? claim;
            try
            {
                claim = await client.ClaimAsync(
                    new ProcessingRunnerClaimRequest(ProcessingRunnerJobClass.CentralRecipe, slot), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ProcessingRunnerClientException exception)
            {
                log.Warning("claim-failed", exception.Message);
                await DelayAsync(Backoff(registration.ClaimBackoff, exception), cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException exception)
            {
                log.Warning("claim-failed", exception.Message);
                await DelayAsync(registration.ClaimBackoff, cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                log.Warning("claim-failed", "Claim timed out.");
                await DelayAsync(registration.ClaimBackoff, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (claim is null)
            {
                await DelayAsync(registration.ClaimBackoff, cancellationToken).ConfigureAwait(false);
                continue;
            }
            Volatile.Write(ref _lastWorkTimestamp, _timeProvider.GetTimestamp());
            Interlocked.Decrement(ref _availableSlots);
            try
            {
                await ExecuteClaimAsync(claim, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Increment(ref _availableSlots);
                Volatile.Write(ref _lastWorkTimestamp, _timeProvider.GetTimestamp());
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A recipe or transfer failure must be reported to LogicHost and must never stop the slot.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2025:Ensure tasks using IDisposable instances complete before the instances are disposed",
        Justification = "The renewal task is awaited in the finally block before the job cancellation source is disposed.")]
    private async Task ExecuteClaimAsync(ProcessingRunnerClaim claim, CancellationToken claimStopToken)
    {
        _ = claimStopToken;
        var stoppingToken = _lifetime.Token;
        using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _activeJobs[claim.JobId] = jobCancellation;
        var renewal = RenewLoopAsync(claim, jobCancellation);
        try
        {
            var execution = await RunnerJobExecution.ExecuteAsync(
                client, executor, claim, options.MaxTransferBytes, _timeProvider, jobCancellation.Token)
                .ConfigureAwait(false);
            if (execution.Outcome is { } outcome)
            {
                var (request, payloads) = ProcessingRunnerProjection.ProjectOutcome(
                    claim.LeaseToken, outcome, execution.InputBytes, execution.Duration);
                var response = await client.CompleteAsync(claim.JobId, request, payloads, stoppingToken).ConfigureAwait(false);
                log.Info("job-completed",
                    $"Recipe {claim.RecipeName} attempt {claim.AttemptCount} finished {response.Status} ({response.ReasonCode ?? "no-reason"}) in {execution.Duration.TotalMilliseconds:0} ms with {payloads.Sum(static payload => (long)payload.Length)} product bytes.",
                    claim.JobId);
            }
            else
            {
                await client.FailAsync(claim.JobId, execution.Failure!, stoppingToken).ConfigureAwait(false);
                log.Warning("job-failed",
                    $"Recipe {claim.RecipeName} attempt {claim.AttemptCount} reported {execution.Failure!.ReasonCode} ({(execution.Failure.Retryable ? "retryable" : "terminal")}): {execution.Failure.Message}",
                    claim.JobId);
            }
        }
        catch (OperationCanceledException) when (jobCancellation.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            log.Info("job-canceled", "Execution stopped because the lease was canceled or lost.", claim.JobId);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            log.Warning("job-abandoned", "Execution abandoned after the shutdown grace period; LogicHost will expire the lease.", claim.JobId);
        }
        catch (ProcessingRunnerClientException exception) when (exception.IsLeaseStale || exception.IsLeaseCanceled)
        {
            log.Warning("job-lease-lost", $"LogicHost rejected the result: {exception.Message}", claim.JobId);
        }
        catch (Exception exception)
        {
            log.Error("job-unexpected", $"Unexpected failure: {exception.Message}", claim.JobId);
            await TryFailAsync(claim, ProcessingRunnerReasonCodes.Unavailable, retryable: true, exception.Message).ConfigureAwait(false);
        }
        finally
        {
            await jobCancellation.CancelAsync().ConfigureAwait(false);
            _activeJobs.TryRemove(claim.JobId, out _);
            try
            {
                await renewal.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task RenewLoopAsync(ProcessingRunnerClaim claim, CancellationTokenSource jobCancellation)
    {
        var token = jobCancellation.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(claim.RenewalInterval, _timeProvider, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                await client.RenewAsync(claim.JobId, claim.LeaseToken, token).ConfigureAwait(false);
            }
            catch (ProcessingRunnerClientException exception) when (exception.IsLeaseStale || exception.IsLeaseCanceled)
            {
                log.Warning("lease-lost", $"Lease renewal refused ({exception.ReasonCode ?? exception.StatusCode.ToString()}); stopping execution.", claim.JobId);
                await jobCancellation.CancelAsync().ConfigureAwait(false);
                return;
            }
            catch (ProcessingRunnerClientException exception)
            {
                log.Warning("lease-renewal-failed", exception.Message, claim.JobId);
            }
            catch (HttpRequestException exception)
            {
                log.Warning("lease-renewal-failed", exception.Message, claim.JobId);
            }
            catch (TaskCanceledException) when (!token.IsCancellationRequested)
            {
                log.Warning("lease-renewal-failed", "Lease renewal timed out.", claim.JobId);
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A best-effort failure report must not mask the original failure.")]
    private async Task TryFailAsync(ProcessingRunnerClaim claim, string reasonCode, bool retryable, string message)
    {
        try
        {
            await client.FailAsync(
                claim.JobId,
                new ProcessingRunnerFailureRequest(claim.LeaseToken, reasonCode, retryable, Truncate(message)),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            log.Warning("job-failure-report-failed", exception.Message, claim.JobId);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Slot failures were already reported by the slots; drain only sequences shutdown.")]
    private async Task DrainAsync(Task slotsTask)
    {
        if (!slotsTask.IsCompleted)
        {
            log.Info("draining", $"Claiming stopped; waiting up to {options.ShutdownGrace.TotalSeconds:0}s for {_activeJobs.Count} active job(s) to complete.");
            if (await WaitForSlotsAsync(slotsTask, options.ShutdownGrace).ConfigureAwait(false))
            {
                return;
            }
            log.Warning("drain-timeout", $"{_activeJobs.Count} job(s) still active after the grace period; canceling.");
            foreach (var cancellation in _activeJobs.Values)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
            }
            // Cancellation must propagate through executors and any completion or failure upload; bound each stage
            // so the configured grace period stays effective even against an executor that ignores cancellation.
            var bound = TimeSpan.FromTicks(Math.Clamp(options.ShutdownGrace.Ticks / 2, TimeSpan.FromSeconds(1).Ticks, TimeSpan.FromSeconds(15).Ticks));
            if (await WaitForSlotsAsync(slotsTask, bound).ConfigureAwait(false))
            {
                return;
            }
            await _lifetime.CancelAsync().ConfigureAwait(false);
            if (!await WaitForSlotsAsync(slotsTask, bound).ConfigureAwait(false))
            {
                log.Error("drain-abandoned", $"{_activeJobs.Count} job(s) did not stop after cancellation; exiting without them. LogicHost will expire their leases.");
            }
            return;
        }
        await WaitForSlotsAsync(slotsTask, Timeout.InfiniteTimeSpan).ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Slot failures were already reported by the slots; the wait only sequences shutdown.")]
    private async Task<bool> WaitForSlotsAsync(Task slotsTask, TimeSpan timeout)
    {
        try
        {
            await slotsTask.WaitAsync(timeout, _timeProvider).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The liveness file is best effort; a probe failure must not stop the runner.")]
    private void TouchLiveness()
    {
        if (string.IsNullOrWhiteSpace(options.LivenessFile))
        {
            return;
        }
        try
        {
            File.WriteAllText(options.LivenessFile, RunnerLiveness.Format(_timeProvider.GetUtcNow(), _registration?.HeartbeatInterval ?? TimeSpan.Zero));
        }
        catch (Exception exception)
        {
            log.Warning("liveness-write-failed", exception.Message);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Retirement is best effort during shutdown.")]
    private async Task RetireAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await client.RetireAsync(timeout.Token).ConfigureAwait(false);
            log.Info("retired", "Retired the runner registration.");
        }
        catch (Exception exception)
        {
            log.Warning("retire-failed", exception.Message);
        }
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose() => _lifetime.Dispose();

    private static TimeSpan Backoff(TimeSpan claimBackoff, ProcessingRunnerClientException exception)
        => exception.IsUnavailable || exception.IsAuthorizationFailure
            ? TimeSpan.FromTicks(Math.Min(claimBackoff.Ticks * 5, TimeSpan.FromMinutes(1).Ticks))
            : claimBackoff;

    private static string Truncate(string value)
        => value.Length <= 512 ? value : value[..512];
}

/// <summary>The liveness file the runner loop rewrites at registration and on every accepted heartbeat.</summary>
internal static class RunnerLiveness
{
    public static string Format(DateTimeOffset heartbeatUtc, TimeSpan heartbeatInterval)
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{heartbeatUtc:O}|{heartbeatInterval.TotalSeconds:0.###}");

    public static bool TryParse(string? content, out DateTimeOffset heartbeatUtc, out TimeSpan heartbeatInterval)
    {
        heartbeatUtc = default;
        heartbeatInterval = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }
        var separator = content.IndexOf('|', StringComparison.Ordinal);
        var stamp = separator < 0 ? content.Trim() : content[..separator].Trim();
        if (!DateTimeOffset.TryParse(stamp, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out heartbeatUtc))
        {
            return false;
        }
        if (separator >= 0 && double.TryParse(content[(separator + 1)..].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
        {
            heartbeatInterval = TimeSpan.FromSeconds(seconds);
        }
        return true;
    }

    /// <summary>The probe tolerates the larger of the configured window and three negotiated heartbeat intervals.</summary>
    public static TimeSpan ResolveMaxAge(TimeSpan configured, TimeSpan heartbeatInterval)
        => heartbeatInterval > TimeSpan.Zero && heartbeatInterval * 3 > configured ? heartbeatInterval * 3 : configured;
}

internal sealed record RunnerHostOptions(
    string RunnerId,
    string DisplayName,
    ProcessingRunnerCapabilities Capabilities,
    int MaxConcurrency,
    long MaxTransferBytes,
    TimeSpan IdleShutdown,
    TimeSpan ShutdownGrace,
    TimeSpan RegistrationRetry,
    bool RetryAuthorizationFailures = false,
    string? LivenessFile = null);

internal sealed record RunnerJobExecutionResult(
    ProcessingOutcome? Outcome,
    ProcessingRunnerFailureRequest? Failure,
    long InputBytes,
    TimeSpan Duration);

/// <summary>Fetches and verifies inputs, rebuilds the request, and runs the recipe kernel for one claim.</summary>
internal static class RunnerJobExecution
{
    public static async Task<RunnerJobExecutionResult> ExecuteAsync(
        ProcessingRunnerClient client,
        IProcessingRecipeExecutor executor,
        ProcessingRunnerClaim claim,
        long maxTransferBytes,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (claim.ProtocolVersion != ProcessingRunnerProtocol.Version)
        {
            return Failure(claim, ProcessingRunnerReasonCodes.InvalidCompletion, false, "Unsupported claim protocol version.");
        }
        long total = 0;
        foreach (var input in claim.Inputs)
        {
            total = checked(total + input.PayloadLength);
        }
        if (total > maxTransferBytes)
        {
            return Failure(claim, ProcessingRunnerReasonCodes.TransferTooLarge, true, "Claimed inputs exceed the runner transfer limit.");
        }
        var payloads = new ReadOnlyMemory<byte>[claim.Inputs.Count];
        for (var index = 0; index < payloads.Length; index++)
        {
            var input = claim.Inputs[index];
            try
            {
                payloads[index] = await client.DownloadInputAsync(claim, input, cancellationToken).ConfigureAwait(false);
            }
            catch (ProcessingRunnerClientException exception) when (exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return Failure(claim, "object.missing", true, $"Input {input.ArtifactId:D} is not available: {exception.Message}", input.ArtifactId);
            }
            catch (ProcessingRunnerClientException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
            {
                return Failure(claim, ProcessingRunnerReasonCodes.InputUnavailable, true, $"Input {input.ArtifactId:D} is pending: {exception.Message}", input.ArtifactId);
            }
            catch (ProcessingRunnerProtocolException exception)
            {
                var reason = exception.ReasonCode == ProcessingRunnerReasonCodes.PayloadChecksumMismatch
                    ? "object.checksum-mismatch"
                    : exception.ReasonCode == ProcessingRunnerReasonCodes.PayloadLengthMismatch
                        ? "object.length-mismatch"
                        : exception.ReasonCode;
                return Failure(claim, reason, true, exception.Message, input.ArtifactId);
            }
        }
        ProcessingExecutionRequest request;
        try
        {
            request = ProcessingRunnerProjection.ReconstructRequest(claim, payloads);
        }
        catch (ProcessingRunnerProtocolException exception)
        {
            return Failure(claim, exception.ReasonCode, false, exception.Message);
        }
        var started = timeProvider.GetTimestamp();
        var outcome = await executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        return new RunnerJobExecutionResult(outcome, null, total, timeProvider.GetElapsedTime(started));
    }

    private static RunnerJobExecutionResult Failure(
        ProcessingRunnerClaim claim,
        string reasonCode,
        bool retryable,
        string message,
        Guid? unavailableArtifactId = null)
        => new(
            null,
            new ProcessingRunnerFailureRequest(claim.LeaseToken, reasonCode, retryable, message, unavailableArtifactId),
            0,
            TimeSpan.Zero);
}
