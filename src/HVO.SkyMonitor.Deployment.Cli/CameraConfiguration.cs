using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Deployment;

internal sealed record GeneratedConfiguration(
    string Json,
    string Sha256,
    string RigProfileSha256,
    string ScheduleSha256,
    string RigProfileName,
    string RigProfileVersion,
    string ScheduleSchemaVersion,
    string ScheduleState);

internal static class CameraConfiguration
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private static readonly string[] ConstellationIds = ["ORI", "UMA", "UMI", "CAS", "CYG", "LYR"];
    private static readonly string[] SolarSystemBodies = ["Sun", "Moon", "Mercury", "Venus", "Mars", "Jupiter", "Saturn"];
    private static readonly string[] RawDependency = ["$raw"];
    private static readonly string[] ProjectedSceneDependency = ["ProjectedScene"];
    private static readonly string[] RollingDependency = ["RollingCombination"];
    private static readonly string[] CombinedPreviewDependencies = ["ProjectedScene", "CombinedPreview"];
    private static readonly string[] ManifestDependencies = ["CombinedPreview", "ScenePresentation", "EnvironmentPresentation"];
    private static readonly string[] MaterializerDependencies = ["CombinedPreview", "ScenePresentation", "EnvironmentPresentation", "OverlayManifest"];
    private static readonly string[] PresentationDependencies = ["$raw", "ProjectedScene", "Calibration", "RollingCombination", "CombinedPreview", "ScenePresentation", "EnvironmentPresentation", "OverlayManifest", "PresentationMaterializer"];
    private static readonly string[] TelemetryDependencies =
        ["ProjectedScene", "Calibration", "RollingCombination", "CombinedPreview", "ScenePresentation", "EnvironmentPresentation", "OverlayManifest", "PresentationMaterializer", "LocalStorage", "ArchiveStorage"];

    public static GeneratedConfiguration Generate(InstallRequest request, Guid applicationIdentity)
    {
        var schedule = CreateSchedule();
        var scheduleValidation = CaptureScheduleContract.Validate(schedule);
        if (!scheduleValidation.IsValid)
        {
            throw new InstallerException($"The installer schedule is invalid ({scheduleValidation.FieldPath}).");
        }

        var rig = CreateRig();
        var config = new CameraModuleConfig(
            new ObservatoryLocation(
                request.LatitudeDegrees,
                request.LongitudeDegrees,
                request.ElevationMeters,
                request.TimeZoneId),
            new CameraModuleDescriptor("VirtualSky", JsonSerializer.SerializeToElement(new
            {
                seed = 2025,
                maximumMagnitude = 6.5,
                maximumResults = 2000,
                magnitudeZeroElectronsPerSecond = 300.0,
                bortleClass = 3,
                asi174Sensor = new { enabled = true, blackLevelAdu = 64.0 },
                constellationIds = ConstellationIds,
                includeConstellationEndpointStars = true,
                solarSystemBodies = SolarSystemBodies
            })),
            rig,
            new CapturePipelineConfig(CreateProcessingSteps()),
            AgentId: applicationIdentity.ToString("D"))
        {
            Schedule = schedule
        };

        var element = CaptureContractJson.SerializeToElement(new
        {
            config.AgentId,
            config.Module,
            config.Rig,
            config.Pipeline,
            config.Schedule
        });
        var json = JsonSerializer.Serialize(element, IndentedJson);
        var sha256 = Convert.ToHexStringLower(Convert.FromHexString(
            CaptureContractJson.ComputeCanonicalJsonSha256(element)));
        var scheduleSha256 = Convert.ToHexStringLower(
            Convert.FromHexString(CaptureScheduleContract.ComputeSha256(schedule)));
        var rigSha256 = Convert.ToHexStringLower(Convert.FromHexString(CameraRigProfileIdentity.ComputeSha256(rig)));
        return new GeneratedConfiguration(
            json,
            sha256,
            rigSha256,
            scheduleSha256,
            rig.Sensor.Name,
            rig.ProfileVersion,
            schedule.SchemaVersion,
            "enabled");
    }

    private static CameraRigConfig CreateRig() => new(
        new SensorProfile(
            "VirtualAsi174Mm",
            1936,
            1216,
            5.86,
            SensorColorMode.Mono,
            CameraPixelFormat.Mono16,
            SensorResponseMode.Monochrome,
            3872,
            SampleByteOrder.LittleEndian,
            "virtual-asi174mm-electron-domain-v2"),
        new OpticsProfile(
            "EquidistantFisheye",
            0,
            180,
            0,
            LensKind.Fisheye,
            968,
            608,
            595.84,
            HorizontalFlip: false,
            CalibrationVersion: "virtual-fisheye-180-equidistant-v1"),
        new RigOrientation(90, 0, 0),
        new PipelineExposureProfile(
            TimeSpan.FromSeconds(25),
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromSeconds(20),
            0,
            150,
            new ExposureEnvelope(
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromSeconds(30),
                0,
                400,
                new ExposureDefaults(TimeSpan.FromMilliseconds(250), 0),
                new ExposureDefaults(TimeSpan.FromSeconds(20), 150),
                0.65),
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromSeconds(30)),
        new CameraControlPolicy
        {
            ExposureControl = AutomaticControlOwnership.Disabled,
            GainControl = AutomaticControlOwnership.Disabled
        },
        ProfileVersion: "installer-virtualsky-v3",
        Readout: new SensorReadoutProfile(
            new SensorCrop(0, 0, 1936, 1216),
            1, 1, FrameBinningAlgorithm.IdentityV1,
            CameraPixelFormat.Mono16, 12, 16, FrameSamplePacking.ByteAligned,
            FrameStoredCodeTransform.RightAlignedV1, FrameLevelCodeSpace.NativeSample,
            64, 4095, 3872));

    private static IReadOnlyList<CaptureProcessingStepConfig> CreateProcessingSteps() =>
    [
        Step("ProjectedScene", "ProjectedScene", 5, new
        {
            outputVariant = "projected-scene-v1",
            maximumMagnitude = 6.5,
            maximumResults = 300,
            constellationIds = ConstellationIds,
            includeConstellationEndpointStars = true,
            solarSystemBodies = SolarSystemBodies
        }, RawDependency, durable: true),
        Step("Calibration", "Calibration", -1000, new { strategy = "None", outputVariant = "none" }, RawDependency),
        Step("RollingCombination", "RollingCombination", 25, new
        {
            windowSize = 5,
            windowKind = "Trailing",
            outputVariant = "rolling-mean"
        }, RawDependency),
        Step("CombinedPreview", "CombinedPreview", 50, new
        {
            recipeVersion = "mono16-asinh-v2",
            outputVariant = "combined-preview",
            blackPercentile = 0.5,
            whitePercentile = 0.9995,
            asinhStrength = 10.0
        }, RollingDependency),
        Step("ScenePresentation", "ScenePresentationLayer", 70, new
        {
            annotationOutputVariant = "scene-annotation-layer-v1",
            cardinalOutputVariant = "scene-cardinal-layer-v1",
            imageCircleOutputVariant = "scene-image-circle-layer-v1",
            constellationOutputVariant = "scene-constellation-layer-v1",
            constellationIds = ConstellationIds
        }, ProjectedSceneDependency, durable: true),
        Step("EnvironmentPresentation", "EnvironmentPresentationLayer", 72, new
        {
            widthPixels = 1936,
            heightPixels = 1216,
            stackPreviewVariant = "combined-preview"
        }, CombinedPreviewDependencies, durable: true),
        Step("OverlayManifest", "OverlayManifest", 80, new { }, ManifestDependencies, durable: true),
        Step("PresentationMaterializer", "PresentationMaterializer", 81, new { outputVariant = "installer-annotated-preview" }, MaterializerDependencies, durable: true),
        Step(
            "LocalStorage",
            "Storage",
            100,
            new { storageRoot = "/app/data/raw", retentionDays = 7, updateLatestFrame = true, queueForUpload = false },
            PresentationDependencies),
        Step(
            "ArchiveStorage",
            "Storage",
            110,
            new { storageRoot = "/app/data/archive", retentionDays = 30, updateLatestFrame = false, queueForUpload = false },
            PresentationDependencies),
        Step(
            "Telemetry",
            "Telemetry",
            1000,
            new { },
            TelemetryDependencies)
    ];

    private static CaptureProcessingStepConfig Step(
        string id,
        string type,
        int order,
        object options,
        IReadOnlyList<string>? dependsOn = null,
        bool durable = false)
        => new(type, id, order, JsonSerializer.SerializeToElement(options), dependsOn,
            Publication: durable ? new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal) : null);

    private static CaptureScheduleDefinition CreateSchedule()
    {
        var start = new CaptureScheduleBoundary(
            CaptureScheduleBoundaryKind.Sunset,
            NoEventFallbackLocalTime: new TimeOnly(18, 0));
        var end = new CaptureScheduleBoundary(
            CaptureScheduleBoundaryKind.Sunrise,
            DayOffset: 1,
            NoEventFallbackLocalTime: new TimeOnly(6, 0));
        var windows = Enum.GetValues<DayOfWeek>()
            .Select(day => new CaptureWeeklyScheduleWindow(
                $"night-{DayName(day)}",
                day,
                start,
                end,
                "night"))
            .ToArray();
        return new CaptureScheduleDefinition(
            "capture-schedule-v1",
            [new CaptureScheduleSetpointProfile(
                "night",
                TimeSpan.FromSeconds(20),
                150,
                TimeSpan.FromSeconds(25))],
            windows);
    }

    private static string DayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Sunday => "sunday",
        DayOfWeek.Monday => "monday",
        DayOfWeek.Tuesday => "tuesday",
        DayOfWeek.Wednesday => "wednesday",
        DayOfWeek.Thursday => "thursday",
        DayOfWeek.Friday => "friday",
        DayOfWeek.Saturday => "saturday",
        _ => throw new ArgumentOutOfRangeException(nameof(day))
    };
}
