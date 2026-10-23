using static HVO.SkyMonitor.Deployment.SplitHostJson;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class ExistingDownTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RetainedIntentBeforeLogin_StoppedCameraStillErasesSeededCredentials(bool present)
    {
        using var fixture = new ExistingDownFixture { KeepStoppedContainers = present };
        await fixture.StageAsync();
        var east = fixture.Host.Contract.Targets.Single(t => t.Name == "east");
        var seeded = new[] { east.RuntimeRoot + "/config/private/owner-password", east.RuntimeRoot + "/config/secrets/LocalIdentity__AdminPasswordFile" };
        foreach (var path in seeded) DeploymentFakeHost.Write(fixture.Host.Physical(path), "seeded-credential");
        await Assert.ThrowsAsync<PartialPrepareCrashException>(() => fixture.RunAsync(point =>
        {
            if (point != "abrupt-after-down-commit") return;
            var ledger = Parse(DeploymentFakeHost.Read(fixture.Host.StateRoot + "/down-ledger.json"));
            if (ledger.Array("resources").Any(n => n!.Text("resource") == "cameraagent:east")) throw new PartialPrepareCrashException();
        }));
        Assert.AreEqual(0, fixture.Stops);
        Assert.AreEqual(0, fixture.Queries.Count);
        fixture.StopCameraExternally("east");

        var result = await fixture.RunAsync();

        Assert.AreEqual("passed", result.Text("phaseStatus"));
        foreach (var path in seeded) Assert.IsFalse(File.Exists(fixture.Host.Physical(path)), path);
        Assert.AreEqual(2, fixture.Stops);
        Assert.AreEqual(0, fixture.StoppedHttpAttempts);
        AssertPrivateAbsent(fixture);
        AssertPreserved(fixture);
    }

    [TestMethod]
    [DataRow("east", true, false)]
    [DataRow("east", false, false)]
    [DataRow("west", true, false)]
    [DataRow("west", false, false)]
    [DataRow("east", true, true)]
    [DataRow("east", false, true)]
    [DataRow("west", true, true)]
    [DataRow("west", false, true)]
    public async Task InterruptedStop_RecoversWithoutHttpToStoppedCameras(string camera, bool present, bool cancel)
    {
        using var fixture = new ExistingDownFixture { KeepStoppedContainers = present };
        await fixture.StageAsync();
        using var cancellation = new CancellationTokenSource();
        void Interrupt(string point)
        {
            if (point != "after-graceful-stop-mutation-cameraagent:" + camera) return;
            if (!cancel) throw new PartialPrepareCrashException();
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        }
        if (cancel) await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.RunAsync(Interrupt, cancellation.Token));
        else await Assert.ThrowsAsync<PartialPrepareCrashException>(() => fixture.RunAsync(Interrupt));
        Assert.AreEqual(camera == "east" ? 1 : 2, fixture.Stops);
        var ledger = Parse(DeploymentFakeHost.Read(fixture.Host.StateRoot + "/down-ledger.json"));
        Assert.AreEqual("intent", ledger.Array("resources").Single(n => n!.Text("resource") == "cameraagent:" + camera)!.Text("status"));
        var diagnostics = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in Directory.GetFiles(fixture.Host.StateRoot + "/down-private")) diagnostics.Add(path, await File.ReadAllBytesAsync(path));
        Assert.IsTrue(diagnostics.Count > 0);
        var httpBefore = fixture.Queries.Count;

        var result = await fixture.RunAsync();

        Assert.AreEqual("passed", result.Text("phaseStatus"));
        Assert.IsTrue(result.Array("resources").All(n => n!.Text("status") == "completed"));
        Assert.AreEqual(3, fixture.Stops, "Recovery must not stop an already stopped service again.");
        Assert.AreEqual(0, fixture.StoppedHttpAttempts);
        var stoppedEndpoints = fixture.Host.Inventory.Array("cameraAgents").Where(n => camera == "west" || n!.Text("name") == "east").Select(n => n!.Text("internalEndpoint")).ToArray();
        Assert.IsFalse(fixture.Queries.Skip(httpBefore).Any(url => stoppedEndpoints.Any(endpoint => url.StartsWith(endpoint, StringComparison.Ordinal))));
        foreach (var (path, bytes) in diagnostics) CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path), path);
        AssertPrivateAbsent(fixture);
        AssertPreserved(fixture);
        Assert.AreEqual(Canonical(result), Canonical(Parse(DeploymentFakeHost.Read(fixture.Host.StateRoot + "/down-manifest.json"))));
        Assert.AreEqual(Canonical(result), Canonical(Parse(DeploymentFakeHost.Read(fixture.Host.EvidenceRoot + "/down.json"))));
        Assert.AreEqual(Hash(Canonical(result) + "\n"), Parse(DeploymentFakeHost.Read(fixture.Host.StateRoot + "/down-commit.json")).Text("ledgerSha256"));
        await fixture.RunAsync();
        Assert.AreEqual(3, fixture.Stops);
        Assert.AreEqual(0, fixture.StoppedHttpAttempts);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RetainedStopIntent_RequiresFreshDrainWhenCameraIsStillRunning(bool restartAfterStop)
    {
        using var fixture = new ExistingDownFixture { KeepStoppedContainers = true };
        await fixture.StageAsync();
        if (restartAfterStop)
        {
            await Assert.ThrowsAsync<PartialPrepareCrashException>(() => fixture.RunAsync(point =>
            {
                if (point == "after-graceful-stop-mutation-cameraagent:east") throw new PartialPrepareCrashException();
            }));
            fixture.RestartCamera("east");
        }
        else
        {
            fixture.Fault = "pause-conflict";
            await Assert.ThrowsAsync<InstallerException>(() => fixture.RunAsync());
        }
        var keys = fixture.ControlKeys.ToArray();
        var httpBefore = fixture.Queries.Count;
        fixture.Fault = "leased";
        var error = await Assert.ThrowsAsync<InstallerException>(() => fixture.RunAsync());
        StringAssert.Contains(error.Message, "drain-timeout", StringComparison.Ordinal);
        Assert.AreEqual(restartAfterStop ? 1 : 0, fixture.Stops);
        Assert.IsTrue(fixture.Queries.Count > httpBefore);
        Assert.IsTrue(fixture.ControlKeys.Count > keys.Length);
        Assert.IsFalse(fixture.ControlKeys.Skip(keys.Length).Intersect(keys, StringComparer.Ordinal).Any());
        fixture.Fault = "";
        await fixture.RunAsync();
        Assert.AreEqual(restartAfterStop ? 4 : 3, fixture.Stops);
        Assert.AreEqual(0, fixture.StoppedHttpAttempts);
        AssertPrivateAbsent(fixture);
        AssertPreserved(fixture);
    }

    [TestMethod]
    [DataRow("ssh")]
    [DataRow("daemon")]
    [DataRow("ps-unavailable")]
    [DataRow("inspect-unavailable")]
    [DataRow("project")]
    [DataRow("service")]
    [DataRow("container-id")]
    [DataRow("multiple-containers")]
    public async Task RetainedStopIntent_RejectsUnverifiedDockerAbsenceOrIdentity(string fault)
    {
        using var fixture = new ExistingDownFixture { KeepStoppedContainers = true };
        await fixture.StageAsync();
        await Assert.ThrowsAsync<PartialPrepareCrashException>(() => fixture.RunAsync(point =>
        {
            if (point == "after-graceful-stop-mutation-cameraagent:east") throw new PartialPrepareCrashException();
        }));
        if (fault == "ssh") fixture.Host.BadHost = "east";
        if (fault == "daemon") fixture.Host.BadDaemon = "east";
        var docker = fixture.Host.DockerResponse!;
        fixture.Host.DockerResponse = args =>
        {
            if (args.Count > 2 && args[2] == "compose" && args.Contains("ps"))
            {
                if (fault == "ps-unavailable") return new ProcessResult(1, "", "daemon unreachable");
                if (fault == "container-id") return new ProcessResult(0, "not-a-container", "");
                if (fault == "multiple-containers") return new ProcessResult(0, new string('a', 64) + "\n" + new string('b', 64), "");
            }
            if (args.Count > 3 && args[2] == "container" && args[3] == "inspect")
            {
                if (fault == "inspect-unavailable") return new ProcessResult(1, "", "daemon unreachable");
                if (fault is "project" or "service")
                {
                    var value = Parse(docker(args)!.StandardOutput);
                    value[fault] = "unrelated";
                    return new ProcessResult(0, value.ToJsonString(), "");
                }
            }
            return docker(args);
        };
        await Assert.ThrowsAsync<InstallerException>(() => fixture.RunAsync());
        Assert.AreEqual(1, fixture.Stops);
        Assert.AreEqual(0, fixture.StoppedHttpAttempts);
        var ledger = Parse(DeploymentFakeHost.Read(fixture.Host.StateRoot + "/down-ledger.json"));
        Assert.AreEqual("failed", ledger.Text("phaseStatus"));
        Assert.AreEqual("intent", ledger.Array("resources").Single(n => n!.Text("resource") == "cameraagent:east")!.Text("status"));
        AssertPrivateAbsent(fixture);
        AssertPreserved(fixture);
    }
}
