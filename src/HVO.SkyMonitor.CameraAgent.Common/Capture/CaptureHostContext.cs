using System;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture;

internal sealed class CaptureHostContext(
    CameraModuleConfig configuration,
    IRawCaptureIngress rawCaptureIngress,
    ICaptureDistributor captureDistributor) : ICaptureHostContext
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
        _captureDistributor.NotifyCommittedCapture();
    }
}
