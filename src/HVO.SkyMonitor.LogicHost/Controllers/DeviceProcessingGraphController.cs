using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.LogicHost.Controllers;

[ApiController]
[Route("api/device/processing-graphs")]
[AllowAnonymous]
internal sealed class DeviceProcessingGraphController(
    IDeviceCredentialValidator credentialValidator,
    IProcessingGraphDeliveryService delivery) : ControllerBase
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    [HttpPost("pull")]
    [RequestSizeLimit(64 * 1024)]
    public async Task<ActionResult<ProcessingGraphProposalPollResponseV1>> PullAsync(
        [FromHeader(Name = "X-HVO-Device-Id"), Required, StringLength(128)] string deviceId,
        [FromHeader(Name = "X-HVO-Device-Key"), Required, StringLength(512)] string deviceKey,
        [FromBody] JsonElement payload,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = Deserialize<ProcessingGraphProposalPollRequestV1>(payload);
            var registration = await credentialValidator.ValidateAsync(deviceId, deviceKey, cancellationToken)
                .ConfigureAwait(false);
            var response = await delivery.PullAsync(registration, request, cancellationToken).ConfigureAwait(false);
            return new JsonResult(response, SerializerOptions);
        }
        catch (DeviceRegistrationException)
        {
            return Unauthorized(new ProblemDetails { Title = "Device processing graph credentials were rejected." });
        }
        catch (JsonException)
        {
            return BadRequest(new ProblemDetails { Title = "The processing graph proposal poll is invalid." });
        }
        catch (NotSupportedException)
        {
            return BadRequest(new ProblemDetails { Title = "The processing graph proposal poll is invalid." });
        }
        catch (ArgumentException)
        {
            return BadRequest(new ProblemDetails { Title = "The processing graph proposal poll is invalid." });
        }
    }

    [HttpPost("facts")]
    [RequestSizeLimit(32 * 1024)]
    public async Task<ActionResult<ProcessingGraphFactAcknowledgementV1>> AcknowledgeAsync(
        [FromHeader(Name = "X-HVO-Device-Id"), Required, StringLength(128)] string deviceId,
        [FromHeader(Name = "X-HVO-Device-Key"), Required, StringLength(512)] string deviceKey,
        [FromBody] JsonElement payload,
        CancellationToken cancellationToken)
    {
        try
        {
            var fact = Deserialize<ProcessingGraphDeliveryFactV1>(payload);
            var registration = await credentialValidator.ValidateAsync(deviceId, deviceKey, cancellationToken)
                .ConfigureAwait(false);
            var response = await delivery.AcknowledgeAsync(registration, fact, cancellationToken).ConfigureAwait(false);
            return new JsonResult(response, SerializerOptions);
        }
        catch (DeviceRegistrationException)
        {
            return Unauthorized(new ProblemDetails { Title = "Device processing graph credentials were rejected." });
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new ProblemDetails { Title = "The processing graph proposal was not found." });
        }
        catch (JsonException)
        {
            return BadRequest(new ProblemDetails { Title = "The processing graph delivery fact is invalid." });
        }
        catch (NotSupportedException)
        {
            return BadRequest(new ProblemDetails { Title = "The processing graph delivery fact is invalid." });
        }
        catch (ArgumentException)
        {
            return BadRequest(new ProblemDetails { Title = "The processing graph delivery fact is invalid." });
        }
        catch (InvalidOperationException)
        {
            return Conflict(new ProblemDetails { Title = "The processing graph delivery fact conflicts with durable state." });
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            NumberHandling = JsonNumberHandling.Strict,
            PropertyNameCaseInsensitive = false,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }

    private static T Deserialize<T>(JsonElement payload)
        where T : class
        => payload.Deserialize<T>(SerializerOptions)
            ?? throw new JsonException("The JSON request body cannot be null.");
}
