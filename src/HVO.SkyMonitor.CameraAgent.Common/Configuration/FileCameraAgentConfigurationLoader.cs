using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Configuration;

public sealed class FileCameraAgentConfigurationLoader(
    IOptions<CameraAgentHostOptions> options,
    ILogger<FileCameraAgentConfigurationLoader> logger,
    IDeploymentLocationStore? deploymentLocationStore = null,
    ICaptureAgentIdentityProvider? captureAgentIdentityProvider = null) : ICameraAgentConfigurationLoader
{
    private const string SampleAgentId = "replace-with-registered-device-id";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        Converters =
        {
            new JsonStringEnumConverter<CaptureCadenceMode>(allowIntegerValues: false),
            new JsonStringEnumConverter<AutomaticControlOwnership>(allowIntegerValues: false),
            new JsonStringEnumConverter<CaptureMeteringCfaSelection>(allowIntegerValues: false),
            new JsonStringEnumConverter<ExposureGainPreference>(allowIntegerValues: false),
            new JsonStringEnumConverter<SampleByteOrder>(allowIntegerValues: false),
            new JsonStringEnumConverter<CaptureProcessingPersistenceMode>(allowIntegerValues: false),
            new JsonStringEnumConverter()
        }
    };

    private readonly CameraAgentHostOptions _options = options.Value;
    private readonly ILogger<FileCameraAgentConfigurationLoader> _logger = logger;
    private readonly IDeploymentLocationStore? _deploymentLocationStore = deploymentLocationStore;

    public async Task<CameraModuleConfig> LoadAsync(CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(_options.ConfigFilePath);
        if (!File.Exists(path))
        {
            _logger.ConfigurationFileMissing(path);
            throw new FileNotFoundException("Camera agent configuration file was not found.", path);
        }

        using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<CameraModuleDocument>(stream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);

        if (document is null)
        {
            _logger.ConfigurationFileInvalid(path);
            throw new InvalidOperationException("Camera agent configuration is invalid.");
        }

        var configuredAgentId = string.IsNullOrWhiteSpace(_options.AgentId) ? document.AgentId : _options.AgentId;
        var agentId = configuredAgentId;
        if (_options.CentralIntegration.Mode == CentralIntegrationMode.Enabled)
        {
            if (captureAgentIdentityProvider is null)
            {
                throw new InvalidOperationException(
                    "Central integration requires a persistent CameraAgent device identity provider.");
            }

            var provisionedAgentId = await captureAgentIdentityProvider.GetAgentIdAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(provisionedAgentId))
            {
                throw new InvalidOperationException(
                    "The persistent CameraAgent device identity does not contain a valid device identifier.");
            }

            if (string.IsNullOrWhiteSpace(configuredAgentId) ||
                string.Equals(configuredAgentId, SampleAgentId, StringComparison.Ordinal))
            {
                agentId = provisionedAgentId;
            }
            else if (!string.Equals(configuredAgentId, provisionedAgentId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Configured AgentId does not match the persistent CameraAgent device identity.");
            }
        }
        var locationSeed = new DeploymentLocationSeed(
            _options.DeploymentLocation.LocationId,
            _options.DeploymentLocation.Source,
            _options.DeploymentLocation.HorizontalAccuracyMeters,
            _options.DeploymentLocation.EffectiveFromUtc,
            _options.DeploymentLocation.EffectiveUntilUtc,
            _options.Observatory,
            _options.DeploymentLocation.SourceKind);
        var location = DeploymentLocationSnapshot.Create(
            locationSeed.LocationId,
            1,
            locationSeed.Source,
            locationSeed.HorizontalAccuracyMeters,
            locationSeed.EffectiveFromUtc ?? DateTimeOffset.UnixEpoch,
            locationSeed.EffectiveUntilUtc,
            locationSeed.Coordinates.LatitudeDegrees,
            locationSeed.Coordinates.LongitudeDegrees,
            locationSeed.Coordinates.ElevationMeters,
            locationSeed.Coordinates.TimeZoneId);
        var config = new CameraModuleConfig(
            Observatory: _options.Observatory,
            Module: document.Module,
            Rig: document.Rig,
            Pipeline: document.Pipeline,
            AgentId: agentId)
        {
            DeploymentLocation = location,
            Schedule = document.Schedule
        };

        ValidateCurrentFileContract(config);
        ValidateConfig(config);
        if (_deploymentLocationStore is not null)
        {
            location = await _deploymentLocationStore.InitializeAsync(locationSeed, cancellationToken).ConfigureAwait(false);
            config = config with { DeploymentLocation = location };
            if (config.Schedule is not null)
            {
                ValidateScheduleCompatibility(config);
            }
        }
        _logger.ConfigurationLoaded(path);
        return config;
    }

    internal static void ValidateConfig(CameraModuleConfig config)
    {
        if (config.Module is null || string.IsNullOrWhiteSpace(config.Module.Type))
        {
            throw new InvalidOperationException("Camera module type must be specified.");
        }

        if (string.IsNullOrWhiteSpace(config.AgentId))
        {
            throw new InvalidOperationException("AgentId must be specified and match the registered device identity.");
        }

        var locationValidation = config.DeploymentLocation?.Validate()
            ?? CaptureContractValidationResult.Failure(CaptureContractReasonCodes.InvalidLocation, "location");
        if (!locationValidation.IsValid)
        {
            throw new InvalidOperationException(
                $"Deployment location is invalid ({locationValidation.ReasonCode}, {locationValidation.FieldPath}).");
        }

        var scheduleValidation = CaptureScheduleContract.Validate(config.Schedule);
        if (!scheduleValidation.IsValid)
        {
            throw new InvalidOperationException(
                $"Capture schedule is invalid ({scheduleValidation.ReasonCode}, {scheduleValidation.FieldPath}).");
        }
        ValidateScheduleCompatibility(config);

        if (config.Rig.Sensor.WidthPixels <= 0 || config.Rig.Sensor.HeightPixels <= 0)
        {
            throw new InvalidOperationException("Sensor resolution must be greater than zero.");
        }
        if (config.Rig.Readout is not null)
        {
            try
            {
                _ = SensorReadoutResolver.Resolve(config.Rig.Sensor, config.Rig.Readout);
                _ = RigProjectionContextFactory.Create(config.Rig);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
            {
                throw new InvalidOperationException("Configured sensor readout is invalid.", exception);
            }
        }

        if (config.Rig.Pipeline.CaptureInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("CaptureInterval must be greater than zero.");
        }
        var initialBackoff = config.Rig.Pipeline.CaptureFailureInitialDelay ?? TimeSpan.FromMilliseconds(250);
        var maximumBackoff = config.Rig.Pipeline.CaptureFailureMaximumDelay ?? TimeSpan.FromSeconds(30);
        if (initialBackoff <= TimeSpan.Zero || maximumBackoff < initialBackoff)
        {
            throw new InvalidOperationException(
                "Capture failure backoff delays must be positive and the maximum must not be less than the initial delay.");
        }

        if (!Enum.IsDefined(config.Rig.Pipeline.CadenceMode))
        {
            throw new InvalidOperationException("Capture cadence mode is invalid.");
        }

        ValidateControlPolicy(config.Rig);
    }

    private static void ValidateCurrentFileContract(CameraModuleConfig config)
    {
        if (config.Pipeline.SchemaVersion != CapturePipelineSchemaVersions.ExplicitV2)
        {
            throw new InvalidOperationException(
                $"Unsupported current capture pipeline schema '{config.Pipeline.SchemaVersion}'.");
        }
        if (config.Pipeline.DependencyPolicy != CapturePipelineDependencyPolicy.RejectEnabledDependent)
        {
            throw new InvalidOperationException(
                $"Unsupported current capture pipeline dependency policy '{config.Pipeline.DependencyPolicy}'.");
        }
        if (config.Schedule is not { } schedule || schedule.WeeklyWindows.Count == 0)
        {
            throw new InvalidOperationException(
                "Current CameraAgent file configuration requires at least one recurring weekly schedule window.");
        }
        if (schedule.LegacyAlwaysOpen || schedule.LegacySetpointProfileId is not null)
        {
            throw new InvalidOperationException(
                "Current CameraAgent file configuration cannot use legacy schedule compatibility fields or admission/source values.");
        }
    }

    private static void ValidateScheduleCompatibility(CameraModuleConfig config)
    {
        var schedule = config.Schedule!;
        var envelope = config.Rig.Pipeline.Envelope;
        if (envelope is not null && schedule.SetpointProfiles.Any(profile =>
                profile.Exposure < envelope.MinExposure || profile.Exposure > envelope.MaxExposure ||
                profile.Gain < envelope.MinGain || profile.Gain > envelope.MaxGain))
        {
            throw new InvalidOperationException("Capture schedule setpoints must be within the rig exposure envelope.");
        }
        var sensorResponse = config.Rig.Sensor.SimulationResponse;
        if (sensorResponse is not null && schedule.SetpointProfiles.Any(profile =>
                profile.Gain < sensorResponse.MinimumGainControl ||
                profile.Gain > sensorResponse.MaximumGainControl))
        {
            throw new InvalidOperationException("Capture schedule gains must be within the sensor response range.");
        }

        try
        {
            var observer = config.ResolveObservatory();
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(observer.TimeZoneId);
            var solarEvents = new AstronomyEngineSolarEventCalculator();
            _ = CaptureScheduleIntervalExpander.Expand(
                schedule,
                new DateOnly(2024, 1, 1),
                366,
                timeZone,
                observer,
                solarEvents);
            foreach (var date in (schedule.DateExceptions ?? []).Select(static rule => rule.Date).Distinct())
            {
                _ = CaptureScheduleIntervalExpander.Expand(
                    schedule,
                    date,
                    1,
                    timeZone,
                    observer,
                    solarEvents);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidOperationException("Capture schedule expansion is invalid.", exception);
        }
    }

    private static void ValidateControlPolicy(CameraRigConfig rig)
    {
        var policy = rig.ControlPolicy;
        if (policy is null || policy.ExposureControl == AutomaticControlOwnership.Unspecified ||
            policy.GainControl == AutomaticControlOwnership.Unspecified)
        {
            throw new InvalidOperationException("Camera control ownership must be configured explicitly.");
        }

        if (!Enum.IsDefined(policy.ExposureControl) || !Enum.IsDefined(policy.GainControl) ||
            policy.ExposureControl == AutomaticControlOwnership.CameraNative &&
            policy.GainControl == AutomaticControlOwnership.HostMetered ||
            policy.ExposureControl == AutomaticControlOwnership.HostMetered &&
            policy.GainControl == AutomaticControlOwnership.CameraNative)
        {
            throw new InvalidOperationException(
                "Camera-native and host-metered ownership cannot be mixed in one control policy.");
        }

        var hostMetered = policy.ExposureControl == AutomaticControlOwnership.HostMetered ||
            policy.GainControl == AutomaticControlOwnership.HostMetered;
        if (!hostMetered)
        {
            return;
        }

        var meter = policy.Metering ?? new CaptureMeteringPolicy();
        var solar = policy.SolarRegimes ?? new CaptureSolarRegimePolicy();
        var envelope = rig.Pipeline.Envelope;
        var readout = rig.Readout is null ? null : SensorReadoutResolver.Resolve(rig.Sensor, rig.Readout).Layout;
        var effectiveFormat = readout?.PixelFormat ?? rig.Sensor.PixelFormat;
        var effectiveWidth = readout?.Width ?? rig.Sensor.WidthPixels;
        var effectiveHeight = readout?.Height ?? rig.Sensor.HeightPixels;
        if (envelope is null ||
            effectiveFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16) ||
            meter.XStride <= 0 || meter.YStride <= 0 ||
            !double.IsFinite(meter.SaturationFraction) ||
            meter.SaturationFraction is <= 0 or > 1 ||
            meter.CfaSelection == CaptureMeteringCfaSelection.None ||
            (meter.CfaSelection & ~CaptureMeteringCfaSelection.All) != 0 ||
            !double.IsFinite(solar.DayAltitudeThresholdDegrees) ||
            !double.IsFinite(solar.NightAltitudeThresholdDegrees) ||
            solar.DayAltitudeThresholdDegrees is < -90 or > 90 ||
            solar.NightAltitudeThresholdDegrees is < -90 or > 90 ||
            solar.DayAltitudeThresholdDegrees <= solar.NightAltitudeThresholdDegrees)
        {
            throw new InvalidOperationException(
                "Host-metered control requires a valid envelope, sparse meter, and solar regime policy.");
        }
        var hasInvalidBayerStride = effectiveFormat == CameraPixelFormat.BayerRggb16 &&
            ((meter.XStride & 1) != 0 || (meter.YStride & 1) != 0);
        var hasInvalidExcludedRegion = meter.ExcludedRegions?.Any(
            region => region is null || !FitsFrame(region, effectiveWidth, effectiveHeight)) == true;
        var hasInvalidImageCircle = meter.UseImageCircle &&
            (rig.Optics.ImageCircleRadiusPixels is not { } radius || !double.IsFinite(radius) || radius <= 0);
        if (hasInvalidBayerStride || !Enum.IsDefined(rig.Readout?.ByteOrder ?? rig.Sensor.ByteOrder) ||
            !FitsFrame(meter.Region, effectiveWidth, effectiveHeight) || hasInvalidExcludedRegion || hasInvalidImageCircle)
        {
            throw new InvalidOperationException(
                "Host metering regions, byte order, Bayer strides, and image-circle policy must match the sensor.");
        }
        if (envelope.MinExposure <= TimeSpan.Zero || envelope.MaxExposure < envelope.MinExposure ||
            !double.IsFinite(envelope.MinGain) || envelope.MinGain < 0 ||
            !double.IsFinite(envelope.MaxGain) ||
            envelope.MaxGain < envelope.MinGain || envelope.TargetAduLevel is <= 0 or >= 1 ||
            !double.IsFinite(envelope.TargetAduLevel) ||
            !double.IsFinite(envelope.Hysteresis ?? 0.05) ||
            (envelope.Hysteresis ?? 0.05) is < 0 or >= 1 ||
            !double.IsFinite(envelope.AdjustmentFactor ?? 1.25) ||
            (envelope.AdjustmentFactor ?? 1.25) <= 1 ||
            !double.IsFinite(envelope.GainStep ?? 10) || (envelope.GainStep ?? 10) <= 0 ||
            !Enum.IsDefined(envelope.Preference) ||
            !ValidDefaults(envelope.DayDefaults, envelope) ||
            !ValidDefaults(envelope.NightDefaults, envelope) ||
            envelope.TwilightDefaults is { } twilight && !ValidDefaults(twilight, envelope))
        {
            throw new InvalidOperationException("Host-metered exposure envelope is invalid.");
        }
    }

    private static bool FitsFrame(SensorCrop? region, int width, int height)
        => region is null || region is { X: >= 0, Y: >= 0, Width: > 0, Height: > 0 } value &&
            value.X <= width - value.Width && value.Y <= height - value.Height;

    private static bool ValidDefaults(ExposureDefaults? value, ExposureEnvelope envelope)
        => value is not null &&
            value.Exposure >= envelope.MinExposure && value.Exposure <= envelope.MaxExposure &&
            double.IsFinite(value.Gain) && value.Gain >= envelope.MinGain && value.Gain <= envelope.MaxGain;
}

internal sealed record CameraModuleDocument(
    string AgentId,
    CameraModuleDescriptor Module,
    CameraRigConfig Rig,
    [property: JsonRequired] CapturePipelineConfig Pipeline,
    [property: JsonRequired] CaptureScheduleDefinition Schedule);
