using HVO.SkyMonitor.CameraAgent.Common.Configuration;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal sealed class ProvisionedCaptureAgentIdentityProvider(IDeviceIdentityStore identityStore) :
    ICaptureAgentIdentityProvider
{
    public async ValueTask<string?> GetAgentIdAsync(CancellationToken cancellationToken)
        => (await identityStore.GetOrCreateAsync(cancellationToken).ConfigureAwait(false)).DeviceId;
}
