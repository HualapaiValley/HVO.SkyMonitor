using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.Deployment;

internal sealed record CatalogLineage(
    int SchemaVersion,
    string CatalogId,
    string PackageKind,
    string PackageLineage);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CatalogLineage))]
internal sealed partial class CatalogLineageJsonContext : JsonSerializerContext;
