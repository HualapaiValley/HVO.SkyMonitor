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

/// <summary>Validates transport-neutral module configuration without acquiring hardware or native resources.</summary>
public interface ICameraModuleConfigurationPreflight
{
    void ValidateConfiguration(CameraModuleConfig configuration);
}

/// <summary>Applies host-selected controls before the next acquisition begins.</summary>
public interface ICameraSetpointController
{
    /// <summary>Applies a complete setpoint and returns the module-observed UTC application time.</summary>
    ValueTask<DateTimeOffset> ApplySetpointAsync(
        CaptureSetpoint setpoint,
        CancellationToken cancellationToken);
}
