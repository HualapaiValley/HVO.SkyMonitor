using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Configuration;

public sealed class CameraAgentConfigurationAccessor : ICameraAgentConfigurationAccessor
{
    private readonly TaskCompletionSource<CameraModuleConfig> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsConfigured => _tcs.Task.IsCompletedSuccessfully;

    public void SetConfiguration(CameraModuleConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!_tcs.TrySetResult(config))
        {
            throw new InvalidOperationException("Camera agent configuration has already been set.");
        }
    }

    public async ValueTask<CameraModuleConfig> WaitForConfigurationAsync(CancellationToken cancellationToken)
    {
        await _tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await _tcs.Task.ConfigureAwait(false);
    }
}
