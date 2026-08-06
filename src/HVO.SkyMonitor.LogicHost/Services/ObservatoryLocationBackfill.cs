using System.Data;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.LogicHost.Services;

internal static partial class ObservatoryLocationBackfill
{
    internal static async Task<int> RunAsync(
        ApplicationDbContext dbContext,
        TimeProvider timeProvider,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
        => await RunCoreAsync(dbContext, timeProvider, logger, false, cancellationToken).ConfigureAwait(false);

    internal static async Task<int> RunStrictAsync(
        ApplicationDbContext dbContext,
        TimeProvider timeProvider,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
        => await RunCoreAsync(dbContext, timeProvider, logger, true, cancellationToken).ConfigureAwait(false);

    private static async Task<int> RunCoreAsync(
        ApplicationDbContext dbContext,
        TimeProvider timeProvider,
        ILogger? logger,
        bool failOnInvalidLocation,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        var isRelational = dbContext.Database.IsRelational();
        await using var transaction = isRelational
            ? await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false)
            : null;
        var source = isRelational
            ? dbContext.Observatories.FromSqlRaw("""
                SELECT * FROM [Observatories] WITH (UPDLOCK, HOLDLOCK)
                WHERE [CurrentLocationVersion] IS NULL OR [CurrentLocationCanonicalSha256] IS NULL
                """)
            : dbContext.Observatories.Where(item =>
                item.CurrentLocationVersion == null || item.CurrentLocationCanonicalSha256 == null);
        var observatories = await source
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        var backfilled = 0;
        var invalid = 0;
        foreach (var observatory in observatories)
        {
            if (!TryNormalizeLegacyLocation(observatory, now))
            {
                if (logger is not null)
                {
                    Log.InvalidLegacyLocation(logger);
                }
                invalid++;
                continue;
            }
            _ = await ObservatoryLocationAuthority.EnsureCurrentVersionAsync(
                dbContext,
                observatory,
                now,
                "legacy-current-state-backfill",
                cancellationToken).ConfigureAwait(false);
            backfilled++;
        }
        if (backfilled > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (logger is not null)
            {
                Log.Completed(logger, backfilled);
            }
        }
        if (invalid > 0 && failOnInvalidLocation)
        {
            throw new InvalidOperationException(
                $"Observatory location backfill found {invalid} invalid legacy location(s); owner correction is required.");
        }
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        return backfilled;
    }

    private static partial class Log
    {
        [LoggerMessage(7404, LogLevel.Error,
            "Observatory location backfill skipped one invalid legacy location; owner correction is required")]
        internal static partial void InvalidLegacyLocation(ILogger logger);

        [LoggerMessage(7405, LogLevel.Information,
            "Observatory location backfill completed: ObservatoryCount={ObservatoryCount}")]
        internal static partial void Completed(ILogger logger, int observatoryCount);
    }

    private static bool TryNormalizeLegacyLocation(Observatory observatory, DateTimeOffset effectiveFromUtc)
    {
        var snapshot = ObservatoryLocationSnapshot.Create(
            observatory.Id,
            1,
            effectiveFromUtc,
            observatory.LatitudeDegrees,
            observatory.LongitudeDegrees,
            observatory.ElevationMeters,
            observatory.TimeZoneId,
            observatory.AllowedDeploymentRadiusMeters);
        if (snapshot.Validate().IsValid)
        {
            return true;
        }
        if (!TimeZoneInfo.TryConvertWindowsIdToIanaId(observatory.TimeZoneId, out var portableTimeZoneId))
        {
            return false;
        }
        var normalized = ObservatoryLocationSnapshot.Create(
            observatory.Id,
            1,
            effectiveFromUtc,
            observatory.LatitudeDegrees,
            observatory.LongitudeDegrees,
            observatory.ElevationMeters,
            portableTimeZoneId,
            observatory.AllowedDeploymentRadiusMeters);
        if (!normalized.Validate().IsValid)
        {
            return false;
        }
        observatory.TimeZoneId = portableTimeZoneId;
        return true;
    }
}
