using System.Globalization;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;
using ContractReplayProfile = HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// Whether the evaluated image must declare the current durable state contract. A rollback target installed
/// before the label correction declares the superseded unbounded value and is admitted as a known contract.
/// </summary>
internal enum CameraAgentStateContractPolicy
{
    RequireCurrent,
    AllowLegacy
}

internal sealed record CameraAgentStatePreflightFinding(
    string Code,
    string Boundary,
    bool Blocking,
    string Path,
    string? Observed,
    string? Expected,
    string Remediation);

internal sealed record CameraAgentStatePreflightReport(
    int SchemaVersion,
    string Outcome,
    Guid InstanceId,
    string InstanceRoot,
    string? CandidateImageId,
    string? CandidateRelease,
    string CandidateStateContract,
    string? MinimumCompatibleRevision,
    string InstalledStateContract,
    bool Compatible,
    IReadOnlyList<CameraAgentStatePreflightFinding> Findings,
    DateTimeOffset EvaluatedUtc);

/// <summary>
/// The contract identities every canonical CameraAgent image declares and an upgrade requires the candidate to
/// match. Shared by the upgrade's contract-identity gate and the signed-release preflight so the two cannot drift.
/// </summary>
internal static class CameraAgentImageContract
{
    public const string Component = "CameraAgent";
    public const string CatalogContract = "hyg-v42-production-p3-s2";
    public const string ReplayRunnerContract = "local-replay-runner-v1";
}

/// <summary>
/// The contract identities a candidate declares, taken from a signed release's image identity and compatibility
/// record. These are the values the upgrade's contract-identity gate compares against the instance, so a
/// read-only preflight can reach that gate's verdict without loading the image. The loaded image's label
/// agreement with the signed record remains an upgrade-time check.
/// </summary>
internal sealed record CameraAgentContractIdentity(
    string? Component,
    string? ConfigurationContract,
    string? CatalogContract,
    string? ReplayRunnerContract)
{
    public static CameraAgentContractIdentity From(DistributionImageIdentity image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return new(
            image.Component,
            image.Compatibility.ConfigurationContract,
            image.Compatibility.CatalogContract,
            image.Compatibility.ReplayRunnerContract);
    }
}

/// <summary>
/// The persisted-state boundaries a candidate CameraAgent image declares through its OCI labels.
/// A boundary the image does not declare cannot be checked and is reported rather than assumed compatible.
/// </summary>
internal sealed record CameraAgentStateRequirements(
    string? StateContract,
    string? MinimumCompatibleRevision,
    string? IdentityMigration,
    int? RawIngressSchema,
    int? CatalogManifestVersion)
{
    public static CameraAgentStateRequirements From(ImageInstallationIdentity image) => new(
        image.UpgradeCompatibility,
        image.MinimumCompatibleRevision,
        image.IdentityMigration,
        ParseVersion(image.RawIngressSchema),
        ParseVersion(image.CatalogManifestVersion));

    /// <summary>
    /// The boundaries a signed image release declares. These are exactly the label values an upgrade requires the
    /// prepared image to carry, so evaluating persisted state against them reaches the same verdict the upgrade's
    /// own in-flight state preflight will reach, without a local copy of the image. The upgrade's separate
    /// contract-identity and label-agreement gates are not evaluated here.
    /// </summary>
    public static CameraAgentStateRequirements From(DistributionImageCompatibility compatibility)
    {
        ArgumentNullException.ThrowIfNull(compatibility);
        return new(
            compatibility.StateContract,
            compatibility.MinimumCompatibleRevision,
            compatibility.IdentityMigration,
            compatibility.RawIngressSchema,
            compatibility.CatalogManifestVersion);
    }

    private static int? ParseVersion(string? value)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
}

/// <summary>
/// Evaluates every persisted CameraAgent state boundary against a candidate image before Compose starts the
/// container, so an incompatible upgrade fails once with a consolidated report instead of through restart loops.
/// </summary>
internal static class CameraAgentStatePreflight
{
    public const int SchemaVersion = DeploymentSchemaVersions.StatePreflightReport;

    /// <summary>
    /// Runs after the main database has been copied for inspection and before its recovery files are. That is
    /// the only window in which a writer this preflight cannot see can commit and leave the two halves of the
    /// copy describing different generations, and a race that narrow cannot be won reliably from outside. A
    /// test installs a commit here to prove such a copy is discarded rather than replayed; production leaves
    /// it empty and pays one delegate call per inspected database.
    /// </summary>
    internal static Action SnapshotCopyBarrier { get; set; } = static () => { };

    /// <summary>Deletes and reports nothing; every check is read-only.</summary>
    public static CameraAgentStatePreflightReport Evaluate(
        InstallationPaths paths,
        Guid instanceId,
        string? candidateImageId,
        string? candidateRelease,
        CameraAgentStateRequirements requirements,
        string? installedStateContract,
        uint uid,
        uint gid,
        ContractReplayProfile replayProfile,
        CameraAgentStateContractPolicy contractPolicy = CameraAgentStateContractPolicy.RequireCurrent,
        CameraAgentContractIdentity? candidateContracts = null,
        string? instanceConfigurationContract = null,
        string? installedImageId = null,
        string? signedOfflineArchiveImageId = null,
        string? signedPlatformManifestDigest = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(requirements);
        var findings = new List<CameraAgentStatePreflightFinding>();
        EvaluateStateContract(findings, requirements, installedStateContract, contractPolicy);
        if (candidateContracts is not null)
        {
            EvaluateContractIdentity(findings, candidateContracts, instanceConfigurationContract, replayProfile);
        }
        EvaluateCandidateImageIdentity(
            findings, installedImageId, signedOfflineArchiveImageId, signedPlatformManifestDigest);
        EvaluateCatalog(findings, paths, requirements);
        EvaluateIdentityLineage(findings, paths, requirements);
        EvaluateRawIngressSchema(findings, paths, requirements);
        EvaluateBindSources(findings, paths, uid, gid, replayProfile);
        var compatible = !findings.Any(static finding => finding.Blocking);
        return new CameraAgentStatePreflightReport(
            SchemaVersion,
            compatible ? "compatible" : "incompatible",
            instanceId,
            paths.InstanceRoot,
            candidateImageId,
            candidateRelease,
            CameraAgentStateContract.Describe(requirements.StateContract),
            requirements.MinimumCompatibleRevision,
            CameraAgentStateContract.Describe(installedStateContract),
            compatible,
            findings,
            DateTimeOffset.UtcNow);
    }

    private static void EvaluateCandidateImageIdentity(
        List<CameraAgentStatePreflightFinding> findings,
        string? installedImageId,
        string? signedOfflineArchiveImageId,
        string? signedPlatformManifestDigest)
    {
        if (string.IsNullOrWhiteSpace(installedImageId) ||
            (!string.Equals(installedImageId, signedOfflineArchiveImageId, StringComparison.Ordinal) &&
             !string.Equals(installedImageId, signedPlatformManifestDigest, StringComparison.Ordinal)))
        {
            return;
        }

        findings.Add(new CameraAgentStatePreflightFinding(
            "candidate-image-already-active",
            "image-identity",
            Blocking: false,
            "instance-manifest.image.imageId",
            installedImageId,
            "a signed candidate identity different from the installed image",
            "Choose another signed release, or take no upgrade action because this image is already active."));
    }

    /// <summary>
    /// Evaluates the persisted state for a candidate image and fails once with the complete boundary report before
    /// any container, backup, or Compose mutation begins.
    /// </summary>
    public static async Task<CameraAgentStatePreflightReport> EnsureCompatibleAsync(
        InstallationPaths paths,
        Guid instanceId,
        ImageInstallationIdentity candidate,
        string? installedStateContract,
        uint uid,
        uint gid,
        ContractReplayProfile replayProfile,
        bool persist,
        bool renderToStandardError,
        CancellationToken cancellationToken,
        CameraAgentStateContractPolicy contractPolicy = CameraAgentStateContractPolicy.RequireCurrent,
        string? candidateRelease = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(candidate);
        var report = Evaluate(
            paths,
            instanceId,
            candidate.ImageId,
            // image-distribution.json is written only once the image is accepted, so a refused signed upgrade
            // leaves this report as the operator's only artifact; it must name the release it refused.
            candidateRelease,
            CameraAgentStateRequirements.From(candidate),
            installedStateContract,
            uid,
            gid,
            replayProfile,
            contractPolicy);
        var reportPath = Path.Combine(paths.DeploymentStateRoot, "state-preflight.json");
        if (persist)
        {
            await SafeFileSystem.WriteJsonAtomicAsync(
                reportPath,
                report,
                DeploymentJsonContext.Default.CameraAgentStatePreflightReport,
                cancellationToken).ConfigureAwait(false);
        }
        if (report.Compatible)
        {
            return report;
        }

        // The rendered report carries every boundary at once, but a --json invocation reserves standard error for
        // exactly one JSON object, so that form relies on the enumerated codes and the retained report instead.
        if (renderToStandardError)
        {
            await Console.Error.WriteLineAsync(Render(report)).ConfigureAwait(false);
        }
        var codes = string.Join(", ", report.Findings.Where(static finding => finding.Blocking)
            .Select(static finding => finding.Code).Distinct(StringComparer.Ordinal));
        throw new InstallerException(
            $"CameraAgent state preflight rejected this deployment before startup ({codes}). Minimum compatible revision {report.MinimumCompatibleRevision ?? "unknown"}; complete the CameraAgent-only reset or state-disposition procedure{(persist ? $" and see {reportPath}" : string.Empty)}.");
    }

    /// <summary>Renders every finding at once so an operator resolves the complete boundary set in one pass.</summary>
    public static string Render(CameraAgentStatePreflightReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"CameraAgent state preflight for instance {report.InstanceId:D}: ");
        builder.AppendLine(report.Compatible ? "compatible." : "incompatible.");
        if (report.CandidateRelease is { Length: > 0 } release)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"Candidate signed release: {release} ({report.CandidateImageId ?? "no immutable image ID published"}).");
        }
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"Candidate state contract: {report.CandidateStateContract}; installed: {report.InstalledStateContract}.");
        if (report.MinimumCompatibleRevision is { Length: > 0 } minimum)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"Minimum compatible CameraAgent revision: {minimum}. Earlier state requires an explicit reset or state-disposition procedure.");
        }
        foreach (var finding in report.Findings)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"- [{(finding.Blocking ? "blocking" : "advisory")}] {finding.Code} ({finding.Boundary}) at {finding.Path}: observed {finding.Observed ?? "none"}; expected {finding.Expected ?? "unspecified"}. {finding.Remediation}");
        }
        if (report.Findings.Count == 0)
        {
            builder.AppendLine("- No incompatible boundary was detected.");
        }
        return builder.ToString().TrimEnd();
    }

    private static void EvaluateStateContract(
        List<CameraAgentStatePreflightFinding> findings,
        CameraAgentStateRequirements requirements,
        string? installedStateContract,
        CameraAgentStateContractPolicy contractPolicy)
    {
        var candidateAccepted = contractPolicy == CameraAgentStateContractPolicy.AllowLegacy
            ? CameraAgentStateContract.IsKnown(requirements.StateContract)
            : CameraAgentStateContract.IsCurrent(requirements.StateContract);
        if (!candidateAccepted)
        {
            findings.Add(new CameraAgentStatePreflightFinding(
                "candidate-state-contract-unsupported",
                "image-label",
                Blocking: true,
                "io.hvo.skymonitor.state-compatibility",
                CameraAgentStateContract.Describe(requirements.StateContract),
                CameraAgentStateContract.Current,
                "Deploy a CameraAgent image that declares the supported durable state contract."));
            return;
        }

        // A candidate that omits a boundary label leaves that boundary unverifiable. Name exactly which ones
        // instead of implying a comparison ran; an image predating the correction declares none of them.
        var undeclared = new List<string>();
        if (requirements.MinimumCompatibleRevision is not { Length: > 0 }) undeclared.Add("minimum-compatible-revision");
        if (requirements.IdentityMigration is not { Length: > 0 }) undeclared.Add("identity-migration");
        if (requirements.RawIngressSchema is null) undeclared.Add("raw-ingress-schema");
        if (requirements.CatalogManifestVersion is null) undeclared.Add("catalog-manifest-version");
        if (undeclared.Count > 0)
        {
            findings.Add(new CameraAgentStatePreflightFinding(
                "candidate-boundaries-undeclared",
                "image-label",
                Blocking: false,
                string.Join(", ", undeclared.Select(static label => $"io.hvo.skymonitor.{label}")),
                "none",
                "a declared boundary for each label",
                "This image declares no value for those boundaries, so no comparison against its own values ran for them. Confirm the instance has not crossed a state boundary since it was installed."));
        }

        // The superseded label carried no boundary, so an installation that declares it is admitted only when the
        // persisted boundaries below prove the state already matches the candidate contract.
        if (!CameraAgentStateContract.IsKnown(installedStateContract))
        {
            findings.Add(new CameraAgentStatePreflightFinding(
                "installed-state-contract-unknown",
                "image-label",
                Blocking: true,
                "io.hvo.skymonitor.state-compatibility",
                CameraAgentStateContract.Describe(installedStateContract),
                CameraAgentStateContract.Current,
                "The installed image declares an unrecognized state contract; complete an explicit state-disposition procedure."));
        }
    }

    /// <summary>
    /// The upgrade's contract-identity gate, evaluated from the signed declaration: the candidate's component,
    /// configuration, catalog, and (for a LocalRunner instance) replay-runner contract identities must match this
    /// instance, or the upgrade refuses the image after acquiring it. Each mismatch is its own blocking finding so
    /// the operator learns every disagreement at once. Architecture is not compared here because the release's
    /// platform is selected for the instance's recorded daemon architecture before this evaluation runs.
    /// </summary>
    private static void EvaluateContractIdentity(
        List<CameraAgentStatePreflightFinding> findings,
        CameraAgentContractIdentity candidate,
        string? instanceConfigurationContract,
        ContractReplayProfile replayProfile)
    {
        const string boundary = "contract-identity";
        const string remediation =
            "Select a CameraAgent release built for this instance's contracts; the upgrade refuses a candidate whose declared contract identities differ from the instance.";
        if (!string.Equals(candidate.Component, CameraAgentImageContract.Component, StringComparison.Ordinal))
        {
            findings.Add(new CameraAgentStatePreflightFinding(
                "contract-component", boundary, Blocking: true, "io.hvo.skymonitor.component",
                candidate.Component ?? "none", CameraAgentImageContract.Component, remediation));
        }
        if (!string.Equals(candidate.ConfigurationContract, instanceConfigurationContract, StringComparison.Ordinal))
        {
            findings.Add(new CameraAgentStatePreflightFinding(
                "contract-configuration", boundary, Blocking: true, "io.hvo.skymonitor.configuration-contract",
                candidate.ConfigurationContract ?? "none", instanceConfigurationContract ?? "unspecified", remediation));
        }
        if (!string.Equals(candidate.CatalogContract, CameraAgentImageContract.CatalogContract, StringComparison.Ordinal))
        {
            findings.Add(new CameraAgentStatePreflightFinding(
                "contract-catalog", boundary, Blocking: true, "io.hvo.skymonitor.catalog-contract",
                candidate.CatalogContract ?? "none", CameraAgentImageContract.CatalogContract, remediation));
        }
        // An in-process instance never dispatches to the local replay runner, so it imposes no requirement here;
        // a LocalRunner instance requires the runner contract exactly as the upgrade does.
        if (replayProfile == ContractReplayProfile.LocalRunner &&
            !string.Equals(candidate.ReplayRunnerContract, CameraAgentImageContract.ReplayRunnerContract, StringComparison.Ordinal))
        {
            findings.Add(new CameraAgentStatePreflightFinding(
                "contract-replay-runner", boundary, Blocking: true, "io.hvo.skymonitor.replay-runner-contract",
                candidate.ReplayRunnerContract ?? "none", CameraAgentImageContract.ReplayRunnerContract,
                "This instance runs the local replay runner, so its candidate must declare the runner contract; select a release that publishes it."));
        }
    }

    private static void EvaluateCatalog(
        List<CameraAgentStatePreflightFinding> findings,
        InstallationPaths paths,
        CameraAgentStateRequirements requirements)
    {
        const string boundary = "catalog";
        var manifestPath = Path.Combine(paths.CatalogRoot, "current", "manifest.json");
        if (!Directory.Exists(paths.CatalogRoot))
        {
            findings.Add(new CameraAgentStatePreflightFinding(
                "catalog-missing", boundary, Blocking: true, paths.CatalogRoot, "absent", "installed catalog root",
                "Install the approved production catalog bundle before starting CameraAgent."));
            return;
        }

        // Full snapshot verification belongs to catalog installation and selection. Preflight owns the boundary a
        // legacy in-place upgrade actually breaks: the selected manifest version and catalog identity.
        var manifest = TryReadCatalogManifest(manifestPath);
        if (manifest is null)
        {
            findings.Add(new CameraAgentStatePreflightFinding(
                "catalog-manifest-unreadable", boundary, Blocking: true, manifestPath, "unreadable",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"a readable manifest declaring version {CatalogSnapshotResolver.SupportedManifestVersion}"),
                "Reinstall the approved catalog bundle; the selected catalog manifest cannot be read."));
            return;
        }
        // A candidate that declares no manifest version is still compared against the resolver the runtime uses.
        var expectedManifestVersion = requirements.CatalogManifestVersion ?? CatalogSnapshotResolver.SupportedManifestVersion;
        if (manifest.Value.ManifestVersion != expectedManifestVersion)
        {
            findings.Add(new CameraAgentStatePreflightFinding(
                "catalog-manifest-version", boundary, Blocking: true, manifestPath,
                manifest.Value.ManifestVersion.ToString(CultureInfo.InvariantCulture),
                expectedManifestVersion.ToString(CultureInfo.InvariantCulture),
                "Select an installed catalog whose manifest version matches the candidate image, then rerun the deployment."));
        }
        if (!string.Equals(manifest.Value.CatalogId, ProductionCatalog.CatalogId, StringComparison.Ordinal))
        {
            findings.Add(new CameraAgentStatePreflightFinding(
                "catalog-identity", boundary, Blocking: true, manifestPath,
                manifest.Value.CatalogId ?? "none", ProductionCatalog.CatalogId,
                "Select the approved production catalog identity for this instance."));
        }
    }

    private static void EvaluateIdentityLineage(
        List<CameraAgentStatePreflightFinding> findings,
        InstallationPaths paths,
        CameraAgentStateRequirements requirements)
    {
        const string boundary = "identity";
        if (requirements.IdentityMigration is not { Length: > 0 } expected)
        {
            return;
        }
        var databasePath = CameraAgentStateLayout.IdentityDatabasePath(paths.StateRoot);
        if (!File.Exists(databasePath))
        {
            return;
        }

        List<string> applied;
        bool uninitialized;
        try
        {
            (applied, uninitialized) = ReadIdentityLineage(databasePath);
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            findings.Add(new CameraAgentStatePreflightFinding(
                "identity-lineage-unreadable", boundary, Blocking: true, databasePath,
                Redaction.SafeDiagnostic(exception.Message), expected,
                "Stop the CameraAgent instance and rerun the preflight so the Identity database can be read."));
            return;
        }

        // The runtime creates and migrates this database itself, so a file that carries neither a recorded
        // migration nor any Identity table is a fresh start rather than an incompatible lineage.
        if (uninitialized || (applied.Count == 1 && string.Equals(applied[0], expected, StringComparison.Ordinal)))
        {
            return;
        }
        findings.Add(new CameraAgentStatePreflightFinding(
            "identity-migration-lineage", boundary, Blocking: true, databasePath,
            applied.Count == 0 ? "no recorded migration" : string.Join(", ", applied), expected,
            "The persisted Identity database was created by an incompatible CameraAgent revision; complete the CameraAgent-only reset procedure before upgrading."));
    }

    private static void EvaluateRawIngressSchema(
        List<CameraAgentStatePreflightFinding> findings,
        InstallationPaths paths,
        CameraAgentStateRequirements requirements)
    {
        const string boundary = "raw-ingress";
        if (requirements.RawIngressSchema is not { } expected)
        {
            return;
        }
        var databasePath = CameraAgentStateLayout.RawIngressDatabasePath(paths.StateRoot);
        if (!File.Exists(databasePath))
        {
            return;
        }

        long observed;
        long schemaObjects;
        try
        {
            (observed, schemaObjects) = ReadRawIngressSchema(databasePath);
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            findings.Add(new CameraAgentStatePreflightFinding(
                "raw-ingress-schema-unreadable", boundary, Blocking: true, databasePath,
                Redaction.SafeDiagnostic(exception.Message), expected.ToString(CultureInfo.InvariantCulture),
                "Stop the CameraAgent instance and rerun the preflight so the raw-ingress journal can be read."));
            return;
        }

        // SqliteRawCaptureJournal initializes a database whose user_version is 0 with no schema objects, so that
        // state is compatible rather than an unsupported schema.
        if (observed == expected || (observed == 0 && schemaObjects == 0))
        {
            return;
        }
        findings.Add(new CameraAgentStatePreflightFinding(
            "raw-ingress-schema", boundary, Blocking: true, databasePath,
            observed.ToString(CultureInfo.InvariantCulture), expected.ToString(CultureInfo.InvariantCulture),
            "Archive the raw-ingress database and complete the CameraAgent-only reset procedure before upgrading."));
    }

    private static void EvaluateBindSources(
        List<CameraAgentStatePreflightFinding> findings,
        InstallationPaths paths,
        uint uid,
        uint gid,
        ContractReplayProfile replayProfile)
    {
        const string boundary = "bind-source";
        foreach (var source in CameraAgentStateLayout.WritableBindSources(paths.StateRoot, replayProfile))
        {
            if (!Directory.Exists(source.HostPath))
            {
                findings.Add(new CameraAgentStatePreflightFinding(
                    "bind-source-missing", boundary, Blocking: true, source.HostPath, "absent",
                    $"directory owned by {uid}:{gid} mode 0700 for {source.ContainerPath}",
                    "Let the deployment tooling create the bind source before Compose starts; Docker would otherwise create it as root."));
                continue;
            }
            if (new DirectoryInfo(source.HostPath).LinkTarget is not null)
            {
                findings.Add(new CameraAgentStatePreflightFinding(
                    "bind-source-link", boundary, Blocking: true, source.HostPath, "symbolic link",
                    "regular directory",
                    "Replace the linked bind source with a regular owner-only directory."));
                continue;
            }
            UnixPathIdentity identity;
            try
            {
                identity = NativeLinux.GetDirectoryIdentity(source.HostPath);
            }
            catch (InstallerException exception)
            {
                findings.Add(new CameraAgentStatePreflightFinding(
                    "bind-source-unreadable", boundary, Blocking: true, source.HostPath,
                    Redaction.SafeDiagnostic(exception.Message),
                    string.Create(CultureInfo.InvariantCulture, $"directory owned by {uid}:{gid} mode 0700"),
                    "Re-create the bind source as a regular owner-only directory before starting CameraAgent."));
                continue;
            }
            if (identity.Uid != uid || identity.Gid != gid)
            {
                findings.Add(new CameraAgentStatePreflightFinding(
                    "bind-source-ownership", boundary, Blocking: true, source.HostPath,
                    string.Create(CultureInfo.InvariantCulture, $"{identity.Uid}:{identity.Gid}"),
                    string.Create(CultureInfo.InvariantCulture, $"{uid}:{gid}"),
                    "Re-create the bind source with the configured runtime UID/GID; the capability-dropped container cannot adopt a root-owned directory."));
                continue;
            }
            // The mount must be exactly owner rwx. Extra bits expose the state tree, and a tighter mode such as
            // 0500 or 0600 silently breaks the container's writes or its traversal into the bind source.
            // Compared against the constants CreateRuntimeDirectory writes, so the preflight can never reject a
            // directory the deployment tooling itself produced.
            if ((identity.Mode & SafeFileSystem.AllPermissions) != SafeFileSystem.OwnerDirectoryMode)
            {
                findings.Add(new CameraAgentStatePreflightFinding(
                    "bind-source-mode", boundary, Blocking: true, source.HostPath,
                    FormatMode(identity.Mode), "0700",
                    "Set the bind source to exactly owner-only read, write, and execute; a looser mode exposes CameraAgent state and a tighter one blocks its writes or traversal."));
            }
        }
    }

    private static string FormatMode(UnixFileMode mode)
        => Convert.ToString((int)mode & 0b111_111_111_111, 8).PadLeft(4, '0');

    private static (int ManifestVersion, string? CatalogId)? TryReadCatalogManifest(string manifestPath)
    {
        try
        {
            using var stream = SafeFileSystem.OpenRegularFileRead(manifestPath);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("manifestVersion", out var version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var manifestVersion))
            {
                return null;
            }
            var catalogId = document.RootElement.TryGetProperty("catalog", out var catalog) &&
                            catalog.ValueKind == JsonValueKind.Object &&
                            catalog.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
            return (manifestVersion, catalogId);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException
                                              or InstallerException)
        {
            return null;
        }
    }

    private static (List<string> Applied, bool Uninitialized) ReadIdentityLineage(string databasePath)
    {
        using var database = ReadOnlyDatabase.Open(databasePath);
        var connection = database.Connection;
        var migrations = new List<string>();
        // The runtime materializes the file on its first connection and only then creates the history table, so a
        // table-less database is an interrupted fresh start rather than an unreadable one.
        using (var history = connection.CreateCommand())
        {
            history.CommandText =
                "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = '__EFMigrationsHistory';";
            if (Convert.ToInt64(history.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    migrations.Add(reader.GetString(0));
                }
            }
        }
        if (migrations.Count > 0)
        {
            return (migrations, false);
        }

        using var identityTables = connection.CreateCommand();
        identityTables.CommandText =
            "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name LIKE 'AspNet%';";
        return (migrations, Convert.ToInt64(identityTables.ExecuteScalar(), CultureInfo.InvariantCulture) == 0);
    }

    private static (long UserVersion, long SchemaObjects) ReadRawIngressSchema(string databasePath)
    {
        using var database = ReadOnlyDatabase.Open(databasePath);
        var connection = database.Connection;
        long userVersion;
        using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            userVersion = Convert.ToInt64(version.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        using var objects = connection.CreateCommand();
        objects.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%';";
        return (userVersion, Convert.ToInt64(objects.ExecuteScalar(), CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Opens a persisted CameraAgent database without writing anything beside it. A plain read-only connection is
    /// not side-effect free: SQLite creates the wal-index for a WAL database even through a read-only handle, and
    /// the raw-ingress journal is WAL at rest. For a stopped instance that would leave installer-owned
    /// <c>-wal</c>/<c>-shm</c> files inside a 0700 runtime-owned bind source that the capability-dropped container
    /// then cannot open. The three shapes a journal can be in each admit a different side-effect-free read, and
    /// which one applies was measured rather than assumed:
    /// <list type="bullet">
    /// <item>No wal-index and no recovery state: the instance is stopped and checkpointed, and
    /// <c>immutable=1</c> reads it while creating nothing.</item>
    /// <item>A wal-index <em>and</em> its log both exist: the files are already present and owned by the runtime
    /// identity, and an in-place read-only open creates nothing while observing a consistent snapshot through that
    /// index. A wal-index without its log does not qualify — an in-place open there creates the log — so it falls
    /// to the first case, which is correct because with no log there is no pending content to read.</item>
    /// <item>Recovery state without a usable wal-index: an in-place open would create one. The database and its
    /// recovery files are read through a private copy, and the pending journal replays into the copy rather than
    /// here. A writer can hold them while that copy is made, because preflight runs before the drain, so the copy
    /// is only used when the source is verifiably the same generation on both sides of it.</item>
    /// </list>
    /// A clean shutdown between the probe and the open would leave the third case reading a checkpointed
    /// database; preflight cannot observe a running instance without contacting Docker, which it must not do.
    /// Reading a live WAL database registers a reader in the existing wal-index, which is what every reader of
    /// such a database does, including the instance's own. No file is created and no durable state changes. The
    /// alternative — copying a database a writer holds — is what the first two cases exist to avoid, and where
    /// the third case has no choice but to copy, the copy is checked rather than assumed.
    /// </summary>
    private sealed class ReadOnlyDatabase : IDisposable
    {
        private readonly DirectoryInfo? snapshotRoot;

        private ReadOnlyDatabase(SqliteConnection connection, DirectoryInfo? snapshotRoot)
        {
            Connection = connection;
            this.snapshotRoot = snapshotRoot;
        }

        public SqliteConnection Connection { get; }

        private const int Attempts = 3;

        public static ReadOnlyDatabase Open(string databasePath)
        {
            // The shape can change under an in-flight preflight, which runs before the drain: a running instance
            // can commit and remove its rollback journal between the observation and the copy. The Identity
            // database uses a rollback journal, so that is the likeliest shape to move. Observe it again rather
            // than reporting a database that is merely busy as unreadable and blocking a valid upgrade.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return OpenObservedShape(databasePath);
                }
                catch (FileNotFoundException) when (attempt < Attempts)
                {
                }
                catch (DirectoryNotFoundException) when (attempt < Attempts)
                {
                }
                catch (InspectionSnapshotMoved) when (attempt < Attempts)
                {
                }
                catch (InspectionSnapshotMoved exception)
                {
                    // Reported as an I/O condition because that is what it is, and because each boundary already
                    // turns one of those into its own blocking unreadable-database finding. A preflight that
                    // cannot take a consistent copy has not found an incompatibility; it has failed to look, and
                    // the operator is told so in the same consolidated report as everything else.
                    throw new IOException(
                        "The database kept changing while preflight was copying it, so no consistent snapshot " +
                        "of it could be taken. A journalled database is read through a private copy because " +
                        "preflight must not write beside the original, and a copy taken across a commit " +
                        "describes state that never existed.",
                        exception);
                }
            }
        }

        private static ReadOnlyDatabase OpenObservedShape(string databasePath)
        {
            var recoveryFiles = new[] { databasePath + "-wal", databasePath + "-journal" }
                .Where(File.Exists).ToArray();
            if (recoveryFiles.Contains(databasePath + "-wal", StringComparer.Ordinal) &&
                File.Exists(databasePath + "-shm"))
            {
                return new ReadOnlyDatabase(OpenConnection(databasePath, SqliteOpenMode.ReadOnly), null);
            }
            if (recoveryFiles.Length == 0)
            {
                return new ReadOnlyDatabase(
                    OpenConnection(new Uri(databasePath).AbsoluteUri + "?immutable=1", SqliteOpenMode.ReadOnly), null);
            }
            // The observation that chose this branch fixes which files are copied, not what they hold. Preflight
            // runs before the drain, so a writer can commit between the copy of the main database and the copy of
            // its journal; the halves then belong to different generations. A missing file is caught by the retry
            // above and is the loud half of that race. This is the quiet half: replaying a journal against a main
            // database it did not come from does not fail, it succeeds and reports state that never existed. So
            // the generation is recorded on both sides of the copy and a copy that straddled a commit is thrown
            // away rather than read.
            DirectoryInfo? snapshotRoot = null;
            try
            {
                var generation = SnapshotGeneration(databasePath, recoveryFiles);
                snapshotRoot = Directory.CreateTempSubdirectory("hvo-preflight-inspection-");
                var inspectionPath = Path.Combine(snapshotRoot.FullName, Path.GetFileName(databasePath));
                File.Copy(databasePath, inspectionPath);
                // File.Copy carries the source mode across, and replaying a rollback journal needs a writable
                // main database, so the private copy is made writable explicitly.
                File.SetUnixFileMode(inspectionPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                SnapshotCopyBarrier();
                foreach (var recoveryFile in recoveryFiles)
                {
                    var copied = inspectionPath + recoveryFile[databasePath.Length..];
                    File.Copy(recoveryFile, copied);
                    File.SetUnixFileMode(copied, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                if (!string.Equals(
                        generation,
                        SnapshotGeneration(databasePath, recoveryFiles),
                        StringComparison.Ordinal))
                {
                    throw new InspectionSnapshotMoved();
                }
                return new ReadOnlyDatabase(OpenConnection(inspectionPath, SqliteOpenMode.ReadWrite), snapshotRoot);
            }
            catch
            {
                Discard(snapshotRoot);
                throw;
            }
        }

        /// <summary>
        /// Describes the generation of a database and its recovery files well enough that a commit landing
        /// between two reads of it cannot go unnoticed. Length and last-write time catch a file being rewritten.
        /// The headers catch the case those miss, which a rollback journal makes ordinary rather than exotic: a
        /// transaction restores the file it started from, so the same length at the same coarse timestamp is a
        /// normal outcome. The first hundred bytes of a main database carry the file change counter and the
        /// version-valid-for number, both of which move on every write transaction, and the first thirty-two
        /// bytes of a log carry the write-ahead salts, which move whenever a checkpoint restarts it.
        /// </summary>
        private static string SnapshotGeneration(string databasePath, IReadOnlyList<string> recoveryFiles)
        {
            var generation = new StringBuilder();
            Describe(generation, databasePath, 100);
            foreach (var recoveryFile in recoveryFiles)
            {
                Describe(generation, recoveryFile, 32);
            }
            return generation.ToString();

            static void Describe(StringBuilder generation, string path, int headerLength)
            {
                var header = new byte[headerLength];
                // The writer holds these files open and may delete them, so the read shares everything it can;
                // a file that disappears here raises the same exception the retry above already handles.
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var read = stream.ReadAtLeast(header, headerLength, throwOnEndOfStream: false);
                generation.Append(CultureInfo.InvariantCulture, $"{path}|{stream.Length}|");
                generation.Append(
                    CultureInfo.InvariantCulture, $"{File.GetLastWriteTimeUtc(path).Ticks}|");
                generation.Append(Convert.ToHexStringLower(header.AsSpan(0, read))).Append('\n');
            }
        }

        /// <summary>
        /// The copy straddled a commit. It is a retry signal rather than an outcome, so it stays private to this
        /// class; the outcome an operator sees is the installer exception raised once the retries are spent.
        /// </summary>
        private sealed class InspectionSnapshotMoved : Exception
        {
            public InspectionSnapshotMoved()
            {
            }

            public InspectionSnapshotMoved(string message)
                : base(message)
            {
            }

            public InspectionSnapshotMoved(string message, Exception innerException)
                : base(message, innerException)
            {
            }
        }

        public void Dispose()
        {
            try
            {
                Connection.Dispose();
            }
            finally
            {
                Discard(snapshotRoot);
            }
        }

        private static SqliteConnection OpenConnection(string dataSource, SqliteOpenMode mode)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dataSource,
                Mode = mode,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString());
            try
            {
                connection.Open();
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        private static void Discard(DirectoryInfo? snapshotRoot)
        {
            try
            {
                snapshotRoot?.Delete(recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A temporary copy that cannot be removed must not mask the reason the caller is unwinding, nor
                // fail a preflight that has already produced its answer.
            }
        }
    }
}

/// <summary>
/// Resolves the installed instance and candidate image for the operator-facing
/// <c>cameraagent preflight</c> command. The command inspects persisted state only; it never pulls, loads,
/// starts, mutates, or deletes anything, and it writes nothing into the instance, the release media, or the
/// distribution cache. A journal carrying unreplayed recovery state is read through a private temporary copy.
/// </summary>
internal static class CameraAgentStatePreflightManager
{
    public static Task<CameraAgentStatePreflightReport> ExecuteAsync(
        CameraAgentStatePreflightRequest request,
        CancellationToken cancellationToken)
        => ExecuteAsync(request, new ProcessRunner(), cancellationToken);

    internal static async Task<CameraAgentStatePreflightReport> ExecuteAsync(
        CameraAgentStatePreflightRequest request,
        IProcessRunner processRunner,
        CancellationToken cancellationToken,
        Func<DistributionAcquirer>? distributionFactory = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var instanceId = request.InstanceId!.Value;
        var paths = InstallationPaths.Create(request.ProductRoot, instanceId, ProductionCatalog.CatalogId);
        if (!File.Exists(paths.ManifestPath))
        {
            throw new InstallerException("The selected instance has no retained manifest.");
        }
        InstanceManifest manifest;
        await using (var stream = SafeFileSystem.OpenOwnerFileRead(paths.ManifestPath))
        {
            manifest = await JsonSerializer.DeserializeAsync(
                           stream, DeploymentJsonContext.Default.InstanceManifest, cancellationToken).ConfigureAwait(false)
                       ?? throw new InstallerException("The selected instance manifest is empty.");
        }
        if (manifest.InstanceId != instanceId || manifest.StateRoot != paths.StateRoot)
        {
            throw new InstallerException("The retained instance manifest does not correlate with the selected instance.");
        }

        var requirements = CameraAgentStateRequirements.From(manifest.Image);
        var candidateImageId = manifest.Image.ImageId;
        string? candidateRelease = null;
        CameraAgentContractIdentity? candidateContracts = null;
        string? signedOfflineArchiveImageId = null;
        string? signedPlatformManifestDigest = null;
        // Evaluating the installed image reports the instance as it stands; naming a candidate evaluates the
        // in-place upgrade, which additionally requires the current durable state contract.
        var policy = CameraAgentStateContractPolicy.AllowLegacy;
        if (request.NamesSignedImageRelease)
        {
            policy = CameraAgentStateContractPolicy.RequireCurrent;
            using var acquirer = (distributionFactory ?? CreateDistributionAcquirer)();
            var release = await acquirer.ResolveImageAsync(
                                  request.ImageSelection(), manifest.DockerDaemon.Architecture, cancellationToken)
                              .ConfigureAwait(false)
                          ?? throw new InstallerException(
                              "The signed CameraAgent image release did not resolve a candidate image.");
            // The release is verified and its platform selected exactly as an upgrade does, for the instance's
            // recorded Docker daemon architecture rather than this process's, but the offline
            // archive is deliberately not acquired and Docker is never contacted: the signed compatibility record
            // is the candidate declaration, and an upgrade refuses any image that contradicts it. That keeps the
            // command read-only and lets an operator evaluate a release the host has not received yet. The signed
            // declaration also carries the contract identities the upgrade's contract-identity gate compares, so
            // that gate is reached here too; only the loaded image's label agreement with this record remains an
            // upgrade-time check, so a clean report here is still not a promise the upgrade proceeds.
            requirements = CameraAgentStateRequirements.From(release.Image.Compatibility);
            candidateContracts = CameraAgentContractIdentity.From(release.Image);
            signedOfflineArchiveImageId = release.Platform.OfflineArchiveImageId;
            signedPlatformManifestDigest = release.Platform.ManifestDigest;
            candidateImageId = signedOfflineArchiveImageId ?? signedPlatformManifestDigest;
            candidateRelease = release.Release.Tag;
        }
        else if (request.ImageReference is { Length: > 0 } reference)
        {
            policy = CameraAgentStateContractPolicy.RequireCurrent;
            var docker = new DockerClient(processRunner);
            var synthetic = new InstallRequest
            {
                FriendlyName = manifest.FriendlyName,
                OwnerEmail = manifest.OwnerEmail,
                ProductRoot = manifest.ProductRoot,
                CatalogBundle = "/dev/null",
                ImageReference = reference,
                // The preflight never mutates, so the candidate is inspected locally and never pulled or loaded.
                NoDownload = true
            };
            var prepared = await docker.PrepareImageAsync(synthetic, allowMutation: false, signedImage: null, cancellationToken)
                .ConfigureAwait(false);
            requirements = CameraAgentStateRequirements.From(prepared.Image);
            candidateImageId = prepared.Image.ImageId;
        }

        return CameraAgentStatePreflight.Evaluate(
            paths,
            instanceId,
            candidateImageId,
            candidateRelease,
            requirements,
            manifest.Image.UpgradeCompatibility ?? manifest.UpgradeCompatibility,
            manifest.RuntimeUid,
            manifest.RuntimeGid,
            manifest.ReplayProfile,
            policy,
            candidateContracts,
            manifest.ComponentSchemaVersion,
            manifest.Image.ImageId,
            signedOfflineArchiveImageId,
            signedPlatformManifestDigest);
    }

    private static DistributionAcquirer CreateDistributionAcquirer() => new();
}
