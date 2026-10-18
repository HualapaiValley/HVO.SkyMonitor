using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

internal abstract record DeploymentCommand(bool Json)
{
    /// <summary>
    /// Whether this invocation may address a product root other than <see cref="ProductionProductRoot"/>.
    /// Deny by default and decided once at the command-line boundary, so validation answers from the request it
    /// was given rather than from process state that anything else can change under it.
    /// </summary>
    public bool AllowTestProductRoot { get; init; }

    /// <summary>
    /// The production product root resolved once at the command-line boundary from the host policy, or
    /// <see cref="InstallRequest.DefaultProductRoot"/> when the host has none. Every command defaults to it and
    /// validates against it, so an instance is found under a relocated root with no extra option.
    /// </summary>
    public string ProductionProductRoot { get; init; } = InstallRequest.DefaultProductRoot;

    /// <summary>
    /// Advisories about the command line itself, such as a deprecated option that was accepted. They are said before the
    /// command runs, or with <c>--json</c> reported in its result, and never change what it does.
    /// </summary>
    public IReadOnlyList<InstallationWarning> ParseWarnings { get; init; } = [];
}

internal sealed record InstallDeploymentCommand(InstallRequest Request) : DeploymentCommand(Request.Json);

/// <summary>A request for usage text, which is printed and changes nothing.</summary>
internal sealed record HelpCommand(string Usage) : DeploymentCommand(Json: false);

/// <summary>A request for the installer's build identity, which is printed and changes nothing.</summary>
internal sealed record VersionCommand(string Identity) : DeploymentCommand(Json: false);

internal sealed record OwnerRecoveryRequest(
    Guid? InstanceId,
    string ProductRoot,
    string? PasswordFile,
    bool GeneratePassword,
    bool Resume,
    bool Json) : DeploymentCommand(Json)
{
    public void Validate()
    {
        if (InstanceId is null)
        {
            throw new InstallUsageException("--instance-id is required.");
        }
        ProductRootAdmission.Validate(ProductRoot, ProductionProductRoot, AllowTestProductRoot);
        if (PasswordFile is not null)
        {
            ValidateAbsolutePath(PasswordFile, "--password-file");
        }
        if ((PasswordFile is null) == !GeneratePassword)
        {
            throw new InstallUsageException("Specify exactly one of --password-file or --generate-password.");
        }
    }

    public string ComputeRequestSha256()
    {
        var value = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{InstanceId:D}\n{GeneratePassword}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static void ValidateAbsolutePath(string path, string option)
    {
        if (!Path.IsPathFullyQualified(path) || path.Contains("//", StringComparison.Ordinal) ||
            Path.GetFullPath(path) != path.TrimEnd('/') || path == "/")
        {
            throw new InstallUsageException($"{option} must be an absolute normalized path other than root.");
        }
    }
}

internal sealed record CameraAgentStatePreflightRequest(
    Guid? InstanceId,
    string ProductRoot,
    string? ImageReference,
    bool Json) : DeploymentCommand(Json)
{
    public void Validate()
    {
        if (InstanceId is null)
        {
            throw new InstallUsageException("--instance-id is required.");
        }
        ProductRootAdmission.Validate(ProductRoot, ProductionProductRoot, AllowTestProductRoot);
        if (ImageReference is not null &&
            !System.Text.RegularExpressions.Regex.IsMatch(
                ImageReference,
                "^(sha256:[a-f0-9]{64}|[^@\\s]+@sha256:[a-f0-9]{64})$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            throw new InstallUsageException("preflight requires an immutable --image-ref.");
        }
    }
}

internal sealed record CameraAgentStateResetRequest(
    Guid? InstanceId,
    Guid? ConfirmationInstanceId,
    string ProductRoot,
    bool DryRun,
    bool Json) : DeploymentCommand(Json)
{
    public void Validate()
    {
        if (InstanceId is null)
        {
            throw new InstallUsageException("--instance-id is required.");
        }
        ProductRootAdmission.Validate(ProductRoot, ProductionProductRoot, AllowTestProductRoot);
        if (ConfirmationInstanceId != InstanceId)
        {
            throw new InstallUsageException("reset-state requires --confirm-instance-id matching --instance-id.");
        }
    }

    public string ComputeRequestSha256()
    {
        var value = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"reset-state\n{InstanceId:D}\n{ProductRoot}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}

internal sealed record LifecycleRequest : DeploymentCommand
{
    public LifecycleRequest(
        LifecycleOperationKind? operation,
        Guid? instanceId,
        string productRoot,
        bool dryRun,
        bool resume,
        bool json)
        : base(json)
    {
        Operation = operation;
        InstanceId = instanceId;
        ProductRoot = productRoot;
        DryRun = dryRun;
        Resume = resume;
    }

    public LifecycleOperationKind? Operation { get; init; }
    public Guid? InstanceId { get; init; }
    public string ProductRoot { get; init; }
    public bool DryRun { get; init; }
    public bool Resume { get; init; }
    public bool RestoreOnly { get; init; }
    public Guid? RecoveryOperationId { get; init; }
    public string? ImageReference { get; init; }
    public string? ImageArchive { get; init; }
    public string? ImageArchiveSha256 { get; init; }

    /// <summary>The folder holding the release an upgrade installs (<c>--release</c>), as <see cref="InstallRequest.Release"/>.</summary>
    public string? Release { get; init; }

    /// <summary>How the release's image reaches Docker (<c>--image-source</c>), as <see cref="InstallRequest.ImageSource"/>.</summary>
    public ReleaseImageSource? ImageSource { get; init; }
    public bool NoDownload { get; init; }
    public bool MigrationBackwardCompatible { get; init; }
    public Guid? ConfirmationInstanceId { get; init; }

    /// <summary>
    /// The operator's disposition that the interrupted purge with this operation ID lost its evidenced tree to a
    /// removal outside the CLI. It is an attestation for one retained operation, not an input of the request, so it is
    /// excluded from the request hash.
    /// </summary>
    public Guid? ConfirmRemovedOperationId { get; init; }
    public string? CatalogBundle { get; init; }
    public string? CatalogVersion { get; init; }

    /// <summary>The name of the backup <c>cameraagent restore</c> puts back, in the product root's backup folder (#1353).</summary>
    public string? Archive { get; init; }

    /// <summary>
    /// The folder a backup or restore run as root keeps its archives in under an admitted test product root, from
    /// <c>HVO_INSTALLER_BACKUP_FOLDER</c>, so a test can exercise root's vault without the production folder. It is not an
    /// input of the operation, so it is not part of the request hash.
    /// </summary>
    public string? TestBackupFolder { get; init; }

    /// <summary>
    /// The release.json of the release an upgrade installs. The upgrade shows its notes and refuses an older target
    /// before anything changes. It is set only from the verified release.json that also selects the candidate image
    /// (<see cref="Release"/>), so a document is never paired with an image it does not describe. It is not part of the
    /// request hash, because it adds no input to the operation.
    /// </summary>
    public ReleaseManifest? TargetRelease { get; init; }

    public void Validate()
    {
        ProductRootAdmission.Validate(ProductRoot, ProductionProductRoot, AllowTestProductRoot);
        if (TargetRelease is not null && Operation != LifecycleOperationKind.Upgrade)
        {
            throw new InstallUsageException("A release's upgrade notes apply only to an upgrade.");
        }
        if (Operation is not null and not LifecycleOperationKind.CatalogInstall and not LifecycleOperationKind.CatalogGarbageCollect &&
            InstanceId is null)
        {
            throw new InstallUsageException("--instance-id is required.");
        }
        if (Operation == LifecycleOperationKind.Upgrade)
        {
            ValidateImage();
        }
        if (Operation == LifecycleOperationKind.Restore && (Archive is null || !BackupVaultFiles.IsName(Archive)))
        {
            throw new InstallUsageException("restore requires the name of a backup in the backup folder, such as hvo-sky-<instance>-<time>-<operation>.tar.gz.");
        }
        if (Operation != LifecycleOperationKind.Restore && Archive is not null)
        {
            throw new InstallUsageException("Only restore takes a backup archive.");
        }
        if (TestBackupFolder is not null)
        {
            if (Operation is not (LifecycleOperationKind.Backup or LifecycleOperationKind.Restore) ||
                string.Equals(ProductRoot, ProductionProductRoot, StringComparison.Ordinal))
            {
                throw new InstallUsageException("HVO_INSTALLER_BACKUP_FOLDER applies only to a backup or restore under a test product root.");
            }
            ValidateAbsolutePath(TestBackupFolder, "HVO_INSTALLER_BACKUP_FOLDER");
        }
        if (Operation == LifecycleOperationKind.Purge && ConfirmationInstanceId != InstanceId)
        {
            throw new InstallUsageException("purge requires --confirm-instance-id matching --instance-id.");
        }
        if (Operation == LifecycleOperationKind.CatalogInstall && CatalogBundle is null)
        {
            throw new InstallUsageException("catalog install requires --catalog-bundle.");
        }
        if (Operation == LifecycleOperationKind.CatalogInstall)
        {
            ValidateAbsolutePath(CatalogBundle!, "--catalog-bundle");
        }
        if (Operation == LifecycleOperationKind.CatalogGarbageCollect && !DryRun && CatalogVersion is null)
        {
            throw new InstallUsageException("catalog gc requires --catalog-version unless --dry-run is used.");
        }
        if (Resume && Operation is null)
        {
            throw new InstallUsageException("--resume requires a lifecycle operation.");
        }
        if (Resume && DryRun) throw new InstallUsageException("--resume cannot be combined with --dry-run.");
        if (RestoreOnly && (!Resume || Operation != LifecycleOperationKind.Upgrade ||
                            RecoveryOperationId is null || RecoveryOperationId == Guid.Empty ||
                            Release is not null))
        {
            throw new InstallUsageException(
                "--restore-only requires upgrade --resume --operation-id and the original operator-supplied image request; release recovery is not supported.");
        }
        if (!RestoreOnly && RecoveryOperationId is not null)
            throw new InstallUsageException("--operation-id requires --restore-only.");
        if (ConfirmRemovedOperationId is not null &&
            (Operation != LifecycleOperationKind.Purge || !Resume || ConfirmRemovedOperationId == Guid.Empty))
        {
            throw new InstallUsageException("--confirm-removed-operation-id requires cameraagent purge --resume and a nonempty operation ID.");
        }
        ValidateOperationOptions();
    }

    public string ComputeRequestSha256()
    {
        var value = JsonSerializer.Serialize(new
        {
            Operation,
            InstanceId,
            ProductRoot,
            ImageReference,
            ImageArchiveSha256,
            NoDownload,
            MigrationBackwardCompatible,
            // Keep the null slot stable so current password-free operation receipts survive executable replacement.
            OwnerPasswordFile = (string?)null,
            CatalogBundle,
            // The retired signed-release locators keep their empty slots, null and the local channel, so every receipt
            // journaled before they were retired (#1309) keeps the identity it was written with and stays resumable.
            CatalogManifest = (string?)null,
            CatalogIndex = (string?)null,
            CatalogVersion,
            AssetBaseUrl = (string?)null,
            Channel = 0
        });
        // The release selection is appended only when it is used, for the same reason.
        if (Release is not null)
        {
            value += JsonSerializer.Serialize(new { Release, ImageSource = ImageSource ?? ReleaseImageSource.Archive });
        }
        // A restore's archive likewise, so a --resume continues the restore of the same archive.
        if (Archive is not null)
        {
            value += JsonSerializer.Serialize(new { Archive });
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private void ValidateImage()
    {
        if (ImageSource is { } source && !Enum.IsDefined(source))
        {
            throw new InstallUsageException("--image-source must be archive or registry.");
        }
        if (Release is not null)
        {
            ValidateAbsolutePath(Release, "--release");
            if (ImageReference is not null || ImageArchive is not null || ImageArchiveSha256 is not null)
            {
                throw new InstallUsageException(
                    "--image-ref, --image-archive, and --image-archive-sha256 cannot be combined with --release.");
            }
            return;
        }
        if (ImageSource is not null)
        {
            throw new InstallUsageException("--image-source requires --release.");
        }
        if (ImageReference is null ||
            !System.Text.RegularExpressions.Regex.IsMatch(
                ImageReference,
                "^(sha256:[a-f0-9]{64}|[^@\\s]+@sha256:[a-f0-9]{64})$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            throw new InstallUsageException("upgrade requires an immutable --image-ref.");
        }
        if (ImageArchive is not null)
        {
            ValidateAbsolutePath(ImageArchive, "--image-archive");
            if (ImageArchiveSha256 is null || ImageArchiveSha256.Length != 64 ||
                ImageArchiveSha256.Any(static character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            {
                throw new InstallUsageException("--image-archive-sha256 is required with --image-archive.");
            }
        }
    }

    private void ValidateOperationOptions()
    {
        var hasImage = ImageReference is not null || ImageArchive is not null || ImageArchiveSha256 is not null ||
                       Release is not null || ImageSource is not null || NoDownload || MigrationBackwardCompatible;
        var hasCatalogSource = CatalogBundle is not null;
        switch (Operation)
        {
            case LifecycleOperationKind.Upgrade:
                Reject(hasCatalogSource || CatalogVersion is not null || ConfirmationInstanceId is not null,
                    "Image upgrade does not accept catalog or purge options.");
                break;
            case LifecycleOperationKind.CatalogInstall:
                Reject(hasImage || InstanceId is not null || ConfirmationInstanceId is not null || CatalogVersion is not null,
                    "Catalog install accepts only --catalog-bundle and common options.");
                break;
            case LifecycleOperationKind.CatalogSelect:
                Reject(hasImage || hasCatalogSource || ConfirmationInstanceId is not null,
                    "Catalog select accepts only --catalog-version and instance/common options.");
                break;
            case LifecycleOperationKind.CatalogGarbageCollect:
                Reject(hasImage || hasCatalogSource || InstanceId is not null || ConfirmationInstanceId is not null,
                    "Catalog garbage collection accepts only an optional --catalog-version and common options.");
                break;
            case LifecycleOperationKind.Purge:
                Reject(hasImage || hasCatalogSource || CatalogVersion is not null,
                    "Purge accepts only instance confirmation and common options.");
                break;
            case null:
                Reject(hasImage || hasCatalogSource || CatalogVersion is not null || ConfirmationInstanceId is not null ||
                       DryRun || Resume,
                    "Status accepts only instance, product-root, and JSON options.");
                break;
            default:
                Reject(hasImage || hasCatalogSource || CatalogVersion is not null || ConfirmationInstanceId is not null,
                    "The command received options owned by a different lifecycle operation.");
                break;
        }
    }

    private static void Reject(bool condition, string message)
    {
        if (condition) throw new InstallUsageException(message);
    }

    private static void ValidateAbsolutePath(string path, string option)
    {
        if (!Path.IsPathFullyQualified(path) || path.Contains("//", StringComparison.Ordinal) ||
            Path.GetFullPath(path) != path.TrimEnd('/') || path == "/")
        {
            throw new InstallUsageException($"{option} must be an absolute normalized path other than root.");
        }
    }
}

/// <summary>
/// <c>host set-product-root &lt;path&gt;</c>: names the production product root in the root-owned host policy. The
/// write itself happens only in the narrow privileged helper, which re-reads and re-validates the policy.
/// </summary>
internal sealed record HostProductRootRequest(string ProductRoot, bool Json) : DeploymentCommand(Json)
{
    /// <summary>
    /// The policy this command writes. Production always writes <see cref="HostPolicyLocation.Production"/> through
    /// <c>sudo</c>; an isolated test carries its own location on the request instead of redirecting process state.
    /// </summary>
    public HostPolicyLocation PolicyLocation { get; init; } = HostPolicyLocation.Production;

    public void Validate()
    {
        if (HostProductRootPolicy.ProductRootTextError(ProductRoot) is { } error)
        {
            throw new InstallUsageException($"host set-product-root: {error}");
        }
    }
}
