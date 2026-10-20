using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class LogicHostRowLimitQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
    private static Guid Key(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");

    [TestMethod]
    public async Task SqliteFixtureRaisesTheUnorderedLimitWarningAsAnError()
    {
        await using var fixture = new SqliteRowLimitFixture();
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => fixture.Db.CentralFrames.Take(1).ToArrayAsync());
        StringAssert.Contains(error.Message, "RowLimitingOperationWithoutOrderByWarning");
    }

    [TestMethod]
    public async Task SqliteSeedDiagnosticsOrdersRepresentativesAndCountsTheWholeGroup()
    {
        await using var fixture = new SqliteRowLimitFixture();
        foreach (var id in new[] { 3, 1, 2 })
            fixture.Insert(new CentralDerivativeJob
            {
                Id = Key(id), RecipeName = "unknown-recipe", RequestedRecipeIdentitySha256 = new string('A', 64),
                Status = CentralDerivativeJobStatus.Pending
            });
        Assert.AreEqual(3, await CanonicalCentralGraphSeedDiagnostics.CountFrozenIdentityMismatchesAsync(fixture.Db));
        var sample = fixture.Queries.Commands.Single(sql => sql.Contains("LIMIT", StringComparison.Ordinal));
        StringAssert.Contains(sample, "ORDER BY");
    }

    [TestMethod]
    public async Task SqliteSubmissionConflictUsesTheLowestKeyAcrossBothConflictPredicates()
    {
        await using var fixture = new SqliteRowLimitFixture();
        var candidateId = Key(100);
        var eventId = Key(200);
        fixture.Insert(new CentralTransientValidationIdentitySlot
        {
            Id = Key(2), CentralDerivativeJobId = Key(12), CandidateId = candidateId, AgentId = "other"
        });
        fixture.Insert(new CentralTransientValidationIdentitySlot
        {
            Id = Key(1), CentralDerivativeJobId = Key(11), AgentId = "agent", SubmittedEventId = eventId
        });
        Assert.AreEqual(Key(11), await CentralTransientSubmissionService.FindIdentityConflictAsync(
            fixture.Db, candidateId, "agent", eventId, CancellationToken.None));
        Assert.IsNull(await CentralTransientSubmissionService.FindIdentityConflictAsync(
            fixture.Db, Key(999), "unrelated", eventId, CancellationToken.None));
        Assert.AreEqual(2, await fixture.Db.CentralTransientValidationIdentitySlots.CountAsync());
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task SqliteBookmarksUseLatestDecisionWithIdTieBreakAndCanRemoveAfterWithdrawal(bool sameTime, bool remove)
    {
        await using var fixture = new SqliteRowLimitFixture();
        fixture.Insert(new ApplicationUser { Id = "reader", AccountType = AccountType.User });
        var old = Decision(1, Key(100), Key(201), Key(301), Now.AddMinutes(-1));
        var latest = Decision(2, old.PublicId, Key(202), Key(302), sameTime ? old.OccurredAtUtc : Now);
        fixture.Insert(old);
        fixture.Insert(latest);
        var service = new RegisteredUserPersonalizationService(fixture.Db, TimeProvider.System);
        if (!remove)
        {
            Assert.AreEqual(PersonalizationMutationOutcome.Applied, await service.BookmarkAsync("reader", old.PublicId));
            var bookmark = await fixture.Db.RegisteredUserTransientEventBookmarks.SingleAsync();
            Assert.AreEqual(latest.CentralTransientEventId, bookmark.CentralTransientEventId);
            return;
        }
        fixture.Insert(new RegisteredUserTransientEventBookmark
        {
            UserId = "reader", CentralTransientEventId = latest.CentralTransientEventId!.Value, CreatedUtc = Now
        });
        var withdrawal = Decision(3, old.PublicId, latest.CentralTransientEventId!.Value,
            latest.SourceEventVersionId!.Value, sameTime ? latest.OccurredAtUtc : Now.AddMinutes(1));
        withdrawal.State = PublicationDecisionState.Withdrawn;
        withdrawal.SupersedesDecisionId = latest.Id;
        fixture.Insert(withdrawal);
        Assert.AreEqual(PersonalizationMutationOutcome.Applied, await service.UnbookmarkAsync("reader", old.PublicId));
        Assert.AreEqual(0, await fixture.Db.RegisteredUserTransientEventBookmarks.CountAsync());
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task SqlitePublicEventListAndDetailTakeEventAndVersionFromLatestEligibleDecision(bool sameTime, bool detail)
    {
        await using var fixture = new SqliteRowLimitFixture();
        var observatory = new Observatory { IsActive = true };
        fixture.Insert(observatory);
        fixture.Insert(new ObservatoryPublicationProfileVersion
        {
            ObservatoryId = observatory.Id, PublicSlug = "test", PublicDisplayName = "Test",
            ProfileVisibility = ObservatoryProfileVisibility.Public
        });
        var old = Decision(1, Key(100), Key(201), Key(301), Now.AddMinutes(-1));
        var latest = Decision(2, old.PublicId, Key(202), Key(302), sameTime ? old.OccurredAtUtc : Now);
        foreach (var decision in new[] { old, latest })
        {
            decision.AuthorityObservatoryId = observatory.Id;
            fixture.Insert(decision);
        }
        fixture.Insert(new CentralTransientEventRecord { Id = latest.CentralTransientEventId!.Value, EventCreatedUtc = Now });
        fixture.Insert(new CentralTransientEventVersionRecord
        {
            EventVersionId = latest.SourceEventVersionId!.Value, CentralTransientEventId = latest.CentralTransientEventId.Value,
            FirstObservedUtc = Now, LastObservedUtc = Now
        });
        var assessment = new CentralTransientAssessmentRecord
        {
            AssessmentId = Key(400), CentralTransientEventId = latest.CentralTransientEventId.Value,
            Authority = TransientAssessmentAuthority.Authoritative
        };
        fixture.Insert(assessment);
        fixture.Insert(new CentralTransientEventCurrent
        {
            CentralTransientEventId = latest.CentralTransientEventId.Value, LatestEventVersionId = latest.SourceEventVersionId.Value,
            ActiveAssessmentId = assessment.AssessmentId, ReviewState = CentralTransientReviewState.Reviewed,
            EffectiveConfidenceMillionths = 750000
        });
        // A newer withdrawal does not make a different authority's release disappear.
        var withdrawn = Decision(3, old.PublicId, Key(203), Key(303), Now.AddMinutes(1));
        withdrawn.AuthorityObservatoryId = observatory.Id;
        fixture.Insert(withdrawn);
        var successor = Decision(4, old.PublicId, Key(203), Key(303), Now.AddMinutes(2));
        successor.State = PublicationDecisionState.Withdrawn;
        successor.SupersedesDecisionId = withdrawn.Id;
        fixture.Insert(successor);
        var service = new PublicNetworkReadService(fixture.Db);
        PublicEventSummary? result;
        if (detail)
        {
            // Home curation delegates to this same private detail query.
            var method = typeof(PublicNetworkReadService).GetMethod("GetEventAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            result = await (Task<PublicEventSummary?>)method.Invoke(service, [old.PublicId, CancellationToken.None])!;
        }
        else
        {
            var page = await service.ListEventsAsync(10, null);
            Assert.HasCount(1, page.Items);
            result = page.Items[0];
        }
        Assert.IsNotNull(result);
        Assert.AreEqual(old.PublicId, result.PublicId);
        Assert.AreEqual(750000, result.ConfidenceMillionths);
    }

    private static PublicRecordPublicationDecision Decision(int id, Guid publicId, Guid eventId, Guid versionId,
        DateTimeOffset occurredAtUtc) => new()
        {
            Id = Key(id), PublicId = publicId, CentralTransientEventId = eventId, SourceEventVersionId = versionId,
            SubjectKind = PublicRecordSubjectKind.TransientEvent, State = PublicationDecisionState.Released,
            OccurredAtUtc = occurredAtUtc
        };
}
