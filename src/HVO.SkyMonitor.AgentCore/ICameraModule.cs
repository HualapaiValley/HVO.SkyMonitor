using System.Threading;
using System.Threading.Tasks;

namespace HVO.SkyMonitor.AgentCore;

public interface ICameraModule : IAsyncDisposable
{
    string Id { get; }

    string DisplayName { get; }

    string ModuleType { get; }

    CameraModuleCapabilities Capabilities { get; }

    Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken);

    Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken);
}
