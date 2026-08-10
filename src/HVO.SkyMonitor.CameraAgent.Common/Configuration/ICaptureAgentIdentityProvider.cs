namespace HVO.SkyMonitor.CameraAgent.Common.Configuration;

public interface ICaptureAgentIdentityProvider
{
    ValueTask<string?> GetAgentIdAsync(CancellationToken cancellationToken);
}
