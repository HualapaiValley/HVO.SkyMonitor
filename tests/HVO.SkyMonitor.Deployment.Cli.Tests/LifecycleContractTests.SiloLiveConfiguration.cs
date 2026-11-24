using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The #1966 second review's rough boundaries on a real installed Silo-layout instance's records. A verification proves
/// the configuration the running CameraAgent consumes, its live settings mount, the endpoint and writer credential read
/// from it, and no environment override, rather than the host files or a fixed name alone. It proves that the running
/// process started after the exact active credential pair was published (renamed into place), not after a staged
/// temporary file was written.
/// </summary>
public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("operator-healthy", null)]
    [DataRow("settings-mount-missing", "CameraAgent")]
    [DataRow("settings-mount-from-elsewhere", "CameraAgent")]
    [DataRow("settings-mount-writable", "CameraAgent")]
    [DataRow("endpoint-overridden-by-environment", "CameraAgent")]
    [DataRow("live-endpoint-elsewhere", "endpoint")]
    [DataRow("live-credential-differs", "credential")]
    public async Task SiloVerification_ProvesTheConfigurationTheRunningCameraAgentConsumes(string change, string? refusal)
    {
        using var fixture = await SiloFixtureAsync(InstanceLifecycleCondition.Installed);
        using var harness = new SiloHarness(fixture.Paths, fixture.InstanceId).Defaults();
        harness.SiloImage = harness.Identity.ImageId;
        harness.CameraAgentImageId = fixture.Manifest.Image.ImageId;
        switch (change)
        {
            case "settings-mount-missing": harness.SecretsMountPresent = false; break;
            case "settings-mount-from-elsewhere":
                // A real folder elsewhere, as Docker binds only one that exists: apart from the data root, but not the settings.
                harness.SecretsMountSource = Path.Combine(fixture.Root, "elsewhere", "secrets");
                Directory.CreateDirectory(harness.SecretsMountSource);
                break;
            case "settings-mount-writable": harness.SecretsMountReadOnly = false; break;
            case "endpoint-overridden-by-environment": harness.CameraAgentEnvironment.Add("CameraAgent__Silo__Endpoint=http://other-silo:9000"); break;
            case "live-endpoint-elsewhere": harness.LiveEndpoint = "http://other-silo:9000"; break;
            case "live-credential-differs": harness.LiveSecret = "someOtherWriterSecret0123456789ABCDEFGHI"; break;
        }
        var manifest = fixture.Manifest with { Silo = harness.Identity };
        var result = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None, allowSiloLayout: true);
        var docker = harness.Docker(out var runner);

        var rerun = SiloInstallation.ReRunAsync(docker, fixture.Paths, fixture.InstanceId, sameInputs: true, manifest, result, [], CancellationToken.None);

        if (refusal is null)
        {
            await rerun;
            var probe = runner.Calls.Single(call => call.Input?.Contains("writer/", StringComparison.Ordinal) == true);
            CollectionAssert.Contains(probe.Arguments, $"{harness.CameraAgentId}:ro", "The probe reads CameraAgent's own live mounts.");
            Assert.IsFalse(probe.Input!.Contains(SiloHarness.Secret, StringComparison.Ordinal), "The probe reads the credential CameraAgent holds, not one it is sent.");
        }
        else
        {
            var refused = await Assert.ThrowsAsync<InstallerException>(() => rerun);
            Assert.IsTrue(refused.IsRefusal, refused.Message);
            StringAssert.Contains(refused.Message, refusal, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("restart-after-both-committed", false)]
    [DataRow("restart-before-rename", true)]
    [DataRow("split-files", true)]
    public async Task SiloVerification_RequiresTheProcessToStartAfterTheActiveCredentialPairWasPublished(string order, bool refused)
    {
        using var fixture = await SiloFixtureAsync(InstanceLifecycleCondition.Installed);
        using var harness = new SiloHarness(fixture.Paths, fixture.InstanceId).Defaults();
        harness.SiloImage = harness.Identity.ImageId;
        harness.CameraAgentImageId = fixture.Manifest.Image.ImageId;
        var secrets = Path.Combine(fixture.Paths.ConfigRoot, "secrets");
        string Staged(string setting) => Path.Combine(secrets, $".{setting}.new");
        void Stage(string setting, string value)
        {
            File.WriteAllText(Staged(setting), value);
            File.SetUnixFileMode(Staged(setting), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        void Publish(string setting) => File.Move(Staged(setting), Path.Combine(secrets, setting), overwrite: true);
        void Start()
        {
            Thread.Sleep(30);
            harness.CameraAgentStarted = DateTimeOffset.UtcNow;
            Thread.Sleep(30);
        }
        Stage(SiloRuntimeProfile.WriterAccessKeySetting, SiloHarness.Access);
        Stage(SiloRuntimeProfile.WriterSecretKeySetting, SiloHarness.Secret);
        switch (order)
        {
            case "restart-after-both-committed":
                Publish(SiloRuntimeProfile.WriterAccessKeySetting);
                Publish(SiloRuntimeProfile.WriterSecretKeySetting);
                Start();
                break;
            case "restart-before-rename":
                Start();
                Publish(SiloRuntimeProfile.WriterAccessKeySetting);
                Publish(SiloRuntimeProfile.WriterSecretKeySetting);
                break;
            case "split-files":
                Publish(SiloRuntimeProfile.WriterAccessKeySetting);
                Start();
                Publish(SiloRuntimeProfile.WriterSecretKeySetting);
                break;
        }
        var manifest = fixture.Manifest with { Silo = harness.Identity };
        var result = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None, allowSiloLayout: true);
        var docker = harness.Docker(out _);

        var rerun = SiloInstallation.ReRunAsync(docker, fixture.Paths, fixture.InstanceId, sameInputs: true, manifest, result, [], CancellationToken.None);

        if (!refused)
        {
            await rerun;
        }
        else
        {
            var refusal = await Assert.ThrowsAsync<InstallerException>(() => rerun);
            StringAssert.Contains(refusal.Message, "has not restarted", StringComparison.Ordinal);
        }
    }
}
