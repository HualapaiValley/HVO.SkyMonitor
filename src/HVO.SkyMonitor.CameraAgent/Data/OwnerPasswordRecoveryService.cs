using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Data;

internal enum OwnerPasswordRecoveryOutcome
{
    Completed,
    AlreadyCompleted,
    AlreadyCompletedPasswordReplaced,
    InvalidRequest,
    Conflict
}

internal sealed record OwnerPasswordRecoveryChallengeResult(
    OwnerPasswordRecoveryOutcome Outcome,
    string? Challenge = null);

internal sealed class OwnerPasswordRecoveryService(
    ApplicationDbContext dbContext,
    UserManager<ApplicationUser> userManager,
    IDataProtectionProvider dataProtectionProvider,
    IOptions<LocalIdentityOptions> identityOptions,
    ILogger<OwnerPasswordRecoveryService> logger)
{
    private const string LoginProvider = "HVO.SkyMonitor.OwnerRecovery";
    private const string OperationTokenName = "LastCompletedOperation";
    private static readonly EventId RecoveryRequestedEvent = new(4184, "OwnerPasswordRecoveryRequested");
    private static readonly EventId RecoveryCompletedEvent = new(4185, "OwnerPasswordRecoveryCompleted");
    private static readonly EventId RecoveryRejectedEvent = new(4186, "OwnerPasswordRecoveryRejected");
    private readonly ITimeLimitedDataProtector _protector = dataProtectionProvider
        .CreateProtector("HVO.SkyMonitor.CameraAgent.OwnerPasswordRecovery.v1")
        .ToTimeLimitedDataProtector();
    private readonly LocalIdentityOptions _identityOptions = identityOptions.Value;

    internal async Task<OwnerPasswordRecoveryChallengeResult> CreateChallengeAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty || !PasswordBootstrapAuthorityWasRemoved())
        {
            return Reject(operationId, "invalid-state", OwnerPasswordRecoveryOutcome.InvalidRequest);
        }

        var owner = await FindConfiguredOwnerAsync(cancellationToken).ConfigureAwait(false);
        if (owner is null)
        {
            return Reject(operationId, "owner-mismatch", OwnerPasswordRecoveryOutcome.Conflict);
        }

        var securityStamp = await userManager.GetSecurityStampAsync(owner).ConfigureAwait(false);
        if (string.IsNullOrEmpty(securityStamp))
        {
            return Reject(operationId, "missing-security-stamp", OwnerPasswordRecoveryOutcome.Conflict);
        }

        var payload = SerializeChallenge(new RecoveryChallengePayload(
            SchemaVersion: 1,
            OperationId: operationId,
            OwnerId: owner.Id,
            SecurityStamp: securityStamp));
        var challenge = Convert.ToBase64String(_protector.Protect(payload, TimeSpan.FromMinutes(5)));

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                RecoveryRequestedEvent,
                "Accepted a local owner password recovery challenge for operation {OperationId}",
                operationId);
        }

        return new OwnerPasswordRecoveryChallengeResult(OwnerPasswordRecoveryOutcome.Completed, challenge);
    }

    internal async Task<OwnerPasswordRecoveryOutcome> CompleteAsync(
        Guid operationId,
        string challenge,
        string? temporaryPassword,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(challenge) ||
            temporaryPassword is null ||
            temporaryPassword.Length is < 16 or > 256 ||
            temporaryPassword.Any(static character => character is < '!' or > '~') ||
            !PasswordBootstrapAuthorityWasRemoved())
        {
            return Reject(operationId, "invalid-request", OwnerPasswordRecoveryOutcome.InvalidRequest).Outcome;
        }

        var owner = await FindConfiguredOwnerAsync(cancellationToken).ConfigureAwait(false);
        if (owner is null)
        {
            return Reject(operationId, "owner-mismatch", OwnerPasswordRecoveryOutcome.Conflict).Outcome;
        }

        var completedOperation = await userManager.GetAuthenticationTokenAsync(
            owner,
            LoginProvider,
            OperationTokenName).ConfigureAwait(false);
        if (string.Equals(completedOperation, operationId.ToString("D"), StringComparison.Ordinal))
        {
            return owner.PasswordChangeRequired
                ? OwnerPasswordRecoveryOutcome.AlreadyCompleted
                : OwnerPasswordRecoveryOutcome.AlreadyCompletedPasswordReplaced;
        }

        RecoveryChallengePayload? payload;
        try
        {
            var unprotected = _protector.Unprotect(Convert.FromBase64String(challenge), out _);
            payload = DeserializeChallenge(unprotected);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or IOException or ArgumentException)
        {
            return Reject(operationId, "invalid-challenge", OwnerPasswordRecoveryOutcome.InvalidRequest).Outcome;
        }

        var securityStamp = await userManager.GetSecurityStampAsync(owner).ConfigureAwait(false);
        if (payload is not
            {
                SchemaVersion: 1
            } ||
            payload.OperationId != operationId ||
            !string.Equals(payload.OwnerId, owner.Id, StringComparison.Ordinal) ||
            !string.Equals(payload.SecurityStamp, securityStamp, StringComparison.Ordinal))
        {
            return Reject(operationId, "stale-challenge", OwnerPasswordRecoveryOutcome.Conflict).Outcome;
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var resetToken = await userManager.GeneratePasswordResetTokenAsync(owner).ConfigureAwait(false);
            var reset = await userManager.ResetPasswordAsync(owner, resetToken, temporaryPassword).ConfigureAwait(false);
            if (!reset.Succeeded)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return Reject(operationId, "password-policy", OwnerPasswordRecoveryOutcome.InvalidRequest).Outcome;
            }

            owner.PasswordChangeRequired = true;
            var update = await userManager.UpdateAsync(owner).ConfigureAwait(false);
            if (!update.Succeeded)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return Reject(operationId, "owner-update", OwnerPasswordRecoveryOutcome.Conflict).Outcome;
            }

            var receipt = await userManager.SetAuthenticationTokenAsync(
                owner,
                LoginProvider,
                OperationTokenName,
                operationId.ToString("D")).ConfigureAwait(false);
            if (!receipt.Succeeded)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return Reject(operationId, "receipt-update", OwnerPasswordRecoveryOutcome.Conflict).Outcome;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return Reject(operationId, "concurrent-recovery", OwnerPasswordRecoveryOutcome.Conflict).Outcome;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                RecoveryCompletedEvent,
                "Completed local owner password recovery operation {OperationId}",
                operationId);
        }

        return OwnerPasswordRecoveryOutcome.Completed;
    }

    private bool PasswordBootstrapAuthorityWasRemoved()
        => _identityOptions.AllowMissingAdminPassword && string.IsNullOrEmpty(_identityOptions.AdminPassword);

    private async Task<ApplicationUser?> FindConfiguredOwnerAsync(CancellationToken cancellationToken)
    {
        var owners = await userManager.Users
            .Where(static candidate => candidate.IsSiteOwner)
            .Take(2)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (owners.Count != 1)
        {
            return null;
        }

        var configuredEmail = userManager.NormalizeEmail(_identityOptions.AdminEmail);
        return string.Equals(owners[0].NormalizedEmail, configuredEmail, StringComparison.Ordinal)
            ? owners[0]
            : null;
    }

    private OwnerPasswordRecoveryChallengeResult Reject(
        Guid operationId,
        string reason,
        OwnerPasswordRecoveryOutcome outcome)
    {
        if (logger.IsEnabled(LogLevel.Warning))
        {
            logger.LogWarning(
                RecoveryRejectedEvent,
                "Rejected local owner password recovery operation {OperationId}; {Reason}",
                operationId,
                reason);
        }

        return new OwnerPasswordRecoveryChallengeResult(outcome);
    }

    private static byte[] SerializeChallenge(RecoveryChallengePayload payload)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(payload.SchemaVersion);
            writer.Write(payload.OperationId.ToByteArray());
            writer.Write(payload.OwnerId);
            writer.Write(payload.SecurityStamp);
        }
        return stream.ToArray();
    }

    private static RecoveryChallengePayload? DeserializeChallenge(byte[] value)
    {
        using var stream = new MemoryStream(value, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        var schemaVersion = reader.ReadInt32();
        var operationId = reader.ReadBytes(16);
        if (operationId.Length != 16)
        {
            return null;
        }
        var payload = new RecoveryChallengePayload(
            schemaVersion,
            new Guid(operationId),
            reader.ReadString(),
            reader.ReadString());
        return stream.Position == stream.Length ? payload : null;
    }

    private sealed record RecoveryChallengePayload(
        int SchemaVersion,
        Guid OperationId,
        string OwnerId,
        string SecurityStamp);
}
