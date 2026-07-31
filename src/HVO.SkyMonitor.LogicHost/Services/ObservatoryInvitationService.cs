using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal enum ObservatoryInvitationMutationOutcome
{
    Applied,
    NotFoundOrDenied,
    Conflict,
    Expired,
    Invalid
}

internal sealed record ObservatoryInvitationIssueResult(
    ObservatoryInvitationMutationOutcome Outcome,
    Guid? InvitationId = null,
    string? AcceptanceToken = null);

internal sealed record ObservatoryInvitationSummary(
    Guid InvitationId,
    string TargetUserId,
    string? TargetEmail,
    ObservatoryMembershipRole OfferedRole,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);

internal sealed record ObservatoryInvitationPage(
    IReadOnlyList<ObservatoryInvitationSummary> Items,
    string? NextCursor);

internal interface IObservatoryInvitationService
{
    Task<ObservatoryInvitationIssueResult> IssueAsync(
        Guid observatoryId,
        string actorUserId,
        string targetUserId,
        ObservatoryMembershipRole offeredRole,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default);

    Task<ObservatoryInvitationMutationOutcome> AcceptAsync(
        Guid invitationId,
        string actorUserId,
        string acceptanceToken,
        CancellationToken cancellationToken = default);

    Task<ObservatoryInvitationMutationOutcome> DeclineAsync(
        Guid invitationId,
        string actorUserId,
        CancellationToken cancellationToken = default);

    Task<ObservatoryInvitationMutationOutcome> RevokeAsync(
        Guid invitationId,
        string actorUserId,
        CancellationToken cancellationToken = default);

    Task<ObservatoryInvitationPage> ListPendingAsync(
        Guid observatoryId,
        string actorUserId,
        int take,
        string? cursor,
        CancellationToken cancellationToken = default);
}

internal sealed class ObservatoryInvitationService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<ObservatoryInvitationService>? logger = null) : IObservatoryInvitationService
{
    public async Task<ObservatoryInvitationPage> ListPendingAsync(
        Guid observatoryId,
        string actorUserId,
        int take,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        if (take is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(take));
        if (!TryDecode(cursor, out InvitationCursor? cursorValue))
        {
            throw new ArgumentException("The invitation cursor is invalid.", nameof(cursor));
        }
        if (!await ObservatoryMembershipAccess.ForOwner(dbContext, actorUserId)
            .AnyAsync(item => item.ObservatoryId == observatoryId, cancellationToken).ConfigureAwait(false))
        {
            return new([], null);
        }
        var now = timeProvider.GetUtcNow();
        var query = dbContext.ObservatoryInvitations.AsNoTracking()
            .Where(item => item.ObservatoryId == observatoryId
                && item.ExpiresAtUtc > now
                && !item.Dispositions.Any());
        if (cursorValue is not null)
        {
            query = query.Where(item => item.ExpiresAtUtc > cursorValue.ExpiresAtUtc
                || item.ExpiresAtUtc == cursorValue.ExpiresAtUtc
                && item.Id.CompareTo(cursorValue.InvitationId) > 0);
        }
        var rows = await query
            .Join(dbContext.Users.AsNoTracking(), item => item.TargetUserId, user => user.Id,
                (item, user) => new { Invitation = item, user.Email })
            .OrderBy(item => item.Invitation.ExpiresAtUtc)
            .ThenBy(item => item.Invitation.Id)
            .Select(item => new ObservatoryInvitationSummary(
                item.Invitation.Id,
                item.Invitation.TargetUserId,
                item.Email,
                item.Invitation.OfferedRole,
                item.Invitation.IssuedAtUtc,
                item.Invitation.ExpiresAtUtc))
            .Take(take + 1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasMore = rows.Count > take;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new(rows, hasMore && rows.Count > 0
            ? WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(
                new InvitationCursor(rows[^1].ExpiresAtUtc, rows[^1].InvitationId)))
            : null);
    }

    private static bool TryDecode<T>(string? cursor, out T? value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(cursor)) return true;
        try
        {
            value = JsonSerializer.Deserialize<T>(WebEncoders.Base64UrlDecode(cursor));
            return value is not null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return false;
        }
    }

    private sealed record InvitationCursor(DateTimeOffset ExpiresAtUtc, Guid InvitationId);

    public async Task<ObservatoryInvitationIssueResult> IssueAsync(
        Guid observatoryId,
        string actorUserId,
        string targetUserId,
        ObservatoryMembershipRole offeredRole,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetUserId);
        if (!Enum.IsDefined(offeredRole) || lifetime < TimeSpan.FromMinutes(5) || lifetime > TimeSpan.FromDays(30))
        {
            return new(ObservatoryInvitationMutationOutcome.Invalid);
        }
        var isRelational = dbContext.Database.IsRelational();
        await using var transaction = isRelational
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false)
            : null;
        if (!await ObservatoryMembershipAccess.ForOwner(dbContext, actorUserId)
            .AnyAsync(item => item.ObservatoryId == observatoryId, cancellationToken).ConfigureAwait(false))
        {
            return new(ObservatoryInvitationMutationOutcome.NotFoundOrDenied);
        }
        var target = await (isRelational
                ? dbContext.Users.FromSqlInterpolated($"""
                    SELECT * FROM [AspNetUsers] WITH (UPDLOCK, HOLDLOCK)
                    WHERE [Id] = {targetUserId}
                    """)
                : dbContext.Users.Where(item => item.Id == targetUserId))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (target is null || target.AccountType != AccountType.User || string.IsNullOrWhiteSpace(target.Email))
        {
            return new(ObservatoryInvitationMutationOutcome.NotFoundOrDenied);
        }
        if (await dbContext.ObservatoryMemberships.AnyAsync(item =>
            item.ObservatoryId == observatoryId && item.UserId == targetUserId, cancellationToken).ConfigureAwait(false))
        {
            return new(ObservatoryInvitationMutationOutcome.Conflict);
        }
        var now = timeProvider.GetUtcNow();
        var priorInvitations = await LockInvitationsAsync(
            isRelational, observatoryId, targetUserId, cancellationToken).ConfigureAwait(false);
        var pending = priorInvitations.SingleOrDefault(item => item.Dispositions.Count == 0 && item.ExpiresAtUtc > now);
        if (pending is not null)
        {
            return new(ObservatoryInvitationMutationOutcome.Conflict, pending.Id);
        }
        foreach (var expired in priorInvitations.Where(item =>
                     item.Dispositions.Count == 0 && item.ExpiresAtUtc <= now))
        {
            dbContext.ObservatoryInvitationDispositions.Add(new ObservatoryInvitationDisposition
            {
                InvitationId = expired.Id,
                Action = ObservatoryInvitationDispositionAction.Expired,
                ActorUserId = actorUserId,
                OccurredAtUtc = now,
                ReasonCode = "invitation-expired"
            });
        }

        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var tokenSha256 = HashText(token);
        var emailSha256 = HashText(target.Email.Trim().ToUpperInvariant());
        var invitation = new ObservatoryInvitation
        {
            ObservatoryId = observatoryId,
            TargetUserId = targetUserId,
            TargetEmailSha256 = emailSha256,
            OfferedRole = offeredRole,
            InvitedByUserId = actorUserId,
            IssuedAtUtc = now,
            ExpiresAtUtc = now + lifetime,
            AcceptanceTokenSha256 = tokenSha256,
            CanonicalSha256 = Hash(new
            {
                observatoryId,
                targetUserId,
                TargetEmailSha256 = emailSha256,
                offeredRole,
                actorUserId,
                IssuedAtUtc = now,
                ExpiresAtUtc = now + lifetime,
                AcceptanceTokenSha256 = tokenSha256
            })
        };
        dbContext.ObservatoryInvitations.Add(invitation);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (logger is not null) OperatorUiAuditLog.Invitation(logger, "issue", "applied", offeredRole.ToString());
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        return new(ObservatoryInvitationMutationOutcome.Applied, invitation.Id, token);
    }

    public async Task<ObservatoryInvitationMutationOutcome> AcceptAsync(
        Guid invitationId,
        string actorUserId,
        string acceptanceToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(acceptanceToken);
        return await CompleteAsync(
            invitationId,
            actorUserId,
            ObservatoryInvitationDispositionAction.Accepted,
            acceptanceToken,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<ObservatoryInvitationMutationOutcome> DeclineAsync(
        Guid invitationId,
        string actorUserId,
        CancellationToken cancellationToken = default)
        => CompleteAsync(
            invitationId,
            actorUserId,
            ObservatoryInvitationDispositionAction.Declined,
            null,
            cancellationToken);

    public Task<ObservatoryInvitationMutationOutcome> RevokeAsync(
        Guid invitationId,
        string actorUserId,
        CancellationToken cancellationToken = default)
        => CompleteAsync(
            invitationId,
            actorUserId,
            ObservatoryInvitationDispositionAction.Revoked,
            null,
            cancellationToken);

    private async Task<ObservatoryInvitationMutationOutcome> CompleteAsync(
        Guid invitationId,
        string actorUserId,
        ObservatoryInvitationDispositionAction action,
        string? acceptanceToken,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        var isRelational = dbContext.Database.IsRelational();
        await using var transaction = isRelational
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false)
            : null;
        var invitation = await (isRelational
                ? dbContext.ObservatoryInvitations.FromSqlInterpolated($"""
                    SELECT * FROM [ObservatoryInvitations] WITH (UPDLOCK, HOLDLOCK)
                    WHERE [Id] = {invitationId}
                    """)
                : dbContext.ObservatoryInvitations.Where(item => item.Id == invitationId))
            .Include(item => item.Dispositions)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (invitation is null || invitation.Dispositions.Count != 0)
        {
            return ObservatoryInvitationMutationOutcome.NotFoundOrDenied;
        }
        var isTargetAction = action is ObservatoryInvitationDispositionAction.Accepted
            or ObservatoryInvitationDispositionAction.Declined;
        if (isTargetAction && !string.Equals(invitation.TargetUserId, actorUserId, StringComparison.Ordinal))
        {
            return ObservatoryInvitationMutationOutcome.NotFoundOrDenied;
        }
        if (action == ObservatoryInvitationDispositionAction.Revoked
            && !await ObservatoryMembershipAccess.ForOwner(dbContext, actorUserId)
                .AnyAsync(item => item.ObservatoryId == invitation.ObservatoryId, cancellationToken)
                .ConfigureAwait(false))
        {
            return ObservatoryInvitationMutationOutcome.NotFoundOrDenied;
        }
        var now = timeProvider.GetUtcNow();
        if (invitation.ExpiresAtUtc <= now)
        {
            dbContext.ObservatoryInvitationDispositions.Add(new ObservatoryInvitationDisposition
            {
                InvitationId = invitation.Id,
                Action = ObservatoryInvitationDispositionAction.Expired,
                ActorUserId = actorUserId,
                OccurredAtUtc = now,
                ReasonCode = "invitation-expired"
            });
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            return ObservatoryInvitationMutationOutcome.Expired;
        }
        if (action == ObservatoryInvitationDispositionAction.Accepted)
        {
            if (!TokenMatches(invitation.AcceptanceTokenSha256, acceptanceToken!))
            {
                return ObservatoryInvitationMutationOutcome.NotFoundOrDenied;
            }
            var targetIsHuman = await dbContext.Users.AnyAsync(item =>
                item.Id == actorUserId && item.AccountType == AccountType.User, cancellationToken).ConfigureAwait(false);
            if (!targetIsHuman || await dbContext.ObservatoryMemberships.AnyAsync(item =>
                item.ObservatoryId == invitation.ObservatoryId && item.UserId == actorUserId, cancellationToken)
                .ConfigureAwait(false))
            {
                return ObservatoryInvitationMutationOutcome.Conflict;
            }
            dbContext.ObservatoryMemberships.Add(new ObservatoryMembership
            {
                ObservatoryId = invitation.ObservatoryId,
                UserId = actorUserId,
                Role = invitation.OfferedRole,
                AddedAtUtc = now
            });
            dbContext.ObservatoryMembershipAudits.Add(new ObservatoryMembershipAudit
            {
                ObservatoryId = invitation.ObservatoryId,
                TargetUserId = actorUserId,
                ActorUserId = actorUserId,
                Action = ObservatoryMembershipAuditAction.Granted,
                NewRole = invitation.OfferedRole,
                ReasonCode = "invitation-accepted",
                OccurredAtUtc = now
            });
        }
        dbContext.ObservatoryInvitationDispositions.Add(new ObservatoryInvitationDisposition
        {
            InvitationId = invitation.Id,
            Action = action,
            ActorUserId = actorUserId,
            OccurredAtUtc = now,
            ReasonCode = action switch
            {
                ObservatoryInvitationDispositionAction.Accepted => "invitation-accepted",
                ObservatoryInvitationDispositionAction.Declined => "invitation-declined",
                _ => "invitation-revoked"
            }
        });
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (logger is not null) OperatorUiAuditLog.Invitation(logger, action.ToString(), "applied", invitation.OfferedRole.ToString());
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        return ObservatoryInvitationMutationOutcome.Applied;
    }

    private async Task<IReadOnlyList<ObservatoryInvitation>> LockInvitationsAsync(
        bool isRelational,
        Guid observatoryId,
        string targetUserId,
        CancellationToken cancellationToken)
        => await (isRelational
                ? dbContext.ObservatoryInvitations.FromSqlInterpolated($"""
                    SELECT * FROM [ObservatoryInvitations] WITH (UPDLOCK, HOLDLOCK)
                    WHERE [ObservatoryId] = {observatoryId} AND [TargetUserId] = {targetUserId}
                    """)
                : dbContext.ObservatoryInvitations.Where(item =>
                    item.ObservatoryId == observatoryId && item.TargetUserId == targetUserId))
            .Include(item => item.Dispositions)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    private static bool TokenMatches(string expectedSha256, string token)
        => CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(expectedSha256),
            Convert.FromHexString(HashText(token)));

    private static string HashText(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Hash<T>(T value)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
