using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Configuration;

[TestClass]
[TestCategory("Unit")]
public sealed class CaptureControlConfigurationTests
{
    [TestMethod]
    public void LegacyDefaults_AreOmittedFromCanonicalRigJson()
    {
        var pipeline = new PipelineExposureProfile(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(4),
            1,
            100,
            new ExposureEnvelope(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(5),
                1,
                200,
                new ExposureDefaults(TimeSpan.FromSeconds(1), 1),
                new ExposureDefaults(TimeSpan.FromSeconds(4), 100),
                0.65));
        var policy = new CameraControlPolicy
        {
            AutoExposure = CameraFeatureDirective.Disabled,
            AutoGain = CameraFeatureDirective.Disabled
        };

        var json = JsonSerializer.Serialize(new { pipeline, policy });

        foreach (var property in new[]
                 {
                     "cadenceMode", "twilightDefaults", "hysteresis", "adjustmentFactor", "gainStep",
                     "exposureControl", "gainControl", "metering", "solarRegimes"
                 })
        {
            Assert.IsFalse(json.Contains(property, StringComparison.Ordinal), property);
        }
    }

    [TestMethod]
    public async Task LoadAsync_OmittedCadenceAndOwnership_UsesFixedCadenceAndDisabledControls()
    {
        var config = await LoadAsync(root =>
        {
            Remove(root, "rig.controlPolicy.exposureControl");
            Remove(root, "rig.controlPolicy.gainControl");
        }).ConfigureAwait(false);

        Assert.AreEqual(CaptureCadenceMode.MinimumStartInterval, config.Rig.Pipeline.CadenceMode);
        Assert.IsNotNull(config.Rig.ControlPolicy);
        Assert.AreEqual(AutomaticControlOwnership.Unspecified, config.Rig.ControlPolicy.ExposureControl);
        Assert.AreEqual(AutomaticControlOwnership.Unspecified, config.Rig.ControlPolicy.GainControl);
        Assert.AreEqual(AutomaticControlOwnership.Disabled, CameraModuleRunner.ResolveOwnership(
            config.Rig.ControlPolicy.ExposureControl, config.Rig.ControlPolicy.AutoExposure));
        Assert.AreEqual(AutomaticControlOwnership.Disabled, CameraModuleRunner.ResolveOwnership(
            config.Rig.ControlPolicy.GainControl, config.Rig.ControlPolicy.AutoGain));
    }

    [TestMethod]
    public async Task LoadAsync_ContinuousHostMeteredPolicy_LoadsCompletePolicy()
    {
        var config = await LoadAsync(root => Set(root, "rig.pipeline.cadenceMode", "\"Continuous\""))
            .ConfigureAwait(false);

        var policy = config.Rig.ControlPolicy;
        var envelope = config.Rig.Pipeline.Envelope;
        Assert.IsNotNull(policy);
        Assert.IsNotNull(envelope);
        var metering = policy.Metering;
        var solarRegimes = policy.SolarRegimes;
        Assert.IsNotNull(metering);
        Assert.IsNotNull(solarRegimes);
        Assert.AreEqual(CaptureCadenceMode.Continuous, config.Rig.Pipeline.CadenceMode);
        Assert.AreEqual(AutomaticControlOwnership.HostMetered, policy.ExposureControl);
        Assert.AreEqual(AutomaticControlOwnership.HostMetered, policy.GainControl);
        Assert.AreEqual(TimeSpan.FromSeconds(2), envelope.TwilightDefaults?.Exposure);
        Assert.AreEqual(25d, envelope.TwilightDefaults?.Gain);
        Assert.AreEqual(ExposureGainPreference.GainFirst, envelope.Preference);
        Assert.AreEqual(0.08d, envelope.Hysteresis);
        Assert.AreEqual(1.5d, envelope.AdjustmentFactor);
        Assert.AreEqual(12.5d, envelope.GainStep);
        Assert.AreEqual(8, metering.XStride);
        Assert.AreEqual(12, metering.YStride);
        Assert.AreEqual(new SensorCrop(2, 3, 40, 30), metering.Region);
        var excludedRegions = metering.ExcludedRegions?.ToArray();
        Assert.IsNotNull(excludedRegions);
        CollectionAssert.AreEqual(
            new[] { new SensorCrop(0, 0, 4, 5), new SensorCrop(50, 40, 14, 8) },
            excludedRegions);
        Assert.IsFalse(metering.UseImageCircle);
        Assert.AreEqual(0.97d, metering.SaturationFraction);
        Assert.AreEqual(
            CaptureMeteringCfaSelection.Red | CaptureMeteringCfaSelection.GreenOnBlueRow,
            metering.CfaSelection);
        Assert.AreEqual(-1d, solarRegimes.DayAltitudeThresholdDegrees);
        Assert.AreEqual(-15d, solarRegimes.NightAltitudeThresholdDegrees);
    }

    [TestMethod]
    [DataRow(90d, -90d)]
    [DataRow(-89d, -90d)]
    [DataRow(90d, 89d)]
    public async Task LoadAsync_SolarAltitudeThresholdBoundaries_AcceptsConfiguration(
        double dayThreshold,
        double nightThreshold)
    {
        var config = await LoadAsync(root =>
        {
            Set(root, "rig.controlPolicy.solarRegimes.dayAltitudeThresholdDegrees", dayThreshold.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            Set(root, "rig.controlPolicy.solarRegimes.nightAltitudeThresholdDegrees", nightThreshold.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }).ConfigureAwait(false);

        Assert.AreEqual(dayThreshold, config.Rig.ControlPolicy!.SolarRegimes!.DayAltitudeThresholdDegrees);
        Assert.AreEqual(nightThreshold, config.Rig.ControlPolicy.SolarRegimes.NightAltitudeThresholdDegrees);
    }

    [TestMethod]
    [DataRow("rig.controlPolicy.metering.xStride", "0")]
    [DataRow("rig.controlPolicy.metering.yStride", "-1")]
    [DataRow("rig.controlPolicy.metering.xStride", "7")]
    [DataRow("rig.controlPolicy.metering.yStride", "11")]
    [DataRow("rig.controlPolicy.metering.region", "{\"x\":25,\"y\":3,\"width\":40,\"height\":30}")]
    [DataRow("rig.controlPolicy.metering.excludedRegions", "[{\"x\":50,\"y\":40,\"width\":15,\"height\":8}]")]
    [DataRow("rig.controlPolicy.metering.saturationFraction", "0")]
    [DataRow("rig.controlPolicy.metering.saturationFraction", "1.01")]
    [DataRow("rig.controlPolicy.metering.saturationFraction", "\"NaN\"")]
    [DataRow("rig.controlPolicy.metering.cfaSelection", "\"None\"")]
    [DataRow("rig.controlPolicy.solarRegimes.dayAltitudeThresholdDegrees", "-15")]
    [DataRow("rig.controlPolicy.solarRegimes.dayAltitudeThresholdDegrees", "90.01")]
    [DataRow("rig.controlPolicy.solarRegimes.dayAltitudeThresholdDegrees", "-90.01")]
    [DataRow("rig.controlPolicy.solarRegimes.dayAltitudeThresholdDegrees", "\"NaN\"")]
    [DataRow("rig.controlPolicy.solarRegimes.nightAltitudeThresholdDegrees", "90.01")]
    [DataRow("rig.controlPolicy.solarRegimes.nightAltitudeThresholdDegrees", "-90.01")]
    [DataRow("rig.controlPolicy.solarRegimes.nightAltitudeThresholdDegrees", "\"Infinity\"")]
    [DataRow("rig.pipeline.envelope.minExposure", "\"00:00:00\"")]
    [DataRow("rig.pipeline.envelope.minExposure", "\"00:00:20\"")]
    [DataRow("rig.pipeline.envelope.minGain", "\"NaN\"")]
    [DataRow("rig.pipeline.envelope.minGain", "-1")]
    [DataRow("rig.pipeline.envelope.maxGain", "-1")]
    [DataRow("rig.pipeline.envelope.maxGain", "\"Infinity\"")]
    [DataRow("rig.pipeline.envelope.targetAduLevel", "1")]
    [DataRow("rig.pipeline.envelope.targetAduLevel", "\"NaN\"")]
    [DataRow("rig.pipeline.envelope.hysteresis", "1")]
    [DataRow("rig.pipeline.envelope.hysteresis", "\"NaN\"")]
    [DataRow("rig.pipeline.envelope.adjustmentFactor", "1")]
    [DataRow("rig.pipeline.envelope.adjustmentFactor", "\"Infinity\"")]
    [DataRow("rig.pipeline.envelope.gainStep", "0")]
    [DataRow("rig.pipeline.envelope.gainStep", "\"NaN\"")]
    [DataRow("rig.pipeline.envelope.dayDefaults.exposure", "\"00:00:00.001\"")]
    [DataRow("rig.pipeline.envelope.dayDefaults", "null")]
    [DataRow("rig.pipeline.envelope.dayDefaults.gain", "-1")]
    [DataRow("rig.pipeline.envelope.nightDefaults", "null")]
    [DataRow("rig.pipeline.envelope.nightDefaults.gain", "201")]
    [DataRow("rig.pipeline.envelope.twilightDefaults.exposure", "\"00:00:11\"")]
    [DataRow("rig.controlPolicy.metering.excludedRegions", "[null]")]
    [DataRow("rig.sensor.pixelFormat", "\"Mono8\"")]
    [DataRow("rig.sensor.pixelFormat", "\"Rgb24\"")]
    public async Task LoadAsync_InvalidCaptureControlValue_RejectsConfiguration(string path, string jsonValue)
    {
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => LoadAsync(root => Set(root, path, jsonValue))).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("rig.pipeline.cadenceMode", "0")]
    [DataRow("rig.pipeline.cadenceMode", "99")]
    [DataRow("rig.controlPolicy.exposureControl", "3")]
    [DataRow("rig.controlPolicy.gainControl", "0")]
    [DataRow("rig.controlPolicy.metering.cfaSelection", "1")]
    [DataRow("rig.controlPolicy.metering.cfaSelection", "16")]
    [DataRow("rig.pipeline.envelope.preference", "1")]
    [DataRow("rig.pipeline.envelope.preference", "99")]
    [DataRow("rig.sensor.byteOrder", "0")]
    [DataRow("rig.sensor.byteOrder", "99")]
    public async Task LoadAsync_NumericCaptureControlEnum_RejectsConfiguration(string path, string jsonValue)
    {
        await Assert.ThrowsExactlyAsync<JsonException>(
            () => LoadAsync(root => Set(root, path, jsonValue))).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task LoadAsync_LegacyNumericEnum_RemainsSupported()
    {
        var config = await LoadAsync(root => Set(root, "rig.sensor.pixelFormat", "3")).ConfigureAwait(false);

        Assert.AreEqual(CameraPixelFormat.BayerRggb16, config.Rig.Sensor.PixelFormat);
    }

    [TestMethod]
    public async Task LoadAsync_ImageCircleEnabledWithoutRadius_RejectsConfiguration()
    {
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => LoadAsync(root =>
            Set(root, "rig.controlPolicy.metering.useImageCircle", "true"))).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("-1")]
    [DataRow("\"NaN\"")]
    [DataRow("\"Infinity\"")]
    public async Task LoadAsync_ImageCircleEnabledWithInvalidRadius_RejectsConfiguration(string radius)
    {
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => LoadAsync(root =>
        {
            Set(root, "rig.controlPolicy.metering.useImageCircle", "true");
            Set(root, "rig.optics.imageCircleRadiusPixels", radius);
        })).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task LoadAsync_HostMeteredWithoutEnvelope_RejectsConfiguration()
    {
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => LoadAsync(root => Remove(root, "rig.pipeline.envelope"))).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("CameraNative", "HostMetered")]
    [DataRow("HostMetered", "CameraNative")]
    public async Task LoadAsync_MixedCameraNativeAndHostMeteredOwnership_RejectsConfiguration(
        string exposureControl,
        string gainControl)
    {
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => LoadAsync(root =>
        {
            Set(root, "rig.controlPolicy.exposureControl", JsonSerializer.Serialize(exposureControl));
            Set(root, "rig.controlPolicy.gainControl", JsonSerializer.Serialize(gainControl));
        })).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task LoadAsync_InvalidConfigurationDoesNotMutateDeploymentLocationHistory()
    {
        var locationStore = new RecordingDeploymentLocationStore();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => LoadAsync(
            root => Remove(root, "module.type"),
            locationStore)).ConfigureAwait(false);

        Assert.AreEqual(0, locationStore.InitializeCalls);
    }

    [TestMethod]
    public async Task LoadAsync_ValidScheduleIsRetainedAndInvalidScheduleHasNoLocationSideEffect()
    {
        var config = await LoadAsync(root => root["schedule"] = JsonNode.Parse(ValidScheduleJson))
            .ConfigureAwait(false);

        Assert.IsNotNull(config.Schedule);
        Assert.AreEqual("night", config.Schedule.SetpointProfiles.Single().Id);

        var locationStore = new RecordingDeploymentLocationStore();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => LoadAsync(
            root =>
            {
                root["schedule"] = JsonNode.Parse(ValidScheduleJson);
                root["schedule"]!["setpointProfiles"]!.AsArray()[0]!["exposure"] = "00:00:11";
            },
            locationStore)).ConfigureAwait(false);
        Assert.AreEqual(0, locationStore.InitializeCalls);
    }

    [TestMethod]
    public async Task LoadAsync_ScheduleGainOutsideSensorResponseIsRejected()
    {
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => LoadAsync(root =>
        {
            root["schedule"] = JsonNode.Parse(ValidScheduleJson);
            root["rig"]!["sensor"]!["simulationResponse"] = JsonNode.Parse("""
                {
                  "modelVersion": "test-v1",
                  "adcBitDepth": 16,
                  "minimumGainControl": 0,
                  "maximumGainControl": 10,
                  "electronsPerAduAtZeroGain": 1,
                  "gainControlDivisor": 10,
                  "maximumFullWellElectrons": 10000,
                  "readNoisePoints": [],
                  "blackLevelAdu": 0,
                  "gainUnits": "test",
                  "compatibilityLabel": "test"
                }
                """);
        })).ConfigureAwait(false);
    }

    private static async Task<CameraModuleConfig> LoadAsync(
        Action<JsonObject>? mutate = null,
        IDeploymentLocationStore? deploymentLocationStore = null)
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "hvo-capture-control-configuration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var root = JsonNode.Parse(ValidHostMeteredJson)!.AsObject();
            mutate?.Invoke(root);
            var path = Path.Combine(directory, "cameraagent.json");
            await File.WriteAllTextAsync(path, root.ToJsonString()).ConfigureAwait(false);
            var loader = new FileCameraAgentConfigurationLoader(
                Options.Create(new CameraAgentHostOptions
                {
                    ConfigFilePath = path,
                    AgentId = "configuration-test",
                    CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled },
                    Observatory = new ObservatoryLocation(35, -114, 1_500, "UTC")
                }),
                NullLogger<FileCameraAgentConfigurationLoader>.Instance,
                deploymentLocationStore);

            return await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Set(JsonObject root, string path, string jsonValue)
    {
        var (parent, property) = Resolve(root, path);
        parent[property] = JsonNode.Parse(jsonValue);
    }

    private static void Remove(JsonObject root, string path)
    {
        var (parent, property) = Resolve(root, path);
        Assert.IsTrue(parent.Remove(property), $"Configuration property '{path}' was not present.");
    }

    private static (JsonObject Parent, string Property) Resolve(JsonObject root, string path)
    {
        var segments = path.Split('.');
        var parent = root;
        foreach (var segment in segments[..^1])
        {
            parent = parent[segment]?.AsObject()
                ?? throw new InvalidOperationException($"Configuration object '{segment}' was not present.");
        }
        return (parent, segments[^1]);
    }

    private sealed class RecordingDeploymentLocationStore : IDeploymentLocationStore
    {
        internal int InitializeCalls { get; private set; }

        public DeploymentLocationSnapshot? Active => null;

        public ValueTask<DeploymentLocationSnapshot> InitializeAsync(
            DeploymentLocationSeed seed,
            CancellationToken cancellationToken)
        {
            InitializeCalls++;
            throw new InvalidOperationException("A rejected configuration must not initialize location state.");
        }

        public DeploymentLocationSnapshot Resolve(
            CaptureLocationProvenance provenance,
            DateTimeOffset? effectiveUtc = null)
            => throw new NotSupportedException();
    }

    private const string ValidHostMeteredJson = """
        {
          "agentId": "configuration-test",
          "module": { "type": "Test" },
          "rig": {
            "sensor": {
              "name": "Test",
              "widthPixels": 64,
              "heightPixels": 48,
              "pixelSizeMicrons": 3.75,
              "colorMode": "Color",
              "pixelFormat": "BayerRggb16"
            },
            "optics": {
              "projectionModel": "EquidistantFisheye",
              "focalLengthMillimeters": 1,
              "fieldOfViewDegrees": 180,
              "rollDegrees": 0
            },
            "orientation": {
              "boresightAltitudeDegrees": 90,
              "boresightAzimuthDegrees": 0,
              "rollAdjustmentDegrees": 0
            },
            "pipeline": {
              "captureInterval": "00:00:10",
              "dayExposure": "00:00:00.1000000",
              "nightExposure": "00:00:05",
              "dayGain": 1,
              "nightGain": 100,
              "envelope": {
                "minExposure": "00:00:00.0100000",
                "maxExposure": "00:00:10",
                "minGain": 0,
                "maxGain": 200,
                "dayDefaults": { "exposure": "00:00:00.1000000", "gain": 1 },
                "twilightDefaults": { "exposure": "00:00:02", "gain": 25 },
                "nightDefaults": { "exposure": "00:00:05", "gain": 100 },
                "targetAduLevel": 0.6,
                "preference": "GainFirst",
                "hysteresis": 0.08,
                "adjustmentFactor": 1.5,
                "gainStep": 12.5
              }
            },
            "controlPolicy": {
              "exposureControl": "HostMetered",
              "gainControl": "HostMetered",
              "metering": {
                "xStride": 8,
                "yStride": 12,
                "region": { "x": 2, "y": 3, "width": 40, "height": 30 },
                "excludedRegions": [
                  { "x": 0, "y": 0, "width": 4, "height": 5 },
                  { "x": 50, "y": 40, "width": 14, "height": 8 }
                ],
                "useImageCircle": false,
                "saturationFraction": 0.97,
                "cfaSelection": "Red, GreenOnBlueRow"
              },
              "solarRegimes": {
                "dayAltitudeThresholdDegrees": -1,
                "nightAltitudeThresholdDegrees": -15
              }
            }
          }
        }
        """;

    private const string ValidScheduleJson = """
        {
          "schemaVersion": "capture-schedule-v1",
          "setpointProfiles": [
            {
              "id": "night",
              "exposure": "00:00:05",
              "gain": 100,
              "captureInterval": "00:00:10"
            }
          ],
          "weeklyWindows": [
            {
              "id": "monday-night",
              "day": "Monday",
              "start": { "kind": "FixedLocalTime", "localTime": "18:00:00" },
              "end": { "kind": "FixedLocalTime", "localTime": "06:00:00", "dayOffset": 1 },
              "setpointProfileId": "night"
            }
          ]
        }
        """;
}
