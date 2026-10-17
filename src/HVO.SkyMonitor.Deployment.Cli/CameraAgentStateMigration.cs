using System.Globalization;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// Decides whether an in-place upgrade candidate declares a migration of durable CameraAgent state, which is the
/// one case in which the upgrade snapshots the small databases before the candidate starts.
/// </summary>
/// <remarks>
/// A candidate that changes the state contract can leave the prior image facing state it cannot read, so a failed
/// candidate is restored onto that snapshot and a rollback of the committed upgrade puts it back. A candidate that
/// declares exactly the installed state contract is a pure swap and takes no snapshot.
/// </remarks>
internal static class CameraAgentStateMigration
{
    /// <summary>
    /// Names every way the candidate declares a state migration, or nothing when it declares none.
    /// </summary>
    /// <remarks>
    /// The candidate declares a migration when any persisted-state boundary label differs from the installed
    /// image's, when either image omits a boundary label the comparison needs, or when an initialized raw-ingress
    /// journal is not already at the candidate's schema. A boundary that cannot be compared counts as a migration.
    /// </remarks>
    public static IReadOnlyList<string> DeclaredChanges(
        InstallationPaths paths,
        ImageInstallationIdentity installed,
        string? installedStateContract,
        ImageInstallationIdentity candidate)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(candidate);
        var changes = new List<string>();
        CompareDeclared(changes, "state-compatibility", installedStateContract, candidate.UpgradeCompatibility);
        CompareDeclared(changes, "minimum-compatible-revision", installed.MinimumCompatibleRevision, candidate.MinimumCompatibleRevision);
        CompareDeclared(changes, "identity-migration", installed.IdentityMigration, candidate.IdentityMigration);
        CompareDeclared(changes, "raw-ingress-schema", installed.RawIngressSchema, candidate.RawIngressSchema);
        CompareDeclared(changes, "catalog-manifest-version", installed.CatalogManifestVersion, candidate.CatalogManifestVersion);
        // Only an image able to migrate an earlier raw journal declares this label, so two images omitting it agree.
        if (!string.Equals(Declared(installed.RawIngressMigration), Declared(candidate.RawIngressMigration), StringComparison.Ordinal))
        {
            changes.Add(Change("raw-ingress-migration", installed.RawIngressMigration, candidate.RawIngressMigration));
        }
        ComparePersistedRawIngress(changes, paths, candidate);
        return changes;
    }

    private static void CompareDeclared(List<string> changes, string label, string? installed, string? candidate)
    {
        if (Declared(installed) is null || Declared(candidate) is null ||
            !string.Equals(installed, candidate, StringComparison.Ordinal))
        {
            changes.Add(Change(label, installed, candidate));
        }
    }

    /// <summary>
    /// The installed image's label says what that image migrates the journal to, not what the journal holds. A
    /// journal still below the candidate's schema, for example one restored from an earlier backup, is migrated by
    /// the candidate on startup even when both images declare the same schema.
    /// </summary>
    private static void ComparePersistedRawIngress(
        List<string> changes, InstallationPaths paths, ImageInstallationIdentity candidate)
    {
        var databasePath = CameraAgentStateLayout.RawIngressDatabasePath(paths.StateRoot);
        if (!File.Exists(databasePath))
        {
            return;
        }
        long observed;
        long schemaObjects;
        try
        {
            (observed, schemaObjects) = CameraAgentStatePreflight.ReadRawIngressSchema(databasePath);
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            changes.Add($"persisted raw-ingress journal unreadable ({Redaction.SafeDiagnostic(exception.Message)}), candidate schema {Describe(candidate.RawIngressSchema)}");
            return;
        }
        // The runtime initializes a journal it finds at user_version 0 with no schema objects, so that is a fresh
        // start rather than a migration.
        if (observed == 0 && schemaObjects == 0)
        {
            return;
        }
        if (CameraAgentStateRequirements.From(candidate).RawIngressSchema != observed)
        {
            changes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"persisted raw-ingress journal schema {observed}, candidate schema {Describe(candidate.RawIngressSchema)}"));
        }
    }

    private static string Change(string label, string? installed, string? candidate)
        => $"io.hvo.skymonitor.{label} installed {Describe(installed)}, candidate {Describe(candidate)}";

    private static string? Declared(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string Describe(string? value) => Declared(value) ?? "none";
}
