using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.SiteProfile;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Settings;
using HVO.SkyMonitor.Common.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Settings;

/// <summary>
/// The settings service over the stores a host uses: a real operator settings file and site profile store, real
/// named-rig and schedule stores over a temporary journal, and a real protected or recording deployment-location store.
/// </summary>
internal sealed class CameraAgentSettingsHarness : IDisposable
{
    internal const string Actor = "owner-id";
    internal const string InstalledName = "Installed camera name";

    private readonly List<IDisposable> _owned = [];
    private readonly string _root;

    private CameraAgentSettingsHarness(string root)
    {
        _root = root;
    }

    public ConfigurationManager Configuration { get; private set; } = default!;
    public OperatorSettingsFile SettingsFile { get; private set; } = default!;
    public RecordingLocationStore Location { get; } = new();
    public IDeploymentLocationStore LocationStore { get; private set; } = default!;
    public SqliteNamedRigProfileStore Named { get; private set; } = default!;
    public SqliteCaptureScheduleStore Schedule { get; private set; } = default!;
    public CaptureScheduleRuntimeCoordinator Runtime { get; private set; } = default!;
    public CameraModuleConfig CameraConfiguration { get; private set; } = default!;
    public ISceneObjectBound SceneObjectBound { get; private set; } = default!;
    public CurrentSkyRefreshSetting CurrentSkyRefresh { get; private set; } = default!;
    public CameraAgentSettingsService Service { get; private set; } = default!;
    public string SettingsPath => SettingsFile.FilePath;

    /// <param name="moduleType">The active camera's module, <c>VirtualSky</c> unless a test needs a physical one.</param>
    /// <param name="overrides">Host settings a source above the settings file supplies.</param>
    public static async Task<CameraAgentSettingsHarness> CreateAsync(
        string moduleType = "VirtualSky",
        IReadOnlyDictionary<string, string?>? overrides = null,
        bool configured = true,
        bool realLocation = false,
        ILogger<CameraAgentSettingsService>? logger = null,
        Func<string, CancellationToken, Task>? beforeStoreWrite = null,
        TimeProvider? timeProvider = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        var root = Path.Combine(Path.GetTempPath(), "hvo-settings-harness", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var harness = new CameraAgentSettingsHarness(root);
        try
        {
            var configuration = new ConfigurationManager();
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DeploymentLocalSettings.PathVariable] = Path.Combine(root, DeploymentLocalSettings.FileName),
                ["CameraAgent:DisplayName"] = InstalledName
            });
            var local = DeploymentLocalSettings.AddConfiguredFile(configuration, root)!;
            if (overrides is not null)
            {
                configuration.AddInMemoryCollection(overrides);
            }
            var settingsFile = harness.Own(new OperatorSettingsFile(
                local.FilePath, local.Reload, local.FindOverriddenKeys,
                NullLogger<OperatorSettingsFile>.Instance));
            var hostOptions = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root, DisplayName = InstalledName });
            var site = harness.Own(new SettingsFileSiteProfileStore(
                hostOptions, NullLogger<SettingsFileSiteProfileStore>.Instance, settingsFile));
            var sceneObjects = new ConfigurationSceneObjectBound(
                CameraAgentServiceCollectionExtensions.SkyMapObjectBound(configuration));

            var camera = Camera(moduleType);
            var ingress = new JournalInitializer(root);
            var schedule = harness.Own(new SqliteCaptureScheduleStore(ingress, hostOptions, clock));
            var active = await schedule.InitializeAsync(camera, CancellationToken.None).ConfigureAwait(false);
            var telemetry = harness.Own(new CaptureControlTelemetry());
            var admission = harness.Own(new CaptureAdmissionCoordinator(ingress, hostOptions, clock, telemetry));
            var runtime = harness.Own(new CaptureScheduleRuntimeCoordinator(schedule, admission,
                new RawIngressState(clock), new CaptureLaneState(clock, hostOptions),
                new EmptyPipelineFactory(), clock));
            if (configured)
            {
                // A running host starts the schedule runtime before any operator request.
                _ = await runtime.InitializeAsync(camera, CancellationToken.None).ConfigureAwait(false);
            }
            var named = new SqliteNamedRigProfileStore(ingress, hostOptions, clock, schedule, runtime);
            _ = await named.ImportActiveAsync(active.ActiveRevision, CancellationToken.None).ConfigureAwait(false);

            harness.LocationStore = harness.Location;
            if (realLocation)
            {
                var protector = new DataProtectionDeploymentLocationProtector(
                    DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys"))));
                var location = harness.Own(new ProtectedDeploymentLocationStore(hostOptions, protector,
                    clock, NullLogger<ProtectedDeploymentLocationStore>.Instance));
                _ = await location.InitializeAsync(new DeploymentLocationSeed("site", "installer", null, null, null,
                    new ObservatoryLocation(35, -114, 1000, "UTC")), CancellationToken.None).ConfigureAwait(false);
                harness.LocationStore = location;
            }
            harness.Configuration = configuration;
            harness.SettingsFile = settingsFile;
            harness.Named = named;
            harness.Schedule = schedule;
            harness.Runtime = runtime;
            harness.CameraConfiguration = camera;
            harness.SceneObjectBound = sceneObjects;
            harness.CurrentSkyRefresh = new CurrentSkyRefreshSetting(configuration);
            harness.Service = new CameraAgentSettingsService(
                new CameraAgentSettingSources(configuration, local.IsSettingsFileProvider),
                sceneObjects,
                harness.CurrentSkyRefresh,
                site,
                harness.LocationStore,
                named,
                runtime,
                new FixedConfigurationAccessor(configured ? camera : null),
                harness.Own(new CameraAgentOperatorTelemetry()),
                logger ?? NullLogger<CameraAgentSettingsService>.Instance,
                settingsFile)
            {
                BeforeStoreWriteAsync = beforeStoreWrite
            };
            return harness;
        }
        catch
        {
            harness.Dispose();
            throw;
        }
    }

    public async Task<CameraAgentSettingsGroupState> GroupAsync(string group)
        => (await Service.GetGroupAsync(group, CancellationToken.None).ConfigureAwait(false))!;

    public Task<CameraAgentSettingsWriteResult> WriteAsync(
        string group,
        string? expectedVersion,
        IReadOnlyDictionary<string, object?> values,
        string? idempotencyKey = null,
        string? reason = null)
        => Service.WriteAsync(
            group,
            new CameraAgentSettingsUpdateRequest(
                expectedVersion,
                values.ToDictionary(
                    pair => pair.Key,
                    pair => System.Text.Json.JsonSerializer.SerializeToElement(pair.Value),
                    StringComparer.Ordinal),
                reason),
            Actor,
            idempotencyKey,
            CancellationToken.None);

    public string ReadSettingsFile() => File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath) : string.Empty;

    /// <summary>Stages a draft schedule revision through the runtime, as the Schedule page does.</summary>
    public async Task<CaptureScheduleStoreSnapshot> StageScheduleDraftAsync(string profileId, double gain)
    {
        var current = await Schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        return await Runtime.StageAsync(
            LocalCaptureProfileDefinition.CreateV2(CameraConfiguration, Definition(profileId, gain)),
            $"stage-{profileId}", current.Version, Actor, null, CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<(long Commands, long Activations)> ScheduleMutationCountsAsync()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_root, "journal", "raw-ingress.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM capture_schedule_commands),
                   (SELECT COUNT(*) FROM capture_schedule_activations);
            """;
        using var reader = await command.ExecuteReaderAsync(CancellationToken.None).ConfigureAwait(false);
        _ = await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private T Own<T>(T value) where T : IDisposable
    {
        _owned.Add(value);
        return value;
    }

    public void Dispose()
    {
        for (var index = _owned.Count - 1; index >= 0; index--) _owned[index].Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private static CameraModuleConfig Camera(string moduleType)
        => new(
            new ObservatoryLocation(35, -114, 1000, "UTC"),
            new CameraModuleDescriptor(moduleType),
            new CameraRigConfig(
                new SensorProfile("test", 2, 2, 5, SensorColorMode.Mono, CameraPixelFormat.Mono16),
                new OpticsProfile("Perspective", 50, 10, 0, ImageCircleRadiusPixels: 1),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), 1, 10)
                {
                    Envelope = new ExposureEnvelope(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), 1, 10,
                        new ExposureDefaults(TimeSpan.FromSeconds(1), 1), new ExposureDefaults(TimeSpan.FromSeconds(5), 10), 0.5)
                },
                new CameraControlPolicy
                {
                    ExposureControl = AutomaticControlOwnership.Disabled,
                    GainControl = AutomaticControlOwnership.Disabled
                }),
            CapturePipelineConfig.Empty)
        {
            AgentId = "settings-agent",
            Schedule = Definition("initial", 1),
            DeploymentLocation = DeploymentLocationSnapshot.Create("test-location", 1, "test", null,
                DateTimeOffset.UnixEpoch, null, 35, -114, 1000, "UTC")
        };

    private static CaptureScheduleDefinition Definition(string profileId, double gain)
        => new(
            "capture-schedule-v1",
            [new CaptureScheduleSetpointProfile(profileId, TimeSpan.FromSeconds(5), gain, TimeSpan.FromSeconds(10))],
            [new CaptureWeeklyScheduleWindow(
                "monday",
                DayOfWeek.Monday,
                new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(18, 0)),
                new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(6, 0), DayOffset: 1),
                profileId)]);

    /// <summary>A manual-location store that records commands and answers with a fixed disposition.</summary>
    internal sealed class RecordingLocationStore : IDeploymentLocationStore
    {
        public DeploymentLocationSnapshot? Active { get; set; } = DeploymentLocationSnapshot.Create(
            "site", 3, "installer", null, DateTimeOffset.UnixEpoch, null, 35, -114, 1000, "UTC");

        private ManualDeploymentLocationState _manual = new(
            true, "site", 3, 3, 4, null, 0, false, false, false, null, null, []);

        public bool FailRead { get; set; }

        public ManualDeploymentLocationState Manual
        {
            get => FailRead ? throw new IOException("Test location store unavailable.") : _manual;
            set => _manual = value;
        }

        public List<ManualDeploymentLocationRequest> Requests { get; } = [];

        public ManualDeploymentLocationStatus Status { get; set; } = ManualDeploymentLocationStatus.Applied;

        public string? FieldPath { get; set; }

        public ValueTask<ManualDeploymentLocationResult> ApplyManualAsync(
            ManualDeploymentLocationRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return ValueTask.FromResult(new ManualDeploymentLocationResult(Status, null, FieldPath, Manual));
        }

        public ValueTask<DeploymentLocationSnapshot> InitializeAsync(
            DeploymentLocationSeed seed,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public DeploymentLocationSnapshot Resolve(CaptureLocationProvenance provenance, DateTimeOffset? effectiveUtc = null)
            => throw new NotSupportedException();
    }

    private sealed class FixedConfigurationAccessor(CameraModuleConfig? configuration) : ICameraAgentConfigurationAccessor
    {
        public bool IsConfigured => configuration is not null;

        public void SetConfiguration(CameraModuleConfig config) => throw new NotSupportedException();

        public ValueTask<CameraModuleConfig> WaitForConfigurationAsync(CancellationToken cancellationToken)
            => configuration is null
                ? throw new InvalidOperationException("The test configuration is not loaded.")
                : ValueTask.FromResult(configuration);
    }

    private sealed class EmptyPipelineFactory : ICaptureProcessingPipelineFactory
    {
        public CaptureProcessingGraph CreateGraph(CameraModuleConfig config) => new([]);

        public CaptureProcessingPlanPreview PreviewPlan(CameraModuleConfig config) => throw new NotSupportedException();
    }

    private sealed class JournalInitializer(string root) : IRawCaptureIngress
    {
        public async ValueTask InitializeAsync(CancellationToken cancellationToken)
            => await new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), busyTimeoutSeconds: 5)
                .InitializeAsync(cancellationToken).ConfigureAwait(false);

        public ValueTask<RawCaptureReceipt?> AcceptAsync(CameraModuleConfig configuration,
            CaptureLoopSubmission submission, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
