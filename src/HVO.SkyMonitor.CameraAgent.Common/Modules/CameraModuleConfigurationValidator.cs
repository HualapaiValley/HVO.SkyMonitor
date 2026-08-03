using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Modules;

public interface ICameraModuleConfigurationValidator
{
    void Validate(CameraModuleConfig configuration);
}
