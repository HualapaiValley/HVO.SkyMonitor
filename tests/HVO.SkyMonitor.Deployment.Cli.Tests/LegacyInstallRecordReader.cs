// Reader declarations copied from 6aec42656b28029be8336d39bc8710574a062115.
// Intentionally independent of current record declarations: released readers disallow unknown JSON members.
using System.Text.Json.Serialization;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

internal sealed record LegacyInstallRecord(
    int SchemaVersion,
    string Product,
    string ProductRoot,
    string InstallerVersion,
    DateTimeOffset UpdatedUtc,
    IReadOnlyList<LegacyInstallRecordInstance> Instances)
{
    public const int CurrentSchemaVersion = 1;

    public const string ProductName = "HVO.SkyMonitor";

    public const string FileName = "install.json";

    /// <summary>Whether the two say the same, whenever each was written.</summary>
    public bool SameAs(LegacyInstallRecord? other)
        => other is not null &&
           SchemaVersion == other.SchemaVersion && Product == other.Product && ProductRoot == other.ProductRoot &&
           InstallerVersion == other.InstallerVersion && Instances.Count == other.Instances.Count &&
           Instances.Zip(other.Instances).All(static pair => pair.First.SameAs(pair.Second));
}

/// <summary>
/// One instance in the install record. Every version in it is read from the instance's own release records
/// (<see cref="ReleaseRecords.VersionOfAsync"/>), which own the installed version; the entry only says which of them it
/// is. <see cref="Version"/> is the release the instance runs; <see cref="PreviousVersion"/> is the release of the image
/// it ran before its last upgrade, which a rollback returns to, and is <see langword="null"/> after a rollback;
/// <see cref="RolledBackFrom"/> is the release a rollback left, <see langword="null"/> after an upgrade. Each is
/// <see langword="null"/> when no release names the image (an image given by <c>--image-ref</c>).
/// <see cref="LastImageChange"/> says whether the image last changed by upgrade or rollback, which decides those two.
/// </summary>
internal sealed record LegacyInstallRecordInstance(
    string Component,
    Guid InstanceId,
    string FriendlyName,
    InstanceLifecycleCondition Condition,
    string? Version,
    string? PreviousVersion,
    string? RolledBackFrom,
    string ImageId,
    string? PreviousImageId,
    Uri Url,
    ContainerResourceLimits? Resources,
    DateTimeOffset UpdatedUtc,
    LegacyInstallRecordImageChange? LastImageChange = null)
{
    public const string CameraAgent = "CameraAgent";

    /// <summary>Whether the two say the same, whenever each was written.</summary>
    public bool SameAs(LegacyInstallRecordInstance? other) => other is not null && this with { UpdatedUtc = other.UpdatedUtc } == other;
}

/// <summary>How an instance's image last changed.</summary>
internal enum LegacyInstallRecordImageChange
{
    Upgrade,
    Rollback
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(LegacyInstallRecord))]
[JsonSerializable(typeof(LegacyInstallRecordInstance))]
internal sealed partial class LegacyInstallRecordJsonContext : JsonSerializerContext;
