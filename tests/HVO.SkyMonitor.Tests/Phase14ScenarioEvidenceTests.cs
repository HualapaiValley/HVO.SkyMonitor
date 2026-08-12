using System.Runtime.Versioning;
using System.Text.Json;
using HVO.SkyMonitor.TestSupport;

namespace HVO.SkyMonitor.Tests;

[TestClass]
[DoNotParallelize]
public sealed class Phase14ScenarioEvidenceTests
{
    private const string Revision = "2c4ca26bd80e57cf9deb9aab17ac2aa8c9f4bcb5";
    private const string Tree = "0123456789abcdef0123456789abcdef01234567";
    private string? originalRoot;
    private string? originalAllowedRoot;
    private string? originalRevision;
    private string? originalTree;
    private string? originalObservationPrefix;
    private string? temporaryRoot;

    [TestInitialize]
    public void Initialize()
    {
        originalRoot = Environment.GetEnvironmentVariable("HVO_PHASE14_EVIDENCE_ROOT");
        originalAllowedRoot = Environment.GetEnvironmentVariable("HVO_PHASE14_EVIDENCE_ALLOWED_ROOT");
        originalRevision = Environment.GetEnvironmentVariable("HVO_PHASE14_SOURCE_REVISION");
        originalTree = Environment.GetEnvironmentVariable("HVO_PHASE14_SOURCE_TREE");
        originalObservationPrefix = Environment.GetEnvironmentVariable("HVO_PHASE14_OBSERVATION_PREFIX");
        Environment.SetEnvironmentVariable("HVO_PHASE14_EVIDENCE_ROOT", null);
        Environment.SetEnvironmentVariable("HVO_PHASE14_EVIDENCE_ALLOWED_ROOT", null);
        Environment.SetEnvironmentVariable("HVO_PHASE14_SOURCE_REVISION", null);
        Environment.SetEnvironmentVariable("HVO_PHASE14_SOURCE_TREE", null);
        Environment.SetEnvironmentVariable("HVO_PHASE14_OBSERVATION_PREFIX", null);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable("HVO_PHASE14_EVIDENCE_ROOT", originalRoot);
        Environment.SetEnvironmentVariable("HVO_PHASE14_EVIDENCE_ALLOWED_ROOT", originalAllowedRoot);
        Environment.SetEnvironmentVariable("HVO_PHASE14_SOURCE_REVISION", originalRevision);
        Environment.SetEnvironmentVariable("HVO_PHASE14_SOURCE_TREE", originalTree);
        Environment.SetEnvironmentVariable("HVO_PHASE14_OBSERVATION_PREFIX", originalObservationPrefix);
        if (temporaryRoot is not null)
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RecordAsync_WhenDisabled_IsInert()
    {
        Environment.SetEnvironmentVariable("HVO_PHASE14_OBSERVATION_PREFIX", "INVALID PREFIX");
        await Phase14ScenarioEvidence.RecordAsync("INVALID SCENARIO", "", "INVALID SELECTOR!", []);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RecordAsync_WhenEnabled_PrefixesNormalizedObservation()
    {
        var (_, _, fragments) = PrepareDirectories();
        Environment.SetEnvironmentVariable("HVO_PHASE14_OBSERVATION_PREFIX", "trial-2");

        await Phase14ScenarioEvidence.RecordAsync(
            "edge-upload-retry", "FaultPoint True", null, ["passed"]);

        var path = Directory.GetFiles(fragments, "*.json").Single();
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        Assert.AreEqual("trial-2-fault-point-true", document.RootElement.GetProperty("observationId").GetString());
        var expectedHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("trial-2-fault-point-true")));
        Assert.AreEqual($"edge-upload-retry--{expectedHash}.json", Path.GetFileName(path));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RecordAsync_WhenEnabled_RejectsInvalidPrefix()
    {
        PrepareDirectories();
        Environment.SetEnvironmentVariable("HVO_PHASE14_OBSERVATION_PREFIX", "Trial 2");

        var exception = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => Phase14ScenarioEvidence.RecordAsync("edge-upload-retry", "retry", null, ["passed"]));

        Assert.AreEqual("HVO_PHASE14_OBSERVATION_PREFIX", exception.ParamName);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RecordAsync_NormalizesLabelsAndPublishesPrivateFragment()
    {
        var (_, root, fragments) = PrepareDirectories();

        await Phase14ScenarioEvidence.RecordAsync(
            "edge-upload-retry",
            "FaultPoint True",
            "FaultPoint.True_1",
            ["Artifact Uploaded", "ChecksumSHA256Matches"],
            [new Phase14EvidenceMeasurement("ElapsedMilliseconds", 12, "milliseconds")]);

        var paths = Directory.GetFiles(fragments, "*.json");
        Assert.HasCount(1, paths);
        var path = paths[0];
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        var evidence = document.RootElement;
        Assert.AreEqual("fault-point-true", evidence.GetProperty("observationId").GetString());
        Assert.AreEqual("FaultPoint.True_1", evidence.GetProperty("caseSelector").GetString());
        CollectionAssert.AreEqual(
            new[] { "artifact-uploaded", "checksum-sha256-matches" },
            evidence.GetProperty("assertions").EnumerateArray()
                .Select(static assertion => assertion.GetProperty("id").GetString())
                .ToArray());
        Assert.AreEqual(
            "elapsed-milliseconds",
            evidence.GetProperty("measurements")[0].GetProperty("id").GetString());
        Assert.AreEqual(Revision, evidence.GetProperty("sourceRevision").GetString());
        Assert.AreEqual(Tree, evidence.GetProperty("sourceTree").GetString());
        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.AreEqual(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(root));
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RecordAsync_RejectsInvalidCaseSelector()
    {
        PrepareDirectories();

        var exception = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => Phase14ScenarioEvidence.RecordAsync(
                "edge-upload-retry", "retry", "invalid selector", ["passed"]));

        Assert.AreEqual("caseSelector", exception.ParamName);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RecordAsync_RejectsRootOutsideAllowedDirectory()
    {
        var (allowedRoot, _, _) = PrepareDirectories();
        var outsideRoot = Path.Combine(temporaryRoot!, "outside");
        CreatePrivateDirectory(outsideRoot);
        CreatePrivateDirectory(Path.Combine(outsideRoot, "fragments"));
        Environment.SetEnvironmentVariable("HVO_PHASE14_EVIDENCE_ROOT", outsideRoot);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => Phase14ScenarioEvidence.RecordAsync("edge-upload-retry", "retry", null, ["passed"]));

        StringAssert.Contains(exception.Message, "must be a child");
        Assert.AreEqual(allowedRoot, Environment.GetEnvironmentVariable("HVO_PHASE14_EVIDENCE_ALLOWED_ROOT"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    [SupportedOSPlatform("linux")]
    public async Task RecordAsync_RejectsSymbolicLinkFragmentsDirectory()
    {
        var (allowedRoot, root, fragments) = PrepareDirectories();
        Directory.Delete(fragments);
        var target = Path.Combine(temporaryRoot!, "target");
        CreatePrivateDirectory(target);
        Directory.CreateSymbolicLink(fragments, target);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => Phase14ScenarioEvidence.RecordAsync("edge-upload-retry", "retry", null, ["passed"]));

        StringAssert.Contains(exception.Message, "pre-created regular directories");
        Directory.Delete(fragments);
        Assert.AreEqual(allowedRoot, Environment.GetEnvironmentVariable("HVO_PHASE14_EVIDENCE_ALLOWED_ROOT"));
        Assert.AreEqual(root, Environment.GetEnvironmentVariable("HVO_PHASE14_EVIDENCE_ROOT"));
    }

    private (string AllowedRoot, string Root, string Fragments) PrepareDirectories()
    {
        temporaryRoot = Path.Combine(Path.GetTempPath(), $"hvo-phase14-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        var allowedRoot = Path.Combine(temporaryRoot, "allowed");
        var root = Path.Combine(allowedRoot, "run");
        var fragments = Path.Combine(root, "fragments");
        CreatePrivateDirectory(allowedRoot);
        CreatePrivateDirectory(root);
        CreatePrivateDirectory(fragments);
        Environment.SetEnvironmentVariable("HVO_PHASE14_EVIDENCE_ALLOWED_ROOT", allowedRoot);
        Environment.SetEnvironmentVariable("HVO_PHASE14_EVIDENCE_ROOT", root);
        Environment.SetEnvironmentVariable("HVO_PHASE14_SOURCE_REVISION", Revision);
        Environment.SetEnvironmentVariable("HVO_PHASE14_SOURCE_TREE", Tree);
        return (allowedRoot, root, fragments);
    }

    private static void CreatePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
