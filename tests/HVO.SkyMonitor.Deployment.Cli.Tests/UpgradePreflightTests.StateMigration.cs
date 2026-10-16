using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// What counts as a declared state migration (#1307): every persisted-state boundary label compared between the
/// installed image and the candidate, a boundary either image omits, and an initialized raw-ingress journal that is
/// not already at the candidate's schema.
/// </summary>
public sealed partial class UpgradePreflightTests
{
    private static readonly ImageInstallationIdentity DeclaringImage = new(
        "registry",
        $"cameraagent@sha256:{new string('b', 64)}",
        $"sha256:{new string('c', 64)}",
        "arm64",
        null,
        UpgradeCompatibility: CameraAgentStateContract.Current,
        MinimumCompatibleRevision: MinimumCompatibleRevision,
        IdentityMigration: CurrentIdentityMigration,
        RawIngressSchema: "15",
        CatalogManifestVersion: "2",
        RawIngressMigration: RawIngressV13Schema.RecoveryMigrationContract);

    private static readonly ImageInstallationIdentity DeclaringCandidate =
        DeclaringImage with { ImageId = $"sha256:{new string('d', 64)}", SourceRevision = new string('9', 40) };

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void StateMigration_UnchangedContractAndJournalAtTheCandidateSchema_DeclaresNoMigration()
    {
        using var fixture = new PreflightFixture();
        fixture.WriteRawIngressDatabase(schemaVersion: 15);

        var changes = CameraAgentStateMigration.DeclaredChanges(
            fixture.Paths, DeclaringImage, CameraAgentStateContract.Current, DeclaringCandidate);

        Assert.AreEqual(0, changes.Count, string.Join("; ", changes));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("state-compatibility", "backward-compatible", "cameraagent-state-v2")]
    [DataRow("minimum-compatible-revision", MinimumCompatibleRevision, "1111111111111111111111111111111111111111")]
    [DataRow("identity-migration", CurrentIdentityMigration, "20261016000000_NextIdentity")]
    [DataRow("raw-ingress-schema", "15", "16")]
    [DataRow("raw-ingress-migration", "canonical-v13-v14-to-v15", "canonical-v15-to-v16")]
    [DataRow("catalog-manifest-version", "2", "3")]
    public void StateMigration_EachChangedBoundaryLabel_IsNamedWithBothValues(string label, string installed, string candidate)
    {
        using var fixture = new PreflightFixture();
        var installedStateContract = CameraAgentStateContract.Current;
        var changedCandidate = DeclaringCandidate;
        switch (label)
        {
            // The installed side of the state contract is the instance's recorded contract, which the caller supplies.
            case "state-compatibility": installedStateContract = installed; break;
            case "minimum-compatible-revision": changedCandidate = changedCandidate with { MinimumCompatibleRevision = candidate }; break;
            case "identity-migration": changedCandidate = changedCandidate with { IdentityMigration = candidate }; break;
            case "raw-ingress-schema": changedCandidate = changedCandidate with { RawIngressSchema = candidate }; break;
            case "raw-ingress-migration": changedCandidate = changedCandidate with { RawIngressMigration = candidate }; break;
            case "catalog-manifest-version": changedCandidate = changedCandidate with { CatalogManifestVersion = candidate }; break;
            default: Assert.Fail($"Unknown label {label}."); break;
        }

        var changes = CameraAgentStateMigration.DeclaredChanges(
            fixture.Paths, DeclaringImage, installedStateContract, changedCandidate);

        AssertSingleChange(changes, $"io.hvo.skymonitor.{label} installed {installed}, candidate {candidate}");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("state-compatibility", true)]
    [DataRow("state-compatibility", false)]
    [DataRow("minimum-compatible-revision", true)]
    [DataRow("minimum-compatible-revision", false)]
    [DataRow("identity-migration", true)]
    [DataRow("identity-migration", false)]
    [DataRow("raw-ingress-schema", true)]
    [DataRow("raw-ingress-schema", false)]
    [DataRow("catalog-manifest-version", true)]
    [DataRow("catalog-manifest-version", false)]
    public void StateMigration_UndeclaredBoundary_CannotBeComparedAndCountsAsAMigration(string label, bool installedOmits)
    {
        using var fixture = new PreflightFixture();
        static ImageInstallationIdentity Omit(ImageInstallationIdentity image, string label) => label switch
        {
            "state-compatibility" => image with { UpgradeCompatibility = null },
            "minimum-compatible-revision" => image with { MinimumCompatibleRevision = string.Empty },
            "identity-migration" => image with { IdentityMigration = null },
            "raw-ingress-schema" => image with { RawIngressSchema = null },
            _ => image with { CatalogManifestVersion = null }
        };
        var installed = installedOmits ? Omit(DeclaringImage, label) : DeclaringImage;
        var candidate = installedOmits ? DeclaringCandidate : Omit(DeclaringCandidate, label);
        var installedStateContract = label == "state-compatibility" && installedOmits ? null : CameraAgentStateContract.Current;

        var changes = CameraAgentStateMigration.DeclaredChanges(fixture.Paths, installed, installedStateContract, candidate);

        Assert.AreEqual(1, changes.Count, string.Join("; ", changes));
        StringAssert.StartsWith(changes[0], $"io.hvo.skymonitor.{label} installed ", StringComparison.Ordinal);
        StringAssert.Contains(changes[0], installedOmits ? "installed none," : "candidate none", StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void StateMigration_RawIngressMigrationOmittedByBothImages_IsAgreementAndDeclaredByOneIsAChange()
    {
        using var fixture = new PreflightFixture();
        var installed = DeclaringImage with { RawIngressMigration = null };

        var agreed = CameraAgentStateMigration.DeclaredChanges(
            fixture.Paths, installed, CameraAgentStateContract.Current, DeclaringCandidate with { RawIngressMigration = string.Empty });
        var added = CameraAgentStateMigration.DeclaredChanges(
            fixture.Paths, installed, CameraAgentStateContract.Current, DeclaringCandidate);

        Assert.AreEqual(0, agreed.Count, string.Join("; ", agreed));
        AssertSingleChange(added, "io.hvo.skymonitor.raw-ingress-migration installed none, candidate canonical-v13-v14-to-v15");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(13)]
    [DataRow(14)]
    [DataRow(16)]
    public void StateMigration_InitializedJournalAtAnotherSchema_IsAMigrationEvenWhenTheLabelsAgree(int persisted)
    {
        using var fixture = new PreflightFixture();
        fixture.WriteRawIngressDatabase(persisted);

        var changes = CameraAgentStateMigration.DeclaredChanges(
            fixture.Paths, DeclaringImage, CameraAgentStateContract.Current, DeclaringCandidate);

        AssertSingleChange(changes, $"persisted raw-ingress journal schema {persisted}, candidate schema 15");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void StateMigration_AbsentOrFreshlyInitializedJournal_IsNotAMigration()
    {
        using var fixture = new PreflightFixture();
        var absent = CameraAgentStateMigration.DeclaredChanges(
            fixture.Paths, DeclaringImage, CameraAgentStateContract.Current, DeclaringCandidate);
        // The runtime initializes a journal it finds at user_version 0 with no schema objects itself.
        fixture.WriteRawIngressDatabase(schemaVersion: 0, createTable: false);
        var fresh = CameraAgentStateMigration.DeclaredChanges(
            fixture.Paths, DeclaringImage, CameraAgentStateContract.Current, DeclaringCandidate);

        Assert.AreEqual(0, absent.Count, string.Join("; ", absent));
        Assert.AreEqual(0, fresh.Count, string.Join("; ", fresh));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void StateMigration_UnreadableJournal_CannotBeComparedAndCountsAsAMigration()
    {
        using var fixture = new PreflightFixture();
        var path = CameraAgentStateLayout.RawIngressDatabasePath(fixture.Paths.StateRoot);
        SafeFileSystem.CreateOwnerDirectory(Path.Combine(fixture.Paths.StateRoot, CameraAgentStateLayout.RawDirectoryName));
        SafeFileSystem.CreateOwnerDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "this is not a SQLite database, and it is long enough to be read as a header.....");

        var changes = CameraAgentStateMigration.DeclaredChanges(
            fixture.Paths, DeclaringImage, CameraAgentStateContract.Current, DeclaringCandidate);

        Assert.AreEqual(1, changes.Count, string.Join("; ", changes));
        StringAssert.StartsWith(changes[0], "persisted raw-ingress journal unreadable (", StringComparison.Ordinal);
        StringAssert.EndsWith(changes[0], "), candidate schema 15", StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void StateMigration_InitializedJournalAgainstAnUnparseableCandidateSchema_CountsAsAMigration()
    {
        using var fixture = new PreflightFixture();
        fixture.WriteRawIngressDatabase(schemaVersion: 15);

        // Both images carry the same unparseable value, so the labels agree but the journal cannot be compared.
        var changes = CameraAgentStateMigration.DeclaredChanges(
            fixture.Paths,
            DeclaringImage with { RawIngressSchema = "fifteen" },
            CameraAgentStateContract.Current,
            DeclaringCandidate with { RawIngressSchema = "fifteen" });

        AssertSingleChange(changes, "persisted raw-ingress journal schema 15, candidate schema fifteen");
    }

    private static void AssertSingleChange(IReadOnlyList<string> changes, string expected)
    {
        Assert.AreEqual(1, changes.Count, string.Join("; ", changes));
        Assert.AreEqual(expected, changes[0]);
    }
}
