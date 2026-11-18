// Deliberate violations compiled into this test assembly only (#1768 S-L4). The control sits where the custom-script
// rule selects, and each dependency is an acquisition-side type outside HVO.SkyMonitor.CameraAgent.Common.
namespace HVO.SkyMonitor.AgentCore
{
    internal sealed class RigTarget;
}

namespace HVO.SkyMonitor.CameraAgent.Modules.Zwo
{
    internal sealed class SensorTarget;
}

namespace HVO.SkyMonitor.CameraAgent.Replay
{
    internal sealed class ReplayTarget;
}

namespace HVO.SkyMonitor.Imaging.Acquisition
{
    internal sealed class ExposureTarget;
}

namespace HVO.SkyMonitor.Astronomy
{
    internal sealed class CaptureWindowTarget;
}

namespace HVO.SkyMonitor.CameraAgent.Common.Automation.Scripts
{
    internal static class ScriptBoundaryDependencyControl
    {
        internal static object[] Create() =>
        [
            new HVO.SkyMonitor.AgentCore.RigTarget(),
            new HVO.SkyMonitor.CameraAgent.Modules.Zwo.SensorTarget(),
            new HVO.SkyMonitor.CameraAgent.Replay.ReplayTarget(),
            new HVO.SkyMonitor.Imaging.Acquisition.ExposureTarget(),
            new HVO.SkyMonitor.Astronomy.CaptureWindowTarget(),
        ];
    }
}
