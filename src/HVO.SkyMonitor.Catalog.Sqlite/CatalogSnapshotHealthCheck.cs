using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.SkyMonitor.Catalog.Sqlite;

/// <summary>Reports the identity and package kind of the already loaded catalog snapshot.</summary>
public sealed class CatalogSnapshotHealthCheck : IHealthCheck
{
    private const int MaximumCatalogVersionLength = 64;
    private const int MaximumComponentVersionLength = 32;
    private readonly HealthCheckResult _result;

    /// <summary>Creates a health check from a resolved immutable snapshot without retaining file access.</summary>
    public CatalogSnapshotHealthCheck(CatalogSnapshotResult snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateVersion(snapshot.CatalogId, nameof(snapshot.CatalogId), MaximumCatalogVersionLength);
        ValidateVersion(snapshot.CatalogVersion, nameof(snapshot.CatalogVersion), MaximumCatalogVersionLength);
        ValidateVersion(snapshot.SchemaVersion, nameof(snapshot.SchemaVersion), MaximumComponentVersionLength);
        ValidateVersion(snapshot.PreprocessingVersion, nameof(snapshot.PreprocessingVersion), MaximumComponentVersionLength);
        if (snapshot.DatabaseSha256.Length != 64 || !snapshot.DatabaseSha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("The catalog database SHA-256 is invalid.", nameof(snapshot));
        }
        if (snapshot.RowCount is <= 0 or > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(snapshot), "The catalog row count is not bounded.");
        }

        var data = new ReadOnlyDictionary<string, object>(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Kind"] = snapshot.PackageKind.ToString(),
            ["CatalogId"] = snapshot.CatalogId,
            ["CatalogIdentitySource"] = snapshot.CatalogIdDerivedFromLegacyManifest
                ? "derived-manifest-v1"
                : "explicit-manifest-v2",
            ["CatalogVersion"] = snapshot.CatalogVersion,
            ["SchemaVersion"] = snapshot.SchemaVersion,
            ["PreprocessingVersion"] = snapshot.PreprocessingVersion,
            ["DatabaseSha256"] = snapshot.DatabaseSha256,
            ["RowCount"] = snapshot.RowCount
        });
        _result = snapshot.PackageKind switch
        {
            CatalogSnapshotPackageKind.Production => HealthCheckResult.Healthy(
                "Production celestial catalog snapshot is installed.", data),
            CatalogSnapshotPackageKind.Fixture => HealthCheckResult.Degraded(
                "Fixture celestial catalog snapshot is installed; production data is not active.", null, data),
            _ => throw new ArgumentOutOfRangeException(nameof(snapshot), "The catalog package kind is invalid.")
        };
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_result);
    }

    private static void ValidateVersion(string value, string name, int maximumLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maximumLength ||
            !value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '+' or '-'))
        {
            throw new ArgumentException($"The catalog {name} is invalid or unbounded.", nameof(value));
        }
    }
}

/// <summary>Registers catalog snapshot health reporting.</summary>
public static class CatalogSnapshotHealthCheckBuilderExtensions
{
    /// <summary>Registers the installed snapshot health check under the stable <c>catalog</c> name.</summary>
    public static IHealthChecksBuilder AddInstalledCelestialCatalogHealthCheck(this IHealthChecksBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddCheck<CatalogSnapshotHealthCheck>("catalog", tags: ["dependency"]);
    }
}
