using System.Security.Claims;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class ProcessingGraphProposalReadServiceTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task CurrentMemberReadsRetainedInstallationWithoutTrackingOrWrites(int role)
    {
        await using var db = CreateContext();
        var proposal = await SeedAsync(db, (ObservatoryMembershipRole)role).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        var service = new ProcessingGraphProposalReadService(db);
        var result = await ReadAsync(service, proposal).ConfigureAwait(false);
        Assert.IsNotNull(result);
        Assert.AreEqual(proposal.Id, result.Proposal.Id);
        Assert.AreEqual(proposal.LogicalCameraInstallationId, result.Proposal.LogicalCameraInstallationId);
        Assert.AreNotEqual(result.Proposal.InstallationPublicId, result.Proposal.LogicalCameraInstallationId);
        CollectionAssert.AreEqual(new[] { "RolledBack", "Activated", "Accepted" }, result.Facts.Select(fact => fact.Kind).ToArray());
        Assert.AreEqual(proposal.IssuedAtUtc.AddMinutes(1), result.Facts[0].OccurredAtUtc);
        Assert.AreEqual(proposal.IssuedAtUtc.AddMinutes(4), result.Facts[0].RecordedAtUtc);
        Assert.IsFalse(result.FactsAreTruncated);
        Assert.IsFalse(db.ChangeTracker.Entries().Any());
        Assert.AreEqual(1, await db.CentralProcessingGraphDeliveryProposals.CountAsync().ConfigureAwait(false));
        Assert.AreEqual(3, await db.CentralProcessingGraphDeliveryFacts.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task PrivateRelationshipAndMembershipAreRequiredDespiteGlobalCredentialAuthority()
    {
        await using var db = CreateContext();
        var proposal = await SeedAsync(db, ObservatoryMembershipRole.Owner).ConfigureAwait(false);
        var service = new ProcessingGraphProposalReadService(db);
        var installation = proposal.LogicalCameraInstallation!;
        var camera = installation.LogicalCamera!;
        foreach (var ids in new[]
        {
            (Guid.NewGuid(), camera.Id, installation.Id, proposal.Id),
            (camera.ObservatoryId, Guid.NewGuid(), installation.Id, proposal.Id),
            (camera.ObservatoryId, camera.Id, installation.InstallationPublicId, proposal.Id),
            (camera.ObservatoryId, camera.Id, installation.Id, Guid.NewGuid())
        })
        {
            Assert.IsNull(await service.GetAsync(Principal("member"), ids.Item1, ids.Item2, ids.Item3, ids.Item4).ConfigureAwait(false));
        }
        Assert.IsNull(await ReadAsync(service, proposal, new ClaimsPrincipal()).ConfigureAwait(false));
        Assert.IsNull(await ReadAsync(service, proposal, Principal("nonmember")).ConfigureAwait(false));
        var membership = await db.ObservatoryMemberships.SingleAsync().ConfigureAwait(false);
        db.Remove(membership);
        await db.SaveChangesAsync().ConfigureAwait(false);
        Assert.IsNull(await ReadAsync(service, proposal).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ScopedMalformedAndSystemCredentialsCannotReadPrivateFacts()
    {
        await using var db = CreateContext();
        var proposal = await SeedAsync(db, ObservatoryMembershipRole.Owner).ConfigureAwait(false);
        var service = new ProcessingGraphProposalReadService(db);
        var key = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "member"), new Claim("account_type", "User"),
            new Claim(ApiKeyClaims.AuthenticationType, ApiKeyAuthenticationOptions.AuthenticationScheme),
            new Claim(ApiKeyClaims.AccessLevel, nameof(ApiKeyAccessLevel.Read)),
            new Claim(ApiKeyClaims.ObservatoryId, Guid.NewGuid().ToString("D"))], ApiKeyAuthenticationOptions.AuthenticationScheme);
        Assert.IsNull(await ReadAsync(service, proposal, new ClaimsPrincipal(key)).ConfigureAwait(false));
        key.RemoveClaim(key.FindFirst(ApiKeyClaims.ObservatoryId)!);
        Assert.IsNotNull(await ReadAsync(service, proposal, new ClaimsPrincipal(key)).ConfigureAwait(false));
        key.AddClaim(new Claim(ApiKeyClaims.ObservatoryId, "malformed"));
        Assert.IsNull(await ReadAsync(service, proposal, new ClaimsPrincipal(key)).ConfigureAwait(false));
        var system = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("sub", "member"), new Claim("account_type", "System"), new Claim("scope", "api.admin")],
            CanonicalCredentialClaims.BearerAuthenticationType));
        Assert.IsNull(await ReadAsync(service, proposal, system).ConfigureAwait(false));
        Assert.IsNull(await ReadAsync(service, proposal, new ClaimsPrincipal([Principal("member").Identities.Single(), Principal("other").Identities.Single()])).ConfigureAwait(false));
        // Independent immutable installation identities must agree with the proposal, not just its FK.
        proposal.InstallationPublicId = Guid.NewGuid();
        await db.SaveChangesAsync().ConfigureAwait(false);
        Assert.IsNull(await ReadAsync(service, proposal).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task FactsAreBoundedAndEqualTimestampOrderUsesStableIdentity()
    {
        await using var db = CreateContext();
        var proposal = await SeedAsync(db, ObservatoryMembershipRole.Viewer).ConfigureAwait(false);
        for (var index = 0; index < 70; index++)
        {
            db.Add(new CentralProcessingGraphDeliveryFact
            {
                ProposalId = proposal.Id,
                Kind = "Retrieved",
                Source = "CameraAgent",
                OccurredAtUtc = proposal.IssuedAtUtc,
                RecordedAtUtc = proposal.ExpiresAtUtc
            });
        }
        await db.SaveChangesAsync().ConfigureAwait(false);
        var expected = await db.CentralProcessingGraphDeliveryFacts.Where(fact => fact.ProposalId == proposal.Id)
            .OrderByDescending(fact => fact.RecordedAtUtc).ThenByDescending(fact => fact.Id)
            .Take(50).Select(fact => fact.Id).ToArrayAsync().ConfigureAwait(false);
        var service = new ProcessingGraphProposalReadService(db);
        var result = await ReadAsync(service, proposal).ConfigureAwait(false);
        Assert.IsNotNull(result);
        Assert.HasCount(50, result.Facts);
        Assert.IsTrue(result.FactsAreTruncated);
        CollectionAssert.AreEqual(expected, result.Facts.Select(fact => fact.Id).ToArray());
        var small = await service.GetAsync(Principal("member"), proposal.LogicalCameraInstallation!.LogicalCamera!.ObservatoryId,
            proposal.LogicalCameraInstallation.LogicalCameraId, proposal.LogicalCameraInstallationId, proposal.Id, 1).ConfigureAwait(false);
        Assert.HasCount(1, small!.Facts);
        Assert.IsTrue(small.FactsAreTruncated);
    }

    [TestMethod]
    public async Task InvalidBoundsAndCancellationFailBeforeQuerying()
    {
        await using var db = CreateContext();
        var service = new ProcessingGraphProposalReadService(db);
        foreach (var take in new[] { 0, -1, 51, int.MaxValue })
        {
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => service.GetAsync(
                Principal("member"), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), take)).ConfigureAwait(false);
        }
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.GetAsync(
            Principal("member"), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), cancellationToken: cancelled.Token)).ConfigureAwait(false);
    }

    private static Task<ProcessingGraphProposalReadResult?> ReadAsync(
        ProcessingGraphProposalReadService service, CentralProcessingGraphDeliveryProposal proposal, ClaimsPrincipal? principal = null)
        => service.GetAsync(principal ?? Principal("member"), proposal.LogicalCameraInstallation!.LogicalCamera!.ObservatoryId,
            proposal.LogicalCameraInstallation.LogicalCameraId, proposal.LogicalCameraInstallationId, proposal.Id);

    private static ClaimsPrincipal Principal(string actor)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor), new Claim("account_type", "User")],
            IdentityConstants.ApplicationScheme));

    private static ApplicationDbContext CreateContext()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<CentralProcessingGraphDeliveryProposal> SeedAsync(ApplicationDbContext db, ObservatoryMembershipRole role)
    {
        var now = new DateTimeOffset(2026, 10, 15, 1, 0, 0, TimeSpan.Zero);
        var user = new ApplicationUser { Id = "member", AccountType = AccountType.User };
        var observatory = new Observatory { OwnerUserId = user.Id, Name = "Retained", TimeZoneId = "UTC" };
        var camera = new LogicalCamera { Observatory = observatory, ObservatoryId = observatory.Id, Name = "Camera", Slug = "camera", DeactivatedAtUtc = now };
        var installation = new LogicalCameraInstallation
        {
            LogicalCamera = camera,
            LogicalCameraId = camera.Id,
            InstallationPublicId = Guid.NewGuid(),
            RetiredAtUtc = now
        };
        var proposal = new CentralProcessingGraphDeliveryProposal
        {
            LogicalCameraInstallation = installation,
            LogicalCameraInstallationId = installation.Id,
            InstallationPublicId = installation.InstallationPublicId,
            IssuedAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
            CapabilitySnapshotSha256 = new string('a', 64)
        };
        db.AddRange(user, observatory, camera, installation, proposal,
            new ObservatoryMembership { User = user, UserId = user.Id, Observatory = observatory, ObservatoryId = observatory.Id, Role = role });
        foreach (var (kind, index) in new[] { ("Accepted", 0), ("Activated", 1), ("RolledBack", 2) })
        {
            db.Add(new CentralProcessingGraphDeliveryFact
            {
                ProposalId = proposal.Id,
                Proposal = proposal,
                Kind = kind,
                Source = "CameraAgent",
                OccurredAtUtc = now.AddMinutes(1),
                RecordedAtUtc = now.AddMinutes(2 + index),
                ReasonCode = "stored-reason"
            });
        }
        await db.SaveChangesAsync().ConfigureAwait(false);
        return proposal;
    }
}
