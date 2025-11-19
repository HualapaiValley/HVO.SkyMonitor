namespace HVO.SkyMonitor.AgentCore;

public interface ICameraModuleFactory
{
    ICameraModule Create(string moduleType);
}
