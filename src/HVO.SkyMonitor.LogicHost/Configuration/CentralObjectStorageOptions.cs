using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.LogicHost.Configuration;

/// <summary>
/// Provider-neutral object storage configuration for LogicHost. The application selects a
/// provider by <see cref="Provider"/> and consumes only the neutral surface (bucket names and
/// the logical <c>object://</c> identity); provider-specific transport settings live in their
/// own option group and are read only by that provider's infrastructure adapter.
/// </summary>
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "The public diagnostics controller accepts these typed options through dependency injection.")]
public sealed class CentralObjectStorageOptions
{
    public const string SectionName = "ObjectStorage";
    public const string DefaultArtifactBucket = "skymonitor-artifacts";
    public const string DefaultDiagnosticsBucket = "skymonitor-diagnostics";

    /// <summary>
    /// The scheme of every persisted storage reference. It names a logical object identity
    /// (bucket and key) that every provider resolves, so the reference does not change when
    /// the physical provider does.
    /// </summary>
    public const string LogicalScheme = "object://";

    /// <summary>
    /// Which provider adapter serves <c>IObjectStore</c>. <see cref="ObjectStorageProvider.S3"/>
    /// is the only adapter delivered today and is therefore the default; the supported
    /// first-release profile is <see cref="ObjectStorageProvider.Filesystem"/> once #585
    /// delivers it, and installer preflight is what enforces that profile, not this class.
    /// </summary>
    [EnumDataType(typeof(ObjectStorageProvider))]
    public ObjectStorageProvider Provider { get; set; } = ObjectStorageProvider.S3;

    [Required]
    [RegularExpression("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$")]
    public string ArtifactBucket { get; set; } = DefaultArtifactBucket;

    [Required]
    [RegularExpression("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$")]
    public string DiagnosticsBucket { get; set; } = DefaultDiagnosticsBucket;

    public string ArtifactPrefix => $"{LogicalScheme}{ArtifactBucket}/";

    /// <summary>
    /// S3-transport settings. Bound from the same <c>ObjectStorage</c> section as before, so
    /// existing <c>ObjectStorage__ServiceEndpoint</c>-style keys keep their meaning; they are
    /// only honoured when <see cref="Provider"/> is <see cref="ObjectStorageProvider.S3"/>.
    /// </summary>
    public S3ObjectStorageOptions S3 { get; } = new();

    /// <summary>
    /// Filesystem-provider settings. Supported local deployments select this provider explicitly;
    /// a configured root with <see cref="Provider"/> not set to Filesystem remains a validation
    /// error so a contradictory deployment fails closed rather than silently using S3.
    /// </summary>
    public FilesystemObjectStorageOptions Filesystem { get; } = new();

    // Flattened S3 members, bound from ObjectStorage:* for compatibility with the existing
    // deployment inventory and compose files. They forward to the S3 option group so there
    // is exactly one storage location for each value.
    public string? ServiceEndpoint { get => S3.ServiceEndpoint; set => S3.ServiceEndpoint = value; }

    public string Region { get => S3.Region; set => S3.Region = value; }

    public bool UseTls { get => S3.UseTls; set => S3.UseTls = value; }

    public ObjectStorageAddressingStyle AddressingStyle { get => S3.AddressingStyle; set => S3.AddressingStyle = value; }

    public ObjectStorageCredentialMode CredentialMode { get => S3.CredentialMode; set => S3.CredentialMode = value; }

    public string? AccessKey { get => S3.AccessKey; set => S3.AccessKey = value; }

    public string? SecretKey { get => S3.SecretKey; set => S3.SecretKey = value; }

    public string? SessionToken { get => S3.SessionToken; set => S3.SessionToken = value; }

    /// <summary>True when any S3-transport value differs from its default.</summary>
    public bool HasS3Settings
        => !string.IsNullOrWhiteSpace(S3.ServiceEndpoint)
            || !string.Equals(S3.Region, S3ObjectStorageOptions.DefaultRegion, StringComparison.Ordinal)
            || !S3.UseTls
            || S3.AddressingStyle != ObjectStorageAddressingStyle.VirtualHost
            || S3.CredentialMode != ObjectStorageCredentialMode.DefaultChain
            || !string.IsNullOrWhiteSpace(S3.AccessKey)
            || !string.IsNullOrWhiteSpace(S3.SecretKey)
            || !string.IsNullOrWhiteSpace(S3.SessionToken);

    /// <summary>True when any filesystem-provider value is set.</summary>
    public bool HasFilesystemSettings => !string.IsNullOrWhiteSpace(Filesystem.Root);
}

[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "Member type of the public options class.")]
public enum ObjectStorageProvider
{
    /// <summary>Local durable filesystem provider; delivered by #585, the supported first-release profile.</summary>
    Filesystem,

    /// <summary>AWS SDK S3-compatible transport; retained for development and for the #589 external profile.</summary>
    S3
}

[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "Nested option group of a public options type.")]
public sealed class S3ObjectStorageOptions
{
    public const string DefaultRegion = "us-east-1";

    public string? ServiceEndpoint { get; set; }

    [Required]
    public string Region { get; set; } = DefaultRegion;

    public bool UseTls { get; set; } = true;

    [EnumDataType(typeof(ObjectStorageAddressingStyle))]
    public ObjectStorageAddressingStyle AddressingStyle { get; set; } = ObjectStorageAddressingStyle.VirtualHost;

    [EnumDataType(typeof(ObjectStorageCredentialMode))]
    public ObjectStorageCredentialMode CredentialMode { get; set; } = ObjectStorageCredentialMode.DefaultChain;

    public string? AccessKey { get; set; }

    public string? SecretKey { get; set; }

    public string? SessionToken { get; set; }
}

[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "Nested option group of a public options type.")]
public sealed class FilesystemObjectStorageOptions
{
    /// <summary>
    /// Absolute path of the directory that owns every bucket. Validated for shape here;
    /// existence, ownership, mode and durability are the provider's concern (#585).
    /// </summary>
    public string? Root { get; set; }
}

public enum ObjectStorageAddressingStyle
{
    Path,
    VirtualHost
}

public enum ObjectStorageCredentialMode
{
    DefaultChain,
    Static,
    Session
}
