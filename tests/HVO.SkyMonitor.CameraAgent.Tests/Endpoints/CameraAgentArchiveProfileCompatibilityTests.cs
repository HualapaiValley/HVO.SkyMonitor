using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Modules;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Endpoints;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Endpoints")]
public sealed class CameraAgentArchiveProfileCompatibilityTests
{
    [TestMethod]
    public async Task ReleasedLocalArchiveListsLooksUpAndOpensAfterRestartUntilJournalApplies()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-local-archive-http");
        var archive = Path.Combine(root, "storage-b");
        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "legacy-local-archive");
        try
        {
            foreach (var source in Directory.GetFiles(fixtures, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(archive, Path.GetRelativePath(fixtures, source));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await File.WriteAllBytesAsync(destination, await File.ReadAllBytesAsync(source).ConfigureAwait(false))
                    .ConfigureAwait(false);
            }

            var id = Guid.Parse("f866ce6a-86b7-4289-bbe1-247cc658f198");
            var startup = StoragePolicyRawIngressIntegrationTests.Profile(root);
            var archiveStorage = startup.Pipeline.Steps.Single(static step => step.Type == "Storage") with
            {
                Id = "storage-b",
                Enabled = false,
                Options = JsonSerializer.SerializeToElement(new FileStorageCaptureProcessingStepOptions
                {
                    StorageRoot = archive,
                    UpdateLatestFrame = false
                })
            };
            startup = startup with { Pipeline = startup.Pipeline with { Steps = [.. startup.Pipeline.Steps, archiveStorage] } };
            var config = startup with
            {
                Pipeline = startup.Pipeline with
                {
                    Steps = startup.Pipeline.Steps.Select(static step => step.Type == "Storage"
                        ? step with { Enabled = step.Id == "storage-b" }
                        : step).ToArray()
                }
            };
            var expected = new byte[] { 1, 2, 3, 4 };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await AssertArchiveFollowsActiveStorageAsync(root, startup, config, id, expected, timeout.Token)
                .ConfigureAwait(false);
            for (var reopen = 0; reopen < 2; reopen++)
            {
                var app = CreateApp(root, config);
                await using var scope = app.ConfigureAwait(false);
                await AssertArchiveAsync(app, id, FrameArtifactRole.Calibrated, expected, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            // A retained raw-only journal has no evidence of product publication.
            var journalDirectory = Path.Combine(archive, "journal");
            var journal = Path.Combine(journalDirectory, "raw-ingress.db");
            Assert.IsFalse(File.Exists(journal), "Browsing the standalone files must not create archive journal records.");
            Directory.CreateDirectory(journalDirectory);
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var date = new DateOnly(2026, 1, 2);
            Assert.HasCount(1, storage.List(archive, date, FrameArtifactRole.Calibrated, 10));
            var connection = new SqliteConnection($"Data Source={journal};Pooling=False");
            await using (connection.ConfigureAwait(false))
            {
                await connection.OpenAsync(timeout.Token).ConfigureAwait(false);
                using var schema = connection.CreateCommand();
                schema.CommandText = """
                    PRAGMA journal_mode=DELETE;
                    PRAGMA user_version=15;
                    CREATE TABLE raw_captures(capture_id TEXT PRIMARY KEY);
                    BEGIN EXCLUSIVE;
                    """;
                await schema.ExecuteNonQueryAsync(timeout.Token).ConfigureAwait(false);
                var locked = Assert.ThrowsExactly<SqliteException>(() => storage.List(archive, date, FrameArtifactRole.Calibrated, 10));
                Assert.AreEqual(5, locked.SqliteErrorCode, "The authority must be busy while the exclusive lock is held.");
                await AssertArchiveRefusedAfterReopenAsync(root, config, id, FrameArtifactRole.Calibrated, timeout.Token)
                    .ConfigureAwait(false);
            }

            Assert.IsEmpty(storage.List(archive, date, FrameArtifactRole.Calibrated, 10));
            await AssertArchiveRefusedAfterReopenAsync(root, config, id, FrameArtifactRole.Calibrated,
                timeout.Token).ConfigureAwait(false);

            if (OperatingSystem.IsLinux())
            {
                var redirected = Path.Combine(root, "redirected-journal.db");
                File.Move(journal, redirected);
                File.CreateSymbolicLink(journal, redirected);
                _ = Assert.ThrowsExactly<IOException>(() => storage.List(archive, date, FrameArtifactRole.Calibrated, 10));
                await AssertArchiveRefusedAfterReopenAsync(root, config, id, FrameArtifactRole.Calibrated, timeout.Token)
                    .ConfigureAwait(false);

                File.Delete(redirected);
                _ = Assert.ThrowsExactly<IOException>(() => storage.List(archive, date, FrameArtifactRole.Calibrated, 10));
            }
            File.Delete(journal);
            Directory.CreateDirectory(journal);
            _ = Assert.ThrowsExactly<IOException>(() => storage.List(archive, date, FrameArtifactRole.Calibrated, 10));
            await AssertArchiveRefusedAfterReopenAsync(root, config, id, FrameArtifactRole.Calibrated, timeout.Token)
                .ConfigureAwait(false);
            Directory.Delete(journal);

            Directory.Delete(journalDirectory);
            await File.WriteAllTextAsync(journalDirectory, "invalid", timeout.Token).ConfigureAwait(false);
            _ = Assert.ThrowsExactly<IOException>(() => storage.List(archive, date, FrameArtifactRole.Calibrated, 10));
            await AssertArchiveRefusedAfterReopenAsync(root, config, id, FrameArtifactRole.Calibrated, timeout.Token)
                .ConfigureAwait(false);
            File.Delete(journalDirectory);

            var recovered = CreateApp(root, config);
            await using var recoveredScope = recovered.ConfigureAwait(false);
            await AssertArchiveAsync(recovered, id, FrameArtifactRole.Calibrated, expected, timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertArchiveFollowsActiveStorageAsync(string root, CameraModuleConfig startup,
        CameraModuleConfig archiveProfile, Guid artifactId, byte[] pixels, CancellationToken token)
    {
        var app = CreateApp(root, startup);
        await using var scope = app.ConfigureAwait(false);
        var schedule = app.Services.GetRequiredService<CaptureScheduleRuntimeCoordinator>();
        var store = app.Services.GetRequiredService<SqliteCaptureScheduleStore>();
        var initial = await schedule.InitializeAsync(startup, token).ConfigureAwait(false);
        await AssertArchiveAsync(app, artifactId, FrameArtifactRole.Calibrated, expected: null, token).ConfigureAwait(false);
        var beforeStage = await store.GetSnapshotAsync(token).ConfigureAwait(false);
        var profile = LocalCaptureProfileDefinition.CreateV2(archiveProfile, startup.Schedule!);
        var staged = await CameraAgentProcessingGraphOperationsEndpointsTests.InvokeForBodyAsync(
            app, "StageCameraAgentSchedule", HttpMethods.Post,
            new { profile, basisRevisionId = initial.Revision.RevisionId, expectedVersion = beforeStage.Version },
            idempotencyKey: "stage-archive", antiforgeryValidated: true, cancellationToken: token).ConfigureAwait(false);
        Assert.AreEqual(StatusCodes.Status200OK, staged.Status, staged.Body);
        var pending = await store.GetSnapshotAsync(token).ConfigureAwait(false);
        Assert.IsNotNull(pending.PendingRevision);

        var archiveRevision = pending.PendingRevision.RevisionId;
        await SelectRevisionAsync("ActivateCameraAgentSchedule", archiveRevision, "activate-archive",
            "storage-b", pixels).ConfigureAwait(false);
        await SelectRevisionAsync("RollbackCameraAgentSchedule", initial.Revision.RevisionId, "rollback-archive",
            "storage", expected: null).ConfigureAwait(false);
        await SelectRevisionAsync("ActivateCameraAgentSchedule", archiveRevision, "restore-archive",
            "storage-b", pixels).ConfigureAwait(false);

        async Task SelectRevisionAsync(string endpoint, string revisionId, string key, string storageId, byte[]? expected)
        {
            var before = await store.GetSnapshotAsync(token).ConfigureAwait(false);
            var result = await CameraAgentProcessingGraphOperationsEndpointsTests.InvokeForBodyAsync(
                app, endpoint, HttpMethods.Post, new { revisionId, expectedVersion = before.Version },
                idempotencyKey: key, antiforgeryValidated: true, cancellationToken: token).ConfigureAwait(false);
            Assert.AreEqual(StatusCodes.Status200OK, result.Status, result.Body);
            var durable = (await store.GetSnapshotAsync(token).ConfigureAwait(false)).ActiveRevision;
            Assert.AreEqual(revisionId, durable.RevisionId);
            Assert.AreEqual(storageId, durable.Profile.ProcessingSteps.Single(IsEnabledStorage).Id);
            var active = schedule.Snapshot!;
            Assert.AreEqual(revisionId, active.Revision.RevisionId);
            Assert.AreEqual(storageId, active.Configuration.Pipeline.Steps.Single(IsEnabledStorage).Id);
            await AssertArchiveAsync(app, artifactId, FrameArtifactRole.Calibrated, expected, token).ConfigureAwait(false);
        }

        static bool IsEnabledStorage(CaptureProcessingStepConfig step) => step.Type == "Storage" && step.Enabled != false;
    }

    [TestMethod]
    public async Task InvalidCenteredProfileLoadStageAndActivationPreserveWorkingLegacyCapture()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-centered-profile-http");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var token = deadline.Token;
        try
        {
            var legacy = StoragePolicyRawIngressIntegrationTests.Profile(root);
            var app = CreateApp(root, legacy);
            await using var scope = app.ConfigureAwait(false);
            var services = app.Services;
            var factory = services.GetRequiredService<ICameraModuleFactory>();
            var validation = services.GetRequiredService<ICameraModuleConfigurationValidator>();
            var schedule = services.GetRequiredService<CaptureScheduleRuntimeCoordinator>();
            var store = services.GetRequiredService<SqliteCaptureScheduleStore>();
            var initial = await schedule.InitializeAsync(legacy, token).ConfigureAwait(false);
            var context = schedule.CaptureContext;

            var compatibleModule = new CameraModuleDescriptor("VirtualSky");
            var validCentered = new CenteredExposureProfile("fixture-camera", CenteredExposureProfile.CurrentExecutionVersion);
            var supportedConfig = legacy with
            {
                Module = compatibleModule,
                CenteredExposure = validCentered,
                Rig = legacy.Rig with { Sensor = legacy.Rig.Sensor with { ResponseMode = SensorResponseMode.Monochrome } },
                Pipeline = legacy.Pipeline with
                {
                    Steps =
                    [
                        new("ProjectedScene", "scene", DependsOn: ["$raw"], Required: false,
                            Publication: new(CaptureProcessingPersistenceMode.DurableLocal)),
                        .. legacy.Pipeline.Steps
                    ]
                }
            };
            validation.Validate(supportedConfig);
            var invalid = new[]
            {
                legacy with { CenteredExposure = validCentered },
                supportedConfig with { CenteredExposure = validCentered with { ExecutionVersion = "future-unsupported" } },
                supportedConfig with { CenteredExposure = validCentered with { CameraId = "bad id" } }
            };

            var filePath = Path.Combine(root, "cameraagent.json");
            var sample = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "cameraagent.sample.json"), token)
                .ConfigureAwait(false);
            var document = JsonNode.Parse(sample)!.AsObject();
            var loader = new FileCameraAgentConfigurationLoader(Options.Create(new CameraAgentHostOptions
            {
                ConfigFilePath = filePath,
                AgentId = legacy.AgentId,
                Observatory = legacy.Observatory,
                CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled }
            }), NullLogger<FileCameraAgentConfigurationLoader>.Instance, moduleConfigurationValidator: validation);
            var supportedSchedule = supportedConfig.Schedule!;
            var alwaysOpen = supportedSchedule with
            {
                LegacyAlwaysOpen = true,
                LegacySetpointProfileId = supportedSchedule.SetpointProfiles[0].Id
            };
            document["module"]!["type"] = compatibleModule.Type;
            document["centeredExposure"] = JsonSerializer.SerializeToNode(validCentered, JsonSerializerOptions.Web);
            var fileSchedule = document["schedule"]!;
            fileSchedule["legacyAlwaysOpen"] = true;
            fileSchedule["legacySetpointProfileId"] = fileSchedule["setpointProfiles"]![0]!["id"]!.GetValue<string>();
            await File.WriteAllTextAsync(filePath, document.ToJsonString(), token).ConfigureAwait(false);
            var alwaysOpenFile = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => loader.LoadAsync(token))
                .ConfigureAwait(false);
            StringAssert.Contains(alwaysOpenFile.Message, "legacy schedule compatibility", StringComparison.Ordinal);
            fileSchedule.AsObject().Remove("legacyAlwaysOpen");
            fileSchedule.AsObject().Remove("legacySetpointProfileId");

            var beforeAlwaysOpen = await store.GetSnapshotAsync(token).ConfigureAwait(false);
            var refusedAlwaysOpen = await Assert.ThrowsExactlyAsync<ArgumentException>(() => schedule.StageAsync(
                LocalCaptureProfileDefinition.CreateV2(supportedConfig, alwaysOpen), "stage-centered-always-open",
                beforeAlwaysOpen.Version, "owner", null, token)).ConfigureAwait(false);
            StringAssert.Contains(refusedAlwaysOpen.Message, "localProfile.schedule.weeklyWindows", StringComparison.Ordinal);
            Assert.AreEqual(beforeAlwaysOpen.Version, (await store.GetSnapshotAsync(token).ConfigureAwait(false)).Version);
            foreach (var invalidProfile in invalid)
            {
                document["module"]!["type"] = invalidProfile.ModuleType;
                document["module"]!.AsObject().Remove("options");
                document["centeredExposure"] = JsonSerializer.SerializeToNode(invalidProfile.CenteredExposure, JsonSerializerOptions.Web);
                await File.WriteAllTextAsync(filePath, document.ToJsonString(), token).ConfigureAwait(false);
                var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => loader.LoadAsync(token))
                    .ConfigureAwait(false);
                StringAssert.Contains(exception.Message, "centered exposure", StringComparison.OrdinalIgnoreCase);
            }
            document["centeredExposure"] = null;
            document["module"]!["type"] = legacy.ModuleType;
            await File.WriteAllTextAsync(filePath, document.ToJsonString(), token).ConfigureAwait(false);
            Assert.IsNull((await loader.LoadAsync(token).ConfigureAwait(false)).CenteredExposure);

            var camera = factory.Create(legacy.ModuleType);
            await using var cameraScope = camera.ConfigureAwait(false);
            await camera.InitializeAsync(initial.Configuration, context.RevisionChanged).ConfigureAwait(false);
            await AssertLegacyFrameAsync().ConfigureAwait(false);
            var before = await store.GetSnapshotAsync(token).ConfigureAwait(false);

            for (var index = 0; index < invalid.Length; index++)
            {
                var proposal = LocalCaptureProfileDefinition.CreateV2(invalid[index], legacy.Schedule!);
                var refused = await StageAsync(proposal, before, $"refuse-stage-{index}").ConfigureAwait(false);
                Assert.AreEqual(StatusCodes.Status422UnprocessableEntity, refused.Status, refused.Body);
                var after = await store.GetSnapshotAsync(token).ConfigureAwait(false);
                Assert.AreEqual(before.Version, after.Version);
                Assert.AreEqual(before.PendingRevision?.RevisionId, after.PendingRevision?.RevisionId);
                Assert.AreEqual(initial.Revision.RevisionId, after.ActiveRevision.RevisionId);
                Assert.IsFalse(context.RevisionChanged.IsCancellationRequested);
            }

            // Seed the durable pending revision through the historical store boundary.
            // The authenticated activate handler must revalidate it before changing the active owner.
            var unchanged = before;
            for (var index = 0; index < invalid.Length; index++)
            {
                var incompatible = LocalCaptureProfileDefinition.CreateV2(invalid[index], legacy.Schedule!);
                var oldPending = await store.StageAsync(incompatible, $"historical-stage-{index}", unchanged.Version,
                    "owner", null, token).ConfigureAwait(false);
                var activation = await ActivateAsync(oldPending, $"refuse-activation-{index}").ConfigureAwait(false);
                Assert.AreEqual(StatusCodes.Status422UnprocessableEntity, activation.Status, activation.Body);
                unchanged = await store.GetSnapshotAsync(token).ConfigureAwait(false);
                Assert.AreEqual(oldPending.Version, unchanged.Version);
                Assert.AreEqual(oldPending.PendingRevision!.RevisionId, unchanged.PendingRevision!.RevisionId);
                Assert.AreEqual(initial.Revision.RevisionId, unchanged.ActiveRevision.RevisionId);
                Assert.IsFalse(context.RevisionChanged.IsCancellationRequested);
            }
            await AssertLegacyFrameAsync().ConfigureAwait(false);

            var supported = LocalCaptureProfileDefinition.CreateV2(supportedConfig, legacy.Schedule!);
            var stagedResponse = await StageAsync(supported, unchanged, "stage-supported").ConfigureAwait(false);
            Assert.AreEqual(StatusCodes.Status200OK, stagedResponse.Status, stagedResponse.Body);
            var staged = await store.GetSnapshotAsync(token).ConfigureAwait(false);
            var accepted = await ActivateAsync(staged, "activate-supported").ConfigureAwait(false);
            Assert.AreEqual(StatusCodes.Status200OK, accepted.Status, accepted.Body);
            Assert.AreEqual(accepted, await ActivateAsync(staged, "activate-supported").ConfigureAwait(false));
            Assert.AreEqual(validCentered, schedule.Snapshot!.Configuration.CenteredExposure);
            Assert.AreEqual(staged.PendingRevision!.RevisionId, (await store.GetSnapshotAsync(token).ConfigureAwait(false)).ActiveRevision.RevisionId);
            Assert.IsTrue(context.RevisionChanged.IsCancellationRequested);
            var virtualCamera = factory.Create(schedule.Snapshot.Configuration.ModuleType);
            await using var virtualScope = virtualCamera.ConfigureAwait(false);
            Assert.IsInstanceOfType<ICameraBoundExposureCapture>(virtualCamera);

            Task<(int Status, string Body)> StageAsync(LocalCaptureProfileDefinition profile, CaptureScheduleStoreSnapshot state, string key) =>
                CameraAgentProcessingGraphOperationsEndpointsTests.InvokeForBodyAsync(app, "StageCameraAgentSchedule", HttpMethods.Post,
                    new { profile, basisRevisionId = state.ActiveRevision.RevisionId, expectedVersion = state.Version },
                    idempotencyKey: key, antiforgeryValidated: true, cancellationToken: token);

            Task<(int Status, string Body)> ActivateAsync(CaptureScheduleStoreSnapshot state, string key) =>
                CameraAgentProcessingGraphOperationsEndpointsTests.InvokeForBodyAsync(app, "ActivateCameraAgentSchedule", HttpMethods.Post,
                    new { revisionId = state.PendingRevision!.RevisionId, expectedVersion = state.Version },
                    idempotencyKey: key, antiforgeryValidated: true, cancellationToken: token);

            async Task AssertLegacyFrameAsync()
            {
                var frame = (await camera.CaptureAsync(new(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), CaptureMode.Still),
                    context.RevisionChanged).ConfigureAwait(false)).Frame;
                Assert.IsNotNull(frame);
                Assert.AreEqual(2, frame.Width);
                Assert.AreEqual(2, frame.Height);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static async Task AssertArchiveRefusedAfterReopenAsync(
        string root, CameraModuleConfig config, Guid artifactId, FrameArtifactRole role, CancellationToken token)
    {
        var app = CreateApp(root, config);
        await using var scope = app.ConfigureAwait(false);
        await AssertArchiveAsync(app, artifactId, role, expected: null, token).ConfigureAwait(false);
    }

    private static WebApplication CreateApp(string root, CameraModuleConfig config)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CameraAgent:RawIngressRoot"] = root,
            ["CameraAgent:RawIngressReserveBytes"] = "0"
        });
        builder.Services.AddCameraAgentInfrastructure(builder.Configuration);
        builder.Services.AddSingleton<CameraAgentOperatorTelemetry>();
        builder.Services.AddSingleton<ICelestialCatalog>(new InMemoryCelestialCatalog([]));
        builder.Services.AddCameraModule<VirtualSkyCameraModule>("VirtualSky");
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddSingleton<IDeploymentLocationProtector, DataProtectionDeploymentLocationProtector>();
        var app = builder.Build();
        app.Services.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(config);
        app.MapCameraAgentGalleryEndpoints();
        app.MapCameraAgentArtifactEndpoints();
        app.MapCameraAgentScheduleOperationsEndpoints();
        return app;
    }

    private static async Task AssertArchiveAsync(
        WebApplication app, Guid artifactId, FrameArtifactRole role, byte[]? expected, CancellationToken token)
    {
        await app.Services.GetRequiredService<IRawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
        var page = await ReadAsync("GetCameraAgentProductPage", "/api/v1/operations/gallery/products", $"?role={role}", null)
            .ConfigureAwait(false);
        Assert.AreEqual(StatusCodes.Status200OK, page.Status);
        using var listed = JsonDocument.Parse(page.Body);
        var isListed = listed.RootElement.GetProperty("items").EnumerateArray()
            .Any(item => item.GetProperty("artifactId").GetGuid() == artifactId);
        var lookup = await ReadAsync("GetCameraAgentProduct", $"/api/v1/operations/gallery/products/{artifactId:D}", null, artifactId)
            .ConfigureAwait(false);
        var content = await ReadAsync("GetCameraAgentArtifactContent", $"/api/v1/operations/artifacts/{artifactId:D}/content", null, artifactId)
            .ConfigureAwait(false);
        Assert.AreEqual(expected is not null, isListed,
            $"Archive detail returned HTTP {lookup.Status} and content returned HTTP {content.Status}.");
        if (expected is null)
        {
            Assert.AreEqual(StatusCodes.Status404NotFound, lookup.Status);
            Assert.AreEqual(StatusCodes.Status404NotFound, content.Status);
        }
        else
        {
            Assert.AreEqual(StatusCodes.Status200OK, lookup.Status);
            Assert.AreEqual(StatusCodes.Status200OK, content.Status);
            using var detail = JsonDocument.Parse(lookup.Body);
            var product = detail.RootElement.GetProperty("product");
            Assert.AreEqual(artifactId, product.GetProperty("artifactId").GetGuid());
            Assert.AreEqual("legacy-calibration", product.GetProperty("nodeId").GetString());
            Assert.AreEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(expected)),
                product.GetProperty("checksumSha256").GetString());
            Assert.AreEqual(JsonValueKind.Null, detail.RootElement.GetProperty("node").ValueKind);
            CollectionAssert.AreEqual(expected, content.Body);
        }

        async Task<(int Status, byte[] Body)> ReadAsync(string endpointName, string path, string? query, Guid? id)
        {
            var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints)
                .Single(item => item.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == endpointName);
            var context = new DefaultHttpContext
            {
                RequestServices = app.Services,
                RequestAborted = token,
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new(ClaimTypes.NameIdentifier, "owner"), new(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)],
                    IdentityConstants.ApplicationScheme))
            };
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = path;
            context.Request.QueryString = new QueryString(query);
            if (id is { } value)
                context.Request.RouteValues["artifactId"] = value.ToString("D");
            using var body = new MemoryStream();
            context.Response.Body = body;
            await endpoint.RequestDelegate!(context).ConfigureAwait(false);
            return (context.Response.StatusCode, body.ToArray());
        }
    }
}
