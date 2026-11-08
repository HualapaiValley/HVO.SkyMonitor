using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(LifecycleOperationKind.Upgrade)]
    [DataRow(LifecycleOperationKind.Reinstall)]
    public async Task ExplicitIllumination_LifecyclePreservesCanonicalConfigurationAndVerification(LifecycleOperationKind kind)
    {
        using var fixture = await LifecycleFixture.CreateAsync(kind == LifecycleOperationKind.Reinstall
            ? InstanceLifecycleCondition.Uninstalled : InstanceLifecycleCondition.Installed);
        var generated = CameraConfiguration.Generate(IlluminationModeTests.Request("ControlledNight"), fixture.ApplicationIdentity);
        var path = Path.Combine(fixture.Paths.ConfigRoot, "camera-module.json");
        // Formatting changes never constitute canonical identity drift.
        using var document = JsonDocument.Parse(generated.Json);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(document.RootElement));
        var manifest = fixture.Manifest with
        {
            ConfigurationSha256 = generated.Sha256,
            RigProfileSha256 = generated.RigProfileSha256,
            ScheduleSha256 = generated.ScheduleSha256,
            RigProfileName = generated.RigProfileName,
            RigProfileVersion = generated.RigProfileVersion,
            ScheduleSchemaVersion = generated.ScheduleSchemaVersion,
            ScheduleState = generated.ScheduleState
        };
        await SafeFileSystem.WriteJsonAtomicAsync(fixture.Paths.ManifestPath, manifest,
            DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
        var installed = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None);
        await SafeFileSystem.WriteJsonAtomicAsync(fixture.Paths.ResultPath, installed with
        {
            ConfigurationSha256 = generated.Sha256,
            RigProfileSha256 = generated.RigProfileSha256,
            ScheduleSha256 = generated.ScheduleSha256,
            RigProfileName = generated.RigProfileName,
            RigProfileVersion = generated.RigProfileVersion,
            ScheduleSchemaVersion = generated.ScheduleSchemaVersion,
            ScheduleState = generated.ScheduleState
        }, DeploymentJsonContext.Default.InstallationResult, CancellationToken.None);

        var candidate = kind == LifecycleOperationKind.Upgrade ? $"sha256:{new string('5', 64)}" : manifest.Image.ImageId;
        fixture.Runner.ConfigureRuntime(fixture.Paths, manifest.Image.ImmutableReference, manifest.Image.ImageId,
            candidate, candidate, fixture.Uid, fixture.Gid, exists: kind != LifecycleOperationKind.Reinstall);
        var verifications = 0;
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity)
        {
            InspectVerification = expectation =>
            {
                using var bound = JsonDocument.Parse(File.ReadAllText(path));
                Assert.AreEqual(IlluminationModeTests.CanonicalSha256(bound.RootElement), expectation.ConfigurationSha256);
                Assert.AreEqual(generated.RigProfileSha256, expectation.RigProfileSha256);
                Assert.AreEqual(generated.ScheduleSha256, expectation.ScheduleSha256);
                verifications++;
            }
        };
        var request = fixture.Request(kind) with
        {
            ImageReference = kind == LifecycleOperationKind.Upgrade ? candidate : null,
            NoDownload = kind == LifecycleOperationKind.Upgrade
        };
        var result = await CameraAgentLifecycleManager.ExecuteAsync(request, fixture.Runner,
            _ => new FakeLifecycleClient(), _ => owner, fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", result.Outcome);
        Assert.IsGreaterThan(0, verifications);
        var retained = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        var retainedResult = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None);
        Assert.AreEqual(generated.Sha256, retained.ConfigurationSha256);
        Assert.AreEqual(retained.ConfigurationSha256, retainedResult.ConfigurationSha256);
        Assert.AreEqual(generated.RigProfileSha256, retained.RigProfileSha256);
        Assert.AreEqual(generated.ScheduleSha256, retained.ScheduleSha256);
        using var preserved = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.AreEqual("ControlledNight", preserved.RootElement.GetProperty("module").GetProperty("options")
            .GetProperty("illuminationMode").GetString());
    }
}
