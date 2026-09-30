using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

internal abstract record DeploymentCommand(bool Json)
{
    /// <summary>
    /// Whether this invocation may address a product root other than <see cref="InstallRequest.DefaultProductRoot"/>.
    /// Deny by default and decided once at the command-line boundary, so validation answers from the request it
    /// was given rather than from process state that anything else can change under it.
    /// </summary>
    public bool AllowTestProductRoot { get; init; }
}

internal sealed record InstallDeploymentCommand(InstallRequest Request) : DeploymentCommand(Request.Json);

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
        ValidateAbsolutePath(ProductRoot, "--product-root");
        if (!string.Equals(ProductRoot, InstallRequest.DefaultProductRoot, StringComparison.Ordinal) &&
            !AllowTestProductRoot)
        {
            throw new InstallUsageException("--product-root must be /var/lib/hvo/skymonitor outside isolated tests.");
        }
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
    public string? ImageManifest { get; init; }
    public string? ImageIndex { get; init; }
    public string? ImageVersion { get; init; }
    public string? AssetBaseUrl { get; init; }
    public bool NoDownload { get; init; }

    /// <summary>
    /// The operator-selected distribution channel, or <c>null</c> when none was named. Presence is retained rather
    /// than collapsed onto the <c>Local</c> default so an explicit <c>--channel local</c> is rejected without a
    /// signed release exactly as any other explicit channel is.
    /// </summary>
    public DistributionChannel? Channel { get; init; }

    /// <summary>Whether the operator named a signed image release instead of an image already established here.</summary>
    public bool NamesSignedImageRelease => ImageManifest is not null || ImageIndex is not null || ImageVersion is not null;

    public void Validate()
    {
        if (InstanceId is null)
        {
            throw new InstallUsageException("--instance-id is required.");
        }
        ValidateProductRoot(ProductRoot, AllowTestProductRoot);
        if (NamesSignedImageRelease)
        {
            // A preflight selects its candidate by exactly the rules an install and an upgrade use, so the mutual
            // exclusion with --image-ref, the index/version pairing, and the locator/channel rules all produce the
            // same usage errors here that they produce there.
            ImageSelection().ValidateImageSelection();
            return;
        }
        if (AssetBaseUrl is not null || Channel is not null || NoDownload)
        {
            throw new InstallUsageException(
                "--asset-base-url, --channel, and --no-download require --image-manifest or --image-index.");
        }
        if (ImageReference is not null &&
            !System.Text.RegularExpressions.Regex.IsMatch(
                ImageReference,
                "^(sha256:[a-f0-9]{64}|[^@\\s]+@sha256:[a-f0-9]{64})$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            throw new InstallUsageException("preflight requires an immutable --image-ref.");
        }
    }

    /// <summary>
    /// The image-train selectors this preflight resolves, carrying no instance state. It is validated as an
    /// install request so one set of selection rules governs install, upgrade, and preflight alike.
    /// </summary>
    internal InstallRequest ImageSelection() => new()
    {
        AllowTestProductRoot = AllowTestProductRoot,
        FriendlyName = "preflight",
        OwnerEmail = "preflight@localhost.invalid",
        ImageReference = ImageReference ?? string.Empty,
        ImageManifest = ImageManifest,
        ImageIndex = ImageIndex,
        ImageVersion = ImageVersion,
        AssetBaseUrl = AssetBaseUrl,
        Channel = Channel ?? DistributionChannel.Local,
        NoDownload = NoDownload
    };

    internal static void ValidateProductRoot(string productRoot, bool allowTestProductRoot)
    {
        if (!Path.IsPathFullyQualified(productRoot) || productRoot.Contains("//", StringComparison.Ordinal) ||
            Path.GetFullPath(productRoot) != productRoot.TrimEnd('/') || productRoot == "/")
        {
            throw new InstallUsageException("--product-root must be an absolute normalized path other than root.");
        }
        if (!string.Equals(productRoot, InstallRequest.DefaultProductRoot, StringComparison.Ordinal) &&
            !allowTestProductRoot)
        {
            throw new InstallUsageException("--product-root must be /var/lib/hvo/skymonitor outside isolated tests.");
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
        CameraAgentStatePreflightRequest.ValidateProductRoot(ProductRoot, AllowTestProductRoot);
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
    public string? ImageManifest { get; init; }
    public string? ImageIndex { get; init; }
    public string? ImageVersion { get; init; }
    public bool NoDownload { get; init; }
    public bool MigrationBackwardCompatible { get; init; }
    public Guid? ConfirmationInstanceId { get; init; }
    public string? CatalogBundle { get; init; }
    public string? CatalogManifest { get; init; }
    public string? CatalogIndex { get; init; }
    public string? CatalogVersion { get; init; }
    public string? AssetBaseUrl { get; init; }
    public DistributionChannel Channel { get; init; }

    public void Validate()
    {
        ValidateAbsolutePath(ProductRoot, "--product-root");
        if (!string.Equals(ProductRoot, InstallRequest.DefaultProductRoot, StringComparison.Ordinal) &&
            !AllowTestProductRoot)
        {
            throw new InstallUsageException("--product-root must be /var/lib/hvo/skymonitor outside isolated tests.");
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
        if (Operation == LifecycleOperationKind.Purge && ConfirmationInstanceId != InstanceId)
        {
            throw new InstallUsageException("purge requires --confirm-instance-id matching --instance-id.");
        }
        if (Operation == LifecycleOperationKind.CatalogInstall &&
            CatalogBundle is null && CatalogManifest is null && CatalogIndex is null)
        {
            throw new InstallUsageException("catalog install requires --catalog-bundle, --catalog-manifest, or --catalog-index.");
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
                            ImageManifest is not null || ImageIndex is not null))
        {
            throw new InstallUsageException(
                "--restore-only requires upgrade --resume --operation-id and the original operator-supplied image request; signed-release recovery is not supported.");
        }
        if (!RestoreOnly && RecoveryOperationId is not null)
            throw new InstallUsageException("--operation-id requires --restore-only.");
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
            CatalogManifest,
            CatalogIndex,
            CatalogVersion,
            AssetBaseUrl,
            Channel
        });
        // The signed image-release selection is appended only when it is used, so every operation receipt written
        // before this train existed keeps the identity it was journaled with and stays resumable.
        if (ImageManifest is not null || ImageIndex is not null || ImageVersion is not null)
        {
            value += JsonSerializer.Serialize(new { ImageManifest, ImageIndex, ImageVersion });
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private void ValidateImage()
    {
        if (ImageManifest is not null && ImageIndex is not null)
        {
            throw new InstallUsageException("--image-manifest cannot be combined with --image-index.");
        }
        if (ImageVersion is not null && ImageIndex is null)
        {
            throw new InstallUsageException("--image-version requires --image-index.");
        }
        if (ImageManifest is not null || ImageIndex is not null)
        {
            if (ImageReference is not null || ImageArchive is not null || ImageArchiveSha256 is not null)
            {
                throw new InstallUsageException(
                    "--image-ref, --image-archive, and --image-archive-sha256 cannot be combined with a signed image release.");
            }
            return;
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
        var hasSignedImage = ImageManifest is not null || ImageIndex is not null || ImageVersion is not null;
        var hasImage = ImageReference is not null || ImageArchive is not null || ImageArchiveSha256 is not null ||
                       hasSignedImage || NoDownload || MigrationBackwardCompatible;
        var hasCatalogBundleSource = CatalogBundle is not null || CatalogManifest is not null || CatalogIndex is not null;
        // A signed image upgrade resolves its release through the same locator and channel options the catalog train
        // uses, so those two options belong to an image upgrade as well and are only rejected without one.
        var hasCatalogSource = hasCatalogBundleSource || AssetBaseUrl is not null || Channel != DistributionChannel.Local;
        switch (Operation)
        {
            case LifecycleOperationKind.Upgrade:
                Reject((hasSignedImage ? hasCatalogBundleSource : hasCatalogSource) ||
                       CatalogVersion is not null || ConfirmationInstanceId is not null,
                    "Image upgrade does not accept catalog or purge options.");
                break;
            case LifecycleOperationKind.CatalogInstall:
                Reject(hasImage || InstanceId is not null || ConfirmationInstanceId is not null,
                    "Catalog install accepts only catalog acquisition and common options.");
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
