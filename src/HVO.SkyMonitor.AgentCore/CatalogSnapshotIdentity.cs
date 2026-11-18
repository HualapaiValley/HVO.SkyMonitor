namespace HVO.SkyMonitor.AgentCore;

/// <summary>Immutable package and byte identity of one independently selected catalog input.</summary>
public sealed record CatalogSnapshotIdentity(
    string CatalogId,
    string PackageVersion,
    string SchemaVersion,
    string PreprocessingVersion,
    string DatabaseSha256);
