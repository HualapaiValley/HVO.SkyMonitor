using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Configuration;

public interface ICameraAgentConfigurationLoader
{
    Task<CameraModuleConfig> LoadAsync(CancellationToken cancellationToken);
}
