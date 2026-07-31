using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal enum ObservatoryPublicationMutationOutcome
{
    Applied,
    Unchanged,
    NotFoundOrDenied,
    Conflict,
    Invalid
}

internal sealed record ObservatoryPublicationProfileRequest(
    string PublicSlug,
    string PublicDisplayName,
    string PublicDescription,
    ObservatoryProfileVisibility ProfileVisibility,
    bool PublishEnvironmentalSummary,
    bool AllowAutomaticVerifiedEventInclusion,
    string ReasonCode);

internal sealed record ObservatoryLocationDisclosureRequest(
    ObservatoryLocationDisclosureLevel DisclosureLevel,
    string? RegionCode,
    string? RegionLabel,
    double? PublicLatitudeDegrees,
    double? PublicLongitudeDegrees,
    double? PublicPrecisionMeters,
    string ReasonCode);

internal sealed record ObservatoryPublicationMutationResult(
    ObservatoryPublicationMutationOutcome Outcome,
    int? Version = null);

internal sealed record ObservatoryPublicationSettings(
    string PublicSlug,
    string PublicDisplayName,
    string PublicDescription,
    ObservatoryProfileVisibility ProfileVisibility,
    bool PublishEnvironmentalSummary,
    bool AllowAutomaticVerifiedEventInclusion,
    ObservatoryLocationDisclosureLevel DisclosureLevel,
    string? RegionCode,
    string? RegionLabel,
    double? PublicPrecisionMeters);

internal interface IObservatoryPublicationService
{
    Task<ObservatoryPublicationMutationResult> SetProfileAsync(
        Guid observatoryId,
        string actorUserId,
        ObservatoryPublicationProfileRequest request,
        CancellationToken cancellationToken = default);

    Task<ObservatoryPublicationMutationResult> SetLocationDisclosureAsync(
        Guid observatoryId,
        string actorUserId,
        ObservatoryLocationDisclosureRequest request,
        CancellationToken cancellationToken = default);

    Task<ObservatoryPublicationSettings?> GetSettingsAsync(
        Guid observatoryId,
        string actorUserId,
        CancellationToken cancellationToken = default);
}

internal sealed partial class ObservatoryPublicationService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<ObservatoryPublicationService>? logger = null) : IObservatoryPublicationService
{
    private const double MinimumApproximatePrecisionMeters = 25_000;

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();

    public async Task<ObservatoryPublicationSettings?> GetSettingsAsync(
        Guid observatoryId,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        if (!await ObservatoryMembershipAccess.ForOwner(dbContext, actorUserId)
            .AnyAsync(item => item.ObservatoryId == observatoryId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        return await dbContext.ObservatoryPublicationProfileVersions.AsNoTracking()
            .Where(profile => profile.ObservatoryId == observatoryId && profile.SupersededAtUtc == null)
            .Join(dbContext.ObservatoryLocationDisclosureVersions.AsNoTracking().Where(location =>
                    location.ObservatoryId == observatoryId && location.SupersededAtUtc == null),
                profile => profile.ObservatoryId,
                location => location.ObservatoryId,
                (profile, location) => new ObservatoryPublicationSettings(
                    profile.PublicSlug,
                    profile.PublicDisplayName,
                    profile.PublicDescription,
                    profile.ProfileVisibility,
                    profile.PublishEnvironmentalSummary,
                    profile.AllowAutomaticVerifiedEventInclusion,
                    location.DisclosureLevel,
                    location.RegionCode,
                    location.RegionLabel,
                    location.PublicPrecisionMeters))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ObservatoryPublicationMutationResult> SetProfileAsync(
        Guid observatoryId,
        string actorUserId,
        ObservatoryPublicationProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        ArgumentNullException.ThrowIfNull(request);
        var normalized = Normalize(request);
        if (normalized is null)
        {
            return new(ObservatoryPublicationMutationOutcome.Invalid);
        }
        var canonicalSha256 = Hash(normalized);
        var isRelational = dbContext.Database.IsRelational();
        await using var transaction = isRelational
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false)
            : null;
        if (!await LockOwnedObservatoryAsync(observatoryId, actorUserId, cancellationToken).ConfigureAwait(false))
        {
            return new(ObservatoryPublicationMutationOutcome.NotFoundOrDenied);
        }
        var current = await CurrentProfiles(isRelational, observatoryId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (current is not null && string.Equals(current.CanonicalSha256, canonicalSha256, StringComparison.Ordinal))
        {
            return new(ObservatoryPublicationMutationOutcome.Unchanged, current.Version);
        }
        if (normalized.ProfileVisibility == ObservatoryProfileVisibility.Public
            && await dbContext.ObservatoryPublicationProfileVersions.AnyAsync(item =>
                item.ObservatoryId != observatoryId
                && item.SupersededAtUtc == null
                && item.ProfileVisibility == ObservatoryProfileVisibility.Public
                && item.PublicSlug == normalized.PublicSlug, cancellationToken).ConfigureAwait(false))
        {
            return new(ObservatoryPublicationMutationOutcome.Conflict);
        }

        var now = timeProvider.GetUtcNow();
        if (current is not null)
        {
            current.SupersededAtUtc = now;
        }
        var version = (current?.Version ?? 0) + 1;
        dbContext.ObservatoryPublicationProfileVersions.Add(new ObservatoryPublicationProfileVersion
        {
            ObservatoryId = observatoryId,
            Version = version,
            PublicSlug = normalized.PublicSlug,
            PublicDisplayName = normalized.PublicDisplayName,
            PublicDescription = normalized.PublicDescription,
            ProfileVisibility = normalized.ProfileVisibility,
            PublishEnvironmentalSummary = normalized.PublishEnvironmentalSummary,
            AllowAutomaticVerifiedEventInclusion = normalized.AllowAutomaticVerifiedEventInclusion,
            EffectiveFromUtc = now,
            ActorUserId = actorUserId,
            ReasonCode = normalized.ReasonCode,
            CanonicalSha256 = canonicalSha256
        });
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsPublicationConflict(exception))
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new(ObservatoryPublicationMutationOutcome.Conflict);
        }
        if (logger is not null)
        {
            OperatorUiAuditLog.Publication(logger, "profile", "applied", normalized.ProfileVisibility.ToString());
        }
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        return new(ObservatoryPublicationMutationOutcome.Applied, version);
    }

    public async Task<ObservatoryPublicationMutationResult> SetLocationDisclosureAsync(
        Guid observatoryId,
        string actorUserId,
        ObservatoryLocationDisclosureRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        ArgumentNullException.ThrowIfNull(request);
        var normalized = Normalize(request);
        if (normalized is null)
        {
            return new(ObservatoryPublicationMutationOutcome.Invalid);
        }
        var isRelational = dbContext.Database.IsRelational();
        await using var transaction = isRelational
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false)
            : null;
        if (!await LockOwnedObservatoryAsync(observatoryId, actorUserId, cancellationToken).ConfigureAwait(false))
        {
            return new(ObservatoryPublicationMutationOutcome.NotFoundOrDenied);
        }
        ObservatoryLocationVersion? source = null;
        if (normalized.DisclosureLevel is ObservatoryLocationDisclosureLevel.Approximate
            or ObservatoryLocationDisclosureLevel.Exact)
        {
            source = await dbContext.ObservatoryLocationVersions.SingleOrDefaultAsync(item =>
                item.ObservatoryId == observatoryId && item.SupersededAtUtc == null, cancellationToken)
                .ConfigureAwait(false);
            if (source is null)
            {
                return new(ObservatoryPublicationMutationOutcome.Invalid);
            }
            if (normalized.DisclosureLevel == ObservatoryLocationDisclosureLevel.Exact)
            {
                return new(ObservatoryPublicationMutationOutcome.Invalid);
            }
            var precisionMeters = Math.Max(
                normalized.PublicPrecisionMeters!.Value,
                MinimumApproximatePrecisionMeters);
            var latitudeStep = precisionMeters / 111_320d;
            var longitudeScale = Math.Max(
                Math.Cos(source.LatitudeDegrees * Math.PI / 180d),
                0.01d);
            var longitudeStep = precisionMeters / (111_320d * longitudeScale);
            normalized = normalized with
            {
                PublicLatitudeDegrees = Math.Clamp(
                    Math.Round(source.LatitudeDegrees / latitudeStep, MidpointRounding.AwayFromZero) * latitudeStep,
                    -90d,
                    90d),
                PublicLongitudeDegrees = Math.Clamp(
                    Math.Round(source.LongitudeDegrees / longitudeStep, MidpointRounding.AwayFromZero) * longitudeStep,
                    -180d,
                    180d),
                PublicPrecisionMeters = precisionMeters
            };
        }
        var canonicalSha256 = Hash(new
        {
            normalized.DisclosureLevel,
            normalized.RegionCode,
            normalized.RegionLabel,
            normalized.PublicLatitudeDegrees,
            normalized.PublicLongitudeDegrees,
            normalized.PublicPrecisionMeters,
            SourceObservatoryLocationVersionId = source?.Id,
            normalized.ReasonCode
        });
        var current = await CurrentDisclosures(isRelational, observatoryId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (current is not null && string.Equals(current.CanonicalSha256, canonicalSha256, StringComparison.Ordinal))
        {
            return new(ObservatoryPublicationMutationOutcome.Unchanged, current.Version);
        }

        var now = timeProvider.GetUtcNow();
        if (current is not null)
        {
            current.SupersededAtUtc = now;
        }
        var version = (current?.Version ?? 0) + 1;
        dbContext.ObservatoryLocationDisclosureVersions.Add(new ObservatoryLocationDisclosureVersion
        {
            ObservatoryId = observatoryId,
            Version = version,
            DisclosureLevel = normalized.DisclosureLevel,
            RegionCode = normalized.RegionCode,
            RegionLabel = normalized.RegionLabel,
            PublicLatitudeDegrees = normalized.PublicLatitudeDegrees,
            PublicLongitudeDegrees = normalized.PublicLongitudeDegrees,
            PublicPrecisionMeters = normalized.PublicPrecisionMeters,
            SourceObservatoryLocationVersionId = source?.Id,
            EffectiveFromUtc = now,
            ActorUserId = actorUserId,
            ReasonCode = normalized.ReasonCode,
            CanonicalSha256 = canonicalSha256
        });
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (logger is not null)
        {
            OperatorUiAuditLog.Location(logger, "applied", normalized.DisclosureLevel.ToString());
        }
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        return new(ObservatoryPublicationMutationOutcome.Applied, version);
    }

    private async Task<bool> LockOwnedObservatoryAsync(
        Guid observatoryId,
        string actorUserId,
        CancellationToken cancellationToken)
    {
        if (dbContext.Database.IsRelational())
        {
            var acquired = await dbContext.Observatories.FromSqlInterpolated($"""
                SELECT * FROM [Observatories] WITH (UPDLOCK, HOLDLOCK)
                WHERE [Id] = {observatoryId} AND [IsActive] = CAST(1 AS bit)
                """).AnyAsync(cancellationToken).ConfigureAwait(false);
            if (!acquired)
            {
                return false;
            }
        }
        else if (!await dbContext.Observatories.AnyAsync(item =>
                     item.Id == observatoryId && item.IsActive, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        return await ObservatoryMembershipAccess.ForOwner(dbContext, actorUserId)
            .AnyAsync(item => item.ObservatoryId == observatoryId, cancellationToken).ConfigureAwait(false);
    }

    private IQueryable<ObservatoryPublicationProfileVersion> CurrentProfiles(bool isRelational, Guid observatoryId)
        => isRelational
            ? dbContext.ObservatoryPublicationProfileVersions.FromSqlInterpolated($"""
                SELECT * FROM [ObservatoryPublicationProfileVersions] WITH (UPDLOCK, HOLDLOCK)
                WHERE [ObservatoryId] = {observatoryId} AND [SupersededAtUtc] IS NULL
                """)
            : dbContext.ObservatoryPublicationProfileVersions.Where(item =>
                item.ObservatoryId == observatoryId && item.SupersededAtUtc == null);

    private IQueryable<ObservatoryLocationDisclosureVersion> CurrentDisclosures(bool isRelational, Guid observatoryId)
        => isRelational
            ? dbContext.ObservatoryLocationDisclosureVersions.FromSqlInterpolated($"""
                SELECT * FROM [ObservatoryLocationDisclosureVersions] WITH (UPDLOCK, HOLDLOCK)
                WHERE [ObservatoryId] = {observatoryId} AND [SupersededAtUtc] IS NULL
                """)
            : dbContext.ObservatoryLocationDisclosureVersions.Where(item =>
                item.ObservatoryId == observatoryId && item.SupersededAtUtc == null);

    private static ObservatoryPublicationProfileRequest? Normalize(ObservatoryPublicationProfileRequest request)
    {
        var slug = request.PublicSlug.Trim().ToLowerInvariant();
        var name = request.PublicDisplayName.Trim();
        var description = request.PublicDescription.Trim();
        var reason = request.ReasonCode.Trim();
        return !Enum.IsDefined(request.ProfileVisibility)
            || slug.Length is < 1 or > 128
            || !SlugPattern().IsMatch(slug)
            || name.Length is < 1 or > 200
            || description.Length > 2000
            || reason.Length is < 1 or > 128
                ? null
                : request with
                {
                    PublicSlug = slug,
                    PublicDisplayName = name,
                    PublicDescription = description,
                    ReasonCode = reason
                };
    }

    private static ObservatoryLocationDisclosureRequest? Normalize(ObservatoryLocationDisclosureRequest request)
    {
        var regionCode = string.IsNullOrWhiteSpace(request.RegionCode) ? null : request.RegionCode.Trim();
        var regionLabel = string.IsNullOrWhiteSpace(request.RegionLabel) ? null : request.RegionLabel.Trim();
        var reason = request.ReasonCode.Trim();
        var valid = Enum.IsDefined(request.DisclosureLevel)
            && reason.Length is >= 1 and <= 128
            && (regionCode is null || regionCode.Length <= 64)
            && (regionLabel is null || regionLabel.Length <= 200)
            && request.DisclosureLevel switch
            {
                ObservatoryLocationDisclosureLevel.Hidden => regionCode is null && regionLabel is null
                    && request.PublicLatitudeDegrees is null && request.PublicLongitudeDegrees is null
                    && request.PublicPrecisionMeters is null,
                ObservatoryLocationDisclosureLevel.Region => regionCode is not null && regionLabel is not null
                    && request.PublicLatitudeDegrees is null && request.PublicLongitudeDegrees is null
                    && request.PublicPrecisionMeters is null,
                ObservatoryLocationDisclosureLevel.Approximate => request.PublicPrecisionMeters is double precisionMeters
                    && double.IsFinite(precisionMeters)
                    && precisionMeters > 0,
                ObservatoryLocationDisclosureLevel.Exact => false,
                _ => false
            };
        return valid
            ? request with { RegionCode = regionCode, RegionLabel = regionLabel, ReasonCode = reason }
            : null;
    }

    private static string Hash<T>(T value)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    private static bool IsPublicationConflict(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is Microsoft.Data.SqlClient.SqlException { Number: 1205 or 2601 or 2627 }) return true;
        }
        return false;
    }
}
