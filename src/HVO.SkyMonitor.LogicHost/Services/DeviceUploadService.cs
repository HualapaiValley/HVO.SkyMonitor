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
    string? FileName,
    DateTimeOffset? CapturedAtUtc = null,
    int? RigProfileVersion = null);

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

        var storageReference = $"stubs://uploads/{Guid.NewGuid():N}";
        var capturedAtUtc = request.CapturedAtUtc ?? now;
        var rigProfileVersion = request.RigProfileVersion ?? registration.CurrentRigProfileVersion;

        if (registration.DevicePublicId is not null)
        {
            await dbContext.DeviceImageUploads.AddAsync(new DeviceImageUpload
            {
                RegistrationId = registration.Id,
                DevicePublicId = registration.DevicePublicId.Value,
                ObservatoryId = registration.ObservatoryId,
                RigProfileVersion = rigProfileVersion,
                CapturedAtUtc = capturedAtUtc,
                ReceivedAtUtc = now,
                ContentType = request.ContentType,
                FileName = request.FileName,
                PayloadBase64Length = request.PayloadBase64.Length,
                StorageReference = storageReference
            }, cancellationToken).ConfigureAwait(false);
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Received stub upload from {DeviceId} ({FriendlyName}) stored at {StorageRef} ({ContentType}, bytes={Length})",
                registration.DeviceId,
                registration.FriendlyName,
                storageReference,
                request.ContentType,
                request.PayloadBase64.Length);
        }

        return new DeviceUploadResult(
            registration.Id,
            registration.ObservatoryId,
            storageReference,
            now);
    }
}
