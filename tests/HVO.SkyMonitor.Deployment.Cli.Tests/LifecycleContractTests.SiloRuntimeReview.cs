using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The #1966 review's lifecycle cases on a real installed Silo-layout instance's records: a completed re-run proves the
/// running CameraAgent and the Silo endpoint it is configured with, not only Silo or a cached result; the Silo commands
/// correlate the instance's records before any authenticated call; and an interrupted bootstrap resumes from an owned,
/// durable intent and credential record instead of re-creating what a lost acknowledgement already created.
/// </summary>
public sealed partial class LifecycleContractTests
{
    private const string IntentFile = "intent.json";
    private const string CredentialFile = "writer-credential.json";

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("complete", null)]
    [DataRow("application-absent", "CameraAgent")]
    [DataRow("application-stopped", "CameraAgent")]
    [DataRow("application-starting", "CameraAgent")]
    [DataRow("application-unhealthy", "CameraAgent")]
    [DataRow("application-wrong-image", "CameraAgent")]
    [DataRow("credential-after-start", "restarted")]
    [DataRow("probe-refused", "writer-cannot-read-bucket")]
    public async Task SiloReRun_VerifiesTheRunningCameraAgentAndItsConfiguredSiloEndpoint(string change, string? refusal)
    {
        using var fixture = await SiloFixtureAsync(InstanceLifecycleCondition.Installed);
        using var harness = new SiloHarness(fixture.Paths, fixture.InstanceId).Defaults();
        harness.SiloImage = harness.Identity.ImageId;
        harness.CameraAgentImageId = fixture.Manifest.Image.ImageId;
        switch (change)
        {
            case "application-absent": harness.CameraAgentExists = false; harness.NetworkMembers.Remove(harness.CameraAgentId); break;
            case "application-stopped": harness.CameraAgentRunning = false; harness.NetworkMembers.Remove(harness.CameraAgentId); break;
            case "application-starting": harness.CameraAgentHealth = "starting"; break;
            case "application-unhealthy": harness.CameraAgentHealth = "unhealthy"; break;
            case "application-wrong-image": harness.CameraAgentImageId = "sha256:" + new string('e', 64); break;
            case "credential-after-start": harness.CameraAgentStarted = DateTimeOffset.UtcNow.AddMinutes(-10); break;
            case "probe-refused": harness.ProbeFails = true; break;
        }
        var manifest = fixture.Manifest with { Silo = harness.Identity };
        var result = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None, allowSiloLayout: true);
        var docker = harness.Docker(out var runner);

        var rerun = SiloInstallation.ReRunAsync(docker, fixture.Paths, fixture.InstanceId, sameInputs: true, manifest, result, [], CancellationToken.None);

        if (refusal is null)
        {
            var completed = await rerun;
            Assert.IsNotNull(completed.Plan);
            // The binding is proved from CameraAgent's own network namespace, through the endpoint it is configured with,
            // never from inside Silo over its loopback.
            var probe = runner.Calls.Single(call => call.Input?.Contains("writer/", StringComparison.Ordinal) == true);
            CollectionAssert.Contains(probe.Arguments, $"container:{harness.CameraAgentId}");
            Assert.IsFalse(probe.Arguments.Contains("exec"), string.Join(' ', probe.Arguments));
            // The endpoint and credential are read from CameraAgent's own live settings mount, not sent in or fixed.
            CollectionAssert.Contains(probe.Arguments, "--volumes-from");
            StringAssert.Contains(probe.Input, "settings='/run/hvo-secrets'", StringComparison.Ordinal);
            Assert.IsFalse(probe.Input!.Contains("127.0.0.1", StringComparison.Ordinal));
            Assert.IsFalse(probe.Input.Contains(SiloHarness.Secret, StringComparison.Ordinal));
            Assert.IsFalse(probe.Arguments.Any(argument => argument.Contains(SiloHarness.Secret, StringComparison.Ordinal)), "No secret is an argument.");
        }
        else
        {
            var refused = await Assert.ThrowsAsync<InstallerException>(() => rerun);
            Assert.IsTrue(refused.IsRefusal, refused.Message);
            StringAssert.Contains(refused.Message, refusal, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("sibling-result", false)]
    [DataRow("redirected-endpoint", false)]
    [DataRow("correlated", true)]
    public async Task SiloCommands_CorrelateTheInstanceRecordsBeforeAnyDockerOrAuthenticatedCall(string change, bool reachesDocker)
    {
        using var fixture = await SiloFixtureAsync(InstanceLifecycleCondition.Installed);
        using var sibling = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var result = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None, allowSiloLayout: true);
        if (change == "sibling-result")
            File.Copy(sibling.Paths.ResultPath, fixture.Paths.ResultPath, overwrite: true);
        if (change == "redirected-endpoint")
            await File.WriteAllTextAsync(fixture.Paths.ResultPath, JsonSerializer.Serialize(
                result with { Url = new Uri($"http://127.0.0.1:{result.Url.Port + 1}/") }, DeploymentJsonContext.Default.InstallationResult));
        var dockerCreated = false;
        var clientCreated = false;
        var originalDocker = SiloCommands.DockerFactory;
        var originalClient = SiloCommands.ClientFactory;
        SiloCommands.DockerFactory = () =>
        {
            dockerCreated = true;
            return new DockerClient(new ScriptedDocker(static (_, _) => null));
        };
        SiloCommands.ClientFactory = uri =>
        {
            clientCreated = true;
            return originalClient(uri);
        };
        try
        {
            var request = new SiloCommandRequest(SiloCommandKind.Verify, fixture.InstanceId, fixture.Paths.ProductRoot, Json: true)
            {
                AllowTestProductRoot = true
            };

            var failure = await Assert.ThrowsAsync<InstallerException>(() => SiloCommands.ExecuteAsync(request, CancellationToken.None));

            Assert.AreEqual(reachesDocker, dockerCreated, failure.Message);
            Assert.IsFalse(clientCreated, "No authenticated CameraAgent call is made by these cases.");
            if (!reachesDocker)
            {
                Assert.IsTrue(failure.IsRefusal, failure.Message);
                StringAssert.Contains(failure.Message, "correlate", StringComparison.Ordinal);
            }
        }
        finally
        {
            SiloCommands.DockerFactory = originalDocker;
            SiloCommands.ClientFactory = originalClient;
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("fresh")]
    [DataRow("crash-between-setting-files")]
    [DataRow("lost-bucket-acknowledgement")]
    [DataRow("foreign-intent")]
    [DataRow("setting-file-without-record")]
    [DataRow("setting-file-differs-from-record")]
    [DataRow("fresh-store-not-empty")]
    public async Task SiloBootstrap_ResumesFromItsOwnDurableIntentAndCredentialRecordOnly(string change)
    {
        using var fixture = await SiloFixtureAsync(InstanceLifecycleCondition.Installed);
        using var harness = new SiloHarness(fixture.Paths, fixture.InstanceId).Defaults();
        harness.CameraAgentExists = false;
        harness.NetworkMembers.Remove(harness.CameraAgentId);
        var secrets = Path.Combine(fixture.Paths.ConfigRoot, "secrets");
        var accessPath = Path.Combine(secrets, SiloRuntimeProfile.WriterAccessKeySetting);
        var secretPath = Path.Combine(secrets, SiloRuntimeProfile.WriterSecretKeySetting);
        File.Delete(accessPath);
        File.Delete(secretPath);
        var bootstrap = SiloRuntimeProfile.BootstrapDirectory(fixture.Paths);
        SafeFileSystem.CreateOwnerDirectory(bootstrap);
        const string RecordedAccess = "AKIARECORDEDWRITER01";
        const string RecordedSecret = "recordedWriterSecret0123456789ABCDEFGHI";
        var bucketExists = change is "crash-between-setting-files" or "lost-bucket-acknowledgement";
        if (change is not ("fresh" or "setting-file-without-record" or "fresh-store-not-empty"))
            SafeFileSystem.WriteTextAtomic(Path.Combine(bootstrap, IntentFile), Intent(harness, change == "foreign-intent" ? Guid.NewGuid() : fixture.InstanceId));
        if (change is "crash-between-setting-files" or "setting-file-differs-from-record")
            SafeFileSystem.WriteTextAtomic(Path.Combine(bootstrap, CredentialFile), Credential(harness, RecordedAccess, RecordedSecret));
        if (change is "crash-between-setting-files" or "setting-file-without-record")
            SafeFileSystem.WriteTextAtomic(accessPath, RecordedAccess);
        if (change == "setting-file-differs-from-record")
            SafeFileSystem.WriteTextAtomic(accessPath, "AKIASOMEOTHERWRITER1");
        var intentPresentAtCreate = false;
        var scripts = new List<string>();
        harness.Store = script =>
        {
            scripts.Add(script);
            if (script.Contains("create_only", StringComparison.Ordinal))
                return ScriptedDocker.Ok(Bound(harness, bootstrap));
            if (script.Contains("hvo-bootstrap: inventory", StringComparison.Ordinal))
            {
                // A store holding this instance's empty versioned bucket (after a lost acknowledgement) or nothing.
                var holdsBucket = bucketExists || change == "fresh-store-not-empty";
                static string B64(string text) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
                var buckets = holdsBucket ? $"{{\"status\":\"success\",\"type\":\"folder\",\"key\":\"{harness.Bucket}/\"}}" : string.Empty;
                var versioning = holdsBucket ? "{\"status\":\"success\",\"versioning\":{\"status\":\"Enabled\"}}" : string.Empty;
                const string Keys = "{\"status\":\"success\",\"user\":\"hvoroot\",\"stsKeys\":null,\"svcaccs\":null}";
                return ScriptedDocker.Ok($"{{\"outcome\":\"inventory\",\"buckets\":\"{B64(buckets)}\",\"objects\":\"\",\"versioning\":\"{B64(versioning)}\",\"keys\":\"{B64(Keys)}\"}}\n");
            }
            intentPresentAtCreate = File.Exists(Path.Combine(bootstrap, IntentFile));
            return ScriptedDocker.Ok($"{{\"outcome\":\"created\",\"versioning\":\"Enabled\",\"accessKey\":\"{SiloHarness.Access}\",\"secretKey\":\"{SiloHarness.Secret}\"}}\n");
        };
        harness.SiloImage = harness.Identity.ImageId;
        var docker = harness.Docker(out var runner);
        var compose = new ComposeFiles(Path.Combine(fixture.Paths.DeploymentStateRoot, "compose.yaml"),
            Path.Combine(fixture.Paths.DeploymentStateRoot, "compose.env"), harness.Project, harness.Project, string.Empty, string.Empty,
            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, null);
        var image = new SiloImageIdentity(SiloImagePin.Reference, harness.Identity.ImageId, "amd64", SiloHarness.Amd64.ManifestDigest,
            SiloImagePin.Release, SiloImagePin.SourceRevision);

        var bootstrapping = SiloInstallation.BootstrapAsync(docker, compose, fixture.Paths, fixture.InstanceId, Guid.NewGuid(), image,
            (harness.Identity.DataRootFileSystem, harness.Identity.DataRootDevice), CancellationToken.None);

        var creations = () => scripts.Count(static script => script.Contains("mcli mb", StringComparison.Ordinal) ||
                                                              script.Contains("accesskey create", StringComparison.Ordinal));
        switch (change)
        {
            case "fresh":
                {
                    var bound = await bootstrapping;
                    Assert.IsTrue(intentPresentAtCreate, "The intent is durable before the store is changed.");
                    Assert.AreEqual(SiloHarness.Access, bound.WriterAccessKeyId);
                    AssertCredential(bootstrap, accessPath, secretPath, SiloHarness.Access, SiloHarness.Secret);
                    break;
                }
            case "crash-between-setting-files":
                {
                    var bound = await bootstrapping;
                    Assert.AreEqual(0, creations(), "A recorded credential is never created again.");
                    Assert.AreEqual(RecordedAccess, bound.WriterAccessKeyId);
                    AssertCredential(bootstrap, accessPath, secretPath, RecordedAccess, RecordedSecret);
                    break;
                }
            case "lost-bucket-acknowledgement":
                {
                    var bound = await bootstrapping;
                    Assert.IsTrue(scripts.Any(static script => script.Contains("hvo-bootstrap: apply", StringComparison.Ordinal) &&
                                                               script.Contains("case 'none' in", StringComparison.Ordinal)),
                        "A bucket this instance's intent created is adopted, not refused as foreign.");
                    Assert.AreEqual(SiloHarness.Access, bound.WriterAccessKeyId);
                    AssertCredential(bootstrap, accessPath, secretPath, SiloHarness.Access, SiloHarness.Secret);
                    break;
                }
            case "fresh-store-not-empty":
                {
                    // A store that is not empty on a fresh install is not this instance's, so no adoptable intent may exist.
                    var refused = await Assert.ThrowsAsync<InstallerException>(() => bootstrapping);
                    Assert.IsTrue(refused.IsRefusal, refused.Message);
                    Assert.IsFalse(File.Exists(Path.Combine(bootstrap, IntentFile)), "No intent is recorded for a store this run did not prove empty.");
                    Assert.AreEqual(0, creations());
                    break;
                }
            default:
                {
                    var refused = await Assert.ThrowsAsync<InstallerException>(() => bootstrapping);
                    Assert.IsTrue(refused.IsRefusal, refused.Message);
                    Assert.AreEqual(0, creations(), "An ambiguous bootstrap changes nothing in the store.");
                    Assert.IsFalse(runner.Called("exec") && scripts.Count != 0, "No store script runs.");
                    break;
                }
        }
    }

    private static void AssertCredential(string bootstrap, string accessPath, string secretPath, string access, string secret)
    {
        Assert.AreEqual(access, File.ReadAllText(accessPath));
        Assert.AreEqual(secret, File.ReadAllText(secretPath));
        var record = JsonNode.Parse(File.ReadAllText(Path.Combine(bootstrap, CredentialFile)))!;
        Assert.AreEqual(access, (string?)record["accessKey"]);
        Assert.AreEqual(secret, (string?)record["secretKey"]);
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(bootstrap, CredentialFile)));
    }

    private static string Intent(SiloHarness harness, Guid instanceId) => new JsonObject
    {
        ["format"] = "hvo.cameraagent.silo.bootstrap-intent/1",
        ["instanceId"] = instanceId.ToString("D"),
        ["storeId"] = SiloRuntimeProfile.StoreIdFor(instanceId),
        ["bucket"] = SiloRuntimeProfile.BucketFor(instanceId),
        ["dataRoot"] = harness.DataRoot,
        ["dataRootDevice"] = harness.Identity.DataRootDevice,
        ["writerGeneration"] = SiloRuntimeProfile.WriterGeneration,
        ["storePreflight"] = "empty"
    }.ToJsonString();

    private static string Credential(SiloHarness harness, string access, string secret) => new JsonObject
    {
        ["format"] = "hvo.cameraagent.silo.writer-credential/1",
        ["instanceId"] = harness.InstanceId.ToString("D"),
        ["bucket"] = harness.Bucket,
        ["writerGeneration"] = SiloRuntimeProfile.WriterGeneration,
        ["accessKey"] = access,
        ["secretKey"] = secret
    }.ToJsonString();

    /// <summary>The binding script's answer for the marker and fence the bootstrap saved before it ran.</summary>
    private static string Bound(SiloHarness harness, string bootstrap)
    {
        var marker = File.ReadAllBytes(Path.Combine(bootstrap, "instance.v1.json"));
        var fence = File.ReadAllBytes(Path.Combine(bootstrap, "fence.json"));
        return $"{{\"outcome\":\"bound\",\"markerVersionId\":\"{harness.Identity.MarkerVersionId}\",\"markerSha256\":\"{SiloHarness.Sha(marker)}\"," +
               $"\"fenceVersionId\":\"{harness.Identity.FenceVersionId}\",\"fenceSha256\":\"{SiloHarness.Sha(fence)}\"}}\n";
    }
}
