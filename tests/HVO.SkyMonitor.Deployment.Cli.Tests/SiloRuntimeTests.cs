using System.Text;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>The Docker-free contract of the stock Silo runtime profile and its installer surface (#1966).</summary>
[TestClass]
[TestCategory("Unit")]
public sealed partial class SiloRuntimeTests
{
    private static readonly string[] MetaDeniedActions = ["s3:PutObject", "s3:DeleteObject", "s3:DeleteObjectVersion"];

    private const string Ext4MountInfo = """
        22 1 8:1 / / rw,relatime shared:1 - ext4 /dev/sda1 rw
        30 22 0:25 / /proc rw - proc proc rw
        41 22 8:17 / /srv/nfs rw - nfs4 server:/export rw
        42 22 0:44 / /srv/overlay rw - overlay overlay rw
        43 22 259:2 / /mnt/nvme rw - ext4 /dev/nvme0n1p2 rw
        """;

    [TestMethod]
    public void Profile_PinsTheQualifiedReleaseByIndexAndPlatformDigests()
    {
        Assert.AreEqual("docker.io/pgsty/silo@sha256:c20d112390b9a9ce8c11b8af95ad35bcaa62f4759663c09c936cd8d2b511f329",
            SiloRuntimeProfile.ImageReference);
        Assert.AreEqual("sha256:6f48a60830eb101e19f955124914554d73c4a4721396d3d13d61634e1396eadf", SiloRuntimeProfile.PlatformDigestFor("amd64"));
        Assert.AreEqual("sha256:dad2a4bc279f9095d9835398dda266abd4f2f7486d0cb66a071951f1e4a6791d", SiloRuntimeProfile.PlatformDigestFor("arm64"));
        Assert.IsNull(SiloRuntimeProfile.PlatformDigestFor("riscv64"));
        // The distinct component schema and state contract are what every earlier reader refuses.
        Assert.IsFalse(new[] { "cameraagent-install-v1" }.Contains(SiloRuntimeProfile.ComponentSchemaVersion));
    }

    [TestMethod]
    public void ComposeService_IsPrivateHardenedAndHoldsTheRootCredentialAlone()
    {
        var service = SiloRuntimeProfile.ComposeService;
        var siloBlock = service[..service.IndexOf("\nnetworks:", StringComparison.Ordinal)];
        Assert.IsFalse(siloBlock.Contains("ports:", StringComparison.Ordinal), "The Silo listener publishes no host port.");
        StringAssert.Contains(service, "  silo:\n    internal: true", StringComparison.Ordinal);
        StringAssert.Contains(siloBlock, "MINIO_BROWSER: \"off\"", StringComparison.Ordinal);
        StringAssert.Contains(siloBlock, "MINIO_FS_OSYNC: \"on\"", StringComparison.Ordinal);
        StringAssert.Contains(siloBlock, "read_only: true", StringComparison.Ordinal);
        StringAssert.Contains(siloBlock, "cap_drop: [ALL]", StringComparison.Ordinal);
        StringAssert.Contains(siloBlock, "pull_policy: never", StringComparison.Ordinal);
        StringAssert.Contains(siloBlock, "/silo-root:/run/hvo-silo:ro", StringComparison.Ordinal);
        StringAssert.Contains(siloBlock, "${HVO_SILO_DATA_ROOT:?silo data root required}:/data", StringComparison.Ordinal);
        Assert.IsFalse(siloBlock.Contains("/secrets", StringComparison.Ordinal), "CameraAgent's settings never reach Silo.");
        StringAssert.Contains(SiloRuntimeProfile.CameraAgentNetworks, "silo: {}", StringComparison.Ordinal);
    }

    [TestMethod]
    public void CameraAgentSettings_NameTheStoreAndPauseButNeverTheRootCredential()
    {
        var instance = Guid.NewGuid();
        var firstBoot = Guid.NewGuid();
        var settings = SiloRuntimeProfile.CameraAgentSettings(instance, firstBoot);
        Assert.AreEqual(firstBoot.ToString("D"), settings["CameraAgent__FirstBootPause__Id"]);
        Assert.AreEqual(SiloRuntimeProfile.Endpoint, settings["CameraAgent__Silo__Endpoint"]);
        Assert.AreEqual($"hvo-{instance:N}", settings["CameraAgent__Silo__Bucket"]);
        Assert.AreEqual(SiloRuntimeProfile.StorageBindingIdFor(instance, SiloRuntimeProfile.StoreIdFor(instance), $"hvo-{instance:N}"),
            settings["CameraAgent__Silo__StorageBindingId"]);
        Assert.IsFalse(settings.Keys.Any(static key => key.Contains("Root", StringComparison.Ordinal) || key.Contains("Password", StringComparison.Ordinal)));
        // The binding names the store, never an endpoint, and differs per instance.
        Assert.AreNotEqual(settings["CameraAgent__Silo__StorageBindingId"],
            SiloRuntimeProfile.CameraAgentSettings(Guid.NewGuid(), firstBoot)["CameraAgent__Silo__StorageBindingId"]);
    }

    [TestMethod]
    public void WriterPolicy_GrantsObjectsAndExactVersionDeletesOnlyInItsBucket()
    {
        var policy = SiloRuntimeProfile.WriterPolicy("hvo-0123456789abcdef0123456789abcdef");
        StringAssert.Contains(policy, "s3:DeleteObjectVersion", StringComparison.Ordinal);
        // The binding is out of the writer's reach: no put, delete or version delete under meta/.
        using (var document = System.Text.Json.JsonDocument.Parse(policy))
        {
            var deny = document.RootElement.GetProperty("Statement").EnumerateArray()
                .Single(static statement => statement.GetProperty("Effect").GetString() == "Deny");
            CollectionAssert.AreEquivalent(
                MetaDeniedActions,
                deny.GetProperty("Action").EnumerateArray().Select(static action => action.GetString()).ToArray());
            Assert.AreEqual("arn:aws:s3:::hvo-0123456789abcdef0123456789abcdef/meta/*", deny.GetProperty("Resource")[0].GetString());
        }
        var allowed = policy[..policy.IndexOf("\"Deny\"", StringComparison.Ordinal)];
        foreach (var denied in new[] { "s3:DeleteObject\"", "s3:PutBucketVersioning", "s3:DeleteBucket", "s3:PutBucketPolicy", "admin:", "s3:*", "arn:aws:s3:::*" })
            Assert.IsFalse(allowed.Contains(denied, StringComparison.Ordinal), denied);
    }

    [TestMethod]
    public void MarkerAndFence_AreFixedBytesThatBindExactlyOneInstance()
    {
        var instance = Guid.NewGuid();
        var created = DateTimeOffset.Parse("2026-11-22T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var marker = SiloRuntimeProfile.Marker(instance, created);
        CollectionAssert.AreEqual(marker, SiloRuntimeProfile.Marker(instance, created));
        Assert.IsTrue(SiloRuntimeProfile.MarkerBinds(marker, instance));
        Assert.IsFalse(SiloRuntimeProfile.MarkerBinds(marker, Guid.NewGuid()));
        Assert.IsFalse(SiloRuntimeProfile.MarkerBinds(Encoding.UTF8.GetBytes("{\"format\":\"other\"}"), instance));
        var fence = Encoding.UTF8.GetString(SiloRuntimeProfile.Fence(instance, "AKIAEXAMPLEWRITER001", created));
        StringAssert.Contains(fence, "\"writerGeneration\":1", StringComparison.Ordinal);
        StringAssert.Contains(fence, "\"writerAccessKeyId\":\"AKIAEXAMPLEWRITER001\"", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Scripts_CarryNoRootSecretAndRefuseACredentialThatCouldLeaveItsQuotes()
    {
        var bucket = SiloRuntimeProfile.BucketFor(Guid.NewGuid());
        var create = SiloRuntimeProfile.ApplyBootstrapScript(bucket, 1, new string('a', 64), [], "create", []);
        StringAssert.Contains(create, "store-changed", StringComparison.Ordinal);
        StringAssert.Contains(create, "cat /run/hvo-silo/root-password", StringComparison.Ordinal);
        var bind = SiloRuntimeProfile.WriteBindingScript(bucket, "AKIAEXAMPLEWRITER001", "secretSECRET+/=0123456789",
            [1, 2, 3], [4, 5, 6]);
        StringAssert.Contains(bind, "If-None-Match: *", StringComparison.Ordinal);
        StringAssert.Contains(bind, "--version-id", StringComparison.Ordinal);
        Assert.ThrowsExactly<InstallerException>(() =>
            SiloRuntimeProfile.WriteBindingScript(bucket, "AKIA'; rm -rf /", "secret0123456789abc", [1], [2]));
        Assert.ThrowsExactly<InstallerException>(() =>
            SiloRuntimeProfile.WriteBindingScript(bucket, "AKIAEXAMPLEWRITER001", "secret'quote0123456", [1], [2]));
        Assert.ThrowsExactly<InstallerException>(() => SiloRuntimeProfile.InventoryStoreScript("../other"));
        // Only keys of the shape Silo generates are ever written into a script, so nothing else can leave its quotes.
        Assert.ThrowsExactly<InstallerException>(() => SiloRuntimeProfile.KeyInfoScript(["foreign-key'; rm -rf /"]));
        Assert.ThrowsExactly<InstallerException>(() =>
            SiloRuntimeProfile.ApplyBootstrapScript(bucket, 1, new string('a', 64), ["OWNPRIORKEY000000001"], "create", ["OTHERKEY000000000001"]));
    }

    [TestMethod]
    public void ScriptResult_RefusalReportsOnlyItsCodeAndNeverEchoesOutput()
    {
        var refused = Assert.ThrowsExactly<InstallerException>(() => SiloRuntimeProfile.ParseScriptResult(
            new ProcessResult(3, "{\"outcome\":\"refused\",\"reason\":\"store-not-empty\"}\n", string.Empty), "created", "bucket bootstrap"));
        Assert.IsTrue(refused.IsRefusal);
        StringAssert.Contains(refused.Message, "store-not-empty", StringComparison.Ordinal);
        var garbled = Assert.ThrowsExactly<InstallerException>(() => SiloRuntimeProfile.ParseScriptResult(
            new ProcessResult(1, "secretKey=THE-WRITER-SECRET not json", string.Empty), "created", "bucket bootstrap"));
        Assert.IsFalse(garbled.Message.Contains("THE-WRITER-SECRET", StringComparison.Ordinal));
        var unknown = Assert.ThrowsExactly<InstallerException>(() => SiloRuntimeProfile.ParseScriptResult(
            new ProcessResult(3, "{\"outcome\":\"refused\",\"reason\":\"<script>THE-WRITER-SECRET\"}", string.Empty), "created", "step"));
        Assert.IsFalse(unknown.Message.Contains("THE-WRITER-SECRET", StringComparison.Ordinal));
        var ok = SiloRuntimeProfile.ParseScriptResult(
            new ProcessResult(0, "noise\n{\"outcome\":\"created\",\"accessKey\":\"A\"}\n", string.Empty), "created", "step");
        Assert.AreEqual("A", ok.GetProperty("accessKey").GetString());
    }

    [TestMethod]
    [DataRow("/var/lib/hvo-sky/cameraagents/x/silo", "ext4", "8:1")]
    [DataRow("/mnt/nvme/hvo-sky/cameraagents/x/silo", "ext4", "259:2")]
    [DataRow("/srv/nfs/hvo/silo", "nfs4", "8:17")]
    [DataRow("/srv/overlay/silo", "overlay", "0:44")]
    public void MountOf_FindsTheLongestContainingMount(string path, string fileSystem, string device)
    {
        var mount = SiloRuntimeProfile.MountOf(path, Ext4MountInfo);
        Assert.AreEqual(fileSystem, mount.FileSystem);
        Assert.AreEqual(device, mount.Device);
    }

    [TestMethod]
    public void FreshDataRoot_IsAdmittedOnlyWhenAbsentAndOnLocalExt4()
    {
        var root = Directory.CreateTempSubdirectory("hvo-silo-root-");
        try
        {
            var absent = Path.Combine(root.FullName, "instance", "silo");
            var mountInfo = $"22 1 8:1 / / rw - ext4 /dev/sda1 rw\n50 22 0:60 / {root.FullName} rw - ext4 /dev/sdb1 rw\n";
            Assert.AreEqual(("ext4", "0:60"), SiloRuntimeProfile.AdmitFreshDataRoot(absent, mountInfo));

            var xfs = $"22 1 8:1 / / rw - ext4 /dev/sda1 rw\n50 22 0:60 / {root.FullName} rw - xfs /dev/sdb1 rw\n";
            Assert.IsTrue(Assert.ThrowsExactly<InstallerException>(() => SiloRuntimeProfile.AdmitFreshDataRoot(absent, xfs)).IsRefusal);

            Directory.CreateDirectory(absent);
            var existing = Assert.ThrowsExactly<InstallerException>(() => SiloRuntimeProfile.AdmitFreshDataRoot(absent, mountInfo));
            Assert.IsTrue(existing.IsRefusal);
            StringAssert.Contains(existing.Message, "already exists", StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void InstallRequest_AdmitsTheSiloLayoutOnlyWithInProcessReplayAndBindsItsIdentity()
    {
        var request = new InstallRequest { FriendlyName = "Silo camera", AllowTestProductRoot = true, ProductRoot = "/tmp/hvo-silo-request" };
        var filesystem = request with { StorageLayout = CameraAgentStorageLayout.Filesystem };
        var localRunner = Assert.ThrowsExactly<InstallUsageException>(() =>
            (request with { ReplayProfile = CameraAgentReplayProfile.LocalRunner }).Validate());
        StringAssert.Contains(localRunner.Message, "--storage-layout silo supports only", StringComparison.Ordinal);
        var instance = Guid.NewGuid();
        Assert.AreNotEqual(CameraAgentInstaller.ComputeRequestSha256(request, instance),
            CameraAgentInstaller.ComputeRequestSha256(filesystem, instance));
        Assert.AreEqual(CameraAgentStorageLayout.Silo, CommandLine.ParseStorageLayout("silo"));
        Assert.AreEqual(CameraAgentStorageLayout.Silo, CommandLine.ParseStorageLayout(null));
        Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseStorageLayout("s3"));
    }

    [TestMethod]
    [DataRow(null, true)]
    [DataRow(LifecycleOperationKind.Uninstall, true)]
    [DataRow(LifecycleOperationKind.Purge, true)]
    [DataRow(LifecycleOperationKind.Upgrade, true)]
    [DataRow(LifecycleOperationKind.Rollback, true)]
    [DataRow(LifecycleOperationKind.Reinstall, true)]
    [DataRow(LifecycleOperationKind.Backup, false)]
    [DataRow(LifecycleOperationKind.Restore, false)]
    [DataRow(LifecycleOperationKind.CatalogSelect, true)]
    [DataRow(LifecycleOperationKind.CatalogRollback, true)]
    public void LifecycleOperations_AdmitTheSiloLayoutOnlyWhereTheFirstReleaseSupportsIt(LifecycleOperationKind? operation, bool admitted)
        => Assert.AreEqual(admitted, CameraAgentLifecycleManager.AdmitsSiloLayout(operation));
}
