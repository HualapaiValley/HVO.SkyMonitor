using System;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture;

internal sealed class CaptureHostContext(
    CameraModuleConfig configuration,
    IRawCaptureIngress rawCaptureIngress,
    ICaptureDistributor captureDistributor,
    EnvironmentalCaptureTriggerBridge? environmentalTriggers = null) : ICaptureHostContext
{
    private readonly CameraModuleConfig _configuration = configuration;
    private readonly IRawCaptureIngress _rawCaptureIngress = rawCaptureIngress;
    private readonly ICaptureDistributor _captureDistributor = captureDistributor;

    public CameraModuleConfig Configuration => _configuration;

    public async ValueTask PublishAsync(CaptureLoopSubmission submission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var receipt = await _rawCaptureIngress.AcceptAsync(_configuration, submission, cancellationToken).ConfigureAwait(false);
        if (receipt is null)
        {
            await _captureDistributor.ProcessEphemeralAsync(
                _configuration, submission, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (environmentalTriggers is not null)
        {
            await environmentalTriggers.AfterCaptureAsync(receipt, submission, cancellationToken).ConfigureAwait(false);
        }
        _captureDistributor.NotifyCommittedCapture();
    }
}
