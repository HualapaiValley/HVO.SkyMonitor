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
    private static readonly string[] CloudDependency = ["CloudAssessment"];
    private static readonly string[] ManifestDependencies = ["CombinedPreview", "ScenePresentation", "EnvironmentPresentation", "CloudPresentation"];
    private static readonly string[] MaterializerDependencies = ["CombinedPreview", "ScenePresentation", "EnvironmentPresentation", "CloudPresentation", "OverlayManifest"];
    // Generation 1 (installer-defaults-1), before the cloud layer: what every release up to v0.9.1-dev.11 installed.
    private static readonly string[] ManifestDependenciesWithoutCloud = ["CombinedPreview", "ScenePresentation", "EnvironmentPresentation"];
    private static readonly string[] MaterializerDependenciesWithoutCloud = ["CombinedPreview", "ScenePresentation", "EnvironmentPresentation", "OverlayManifest"];
    private static readonly string[] PresentationDependencies = ["$raw", "ProjectedScene", "Calibration", "RollingCombination", "CombinedPreview", "ScenePresentation", "EnvironmentPresentation", "OverlayManifest", "PresentationMaterializer"];
    private static readonly string[] TelemetryDependencies =
        ["ProjectedScene", "Calibration", "RollingCombination", "CombinedPreview", "ScenePresentation", "EnvironmentPresentation", "OverlayManifest", "PresentationMaterializer", "LocalStorage", "ArchiveStorage"];

    /// <summary>
    /// The camera configuration this installer writes for <paramref name="request"/>: that of its current
    /// configuration-defaults generation (<see cref="CameraConfigurationDefaults.Current"/>).
    /// </summary>
    public static GeneratedConfiguration Generate(InstallRequest request, Guid applicationIdentity)
        => CameraConfigurationDefaults.Current.Generate(request, applicationIdentity);

    /// <summary>
    /// The camera configuration that configuration-defaults generation <paramref name="generation"/> writes for
    /// <paramref name="request"/>. A generation's output never changes once an installer has shipped it: completed
    /// installations are recognised by it (<see cref="CameraConfigurationDefaults"/>), and frozen fixtures hold it. A
    /// change to what the installer writes is a new generation, added to <see cref="CameraConfigurationDefaults.Shipped"/>.
    /// </summary>
    internal static GeneratedConfiguration Generate(InstallRequest request, Guid applicationIdentity, int generation)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(generation, LatestGeneration);
        var schedule = CreateSchedule();
        var scheduleValidation = CaptureScheduleContract.Validate(schedule);
        if (!scheduleValidation.IsValid)
        {
            throw new InstallerException($"The installer schedule is invalid ({scheduleValidation.FieldPath}).");
        }

        var rig = CreateRig();
        var moduleOptions = JsonSerializer.SerializeToNode(new
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
        })!.AsObject();
        if (request.IlluminationMode is { } illuminationMode)
        {
            moduleOptions["illuminationMode"] = illuminationMode;
        }
        var config = new CameraModuleConfig(
            new ObservatoryLocation(
                request.LatitudeDegrees,
                request.LongitudeDegrees,
                request.ElevationMeters,
                request.TimeZoneId),
            new CameraModuleDescriptor("VirtualSky", JsonSerializer.SerializeToElement(moduleOptions)),
            rig,
            new CapturePipelineConfig(CreateProcessingSteps(generation)),
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
            HorizontalFlip: true,
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
        ProfileVersion: "installer-virtualsky-v4",
        Readout: new SensorReadoutProfile(
            new SensorCrop(0, 0, 1936, 1216),
            1, 1, FrameBinningAlgorithm.IdentityV1,
            CameraPixelFormat.Mono16, 12, 16, FrameSamplePacking.ByteAligned,
            FrameStoredCodeTransform.RightAlignedV1, FrameLevelCodeSpace.NativeSample,
            64, 4095, 3872));

    /// <summary>The newest generation <see cref="Generate(InstallRequest, Guid, int)"/> writes.</summary>
    internal const int LatestGeneration = 2;

    /// <summary>
    /// The processing pipeline of <paramref name="generation"/>. Generation 2 (#1722) adds the cloud assessment and its
    /// presentation layer, which the overlay manifest and the presentation materializer then depend on.
    /// </summary>
    private static List<CaptureProcessingStepConfig> CreateProcessingSteps(int generation)
    {
        var cloud = generation >= 2;
        var steps = new List<CaptureProcessingStepConfig>
        {
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
                whitePercentile = 0.9997,
                asinhStrength = 8.0
            }, RollingDependency)
        };
        if (cloud)
        {
            steps.Add(Step("CloudAssessment", "CloudAssessment", 60, new
            {
                clearReferenceManifestPath = "references/cloud/clear-reference.manifest.json"
            }, RawDependency, durable: true, required: false));
        }
        steps.Add(Step("ScenePresentation", "ScenePresentationLayer", 70, new
        {
            annotationOutputVariant = "scene-annotation-layer-v1",
            cardinalOutputVariant = "scene-cardinal-layer-v1",
            imageCircleOutputVariant = "scene-image-circle-layer-v1",
            constellationOutputVariant = "scene-constellation-layer-v1",
            constellationIds = ConstellationIds
        }, ProjectedSceneDependency, durable: true));
        if (cloud)
        {
            steps.Add(Step("CloudPresentation", "CloudPresentationLayer", 71, new
            {
                widthPixels = 1936,
                heightPixels = 1216,
                requireQuantifiedAssessment = true
            }, CloudDependency, durable: true, required: false));
        }
        steps.Add(Step("EnvironmentPresentation", "EnvironmentPresentationLayer", 72, new
        {
            widthPixels = 1936,
            heightPixels = 1216,
            stackPreviewVariant = "combined-preview"
        }, CombinedPreviewDependencies, durable: true));
        steps.Add(Step("OverlayManifest", "OverlayManifest", 80, new { },
            cloud ? ManifestDependencies : ManifestDependenciesWithoutCloud, durable: true));
        steps.Add(Step("PresentationMaterializer", "PresentationMaterializer", 81, new { outputVariant = "installer-annotated-preview" },
            cloud ? MaterializerDependencies : MaterializerDependenciesWithoutCloud, durable: true));
        steps.Add(Step(
            "LocalStorage",
            "Storage",
            100,
            new { storageRoot = "/app/data/raw", retentionDays = 7, updateLatestFrame = true, queueForUpload = false },
            PresentationDependencies));
        steps.Add(Step(
            "ArchiveStorage",
            "Storage",
            110,
            new { storageRoot = "/app/data/archive", retentionDays = 30, updateLatestFrame = false, queueForUpload = false },
            PresentationDependencies));
        steps.Add(Step(
            "Telemetry",
            "Telemetry",
            1000,
            new { },
            TelemetryDependencies));
        return steps;
    }

    private static CaptureProcessingStepConfig Step(
        string id,
        string type,
        int order,
        object options,
        IReadOnlyList<string>? dependsOn = null,
        bool durable = false,
        bool required = true)
        => new(type, id, order, JsonSerializer.SerializeToElement(options), dependsOn,
            Required: required,
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
