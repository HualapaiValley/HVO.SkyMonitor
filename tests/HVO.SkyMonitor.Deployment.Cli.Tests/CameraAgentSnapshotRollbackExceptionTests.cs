using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// A store change the previous release cannot read ships only as a declared snapshot-rollback exception in the
/// previous-release fixture (#1314 R7, R13). This is the half of that rule that needs the installer: each exception
/// must name a state-boundary label whose value really differs between the frozen previous release
/// (<c>imageLabels</c>) and this release's Dockerfile, and that difference must be one the installer's own label reading
/// (<see cref="CameraAgentImageLabels"/>) and boundary comparison (<see cref="CameraAgentStateMigration.DeclaredChanges"/>)
/// report as a state migration, which is what makes an upgrade snapshot the stores and a rollback restore them.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentSnapshotRollbackExceptionTests
{
    private const string Store = "raw/journal/raw-ingress.db";
    private const string RawIngressSchema = "io.hvo.skymonitor.raw-ingress-schema";
    private const string RawIngressMigration = "io.hvo.skymonitor.raw-ingress-migration";
    private const string MinimumCompatibleRevision = "io.hvo.skymonitor.minimum-compatible-revision";

    /// <summary>
    /// The reader-capability labels a boundary test copies from the frozen release so that only the boundary under test
    /// and the raw-ingress transition remain. The raw-ingress schema and migration labels stay at the candidate's values:
    /// they are asserted to select checkpointing independently of the boundary under test.
    /// </summary>
    internal static IEnumerable<string> UnrelatedReaderCapabilityLabels => CameraAgentImageLabels.StateBoundaryLabels
        .Where(static label => label is not (MinimumCompatibleRevision or RawIngressSchema or RawIngressMigration));

    [TestMethod]
    public void EveryDeclaredException_IsALabelChangeTheInstallerSnapshotsFor()
    {
        var fixture = LoadFixture();
        var frozen = fixture["imageLabels"]!.AsObject();
        foreach (var label in CameraAgentImageLabels.StateBoundaryLabels)
            Assert.IsTrue(frozen.ContainsKey(label), $"The previous-release fixture does not freeze the '{label}' boundary.");

        Assert.IsEmpty(Validate(fixture, ReadDockerfileLabels()));
    }

    [TestMethod]
    [DataRow(RawIngressSchema, "16", "16", null, "does not make the installer take a snapshot", DisplayName = "an unchanged label")]
    [DataRow("io.hvo.skymonitor.component", "CameraAgent-previous", "CameraAgent-previous", null, "does not make the installer take a snapshot", DisplayName = "a label the installer does not compare")]
    [DataRow(RawIngressSchema, "14", "13", null, "not the one the frozen release declares", DisplayName = "a previous value the frozen release does not declare")]
    [DataRow(RawIngressSchema, "14", "14", "undeclared", "not the one this release declares", DisplayName = "a candidate value this release does not declare")]
    public void AnExceptionWithoutARealBoundaryChange_IsRefused(
        string label, string? frozenValue, string? previousValue, string? candidateValue, string expected)
    {
        ArgumentNullException.ThrowIfNull(label);
        var fixture = LoadFixture();
        var candidate = ReadDockerfileLabels();
        if (frozenValue is not null) fixture["imageLabels"]![label] = frozenValue;
        Declare(fixture, label,
            previousValue ?? fixture["imageLabels"]![label]!.GetValue<string>(),
            candidateValue ?? candidate[label]);

        var errors = Validate(fixture, candidate);

        Assert.HasCount(1, errors, string.Join("; ", errors));
        Assert.IsTrue(errors.Any(error => error.Contains(expected, StringComparison.Ordinal)), string.Join("; ", errors));
    }

    [TestMethod]
    public void AnExceptionWhoseLabelReallyChanges_IsAccepted()
    {
        // The positive control: the frozen release declared raw-ingress schema 14 and this release declares its own, so
        // the installer reports a migration and snapshots the stores.
        var fixture = LoadFixture();
        var candidate = ReadDockerfileLabels();
        fixture["imageLabels"]![RawIngressSchema] = "14";
        Declare(fixture, RawIngressSchema, "14", candidate[RawIngressSchema]);

        Assert.IsEmpty(Validate(fixture, candidate));
    }

    [TestMethod]
    [DataRow(false, 1)]
    [DataRow(true, 0)]
    public void SchemaThreeEnvironmentalUpgradeDeclaresTheCheckpointBoundary(bool revertBoundary, int expectedMigrations)
    {
        var previous = ReadSchemaThreeImageLabels();
        var candidate = ReadDockerfileLabels();
        const string boundary = "io.hvo.skymonitor.minimum-compatible-revision";
        // Isolate the environmental checkpoint trigger from unrelated reader-capability upgrades.
        foreach (var label in UnrelatedReaderCapabilityLabels)
            candidate[label] = previous[label];
        if (revertBoundary) candidate[boundary] = previous[boundary];
        var paths = InstallationPaths.Create(Path.Combine(Path.GetTempPath(), "hvo-environment-boundary-" + Guid.NewGuid().ToString("N")),
            Guid.NewGuid(), "catalog");
        var installedIdentity = CameraAgentImageLabels.Identity("registry", "previous", $"sha256:{new string('1', 64)}", "amd64", null,
            JsonSerializer.SerializeToElement(previous));
        var candidateIdentity = CameraAgentImageLabels.Identity("registry", "candidate", $"sha256:{new string('2', 64)}", "amd64", null,
            JsonSerializer.SerializeToElement(candidate));
        var changes = CameraAgentStateMigration.DeclaredChanges(paths, installedIdentity, installedIdentity.UpgradeCompatibility,
            candidateIdentity, Path.Combine(paths.StateRoot, "absent", "raw-ingress.db"));
        Console.WriteLine($"Declared environmental migrations: {changes.Count}; checkpoint required: {changes.Count > 0}.");
        var environmentalChanges = changes.Where(change => change.StartsWith(boundary + " installed ", StringComparison.Ordinal)).ToArray();
        Assert.AreEqual(expectedMigrations, environmentalChanges.Length);
        Assert.AreEqual(2 + expectedMigrations, changes.Count,
            "The raw schema and migration labels independently select checkpointing even when the environmental boundary is unchanged.");
        foreach (var rawBoundary in new[] { RawIngressSchema, "io.hvo.skymonitor.raw-ingress-migration" })
            Assert.IsTrue(changes.Any(change => change.StartsWith(rawBoundary + " installed ", StringComparison.Ordinal)));
        if (!revertBoundary)
        {
            StringAssert.StartsWith(environmentalChanges.Single(), boundary + " installed " + previous[boundary] + ", candidate ", StringComparison.Ordinal);
            var exception = LoadFixture()["snapshotRollbackExceptions"]!.AsArray().Single(item =>
                item!["location"]!.GetValue<string>() == "raw/.environment/environmental-observation-outbox.db")!;
            Assert.AreEqual("5f5d253dd38308b8e62be9a1e32194dd675d0b60", exception["previousImageRevision"]!.GetValue<string>());
            Assert.AreEqual(previous[boundary], exception["previousValue"]!.GetValue<string>());
            Assert.AreEqual(candidate[boundary], exception["candidateValue"]!.GetValue<string>());
            CollectionAssert.AreEquivalent(previous.ToArray(), exception["previousImageLabels"]!.AsObject()
                .ToDictionary(static label => label.Key, static label => label.Value!.GetValue<string>(), StringComparer.Ordinal).ToArray());
        }
    }

    [TestMethod]
    [DataRow(false, 1)]
    [DataRow(true, 0)]
    public void ManifestLedgerUpgradeDeclaresTheCheckpointBoundary(bool revertBoundary, int expectedMigrations)
    {
        var exception = LoadFixture()["snapshotRollbackExceptions"]!.AsArray().Single(item =>
            item!["location"]!.GetValue<string>() == Store &&
            item["stateBoundaryLabel"]!.GetValue<string>() == "io.hvo.skymonitor.minimum-compatible-revision")!;
        var previous = exception["previousImageLabels"]!.AsObject().ToDictionary(
            static label => label.Key, static label => label.Value!.GetValue<string>(), StringComparer.Ordinal);
        var candidate = ReadDockerfileLabels();
        const string boundary = "io.hvo.skymonitor.minimum-compatible-revision";
        Assert.AreEqual("ead49e46a3705a507f7a08aa5c8a2a46dcb58e3c", previous[boundary]);
        Assert.AreEqual("4293871b7c4cf38ce0e0e4ea969aa9a6de057eea", candidate[boundary]);
        Assert.AreEqual("5f5d253dd38308b8e62be9a1e32194dd675d0b60", exception["previousImageRevision"]!.GetValue<string>());
        CollectionAssert.AreEquivalent(ReadSchemaThreeImageLabels().ToArray(), previous.ToArray());
        // Isolate the processing-ledger checkpoint trigger from unrelated reader-capability upgrades.
        foreach (var label in UnrelatedReaderCapabilityLabels)
            candidate[label] = previous[label];
        if (revertBoundary) candidate[boundary] = previous[boundary];
        var paths = InstallationPaths.Create(Path.Combine(Path.GetTempPath(), "hvo-manifest-boundary-" + Guid.NewGuid().ToString("N")), Guid.NewGuid(), "catalog");
        var installed = CameraAgentImageLabels.Identity("registry", "previous", $"sha256:{new string('1', 64)}", "amd64", null, JsonSerializer.SerializeToElement(previous));
        var target = CameraAgentImageLabels.Identity("registry", "candidate", $"sha256:{new string('2', 64)}", "amd64", null, JsonSerializer.SerializeToElement(candidate));
        var changes = CameraAgentStateMigration.DeclaredChanges(paths, installed, installed.UpgradeCompatibility, target,
            Path.Combine(paths.StateRoot, "absent", "raw-ingress.db"));
        var ledgerChanges = changes.Where(change => change.StartsWith(boundary + " installed ", StringComparison.Ordinal)).ToArray();
        Assert.AreEqual(expectedMigrations, ledgerChanges.Length);
        Assert.AreEqual(2 + expectedMigrations, changes.Count,
            "The independent raw schema and migration transitions still require a checkpoint.");
        foreach (var rawBoundary in new[] { RawIngressSchema, "io.hvo.skymonitor.raw-ingress-migration" })
            Assert.IsTrue(changes.Any(change => change.StartsWith(rawBoundary + " installed ", StringComparison.Ordinal)));
        if (!revertBoundary) StringAssert.StartsWith(ledgerChanges.Single(), boundary + " installed " + previous[boundary] + ", candidate " + candidate[boundary], StringComparison.Ordinal);
    }

    internal static Dictionary<string, string> ReadSchemaThreeImageLabels()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "environmental-schema3-image-labels.json")));
        Assert.AreEqual("5f5d253dd38308b8e62be9a1e32194dd675d0b60", document.RootElement.GetProperty("sourceRevision").GetString());
        var labels = document.RootElement.GetProperty("imageLabels").EnumerateObject()
            .ToDictionary(static label => label.Name, static label => label.Value.GetString()!, StringComparer.Ordinal);
        using var contracts = JsonDocument.Parse(labels["io.hvo.skymonitor.state-contracts"]);
        Assert.AreEqual(3, contracts.RootElement.GetProperty("environmentalOutbox").GetProperty("writes").GetInt32());
        Assert.AreEqual("ead49e46a3705a507f7a08aa5c8a2a46dcb58e3c", labels["io.hvo.skymonitor.minimum-compatible-revision"]);
        return labels;
    }

    /// <summary>Every reason a declared exception would not make the installer snapshot the stores.</summary>
    internal static List<string> Validate(JsonObject fixture, IReadOnlyDictionary<string, string> candidateLabels)
    {
        var errors = new List<string>();
        var frozen = fixture["imageLabels"]!.AsObject()
            .ToDictionary(static label => label.Key, static label => label.Value!.GetValue<string>(), StringComparer.Ordinal);
        var locations = fixture["stores"]!.AsArray()
            .Select(static store => store!["location"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        var root = Path.Combine(Path.GetTempPath(), $"hvo-exception-{Guid.NewGuid():N}");
        var paths = InstallationPaths.Create(root, Guid.NewGuid(), "catalog");
        var candidate = CameraAgentImageLabels.Identity(
            "registry", "candidate", $"sha256:{new string('2', 64)}", "amd64", null, JsonSerializer.SerializeToElement(candidateLabels));
        foreach (var exception in fixture["snapshotRollbackExceptions"]!.AsArray())
        {
            var previous = exception!["previousImageLabels"] is JsonObject labels
                ? labels.ToDictionary(static label => label.Key, static label => label.Value!.GetValue<string>(), StringComparer.Ordinal)
                : frozen;
            var installed = CameraAgentImageLabels.Identity(
                "registry", "previous", $"sha256:{new string('1', 64)}", "amd64", null, JsonSerializer.SerializeToElement(previous));
            // No persisted journal: only this exception's actual installed-image boundaries decide.
            var changes = CameraAgentStateMigration.DeclaredChanges(
                paths, installed, installed.UpgradeCompatibility, candidate, Path.Combine(root, "absent", "raw-ingress.db"));
            var location = exception["location"]!.GetValue<string>();
            var label = exception["stateBoundaryLabel"]!.GetValue<string>();
            var previousValue = exception["previousValue"]?.GetValue<string>();
            var candidateValue = exception["candidateValue"]?.GetValue<string>();
            if (!locations.Contains(location))
                errors.Add($"{location}: not a previous-release store");
            if (!string.Equals(previous.GetValueOrDefault(label), previousValue, StringComparison.Ordinal))
                errors.Add($"{location} {label}: previous value '{previousValue}' is not the one the frozen release declares");
            if (!string.Equals(candidateLabels.GetValueOrDefault(label), candidateValue, StringComparison.Ordinal))
                errors.Add($"{location} {label}: candidate value '{candidateValue}' is not the one this release declares");
            if (!changes.Any(change => change.StartsWith(label + " installed ", StringComparison.Ordinal)))
                errors.Add($"{location} {label}: the change does not make the installer take a snapshot");
        }
        return errors;
    }

    private static void Declare(JsonObject fixture, string label, string previousValue, string candidateValue)
    {
        var exceptions = fixture["snapshotRollbackExceptions"]!.AsArray();
        // Isolate the synthetic raw-store boundary under test from the real raw-schema transition.
        for (var index = exceptions.Count - 1; index >= 0; index--)
            if (exceptions[index]!["location"]!.GetValue<string>() == Store &&
                exceptions[index]!["stateBoundaryLabel"]!.GetValue<string>() == RawIngressSchema)
                exceptions.RemoveAt(index);
        exceptions.Add(new JsonObject
        {
            ["location"] = Store,
            ["objects"] = new JsonArray("table:processing_outputs"),
            ["stateBoundaryLabel"] = label,
            ["previousValue"] = previousValue,
            ["candidateValue"] = candidateValue
        });
    }

    private static JsonObject LoadFixture()
        => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "previous-release", "manifest.json")))!.AsObject();

    /// <summary>The labels this release's CameraAgent image declares.</summary>
    internal static Dictionary<string, string> ReadDockerfileLabels()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
            directory = directory.Parent;
        var dockerfile = File.ReadAllText(Path.Combine(
            directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found."),
            "src", "HVO.SkyMonitor.CameraAgent", "Dockerfile"));
        var labels = Regex.Matches(
                dockerfile, "^\\s*(?:LABEL\\s+)?(io\\.hvo\\.skymonitor\\.[a-z-]+)=\"((?:[^\"\\\\]|\\\\.)*)\"",
                RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5))
            .ToDictionary(static match => match.Groups[1].Value, static match => JsonSerializer.Deserialize<string>("\"" + match.Groups[2].Value + "\"")!, StringComparer.Ordinal);
        Assert.IsGreaterThanOrEqualTo(8, labels.Count, $"expected the full CameraAgent label set; found {labels.Count}");
        return labels;
    }
}
