using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface IDeviceHeartbeatService
{
    Task<DeviceHeartbeatResult> RecordHeartbeatAsync(DeviceHeartbeatRequest request, CancellationToken cancellationToken = default);
}

internal sealed record DeviceHeartbeatRequest(
    string DeviceId,
    string DeviceKey,
    string? SoftwareVersion,
    string? AgentState,
    double? TemperatureCelsius,
    double? CpuPercent);

internal sealed record DeviceHeartbeatResult(
    Guid RegistrationId,
    Guid DevicePublicId,
    Guid ObservatoryId,
    string ObservatoryName,
    string FriendlyName,
    DateTimeOffset ServerTimeUtc,
    int RecommendedHeartbeatSeconds);

internal sealed class DeviceHeartbeatService(
    IDeviceCredentialValidator credentialValidator,
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<DeviceHeartbeatService> logger) : IDeviceHeartbeatService
{
    private const int RecommendedHeartbeatSeconds = 60;

    public async Task<DeviceHeartbeatResult> RecordHeartbeatAsync(DeviceHeartbeatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var registration = await credentialValidator
            .ValidateAsync(request.DeviceId, request.DeviceKey, cancellationToken)
            .ConfigureAwait(false);

        var now = timeProvider.GetUtcNow();
        registration.LastSeenUtc = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Heartbeat received for device {DeviceId} ({FriendlyName})",
                registration.DeviceId,
                registration.FriendlyName);
        }

        if (!string.IsNullOrWhiteSpace(request.SoftwareVersion) || !string.IsNullOrWhiteSpace(request.AgentState))
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    "Device {DeviceId} status version={Version} state={State} cpu={Cpu} temp={Temp}",
                    registration.DeviceId,
                    request.SoftwareVersion,
                    request.AgentState,
                    request.CpuPercent,
                    request.TemperatureCelsius);
            }
        }

        if (registration.DevicePublicId is null)
        {
            throw new DeviceRegistrationException("Device is not fully activated. Complete bootstrap before sending heartbeats.");
        }

        return new DeviceHeartbeatResult(
            registration.Id,
            registration.DevicePublicId.Value,
            registration.ObservatoryId,
            registration.ObservatoryName,
            registration.FriendlyName,
            now,
            RecommendedHeartbeatSeconds);
    }
}
