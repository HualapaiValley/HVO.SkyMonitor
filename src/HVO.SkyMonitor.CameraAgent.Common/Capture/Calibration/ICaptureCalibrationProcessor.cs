using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Frames;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;

public interface ICaptureCalibrationProcessor
{
    ValueTask<CameraFrame> ApplyCalibrationAsync(
        CameraModuleConfig config,
        CameraFrame frame,
        CancellationToken cancellationToken);
}

internal sealed class NullCaptureCalibrationProcessor : ICaptureCalibrationProcessor
{
    public ValueTask<CameraFrame> ApplyCalibrationAsync(
        CameraModuleConfig config,
        CameraFrame frame,
        CancellationToken cancellationToken) => ValueTask.FromResult(frame);
}
