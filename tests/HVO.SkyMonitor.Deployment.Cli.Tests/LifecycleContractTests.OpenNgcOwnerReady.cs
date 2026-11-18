using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("select")]
    [DataRow("rollback")]
    [DataRow("disabled")]
    public async Task OpenNgcOwnerReadyTransitionsResumeCapture(string action)
    {
        using var fixture = await PrepareOwnerReadyFixtureAsync(action);
        using var endpoint = new OwnerReadyInstallationEndpoint(fixture);
        var owner = new OwnerBootstrapClient(new Uri("http://verification.invalid"), endpoint);
        var lifecycle = new OwnerReadyCaptureClient();
        var selected = await OwnerReadyTransitionAsync(fixture, action, lifecycle, owner);

        Assert.AreEqual(action == "select" ? DeepSkyIdentity(fixture) : null, selected.DeepSkyCatalog);
        Assert.AreEqual(fixture.Manifest.Catalog, selected.Catalog);
        Assert.AreEqual(1, lifecycle.Inner.PauseCount);
        Assert.AreEqual(1, lifecycle.Inner.ResumeCount);
        Assert.IsFalse(lifecycle.CapturePaused);
        Assert.AreEqual(2, endpoint.VerificationRequests);
        await AssertOwnerReadyOperationCompletedAsync(fixture, selected.OperationId!.Value);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("select")]
    [DataRow("rollback")]
    [DataRow("disabled")]
    public async Task OpenNgcOwnerReadyInterruptedRecoveryResumesCapture(string action)
    {
        using var fixture = await PrepareOwnerReadyFixtureAsync(action);
        using var endpoint = new OwnerReadyInstallationEndpoint(fixture) { FailVerificationRequests = 2 };
        var owner = new OwnerBootstrapClient(new Uri("http://verification.invalid"), endpoint);
        var lifecycle = new OwnerReadyCaptureClient();
        var interrupted = await Assert.ThrowsExactlyAsync<InstallerException>(() =>
            OwnerReadyTransitionAsync(fixture, action, lifecycle, owner));
        StringAssert.Contains(interrupted.Message, "recovery is incomplete", StringComparison.Ordinal);
        var operation = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.IsTrue(operation!.MutationStarted);
        Assert.AreEqual(0, lifecycle.Inner.ResumeCount);
        Assert.IsTrue(lifecycle.CapturePaused);
        Assert.AreEqual(0, endpoint.FailVerificationRequests);

        var resumed = await OwnerReadyTransitionAsync(fixture, action, lifecycle, owner, resume: true);
        Assert.AreEqual(operation.OperationId, resumed.OperationId);
        Assert.AreEqual(action == "select" ? DeepSkyIdentity(fixture) : null, resumed.DeepSkyCatalog);
        Assert.AreEqual(fixture.Manifest.Catalog, resumed.Catalog);
        Assert.AreEqual(2, lifecycle.Inner.PauseCount);
        Assert.AreEqual(2, lifecycle.Inner.ResumeCount);
        Assert.IsFalse(lifecycle.CapturePaused);
        Assert.AreEqual(5, endpoint.VerificationRequests);
        await AssertOwnerReadyOperationCompletedAsync(fixture, operation.OperationId);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("agentId")]
    [DataRow("databaseSha256")]
    [DataRow("deepSkyCatalog")]
    [DataRow("ownerBootstrapState")]
    public async Task OpenNgcOwnerReadyStillRejectsInvalidInstallationIdentity(string field)
    {
        using var fixture = await PrepareOwnerReadyFixtureAsync("select");
        using var endpoint = new OwnerReadyInstallationEndpoint(fixture) { CorruptNextField = field };
        var owner = new OwnerBootstrapClient(new Uri("http://verification.invalid"), endpoint);
        var lifecycle = new OwnerReadyCaptureClient();
        var error = await Assert.ThrowsExactlyAsync<InstallerException>(() =>
            OwnerReadyTransitionAsync(fixture, "select", lifecycle, owner));
        StringAssert.Contains(error.Message, "original selections were restored", StringComparison.Ordinal);
        Assert.IsInstanceOfType<InstallerException>(error.InnerException);
        StringAssert.Contains(error.InnerException.Message, "identities that differ", StringComparison.Ordinal);
        Assert.AreEqual(1, lifecycle.Inner.ResumeCount);
        Assert.IsFalse(lifecycle.CapturePaused);
        var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        Assert.IsNull(manifest.DeepSkyCatalog);
        Assert.AreEqual(fixture.Manifest.Catalog, manifest.Catalog);
        var operation = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(InstallationStatus.Failed, operation!.Status);
        Assert.IsFalse(operation.MutationStarted);
    }

    private static async Task<LifecycleFixture> PrepareOwnerReadyFixtureAsync(string action)
    {
        var fixture = await DeepSkyFixtureAsync();
        if (action != "select")
            await DeepSkyTransitionAsync(fixture, DeepSkyIdentity(fixture), new FakeLifecycleClient(),
                new FakeOwnerClient(fixture.ApplicationIdentity));
        return fixture;
    }

    private static async Task<LifecycleResult> OwnerReadyTransitionAsync(
        LifecycleFixture fixture, string action, OwnerReadyCaptureClient lifecycle, IOwnerBootstrapClient owner, bool resume = false)
    {
        var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        var result = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None);
        var candidate = action == "select" ? DeepSkyIdentity(fixture) : null;
        var request = fixture.Request(action == "rollback" ? LifecycleOperationKind.CatalogRollback : LifecycleOperationKind.CatalogSelect) with
        {
            CatalogFamily = "deep-sky",
            CatalogVersion = action == "rollback" ? null : candidate?.PackageVersion ?? "disabled",
            Resume = resume
        };
        var operation = await CameraAgentLifecycleManager.BeginAsync(request, fixture.Paths, request.Operation!.Value,
            manifest, CancellationToken.None);
        // Snapshot admission is covered separately; exercise the shared transition and real verification protocol here.
        return await DeepSkyCatalogSelection.TransitionAsync(request, fixture.Paths, manifest, result,
            CameraAgentLifecycleManager.ComposeFrom(fixture.Paths, manifest, result), new DockerClient(fixture.Runner),
            _ => lifecycle, _ => owner, "lifecycle-token", "verification-token", candidate, operation, CancellationToken.None);
    }

    private static async Task AssertOwnerReadyOperationCompletedAsync(LifecycleFixture fixture, Guid operationId)
    {
        var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        var result = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None);
        CameraAgentLifecycleManager.EnsureCorrelated(fixture.Paths, fixture.InstanceId, manifest, result, allowTransactionalDrift: false);
        Assert.AreEqual("owner-password-change-required", result.OwnerBootstrapState,
            "Completed onboarding is an allowed runtime progression; the immutable installation result must stay unchanged.");
        var operation = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(operationId, operation!.OperationId);
        Assert.AreEqual(InstallationStatus.Completed, operation.Status);
        Assert.AreEqual(LifecycleOperationPhase.Completed, operation.Phase);
    }

    private sealed class OwnerReadyCaptureClient : ICameraAgentLifecycleClient
    {
        public FakeLifecycleClient Inner { get; } = new();
        public bool CapturePaused { get; private set; }

        public async Task<LifecycleContinuity> PauseAndDrainAsync(Guid operationId, string verificationToken, CancellationToken cancellationToken)
        {
            var drained = await Inner.PauseAndDrainAsync(operationId, verificationToken, cancellationToken);
            CapturePaused = true;
            return drained;
        }

        public async Task ResumeAsync(Guid operationId, string verificationToken, CancellationToken cancellationToken)
        {
            await Inner.ResumeAsync(operationId, verificationToken, cancellationToken);
            CapturePaused = false;
        }

        public Task<LifecycleContinuity> ConfirmDrainedAsync(string verificationToken, CancellationToken cancellationToken)
            => Inner.ConfirmDrainedAsync(verificationToken, cancellationToken);
        public Task<LifecycleContinuity> ReadContinuityAsync(string verificationToken, CancellationToken cancellationToken)
            => Inner.ReadContinuityAsync(verificationToken, cancellationToken);
        public Task<LifecycleResumeReceipt> ResumeRecoveryAsync(Guid operationId, Guid commandId, long expectedVersion,
            string verificationToken, CancellationToken cancellationToken)
            => Inner.ResumeRecoveryAsync(operationId, commandId, expectedVersion, verificationToken, cancellationToken);
    }

    private sealed class OwnerReadyInstallationEndpoint(LifecycleFixture fixture) : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions VerificationJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        public int FailVerificationRequests { get; set; }
        public string? CorruptNextField { get; set; }
        public int VerificationRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri!.AbsolutePath == "/alive") return Reply(new { });
            if (request.RequestUri.AbsolutePath == "/health")
            {
                var contract = ProductionCatalog.Get(fixture.Manifest.Catalog.CatalogId);
                return Reply(new
                {
                    checks = new[] { new { name = "catalog", data = new
                    {
                        contract.CatalogId, contract.CatalogVersion, contract.SchemaVersion,
                        contract.PreprocessingVersion, contract.RowCount
                    } } }
                });
            }
            Assert.AreEqual("/api/internal/owner-bootstrap/installation-verification", request.RequestUri.AbsolutePath);
            Assert.AreEqual("verification-token", request.Headers.GetValues("X-HVO-Installation-Token").Single());
            VerificationRequests++;
            if (FailVerificationRequests > 0)
            {
                FailVerificationRequests--;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            var enabledPath = Path.Combine(fixture.Paths.ConfigRoot, "secrets", "DeepSkyCatalog__Enabled");
            var deepSky = File.Exists(enabledPath) && await File.ReadAllTextAsync(enabledPath, cancellationToken) == "true"
                ? DeepSkyIdentity(fixture) : null;
            var manifest = fixture.Manifest;
            var body = JsonSerializer.SerializeToNode(new
            {
                agentId = fixture.ApplicationIdentity.ToString("D"),
                manifest.OwnerEmail,
                ownerBootstrapState = "owner-ready",
                manifest.ConfigurationSha256,
                manifest.RigProfileSha256,
                manifest.ScheduleSha256,
                manifest.DeploymentLocationId,
                manifest.DeploymentLocationVersion,
                manifest.DeploymentLocationSha256,
                rawIngressRoot = "/app/data/raw",
                replayProfile = manifest.ReplayProfile.ToString(),
                manifest.Catalog.CatalogId,
                manifest.Catalog.PackageVersion,
                manifest.Catalog.SchemaVersion,
                manifest.Catalog.PreprocessingVersion,
                manifest.Catalog.DatabaseSha256,
                manifest.Catalog.DatabaseLength,
                manifest.Catalog.RowCount,
                deepSkyCatalog = deepSky is null ? null : new
                {
                    deepSky.CatalogId,
                    deepSky.PackageVersion,
                    deepSky.SchemaVersion,
                    deepSky.PreprocessingVersion,
                    deepSky.DatabaseSha256
                }
            }, VerificationJsonOptions)!.AsObject();
            if (CorruptNextField is { } field)
            {
                CorruptNextField = null;
                if (field == "deepSkyCatalog") body[field]!["databaseSha256"] = new string('f', 64);
                else body[field] = field == "ownerBootstrapState" ? "owner-disabled" : new string('f', 64);
            }
            return Reply(body);
        }

        private static HttpResponseMessage Reply(object body) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(body)) };
    }
}
