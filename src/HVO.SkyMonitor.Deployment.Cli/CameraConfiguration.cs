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
    private static readonly string[] CalibrationDependency = ["Calibration"];
    private static readonly string[] PreviewDependency = ["Preview"];
    private static readonly string[] AnnotationDependency = ["Annotation"];
    private static readonly string[] LocalStorageDependency = ["LocalStorage"];
    private static readonly string[] ArchiveStorageDependency = ["ArchiveStorage"];

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
            "VirtualAsi174MmReduced",
            484,
            304,
            5.86,
            SensorColorMode.Mono,
            CameraPixelFormat.Mono16,
            SensorResponseMode.Monochrome,
            968,
            SampleByteOrder.LittleEndian,
            "virtual-asi174mm-electron-domain-v2"),
        new OpticsProfile(
            "EquidistantFisheye",
            0,
            180,
            0,
            LensKind.Fisheye,
            242,
            152,
            148.96,
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
        ProfileVersion: "installer-virtualsky-v1");

    private static IReadOnlyList<CaptureProcessingStepConfig> CreateProcessingSteps() =>
    [
        Step("Calibration", "Calibration", -1000, new { strategy = "None", outputVariant = "none" }, RawDependency),
        Step("Preview", "Preview", 50, new
        {
            recipeVersion = "mono16-asinh-v2",
            blackPercentile = 0.5,
            whitePercentile = 0.9999,
            asinhStrength = 4.0
        }, CalibrationDependency),
        Step("Annotation", "Annotation", 75, new
        {
            markRadius = 6,
            markerValue = 144,
            drawLabels = true,
            maximumLabelMagnitude = 2.5,
            labelScale = 2,
            drawConstellationLines = true,
            constellationIds = ConstellationIds,
            drawImageCircle = true,
            drawCardinalDirections = true,
            recipeVersion = "named-object-compass-annotation-v3"
        }, PreviewDependency),
        Step(
            "LocalStorage",
            "Storage",
            100,
            new { storageRoot = "/app/data/raw", retentionDays = 7, updateLatestFrame = true, queueForUpload = false },
            AnnotationDependency),
        Step(
            "ArchiveStorage",
            "Storage",
            110,
            new { storageRoot = "/app/data/archive", retentionDays = 30, updateLatestFrame = false, queueForUpload = false },
            LocalStorageDependency),
        Step(
            "Telemetry",
            "Telemetry",
            1000,
            new { },
            ArchiveStorageDependency)
    ];

    private static CaptureProcessingStepConfig Step(
        string id,
        string type,
        int order,
        object options,
        IReadOnlyList<string>? dependsOn = null)
        => new(type, id, order, JsonSerializer.SerializeToElement(options), dependsOn);

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
