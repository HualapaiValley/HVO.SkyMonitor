using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Configuration;

public interface ICameraAgentConfigurationAccessor
{
    bool IsConfigured { get; }

    void SetConfiguration(CameraModuleConfig config);

    ValueTask<CameraModuleConfig> WaitForConfigurationAsync(CancellationToken cancellationToken);
}
