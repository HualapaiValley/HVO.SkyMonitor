using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface IDeviceRigProfileService
{
    Task<DeviceRigProfileUpsertResult> UpsertAsync(DeviceRigProfileUpsertRequest request, CancellationToken cancellationToken = default);
}

internal sealed record DeviceRigProfileUpsertRequest(
    string DeviceId,
    string DeviceKey,
    string RigConfigJson,
    string? SoftwareVersion = null);

internal sealed record DeviceRigProfileUpsertResult(
    Guid RegistrationId,
    Guid DevicePublicId,
    Guid ObservatoryId,
    int RigProfileVersion,
    string RigProfileHash,
    DateTimeOffset AcceptedAtUtc,
    bool CreatedNewVersion);

internal sealed class DeviceRigProfileService(
    IDeviceCredentialValidator credentialValidator,
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<DeviceRigProfileService> logger) : IDeviceRigProfileService
{
    private static readonly JsonSerializerOptions RigSerializerOptions = CreateRigSerializerOptions();

    public async Task<DeviceRigProfileUpsertResult> UpsertAsync(DeviceRigProfileUpsertRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DeviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DeviceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RigConfigJson);

        var registration = await credentialValidator
            .ValidateAsync(request.DeviceId, request.DeviceKey, cancellationToken)
            .ConfigureAwait(false);

        if (registration.DevicePublicId is null)
        {
            throw new DeviceRegistrationException("Device is not fully activated. Complete bootstrap before submitting rig profiles.");
        }

        var now = timeProvider.GetUtcNow();

        var canonicalJson = CanonicalizeJson(request.RigConfigJson);
        var configHash = ComputeSha256Hex(canonicalJson);
        var identity = CreateIdentity(canonicalJson);

        var latest = await dbContext.Set<DeviceRigProfile>()
            .Where(profile => profile.DevicePublicId == registration.DevicePublicId.Value)
            .OrderByDescending(profile => profile.Version)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (latest is not null && string.Equals(latest.ConfigHash, configHash, StringComparison.Ordinal))
        {
            registration.LastSeenUtc = now;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return new DeviceRigProfileUpsertResult(
                registration.Id,
                registration.DevicePublicId.Value,
                registration.ObservatoryId,
                latest.Version,
                latest.ConfigHash,
                now,
                CreatedNewVersion: false);
        }

        var nextVersion = (latest?.Version ?? 0) + 1;

        var profile = new DeviceRigProfile
        {
            RegistrationId = registration.Id,
            DevicePublicId = registration.DevicePublicId.Value,
            ObservatoryId = registration.ObservatoryId,
            Version = nextVersion,
            ConfigHash = configHash,
            ConfigJson = canonicalJson,
            ProfileName = identity.Name,
            ProfileVersion = identity.Version,
            ProfileSha256 = identity.Sha256,
            SoftwareVersion = string.IsNullOrWhiteSpace(request.SoftwareVersion) ? null : request.SoftwareVersion.Trim(),
            CreatedAtUtc = now,
            EffectiveFromUtc = now
        };

        await dbContext.AddAsync(profile, cancellationToken).ConfigureAwait(false);

        registration.CurrentRigProfileVersion = nextVersion;
        registration.CurrentRigProfileHash = configHash;
        registration.CurrentRigProfileUpdatedAtUtc = now;
        registration.LastSeenUtc = now;

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Accepted rig profile v{Version} for device {DeviceId} ({FriendlyName})",
                nextVersion,
                registration.DeviceId,
                registration.FriendlyName);
        }

        return new DeviceRigProfileUpsertResult(
            registration.Id,
            registration.DevicePublicId.Value,
            registration.ObservatoryId,
            nextVersion,
            configHash,
            now,
            CreatedNewVersion: true);
    }

    private static string ComputeSha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes);
    }

    private static ProfileIdentityDescriptor CreateIdentity(string canonicalJson)
    {
        var rig = JsonSerializer.Deserialize<CameraRigConfig>(canonicalJson, RigSerializerOptions);
        if (rig?.Sensor is null || rig.Optics is null || rig.Orientation is null || rig.Pipeline is null ||
            string.IsNullOrWhiteSpace(rig.ProfileVersion) || rig.ProfileVersion.Length > 128)
        {
            throw new JsonException("RigConfigJson must contain a complete current camera rig configuration.");
        }
        if (string.IsNullOrWhiteSpace(rig.Sensor.Name)
            || rig.Sensor.WidthPixels <= 0
            || rig.Sensor.HeightPixels <= 0
            || !double.IsFinite(rig.Sensor.PixelSizeMicrons)
            || rig.Sensor.PixelSizeMicrons <= 0
            || !Enum.IsDefined(rig.Sensor.ColorMode)
            || !Enum.IsDefined(rig.Sensor.PixelFormat)
            || string.IsNullOrWhiteSpace(rig.Optics.ProjectionModel)
            || !double.IsFinite(rig.Optics.FieldOfViewDegrees)
            || rig.Optics.FieldOfViewDegrees <= 0
            || !double.IsFinite(rig.Orientation.BoresightAltitudeDegrees)
            || rig.Orientation.BoresightAltitudeDegrees is < -90 or > 90
            || !double.IsFinite(rig.Orientation.BoresightAzimuthDegrees)
            || !double.IsFinite(rig.Orientation.RollAdjustmentDegrees)
            || rig.Pipeline.CaptureInterval <= TimeSpan.Zero
            || rig.Pipeline.DayExposure <= TimeSpan.Zero
            || rig.Pipeline.NightExposure <= TimeSpan.Zero
            || !double.IsFinite(rig.Pipeline.DayGain)
            || rig.Pipeline.DayGain < 0
            || !double.IsFinite(rig.Pipeline.NightGain)
            || rig.Pipeline.NightGain < 0
            || !Enum.IsDefined(rig.Pipeline.CadenceMode))
        {
            throw new JsonException("RigConfigJson contains an invalid current camera rig configuration.");
        }
        if (rig.Readout is not null)
        {
            try
            {
                _ = SensorReadoutResolver.Resolve(rig.Sensor, rig.Readout);
            }
            catch (Exception exception) when (exception is ArgumentException or OverflowException)
            {
                throw new JsonException("RigConfigJson contains an invalid sensor readout.", exception);
            }
        }
        return new ProfileIdentityDescriptor("rig", rig.ProfileVersion, CameraRigProfileIdentity.ComputeSha256(rig));
    }

    private static JsonSerializerOptions CreateRigSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            RespectRequiredConstructorParameters = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static string CanonicalizeJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, document.RootElement);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.Number:
                if (element.TryGetInt64(out var i64))
                {
                    writer.WriteNumberValue(i64);
                }
                else if (element.TryGetDouble(out var d))
                {
                    writer.WriteNumberValue(d);
                }
                else
                {
                    writer.WriteRawValue(element.GetRawText());
                }
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;

            default:
                writer.WriteRawValue(element.GetRawText());
                break;
        }
    }
}
