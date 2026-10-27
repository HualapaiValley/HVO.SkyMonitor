using System.Data.SqlTypes;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class LogicalCameraArchiveIntegrationTests
{
    private static readonly int[] ExpiredIndexes = [10, 20];

    [TestMethod]
    public async Task SqlServer_RevalidationKeepsExactOlderPreviewAndRechecksMembership()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ApplicationDbContext>();
        var data = await SeedAsync(services, 12).ConfigureAwait(false);
        var service = services.GetRequiredService<ILogicalCameraArchiveReadService>();
        var principal = Principal(data.Owner);
        var page = await service.SelectCurrentAsync(principal, data.Observatory, data.Camera, null).ConfigureAwait(false);
        var preview = await AddPreviewAsync(services, data.Frames[6], put: true).ConfigureAwait(false);
        var selected = (await service.SelectCurrentAsync(principal, data.Observatory, data.Camera, page!.NextCursor).ConfigureAwait(false))!;
        selected.Capture!.CaptureId.Should().Be(data.Frames[6].Id);
        _ = await AddPreviewAsync(services, data.Frames[0], put: true).ConfigureAwait(false);
        _ = await AddPreviewAsync(services, data.Frames[6], put: true).ConfigureAwait(false);
        (await service.SelectCurrentAsync(principal, data.Observatory, data.Camera, null).ConfigureAwait(false))!
            .Capture!.CaptureId.Should().Be(data.Frames[0].Id);
        var rechecked = (await service.RevalidateCurrentAsync(principal, data.Observatory, data.Camera, selected).ConfigureAwait(false))!;
        rechecked.Capture!.CaptureId.Should().Be(selected.Capture.CaptureId);
        rechecked.ArtifactId.Should().Be(preview.ArtifactId);
        rechecked.Status.Should().Be("found");
        rechecked.ExaminedCaptures.Should().Be(1);
        rechecked.ExaminedArtifacts.Should().Be(1);

        await services.GetRequiredService<IObjectStore>().DeleteAsync("skymonitor-artifacts", Key(preview), CancellationToken.None).ConfigureAwait(false);
        var unavailable = (await service.RevalidateCurrentAsync(principal, data.Observatory, data.Camera, rechecked).ConfigureAwait(false))!;
        unavailable.Status.Should().Be("unavailable");
        unavailable.Capture!.CaptureId.Should().Be(selected.Capture.CaptureId);
        unavailable.ArtifactId.Should().Be(preview.ArtifactId);
        unavailable.ContentPath.Should().BeNull();
        await PutAsync(services, preview).ConfigureAwait(false);
        (await service.RevalidateCurrentAsync(principal, data.Observatory, data.Camera, unavailable).ConfigureAwait(false))!
            .ArtifactId.Should().Be(preview.ArtifactId);

        var revoking = new LogicalCameraArchiveReadService(db, services.GetRequiredService<IDataProtectionProvider>(),
            services.GetRequiredService<INetworkOperationsReadService>(), services.GetRequiredService<ICentralArtifactRetrievalService>(),
            new FaultReader(services.GetRequiredService<ICentralArtifactObjectReader>(), async () =>
                { await db.ObservatoryMemberships.Where(item => item.ObservatoryId == data.Observatory && item.UserId == data.Owner).ExecuteDeleteAsync().ConfigureAwait(false); }));
        (await revoking.RevalidateCurrentAsync(principal, data.Observatory, data.Camera, selected).ConfigureAwait(false)).Should().BeNull();
    }

    [TestMethod]
    public async Task SqlServer_RevalidationPreservesContinuationWithoutSearchingAndBindsActor()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ApplicationDbContext>();
        var data = await SeedAsync(services, 12).ConfigureAwait(false);
        var service = services.GetRequiredService<ILogicalCameraArchiveReadService>();
        var principal = Principal(data.Owner);
        var page = (await service.SelectCurrentAsync(principal, data.Observatory, data.Camera, null).ConfigureAwait(false))!;
        _ = await AddPreviewAsync(services, data.Frames[0], put: true).ConfigureAwait(false);
        var rechecked = (await service.RevalidateCurrentAsync(principal, data.Observatory, data.Camera, page).ConfigureAwait(false))!;
        rechecked.Status.Should().Be("continue");
        rechecked.NextCursor.Should().Be(page.NextCursor);
        rechecked.Capture.Should().BeNull();
        rechecked.ExaminedCaptures.Should().Be(0);
        rechecked.ExaminedArtifacts.Should().Be(0);
        var viewer = await db.Users.SingleAsync(user => user.Email == TestUsers.Viewer.Email).ConfigureAwait(false);
        db.ObservatoryMemberships.Add(new() { ObservatoryId = data.Observatory, UserId = viewer.Id,
            Role = ObservatoryMembershipRole.Viewer, AddedAtUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync().ConfigureAwait(false);
        Func<Task> anotherActor = () => service.RevalidateCurrentAsync(Principal(viewer.Id), data.Observatory, data.Camera, page);
        await anotherActor.Should().ThrowAsync<ArgumentException>().ConfigureAwait(false);
        await db.ObservatoryMemberships.Where(item => item.ObservatoryId == data.Observatory && item.UserId == data.Owner)
            .ExecuteDeleteAsync().ConfigureAwait(false);
        (await service.RevalidateCurrentAsync(principal, data.Observatory, data.Camera, page).ConfigureAwait(false)).Should().BeNull();
    }

    [TestMethod]
    public async Task SqlServer_FiltersPrecedeTakeAndCursorsBindScopeActorAndFilters()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ApplicationDbContext>();
        var data = await SeedAsync(services, 70).ConfigureAwait(false);
        var service = services.GetRequiredService<ILogicalCameraArchiveReadService>();
        var principal = Principal(data.Owner);
        var filter = new CameraArchiveFilter(Product: FrameArtifactRole.Preview, ObjectState: CentralArtifactObjectState.Expired);
        var page = await service.ListCapturesAsync(principal, data.Observatory, data.Camera, filter, 1, null).ConfigureAwait(false);
        page.Items.Should().ContainSingle().Which.CaptureId.Should().Be(data.Frames[10].Id);
        page.NextCursor.Should().NotBeNull();
        var next = await service.ListCapturesAsync(principal, data.Observatory, data.Camera, filter, 1, page.NextCursor).ConfigureAwait(false);
        next.Items.Should().ContainSingle().Which.CaptureId.Should().Be(data.Frames[20].Id);
        next.NextCursor.Should().BeNull();
        var datePage = await service.ListCapturesAsync(principal, data.Observatory, data.Camera,
            new(FromUtc: data.Frames[20].CapturedAtUtc, UntilUtc: data.Frames[10].CapturedAtUtc), 50, null).ConfigureAwait(false);
        datePage.Items.Should().HaveCount(10);
        var exact = await service.ListCapturesAsync(principal, data.Observatory, data.Camera,
            new(Text: data.Frames[60].FrameId.ToString()), 50, null).ConfigureAwait(false);
        exact.Items.Should().ContainSingle().Which.CaptureId.Should().Be(data.Frames[60].Id);
        var sequence = await service.ListCapturesAsync(principal, data.Observatory, data.Camera,
            new(Text: "00060"), 50, null).ConfigureAwait(false);
        sequence.Items.Should().ContainSingle().Which.CaptureId.Should().Be(data.Frames[60].Id);
        Func<Task> changed = () => service.ListCapturesAsync(principal, data.Observatory, data.Camera, new(), 1, page.NextCursor);
        await changed.Should().ThrowAsync<ArgumentException>().ConfigureAwait(false);
        Func<Task> wrongPurpose = () => service.SelectCurrentAsync(principal, data.Observatory, data.Camera, page.NextCursor);
        await wrongPurpose.Should().ThrowAsync<ArgumentException>().ConfigureAwait(false);
        Func<Task> tampered = () => service.ListCapturesAsync(principal, data.Observatory, data.Camera, filter, 1, page.NextCursor + "x");
        await tampered.Should().ThrowAsync<ArgumentException>().ConfigureAwait(false);
        Func<Task> unsupportedText = () => service.ListCapturesAsync(principal, data.Observatory, data.Camera, new(Text: "clear sky"), 1, null);
        await unsupportedText.Should().ThrowAsync<ArgumentException>().ConfigureAwait(false);
        Func<Task> tooMany = () => service.ListCapturesAsync(principal, data.Observatory, data.Camera, new(), 51, null);
        await tooMany.Should().ThrowAsync<ArgumentOutOfRangeException>().ConfigureAwait(false);
        var other = await SeedAsync(services, 1).ConfigureAwait(false);
        Func<Task> otherCamera = () => service.ListCapturesAsync(principal, other.Observatory, other.Camera, filter, 1, page.NextCursor);
        await otherCamera.Should().ThrowAsync<ArgumentException>().ConfigureAwait(false);
        (await service.ListCapturesAsync(principal, data.Observatory, other.Camera, new(), 50, null).ConfigureAwait(false)).Items.Should().BeEmpty();
        (await service.GetCaptureAsync(principal, data.Observatory, data.Camera, other.Frames[0].Id).ConfigureAwait(false)).Should().BeNull();
        (await service.ListCapturesAsync(principal, data.Observatory, data.Camera, new(Product: FrameArtifactRole.Raw,
            ObjectState: CentralArtifactObjectState.Expired), 50, null).ConfigureAwait(false)).Items.Should().BeEmpty();
        db.ChangeTracker.Clear();
    }

    [TestMethod]
    public async Task SqlServer_CurrentIsBoundedExactAndRechecksContentAndMembership()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ApplicationDbContext>();
        var data = await SeedAsync(services, 12).ConfigureAwait(false);
        var principal = Principal(data.Owner);
        var service = services.GetRequiredService<ILogicalCameraArchiveReadService>();
        var page = await service.SelectCurrentAsync(principal, data.Observatory, data.Camera, null).ConfigureAwait(false);
        page!.Status.Should().Be("continue");
        page.ExaminedCaptures.Should().Be(LogicalCameraArchiveReadService.CurrentCaptureLimit);
        page.Capture.Should().BeNull();
        page.NextCursor.Should().NotBeNull();
        var artifact = await AddPreviewAsync(services, data.Frames[6], put: true).ConfigureAwait(false);
        var selected = await service.SelectCurrentAsync(principal, data.Observatory, data.Camera, page.NextCursor).ConfigureAwait(false);
        selected!.Capture!.CaptureId.Should().Be(data.Frames[6].Id);
        selected.ArtifactId.Should().Be(artifact.ArtifactId);
        selected.Status.Should().Be("found");
        // Newer metadata without bytes must not hide an older verified presentation.
        var missing = await AddPreviewAsync(services, data.Frames[5], put: false).ConfigureAwait(false);
        selected = await service.SelectCurrentAsync(principal, data.Observatory, data.Camera, page.NextCursor).ConfigureAwait(false);
        selected!.Capture!.CaptureId.Should().Be(data.Frames[6].Id);
        var exact = await service.GetCaptureAsync(principal, data.Observatory, data.Camera, data.Frames[5].Id).ConfigureAwait(false);
        exact!.Capture.CaptureId.Should().Be(data.Frames[5].Id);
        exact.Artifacts.Should().ContainSingle(item => item.ArtifactId == missing.ArtifactId);
        await PutAsync(services, missing).ConfigureAwait(false);
        selected = await service.SelectCurrentAsync(principal, data.Observatory, data.Camera, page.NextCursor).ConfigureAwait(false);
        selected!.Capture!.CaptureId.Should().Be(data.Frames[5].Id);
        await services.GetRequiredService<IObjectStore>().DeleteAsync("skymonitor-artifacts", Key(missing), CancellationToken.None).ConfigureAwait(false);
        selected = await service.SelectCurrentAsync(principal, data.Observatory, data.Camera, page.NextCursor).ConfigureAwait(false);
        selected!.Capture!.CaptureId.Should().Be(data.Frames[6].Id);
        await db.CentralArtifacts.Where(item => item.Id == missing.Id).ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.ObjectState, CentralArtifactObjectState.Pending)
            .SetProperty(item => item.StateReasonCode, "object.missing")).ConfigureAwait(false);
        exact = await service.GetCaptureAsync(principal, data.Observatory, data.Camera, data.Frames[5].Id).ConfigureAwait(false);
        exact!.Artifacts.Single(item => item.ArtifactId == missing.ArtifactId).StateReasonCode.Should().Be("object.missing");
        var failing = new LogicalCameraArchiveReadService(db, services.GetRequiredService<IDataProtectionProvider>(),
            services.GetRequiredService<INetworkOperationsReadService>(), services.GetRequiredService<ICentralArtifactRetrievalService>(),
            new FaultReader(services.GetRequiredService<ICentralArtifactObjectReader>(), () => throw new CentralArtifactStorageException(new IOException("injected"))));
        (await failing.SelectCurrentAsync(principal, data.Observatory, data.Camera, page.NextCursor).ConfigureAwait(false))!.Status.Should().Be("dependency-unavailable");
        var revoking = new LogicalCameraArchiveReadService(db, services.GetRequiredService<IDataProtectionProvider>(),
            services.GetRequiredService<INetworkOperationsReadService>(), services.GetRequiredService<ICentralArtifactRetrievalService>(),
            new FaultReader(services.GetRequiredService<ICentralArtifactObjectReader>(), async () =>
                { await db.ObservatoryMemberships.Where(item => item.ObservatoryId == data.Observatory && item.UserId == data.Owner).ExecuteDeleteAsync().ConfigureAwait(false); }));
        (await revoking.SelectCurrentAsync(principal, data.Observatory, data.Camera, page.NextCursor).ConfigureAwait(false)).Should().BeNull();
        (await service.ListCapturesAsync(principal, data.Observatory, data.Camera, new(), 50, null).ConfigureAwait(false)).Items.Should().BeEmpty();
    }

    [TestMethod]
    public async Task SqlServer_CurrentRetainsEligibleSiblingsBeforeAdvancingCapture()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ApplicationDbContext>();
        var data = await SeedAsync(services, 2).ConfigureAwait(false);
        _ = await AddPreviewAsync(services, data.Frames[1], put: true).ConfigureAwait(false);
        var usable = await AddPreviewAsync(services, data.Frames[0], put: true).ConfigureAwait(false);
        var preferred = await AddPreviewAsync(services, data.Frames[0], put: false).ConfigureAwait(false);
        var received = DateTimeOffset.UtcNow;
        usable.ReceivedAtUtc = received;
        preferred.ReceivedAtUtc = received.AddSeconds(1);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var service = services.GetRequiredService<ILogicalCameraArchiveReadService>();
        var principal = Principal(data.Owner);
        var selected = await service.SelectCurrentAsync(principal, data.Observatory, data.Camera, null).ConfigureAwait(false);
        selected!.Status.Should().Be("found");
        selected.Capture!.CaptureId.Should().Be(data.Frames[0].Id);
        selected.ArtifactId.Should().Be(usable.ArtifactId);
        // A same-length corrupt object is also ineligible, without hiding a usable sibling.
        var corrupt = Preview.ToArray();
        corrupt[0] ^= 255;
        using (var stream = new MemoryStream(corrupt))
            await services.GetRequiredService<IObjectStore>().PutAsync("skymonitor-artifacts", Key(preferred), stream,
                corrupt.Length, "image/png", CancellationToken.None).ConfigureAwait(false);
        selected = await service.SelectCurrentAsync(principal, data.Observatory, data.Camera, null).ConfigureAwait(false);
        selected!.Capture!.CaptureId.Should().Be(data.Frames[0].Id);
        selected.ArtifactId.Should().Be(usable.ArtifactId);
        var tied = new[] { usable, preferred }.OrderBy(item => new SqlGuid(item.ArtifactId)).ToArray();
        foreach (var item in tied)
        {
            item.ReceivedAtUtc = received;
            await services.GetRequiredService<IObjectStore>().DeleteAsync("skymonitor-artifacts", Key(item), CancellationToken.None).ConfigureAwait(false);
        }
        usable = tied[0];
        await PutAsync(services, usable).ConfigureAwait(false);
        await db.SaveChangesAsync().ConfigureAwait(false);
        selected = await service.SelectCurrentAsync(principal, data.Observatory, data.Camera, null).ConfigureAwait(false);
        selected!.ArtifactId.Should().Be(usable.ArtifactId);
        selected.ExaminedArtifacts.Should().Be(2);
        // More siblings than the per-request budget must resume within this capture.
        for (var index = 0; index < 6; index++)
        {
            var missing = await AddPreviewAsync(services, data.Frames[0], put: false).ConfigureAwait(false);
            missing.ReceivedAtUtc = received.AddSeconds(10 + index);
        }
        await db.SaveChangesAsync().ConfigureAwait(false);
        var page = await service.SelectCurrentAsync(principal, data.Observatory, data.Camera, null).ConfigureAwait(false);
        page!.Status.Should().Be("continue");
        page.ExaminedCaptures.Should().Be(1);
        page.ExaminedArtifacts.Should().Be(LogicalCameraArchiveReadService.CurrentArtifactLimit);
        page.Capture.Should().BeNull();
        page.ArtifactId.Should().BeNull();
        page.NextCursor.Should().NotBeNull();
        selected = await service.SelectCurrentAsync(principal, data.Observatory, data.Camera, page.NextCursor).ConfigureAwait(false);
        selected!.Status.Should().Be("found");
        selected.Capture!.CaptureId.Should().Be(data.Frames[0].Id);
        selected.ArtifactId.Should().Be(usable.ArtifactId);
        selected.ExaminedArtifacts.Should().Be(3);
        var other = await SeedAsync(services, 1).ConfigureAwait(false);
        Func<Task> changedScope = () => service.SelectCurrentAsync(principal, other.Observatory, other.Camera, page.NextCursor);
        await changedScope.Should().ThrowAsync<ArgumentException>().ConfigureAwait(false);
        Func<Task> changedPurpose = () => service.ListCapturesAsync(principal, data.Observatory, data.Camera, new(), 50, page.NextCursor);
        await changedPurpose.Should().ThrowAsync<ArgumentException>().ConfigureAwait(false);
        await db.ObservatoryMemberships.Where(item => item.ObservatoryId == data.Observatory && item.UserId == data.Owner)
            .ExecuteDeleteAsync().ConfigureAwait(false);
        (await service.SelectCurrentAsync(principal, data.Observatory, data.Camera, page.NextCursor).ConfigureAwait(false)).Should().BeNull();
        // Exhausting exactly the budget is not evidence of another page.
        for (var index = 0; index < LogicalCameraArchiveReadService.CurrentArtifactLimit; index++)
            _ = await AddPreviewAsync(services, other.Frames[0], put: false).ConfigureAwait(false);
        var exhausted = await service.SelectCurrentAsync(principal, other.Observatory, other.Camera, null).ConfigureAwait(false);
        exhausted!.Status.Should().Be("no-result");
        exhausted.ExaminedArtifacts.Should().Be(LogicalCameraArchiveReadService.CurrentArtifactLimit);
        exhausted.NextCursor.Should().BeNull();
    }

    [TestMethod]
    public async Task SqlServer_RolesScopedCredentialsReplacementAndEqualTimesRemainIsolated()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ApplicationDbContext>();
        var data = await SeedAsync(services, 4, equalTimes: true).ConfigureAwait(false);
        var other = await SeedAsync(services, 2).ConfigureAwait(false);
        var viewer = await db.Users.SingleAsync(user => user.Email == TestUsers.Viewer.Email).ConfigureAwait(false);
        db.ObservatoryMemberships.Add(new() { ObservatoryId = other.Observatory, UserId = viewer.Id, Role = ObservatoryMembershipRole.Viewer, AddedAtUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync().ConfigureAwait(false);
        var service = services.GetRequiredService<ILogicalCameraArchiveReadService>();
        foreach (var denied in new[] { Principal(viewer.Id), new ClaimsPrincipal(new ClaimsIdentity()) })
        {
            (await service.ListCapturesAsync(denied, data.Observatory, data.Camera, new(), 50, null).ConfigureAwait(false)).Items.Should().BeEmpty();
            (await service.SelectCurrentAsync(denied, data.Observatory, data.Camera, null).ConfigureAwait(false)).Should().BeNull();
            (await service.GetCaptureAsync(denied, data.Observatory, data.Camera, data.Frames[0].Id).ConfigureAwait(false)).Should().BeNull();
        }
        var membership = new ObservatoryMembership { ObservatoryId = data.Observatory, UserId = viewer.Id, Role = ObservatoryMembershipRole.Viewer, AddedAtUtc = DateTimeOffset.UtcNow };
        db.ObservatoryMemberships.Add(membership);
        await db.SaveChangesAsync().ConfigureAwait(false);
        foreach (var role in new[] { ObservatoryMembershipRole.Viewer, ObservatoryMembershipRole.Manager, ObservatoryMembershipRole.Owner })
        {
            membership.Role = role;
            await db.SaveChangesAsync().ConfigureAwait(false);
            (await service.ListCapturesAsync(Principal(viewer.Id), data.Observatory, data.Camera, new(), 50, null).ConfigureAwait(false)).Items.Should().HaveCount(4);
        }
        var scoped = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, viewer.Id),
            new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType),
            new Claim(ApiKeyClaims.AuthenticationType, ApiKeyAuthenticationOptions.AuthenticationScheme),
            new Claim(ApiKeyClaims.ApiKeyId, Guid.NewGuid().ToString()),
            new Claim(ApiKeyClaims.AccessLevel, nameof(ApiKeyAccessLevel.Read)),
            new Claim(ApiKeyClaims.ObservatoryId, data.Observatory.ToString())
        }, ApiKeyAuthenticationOptions.AuthenticationScheme));
        (await service.ListCapturesAsync(scoped, data.Observatory, data.Camera, new(), 50, null).ConfigureAwait(false)).Items.Should().HaveCount(4);
        (await service.ListCapturesAsync(scoped, other.Observatory, other.Camera, new(), 50, null).ConfigureAwait(false)).Items.Should().BeEmpty();
        (await service.GetCaptureAsync(scoped, data.Observatory, data.Camera, other.Frames[0].Id).ConfigureAwait(false)).Should().BeNull();
        var expected = await db.CentralFrames.Where(frame => frame.ObservatoryId == data.Observatory)
            .OrderByDescending(frame => frame.CapturedAtUtc).ThenByDescending(frame => frame.Id).Select(frame => frame.Id).ToArrayAsync().ConfigureAwait(false);
        var seen = new List<Guid>();
        string? cursor = null;
        for (var index = 0; index < 4; index++)
        {
            var page = await service.ListCapturesAsync(scoped, data.Observatory, data.Camera, new(), 1, cursor).ConfigureAwait(false);
            seen.Add(page.Items.Single().CaptureId);
            cursor = page.NextCursor;
        }
        seen.Should().Equal(expected);
        cursor.Should().BeNull();
        var empty = await SeedAsync(services, 0).ConfigureAwait(false);
        (await service.SelectCurrentAsync(Principal(empty.Owner), empty.Observatory, empty.Camera, null).ConfigureAwait(false))!.Status.Should().Be("no-result");
        // Both retained installation identities are represented even after replacement.
        var all = await service.ListCapturesAsync(scoped, data.Observatory, data.Camera, new(), 50, null).ConfigureAwait(false);
        all.Items.Select(item => item.InstallationId).Distinct().Should().HaveCount(2);
        using var client = AssemblyHooks.Fixture.Factory.CreateClient();
        using var deniedResponse = await client.GetAsync(new Uri($"/api/v1.0/observatories/{data.Observatory}/cameras/{data.Camera}/captures", UriKind.Relative)).ConfigureAwait(false);
        deniedResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var ownerPage = await service.ListCapturesAsync(Principal(data.Owner), data.Observatory, data.Camera, new(), 1, null).ConfigureAwait(false);
        Func<Task> otherActor = () => service.ListCapturesAsync(Principal(viewer.Id), data.Observatory, data.Camera, new(), 1, ownerPage.NextCursor);
        await otherActor.Should().ThrowAsync<ArgumentException>().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task SqlServer_IneligibleStatesAndHttpReadsKeepExactMetadata()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ApplicationDbContext>();
        var data = await SeedAsync(services, 2).ConfigureAwait(false);
        var latest = await AddPreviewAsync(services, data.Frames[0], true).ConfigureAwait(false);
        var older = await AddPreviewAsync(services, data.Frames[1], true).ConfigureAwait(false);
        var service = services.GetRequiredService<ILogicalCameraArchiveReadService>();
        var principal = Principal(data.Owner);
        foreach (var (state, reconstruction) in new[] {
            (CentralArtifactObjectState.Pending, CentralReconstructionState.Complete),
            (CentralArtifactObjectState.Expired, CentralReconstructionState.Complete),
            (CentralArtifactObjectState.Quarantined, CentralReconstructionState.Quarantined),
            (CentralArtifactObjectState.Available, CentralReconstructionState.PendingReference) })
        {
            await db.CentralArtifacts.Where(item => item.Id == latest.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ObjectState, state).SetProperty(item => item.ReconstructionState, reconstruction)
                .SetProperty(item => item.StateReasonCode, "fixture.unavailable")).ConfigureAwait(false);
            var result = await service.SelectCurrentAsync(principal, data.Observatory, data.Camera, null).ConfigureAwait(false);
            result!.Capture!.CaptureId.Should().Be(data.Frames[1].Id);
            result.ArtifactId.Should().Be(older.ArtifactId);
            var exact = await service.GetCaptureAsync(principal, data.Observatory, data.Camera, data.Frames[0].Id).ConfigureAwait(false);
            exact!.Artifacts.Single().StateReasonCode.Should().Be("fixture.unavailable");
        }
        using var client = AssemblyHooks.Fixture.Factory.CreateClient();
        var token = await HttpHelpers.GetPasswordTokenAsync(client, "/connect/token", TestUsers.Operator.Username,
            TestUsers.Operator.Password, TestClients.WebUI.ClientId, "api.viewer").ConfigureAwait(false);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        var route = $"/api/v1.0/observatories/{data.Observatory}/cameras/{data.Camera}";
        using var response = await client.GetAsync(new Uri(route + "/captures?take=1&Product=Preview", UriKind.Relative)).ConfigureAwait(false);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var page = await response.Content.ReadFromJsonAsync<CameraArchivePage>().ConfigureAwait(false);
        page!.Items.Should().ContainSingle().Which.CaptureId.Should().Be(latest.CentralFrameId);
        using var currentResponse = await client.GetAsync(new Uri(route + "/current", UriKind.Relative)).ConfigureAwait(false);
        currentResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var current = await currentResponse.Content.ReadFromJsonAsync<CameraCurrentSelection>().ConfigureAwait(false);
        current!.ArtifactId.Should().Be(older.ArtifactId);
        using var bytes = await client.GetAsync(new Uri(current.ContentPath!, UriKind.Relative)).ConfigureAwait(false);
        bytes.StatusCode.Should().Be(HttpStatusCode.OK);
        (await bytes.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).Should().Equal(Preview);
        using var bad = await client.GetAsync(new Uri(route + "/captures?take=51", UriKind.Relative)).ConfigureAwait(false);
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var exactResponse = await client.GetAsync(new Uri(route + $"/captures/{latest.CentralFrameId}", UriKind.Relative)).ConfigureAwait(false);
        exactResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var exactDetail = await exactResponse.Content.ReadFromJsonAsync<OperationsCaptureDetail>().ConfigureAwait(false);
        exactDetail!.Capture.CaptureId.Should().Be(latest.CentralFrameId);
    }

    [TestMethod]
    public async Task CurrentSky_ServiceAndEndpointsIntersectCredentialScopeMembershipAndRetainedInstallations()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ApplicationDbContext>();
        var a = await SeedAsync(services, 2).ConfigureAwait(false);
        var b = await SeedAsync(services, 1).ConfigureAwait(false);
        var retained = await AddPreviewAsync(services, a.Frames[0], true).ConfigureAwait(false);
        var foreign = await AddPreviewAsync(services, b.Frames[0], true).ConfigureAwait(false);
        var owner = await db.Users.SingleAsync(user => user.Id == a.Owner).ConfigureAwait(false);
        var key = await services.GetRequiredService<IApiKeyLifecycleService>().CreateAsync(owner.Id, owner.Email!,
            "current-sky-read", ApiKeyAccessLevel.Read, a.Observatory, DateTimeOffset.UtcNow.AddMinutes(10), CancellationToken.None).ConfigureAwait(false);
        using var client = AssemblyHooks.Fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationOptions.HeaderName, key.PlaintextKey);
        string Route(Dataset data) => $"/api/v1.0/observatories/{data.Observatory:D}/cameras/{data.Camera:D}";
        async Task AssertDeniedAsync(string path)
        {
            using var response = await client.GetAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(false);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await response.Content.ReadAsStringAsync().ConfigureAwait(false)).Should().NotContain(foreign.ArtifactId.ToString());
        }
        using (var selected = await client.GetAsync(new Uri(Route(a) + "/current", UriKind.Relative)).ConfigureAwait(false))
        {
            selected.StatusCode.Should().Be(HttpStatusCode.OK);
            selected.Headers.CacheControl!.NoStore.Should().BeTrue();
            var result = await selected.Content.ReadFromJsonAsync<CameraCurrentSelection>().ConfigureAwait(false);
            result!.Capture!.CaptureId.Should().Be(a.Frames[0].Id);
            result.ArtifactId.Should().Be(retained.ArtifactId);
            // The latest capture belongs to the retired installation, not its replacement.
            (await db.LogicalCameraInstallations.SingleAsync(i => i.Id == result.Capture.InstallationId).ConfigureAwait(false))
                .RetiredAtUtc.Should().NotBeNull();
        }
        await AssertDeniedAsync(Route(b) + "/current").ConfigureAwait(false);
        await AssertDeniedAsync(Route(a) + $"/captures/{b.Frames[0].Id:D}").ConfigureAwait(false);
        await AssertDeniedAsync($"/api/v1.0/observatories/{a.Observatory:D}/cameras/{b.Camera:D}/current").ConfigureAwait(false);
        await AssertDeniedAsync($"/api/v1.0/devices/{foreign.DevicePublicId:D}/artifacts/{foreign.ArtifactId:D}/content").ConfigureAwait(false);
        await db.ObservatoryMemberships.Where(m => m.ObservatoryId == a.Observatory && m.UserId == a.Owner).ExecuteDeleteAsync().ConfigureAwait(false);
        var service = services.GetRequiredService<ILogicalCameraArchiveReadService>();
        (await service.SelectCurrentAsync(Principal(a.Owner), a.Observatory, a.Camera, null).ConfigureAwait(false)).Should().BeNull();
        await AssertDeniedAsync(Route(a) + "/current").ConfigureAwait(false);
        await AssertDeniedAsync(Route(a) + $"/captures/{a.Frames[0].Id:D}").ConfigureAwait(false);
        using var anonymous = AssemblyHooks.Fixture.Factory.CreateClient();
        using var denied = await anonymous.GetAsync(new Uri(Route(a) + "/current", UriKind.Relative)).ConfigureAwait(false);
        denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    internal static ClaimsPrincipal Principal(string owner) => new(new ClaimsIdentity(
        new[] { new Claim(ClaimTypes.NameIdentifier, owner), new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType) }, IdentityConstants.ApplicationScheme));

    internal static async Task<Dataset> SeedAsync(IServiceProvider services, int count, bool equalTimes = false)
    {
        var db = services.GetRequiredService<ApplicationDbContext>();
        var owner = await db.Users.SingleAsync(user => user.Email == TestUsers.Operator.Email).ConfigureAwait(false);
        var now = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var observatory = new Observatory { OwnerUserId = owner.Id, Name = $"Archive {Guid.NewGuid():N}", TimeZoneId = "UTC", CreatedAtUtc = now, IsActive = true };
        db.Observatories.Add(observatory);
        db.ObservatoryMemberships.Add(new() { ObservatoryId = observatory.Id, UserId = owner.Id, Role = ObservatoryMembershipRole.Owner, AddedAtUtc = now });
        var camera = new LogicalCamera { ObservatoryId = observatory.Id, Name = "Camera", Slug = "camera", CreatedAtUtc = now, CreatedByUserId = owner.Id };
        db.LogicalCameras.Add(camera);
        var frames = new List<CentralFrame>();
        for (var installationIndex = 0; installationIndex < 2; installationIndex++)
        {
            var registration = new DeviceRegistration
            {
                DeviceId = $"archive-{Guid.NewGuid():N}",
                DevicePublicId = Guid.NewGuid(),
                ObservatoryId = observatory.Id,
                ObservatoryName = observatory.Name,
                ObservatoryTimeZoneId = "UTC",
                FriendlyName = "Archive",
                OwnerUserId = owner.Id,
                OwnerDisplayName = "Owner",
                OwnerConfirmationMethod = "SelfAttested",
                Status = DeviceRegistrationStatus.Active,
                VerificationCodeHash = "hash",
                IssuedAtUtc = now,
                ActivatedAtUtc = now
            };
            db.DeviceRegistrations.Add(registration);
            var installation = new LogicalCameraInstallation
            {
                LogicalCameraId = camera.Id,
                RegistrationId = registration.Id,
                InstallationPublicId = Guid.NewGuid(),
                AssignedAtUtc = now.AddDays(-2 + installationIndex),
                RetiredAtUtc = installationIndex == 0 ? now.AddDays(-1) : null,
                AssignedByUserId = owner.Id,
                AssignmentReasonCode = "integration"
            };
            db.LogicalCameraInstallations.Add(installation);
            for (var index = installationIndex; index < count; index += 2)
                frames.Add(new CentralFrame
                {
                    RegistrationId = registration.Id,
                    LogicalCameraInstallationId = installation.Id,
                    DevicePublicId = registration.DevicePublicId.Value,
                    ObservatoryId = observatory.Id,
                    AgentId = registration.DeviceId,
                    FrameId = Guid.NewGuid(),
                    CapturedAtUtc = equalTimes ? now : now.AddSeconds(-index),
                    FirstReceivedAtUtc = now.AddSeconds(index),
                    CaptureSequence = index
                });
        }
        frames = frames.OrderBy(frame => frame.CaptureSequence).ToList();
        db.CentralFrames.AddRange(frames);
        await db.SaveChangesAsync().ConfigureAwait(false);
        foreach (var index in ExpiredIndexes.Where(index => index < count))
        {
            var artifact = await AddPreviewAsync(services, frames[index], put: false).ConfigureAwait(false);
            artifact.ObjectState = CentralArtifactObjectState.Expired;
        }
        await db.SaveChangesAsync().ConfigureAwait(false);
        return new(owner.Id, observatory.Id, camera.Id, frames);
    }

    private static readonly byte[] Preview = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jZbkAAAAASUVORK5CYII=");
    internal static async Task<CentralArtifact> AddPreviewAsync(IServiceProvider services, CentralFrame frame, bool put, byte[]? previewBytes = null)
    {
        var bytes = previewBytes ?? Preview;
        var db = services.GetRequiredService<ApplicationDbContext>();
        var artifact = new CentralArtifact
        {
            CentralFrameId = frame.Id,
            DevicePublicId = frame.DevicePublicId,
            ArtifactId = Guid.NewGuid(),
            Role = FrameArtifactRole.Preview,
            RecipeVersion = "preview-v1",
            ManifestSchemaVersion = "v2",
            MediaType = "image/png",
            ByteLength = bytes.Length,
            ChecksumSha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            StorageReference = $"object://skymonitor-artifacts/archive-tests/{Guid.NewGuid():N}",
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete
        };
        db.CentralArtifacts.Add(artifact);
        await db.SaveChangesAsync().ConfigureAwait(false);
        if (put) await PutAsync(services, artifact, bytes).ConfigureAwait(false);
        return artifact;
    }
    private static string Key(CentralArtifact artifact) => artifact.StorageReference["object://skymonitor-artifacts/".Length..];
    private static async Task PutAsync(IServiceProvider services, CentralArtifact artifact, byte[]? previewBytes = null)
    {
        var bytes = previewBytes ?? Preview;
        using var stream = new MemoryStream(bytes);
        await services.GetRequiredService<IObjectStore>().PutAsync("skymonitor-artifacts", Key(artifact), stream, bytes.Length, "image/png", CancellationToken.None).ConfigureAwait(false);
    }
    internal sealed record Dataset(string Owner, Guid Observatory, Guid Camera, IReadOnlyList<CentralFrame> Frames);
    private sealed class FaultReader(ICentralArtifactObjectReader inner, Func<Task> fault) : ICentralArtifactObjectReader
    {
        public async Task<CentralArtifactObjectSnapshot> VerifyAsync(CentralArtifact artifact, CancellationToken token)
        {
            var snapshot = await inner.VerifyAsync(artifact, token).ConfigureAwait(false);
            await fault().ConfigureAwait(false);
            return snapshot;
        }
        public Task<bool> IsCurrentGenerationAsync(CentralArtifact artifact, string generation, CancellationToken token) => inner.IsCurrentGenerationAsync(artifact, generation, token);
        public Task CopyToAsync(CentralArtifactObjectSnapshot snapshot, Stream stream, CentralArtifactByteRange? range, CancellationToken token) => inner.CopyToAsync(snapshot, stream, range, token);
    }
}
