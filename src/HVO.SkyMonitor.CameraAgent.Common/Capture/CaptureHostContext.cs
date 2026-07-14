using System;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture;

internal sealed class CaptureHostContext(
    CameraModuleConfig configuration,
    FrameProcessingChannel channel,
    IRawCaptureIngress rawCaptureIngress) : ICaptureHostContext
{
    private readonly CameraModuleConfig _configuration = configuration;
    private readonly FrameProcessingChannel _channel = channel;
    private readonly IRawCaptureIngress _rawCaptureIngress = rawCaptureIngress;

    public CameraModuleConfig Configuration => _configuration;

    public async ValueTask PublishAsync(CaptureLoopSubmission submission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var receipt = await _rawCaptureIngress.AcceptAsync(_configuration, submission, cancellationToken).ConfigureAwait(false);
        if (receipt is null)
        {
            await _channel.WriteAsync(new FrameProcessingItem(_configuration, submission), cancellationToken).ConfigureAwait(false);
            return;
        }

        var lightweightSubmission = submission with
        {
            Result = submission.Result with { Frame = null, Artifacts = null }
        };
        var queued = _channel.TryWrite(new FrameProcessingItem(_configuration, lightweightSubmission, receipt));
        (_rawCaptureIngress as IRawIngressWakeupReporter)?.ReportWakeup(queued);
    }
}
