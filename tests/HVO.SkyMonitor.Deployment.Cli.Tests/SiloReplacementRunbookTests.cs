using System.Diagnostics;
using System.Text.Json.Nodes;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The runbook's stock-image replacement block for an installed instance (#1966 review rounds 2 to 5), extracted from
/// <c>docs/runbooks/cameraagent-silo-runtime.md</c> and run by bash as written, with only its two placeholders given. A
/// scripted <c>docker</c> and <c>hvo-sky-install</c> stand in for the daemon and the installer, and a scripted
/// <c>findmnt</c> adds bind mounts to this host's real mount table; the data root, its ancestors and the other containers'
/// mount sources are real directories and links, resolved and judged by device and inode on this host. The block refuses
/// before the stop what is there before it, keeps Silo stopped whenever a mount that appears after the stop is not proved
/// apart from its data root, and every stop says once what the phase it reached leaves.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SiloReplacementRunbookTests
{
    private static readonly Guid InstanceId = Guid.Parse("eba0e357-d549-4692-9ed6-9d2634accc6d");
    private static readonly string Compact = InstanceId.ToString("N");
    private static readonly string Silo = $"hvo-skymonitor-{Compact}-silo";
    private const string InventoryLine = "The version inventory taken before the stop is ";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("clean")]
    [DataRow("listed-container-gone-since")]
    [DataRow("archive-degraded")]
    [DataRow("volume-docker-manages-elsewhere")]
    [DataRow("bind-volume-elsewhere")]
    [DataRow("tmpfs-mount")]
    [DataRow("tmpfs-volume")]
    [DataRow("another-path-of-its-filesystem-bound-elsewhere")]
    [DataRow("running-container-holding-only-unrelated-mounts-now")]
    [DataRow("running-container-that-stopped-before-its-table-was-read")]
    [DataRow("unrelated-running-bind-volume-in-a-root-only-docker-folder")]
    public async Task TheBlock_ReplacesSiloWhenItsDataRootIsItsOwn(string change)
    {
        RequireTools();
        using var run = new Run();
        switch (change)
        {
            case "listed-container-gone-since": run.Flag("listed-but-gone"); break;
            case "archive-degraded": run.Verify("degraded"); break;
            case "volume-docker-manages-elsewhere": run.IntruderFromTheStart(run.ManagedVolume("cache")); break;
            case "bind-volume-elsewhere": run.IntruderFromTheStart(run.BindVolume("shared", Path.Combine(run.Root, "elsewhere"))); break;
            case "tmpfs-mount": run.IntruderFromTheStart(Run.Mount("tmpfs", string.Empty)); break;
            case "tmpfs-volume": run.IntruderFromTheStart(run.Volume("memory", "local", ("type", "tmpfs"), ("o", "size=64m"))); break;
            case "running-container-holding-only-unrelated-mounts-now":
                run.IntruderFromTheStart(Run.Bind(Path.Combine(run.Root, "elsewhere")));
                run.Live("intruder", ("0:99", "/", "/"), (run.ElsewhereLocation.Device, run.ElsewhereLocation.Path, "/probe"));
                break;
            case "running-container-that-stopped-before-its-table-was-read":
                // Docker answers it stopped once its own table could not be read: it holds nothing now.
                run.IntruderFromTheStart(Run.Bind(Path.Combine(run.Root, "elsewhere")));
                run.LiveExitsWhenRead("intruder");
                break;
            case "unrelated-running-bind-volume-in-a-root-only-docker-folder":
                // Docker's folders are root-only: the mount point is judged from the mount table alone.
                run.IntruderFromTheStart(run.BindVolume("shared", Path.Combine(run.Root, "elsewhere")));
                run.HostMount($"{run.DockerRootDirectory}/volumes/shared/_data", run.ElsewhereLocation.Path, run.ElsewhereLocation.Device);
                run.DockerRootUnsearchable();
                break;
            case "another-path-of-its-filesystem-bound-elsewhere":
                // A bind mount of a sibling of the instance root, on the data root's own filesystem: apart from it.
                run.IntruderFromTheStart(Run.Bind(run.BoundElsewhere("sibling-alias", run.DataLocation.Path[..run.DataLocation.Path.LastIndexOf('/')] + "-sibling")));
                break;
        }

        var (exit, output, error) = await run.ExecuteAsync(TestContext.CancellationToken);

        Assert.AreEqual(0, exit, error);
        StringAssert.Contains(output, "Replaced: 2 object versions read back by version and SHA-256", StringComparison.Ordinal);
        StringAssert.Contains(output, change == "archive-degraded"
            ? "silo-verify degraded: archive temporarily-unavailable (archive-unreachable), unchanged by the replacement."
            : "silo-verify ready.", StringComparison.Ordinal);
        CollectionAssert.Contains(run.DockerCalls, $"rm {Silo}");
        Assert.IsTrue(run.DockerCalls.Any(static call => call.StartsWith("compose ", StringComparison.Ordinal) && call.EndsWith(" up --detach --no-deps silo", StringComparison.Ordinal)));
        Assert.IsTrue(run.SiloRunning, "The replacement runs.");
        Assert.IsEmpty(Directory.GetFileSystemEntries(run.TemporaryRoot), "The working folder is removed after a replacement.");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("data-root-under-another-name", "container intruder mounts")]
    [DataRow("ancestor-under-another-name", "container intruder mounts")]
    [DataRow("data-root-itself", "container intruder mounts")]
    [DataRow("path-inside-the-data-root", "container intruder mounts")]
    [DataRow("ancestor-path", "container intruder mounts")]
    [DataRow("link-to-a-path-inside-the-data-root", "container intruder mounts")]
    [DataRow("path-inside-the-data-root-through-an-alias", "container intruder mounts")]
    [DataRow("source-renamed-after-the-stop", "which does not resolve here, so it is not proved apart from")]
    [DataRow("source-never-there", "which does not resolve here, so it is not proved apart from")]
    [DataRow("volume-bound-to-the-data-root", "container intruder mounts volume bound, a bind of")]
    [DataRow("volume-bound-to-an-alias-of-the-data-root", "container intruder mounts volume bound, a bind of")]
    [DataRow("volume-docker-manages-around-the-data-root", "container intruder mounts volume cache, kept in Docker's data root")]
    [DataRow("volume-tmpfs-typed-bound-to-the-data-root", "container intruder mounts volume bound, a bind of")]
    [DataRow("volume-rbind-of-another-type", "container intruder mounts volume bound, a bind of")]
    [DataRow("volume-bind-naming-no-device", "container intruder mounts volume bound, a bind that names no device, so where its data lies is not proved.")]
    [DataRow("volume-tmpfs-moved", "container intruder mounts volume moved, a local volume of type tmpfs that is not a bind, a volume Docker manages or a tmpfs, so where its data lies is not proved.")]
    [DataRow("volume-of-another-driver", "container intruder mounts volume remote, whose driver nfs-plugin is not the local driver, so where its data lies is not proved.")]
    [DataRow("volume-of-another-type", "container intruder mounts volume disk, a local volume of type ext4 that is not a bind, a volume Docker manages or a tmpfs, so where its data lies is not proved.")]
    // R6-01: what a running container holds now, and what a volume's mount point holds now.
    [DataRow("running-container-holding-a-path-inside-now", "container intruder holds in its own mount table now")]
    [DataRow("running-container-whose-link-was-retargeted", "container intruder holds in its own mount table now")]
    [DataRow("running-container-whose-folder-was-replaced", "container intruder holds in its own mount table now")]
    [DataRow("running-container-whose-mount-table-cannot-be-read", "container intruder runs, but its own mount table cannot be read, so what it holds now is not proved.")]
    [DataRow("bind-volume-whose-mount-point-holds-the-data-root-now", "whose mount point holds a mount of")]
    // R6-02: values judged exactly as Docker reports them.
    [DataRow("bind-source-with-a-trailing-space", "container intruder mounts")]
    [DataRow("bind-source-ending-in-a-newline", "container intruder mounts")]
    [DataRow("volume-type-with-a-pipe", "container intruder mounts volume bound, a bind of")]
    // R6-03: a volume's mount options are never shown, and a type only when it is a plain name.
    [DataRow("cifs-volume-with-credentials", "container intruder mounts volume share, a local volume of type cifs that is not a bind")]
    [DataRow("volume-type-that-is-not-a-plain-name", "container intruder mounts volume odd, a local volume of type (not shown) that is not a bind")]
    [DataRow("path-inside-the-data-root-bound-elsewhere", "which the mount table places on")]
    [DataRow("path-inside-a-bind-of-a-path-inside-the-data-root", "which the mount table places on")]
    [DataRow("data-root-bound-elsewhere", "which the mount table places on")]
    [DataRow("its-filesystem-root-bound-elsewhere", "which the mount table places on")]
    [DataRow("folder-holding-a-bind-of-the-data-root", "which holds a mount of")]
    [DataRow("folder-holding-a-bind-of-a-path-inside", "which holds a mount of")]
    [DataRow("volume-docker-cannot-inspect", "\"v=$(docker volume inspect \"$volume\")\" failed.")]
    [DataRow("mount-of-another-type", "container intruder has a mount of type image, so where its data lies is not proved.")]
    [DataRow("unanswered-inventory", "\"ids=$(docker ps -aq --no-trunc)\" failed.")]
    [DataRow("uninspectable-container", "Docker could not inspect container intruder, so its mounts are unknown")]
    public async Task TheBlock_KeepsSiloStoppedWhileItsDataRootIsNotProvedItsOwn(string change, string reason)
    {
        // Each other container appears only once Silo has stopped, after the proof before the stop passed.
        RequireTools();
        using var run = new Run();
        var inside = Path.Combine(run.DataRoot, ".minio.sys");
        switch (change)
        {
            case "data-root-under-another-name": run.Intruder(Run.Bind(run.Link("data-alias", run.DataRoot))); break;
            case "ancestor-under-another-name": run.Intruder(Run.Bind(run.Link("instance-alias", run.InstanceRoot))); break;
            case "data-root-itself": run.Intruder(Run.Bind(run.DataRoot)); break;
            case "path-inside-the-data-root": run.Intruder(Run.Bind(inside)); break;
            case "ancestor-path": run.Intruder(Run.Bind(run.InstanceRoot)); break;
            case "link-to-a-path-inside-the-data-root": run.Intruder(Run.Bind(run.Link("minio-alias", inside))); break;
            case "path-inside-the-data-root-through-an-alias":
                run.Intruder(Run.Bind(Path.Combine(run.Link("data-alias", run.DataRoot), ".minio.sys")));
                break;
            case "source-renamed-after-the-stop":
                var moving = Path.Combine(run.Root, "moving");
                Directory.CreateDirectory(moving);
                run.IntruderFromTheStart(Run.Bind(moving));
                run.RenameOnStop(moving, Path.Combine(run.Root, "moved"));
                break;
            case "source-never-there": run.Intruder(Run.Bind(Path.Combine(run.Root, "absent"))); break;
            case "volume-bound-to-the-data-root": run.Intruder(run.BindVolume("bound", run.DataRoot)); break;
            case "volume-bound-to-an-alias-of-the-data-root": run.Intruder(run.BindVolume("bound", run.Link("data-alias", run.DataRoot))); break;
            case "volume-docker-manages-around-the-data-root":
                run.DockerRoot(run.ProductRoot);
                run.Intruder(run.ManagedVolume("cache"));
                break;
            case "volume-tmpfs-typed-bound-to-the-data-root":
                run.Intruder(run.Volume("bound", "local", ("type", "tmpfs"), ("o", "bind"), ("device", run.DataRoot)));
                break;
            case "volume-rbind-of-another-type":
                run.Intruder(run.Volume("bound", "local", ("type", "ext4"), ("o", "rbind,ro"), ("device", run.Link("data-alias", run.DataRoot))));
                break;
            case "volume-bind-naming-no-device": run.Intruder(run.Volume("bound", "local", ("type", "none"), ("o", "bind"))); break;
            case "volume-tmpfs-moved":
                run.Intruder(run.Volume("moved", "local", ("type", "tmpfs"), ("o", "move"), ("device", Path.Combine(run.Root, "elsewhere"))));
                break;
            case "running-container-holding-a-path-inside-now":
                run.Intruder(Run.Bind(Path.Combine(run.Root, "elsewhere")));
                run.Live("intruder", (run.DataLocation.Device, run.DataLocation.Path + "/.minio.sys", "/probe"));
                break;
            case "running-container-whose-link-was-retargeted":
                // The link named a path inside the data root when the container started; it names another folder now.
                run.Intruder(Run.Bind(run.Link("alias", Path.Combine(run.Root, "elsewhere"))));
                run.Live("intruder", (run.DataLocation.Device, run.DataLocation.Path + "/.minio.sys", "/probe"));
                break;
            case "running-container-whose-folder-was-replaced":
                Directory.CreateDirectory(Path.Combine(run.Root, "replaced"));
                run.Intruder(Run.Bind(Path.Combine(run.Root, "replaced")));
                run.Live("intruder", (run.DataLocation.Device, run.DataLocation.Path, "/probe"));
                break;
            case "running-container-whose-mount-table-cannot-be-read":
                run.Intruder(Run.Bind(Path.Combine(run.Root, "elsewhere")));
                run.LiveUnreadable("intruder");
                break;
            case "bind-volume-whose-mount-point-holds-the-data-root-now":
                // Its device names an unrelated folder now; the mount Docker made when it named the data root remains.
                run.Intruder(run.BindVolume("bound", Path.Combine(run.Root, "elsewhere")));
                run.HostMount($"{run.DockerRootDirectory}/volumes/bound/_data", run.DataLocation.Path);
                break;
            case "bind-source-with-a-trailing-space":
                // The name with a trailing space links into the data root; the same name without it is an unrelated folder.
                Directory.CreateDirectory(Path.Combine(run.Root, "mount-alias"));
                run.Intruder(Run.Bind(run.Link("mount-alias ", inside)));
                break;
            case "bind-source-ending-in-a-newline":
                Directory.CreateDirectory(Path.Combine(run.Root, "newline-alias"));
                run.Intruder(Run.Bind(run.Link("newline-alias\n", inside)));
                break;
            case "volume-type-with-a-pipe":
                run.Intruder(run.Volume("bound", "local", ("type", "tmpfs|safe"), ("o", "bind"), ("device", run.DataRoot)));
                break;
            case "cifs-volume-with-credentials":
                run.Intruder(run.Volume("share", "local", ("type", "cifs"),
                    ("o", "username=alice,password=TEST_CIFS_SECRET_VALUE,pass=TEST_SHORT_PASS_VALUE"), ("device", "//archive.example/share")));
                break;
            case "volume-type-that-is-not-a-plain-name":
                run.Intruder(run.Volume("odd", "local", ("type", "cifs,password=TEST_IN_TYPE"), ("o", "TEST_OPTION")));
                break;
            case "path-inside-the-data-root-bound-elsewhere":
                // Sol R5-01: a folder elsewhere that is a bind mount of a path inside the data root.
                run.Intruder(Run.Bind(run.BoundElsewhere("minio-elsewhere", run.DataLocation.Path + "/.minio.sys")));
                break;
            case "path-inside-a-bind-of-a-path-inside-the-data-root":
                // Only where the mount table places it shows this one: no mount lies at or below it, and no folder above it is
                // the data root under another name.
                var buckets = Path.Combine(run.BoundElsewhere("minio-elsewhere", run.DataLocation.Path + "/.minio.sys"), "buckets");
                Directory.CreateDirectory(buckets);
                run.Intruder(Run.Bind(buckets));
                break;
            case "data-root-bound-elsewhere": run.Intruder(Run.Bind(run.BoundElsewhere("data-elsewhere", run.DataLocation.Path))); break;
            case "its-filesystem-root-bound-elsewhere": run.Intruder(Run.Bind(run.BoundElsewhere("filesystem-elsewhere", "/"))); break;
            case "folder-holding-a-bind-of-the-data-root":
                // Fable r5 note 2: Docker binds a source with the mounts below it.
                run.BoundElsewhere(Path.Combine("holder", "below"), run.DataLocation.Path);
                run.Intruder(Run.Bind(Path.Combine(run.Root, "holder")));
                break;
            case "folder-holding-a-bind-of-a-path-inside":
                run.BoundElsewhere(Path.Combine("holder", "deep", "below"), run.DataLocation.Path + "/.minio.sys");
                run.Intruder(Run.Bind(Path.Combine(run.Root, "holder")));
                break;
            case "volume-of-another-driver": run.Intruder(run.Volume("remote", "nfs-plugin")); break;
            case "volume-of-another-type": run.Intruder(run.Volume("disk", "local", ("type", "ext4"), ("device", "/dev/sdz1"))); break;
            case "volume-docker-cannot-inspect": run.Intruder(Run.VolumeMount("vanished", "/var/lib/docker/volumes/vanished/_data")); break;
            case "mount-of-another-type": run.Intruder(Run.Mount("image", "sha256:0123")); break;
            case "unanswered-inventory": run.Flag("ps-fails-once-stopped"); break;
            case "uninspectable-container": run.Intruder(Run.Bind(Path.Combine(run.Root, "elsewhere"))); run.Flag("inspect-fails-intruder"); break;
        }

        var (exit, _, error) = await run.ExecuteAsync(TestContext.CancellationToken);

        Assert.AreEqual(1, exit, error);
        StringAssert.Contains(error, reason, StringComparison.Ordinal);
        AssertStoppedOnce(error);
        CollectionAssert.Contains(run.DockerCalls, $"stop --timeout 30 {Silo}", "The proof before the stop passed.");
        StringAssert.Contains(error, "Silo stays stopped and its original container is kept: its data root is not proved its own.", StringComparison.Ordinal);
        Assert.IsFalse(run.DockerCalls.Any(static call => call.StartsWith("rm ", StringComparison.Ordinal) || call.StartsWith("compose ", StringComparison.Ordinal)),
            "Nothing is removed or started: " + string.Join("; ", run.DockerCalls));
        Assert.IsFalse(run.SiloRunning, "Silo stays stopped.");
        Assert.IsTrue(run.OriginalSiloExists, "The original container is kept.");
        Assert.IsFalse(error.Contains("TEST_", StringComparison.Ordinal), "No mount option is shown: " + error);
        AssertInventoryKept(error);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("data-root-itself", "container intruder mounts")]
    [DataRow("volume-tmpfs-typed-bound-to-the-data-root", "container intruder mounts volume bound, a bind of")]
    [DataRow("path-inside-the-data-root-bound-elsewhere", "which the mount table places on")]
    [DataRow("unanswered-inventory", "\"ids=$(docker ps -aq --no-trunc)\" failed.")]
    [DataRow("running-container-holding-the-data-root-now", "container intruder holds in its own mount table now")]
    public async Task TheBlock_RefusesBeforeTheStopWhatIsThereBeforeIt(string change, string reason)
    {
        // Fable r5 note 3: the same proof runs before the stop, so a refusal then leaves Silo running and changes nothing.
        RequireTools();
        using var run = new Run();
        switch (change)
        {
            case "data-root-itself": run.IntruderFromTheStart(Run.Bind(run.DataRoot)); break;
            case "volume-tmpfs-typed-bound-to-the-data-root":
                run.IntruderFromTheStart(run.Volume("bound", "local", ("type", "tmpfs"), ("o", "bind"), ("device", run.DataRoot)));
                break;
            case "running-container-holding-the-data-root-now":
                run.IntruderFromTheStart(Run.Bind(Path.Combine(run.Root, "elsewhere")));
                run.Live("intruder", (run.DataLocation.Device, run.DataLocation.Path, "/probe"));
                break;
            case "path-inside-the-data-root-bound-elsewhere":
                run.IntruderFromTheStart(Run.Bind(run.BoundElsewhere("minio-elsewhere", run.DataLocation.Path + "/.minio.sys")));
                break;
            case "unanswered-inventory": run.Flag("ps-fails"); break;
        }

        var (exit, _, error) = await run.ExecuteAsync(TestContext.CancellationToken);

        Assert.AreEqual(1, exit, error);
        StringAssert.Contains(error, reason, StringComparison.Ordinal);
        AssertStoppedOnce(error);
        StringAssert.Contains(error, "Nothing was changed; Silo is still running.", StringComparison.Ordinal);
        Assert.IsFalse(error.Contains("docker start", StringComparison.Ordinal), error);
        Assert.IsFalse(run.DockerCalls.Any(static call => call.StartsWith("stop ", StringComparison.Ordinal) || call.StartsWith("exec ", StringComparison.Ordinal)),
            "Nothing is stopped, and no inventory is taken: " + string.Join("; ", run.DockerCalls));
        Assert.IsTrue(run.SiloRunning);
        Assert.IsEmpty(Directory.GetFileSystemEntries(run.TemporaryRoot), "The working folder is removed.");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("not-ready-before", "Nothing was changed; Silo is still running.")]
    [DataRow("inventory-incomplete-before", "Nothing was changed; Silo is still running.")]
    [DataRow("stop-fails", "The stop did not prove Silo's process exited, and Silo may still run; check it with: docker inspect")]
    [DataRow("removal-fails", "Silo stays stopped and its data root is proved its own, but removing its original container failed.")]
    [DataRow("replacement-does-not-start", "The original Silo container is removed and no replacement runs. Start the replacement with:")]
    [DataRow("read-back-differs", "The replacement Silo runs but did not verify as the original.")]
    [DataRow("read-back-incomplete", "The replacement Silo runs but did not verify as the original.")]
    public async Task TheBlock_SaysWhatThePhaseItReachedLeaves(string change, string guidance)
    {
        RequireTools();
        using var run = new Run();
        switch (change)
        {
            case "not-ready-before": run.Verify("not-ready"); break;
            case "inventory-incomplete-before": run.ReadFails("before"); break;
            case "stop-fails": run.Flag("stop-fails"); break;
            case "removal-fails": run.Flag("rm-fails"); break;
            case "replacement-does-not-start": run.Flag("up-fails"); break;
            case "read-back-differs": run.Flag("differ-after"); break;
            case "read-back-incomplete": run.ReadFails("after"); break;
        }

        var (exit, _, error) = await run.ExecuteAsync(TestContext.CancellationToken);

        Assert.AreEqual(1, exit, error);
        AssertStoppedOnce(error);
        StringAssert.Contains(error, guidance, StringComparison.Ordinal);
        if (change != "removal-fails")
            Assert.IsFalse(error.Contains("docker start", StringComparison.Ordinal), "Only an original whose container is kept is ever started again: " + error);
        switch (change)
        {
            case "not-ready-before" or "inventory-incomplete-before":
                Assert.IsFalse(run.DockerCalls.Any(static call => call.StartsWith("stop ", StringComparison.Ordinal)), "Nothing is stopped.");
                Assert.IsTrue(run.SiloRunning);
                Assert.IsFalse(error.Contains("The version inventory", StringComparison.Ordinal), "No completed inventory exists to keep: " + error);
                Assert.IsEmpty(Directory.GetFileSystemEntries(run.TemporaryRoot), "The working folder, and any incomplete inventory in it, is removed.");
                return;
            case "stop-fails":
                Assert.IsFalse(run.DockerCalls.Any(static call => call.StartsWith("rm ", StringComparison.Ordinal) || call.StartsWith("compose ", StringComparison.Ordinal)));
                break;
            case "removal-fails":
                StringAssert.Contains(error, $"docker start {Silo}", StringComparison.Ordinal);
                StringAssert.Contains(error, $"--project-name hvo-skymonitor-{Compact}", StringComparison.Ordinal);
                Assert.IsFalse(error.Contains("is not proved its own", StringComparison.Ordinal), "The data root was proved: " + error);
                Assert.IsFalse(run.DockerCalls.Any(static call => call.StartsWith("compose ", StringComparison.Ordinal)), "Nothing is started.");
                Assert.IsFalse(run.SiloRunning);
                Assert.IsTrue(run.OriginalSiloExists);
                break;
            case "replacement-does-not-start":
                StringAssert.Contains(error, $"--project-name hvo-skymonitor-{Compact}", StringComparison.Ordinal);
                Assert.IsFalse(run.OriginalSiloExists, "The original container was removed before the replacement failed.");
                break;
            case "read-back-differs" or "read-back-incomplete":
                StringAssert.Contains(error, $"docker stop {Silo}", StringComparison.Ordinal);
                break;
        }
        AssertInventoryKept(error);
    }

    private static void RequireTools()
    {
        // The runbook asks for jq on the host, as the development/v1 runners provide it, and the block reads the mount table
        // with util-linux findmnt; without them it cannot run.
        if (!File.Exists("/usr/bin/jq") && !File.Exists("/bin/jq"))
            Assert.Inconclusive("The replacement block needs jq on the host, as the runbook states; install jq to run these cases.");
        if (!File.Exists("/usr/bin/findmnt"))
            Assert.Inconclusive("The replacement block reads the mount table with util-linux findmnt, which this host lacks.");
    }

    private static void AssertStoppedOnce(string error)
        => Assert.AreEqual(1, error.Split("Stopped: ").Length - 1, "The stop is reported once: " + error);

    /// <summary>The kept inventory is the completed one taken before the stop, and nothing incomplete is kept beside it.</summary>
    private static void AssertInventoryKept(string error)
    {
        var line = error.Split('\n').Single(static line => line.StartsWith(InventoryLine, StringComparison.Ordinal));
        var file = line[InventoryLine.Length..line.IndexOf("; remove ", StringComparison.Ordinal)];
        var folder = Path.GetDirectoryName(file)!;
        StringAssert.EndsWith(line, $"; remove {folder} when done.", StringComparison.Ordinal);
        Assert.AreEqual("before", Path.GetFileName(file));
        Assert.HasCount(2, File.ReadAllLines(file), "The kept inventory is complete: " + file);
        Assert.IsEmpty(Directory.GetFiles(folder, "*.partial"), "An incomplete inventory is not kept.");
    }

    /// <summary>One run of the block: a real instance layout, the scripted CLIs on PATH and the state they answer from.</summary>
    private sealed class Run : IDisposable
    {
        private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("hvo-silo-replace-");
        private readonly Dictionary<string, int> pids = [];

        public Run()
        {
            Directory.CreateDirectory(Path.Combine(DataRoot, ".minio.sys"));
            Directory.CreateDirectory(Path.Combine(InstanceRoot, "config", "secrets"));
            Directory.CreateDirectory(Path.Combine(Root, "elsewhere"));
            Directory.CreateDirectory(Path.Combine(Root, "docker"));
            Directory.CreateDirectory(TemporaryRoot);
            Directory.CreateDirectory(Path.Combine(State, "containers"));
            Directory.CreateDirectory(Path.Combine(State, "volumes"));
            Directory.CreateDirectory(Path.Combine(State, "after-stop"));
            Directory.CreateDirectory(Path.Combine(State, "live"));
            DockerRoot(Path.Combine(Root, "docker"));
            Container("silo-original", "/" + Silo, Bind(DataRoot));
            Container("cameraagent", $"/hvo-skymonitor-{Compact}", Bind(Path.Combine(InstanceRoot, "config", "secrets")), Mount("tmpfs", string.Empty));
            Container("unrelated", "/unrelated", Bind(Path.Combine(Root, "elsewhere")));
            File.WriteAllText(Path.Combine(State, "listing"), "meta/instance.v1.json\tv1\nmeta/fence.json\tv2\n");
            Object("meta/instance.v1.json", "v1", "{\"format\":\"marker\"}");
            Object("meta/fence.json", "v2", "{\"format\":\"fence\"}");
            Verify("ready");
            Script("docker", FakeDocker);
            Script("hvo-sky-install", FakeInstaller);
            Script("findmnt", FakeFindmnt);
        }

        public string Root => root.FullName;
        public string ProductRoot => Path.Combine(Root, "product");
        public string InstanceRoot => Path.Combine(ProductRoot, "cameraagents", InstanceId.ToString("D"));
        public string DataRoot => Path.Combine(InstanceRoot, "silo");
        public string TemporaryRoot => Path.Combine(Root, "tmp");
        private string State => Path.Combine(Root, "state");
        private string Bin => Path.Combine(Root, "bin");
        public string DockerRootDirectory => File.ReadAllText(Path.Combine(State, "docker-root")).Trim();

        /// <summary>Where this host's mount table places the data root: the device of its mount and its path within it.</summary>
        public SiloMountLocation DataLocation => SiloDataRootExclusivity.Locate(
            NativeLinux.TryResolvePath(DataRoot)!, File.ReadAllText(HostProductRootPolicy.MountInfoPath));

        /// <summary>Where this host's mount table places the run's unrelated folder.</summary>
        public SiloMountLocation ElsewhereLocation => SiloDataRootExclusivity.Locate(
            NativeLinux.TryResolvePath(Path.Combine(Root, "elsewhere"))!, File.ReadAllText(HostProductRootPolicy.MountInfoPath));

        /// <summary>A mount the scripted host mount table lists at <paramref name="target"/>, without creating that folder.</summary>
        public void HostMount(string target, string root, string? device = null)
        {
            var table = Path.Combine(State, "mounts.json");
            var mounts = File.Exists(table) ? JsonNode.Parse(File.ReadAllText(table))!.AsArray() : new JsonArray();
            mounts.Add(new JsonObject { ["maj:min"] = device ?? DataLocation.Device, ["fsroot"] = root, ["target"] = target });
            File.WriteAllText(table, mounts.ToJsonString());
        }

        /// <summary>A running container process's own mount table, as findmnt reads <c>/proc/&lt;pid&gt;/mountinfo</c>.</summary>
        public void Live(string container, params (string Device, string Root, string Target)[] mounts)
            => File.WriteAllText(Path.Combine(State, "live", $"{pids[container]}.json"), new JsonObject
            {
                ["filesystems"] = new JsonArray([.. mounts.Select(static mount => (JsonNode)new JsonObject
                {
                    ["maj:min"] = mount.Device, ["fsroot"] = mount.Root, ["target"] = mount.Target
                })])
            }.ToJsonString());

        /// <summary>The container process's own mount table cannot be read, as under <c>hidepid</c>.</summary>
        public void LiveUnreadable(string container) => File.WriteAllText(Path.Combine(State, "live", $"{pids[container]}.unreadable"), string.Empty);

        /// <summary>The container stops as its own mount table is read, so that read fails and Docker then answers it stopped.</summary>
        public void LiveExitsWhenRead(string container)
            => File.WriteAllText(Path.Combine(State, "live", $"{pids[container]}.exits"), Path.Combine(State, "containers", container));

        /// <summary>Docker's data root becomes a folder no one but root may enter, as <c>/var/lib/docker</c> is.</summary>
        public void DockerRootUnsearchable() => File.SetUnixFileMode(Path.Combine(Root, "docker"), UnixFileMode.None);

        /// <summary>
        /// A folder under the run's root that the scripted mount table lists as a bind mount of <paramref name="root"/> on
        /// the data root's filesystem: what no path resolution or device and inode comparison sees.
        /// </summary>
        public string BoundElsewhere(string name, string root)
        {
            var target = Path.Combine(Root, name);
            Directory.CreateDirectory(target);
            var table = Path.Combine(State, "mounts.json");
            var mounts = File.Exists(table) ? JsonNode.Parse(File.ReadAllText(table))!.AsArray() : new JsonArray();
            mounts.Add(new JsonObject { ["maj:min"] = DataLocation.Device, ["fsroot"] = root, ["target"] = target });
            File.WriteAllText(table, mounts.ToJsonString());
            return target;
        }

        public string[] DockerCalls => File.Exists(Path.Combine(State, "calls")) ? File.ReadAllLines(Path.Combine(State, "calls")) : [];
        public bool OriginalSiloExists => Directory.Exists(Path.Combine(State, "containers", "silo-original"));
        public bool SiloRunning => Directory.GetDirectories(Path.Combine(State, "containers"))
            .Any(container => File.ReadAllText(Path.Combine(container, "name")).Trim() == "/" + Silo &&
                              File.ReadAllText(Path.Combine(container, "running")).Trim() == "true");

        /// <summary>A mount as <c>docker inspect</c> reports it.</summary>
        public static JsonObject Mount(string type, string source) => new() { ["Type"] = type, ["Source"] = source, ["Destination"] = "/other" };

        public static JsonObject Bind(string source) => Mount("bind", source);

        public static JsonObject VolumeMount(string name, string source)
            => new() { ["Type"] = "volume", ["Name"] = name, ["Source"] = source, ["Destination"] = "/other" };

        public void Flag(string name) => File.WriteAllText(Path.Combine(State, name), string.Empty);

        public void Verify(string readiness) => File.WriteAllText(Path.Combine(State, "verify.json"),
            $"{{\"readiness\":\"{readiness}\",\"archive\":{{\"state\":\"temporarily-unavailable\",\"reason\":\"archive-unreachable\"}}}}\n");

        /// <summary>Reading the second object version fails, in the inventory taken <c>before</c> the stop or <c>after</c> it.</summary>
        public void ReadFails(string when) => File.WriteAllText(Path.Combine(State, "read-fails"), when);

        /// <summary>The daemon's data root, which holds the volumes it manages.</summary>
        public void DockerRoot(string path) => File.WriteAllText(Path.Combine(State, "docker-root"), path);

        public void RenameOnStop(string from, string to) => File.WriteAllText(Path.Combine(State, "rename-on-stop"), $"{from}\t{to}");

        /// <summary>A link elsewhere naming <paramref name="target"/>: the same directory under another name.</summary>
        public string Link(string name, string target)
        {
            var link = Path.Combine(Root, name);
            if (!File.Exists(link) && !Directory.Exists(link)) File.CreateSymbolicLink(link, target);
            return link;
        }

        /// <summary>
        /// A volume as <c>docker volume inspect</c> answers it (no options for one Docker manages), at Docker's own mount
        /// point, and the mount that uses it.
        /// </summary>
        public JsonObject Volume(string name, string driver, params (string Key, string Value)[] options)
        {
            var mountpoint = driver == "local" ? $"{DockerRootDirectory}/volumes/{name}/_data" : $"/mnt/{name}";
            JsonNode? values = null;
            if (options.Length > 0)
            {
                var given = new JsonObject();
                foreach (var (key, value) in options) given[key] = value;
                values = given;
            }
            File.WriteAllText(Path.Combine(State, "volumes", name + ".json"),
                new JsonArray(new JsonObject { ["Name"] = name, ["Driver"] = driver, ["Mountpoint"] = mountpoint, ["Options"] = values }).ToJsonString());
            return VolumeMount(name, mountpoint);
        }

        public JsonObject ManagedVolume(string name) => Volume(name, "local");

        public JsonObject BindVolume(string name, string device) => Volume(name, "local", ("type", "none"), ("o", "bind"), ("device", device));

        /// <summary>Another container that Docker creates once Silo has stopped, with the given mounts.</summary>
        public void Intruder(params JsonObject[] mounts) => Container("intruder", "/intruder", mounts, "after-stop");

        /// <summary>Another container that is there before the procedure starts.</summary>
        public void IntruderFromTheStart(params JsonObject[] mounts) => Container("intruder", "/intruder", mounts);

        public async Task<(int Exit, string Output, string Error)> ExecuteAsync(CancellationToken cancellationToken)
        {
            var block = Block();
            var script = Path.Combine(Root, "replace.sh");
            await File.WriteAllTextAsync(script, block.Replace(
                "ID='<instance-id>'; P='<product-root>';",
                $"ID='{InstanceId:D}'; P='{ProductRoot}';",
                StringComparison.Ordinal), cancellationToken);
            Assert.AreEqual(1, (await File.ReadAllLinesAsync(script, cancellationToken)).Zip(block.Split('\n')).Count(static pair => pair.First != pair.Second),
                "Only the placeholders are given.");
            var start = new ProcessStartInfo("bash")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(script);
            start.Environment["PATH"] = Bin + ":/usr/bin:/bin";
            start.Environment["TMPDIR"] = TemporaryRoot;
            start.Environment["FAKE_STATE"] = State;
            start.Environment["FAKE_SILO"] = Silo;
            start.Environment["FAKE_DATA"] = DataRoot;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return (process.ExitCode, await output, await error);
        }

        private static string Block()
        {
            var runbook = File.ReadAllLines(Path.Combine(RepositoryRoot(), "docs", "runbooks", "cameraagent-silo-runtime.md"));
            var intro = Array.FindIndex(runbook, static line => line.StartsWith("On an installed instance, as the user that installed it, with `jq`", StringComparison.Ordinal));
            Assert.IsGreaterThanOrEqualTo(0, intro, "The runbook's replacement block for an installed instance is found.");
            var open = Array.FindIndex(runbook, intro, static line => line == "```bash");
            var close = Array.FindIndex(runbook, open + 1, static line => line == "```");
            return string.Join('\n', runbook[(open + 1)..close]) + "\n";
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
        }

        private void Container(string id, string name, params JsonObject[] mounts) => Container(id, name, mounts, "containers");

        /// <summary>A container's state; each gets a process ID no real process can have (above the kernel's limit).</summary>
        private void Container(string id, string name, JsonObject[] mounts, string folder)
        {
            var directory = Path.Combine(State, folder, id);
            Directory.CreateDirectory(directory);
            pids[id] = id == "silo-original" ? 2147483646 : 2147480000 + pids.Count;
            File.WriteAllText(Path.Combine(directory, "name"), name + "\n");
            File.WriteAllText(Path.Combine(directory, "running"), "true\n");
            File.WriteAllText(Path.Combine(directory, "health"), "healthy\n");
            File.WriteAllText(Path.Combine(directory, "pid"), pids[id].ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
            File.WriteAllText(Path.Combine(directory, "mounts.json"), new JsonArray([.. mounts]).ToJsonString());
        }

        private void Object(string key, string version, string content)
        {
            var path = Path.Combine(State, "objects", $"{key}@{version}");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        private void Script(string name, string text)
        {
            Directory.CreateDirectory(Bin);
            var path = Path.Combine(Bin, name);
            File.WriteAllText(path, text);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public void Dispose()
        {
            File.SetUnixFileMode(Path.Combine(Root, "docker"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            root.Delete(recursive: true);
        }

        /// <summary>The Docker CLI the block calls, answered from the state folder; every call is logged.</summary>
        private const string FakeDocker = """
            #!/usr/bin/env bash
            set -u
            S=${FAKE_STATE:?}
            printf '%s\n' "$*" >> "$S/calls"
            find_container() {
              local d
              for d in "$S"/containers/*; do
                if [[ -d $d && ( $(basename "$d") == "$1" || $(cat "$d/name") == "/$1" ) ]]; then echo "$d"; return 0; fi
              done
              return 1
            }
            case "$1" in
              ps)
                if [[ -e $S/ps-fails || ( -e $S/ps-fails-once-stopped && $(cat "$S/containers/silo-original/running" 2> /dev/null) == false ) ]]; then
                  echo "Cannot connect to the Docker daemon (scripted)" >&2; exit 1
                fi
                for d in "$S"/containers/*; do if [[ -d $d ]]; then basename "$d"; fi; done
                if [[ -e $S/listed-but-gone ]]; then echo gone0000; fi
                exit 0 ;;
              info)
                if [[ $* != 'info --format {{.DockerRootDir}}' ]]; then echo "unscripted info call" >&2; exit 2; fi
                cat "$S/docker-root"; echo; exit 0 ;;
              volume)
                if [[ $2 != inspect ]]; then echo "unscripted volume call" >&2; exit 2; fi
                name=${@: -1}
                if [[ ! -f $S/volumes/$name.json ]]; then echo "[]"; echo "Error response from daemon: get $name: no such volume" >&2; exit 1; fi
                if [[ $# == 3 ]]; then cat "$S/volumes/$name.json"; exit 0; fi
                # Docker's own rendering of a Go template, for the one an earlier block used.
                if [[ $# == 5 && $3 == --format && $4 == '{{.Driver}}|{{len .Options}}|{{index .Options "type"}}|{{index .Options "o"}}|{{.Mountpoint}}|{{index .Options "device"}}' ]]; then
                  jq -r '.[0] | "\(.Driver)|\(.Options // {} | length)|\(.Options.type // "")|\(.Options.o // "")|\(.Mountpoint)|\(.Options.device // "")"' "$S/volumes/$name.json"
                  exit 0
                fi
                echo "unscripted volume call" >&2; exit 2 ;;
              inspect)
                if [[ $2 != --format ]]; then
                  # The whole inspection, as JSON: name, state with its process ID, and mounts.
                  target=$2
                  if [[ -e $S/inspect-fails-$target ]]; then echo "Error response from daemon: scripted failure" >&2; exit 1; fi
                  d=$(find_container "$target") || { echo "Error response from daemon: No such container: $target" >&2; exit 1; }
                  running=$(cat "$d/running"); pid=0
                  if [[ $running == true ]]; then pid=$(cat "$d/pid"); fi
                  jq -n --arg name "$(cat "$d/name")" --argjson running "$running" --argjson pid "$pid" --slurpfile mounts "$d/mounts.json" \
                    '[{Name: $name, State: {Running: $running, Pid: $pid}, Mounts: $mounts[0]}]'
                  exit 0
                fi
                format=$3; target=$4
                if [[ -e $S/inspect-fails-$target ]]; then echo "Error response from daemon: scripted failure" >&2; exit 1; fi
                d=$(find_container "$target") || { echo "Error response from daemon: No such container: $target" >&2; exit 1; }
                running=$(cat "$d/running")
                case "$format" in
                  '{{.State.Pid}}') if [[ $running == true ]]; then echo 2147483646; else echo 0; fi ;;
                  '{{.State.Running}} {{.State.Pid}}') if [[ $running == true ]]; then echo "true 2147483646"; else echo "false 0"; fi ;;
                  '{{.State.Running}}') cat "$d/running" ;;
                  '{{.Name}}') cat "$d/name" ;;
                  '{{range .Mounts}}{{println .Type (or .Name "-") .Source}}{{end}}')
                    jq -r '.[] | "\(.Type) \(.Name // "-") \(.Source)"' "$d/mounts.json" ;;
                  '{{.State.Health.Status}}') cat "$d/health" ;;
                  *) echo "unscripted format $format" >&2; exit 2 ;;
                esac
                exit 0 ;;
              stop)
                if [[ -e $S/stop-fails ]]; then echo "Error response from daemon: cannot stop container (scripted)" >&2; exit 1; fi
                d=$(find_container "${@: -1}") || exit 1
                echo false > "$d/running"
                for appears in "$S"/after-stop/*; do if [[ -d $appears ]]; then mv "$appears" "$S/containers/"; fi; done
                if [[ -f $S/rename-on-stop ]]; then IFS=$'\t' read -r from to < "$S/rename-on-stop"; mv "$from" "$to"; fi
                echo "${@: -1}"; exit 0 ;;
              rm)
                if [[ -e $S/rm-fails ]]; then echo "Error response from daemon: cannot remove container (scripted)" >&2; exit 1; fi
                d=$(find_container "$2") || exit 1
                rm -rf "$d"; echo "$2"; exit 0 ;;
              compose)
                if [[ " $* " != *" up --detach --no-deps silo "* ]]; then echo "unscripted compose call" >&2; exit 2; fi
                if [[ -e $S/up-fails ]]; then echo "compose up failed (scripted)" >&2; exit 1; fi
                new=$S/containers/silo-replacement; mkdir -p "$new"
                echo "/$FAKE_SILO" > "$new/name"; echo true > "$new/running"; echo healthy > "$new/health"; echo 2147483645 > "$new/pid"
                jq -n --arg source "$FAKE_DATA" '[{Type: "bind", Source: $source, Destination: "/data"}]' > "$new/mounts.json"
                if [[ -e $S/differ-after ]]; then echo '{"format":"changed"}' > "$S/objects/meta/fence.json@v2"; fi
                exit 0 ;;
              exec)
                if [[ $2 == -i ]]; then
                  IFS=$'\t' read -r key version
                  if [[ -f $S/read-fails && $key == meta/instance.v1.json ]]; then
                    when=$(cat "$S/read-fails")
                    if [[ ( $when == before && ! -d $S/containers/silo-replacement ) || ( $when == after && -d $S/containers/silo-replacement ) ]]; then
                      echo "mcli: read failed (scripted)" >&2; exit 1
                    fi
                  fi
                  cat "$S/objects/$key@$version"; exit 0
                fi
                while IFS=$'\t' read -r key version; do
                  printf '{"status":"success","type":"file","key":"%s","versionId":"%s","isLatest":true}\n' "$key" "$version"
                done < "$S/listing"
                exit 0 ;;
            esac
            echo "unscripted docker call: $*" >&2
            exit 2
            """;

        /// <summary>util-linux <c>findmnt</c>: this host's real mount table, with the bind mounts a test adds listed last.</summary>
        private const string FakeFindmnt = """
            #!/usr/bin/env bash
            set -u
            S=${FAKE_STATE:?}
            base='-J -l -o MAJ:MIN,FSROOT,TARGET'
            if [[ $* == "$base" ]]; then
              real=$(/usr/bin/findmnt -J -l -o MAJ:MIN,FSROOT,TARGET) || exit 1
              if [[ -f $S/mounts.json ]]; then
                jq --slurpfile extra "$S/mounts.json" '.filesystems += $extra[0]' <<< "$real"
              else
                printf '%s\n' "$real"
              fi
              exit 0
            fi
            # A container process's own table, which a test scripts by process ID: none overlapping unless given.
            if [[ $# == 6 && "$1 $2 $3 $4" == "$base" && $5 == --tab-file && $6 =~ ^/proc/([0-9]+)/mountinfo$ ]]; then
              pid=${BASH_REMATCH[1]}
              if [[ -f $S/live/$pid.exits ]]; then echo false > "$(cat "$S/live/$pid.exits")/running"; fi
              if [[ -e $S/live/$pid.unreadable || -f $S/live/$pid.exits ]]; then echo "findmnt: cannot open $6: Permission denied (scripted)" >&2; exit 1; fi
              if [[ -f $S/live/$pid.json ]]; then cat "$S/live/$pid.json"; else echo '{"filesystems": []}'; fi
              exit 0
            fi
            echo "unscripted findmnt call: $*" >&2
            exit 2
            """;

        /// <summary><c>hvo-sky-install cameraagent silo-verify</c>: the readiness the state names; not-ready exits 3.</summary>
        private const string FakeInstaller = """
            #!/usr/bin/env bash
            cat "$FAKE_STATE/verify.json"
            if [[ "$(jq -r .readiness "$FAKE_STATE/verify.json")" == not-ready ]]; then exit 3; fi
            exit 0
            """;
    }
}
