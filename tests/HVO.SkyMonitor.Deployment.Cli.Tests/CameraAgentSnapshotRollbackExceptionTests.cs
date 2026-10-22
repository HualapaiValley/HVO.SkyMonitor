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
    [DataRow(RawIngressSchema, null, null, null, "does not make the installer take a snapshot", DisplayName = "an unchanged label")]
    [DataRow("io.hvo.skymonitor.component", "CameraAgent-previous", "CameraAgent-previous", null, "does not make the installer take a snapshot", DisplayName = "a label the installer does not compare")]
    [DataRow(RawIngressSchema, "14", "13", null, "not the one the frozen release declares", DisplayName = "a previous value the frozen release does not declare")]
    [DataRow(RawIngressSchema, "14", "14", "16", "not the one this release declares", DisplayName = "a candidate value this release does not declare")]
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
        var installed = CameraAgentImageLabels.Identity(
            "registry", "previous", $"sha256:{new string('1', 64)}", "amd64", null, JsonSerializer.SerializeToElement(frozen));
        var candidate = CameraAgentImageLabels.Identity(
            "registry", "candidate", $"sha256:{new string('2', 64)}", "amd64", null, JsonSerializer.SerializeToElement(candidateLabels));
        // No persisted journal: only the declared boundaries decide.
        var changes = CameraAgentStateMigration.DeclaredChanges(
            paths, installed, installed.UpgradeCompatibility, candidate, Path.Combine(root, "absent", "raw-ingress.db"));
        foreach (var exception in fixture["snapshotRollbackExceptions"]!.AsArray())
        {
            var location = exception!["location"]!.GetValue<string>();
            var label = exception["stateBoundaryLabel"]!.GetValue<string>();
            var previousValue = exception["previousValue"]?.GetValue<string>();
            var candidateValue = exception["candidateValue"]?.GetValue<string>();
            if (!locations.Contains(location))
                errors.Add($"{location}: not a previous-release store");
            if (!string.Equals(frozen.GetValueOrDefault(label), previousValue, StringComparison.Ordinal))
                errors.Add($"{location} {label}: previous value '{previousValue}' is not the one the frozen release declares");
            if (!string.Equals(candidateLabels.GetValueOrDefault(label), candidateValue, StringComparison.Ordinal))
                errors.Add($"{location} {label}: candidate value '{candidateValue}' is not the one this release declares");
            if (!changes.Any(change => change.StartsWith(label + " installed ", StringComparison.Ordinal)))
                errors.Add($"{location} {label}: the change does not make the installer take a snapshot");
        }
        return errors;
    }

    private static void Declare(JsonObject fixture, string label, string previousValue, string candidateValue)
        => fixture["snapshotRollbackExceptions"]!.AsArray().Add(new JsonObject
        {
            ["location"] = Store,
            ["objects"] = new JsonArray("table:processing_outputs"),
            ["stateBoundaryLabel"] = label,
            ["previousValue"] = previousValue,
            ["candidateValue"] = candidateValue
        });

    private static JsonObject LoadFixture()
        => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "previous-release", "manifest.json")))!.AsObject();

    /// <summary>The labels this release's CameraAgent image declares.</summary>
    private static Dictionary<string, string> ReadDockerfileLabels()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
            directory = directory.Parent;
        var dockerfile = File.ReadAllText(Path.Combine(
            directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found."),
            "src", "HVO.SkyMonitor.CameraAgent", "Dockerfile"));
        var labels = Regex.Matches(
                dockerfile, "^\\s*(?:LABEL\\s+)?(io\\.hvo\\.skymonitor\\.[a-z-]+)=\"([^\"]*)\"",
                RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5))
            .ToDictionary(static match => match.Groups[1].Value, static match => match.Groups[2].Value, StringComparer.Ordinal);
        Assert.IsGreaterThanOrEqualTo(8, labels.Count, $"expected the full CameraAgent label set; found {labels.Count}");
        return labels;
    }
}
