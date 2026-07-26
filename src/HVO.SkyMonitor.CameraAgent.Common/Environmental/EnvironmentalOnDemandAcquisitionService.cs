using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Environmental;

public sealed partial class EnvironmentalOnDemandAcquisitionService(
    EnvironmentalAcquisitionCoordinator coordinator,
    IEnvironmentalOnDemandCommandStore commands,
    IOptions<CameraAgentHostOptions> options,
    TimeProvider timeProvider,
    ILogger<EnvironmentalOnDemandAcquisitionService>? logger = null)
{
    public async ValueTask<EnvironmentalOnDemandAcquisitionResult> AcquireAsync(
        string sourceId,
        string idempotencyKey,
        string actorId,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceId) || sourceId.Length > 128 ||
            !ValidText(idempotencyKey, 128) || !ValidText(actorId, 256) ||
            reason is not null && !ValidText(reason, 128))
        {
            throw new ArgumentException("The environmental on-demand command is invalid.");
        }
        var source = coordinator.Sources.SingleOrDefault(candidate =>
            string.Equals(candidate.Id, sourceId, StringComparison.Ordinal));
        if (source is null)
        {
            throw new KeyNotFoundException("The environmental source is not configured.");
        }
        if (!source.Triggers.Contains(EnvironmentalAcquisitionTrigger.OnDemand))
        {
            throw new InvalidOperationException("The environmental source does not support on-demand acquisition.");
        }
        var payloadSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            Schema = "environmental-on-demand-command-v1",
            SourceId = sourceId,
            ActorId = actorId,
            Reason = reason
        });
        var claim = await commands.ClaimOnDemandAsync(
            options.Value.RawIngressRoot,
            idempotencyKey,
            payloadSha256,
            sourceId,
            actorId,
            reason,
            timeProvider.GetUtcNow(),
            TimeSpan.FromMilliseconds(options.Value.EnvironmentalAcquisition.SourceTimeoutMilliseconds).Add(
                TimeSpan.FromSeconds(30)),
            cancellationToken).ConfigureAwait(false);
        if (claim.Disposition == EnvironmentalOnDemandClaimDisposition.Completed)
        {
            var receipt = claim.Receipt
                ?? throw new InvalidDataException("Completed environmental command omitted its receipt.");
            if (logger is not null)
            {
                OnDemandSettled(logger, receipt.Disposition.ToString(), receipt.Reason, replayed: true);
            }
            return new EnvironmentalOnDemandAcquisitionResult(receipt, Replayed: true);
        }
        if (claim.Disposition == EnvironmentalOnDemandClaimDisposition.Busy)
        {
            throw new EnvironmentalOnDemandCommandBusyException(
                "The environmental on-demand command is already running.");
        }
        try
        {
            var receipt = await coordinator.AcquireSourceAsync(
                sourceId,
                EnvironmentalAcquisitionTrigger.OnDemand,
                claim.ObservedAtUtc,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (receipt.Disposition == EnvironmentalAcquisitionDisposition.Coalesced)
            {
                throw new EnvironmentalOnDemandCommandBusyException(
                    "The environmental source is already running and retained one pending acquisition.");
            }
            await commands.CompleteOnDemandAsync(
                options.Value.RawIngressRoot,
                idempotencyKey,
                claim.LeaseToken,
                receipt,
                cancellationToken).ConfigureAwait(false);
            if (logger is not null)
            {
                OnDemandSettled(logger, receipt.Disposition.ToString(), receipt.Reason, replayed: false);
            }
            return new EnvironmentalOnDemandAcquisitionResult(receipt, Replayed: false);
        }
        catch
        {
            await ReleaseClaimAsync(idempotencyKey, claim.LeaseToken).ConfigureAwait(false);
            throw;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort lease release must not hide the acquisition or settlement failure.")]
    private async ValueTask ReleaseClaimAsync(string idempotencyKey, string leaseToken)
    {
        try
        {
            await commands.ReleaseOnDemandAsync(
                options.Value.RawIngressRoot, idempotencyKey, leaseToken, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    [LoggerMessage(2529, LogLevel.Information,
        "Environmental on-demand acquisition settled with outcome {Outcome}, reason {Reason}, and replayed {Replayed}.")]
    private static partial void OnDemandSettled(ILogger logger, string outcome, string reason, bool replayed);

    private static bool ValidText(string value, int maximumLength)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
            value == value.Trim() && !value.Any(char.IsControl);
}
