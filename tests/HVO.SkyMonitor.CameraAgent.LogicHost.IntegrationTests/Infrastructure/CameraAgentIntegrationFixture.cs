using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.CameraAgent.Authentication;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.CameraAgent.Common.Frames;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.TestInfrastructure;
using HVO.SkyMonitor.TestSupport;
using HVO.SkyMonitor.Common.Identity;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;

/// <summary>
/// Boots the central SkyMonitor host and a camera agent instance for end-to-end testing.
/// </summary>
internal sealed class CameraAgentIntegrationFixture : IDisposable
{
    private static readonly string[] RecurringWorkerSuppressionEvidenceVariables =
    [
        "HVO_ISSUE_246_RETENTION_EVIDENCE",
        "HVO_ISSUE_248_BASELINE_EVIDENCE",
        "HVO_ISSUE_248_SMOKE",
        "HVO_ISSUE_248_CENSORED_SMOKE",
        "HVO_ISSUE_248_AGGREGATE_ONLY",
        "HVO_ISSUE_250_EVIDENCE"
    ];
    /// <summary>Bounded warm-up budget for one complete published capture before the fixture is shared.</summary>
    /// <remarks>
    /// This is the twenty-second semantic budget issue #682 requires. Raising it would let a real
    /// first-capture regression be absorbed silently, so the measured warm-up is recorded instead
    /// and rendered by <see cref="DescribeRuntimeState"/>; drift then reaches the retained result
    /// before it becomes fatal, and any later budget change is backed by that measurement.
    /// </remarks>
    private static readonly TimeSpan WarmReadinessBudget = TimeSpan.FromSeconds(20);
    private readonly bool _hybridTransientMode;
    private readonly EnvironmentalDeliveryCompletionTracker _environmentalDelivery = new();
    private readonly IntegrationTestFixture _hostFixture = CreateHostFixture();
    private readonly BoundedLogRecorder _logRecorder = new(capacity: 200);
    private readonly object _consumerGate = new();
    private readonly List<string> _consumerHistory = [];
    /// <summary>Sentinel for <see cref="_warmReadinessElapsedTicks"/> before the barrier measures.</summary>
    private const long WarmReadinessNotMeasured = -1;
    /// <summary>
    /// The measured warm-up, in <see cref="TimeSpan"/> ticks, or <see cref="WarmReadinessNotMeasured"/>.
    /// </summary>
    /// <remarks>
    /// Written by the initializing thread inside <see cref="WaitForWarmCaptureAsync"/> and read by
    /// whichever thread renders <see cref="DescribeRuntimeState"/>. A <see cref="long"/> written and
    /// read through <see cref="Volatile"/> is atomic on every supported target, so the renderer sees
    /// either the sentinel or a complete measurement, never a partially published value.
    /// </remarks>
    private long _warmReadinessElapsedTicks = WarmReadinessNotMeasured;
    private string? _currentConsumer;
    private string? _previousConsumer;
    private WebApplicationFactory<Program>? _agentFactory;
    private WebApplicationFactory<Program>? _agentBaseFactory;
    private Uri? _centralIdentityBaseUri;
    private Uri? _centralHostBaseUri;
    private JsonWebKeySet? _jwksDocument;
    private string? _configurationPath;
    private string? _storageRoot;
    private CatalogFixtureInstallation? _catalogFixture;

    public CameraAgentIntegrationFixture(bool hybridTransientMode = false)
    {
        _hybridTransientMode = hybridTransientMode;
    }

    internal IntegrationTestFixture HostFixture => _hostFixture;

    /// <summary>
    /// Initializes the host and camera agent factories.
    /// </summary>
    public async Task InitializeAsync()
    {
        await _hostFixture.InitializeAsync().ConfigureAwait(false);
        await _hostFixture.SeedActiveDeviceAsync("cameraagent-integration-test").ConfigureAwait(false);
        _storageRoot = Path.Combine(Path.GetTempPath(), $"hvo-cameraagent-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storageRoot);
        var provisioningRoot = Path.Combine(_storageRoot, "provisioning");
        Directory.CreateDirectory(provisioningRoot);
        await File.WriteAllTextAsync(
            Path.Combine(provisioningRoot, "device-identity.json"),
            JsonSerializer.Serialize(new
            {
                deviceId = "cameraagent-integration-test",
                verificationCode = "INTEG2TEST",
                createdUtc = DateTimeOffset.UtcNow
            })).ConfigureAwait(false);
        _catalogFixture = CatalogFixtureInstallation.Create(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "hyg-v42-bright-stars.sqlite"));
        _configurationPath = Path.Combine(_storageRoot, "cameraagent.integration.json");
        var template = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "cameraagent.integration.json")).ConfigureAwait(false);
        if (_hybridTransientMode)
        {
            template = template
                .Replace("3600.0", "0.5", StringComparison.Ordinal)
                .Replace("\"angularWidthDegrees\": 0.2", "\"angularWidthDegrees\": 2.0", StringComparison.Ordinal)
                .Replace("\"pixelX\": 1.5,\n                \"pixelY\": 1.5", "\"pixelX\": 20.0,\n                \"pixelY\": 24.0", StringComparison.Ordinal)
                .Replace("\"pixelX\": 20.0,\n                \"pixelY\": 24.0,\n                \"electronsPerSecond\": 1000000000.0,\n                \"sigmaPixels\": 0.25\n              }\n            ]", "\"pixelX\": 44.0,\n                \"pixelY\": 24.0,\n                \"electronsPerSecond\": 1000000000.0,\n                \"sigmaPixels\": 2.0\n              }\n            ]", StringComparison.Ordinal)
                .Replace("\"sigmaPixels\": 0.25", "\"sigmaPixels\": 2.0", StringComparison.Ordinal);
        }
        using var hostClient = _hostFixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        _centralHostBaseUri = hostClient.BaseAddress ?? new Uri("http://127.0.0.1");

        var metadataEndpoint = new Uri("/.well-known/openid-configuration", UriKind.Relative);
        var metadataResponse = await hostClient.GetStringAsync(metadataEndpoint).ConfigureAwait(false);
        var metadata = ParseMetadata(metadataResponse);
        _centralIdentityBaseUri = metadata.Issuer ?? _centralHostBaseUri;
        if (metadata.JwksUri is not null)
        {
            var jwksJson = await hostClient.GetStringAsync(metadata.JwksUri).ConfigureAwait(false);
            _jwksDocument = new JsonWebKeySet(jwksJson);
        }

        TransientEpochUtc = (_hybridTransientMode
            ? DateTimeOffset.UtcNow.AddSeconds(10)
            : DateTimeOffset.UtcNow.AddMinutes(-1)).ToUniversalTime();
        await File.WriteAllTextAsync(_configurationPath,
            template
                .Replace("__STORAGE_ROOT__", JsonSerializer.Serialize(_storageRoot), StringComparison.Ordinal)
                .Replace("__TRANSIENT_EPOCH_UTC__", JsonSerializer.Serialize(TransientEpochUtc), StringComparison.Ordinal))
            .ConfigureAwait(false);

        _agentBaseFactory = new WebApplicationFactory<Program>();
        _agentFactory = _agentBaseFactory.WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                // Registered before service resolution so CameraAgent startup and capture-loop
                // recovery failures are retained for a readiness diagnostic.
                builder.ConfigureLogging(logging => logging.AddProvider(_logRecorder));
                var overrides = BuildConfigurationOverrides();
                // Program captures local Identity settings before WebApplicationFactory app overrides are applied.
                foreach (var setting in overrides.Where(static setting =>
                             setting.Key.StartsWith("LocalIdentity:", StringComparison.Ordinal)))
                {
                    builder.UseSetting(setting.Key, setting.Value);
                }
                builder.ConfigureAppConfiguration((_, config) =>
                    config.AddInMemoryCollection(overrides!));
                builder.ConfigureTestServices(services =>
                {
                    services.AddAuthentication(options =>
                        {
                            options.DefaultAuthenticateScheme = IntegrationUserAuthenticationHandler.SchemeName;
                            options.DefaultChallengeScheme = IntegrationUserAuthenticationHandler.SchemeName;
                            options.DefaultForbidScheme = IntegrationUserAuthenticationHandler.SchemeName;
                        })
                        .AddScheme<AuthenticationSchemeOptions, IntegrationUserAuthenticationHandler>(
                            IntegrationUserAuthenticationHandler.SchemeName,
                            _ => { });

                    if (!_hybridTransientMode)
                    {
                        var drainService = services.Single(descriptor =>
                            descriptor.ServiceType == typeof(IHostedService) &&
                            descriptor.ImplementationType == typeof(ArtifactOutboxDrainService));
                        services.Remove(drainService);
                    }

                    services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                    {
                        options.BackchannelHttpHandler = _hostFixture.Factory.Server.CreateHandler();
                        if (_jwksDocument is not null)
                        {
                            options.TokenValidationParameters.IssuerSigningKeys = _jwksDocument.GetSigningKeys();
                        }
                    });

                    services.AddHttpClient(SkyMonitorClientOptions.HttpClientName)
                        .ConfigurePrimaryHttpMessageHandler(_ => new EnvironmentalDeliveryTrackingHandler(
                            _environmentalDelivery,
                            _hostFixture.Factory.Server.CreateHandler()));

                    services.AddHttpClient(CentralAuthenticationService.TokenClientName)
                        .ConfigurePrimaryHttpMessageHandler(_ => _hostFixture.Factory.Server.CreateHandler());
                });
            });

        using var scope = _agentFactory.Services.CreateScope();
        var scopedProvider = scope.ServiceProvider;
        var ownerManager = scopedProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
        var owner = await ownerManager.FindByEmailAsync("owner@cameraagent.integration").ConfigureAwait(false)
            ?? throw new InvalidOperationException("The CameraAgent integration owner was not seeded.");
        owner.PasswordChangeRequired = false;
        var ownerUpdate = await ownerManager.UpdateAsync(owner).ConfigureAwait(false);
        if (!ownerUpdate.Succeeded)
        {
            throw new InvalidOperationException("The CameraAgent integration owner could not be prepared.");
        }
        var identity = await scopedProvider.GetRequiredService<IDeviceIdentityStore>()
            .GetOrCreateAsync(CancellationToken.None).ConfigureAwait(false);
        await _hostFixture.SeedActiveDeviceAsync(identity.DeviceId).ConfigureAwait(false);
        var activeDevice = await _hostFixture.GetActiveDeviceAsync(identity.DeviceId).ConfigureAwait(false);
        var centralIdentity = new CentralIdentityOptions
        {
            ServiceUrl = CentralIdentityBaseUri,
            Mode = AuthenticationMode.ClientCredentials,
            ClientCredentials = new ClientCredentialsOptions
            {
                ClientId = TestClients.SystemCameraAgent.ClientId,
                ClientSecret = TestClients.SystemCameraAgent.ClientSecret
            }
        };
        foreach (var requestedScope in TestClients.SystemCameraAgent.Scopes)
        {
            centralIdentity.ClientCredentials.Scopes.Add(requestedScope);
        }
        var cameraConfiguration = await scopedProvider.GetRequiredService<ICameraAgentConfigurationLoader>()
            .LoadAsync(CancellationToken.None).ConfigureAwait(false);
        await _hostFixture.SeedRigProfileAsync("cameraagent-integration-test", cameraConfiguration.Rig).ConfigureAwait(false);
        var deploymentLocationStore = scopedProvider.GetRequiredService<IDeploymentLocationStore>();
        var deployment = deploymentLocationStore.Active
            ?? throw new InvalidOperationException("The CameraAgent integration deployment location was not initialized.");
        var sourceKind = deploymentLocationStore.ResolveSourceKind(deployment);
        var acknowledgedAtUtc = DateTimeOffset.UtcNow;
        var locationAcknowledgment = new DeploymentLocationAcknowledgment(
            ObservatoryLocationSnapshot.Create(
                activeDevice.ObservatoryId,
                1,
                DateTimeOffset.UnixEpoch,
                deployment.LatitudeDegrees,
                deployment.LongitudeDegrees,
                deployment.ElevationMeters,
                deployment.TimeZoneId,
                null),
            deployment,
            sourceKind,
            DeploymentLocationResolutionStatus.Pending,
            "integration-fixture",
            acknowledgedAtUtc,
            null);
        await scopedProvider.GetRequiredService<IDeviceSecretStore>().SaveAsync(new DeviceSecrets(
            activeDevice.DevicePublicId,
            activeDevice.ObservatoryId,
            "CameraAgent Integration Device",
            "integration-registration-token",
            "/api/device/heartbeat",
            60,
            activeDevice.IssuedAtUtc,
            activeDevice.ExpiresAtUtc,
            "cameraagent-integration-key",
            centralIdentity,
            DeploymentLocationAcknowledgment: locationAcknowledgment), CancellationToken.None).ConfigureAwait(false);
        DeviceId = identity.DeviceId;
        DevicePublicId = activeDevice.DevicePublicId;
        ObservatoryId = activeDevice.ObservatoryId;
        var configuration = scopedProvider.GetRequiredService<IConfiguration>();
        var configuredServiceUrl = configuration["CentralIdentity:ServiceUrl"];
        var jwtOptions = scopedProvider
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
        await WaitForWarmCaptureAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Blocks until the configured capture pipeline has published one complete capture, so no test
    /// observes the shared fixture before the first raw, combined and display roles exist. The
    /// budget is bounded and independent of the per-test assertion window; exceeding it reports the
    /// worker, durable-state and log evidence needed to attribute the stall.
    /// </summary>
    private async Task WaitForWarmCaptureAsync()
    {
        if (_hybridTransientMode)
        {
            // The hybrid fixture deliberately schedules its transient epoch into the future and
            // owns its own readiness assertions.
            return;
        }

        var telemetry = _agentFactory!.Services.GetRequiredService<ICaptureTelemetryProvider>();
        var latest = _agentFactory.Services.GetRequiredService<ILatestFrameAccessor>();
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < WarmReadinessBudget)
        {
            if (latest.TryGetSnapshot(FrameArtifactRole.Raw, out _) &&
                latest.TryGetSnapshot(FrameArtifactRole.Combined, out _) &&
                latest.TryGetSnapshot(out _) &&
                telemetry.Latest is { FrameStored: true })
            {
                // Recorded whether or not the barrier is close to its budget, so a warm-up drifting
                // toward the bound is visible in the retained result instead of only when fatal.
                Volatile.Write(ref _warmReadinessElapsedTicks, stopwatch.Elapsed.Ticks);
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }

        Volatile.Write(ref _warmReadinessElapsedTicks, stopwatch.Elapsed.Ticks);
        throw new InvalidOperationException(FormattableString.Invariant(
            $"The shared CameraAgent fixture did not publish a complete capture within {WarmReadinessBudget.TotalSeconds:F0} s.{Environment.NewLine}") +
            FormattableString.Invariant($"telemetry: {VirtualSkyPipelineReadiness.DescribeSample(telemetry.Latest)}{Environment.NewLine}") +
            FormattableString.Invariant(
                $"raw:      {VirtualSkyPipelineReadiness.DescribeRole(latest.TryGetSnapshot(FrameArtifactRole.Raw, out var raw) ? raw : null)}{Environment.NewLine}") +
            FormattableString.Invariant(
                $"combined: {VirtualSkyPipelineReadiness.DescribeRole(latest.TryGetSnapshot(FrameArtifactRole.Combined, out var combined) ? combined : null)}{Environment.NewLine}") +
            FormattableString.Invariant(
                $"preview:  {VirtualSkyPipelineReadiness.DescribeRole(latest.TryGetSnapshot(out var preview) ? preview : null)}{Environment.NewLine}") +
            DescribeRuntimeState());
    }

    private static IntegrationTestFixture CreateHostFixture()
    {
        var suppressRecurringWorkers = RecurringWorkerSuppressionEvidenceVariables.Any(name => string.Equals(
            Environment.GetEnvironmentVariable(name),
            "1",
            StringComparison.Ordinal));
        return new IntegrationTestFixture(
            new Dictionary<string, string?>
            {
                ["CentralTransient:Mode"] = "Hybrid",
                ["CentralTransient:SourceRole"] = "Raw"
            },
            suppressRecurringWorkers,
            useEphemeralMinioStorage: true);
    }

    /// <summary>
    /// Creates an HTTP client for the central SkyMonitor host.
    /// </summary>
    public HttpClient CreateHostClient([CallerMemberName] string? consumer = null)
    {
        EnsureInitialized();
        RecordConsumer(consumer);
        return _hostFixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    /// <summary>
    /// Creates an HTTP client pointed at the camera agent.
    /// </summary>
    public HttpClient CreateCameraAgentClient([CallerMemberName] string? consumer = null)
    {
        EnsureInitialized();
        RecordConsumer(consumer);
        return _agentFactory!.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    public WebApplicationFactory<Program> CreateCameraAgentFactory(
        Action<IServiceCollection> configureServices,
        [CallerMemberName] string? consumer = null)
    {
        ArgumentNullException.ThrowIfNull(configureServices);
        EnsureInitialized();
        RecordConsumer(consumer);
        return _agentFactory!.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                foreach (var hostedService in services
                             .Where(static descriptor => descriptor.ServiceType == typeof(IHostedService))
                             .ToArray())
                {
                    services.Remove(hostedService);
                }
                configureServices(services);
            }));
    }

    /// <summary>
    /// Creates a scoped service provider from the camera agent factory.
    /// Caller is responsible for disposing the returned scope.
    /// </summary>
    public IServiceScope CreateCameraAgentScope([CallerMemberName] string? consumer = null)
    {
        EnsureInitialized();
        RecordConsumer(consumer);
        return _agentFactory!.Services.CreateScope();
    }

    public IServiceScope CreateHostScope([CallerMemberName] string? consumer = null)
    {
        EnsureInitialized();
        RecordConsumer(consumer);
        return _hostFixture.Factory.Services.CreateScope();
    }

    /// <summary>
    /// Gets the ordered identities of the tests that have taken a scope, client or factory from this
    /// shared fixture. Repeated calls from one test collapse to a single entry, so the previous entry
    /// is the preceding test rather than the preceding call.
    /// </summary>
    internal FixtureConsumers Consumers
    {
        get
        {
            lock (_consumerGate)
            {
                return new FixtureConsumers(_currentConsumer, _previousConsumer, _consumerHistory.ToArray());
            }
        }
    }

    private void RecordConsumer(string? consumer)
    {
        if (string.IsNullOrWhiteSpace(consumer))
        {
            return;
        }
        lock (_consumerGate)
        {
            if (string.Equals(_currentConsumer, consumer, StringComparison.Ordinal))
            {
                return;
            }
            _previousConsumer = _currentConsumer;
            _currentConsumer = consumer;
            _consumerHistory.Add(consumer);
            if (_consumerHistory.Count > 32)
            {
                _consumerHistory.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// Renders the shared-fixture runtime evidence needed to attribute a readiness stall: consumer
    /// transitions, the measured warm-up against its budget, the capture worker task, fleet,
    /// admission, raw-ingress, durable-lane and processing state, and the bounded CameraAgent log
    /// tail.
    /// </summary>
    internal string DescribeRuntimeState()
    {
        var builder = new StringBuilder();
        var consumers = Consumers;
        builder.AppendLine(FormattableString.Invariant(
            $"fixture consumers: current={consumers.Current ?? "<none>"} previous={consumers.Previous ?? "<none>"}"));
        builder.AppendLine(FormattableString.Invariant(
            $"  history: {(consumers.History.Count == 0 ? "<none>" : string.Join(" -> ", consumers.History))}"));
        var measuredTicks = Volatile.Read(ref _warmReadinessElapsedTicks);
        var warmMeasured = measuredTicks != WarmReadinessNotMeasured;
        var warmElapsed = warmMeasured
            ? FormattableString.Invariant($"{TimeSpan.FromTicks(measuredTicks).TotalSeconds:F3} s")
            : "<not measured>";
        // An unmeasured barrier cannot say whether the budget was exceeded. A lifted comparison
        // against a missing measurement renders False, which asserts a fact nobody measured; the
        // hybrid-transient fixture and any failure before the barrier runs both reach that state.
        var warmExceeded = warmMeasured
            ? (measuredTicks >= WarmReadinessBudget.Ticks ? "True" : "False")
            : "<unknown>";
        builder.AppendLine(FormattableString.Invariant(
            $"warm readiness: elapsed={warmElapsed} of {WarmReadinessBudget.TotalSeconds:F3} s budget") +
            FormattableString.Invariant($" (exceeded: {warmExceeded})"));

        if (_agentFactory is null)
        {
            builder.AppendLine("camera agent: <not initialized>");
            return builder.ToString();
        }

        var services = _agentFactory.Services;
        AppendSafely(builder, "capture service", () =>
        {
            var captureService = services.GetServices<IHostedService>().OfType<CameraCaptureService>().FirstOrDefault();
            if (captureService is null)
            {
                return "<not registered>";
            }
            var task = captureService.ExecuteTask;
            var fault = task?.Exception?.GetBaseException().ToString() ?? "<none>";
            return FormattableString.Invariant($"status={task?.Status.ToString() ?? "<not started>"} fault={fault}");
        });
        AppendSafely(builder, "fleet capture", () =>
        {
            var capture = services.GetRequiredService<FleetRuntimeState>().Snapshot.Capture;
            return FormattableString.Invariant($"availability={capture.Availability} reason={capture.Reason} lastSucceededUtc={capture.LastSucceededUtc:O}") +
                FormattableString.Invariant($" lastFailedUtc={capture.LastFailedUtc:O} lastRecoveredUtc={capture.LastRecoveredUtc:O}");
        });
        AppendSafely(builder, "capture admission", () =>
        {
            var admission = services.GetRequiredService<CaptureAdmissionCoordinator>().Snapshot;
            return FormattableString.Invariant(
                $"state={admission.State} version={admission.Version} updatedUtc={admission.UpdatedUtc:O} initialized={admission.IsInitialized}");
        });
        AppendSafely(builder, "raw ingress", () =>
        {
            var ingress = services.GetRequiredService<RawIngressState>().Snapshot;
            return FormattableString.Invariant($"availability={ingress.Availability} reason={ingress.Reason} pending={ingress.PendingCount} pendingBytes={ingress.PendingBytes}") +
                FormattableString.Invariant($" quarantine={ingress.QuarantineCount} oldestPendingUtc={ingress.OldestPendingUtc:O} evaluatedUtc={ingress.EvaluatedUtc:O}");
        });
        AppendSafely(builder, "capture processing", () =>
        {
            var processing = services.GetRequiredService<CaptureProcessingState>().Snapshot;
            return FormattableString.Invariant($"availability={processing.Availability} reason={processing.Reason} pending={processing.PendingCount} retry={processing.RetryCount}") +
                FormattableString.Invariant($" terminal={processing.TerminalCount} quarantine={processing.ProcessingQuarantineCount} missingProducts={processing.MissingProductCount}") +
                FormattableString.Invariant($" durableStateUnavailable={processing.DurableStateUnavailable} reconciliationFailed={processing.ReconciliationFailed}") +
                FormattableString.Invariant($" oldestPendingUtc={processing.OldestPendingUtc:O}");
        });
        AppendSafely(builder, "durable lane", DescribeDurableLane);

        var logs = _logRecorder.Snapshot();
        builder.AppendLine(FormattableString.Invariant($"camera agent logs (bounded, warning and above): {logs.Length}"));
        foreach (var entry in logs)
        {
            builder.AppendLine("  " + entry);
        }
        return builder.ToString();
    }

    private string DescribeDurableLane()
    {
        if (_storageRoot is null)
        {
            return "<no storage root>";
        }
        var path = Path.Combine(_storageRoot, "journal", "raw-ingress.db");
        if (!File.Exists(path))
        {
            return "<journal not created>";
        }
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            DefaultTimeout = 5,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT (SELECT COUNT(*) FROM raw_captures), " +
            "(SELECT COUNT(*) FROM raw_captures WHERE state = 'committed'), " +
            "(SELECT COUNT(*) FROM capture_lane_work), " +
            "(SELECT COUNT(*) FROM capture_lane_work WHERE state <> 'completed'), " +
            "(SELECT COUNT(*) FROM processing_nodes WHERE status <> 'Completed'), " +
            "(SELECT COUNT(*) FROM processing_outputs);";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return "<no rows>";
        }
        return FormattableString.Invariant($"rawCaptures={reader.GetInt64(0)} committed={reader.GetInt64(1)} laneWork={reader.GetInt64(2)}") +
            FormattableString.Invariant($" laneUnfinished={reader.GetInt64(3)} processingNodesUnfinished={reader.GetInt64(4)} processingOutputs={reader.GetInt64(5)}");
    }

    private static void AppendSafely(StringBuilder builder, string label, Func<string> describe)
    {
        string value;
        try
        {
            value = describe();
        }
#pragma warning disable CA1031 // A diagnostic renderer must never mask the failure it is describing.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            value = FormattableString.Invariant($"<unavailable: {exception.GetType().Name}: {exception.Message}>");
        }
        builder.AppendLine(FormattableString.Invariant($"{label}: {value}"));
    }

    /// <summary>
    /// Gets the base URI for the central identity service.
    /// </summary>
    public Uri CentralIdentityBaseUri => _centralIdentityBaseUri ?? throw new InvalidOperationException("Fixture has not been initialized.");

    public string StorageRoot => _storageRoot ?? throw new InvalidOperationException("Fixture has not been initialized.");

    public string DeviceId { get; private set; } = string.Empty;

    public Guid DevicePublicId { get; private set; }

    public Guid ObservatoryId { get; private set; }

    public DateTimeOffset TransientEpochUtc { get; private set; }

    public Task<int> CountEnvironmentalObservationsAsync(Guid observationId)
        => _hostFixture.CountEnvironmentalObservationsAsync(observationId);

    public Task WaitForEnvironmentalDeliveryAsync(Guid observationId, CancellationToken cancellationToken)
        => _environmentalDelivery.WaitAsync(observationId, cancellationToken);

    public void Dispose()
    {
        _agentFactory?.Dispose();
        _agentBaseFactory?.Dispose();
        _catalogFixture?.Dispose();
        _hostFixture.Dispose();
        _logRecorder.Dispose();
        if (_storageRoot is not null && Directory.Exists(_storageRoot))
        {
            Directory.Delete(_storageRoot, recursive: true);
        }
    }

    /// <summary>Ordered identities of the tests that have taken state from the shared fixture.</summary>
    internal sealed record FixtureConsumers(string? Current, string? Previous, IReadOnlyList<string> History);

    /// <summary>
    /// Retains a bounded tail of CameraAgent warning-and-above log entries from before the first test
    /// runs, so startup and capture-loop recovery failures survive into a readiness diagnostic.
    /// </summary>
    private sealed class BoundedLogRecorder(int capacity) : ILoggerProvider
    {
        private readonly Queue<string> _entries = new();
        private readonly object _gate = new();

        public ILogger CreateLogger(string categoryName) => new BoundedLogger(categoryName, this);

        public string[] Snapshot()
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _entries.Clear();
            }
        }

        private void Record(string entry)
        {
            lock (_gate)
            {
                _entries.Enqueue(entry);
                while (_entries.Count > capacity)
                {
                    _entries.Dequeue();
                }
            }
        }

        private sealed class BoundedLogger(string category, BoundedLogRecorder owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullFixtureScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                if (!IsEnabled(logLevel))
                {
                    return;
                }
                owner.Record(
                    FormattableString.Invariant($"{DateTimeOffset.UtcNow:O} {logLevel} {category} [{eventId.Id}] {formatter(state, exception)}") +
                    (exception is null ? string.Empty : " | " + exception.GetType().Name + ": " + exception.Message));
            }
        }

        private sealed class NullFixtureScope : IDisposable
        {
            public static NullFixtureScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed class EnvironmentalDeliveryCompletionTracker
    {
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _pending = new();

        public async Task WaitAsync(Guid observationId, CancellationToken cancellationToken)
        {
            var completion = _pending.GetOrAdd(
                observationId,
                static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            try
            {
                await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _pending.TryRemove(new KeyValuePair<Guid, TaskCompletionSource>(observationId, completion));
            }
        }

        public void Complete(Guid observationId)
        {
            if (_pending.TryRemove(observationId, out var completion))
            {
                completion.TrySetResult();
            }
        }
    }

    private sealed class EnvironmentalDeliveryTrackingHandler(
        EnvironmentalDeliveryCompletionTracker tracker,
        HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Guid? observationId = null;
            if (request.RequestUri?.AbsolutePath == "/api/device/environmental-observations" && request.Content is not null)
            {
                var body = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                observationId = EnvironmentalObservationDeliveryJson.ParseEnvelope(body).Value?.Observation.ObservationId;
            }
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (observationId is { } completed && response.IsSuccessStatusCode)
            {
                tracker.Complete(completed);
            }
            return response;
        }
    }

    private Dictionary<string, string?> BuildConfigurationOverrides()
    {
        var identityBase = CentralIdentityBaseUri.ToString().TrimEnd('/') + "/";
        var apiBase = (_centralHostBaseUri ?? CentralIdentityBaseUri).ToString().TrimEnd('/');
        var overrides = new Dictionary<string, string?>
        {
            ["CentralIdentity:ServiceUrl"] = identityBase,
            ["CentralIdentity:Mode"] = "ClientCredentials",
            ["CentralIdentity:ClientCredentials:ClientId"] = TestClients.SystemCameraAgent.ClientId,
            ["CentralIdentity:ClientCredentials:ClientSecret"] = TestClients.SystemCameraAgent.ClientSecret,
            ["LocalIdentity:AdminEmail"] = "owner@cameraagent.integration",
            ["LocalIdentity:AdminPassword"] = "IntegrationOwner!123",
            ["LocalIdentity:AdminPasswordFile"] = string.Empty,
            ["LocalIdentity:AllowMissingAdminPassword"] = "false",
            ["LocalIdentity:DatabasePath"] = Path.Combine(_storageRoot!, "cameraagent_identity.db"),
            ["LocalIdentity:CookieName"] = "CameraAgent.Integration.Auth",
            ["SkyMonitor:BaseUrl"] = apiBase,
            ["Catalog:Root"] = _catalogFixture?.Root,
            ["Catalog:RequiredCatalogId"] = "hyg-v42-fixture",
            ["Catalog:RequiredPackageKind"] = "Fixture",
            ["CameraAgent:ConfigFilePath"] = _configurationPath,
            ["CameraAgent:RawIngressRoot"] = _storageRoot,
            ["CameraAgent:DiskPressureThresholdPercent"] = "1",
            ["CameraAgent:DiskPressureRecoveryPercent"] = "2",
            ["DeviceProvisioning:StateDirectory"] = Path.Combine(_storageRoot!, "provisioning")
        };
        if (_hybridTransientMode)
        {
            overrides["CameraAgent:TransientDetection:Mode"] = TransientOperatingMode.Hybrid.ToString();
            overrides["CameraAgent:TransientDetection:Required"] = "true";
            overrides["CameraAgent:TransientDetection:WorkerPollIntervalMilliseconds"] = "100";
            overrides["CameraAgent:TransientDetection:StarMaximumMagnitude"] = "-30";
        }

        var scopePrefix = "CentralIdentity:ClientCredentials:Scopes";
        for (var i = 0; i < TestClients.SystemCameraAgent.Scopes.Length; i++)
        {
            overrides[$"{scopePrefix}:{i}"] = TestClients.SystemCameraAgent.Scopes[i];
        }

        return overrides;
    }

    private void EnsureInitialized()
    {
        if (_agentFactory is null || _centralIdentityBaseUri is null)
        {
            throw new InvalidOperationException("Camera agent integration fixture has not been initialized.");
        }
    }

    private static IdentityMetadata ParseMetadata(string metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return new IdentityMetadata();
        }

        using var document = JsonDocument.Parse(metadata);
        Uri? issuer = null;
        Uri? jwks = null;

        if (document.RootElement.TryGetProperty("issuer", out var issuerProperty))
        {
            var issuerValue = issuerProperty.GetString();
            if (!string.IsNullOrWhiteSpace(issuerValue))
            {
                issuer = new Uri(issuerValue, UriKind.Absolute);
            }
        }

        if (document.RootElement.TryGetProperty("jwks_uri", out var jwksProperty))
        {
            var jwksValue = jwksProperty.GetString();
            if (!string.IsNullOrWhiteSpace(jwksValue))
            {
                jwks = new Uri(jwksValue, UriKind.Absolute);
            }
        }

        return new IdentityMetadata
        {
            Issuer = issuer,
            JwksUri = jwks
        };
    }

    private sealed class IdentityMetadata
    {
        public Uri? Issuer { get; init; }

        public Uri? JwksUri { get; init; }
    }
}
