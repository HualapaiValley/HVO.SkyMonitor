using HVO.SkyMonitor.CameraAgent.Common.Deployment;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Automation;

/// <summary>
/// Reads the durable capture sequence a capture-relative trigger is measured against. It is a
/// read-only observation of already-published durable state on the automation runner's own timer;
/// nothing here participates in exposure admission, acquisition timing, or the live processing slot.
/// </summary>
public interface ILocalAutomationCaptureSequenceSource
{
    /// <summary>The highest durably recorded capture sequence, or null when none exists yet.</summary>
    ValueTask<long?> GetCaptureSequenceAsync(CancellationToken cancellationToken);
}

/// <summary>The delivered source, backed by the existing deployment continuity reader.</summary>
public sealed class DeploymentContinuityCaptureSequenceSource(
    DeploymentContinuityReader reader,
    IOptions<CameraAgentHostOptions> options) : ILocalAutomationCaptureSequenceSource
{
    public async ValueTask<long?> GetCaptureSequenceAsync(CancellationToken cancellationToken)
    {
        var sequences = await reader
            .ReadCaptureSequenceContinuityAsync(options.Value.RawIngressRoot, cancellationToken)
            .ConfigureAwait(false);
        return sequences.Count == 0 ? null : sequences.Max(static value => value.LastSequence);
    }
}
