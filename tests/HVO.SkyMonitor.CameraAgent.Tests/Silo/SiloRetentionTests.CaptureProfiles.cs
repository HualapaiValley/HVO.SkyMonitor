using HVO.SkyMonitor.CameraAgent.Common.Silo;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

public sealed partial class SiloRetentionTests
{
    [TestMethod]
    public async Task CaptureProfilesFollowAdmissionFactsAndRecheckHoldsBeforeExpiry()
    {
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var removal = new TestRemoval(silo.Local);
        var retention = Open(silo, removal);
        var day = Capture(1, new(null, "Day"));
        var window = Capture(2, new("morning", "Day"));
        var twilight = Capture(3, new(null, "Twilight"));
        var night = Capture(4, new(null, "Night"));
        var unknown = Capture(5, null);
        foreach (var capture in new[] { day, window, twilight, night, unknown })
            await silo.PublishAsync(capture).ConfigureAwait(false);
        var profiles = new CameraAgentSiloCaptureRetentionProfile[]
        {
            new("day", [new("raw", TimeSpan.Zero), new("preview", TimeSpan.Zero, "image/jpeg"),
                new("preview", TimeSpan.FromHours(4), "image/jpeg", "display")],
                SkyConditions: ["Day"]),
            new("morning", [new("raw", TimeSpan.FromDays(1))], WindowIds: ["morning"]),
            new("twilight", [new("raw", TimeSpan.FromHours(1))], SkyConditions: ["Twilight"]),
            new("night", [new("raw", TimeSpan.FromDays(7))], SkyConditions: ["Night"])
        };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => retention.SetPolicyAsync(new(false, [],
            [.. profiles, new("duplicate", [new("raw", TimeSpan.Zero)], SkyConditions: ["day"])]), token).AsTask())
            .ConfigureAwait(false);
        await retention.SetPolicyAsync(new(false, [], profiles), token).ConfigureAwait(false);
        await silo.RestartAsync().ConfigureAwait(false);
        retention = Open(silo, removal);
        silo.Clock.Advance(TimeSpan.FromHours(2));
        var now = silo.Clock.GetUtcNow();
        var plan = await retention.DryRunAsync(now, false, [], token).ConfigureAwait(false);
        Assert.AreEqual("expire-role", Decision(day, 0));
        Assert.AreEqual("within-policy", Decision(day, 1));
        Assert.AreEqual("within-policy", Decision(window, 0));
        Assert.AreEqual("expire-role", Decision(twilight, 0));
        Assert.AreEqual("within-policy", Decision(night, 0));
        Assert.AreEqual("within-policy", Decision(unknown, 0));

        var raw = day.Members[0];
        var locator = plan.Single(candidate => candidate.References.Any(item => item.Identity.Id == raw.ArtifactId)).Locator;
        var count = 0;
        ValueTask<IReadOnlyList<SiloRetentionHold>> ReadHolds(CancellationToken _) =>
            ValueTask.FromResult<IReadOnlyList<SiloRetentionHold>>(++count == 1 ? [] :
                [new(SiloHoldKind.StackContributor, raw.ArtifactId, raw.ArtifactRevision)]);
        Assert.AreEqual("no-eligible-versions", await retention.SweepAsync(true, ReadHolds, null, token, target => target == locator)
            .ConfigureAwait(false));
        Assert.AreEqual(0, removal.Deletes);

        var pending = day with
        {
            GroupKind = "pending-publication",
            GroupId = "pending-view",
            Members = [],
            Dependencies = [new(raw.ArtifactId, raw.ArtifactRevision, raw.Sha256, SiloDependencyKind.Reopen, true)],
            Provenance = new([])
        };
        Assert.IsTrue(await silo.Publisher.StageAsync(pending, token).ConfigureAwait(false));
        Assert.AreEqual("held-reference", (await PlanAsync(retention, locator, now, token).ConfigureAwait(false)).Decision);
        Assert.IsTrue(await silo.Publisher.AbandonIfUncommittedAsync(pending.GroupId, pending.RevisionId, "test-cancelled", token)
            .ConfigureAwait(false));

        using var committing = new SemaphoreSlim(0, 1);
        Assert.AreEqual("no-eligible-versions", await retention.SweepAsync(true, NoHolds, null, token,
            target => target == locator, storageLifecycle: committing).ConfigureAwait(false));
        Assert.AreEqual(0, removal.Deletes);
        Assert.AreEqual("deleted", await retention.SweepAsync(true, NoHolds, null, token, target => target == locator)
            .ConfigureAwait(false));

        await silo.RestartAsync().ConfigureAwait(false);
        var role = await silo.Index.ReadRoleAsync(new(SiloIdentityKind.Member, raw.ArtifactId, raw.ArtifactRevision), token)
            .ConfigureAwait(false);
        Assert.AreEqual("policy-expired", role?.Journaled);
        Assert.IsNotNull(role);
        Assert.AreEqual("evicted", role.Locators.Single().CopyState);

        SiloGroupDraft Capture(int number, SiloCaptureCondition? condition)
        {
            var draft = SiloDrafts.Capture(silo.Payloads, number);
            return draft with
            {
                GroupKind = "raw-capture",
                Members = [draft.Members[0], draft.Members[1] with { Variant = "display" }],
                Provenance = draft.Provenance with { CaptureCondition = condition }
            };
        }

        string Decision(SiloGroupDraft draft, int member) =>
            plan.Single(candidate => candidate.References.Any(reference => reference.Identity.Id == draft.Members[member].ArtifactId)).Decision;
    }
}
