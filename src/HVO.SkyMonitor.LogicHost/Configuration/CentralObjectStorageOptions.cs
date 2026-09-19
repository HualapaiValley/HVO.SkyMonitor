using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.LogicHost.Configuration;

[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "The public diagnostics controller accepts these typed options through dependency injection.")]
public sealed class CentralObjectStorageOptions
{
    public const string SectionName = "ObjectStorage";
    public const string DefaultArtifactBucket = "skymonitor-artifacts";
    public const string DefaultDiagnosticsBucket = "skymonitor-diagnostics";

    public string? ServiceEndpoint { get; set; }

    [Required]
    public string Region { get; set; } = "us-east-1";

    public bool UseTls { get; set; } = true;

    [EnumDataType(typeof(ObjectStorageAddressingStyle))]
    public ObjectStorageAddressingStyle AddressingStyle { get; set; } = ObjectStorageAddressingStyle.VirtualHost;

    [EnumDataType(typeof(ObjectStorageCredentialMode))]
    public ObjectStorageCredentialMode CredentialMode { get; set; } = ObjectStorageCredentialMode.DefaultChain;

    public string? AccessKey { get; set; }

    public string? SecretKey { get; set; }

    public string? SessionToken { get; set; }

    [Required]
    [RegularExpression("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$")]
    public string ArtifactBucket { get; set; } = DefaultArtifactBucket;

    [Required]
    [RegularExpression("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$")]
    public string DiagnosticsBucket { get; set; } = DefaultDiagnosticsBucket;

    /// <summary>
    /// The scheme of every persisted storage reference. It names a logical object identity
    /// (bucket and key) that every provider resolves, so the reference does not change when
    /// the physical provider does.
    /// </summary>
    public const string LogicalScheme = "object://";

    public string ArtifactPrefix => $"{LogicalScheme}{ArtifactBucket}/";
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
