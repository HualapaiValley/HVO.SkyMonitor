using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// <c>cameraagent first-resume</c> through its command (#1966 review: Sol F1 and F4, Fable 2), on an installed Silo-layout
/// instance's records on disk, a scripted Docker CLI and a scripted CameraAgent. Admission opens only on an installation
/// durably completed, under the installer's locks, whose configured bucket and binding are the recorded ones, whose runtime
/// is ready, and whose installer pause is still the latest capture command; every refusal sends no resume.
/// </summary>
public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("accepted")]
    [DataRow("refused-not-ready")]
    [DataRow("refused-superseded")]
    [DataRow("already-resumed")]
    [DataRow("wrong-bucket")]
    [DataRow("wrong-binding")]
    [DataRow("installation-interrupted-after-its-result")]
    [DataRow("journal-of-another-installation")]
    [DataRow("bucket-corrected-after-start")]
    [DataRow("endpoint-corrected-after-start")]
    [DataRow("another-container-mounts-a-link-to-a-path-inside-the-data-root")]
    [DataRow("another-container-mounts-a-bind-volume-of-the-data-root")]
    [DataRow("another-container-mounts-a-tmpfs-typed-bind-volume-of-the-data-root")]
    public async Task FirstResume_OpensAdmissionOnlyOnACompletedReadyInstanceWhosePauseIsStillInForce(string state)
    {
        using var fixture = await SiloFixtureAsync(InstanceLifecycleCondition.Installed);
        using var harness = new SiloHarness(fixture.Paths, fixture.InstanceId).Defaults();
        harness.SiloImage = harness.Identity.ImageId;
        harness.CameraAgentImageId = fixture.Manifest.Image.ImageId;
        SafeFileSystem.CreateOwnerDirectory(fixture.Paths.OperationsRoot);
        if (state == "refused-not-ready")
        {
            // An archive whose authority file is no longer the one installed is refused, so the runtime is not ready.
            var archive = Path.Combine(fixture.Paths.ConfigRoot, "silo-archive");
            SafeFileSystem.CreateOwnerDirectory(archive);
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var authority = new CertificateRequest("CN=Replaced authority", key, HashAlgorithmName.SHA256)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            SafeFileSystem.WriteTextAtomic(Path.Combine(archive, "authority.pem"), authority.ExportCertificatePem());
            harness.Identity = harness.Identity with
            {
                Archive = new SiloArchiveIdentity("https://archive.example:9443", "hvo-archive", new string('0', 64),
                    "AKIAARCHIVE000000001", new string('1', 64), DateTimeOffset.UtcNow)
            };
        }
        await WriteSiloManifestAsync(fixture, harness.Identity);
        await WriteInstallationStateAsync(fixture,
            state == "installation-interrupted-after-its-result" ? InstallationStatus.Running : InstallationStatus.Completed,
            state == "journal-of-another-installation" ? Guid.NewGuid() : null);
        var secrets = Path.Combine(fixture.Paths.ConfigRoot, "secrets");
        if (state is "bucket-corrected-after-start" or "endpoint-corrected-after-start")
        {
            // CameraAgent started holding a wrong value, read once at its start; the file was corrected afterwards.
            var (setting, right) = state == "bucket-corrected-after-start"
                ? (SiloRuntimeProfile.BucketSetting, harness.Bucket)
                : (SiloRuntimeProfile.EndpointSetting, SiloRuntimeProfile.Endpoint);
            SafeFileSystem.WriteTextAtomic(Path.Combine(secrets, setting), setting == SiloRuntimeProfile.BucketSetting
                ? "hvo-" + new string('f', 32)
                : "http://other-silo:9000");
            await Task.Delay(30);
            harness.CameraAgentStarted = DateTimeOffset.UtcNow;
            await Task.Delay(30);
            SafeFileSystem.WriteTextAtomic(Path.Combine(secrets, setting), right);
        }
        switch (state)
        {
            case "another-container-mounts-a-link-to-a-path-inside-the-data-root":
                // Sol R5-03 (a): the path the other container names is a link; what it resolves to lies inside the data root.
                Directory.CreateDirectory(Path.Combine(harness.DataRoot, ".minio.sys"));
                var link = Path.Combine(fixture.Root, "minio-alias");
                File.CreateSymbolicLink(link, Path.Combine(harness.DataRoot, ".minio.sys"));
                harness.AddOtherContainer(SiloHarness.BindMount(link));
                break;
            case "another-container-mounts-a-bind-volume-of-the-data-root":
                // Sol R5-03 (b): a named volume whose options bind it to the data root.
                harness.AddOtherContainer(harness.LocalVolume("bound", ("type", "none"), ("o", "bind"), ("device", harness.DataRoot)));
                break;
            case "another-container-mounts-a-tmpfs-typed-bind-volume-of-the-data-root":
                // Sol R5-02: the bind option makes it a bind of its device, whatever type it declares.
                harness.AddOtherContainer(harness.LocalVolume("bound", ("type", "tmpfs"), ("o", "bind"), ("device", harness.DataRoot)));
                break;
        }
        if (state == "wrong-bucket")
            SafeFileSystem.WriteTextAtomic(Path.Combine(secrets, SiloRuntimeProfile.BucketSetting), "hvo-" + new string('f', 32));
        if (state == "wrong-binding")
            SafeFileSystem.WriteTextAtomic(Path.Combine(secrets, SiloRuntimeProfile.StorageBindingIdSetting), new string('e', 64));
        using var camera = new FirstBootCamera(harness.Identity.FirstBootPauseId);
        switch (state)
        {
            case "refused-superseded":
                // An operator paused capture after the installer: the pause is no longer the installer's.
                camera.InForce = false;
                camera.Version = 2;
                break;
            case "already-resumed":
                camera.Resumed = true;
                camera.InForce = false;
                camera.State = "running";
                camera.Version = 2;
                break;
        }
        // An installed instance has its lock files; taking them once and releasing them leaves them as an install does.
        var instanceLock = Path.Combine(fixture.Paths.InstanceRoot, ".deployment.lock");
        var productLock = Path.Combine(fixture.Paths.OperationsRoot, "deployment.lock");
        OperationLock.Acquire(productLock).Dispose();
        OperationLock.Acquire(instanceLock).Dispose();
        Assert.IsTrue(LockFreeInChild(instanceLock) && LockFreeInChild(productLock), "Nothing holds the locks before the first resume.");
        var lockedDuringResume = false;
        // Another process can take neither of the installer's locks while CameraAgent records the resume.
        camera.OnResume = () => lockedDuringResume = !LockFreeInChild(instanceLock) && !LockFreeInChild(productLock);

        var (outcome, failure, runner, dockerCreated) = await FirstResumeAsync(fixture, harness, camera);

        var resumes = camera.Requests.Count(static request => request.Method == HttpMethod.Post);
        switch (state)
        {
            case "accepted":
                Assert.IsNull(failure, failure?.Message);
                Assert.AreEqual("resumed", outcome!["outcome"]!.GetValue<string>());
                Assert.AreEqual(1, resumes);
                Assert.AreEqual(1L, camera.ResumedFromVersion);
                Assert.IsTrue(lockedDuringResume, "CameraAgent records the resume while the installer's product and instance locks are held.");
                break;
            case "already-resumed":
                Assert.IsNull(failure, failure?.Message);
                Assert.AreEqual("already-resumed", outcome!["outcome"]!.GetValue<string>());
                Assert.AreEqual(0, resumes);
                break;
            case "refused-not-ready":
                AssertRefused(failure, "the Silo runtime is not ready");
                Assert.AreEqual(0, resumes);
                break;
            case "refused-superseded":
                AssertRefused(failure, "a later accepted capture command holds the pause");
                Assert.AreEqual(0, resumes);
                break;
            case "wrong-bucket":
            case "wrong-binding":
                AssertRefused(failure, state == "wrong-bucket"
                    ? "The Silo bucket CameraAgent is configured with is not this instance's bucket."
                    : $"CameraAgent's Silo setting {SiloRuntimeProfile.StorageBindingIdSetting} is not the one this instance records.");
                Assert.IsEmpty(camera.Requests, "Nothing is asked of CameraAgent, and no resume is sent.");
                Assert.IsFalse(runner!.Calls.Any(static call => call.Arguments is ["run", ..] or ["exec", ..]),
                    "No container probe runs before the configured bucket and binding are proved.");
                break;
            case "installation-interrupted-after-its-result":
                {
                    AssertRefused(failure, "this instance's installation has not completed");
                    Assert.IsFalse(dockerCreated, "Nothing is asked of Docker for an installation that has not completed.");
                    Assert.IsEmpty(camera.Requests);
                    // The re-run settles the installation; only then does the first resume open admission.
                    await WriteInstallationStateAsync(fixture, InstallationStatus.Completed);
                    var (settled, settledFailure, _, _) = await FirstResumeAsync(fixture, harness, camera);
                    Assert.IsNull(settledFailure, settledFailure?.Message);
                    Assert.AreEqual("resumed", settled!["outcome"]!.GetValue<string>());
                    Assert.AreEqual(1, camera.Requests.Count(static request => request.Method == HttpMethod.Post));
                    break;
                }
            case "journal-of-another-installation":
                AssertRefused(failure, "this instance's installation record is not the installation its manifest and result record");
                Assert.IsFalse(dockerCreated, "Nothing is asked of Docker for another installation's journal.");
                Assert.IsEmpty(camera.Requests, "Nothing is asked of CameraAgent, and no resume is sent.");
                break;
            case "bucket-corrected-after-start":
            case "endpoint-corrected-after-start":
                AssertRefused(failure, "CameraAgent has not restarted since its Silo bucket, binding or endpoint settings changed");
                Assert.IsEmpty(camera.Requests, "Nothing is asked of CameraAgent, and no resume is sent.");
                Assert.IsFalse(runner!.Calls.Any(static call => call.Arguments is ["run", ..]),
                    "No probe runs against settings the running process did not load.");
                break;
            case "another-container-mounts-a-link-to-a-path-inside-the-data-root":
            case "another-container-mounts-a-bind-volume-of-the-data-root":
            case "another-container-mounts-a-tmpfs-typed-bind-volume-of-the-data-root":
                AssertRefused(failure, "Another container references the Silo data root; it must belong to this instance's Silo alone");
                StringAssert.Contains(failure!.Message, state.Contains("link", StringComparison.Ordinal)
                    ? "which is the data root, lies inside it or holds it"
                    : $"a bind of {harness.DataRoot}, which is the data root", StringComparison.Ordinal);
                Assert.AreEqual(0, resumes, "No resume is sent.");
                Assert.IsFalse(camera.Requests.Any(static request => request.Method == HttpMethod.Post));
                break;
            default:
                Assert.Fail($"Unknown case {state}.");
                break;
        }
    }

    /// <summary>
    /// Whether another process can take the lock at <paramref name="path"/> now (the region the installer locks): exit 0
    /// when it can, 3 when the lock is held, and anything else (a missing file, say) fails the test rather than reading as
    /// either answer.
    /// </summary>
    private static bool LockFreeInChild(string path)
    {
        var start = new System.Diagnostics.ProcessStartInfo("python3") { UseShellExecute = false, RedirectStandardError = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(
            "import errno, fcntl, sys\n" +
            "f = open(sys.argv[1], 'r+')\n" +
            "try:\n" +
            "    fcntl.lockf(f, fcntl.LOCK_EX | fcntl.LOCK_NB, 1)\n" +
            "except OSError as e:\n" +
            "    sys.exit(3 if e.errno in (errno.EACCES, errno.EAGAIN) else 1)\n");
        start.ArgumentList.Add(path);
        using var process = System.Diagnostics.Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode is not (0 or 3))
            Assert.Fail($"The lock probe could not read {path}: {error}");
        return process.ExitCode == 0;
    }

    private static void AssertRefused(InstallerException? failure, string message)
    {
        Assert.IsNotNull(failure, "The first resume was expected to be refused.");
        Assert.IsTrue(failure.IsRefusal, failure.Message);
        StringAssert.Contains(failure.Message, message, StringComparison.Ordinal);
    }

    private static async Task<(JsonObject? Outcome, InstallerException? Failure, ScriptedDocker? Runner, bool DockerCreated)> FirstResumeAsync(
        LifecycleFixture fixture, SiloHarness harness, FirstBootCamera camera)
    {
        var runners = new List<ScriptedDocker>();
        var originalDocker = SiloCommands.DockerFactory;
        var originalClient = SiloCommands.ClientFactory;
        SiloCommands.DockerFactory = () =>
        {
            // The daemon preflight is the fixture's; everything the Silo checks ask is the harness's.
            var runner = new ScriptedDocker((arguments, input) => arguments is ["info", ..] and not ["info", "--format", "{{.DockerRootDir}}"] or ["compose", "version", ..]
                ? fixture.Runner.RunAsync("docker", arguments, CancellationToken.None).GetAwaiter().GetResult()
                : harness.Answer(arguments, input));
            runners.Add(runner);
            return new DockerClient(runner);
        };
        SiloCommands.ClientFactory = uri => new SiloFirstBootClient(uri, camera);
        try
        {
            var request = new SiloCommandRequest(SiloCommandKind.FirstResume, fixture.InstanceId, fixture.Paths.ProductRoot, Json: true)
            {
                AllowTestProductRoot = true
            };
            var outcome = await SiloCommands.ExecuteAsync(request, CancellationToken.None);
            return (outcome, null, runners.SingleOrDefault(), runners.Count > 0);
        }
        catch (InstallerException exception)
        {
            return (null, exception, runners.SingleOrDefault(), runners.Count > 0);
        }
        finally
        {
            SiloCommands.DockerFactory = originalDocker;
            SiloCommands.ClientFactory = originalClient;
        }
    }

    private static async Task WriteSiloManifestAsync(LifecycleFixture fixture, SiloInstallationIdentity silo)
    {
        var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None, allowSiloLayout: true);
        await SafeFileSystem.WriteJsonAtomicAsync(fixture.Paths.ManifestPath, manifest with { Silo = silo },
            DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
    }

    /// <summary>
    /// The installation state as the installer records it: completed, or still running in its verification phase, where an
    /// interrupted install leaves it after writing its result and before settling its records.
    /// </summary>
    private static async Task WriteInstallationStateAsync(LifecycleFixture fixture, InstallationStatus status, Guid? installationId = null)
    {
        // The installer's journal names the installation its manifest and result record, unless a case says otherwise.
        var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None, allowSiloLayout: true);
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.StatePath,
            new InstallationState(1, installationId ?? manifest.InstallationId, fixture.InstanceId, new string('0', 64),
                status == InstallationStatus.Completed ? InstallationPhase.Completed : InstallationPhase.Verification, status,
                DateTimeOffset.UtcNow),
            DeploymentJsonContext.Default.InstallationState,
            CancellationToken.None);
    }

    /// <summary>CameraAgent's first-boot routes as it answers them: the pause status, and the atomic first resume.</summary>
    private sealed class FirstBootCamera(Guid pauseId) : HttpMessageHandler
    {
        public bool InForce { get; set; } = true;
        public bool Resumed { get; set; }
        public string State { get; set; } = "paused";
        public long Version { get; set; } = 1;
        public long? ResumedFromVersion { get; private set; }
        public Action? OnResume { get; set; }
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Assert.AreEqual("lifecycle-token", request.Headers.GetValues("X-HVO-Installation-Token").Single());
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/internal/deployment/lifecycle/first-boot")
                return Json(HttpStatusCode.OK, new JsonObject
                {
                    ["id"] = pauseId.ToString("D"),
                    ["seeded"] = true,
                    ["inForce"] = InForce,
                    ["resumed"] = Resumed,
                    ["state"] = State,
                    ["version"] = Version
                });
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/internal/deployment/lifecycle/first-resume")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                OnResume?.Invoke();
                if (body.RootElement.GetProperty("firstBootId").GetGuid() != pauseId || !InForce ||
                    body.RootElement.GetProperty("expectedVersion").GetInt64() != Version)
                    return Json(HttpStatusCode.Conflict, new JsonObject { ["reason"] = "superseded" });
                ResumedFromVersion = Version;
                Resumed = true;
                InForce = false;
                State = "running";
                Version++;
                return Json(HttpStatusCode.OK, new JsonObject { ["state"] = State, ["version"] = Version });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, JsonObject body)
            => new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}
