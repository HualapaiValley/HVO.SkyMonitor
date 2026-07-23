using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Configuration;

public sealed class FileCameraAgentConfigurationLoader(
    IOptions<CameraAgentHostOptions> options,
    ILogger<FileCameraAgentConfigurationLoader> logger,
    IDeploymentLocationStore? deploymentLocationStore = null) : ICameraAgentConfigurationLoader
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter<CaptureCadenceMode>(allowIntegerValues: false),
            new JsonStringEnumConverter<AutomaticControlOwnership>(allowIntegerValues: false),
            new JsonStringEnumConverter<CaptureMeteringCfaSelection>(allowIntegerValues: false),
            new JsonStringEnumConverter<ExposureGainPreference>(allowIntegerValues: false),
            new JsonStringEnumConverter<SampleByteOrder>(allowIntegerValues: false),
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

        var agentId = string.IsNullOrWhiteSpace(_options.AgentId) ? document.AgentId : _options.AgentId;
        var locationSeed = new DeploymentLocationSeed(
            _options.DeploymentLocation.LocationId,
            _options.DeploymentLocation.Source,
            _options.DeploymentLocation.HorizontalAccuracyMeters,
            _options.DeploymentLocation.EffectiveFromUtc,
            _options.DeploymentLocation.EffectiveUntilUtc,
            _options.Observatory);
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
            ProcessingSteps: document.ProcessingSteps,
            Pipeline: document.Pipeline,
            AgentId: agentId)
        {
            DeploymentLocation = location
        };

        ValidateConfig(config);
        if (_deploymentLocationStore is not null)
        {
            location = await _deploymentLocationStore.InitializeAsync(locationSeed, cancellationToken).ConfigureAwait(false);
            config = config with { DeploymentLocation = location };
        }
        _logger.ConfigurationLoaded(path);
        return config;
    }

    private static void ValidateConfig(CameraModuleConfig config)
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

        if (config.Rig.Sensor.WidthPixels <= 0 || config.Rig.Sensor.HeightPixels <= 0)
        {
            throw new InvalidOperationException("Sensor resolution must be greater than zero.");
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

    private static void ValidateControlPolicy(CameraRigConfig rig)
    {
        var policy = rig.ControlPolicy;
        if (policy is null)
        {
            return;
        }

        var exposure = CameraModuleRunner.ResolveOwnership(policy.ExposureControl, policy.AutoExposure);
        var gain = CameraModuleRunner.ResolveOwnership(policy.GainControl, policy.AutoGain);
        if (!Enum.IsDefined(policy.ExposureControl) || !Enum.IsDefined(policy.GainControl) ||
            exposure == AutomaticControlOwnership.CameraNative && gain == AutomaticControlOwnership.HostMetered ||
            exposure == AutomaticControlOwnership.HostMetered && gain == AutomaticControlOwnership.CameraNative)
        {
            throw new InvalidOperationException(
                "Camera-native and host-metered ownership cannot be mixed in one control policy.");
        }

        var hostMetered = exposure == AutomaticControlOwnership.HostMetered ||
            gain == AutomaticControlOwnership.HostMetered;
        if (!hostMetered)
        {
            return;
        }

        var meter = policy.Metering ?? new CaptureMeteringPolicy();
        var solar = policy.SolarRegimes ?? new CaptureSolarRegimePolicy();
        var envelope = rig.Pipeline.Envelope;
        if (envelope is null ||
            rig.Sensor.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16) ||
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
        var hasInvalidBayerStride = rig.Sensor.PixelFormat == CameraPixelFormat.BayerRggb16 &&
            ((meter.XStride & 1) != 0 || (meter.YStride & 1) != 0);
        var hasInvalidExcludedRegion = meter.ExcludedRegions?.Any(
            region => region is null || !FitsSensor(region, rig.Sensor)) == true;
        var hasInvalidImageCircle = meter.UseImageCircle &&
            (rig.Optics.ImageCircleRadiusPixels is not { } radius || !double.IsFinite(radius) || radius <= 0);
        if (hasInvalidBayerStride || !Enum.IsDefined(rig.Sensor.ByteOrder) ||
            !FitsSensor(meter.Region, rig.Sensor) || hasInvalidExcludedRegion || hasInvalidImageCircle)
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

    private static bool FitsSensor(SensorCrop? region, SensorProfile sensor)
        => region is null || region is { X: >= 0, Y: >= 0, Width: > 0, Height: > 0 } value &&
            value.X <= sensor.WidthPixels - value.Width &&
            value.Y <= sensor.HeightPixels - value.Height;

    private static bool ValidDefaults(ExposureDefaults? value, ExposureEnvelope envelope)
        => value is not null &&
            value.Exposure >= envelope.MinExposure && value.Exposure <= envelope.MaxExposure &&
            double.IsFinite(value.Gain) && value.Gain >= envelope.MinGain && value.Gain <= envelope.MaxGain;
}

internal sealed record CameraModuleDocument(
    string AgentId,
    CameraModuleDescriptor Module,
    CameraRigConfig Rig,
    IReadOnlyList<CaptureProcessingStepConfig>? ProcessingSteps = null,
    CapturePipelineConfig? Pipeline = null);
