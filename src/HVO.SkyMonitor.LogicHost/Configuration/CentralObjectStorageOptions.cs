using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.LogicHost.Configuration;

[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "The public diagnostics controller accepts these typed options through dependency injection.")]
public sealed class CentralObjectStorageOptions
{
    public const string SectionName = "ObjectStorage";
    public const string DefaultArtifactBucket = "skymonitor-artifacts";
    public const string DefaultDiagnosticsBucket = "skymonitor-diagnostics";

    [Required]
    [RegularExpression("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$")]
    public string ArtifactBucket { get; set; } = DefaultArtifactBucket;

    [Required]
    [RegularExpression("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$")]
    public string DiagnosticsBucket { get; set; } = DefaultDiagnosticsBucket;

    public string ArtifactPrefix => $"minio://{ArtifactBucket}/";
}
