using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

internal abstract record DeploymentCommand(bool Json);

internal sealed record InstallDeploymentCommand(InstallRequest Request) : DeploymentCommand(Request.Json);

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
    public string? ImageReference { get; init; }
    public string? ImageArchive { get; init; }
    public string? ImageArchiveSha256 { get; init; }
    public bool NoDownload { get; init; }
    public bool MigrationBackwardCompatible { get; init; }
    public string? OwnerPasswordFile { get; init; }
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
            Environment.GetEnvironmentVariable("HVO_INSTALLER_ALLOW_TEST_ROOT") != "1")
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
        if (OwnerPasswordFile is not null) ValidateAbsolutePath(OwnerPasswordFile, "--owner-password-file");
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
            OwnerPasswordFile,
            CatalogBundle,
            CatalogManifest,
            CatalogIndex,
            CatalogVersion,
            AssetBaseUrl,
            Channel
        });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private void ValidateImage()
    {
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
                       NoDownload || MigrationBackwardCompatible;
        var hasCatalogSource = CatalogBundle is not null || CatalogManifest is not null || CatalogIndex is not null ||
                               AssetBaseUrl is not null || Channel != DistributionChannel.Local;
        switch (Operation)
        {
            case LifecycleOperationKind.Upgrade:
                Reject(hasCatalogSource || CatalogVersion is not null || ConfirmationInstanceId is not null,
                    "Image upgrade does not accept catalog or purge options.");
                break;
            case LifecycleOperationKind.CatalogInstall:
                Reject(hasImage || InstanceId is not null || ConfirmationInstanceId is not null || OwnerPasswordFile is not null,
                    "Catalog install accepts only catalog acquisition and common options.");
                break;
            case LifecycleOperationKind.CatalogSelect:
                Reject(hasImage || hasCatalogSource || ConfirmationInstanceId is not null,
                    "Catalog select accepts only --catalog-version and instance/common options.");
                break;
            case LifecycleOperationKind.CatalogGarbageCollect:
                Reject(hasImage || hasCatalogSource || InstanceId is not null || ConfirmationInstanceId is not null || OwnerPasswordFile is not null,
                    "Catalog garbage collection accepts only an optional --catalog-version and common options.");
                break;
            case LifecycleOperationKind.Purge:
                Reject(hasImage || hasCatalogSource || CatalogVersion is not null || OwnerPasswordFile is not null,
                    "Purge accepts only instance confirmation and common options.");
                break;
            case null:
                Reject(hasImage || hasCatalogSource || CatalogVersion is not null || ConfirmationInstanceId is not null ||
                       OwnerPasswordFile is not null || DryRun || Resume,
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
