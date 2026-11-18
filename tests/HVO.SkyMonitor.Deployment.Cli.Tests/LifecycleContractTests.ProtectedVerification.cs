using System.Net;
using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(LifecycleOperationKind.Upgrade, "mismatch")]
    [DataRow(LifecycleOperationKind.Upgrade, "timeout")]
    [DataRow(LifecycleOperationKind.Upgrade, "cancel")]
    [DataRow(LifecycleOperationKind.Upgrade, "failure")]
    [DataRow(LifecycleOperationKind.Upgrade, "plan")]
    [DataRow(LifecycleOperationKind.Rollback, "mismatch")]
    [DataRow(LifecycleOperationKind.Rollback, "timeout")]
    [DataRow(LifecycleOperationKind.Rollback, "cancel")]
    [DataRow(LifecycleOperationKind.Rollback, "failure")]
    [DataRow(LifecycleOperationKind.Rollback, "plan")]
    [DataRow(LifecycleOperationKind.Backup, "mismatch")]
    [DataRow(LifecycleOperationKind.Backup, "timeout")]
    [DataRow(LifecycleOperationKind.Backup, "cancel")]
    [DataRow(LifecycleOperationKind.Backup, "failure")]
    [DataRow(LifecycleOperationKind.Backup, "plan")]
    public async Task ProtectedVerificationRefusesBeforeLifecycleMutation(LifecycleOperationKind kind, string fault)
    {
        using var fixture = await BackupFixtureAsync();
        var reference = $"ghcr.io/example/cameraagent@sha256:{new string('4', 64)}";
        var imageId = $"sha256:{new string('5', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            reference, imageId, fixture.Uid, fixture.Gid);
        var upgrade = fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = reference,
            NoDownload = true,
            MigrationBackwardCompatible = true
        };
        if (kind == LifecycleOperationKind.Rollback)
            _ = await CameraAgentLifecycleManager.ExecuteAsync(upgrade, fixture.Runner, _ => new FakeLifecycleClient(),
                _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None);
        var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        var request = (kind == LifecycleOperationKind.Upgrade ? upgrade : fixture.Request(kind)) with { DryRun = fault == "plan" };
        var lifecycle = new FakeLifecycleClient();
        var stopBefore = fixture.Runner.ComposeStopCount;
        var upBefore = fixture.Runner.ComposeUpCount;
        var manifestBefore = await File.ReadAllBytesAsync(fixture.Paths.ManifestPath);
        var resultBefore = await File.ReadAllBytesAsync(fixture.Paths.ResultPath);
        var composePath = Path.Combine(fixture.Paths.ConfigRoot, "compose", "compose.yml");
        var composeBefore = await File.ReadAllBytesAsync(composePath);
        using var caller = new CancellationTokenSource();
        using var handler = new ScriptedHandler(async (_, _, cancellationToken) =>
        {
            if (fault == "cancel") await caller.CancelAsync();
            if (fault is "timeout" or "cancel") await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (fault == "failure") return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            { Content = new StringContent("verification-token") };
            Assert.AreEqual("mismatch", fault, "A plan must never request protected installation state.");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    agentId = "changed-current-agent",
                    ownerEmail = manifest!.OwnerEmail,
                    ownerBootstrapState = "owner-password-change-required",
                    configurationSha256 = manifest.ConfigurationSha256,
                    rigProfileSha256 = manifest.RigProfileSha256,
                    scheduleSha256 = manifest.ScheduleSha256,
                    deploymentLocationId = manifest.DeploymentLocationId,
                    deploymentLocationVersion = manifest.DeploymentLocationVersion,
                    deploymentLocationSha256 = manifest.DeploymentLocationSha256,
                    rawIngressRoot = "/app/data/raw",
                    replayProfile = manifest.ReplayProfile.ToString(),
                    catalogId = manifest.Catalog.CatalogId,
                    packageVersion = manifest.Catalog.PackageVersion,
                    schemaVersion = manifest.Catalog.SchemaVersion,
                    preprocessingVersion = manifest.Catalog.PreprocessingVersion,
                    databaseSha256 = manifest.Catalog.DatabaseSha256,
                    databaseLength = manifest.Catalog.DatabaseLength,
                    rowCount = manifest.Catalog.RowCount
                }))
            };
        });
        var owner = new OwnerBootstrapClient(new Uri("http://verification.invalid"), handler, TimeSpan.FromMilliseconds(100));
        Task<LifecycleResult> RunAsync() => CameraAgentLifecycleManager.ExecuteAsync(request, fixture.Runner,
            _ => lifecycle, _ => owner, fixture.Uid, fixture.Gid, caller.Token, error: TextWriter.Null);
        if (fault == "plan") Assert.AreEqual("planned", (await RunAsync()).Outcome);
        else if (fault == "cancel") await Assert.ThrowsAsync<OperationCanceledException>(RunAsync);
        else if (fault == "failure")
        {
            var exception = await Assert.ThrowsExactlyAsync<HttpRequestException>(RunAsync);
            Assert.IsFalse(exception.ToString().Contains("verification-token", StringComparison.Ordinal));
        }
        else
        {
            var exception = await Assert.ThrowsExactlyAsync<InstallerException>(RunAsync);
            Assert.IsFalse(exception.ToString().Contains("verification-token", StringComparison.Ordinal));
            if (fault == "timeout") Assert.AreEqual("CameraAgent installation verification request timed out.", exception.Message);
            if (fault == "mismatch") StringAssert.Contains(exception.Message, "identities that differ", StringComparison.Ordinal);
        }
        Assert.AreEqual(fault == "plan" ? 0 : 1, handler.RequestCount);
        Assert.AreEqual(0, lifecycle.PauseCount);
        Assert.AreEqual(stopBefore, fixture.Runner.ComposeStopCount);
        Assert.AreEqual(upBefore, fixture.Runner.ComposeUpCount);
        CollectionAssert.AreEqual(manifestBefore, await File.ReadAllBytesAsync(fixture.Paths.ManifestPath));
        CollectionAssert.AreEqual(resultBefore, await File.ReadAllBytesAsync(fixture.Paths.ResultPath));
        CollectionAssert.AreEqual(composeBefore, await File.ReadAllBytesAsync(composePath));
        if (fault != "plan")
        {
            var operation = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
            Assert.IsFalse(operation!.MutationStarted);
            Assert.IsNull(operation.BackupManifestSha256);
        }
    }
}
