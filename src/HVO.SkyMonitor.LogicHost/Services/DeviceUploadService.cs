using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface IDeviceUploadService
{
    Task<DeviceUploadResult> RecordUploadAsync(DeviceUploadRequest request, CancellationToken cancellationToken = default);
}

internal sealed record DeviceUploadRequest(
    string DeviceId,
    string DeviceKey,
    string ContentType,
    string PayloadBase64,
    string? FileName);

internal sealed record DeviceUploadResult(
    Guid RegistrationId,
    Guid ObservatoryId,
    string StorageReference,
    DateTimeOffset AcceptedAtUtc);

internal sealed class DeviceUploadService(
    IDeviceCredentialValidator credentialValidator,
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<DeviceUploadService> logger) : IDeviceUploadService
{
    public async Task<DeviceUploadResult> RecordUploadAsync(DeviceUploadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ContentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PayloadBase64);

        var registration = await credentialValidator
            .ValidateAsync(request.DeviceId, request.DeviceKey, cancellationToken)
            .ConfigureAwait(false);

        var now = timeProvider.GetUtcNow();
        registration.LastSeenUtc = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var storageReference = $"stubs://uploads/{Guid.NewGuid():N}";
        logger.LogInformation(
            "Received stub upload from {DeviceId} ({FriendlyName}) stored at {StorageRef} ({ContentType}, bytes={Length})",
            registration.DeviceId,
            registration.FriendlyName,
            storageReference,
            request.ContentType,
            request.PayloadBase64.Length);

        return new DeviceUploadResult(
            registration.Id,
            registration.ObservatoryId,
            storageReference,
            now);
    }
}
