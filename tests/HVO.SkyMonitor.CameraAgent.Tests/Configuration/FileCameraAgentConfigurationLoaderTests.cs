using System;
using System.Text.Json;
using System.Threading.Tasks;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Configuration;

[TestClass]
public sealed class FileCameraAgentConfigurationLoaderTests
{
    [TestMethod]
    public async Task LoadAsync_WithValidFile_ReturnsConfig()
    {
        var (loader, path, observatory) = await CreateLoaderAsync(SampleConfigJson).ConfigureAwait(false);

        try
        {
            var config = await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(DefaultModuleType, config.Module.Type);
            Assert.AreEqual(640, config.Rig.Sensor.WidthPixels);
            Assert.AreEqual(observatory, config.Observatory);
        }
        finally
        {
            DeleteConfigFile(path);
        }
    }

    [TestMethod]
    public async Task LoadAsync_WithInvalidSensorDimensions_Throws()
    {
        var (loader, path, _) = await CreateLoaderAsync(CreateConfigJson(sensorWidth: 0)).ConfigureAwait(false);

        try
        {
            await AssertThrowsAsync<InvalidOperationException>(() => loader.LoadAsync(CancellationToken.None)).ConfigureAwait(false);
        }
        finally
        {
            DeleteConfigFile(path);
        }
    }

    [TestMethod]
    public async Task LoadAsync_WithNonPositiveCaptureInterval_Throws()
    {
        var (loader, path, _) = await CreateLoaderAsync(CreateConfigJson(captureInterval: TimeSpan.Zero)).ConfigureAwait(false);

        try
        {
            await AssertThrowsAsync<InvalidOperationException>(() => loader.LoadAsync(CancellationToken.None)).ConfigureAwait(false);
        }
        finally
        {
            DeleteConfigFile(path);
        }
    }

    [TestMethod]
    public async Task LoadAsync_WithMissingModuleType_Throws()
    {
        var (loader, path, _) = await CreateLoaderAsync(CreateConfigJson(moduleType: string.Empty)).ConfigureAwait(false);

        try
        {
            await AssertThrowsAsync<InvalidOperationException>(() => loader.LoadAsync(CancellationToken.None)).ConfigureAwait(false);
        }
        finally
        {
            DeleteConfigFile(path);
        }
    }

    private const string DefaultModuleType = "HVO.SkyMonitor.CameraAgent.Common.Modules.RandomImage.RandomImageCameraModule, HVO.SkyMonitor.CameraAgent.Common";

    private static readonly string SampleConfigJson = CreateConfigJson();
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private static async Task<(FileCameraAgentConfigurationLoader Loader, string Path, ObservatoryLocation Observatory)> CreateLoaderAsync(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"camera-agent-config-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
        var observatory = new ObservatoryLocation(19.3, -155.3, 420, "Pacific/Honolulu");
        var options = Options.Create(new CameraAgentHostOptions
        {
            ConfigFilePath = path,
            Observatory = observatory
        });
        var loader = new FileCameraAgentConfigurationLoader(options, NullLogger<FileCameraAgentConfigurationLoader>.Instance);
        return (loader, path, observatory);
    }

    private static void DeleteConfigFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string CreateConfigJson(
        TimeSpan? captureInterval = null,
        int sensorWidth = 640,
        int sensorHeight = 480,
        string? moduleType = DefaultModuleType)
    {
        moduleType ??= DefaultModuleType;
        var interval = captureInterval ?? TimeSpan.FromSeconds(15);

        var document = new
        {
            module = new
            {
                type = moduleType,
                options = new
                {
                    pattern = "Noise"
                }
            },
            rig = new
            {
                sensor = new
                {
                    name = "Sensor",
                    widthPixels = sensorWidth,
                    heightPixels = sensorHeight,
                    pixelSizeMicrons = 3.76,
                    colorMode = "Mono",
                    pixelFormat = "Mono8"
                },
                optics = new
                {
                    projectionModel = "Equidistant",
                    focalLengthMillimeters = 2.8,
                    fieldOfViewDegrees = 180.0,
                    rollDegrees = 0.0
                },
                orientation = new
                {
                    boresightAltitudeDegrees = 90.0,
                    boresightAzimuthDegrees = 0.0,
                    rollAdjustmentDegrees = 0.0
                },
                pipeline = new
                {
                    captureInterval = interval.ToString("c"),
                    dayExposure = "00:00:00.1000000",
                    nightExposure = "00:00:01",
                    dayGain = 1.0,
                    nightGain = 10.0,
                    envelope = new
                    {
                        minExposure = "00:00:00.0500000",
                        maxExposure = "00:00:10",
                        minGain = 1.0,
                        maxGain = 400.0,
                        dayDefaults = new { exposure = "00:00:00.1000000", gain = 2.0 },
                        nightDefaults = new { exposure = "00:00:05", gain = 100.0 },
                        targetAduLevel = 0.65
                    }
                }
            },
            processingSteps = new object[]
            {
                new
                {
                    id = "LocalStorage",
                    type = "HVO.SkyMonitor.CameraAgent.Common.Capture.Processing.NoOpFileStorageProcessingStep, HVO.SkyMonitor.CameraAgent.Common",
                    order = 100,
                    options = new
                    {
                        storageRoot = "/tmp/agent",
                        retentionDays = 7,
                        updateLatestFrame = true
                    }
                }
            }
        };

        return JsonSerializer.Serialize(document, SerializerOptions);
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
            Assert.Fail($"Expected exception of type {typeof(TException).Name}.");
        }
        catch (TException)
        {
            // Expected path
        }
    }
}
