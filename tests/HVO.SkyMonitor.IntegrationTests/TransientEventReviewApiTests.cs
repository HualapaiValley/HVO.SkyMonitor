using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class TransientEventReviewApiTests
{
    [TestMethod]
    public async Task OwnerReview_RequiresPreconditionsAndSystemCredentialsCannotMutate()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var owner = await db.Users.SingleAsync(user => user.Email == TestUsers.Operator.Email).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var observatory = new Observatory
        {
            OwnerUserId = owner.Id,
            Name = $"Transient review {Guid.NewGuid():N}",
            TimeZoneId = "UTC",
            CreatedAtUtc = now,
            IsActive = true
        };
        var registration = new DeviceRegistration
        {
            DeviceId = $"transient-review-{Guid.NewGuid():N}",
            ObservatoryId = observatory.Id,
            Observatory = observatory,
            FriendlyName = "Transient review fixture",
            ObservatoryName = observatory.Name,
            ObservatoryTimeZoneId = observatory.TimeZoneId,
            OwnerUserId = owner.Id,
            OwnerDisplayName = TestUsers.Operator.FullName,
            OwnerEmail = owner.Email,
            OwnerConfirmationMethod = "SelfAttested",
            OwnerConfirmedAtUtc = now,
            Status = DeviceRegistrationStatus.Active,
            VerificationCodeHash = DeviceRegistrationService.ComputeSha256("ABCDE"),
            DevicePublicId = Guid.NewGuid(),
            IssuedAtUtc = now,
            ActivatedAtUtc = now
        };
        db.DeviceRegistrations.Add(registration);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var fixture = CentralTransientPersistenceFixture.Create();
        var seeded = await CentralTransientEventPersistenceIntegrationTests.SeedAsync(
            db, fixture, registrationId: registration.Id).ConfigureAwait(false);
        var persistence = new CentralTransientEventPersistence(db);
        _ = await persistence.AppendAsync(fixture.Request with
        {
            CentralDerivativeJobId = seeded.JobId
        }, CancellationToken.None).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        var current = await db.CentralTransientEventCurrent.AsNoTracking().SingleAsync(item =>
            item.LatestEventVersionId == fixture.Event.EventVersionId).ConfigureAwait(false);
        var path = $"/api/v1.0/transient-events/{current.CentralTransientEventId:D}";

        using var anonymous = AssemblyHooks.Fixture.Factory.CreateClient();
        var ownerClient = await CreateUserClientAsync(
            anonymous, TestUsers.Operator.Username, TestUsers.Operator.Password).ConfigureAwait(false);
        using var invalidCursor = await ownerClient.GetAsync(
            new Uri("/api/v1.0/transient-events?cursor=___", UriKind.Relative)).ConfigureAwait(false);
        invalidCursor.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var ownerResponse = await ownerClient.GetAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(false);
        ownerResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        ownerResponse.Headers.ETag.Should().NotBeNull();
        var detail = await ownerResponse.Content.ReadFromJsonAsync<CentralTransientEventDetail>(
            HttpHelpers.DefaultJsonOptions).ConfigureAwait(false);
        detail!.Summary.ReviewState.Should().Be(CentralTransientReviewState.NeedsReview);

        var review = new CentralTransientReviewRequest(
            current.ActiveAssessmentId,
            TransientReviewDisposition.Overridden,
            new TransientReviewOverrideV1(
                TransientClassification.Meteor,
                TransientMeteorSeverity.Fireball,
                950_000),
            ["human.confirmed"]);
        using var missingHeaders = await ownerClient.PostAsJsonAsync($"{path}/reviews", review)
            .ConfigureAwait(false);
        missingHeaders.StatusCode.Should().Be((HttpStatusCode)428);

        using var reviewRequest = new HttpRequestMessage(HttpMethod.Post, $"{path}/reviews")
        {
            Content = JsonContent.Create(review)
        };
        var idempotencyKey = $"review-{Guid.NewGuid():N}";
        reviewRequest.Headers.TryAddWithoutValidation("If-Match", ownerResponse.Headers.ETag!.Tag);
        reviewRequest.Headers.Add("Idempotency-Key", idempotencyKey);
        using var reviewed = await ownerClient.SendAsync(reviewRequest).ConfigureAwait(false);
        reviewed.StatusCode.Should().Be(HttpStatusCode.OK);
        reviewed.Headers.ETag.Should().NotBeNull();
        var reviewedBody = await reviewed.Content.ReadFromJsonAsync<CentralTransientReviewMutationResponse>(
            HttpHelpers.DefaultJsonOptions).ConfigureAwait(false);
        reviewedBody!.Replayed.Should().BeFalse();
        var notificationEligible = reviewedBody.EffectiveClassification == TransientClassification.Meteor &&
            reviewedBody.EffectiveMeteorSeverity is TransientMeteorSeverity.Meteor or TransientMeteorSeverity.Fireball;
        if (notificationEligible)
        {
            await WaitUntilAsync(async () =>
            {
                await using var notificationScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
                return await notificationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                    .CentralTransientNotificationDispatches.AsNoTracking().AnyAsync(item =>
                        item.ReviewId == reviewedBody.ReviewId &&
                        item.State == CentralTransientNotificationDispatchState.Sent).ConfigureAwait(false);
            }, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        await using (var notificationScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var notificationDb = notificationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var dispatches = await notificationDb.CentralTransientNotificationDispatches.AsNoTracking()
                .Where(item => item.ReviewId == reviewedBody.ReviewId).ToArrayAsync().ConfigureAwait(false);
            dispatches.Should().NotBeEmpty().And.OnlyContain(item => item.Recipient == null ||
                item.Recipient == owner.Email);
            dispatches.Should().OnlyContain(item => notificationEligible
                ? item.State == CentralTransientNotificationDispatchState.Sent
                : item.State == CentralTransientNotificationDispatchState.Suppressed);
        }

        using var forbiddenReprocessing = await ownerClient.PostAsJsonAsync(
            $"{path}/reprocessing-jobs",
            new CentralTransientReprocessingRequest(fixture.AssessmentOptions with
            {
                FireballMinimumIntegratedSignalAdu = 1
            })).ConfigureAwait(false);
        forbiddenReprocessing.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var systemToken = await HttpHelpers.GetClientCredentialsTokenAsync(
            anonymous,
            "/connect/token",
            TestClients.SystemInternal.ClientId,
            TestClients.SystemInternal.ClientSecret,
            string.Join(' ', TestClients.SystemInternal.Scopes)).ConfigureAwait(false);
        using var adminBaseClient = AssemblyHooks.Fixture.Factory.CreateClient();
        var adminClient = HttpHelpers.WithBearerToken(adminBaseClient, systemToken.AccessToken);
        using var adminDetailResponse = await adminClient.GetAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(false);
        adminDetailResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var adminDetail = await adminDetailResponse.Content.ReadFromJsonAsync<CentralTransientEventDetail>(
            HttpHelpers.DefaultJsonOptions).ConfigureAwait(false);
        adminDetail!.Event.Reviews.Should().OnlyContain(item => item.ReviewerIdentity == "redacted");
        var adminResponseText = await adminDetailResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        adminResponseText.Should().NotContain(owner.Id).And.NotContain(TestUsers.Operator.Username);
        var currentEtag = adminDetailResponse.Headers.ETag!.Tag;
        var reprocessing = new CentralTransientReprocessingRequest(fixture.AssessmentOptions with
        {
            FireballMinimumIntegratedSignalAdu = 1
        });
        using var missingReprocessingHeaders = await adminClient.PostAsJsonAsync(
            $"{path}/reprocessing-jobs", reprocessing).ConfigureAwait(false);
        missingReprocessingHeaders.StatusCode.Should().Be((HttpStatusCode)428);
        var reprocessingKey = $"api-reprocess-{Guid.NewGuid():N}";
        using var reprocessingRequest = new HttpRequestMessage(HttpMethod.Post, $"{path}/reprocessing-jobs")
        {
            Content = JsonContent.Create(reprocessing)
        };
        reprocessingRequest.Headers.TryAddWithoutValidation("If-Match", currentEtag);
        reprocessingRequest.Headers.Add("Idempotency-Key", reprocessingKey);
        using var scheduled = await adminClient.SendAsync(reprocessingRequest).ConfigureAwait(false);
        scheduled.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var replayReprocessing = new HttpRequestMessage(HttpMethod.Post, $"{path}/reprocessing-jobs")
        {
            Content = JsonContent.Create(reprocessing)
        };
        replayReprocessing.Headers.TryAddWithoutValidation("If-Match", currentEtag);
        replayReprocessing.Headers.Add("Idempotency-Key", reprocessingKey);
        using var replayedReprocessing = await adminClient.SendAsync(replayReprocessing).ConfigureAwait(false);
        replayedReprocessing.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await replayedReprocessing.Content.ReadFromJsonAsync<CentralTransientReprocessingResponse>(
            HttpHelpers.DefaultJsonOptions).ConfigureAwait(false))!.Replayed.Should().BeTrue();

        var terminalNotificationId = adminDetail.Event.Notifications[^1].NotificationId;
        using var retryRequest = new HttpRequestMessage(
            HttpMethod.Post, $"{path}/notifications/{terminalNotificationId:D}/retry");
        retryRequest.Headers.TryAddWithoutValidation("If-Match", currentEtag);
        retryRequest.Headers.Add("Idempotency-Key", $"api-retry-{Guid.NewGuid():N}");
        using var ineligibleRetry = await adminClient.SendAsync(retryRequest).ConfigureAwait(false);
        ineligibleRetry.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var releaseRequest = new HttpRequestMessage(HttpMethod.Post, $"{path}/payload-release");
        releaseRequest.Headers.TryAddWithoutValidation("If-Match", currentEtag);
        releaseRequest.Headers.Add("Idempotency-Key", $"api-release-{Guid.NewGuid():N}");
        using var disabledRelease = await adminClient.SendAsync(releaseRequest).ConfigureAwait(false);
        disabledRelease.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var replayRequest = CreateReviewRequest(
            path, review, ownerResponse.Headers.ETag.Tag, idempotencyKey);
        using var replayed = await ownerClient.SendAsync(replayRequest).ConfigureAwait(false);
        replayed.StatusCode.Should().Be(HttpStatusCode.OK);
        replayed.Headers.ETag!.Tag.Should().Be(reviewed.Headers.ETag.Tag);
        (await replayed.Content.ReadFromJsonAsync<CentralTransientReviewMutationResponse>(
            HttpHelpers.DefaultJsonOptions).ConfigureAwait(false))!.Replayed.Should().BeTrue();

        using var conflictRequest = CreateReviewRequest(
            path,
            review with { ReasonCodes = ["human.changed"] },
            ownerResponse.Headers.ETag.Tag,
            idempotencyKey);
        using var conflict = await ownerClient.SendAsync(conflictRequest).ConfigureAwait(false);
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var staleRequest = CreateReviewRequest(
            path, review, ownerResponse.Headers.ETag.Tag, $"stale-{Guid.NewGuid():N}");
        using var stale = await ownerClient.SendAsync(staleRequest).ConfigureAwait(false);
        stale.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);

        var otherClient = await CreateUserClientAsync(
            anonymous, TestUsers.Viewer.Username, TestUsers.Viewer.Password).ConfigureAwait(false);
        using var hidden = await otherClient.GetAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(false);
        hidden.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var hiddenMutationRequest = CreateReviewRequest(
            path, review, reviewed.Headers.ETag.Tag, $"hidden-{Guid.NewGuid():N}");
        using var hiddenMutation = await otherClient.SendAsync(hiddenMutationRequest).ConfigureAwait(false);
        hiddenMutation.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var systemClient = adminClient;
        using var systemRequest = new HttpRequestMessage(HttpMethod.Post, $"{path}/reviews")
        {
            Content = JsonContent.Create(review)
        };
        systemRequest.Headers.TryAddWithoutValidation("If-Match", reviewed.Headers.ETag!.Tag);
        systemRequest.Headers.Add("Idempotency-Key", $"system-{Guid.NewGuid():N}");
        using var systemReview = await systemClient.SendAsync(systemRequest).ConfigureAwait(false);
        systemReview.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var ambiguousCredentialRequest = new HttpRequestMessage(HttpMethod.Post, $"{path}/reviews")
        {
            Content = JsonContent.Create(review)
        };
        ambiguousCredentialRequest.Headers.TryAddWithoutValidation("If-Match", reviewed.Headers.ETag!.Tag);
        ambiguousCredentialRequest.Headers.Add("Idempotency-Key", $"ambiguous-{Guid.NewGuid():N}");
        ambiguousCredentialRequest.Headers.Add("X-API-Key", TestApiKeys.InternalService.Key);
        using var ambiguousReview = await systemClient.SendAsync(ambiguousCredentialRequest).ConfigureAwait(false);
        ambiguousReview.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static async Task<HttpClient> CreateUserClientAsync(
        HttpClient client,
        string username,
        string password)
    {
        var token = await HttpHelpers.GetPasswordTokenAsync(
            client,
            "/connect/token",
            username,
            password,
            TestClients.WebUI.ClientId,
            string.Join(' ', TestClients.WebUI.Scopes)).ConfigureAwait(false);
        return HttpHelpers.WithBearerToken(client, token.AccessToken);
    }

    private static HttpRequestMessage CreateReviewRequest(
        string path,
        CentralTransientReviewRequest review,
        string etag,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{path}/reviews")
        {
            Content = JsonContent.Create(review)
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return request;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!await condition().ConfigureAwait(false))
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Fail("Timed out waiting for transient notification dispatch.");
            }
            await Task.Delay(25).ConfigureAwait(false);
        }
    }
}
