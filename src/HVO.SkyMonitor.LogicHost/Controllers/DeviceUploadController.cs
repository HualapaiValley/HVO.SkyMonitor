using System.ComponentModel.DataAnnotations;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.LogicHost.Controllers;

[ApiController]
[Route("api/device/upload")]
[AllowAnonymous]
internal sealed class DeviceUploadController(
    IDeviceUploadService uploadService,
    ILogger<DeviceUploadController> logger) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(50 * 1024 * 1024)]
    public async Task<ActionResult<DeviceUploadResponse>> UploadAsync(
        DeviceUploadRequestDto request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        try
        {
            var result = await uploadService.RecordUploadAsync(new DeviceUploadRequest(
                request.DeviceId,
                request.DeviceKey,
                request.ContentType,
                request.PayloadBase64,
                request.FileName,
                request.CapturedAtUtc,
                request.RigProfileVersion), cancellationToken).ConfigureAwait(false);

            return Accepted(new DeviceUploadResponse(
                result.RegistrationId,
                result.ObservatoryId,
                result.AcceptedAtUtc));
        }
        catch (DeviceRegistrationException ex)
        {
            logger.LogWarning(ex, "Upload rejected for {DeviceId}", request.DeviceId);
            return Unauthorized(new ProblemDetails
            {
                Title = "Device upload rejected",
                Detail = ex.Message,
                Status = StatusCodes.Status401Unauthorized
            });
        }
    }

    internal sealed record DeviceUploadRequestDto(
        [Required, StringLength(128)] string DeviceId,
        [Required, StringLength(256)] string DeviceKey,
        [Required, StringLength(128)] string ContentType,
        [Required, StringLength(4194304)] string PayloadBase64,
        [StringLength(256)] string? FileName,
        DateTimeOffset? CapturedAtUtc,
        [Range(1, int.MaxValue)] int? RigProfileVersion);

    internal sealed record DeviceUploadResponse(
        Guid RegistrationId,
        Guid ObservatoryId,
        DateTimeOffset AcceptedAtUtc);
}
