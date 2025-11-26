using System;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture;

internal sealed class CaptureHostContext(CameraModuleConfig configuration, FrameProcessingChannel channel) : ICaptureHostContext
{
    private readonly CameraModuleConfig _configuration = configuration;
    private readonly FrameProcessingChannel _channel = channel;

    public CameraModuleConfig Configuration => _configuration;

    public ValueTask PublishAsync(CaptureLoopSubmission submission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        return _channel.WriteAsync(new FrameProcessingItem(_configuration, submission), cancellationToken);
    }
}
