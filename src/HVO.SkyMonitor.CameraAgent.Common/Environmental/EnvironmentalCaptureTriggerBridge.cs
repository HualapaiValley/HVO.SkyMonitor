using System.Diagnostics;
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
    IOptions<CameraAgentHostOptions> options)
{
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
        var started = Stopwatch.GetTimestamp();
        var configuredSources = coordinator.Sources;
        var selected = configuredSources.Where(source => source.Triggers.Contains(EnvironmentalAcquisitionTrigger.BeforeCapture)).ToArray();
        if (selected.Length == 0) return null;
        var remaining = TotalCaptureBudget - Stopwatch.GetElapsedTime(started);
        using var deadline = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        var admissionToken = wait.Token;
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
        var acquisition = new EnvironmentalCaptureAcquisition(id, claims);
        var budget = options.Value.EnvironmentalAcquisition.BeforeCaptureWaitMilliseconds;
        var request = new EnvironmentalTriggerRequest(EnvironmentalAcquisitionTrigger.BeforeCapture, observedAtUtc)
        { CaptureAcquisition = acquisition, CaptureBudgetMilliseconds = budget == 0 ? null : budget };
        async Task PrepareAsync(CancellationToken stoppingToken)
        {
            using var preparation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, admissionToken);
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
                        claim = await commands.ClaimOnDemandAsync(options.Value.RawIngressRoot, key, payload, source.Id,
                            "capture-before", null, observedAtUtc, lease, preparationToken).ConfigureAwait(false);
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
                    await commands.CompleteOnDemandAsync(options.Value.RawIngressRoot, claim.Key, claim.LeaseToken,
                        new(claim.Source.Id, request.Trigger, EnvironmentalAcquisitionDisposition.Failed, "capture-queue-capacity",
                            null, observedAtUtc, observedAtUtc), preparationToken).ConfigureAwait(false);
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
                // Device work keeps its configured budget; durable settlement shares the two-second admission deadline.
                await acquisition.Completion.Task.WaitAsync(admissionToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) >= TotalCaptureBudget)
                return acquisition with { Unavailable = true, Claims = [] };
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The hosted worker owns all remaining writes. Raw admission persists this terminal capture fence.
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
        if (!options.Value.EnvironmentalAcquisition.Enabled || submission.CycleEvidence?.EnvironmentalUnavailable == true)
        {
            return;
        }
        var capture = receipt.Manifest.Descriptor.Capture;
        if (submission.CycleEvidence is { EnvironmentalUnavailable: false, AcquisitionAttemptId: { } acquisitionId } &&
            stateStore is SqliteEnvironmentalObservationOutbox store)
            await store.BindCaptureAcquisitionAsync(options.Value.RawIngressRoot, acquisitionId, capture.CaptureId, cancellationToken).ConfigureAwait(false);
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
