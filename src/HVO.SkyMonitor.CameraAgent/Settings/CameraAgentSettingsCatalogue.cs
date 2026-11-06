using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.SiteProfile;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;

namespace HVO.SkyMonitor.CameraAgent.Settings;

/// <summary>
/// Every operator setting the settings API can read and change, described once: the API serves it, and the web UI and
/// command line build their forms from it. Each group is kept in exactly one store and is the unit of one write.
/// </summary>
/// <remarks>
/// Deployment settings (paths, listeners, central integration, credentials) are not here: the installer owns them.
/// </remarks>
internal static class CameraAgentSettingsCatalogue
{
    public const string SiteGroup = "site";
    public const string LocationGroup = "location";
    public const string SkyMapGroup = "skyMap";
    public const string CameraGroup = "camera";
    public const string ScheduleGroup = "schedule";
    public const string CurrentSkyGroup = "currentSky";
    public const string OrbitalElementsGroup = "orbitalElements";
    public const string SatellitePlanningGroup = "satellitePlanning";
    public const string SpaceWeatherGroup = "spaceWeather";
    public const string TimeDataRefreshGroup = "timeDataRefresh";

    public const string SettingsFileStore = "settingsFile";
    public const string DeploymentLocationStore = "deploymentLocation";
    public const string RigRevisionStore = "rigRevision";
    public const string ScheduleRevisionStore = "scheduleRevision";

    public static CameraAgentSettingDefinition ObservatoryName { get; } = SiteText(
        "site.observatoryName",
        SettingsFileSiteProfileStore.ObservatoryNameKey,
        "Observatory name",
        "The observatory this camera belongs to, as the workspace shows it.",
        SiteProfileLimits.MaximumNameLength);

    public static CameraAgentSettingDefinition CameraName { get; } = SiteText(
        "site.cameraName",
        SettingsFileSiteProfileStore.CameraNameKey,
        "Camera name",
        "What the workspace calls this camera. Until the settings file has a site profile, the name the installation " +
        "configured is shown; once it has one, an empty name shows as \"This camera\".",
        SiteProfileLimits.MaximumNameLength);

    public static CameraAgentSettingDefinition OwnerName { get; } = SiteText(
        "site.ownerName",
        SettingsFileSiteProfileStore.OwnerNameKey,
        "Owner name",
        "Who to contact about this camera.",
        SiteProfileLimits.MaximumNameLength);

    public static CameraAgentSettingDefinition OwnerContact { get; } = SiteText(
        "site.ownerContact",
        SettingsFileSiteProfileStore.OwnerContactKey,
        "Owner contact",
        "How to reach the owner: an email address, a telephone number, or both.",
        SiteProfileLimits.MaximumContactLength);

    public static CameraAgentSettingDefinition Latitude { get; } = Coordinate(
        "location.latitudeDegrees", "Latitude", "Degrees north of the equator; south is negative.", -90, 90, "degrees");

    public static CameraAgentSettingDefinition Longitude { get; } = Coordinate(
        "location.longitudeDegrees", "Longitude", "Degrees east of Greenwich; west is negative.", -180, 180, "degrees",
        ", east positive");

    public static CameraAgentSettingDefinition Elevation { get; } = Coordinate(
        "location.elevationMeters",
        "Elevation",
        "Metres above sea level.",
        ManualDeploymentLocationContract.MinimumElevationMeters,
        ManualDeploymentLocationContract.MaximumElevationMeters,
        "metres");

    public static CameraAgentSettingDefinition TimeZone { get; } = new()
    {
        Key = "location.timeZoneId",
        Group = LocationGroup,
        Title = "Time zone",
        Description = "The IANA time zone of the site, such as America/Phoenix or UTC. Schedules and the observing day " +
                      "follow it.",
        Type = CameraAgentSettingType.String,
        Safety = CameraAgentSettingSafety.CaptureGeometry,
        Application = CameraAgentSettingApplication.Restart,
        MaximumLength = 128,
        Rule = value => value is string zone && DeploymentLocationSnapshot.IsPortableTimeZoneId(zone)
            ? null
            : "Time zone must be an IANA identifier this host can resolve, such as America/Phoenix or UTC."
    };

    public static CameraAgentSettingDefinition MaximumSceneObjects { get; } = new()
    {
        Key = "skyMap.maximumObjects",
        Group = SkyMapGroup,
        Title = "Scene object limit",
        Description = "How many of the brightest catalog stars the sky map shows and a virtual sky capture draws and " +
                      "labels. Visible constellation figure stars and solar-system bodies are always included beyond " +
                      "it. A camera profile set to an exhaustive stellar candidate selection ignores it. Clear it to " +
                      "use the default.",
        Type = CameraAgentSettingType.Integer,
        Safety = CameraAgentSettingSafety.CaptureOutput,
        Application = CameraAgentSettingApplication.Immediate,
        Nullable = true,
        Minimum = 1,
        Maximum = CameraAgentSkyMapProjection.MaximumConfigurableObjects,
        Unit = "objects",
        Default = CameraAgentSkyMapProjection.DefaultMaximumObjects,
        ConfigurationKey = SkyMapOptions.MaximumObjectsKey
    };

    public static CameraAgentSettingDefinition SkyLighting { get; } = new()
    {
        Key = "camera.skyLighting",
        Group = CameraGroup,
        Title = "Sky lighting",
        Description = "How a virtual sky camera lights its simulated sky. It applies only to a virtual sky camera. Auto " +
                      "exposure still meters within the configured limits, and capture windows, sunrise, sunset and " +
                      "the observing day still follow the real Sun.",
        Type = CameraAgentSettingType.Enum,
        Safety = CameraAgentSettingSafety.CaptureOutput,
        Application = CameraAgentSettingApplication.Restart,
        AllowedValues =
        [
            new(nameof(VirtualSkyIlluminationMode.LegacyScalarSolar), "Uniform daylight",
                "The whole sky brightens evenly with the real Sun's altitude. This is the default."),
            new(nameof(VirtualSkyIlluminationMode.SolarDriven), "Realistic daylight",
                "A clear-sky approximation: a bounded blue sky and a warm horizon on the side facing the real Sun."),
            new(nameof(VirtualSkyIlluminationMode.ControlledNight), "No daylight",
                "A night sky at the real time, around the clock: stars keep moving, and capture control treats every " +
                "frame as night.")
        ],
        Default = nameof(VirtualSkyIlluminationMode.LegacyScalarSolar)
    };

    public static CameraAgentSettingDefinition ActiveScheduleRevision { get; } = new()
    {
        Key = "schedule.activeRevision",
        Group = ScheduleGroup,
        Title = "Active schedule revision",
        Description = "The capture schedule revision in effect. Choosing the saved draft applies it; choosing an earlier " +
                      "revision rolls back to it. The capture in progress finishes durable publication first, and " +
                      "captures already taken keep the revision they were admitted under.",
        Type = CameraAgentSettingType.Enum,
        Safety = CameraAgentSettingSafety.CaptureOutput,
        Application = CameraAgentSettingApplication.NextCaptureBoundary
    };

    public static CameraAgentSettingDefinition MinimumRefreshInterval { get; } = new()
    {
        Key = "currentSky.minimumRefreshSeconds",
        Group = CurrentSkyGroup,
        Title = "Minimum image refresh",
        Description = "The shortest time the Current Sky page shows one capture before it moves to a newer one. It moves " +
                      "only to a capture whose overlays are complete, and once this time has passed it shows the newest " +
                      "such capture, skipping any in between. Refresh image moves at once. Captures are not affected. " +
                      "Clear it to use the default.",
        Type = CameraAgentSettingType.Integer,
        Safety = CameraAgentSettingSafety.Descriptive,
        Application = CameraAgentSettingApplication.Immediate,
        Nullable = true,
        Minimum = CurrentSkyRefreshSetting.MinimumSeconds,
        Maximum = CurrentSkyRefreshSetting.MaximumSeconds,
        Unit = "seconds",
        Default = CurrentSkyRefreshSetting.DefaultSeconds,
        ConfigurationKey = CurrentSkyRefreshSetting.ConfigurationKey
    };

    public static CameraAgentSettingDefinition OrbitalRefreshEnabled { get; } = FeatureSwitch(
        OrbitalElementsGroup, "CameraAgent:OrbitalElements:Enabled", "Orbital refresh",
        "Allow bounded CelesTrak orbital refresh attempts. After restarting, Software & Catalog offers guided " +
        "creation of the refresh automation. Retained snapshots stay readable when refresh is off.",
        CameraAgentSettingApplication.Restart);

    public static CameraAgentSettingDefinition SatellitePlanningEnabled { get; } = FeatureSwitch(
        SatellitePlanningGroup, "CameraAgent:SatellitePlanning:Enabled", "Satellite planning",
        "Allow diagnostic pass planning from local orbital snapshots and verified Earth-orientation and leap-second " +
        "data: the pair bundled with this release, a newer refreshed pair, or a hand-configured override. Nothing is " +
        "captured or scheduled.",
        CameraAgentSettingApplication.Immediate);

    public static CameraAgentSettingDefinition TimeDataRefreshEnabled { get; } = FeatureSwitch(
        TimeDataRefreshGroup, "CameraAgent:SatellitePlanning:TimeDataRefreshEnabled", "Time-data refresh",
        "Allow an at-most-daily download of newer Earth-orientation (IERS/USNO finals2000A) and leap-second (IERS) files " +
        "from their approved HTTPS endpoints. Each pair is checked against the providers' checksums and parsed before " +
        "it is adopted; a failed refresh keeps the current pair. Runs only as the reserved time-data automation.",
        CameraAgentSettingApplication.Immediate);

    public static CameraAgentSettingDefinition SpaceWeatherEnabled { get; } = FeatureSwitch(
        SpaceWeatherGroup, "CameraAgent:SpaceWeather:Enabled", "Space weather",
        "Allow bounded SWPC space-weather refresh attempts. After restarting, the Space weather page offers guided " +
        "creation of the refresh automation. Retained notices stay readable when refresh is off; cloud is never assessed.",
        CameraAgentSettingApplication.Restart);

    /// <summary>Groups in display order.</summary>
    public static IReadOnlyList<CameraAgentSettingsGroup> Groups { get; } =
    [
        new(SiteGroup, "Site profile",
            "Names and contact details that label this camera in the workspace. They stay on this CameraAgent; " +
            "capture, geometry and LogicHost registration never read them.",
            SettingsFileStore, [ObservatoryName, CameraName, OwnerName, OwnerContact]),
        new(LocationGroup, "Deployment location",
            "The coordinates and time zone schedules, projection and capture evidence use. A change is recorded as a new " +
            "local version that activates at the next CameraAgent start; with central integration on, LogicHost " +
            "reviews it first. Values a change leaves out keep the coordinates that govern the next start.",
            DeploymentLocationStore, [Latitude, Longitude, Elevation, TimeZone]),
        new(SkyMapGroup, "Sky map",
            "The catalog depth the sky map and virtual sky captures project.",
            SettingsFileStore, [MaximumSceneObjects]),
        new(CameraGroup, "Camera",
            "The active camera's operator choices. A change saves a new camera revision and stages a rig revision that " +
            "applies at the next CameraAgent start.",
            RigRevisionStore, [SkyLighting]),
        new(ScheduleGroup, "Capture schedule",
            "Which capture schedule revision is in effect. Schedules are edited and saved as drafts on the Schedule page.",
            ScheduleRevisionStore, [ActiveScheduleRevision]),
        new(CurrentSkyGroup, "Current sky",
            "How the Current Sky page follows new captures. It changes only what the page shows.",
            SettingsFileStore, [MinimumRefreshInterval]),
        new(OrbitalElementsGroup, "Orbital refresh", OrbitalRefreshEnabled.Description,
            SettingsFileStore, [OrbitalRefreshEnabled]),
        new(SatellitePlanningGroup, "Satellite planning", SatellitePlanningEnabled.Description,
            SettingsFileStore, [SatellitePlanningEnabled]),
        new(TimeDataRefreshGroup, "Time-data refresh", TimeDataRefreshEnabled.Description,
            SettingsFileStore, [TimeDataRefreshEnabled]),
        new(SpaceWeatherGroup, "Space weather", SpaceWeatherEnabled.Description,
            SettingsFileStore, [SpaceWeatherEnabled])
    ];

    public static IReadOnlyList<CameraAgentSettingDefinition> All { get; } = [.. Groups.SelectMany(group => group.Settings)];

    public static CameraAgentSettingsGroup? FindGroup(string? name)
        => Groups.FirstOrDefault(group => string.Equals(group.Name, name, StringComparison.Ordinal));

    public static CameraAgentSettingDefinition? Find(string? key)
        => All.FirstOrDefault(definition => string.Equals(definition.Key, key, StringComparison.Ordinal));

    public static CameraAgentSettingsCatalogueResponse ToResponse()
        => new(
            CameraAgentSettingsContract.SchemaVersion,
            [.. Groups.Select(group => new CameraAgentSettingsGroupDescriptor(
                group.Name,
                group.Title,
                group.Description,
                group.Store,
                [.. group.Settings.Select(setting => setting.ToDescriptor())]))]);

    // On/off choices reuse the settings v1 enum wire shape, including clients from earlier releases.
    private static CameraAgentSettingDefinition FeatureSwitch(string group, string configurationKey, string title,
        string description, CameraAgentSettingApplication application) => new()
        {
            Key = group + ".enabled",
            Group = group,
            Title = title,
            Description = description,
            Type = CameraAgentSettingType.Enum,
            Safety = CameraAgentSettingSafety.Descriptive,
            Application = application,
            AllowedValues = [new("false", "Off", "Disabled"), new("true", "On", "Enabled")],
            Default = "false",
            ConfigurationKey = configurationKey
        };

    private static CameraAgentSettingDefinition SiteText(
        string key,
        string configurationKey,
        string title,
        string description,
        int maximumLength)
        => new()
        {
            Key = key,
            Group = SiteGroup,
            Title = title,
            Description = description,
            Type = CameraAgentSettingType.String,
            Safety = CameraAgentSettingSafety.Descriptive,
            Application = CameraAgentSettingApplication.Immediate,
            Nullable = true,
            MaximumLength = maximumLength,
            ConfigurationKey = configurationKey
        };

    private static CameraAgentSettingDefinition Coordinate(
        string key,
        string title,
        string description,
        double minimum,
        double maximum,
        string unit,
        string? rangeNote = null)
        => new()
        {
            RangeNote = rangeNote,
            Key = key,
            Group = LocationGroup,
            Title = title,
            Description = description,
            Type = CameraAgentSettingType.Number,
            Safety = CameraAgentSettingSafety.CaptureGeometry,
            Application = CameraAgentSettingApplication.Restart,
            Minimum = minimum,
            Maximum = maximum,
            Unit = unit
        };
}

/// <summary>One settings group: a title, the store that keeps it, and its settings in display order.</summary>
internal sealed record CameraAgentSettingsGroup(
    string Name,
    string Title,
    string Description,
    string Store,
    IReadOnlyList<CameraAgentSettingDefinition> Settings);
