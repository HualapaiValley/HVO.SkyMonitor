using System.Runtime.ExceptionServices;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Environmental;

public sealed class EnvironmentalCaptureTriggerBridge(
    EnvironmentalAcquisitionCoordinator coordinator,
    EnvironmentalAcquisitionService service,
    IEnvironmentalAcquisitionStateStore stateStore,
    IOptions<CameraAgentHostOptions> options,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    internal static readonly TimeSpan TotalCaptureBudget = TimeSpan.FromSeconds(2);

    public async ValueTask<EnvironmentalCaptureAcquisition?> BeforeCaptureAsync(
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        if (!options.Value.EnvironmentalAcquisition.Enabled)
        {
            return null;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var started = _timeProvider.GetTimestamp();
        var configuredSources = coordinator.Sources;
        var selected = configuredSources.Where(source => source.Triggers.Contains(EnvironmentalAcquisitionTrigger.BeforeCapture)).ToArray();
        if (selected.Length == 0) return null;
        var remaining = TotalCaptureBudget - _timeProvider.GetElapsedTime(started);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(0, Math.Ceiling(remaining.TotalMilliseconds))), _timeProvider);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        var budget = options.Value.EnvironmentalAcquisition.BeforeCaptureWaitMilliseconds;
        // At the shared budget, one timer owns expiry and the terminal marker. An independent caller
        // timer could resume first while the shared token and monotonic elapsed time still look unexpired.
        using var callerDeadline = budget > 0 && TimeSpan.FromMilliseconds(budget) < remaining
            ? new CancellationTokenSource(TimeSpan.FromMilliseconds(budget), _timeProvider) : null;
        using var callerWait = callerDeadline is null ? CancellationTokenSource.CreateLinkedTokenSource(wait.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(wait.Token, callerDeadline.Token);
        var admissionToken = callerWait.Token;
        var dependencies = coordinator.CaptureDependencies();
        var commands = stateStore as IEnvironmentalOnDemandCommandStore
            ?? throw new InvalidOperationException("Capture acquisition requires durable command storage.");
        var id = Guid.NewGuid();
        var claims = new List<EnvironmentalCaptureSourceClaim>(selected.Length);
        var configured = options.Value.EnvironmentalAcquisition;
        // Include other configured sources that may be ahead of this request at the global acquisition gate.
        var lease = TimeSpan.FromMilliseconds((long)configured.SourceTimeoutMilliseconds *
            (configured.QueueCapacity + 2) * configuredSources.Count) + TimeSpan.FromMinutes(1);
        if (lease > TimeSpan.FromDays(configured.RetentionDays)) lease = TimeSpan.FromDays(configured.RetentionDays);
        var acquisition = new EnvironmentalCaptureAcquisition(id, claims)
        { BeforeCaptureSources = selected.Select(source => source.Id).ToArray() };
        var request = new EnvironmentalTriggerRequest(EnvironmentalAcquisitionTrigger.BeforeCapture, observedAtUtc)
        { CaptureAcquisition = acquisition, CaptureBudgetMilliseconds = budget == 0 ? null : budget };
        async Task PrepareAsync(CancellationToken stoppingToken)
        {
            // Preparation remains owned by the hosted worker after the caller's shorter optional wait.
            var preparationRemaining = TotalCaptureBudget - _timeProvider.GetElapsedTime(started);
            using var preparationDeadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(0, Math.Ceiling(preparationRemaining.TotalMilliseconds))), _timeProvider);
            using var preparation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, cancellationToken, preparationDeadline.Token);
            var preparationToken = preparation.Token;
            foreach (var source in selected)
            {
                var payload = CaptureContractJson.ComputeCanonicalJsonSha256(new { Id = id, SourceId = source.Id, source.Kind });
                var key = EnvironmentalCaptureAcquisition.Prefix(id) + payload[..32];
                EnvironmentalOnDemandCommandClaim claim;
                for (var attempt = 0; ; attempt++)
                {
                    preparationToken.ThrowIfCancellationRequested();
                    try
                    {
                        claim = await EnvironmentalCaptureStorageWork.RunAsync(() => commands.ClaimOnDemandAsync(
                            options.Value.RawIngressRoot, key, payload, source.Id, "capture-before", null, observedAtUtc,
                            lease, preparationToken), preparationToken).ConfigureAwait(false);
                        break;
                    }
                    catch (SqliteException exception) when (attempt < 3 && exception.SqliteErrorCode is
                        SQLitePCL.raw.SQLITE_BUSY or SQLitePCL.raw.SQLITE_LOCKED)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(100), preparationToken).ConfigureAwait(false);
                    }
                }
                if (claim.Disposition != EnvironmentalOnDemandClaimDisposition.Claimed)
                    throw new EnvironmentalOnDemandCommandConflictException("Capture acquisition identity already exists.");
                claims.Add(new(source, key, claim.LeaseToken) { InFlight = dependencies.GetValueOrDefault(source.Id) });
            }
            preparationToken.ThrowIfCancellationRequested();
            if (!service.TryEnqueue(request))
            {
                foreach (var claim in claims)
                    await EnvironmentalCaptureStorageWork.RunAsync(() => commands.CompleteOnDemandAsync(options.Value.RawIngressRoot, claim.Key, claim.LeaseToken,
                        new(claim.Source.Id, request.Trigger, EnvironmentalAcquisitionDisposition.Failed, "capture-queue-capacity",
                            null, observedAtUtc, observedAtUtc), preparationToken), preparationToken).ConfigureAwait(false);
                acquisition.Completion.TrySetResult();
            }
        }
        if (!service.TryPrepareCapture(acquisition, PrepareAsync))
        {
            acquisition.Completion.TrySetResult();
            return acquisition with { Unavailable = true, Claims = [] };
        }
        try
        {
            var error = await acquisition.Admission.Task.WaitAsync(admissionToken).ConfigureAwait(false);
            if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
            if (budget > 0)
            {
                // One caller wait covers preparation and completion; expiry does not cancel the captured dependency.
                await acquisition.Completion.Task.WaitAsync(admissionToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (_timeProvider.GetElapsedTime(started) >= TotalCaptureBudget && acquisition.StoragePending)
                return acquisition with { Unavailable = true, Claims = [] };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested &&
            (callerWait.IsCancellationRequested || deadline.IsCancellationRequested || _timeProvider.GetElapsedTime(started) >= TotalCaptureBudget))
        {
            // Only a shared writer deadline creates the raw-admission marker. Sensor work remains pending.
            if ((deadline.IsCancellationRequested || _timeProvider.GetElapsedTime(started) >= TotalCaptureBudget) &&
                (!acquisition.Admission.Task.IsCompletedSuccessfully || await acquisition.Admission.Task.ConfigureAwait(false) is not null || acquisition.StoragePending))
                return acquisition with { Unavailable = true, Claims = [] };
        }

        return acquisition;
    }

    public async ValueTask AfterCaptureAsync(
        RawCaptureReceipt receipt,
        CaptureLoopSubmission submission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(submission);
        if (!options.Value.EnvironmentalAcquisition.Enabled)
        {
            return;
        }
        var capture = receipt.Manifest.Descriptor.Capture;
        // Association binds the attempt once preparation is durable; optional caller waits may precede it.
        var observedAtUtc = submission.Result.AcquisitionTiming?.ReadoutCompletedUtc.ToUniversalTime()
            ?? submission.Result.Frame?.TimestampUtc.ToUniversalTime()
            ?? submission.CaptureStartedUtc.ToUniversalTime();
        var regime = submission.CycleEvidence?.SolarRegime;
        if (regime.HasValue && await stateStore.RecordCaptureRegimeAsync(
            options.Value.RawIngressRoot,
            capture.CaptureSequence,
            capture.CaptureId,
            regime.Value,
            observedAtUtc,
            cancellationToken).ConfigureAwait(false))
        {
            _ = service.TryEnqueue(new EnvironmentalTriggerRequest(
                EnvironmentalAcquisitionTrigger.RegimeChange,
                observedAtUtc,
                capture.CaptureSequence,
                capture.CaptureId));
        }
    }
}
