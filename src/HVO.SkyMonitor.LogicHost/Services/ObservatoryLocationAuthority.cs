using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal static class ObservatoryLocationAuthority
{
    internal static async Task<ObservatoryLocationVersion> EnsureCurrentVersionAsync(
        ApplicationDbContext dbContext,
        Observatory observatory,
        DateTimeOffset now,
        string actor,
        CancellationToken cancellationToken)
    {
        if (observatory.CurrentLocationVersion is { } currentVersion &&
            observatory.CurrentLocationCanonicalSha256 is { Length: 64 } currentHash)
        {
            var current = dbContext.ObservatoryLocationVersions.Local.FirstOrDefault(item =>
                item.ObservatoryId == observatory.Id && item.Version == currentVersion)
                ?? await dbContext.ObservatoryLocationVersions.FirstOrDefaultAsync(item =>
                    item.ObservatoryId == observatory.Id && item.Version == currentVersion,
                    cancellationToken).ConfigureAwait(false);
            if (current is not null && string.Equals(
                    current.CanonicalSha256, currentHash, StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }
        }
        throw new InvalidOperationException("Observatory location authority is incomplete.");
    }

    internal static async Task<ObservatoryLocationVersion> ApplyAsync(
        ApplicationDbContext dbContext,
        Observatory observatory,
        double latitudeDegrees,
        double longitudeDegrees,
        double elevationMeters,
        string timeZoneId,
        double? allowedDeploymentRadiusMeters,
        DateTimeOffset now,
        string actor,
        CancellationToken cancellationToken)
    {
        now = ToMilliseconds(now);
        ObservatoryLocationVersion? current = null;
        if (observatory.CurrentLocationVersion is not null
            || await dbContext.ObservatoryLocationVersions.AnyAsync(
                item => item.ObservatoryId == observatory.Id,
                cancellationToken).ConfigureAwait(false))
        {
            current = await EnsureCurrentVersionAsync(
                dbContext, observatory, now, actor, cancellationToken).ConfigureAwait(false);
        }
        if (current is null)
        {
            observatory.LatitudeDegrees = latitudeDegrees;
            observatory.LongitudeDegrees = longitudeDegrees;
            observatory.ElevationMeters = elevationMeters;
            observatory.TimeZoneId = timeZoneId;
            observatory.AllowedDeploymentRadiusMeters = allowedDeploymentRadiusMeters;
            return AddVersion(dbContext, observatory, 1, now, actor);
        }
        if (current.LatitudeDegrees.Equals(latitudeDegrees) &&
            current.LongitudeDegrees.Equals(longitudeDegrees) &&
            current.ElevationMeters.Equals(elevationMeters) &&
            string.Equals(current.TimeZoneId, timeZoneId, StringComparison.Ordinal) &&
            Nullable.Equals(current.AllowedDeploymentRadiusMeters, allowedDeploymentRadiusMeters))
        {
            return current;
        }

        current.SupersededAtUtc ??= now;
        observatory.LatitudeDegrees = latitudeDegrees;
        observatory.LongitudeDegrees = longitudeDegrees;
        observatory.ElevationMeters = elevationMeters;
        observatory.TimeZoneId = timeZoneId;
        observatory.AllowedDeploymentRadiusMeters = allowedDeploymentRadiusMeters;
        return AddVersion(dbContext, observatory, current.Version + 1, now, actor);
    }

    internal static ObservatoryLocationSnapshot ToSnapshot(ObservatoryLocationVersion version)
        => new(
            version.ObservatoryId,
            version.Version,
            version.CanonicalSha256,
            version.EffectiveFromUtc,
            version.LatitudeDegrees,
            version.LongitudeDegrees,
            version.ElevationMeters,
            version.TimeZoneId,
            version.AllowedDeploymentRadiusMeters);

    internal static bool AppliesAt(ObservatoryLocationVersion version, DateTimeOffset capturedAtUtc)
        => capturedAtUtc >= version.EffectiveFromUtc
            && (version.SupersededAtUtc is null || capturedAtUtc < version.SupersededAtUtc);

    private static ObservatoryLocationVersion AddVersion(
        ApplicationDbContext dbContext,
        Observatory observatory,
        long version,
        DateTimeOffset now,
        string actor)
    {
        var snapshot = ObservatoryLocationSnapshot.Create(
            observatory.Id,
            version,
            now,
            observatory.LatitudeDegrees,
            observatory.LongitudeDegrees,
            observatory.ElevationMeters,
            observatory.TimeZoneId,
            observatory.AllowedDeploymentRadiusMeters);
        var validation = snapshot.Validate();
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"Observatory location is invalid: {validation.ReasonCode} ({validation.FieldPath}).");
        }
        var entity = new ObservatoryLocationVersion
        {
            Observatory = observatory,
            ObservatoryId = observatory.Id,
            Version = snapshot.Version,
            CanonicalSha256 = snapshot.CanonicalSha256,
            EffectiveFromUtc = snapshot.EffectiveFromUtc,
            LatitudeDegrees = snapshot.LatitudeDegrees,
            LongitudeDegrees = snapshot.LongitudeDegrees,
            ElevationMeters = snapshot.ElevationMeters,
            TimeZoneId = snapshot.TimeZoneId,
            AllowedDeploymentRadiusMeters = snapshot.AllowedDeploymentRadiusMeters,
            RecordedAtUtc = now,
            RecordedBy = actor
        };
        observatory.CurrentLocationVersion = snapshot.Version;
        observatory.CurrentLocationCanonicalSha256 = snapshot.CanonicalSha256;
        dbContext.ObservatoryLocationVersions.Add(entity);
        return entity;
    }

    private static DateTimeOffset ToMilliseconds(DateTimeOffset value)
        => DateTimeOffset.FromUnixTimeMilliseconds(value.ToUniversalTime().ToUnixTimeMilliseconds());
}
