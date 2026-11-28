using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

public sealed partial class SiloRetentionTests
{
    private const string CapturePolicyPath = "/api/internal/deployment/lifecycle/silo/retention/policy";
    private const string CapturePolicyJson = """
        {"expiryHeld":false,"roles":[],"captureProfiles":[{"name":"day","windowIds":["morning"],
          "skyConditions":["day"],"variants":[{"role":"raw","maximumAge":"00:00:00"}]}]}
        """;

    [TestMethod]
    public async Task CapturePolicyPutRejectsInvalidAndUnmappedFieldsWithoutChangingPolicy()
    {
        var token = TestContext.CancellationToken;
        var root = Directory.CreateTempSubdirectory("hvo-silo-policy-");
        try
        {
            using var services = SiloInfrastructureFixture.OpenStore(root.FullName);
            await services.GetRequiredService<RawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            var retention = services.GetRequiredService<ICameraAgentSiloRetention>();
            Assert.IsTrue(retention.Ready);
            var storage = services.GetRequiredService<SiloInstalledStorage>();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration["LifecycleControl:Token"] = "retention-test-token";
            builder.Services.AddSingleton(retention);
            var app = builder.Build();
            await using var lease = app.ConfigureAwait(false);
            app.MapGroup("/api/internal/deployment/lifecycle").MapSiloRetentionEndpoints(CameraAgentLifecycleEndpoints.IsAuthorized);
            await app.StartAsync(token).ConfigureAwait(false);
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            client.DefaultRequestHeaders.Add("X-HVO-Installation-Token", "retention-test-token");
            var uri = new Uri(CapturePolicyPath, UriKind.Relative);
            using var valid = await client.PutAsJsonAsync(uri, JsonNode.Parse(CapturePolicyJson), token).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.NoContent, valid.StatusCode);
            var saved = await storage.Index.ReadRetentionPolicyAsync(token).ConfigureAwait(false);
            Assert.AreEqual("day", Assert.ContainsSingle(saved.CaptureProfiles!).Name);
            Assert.AreEqual(TimeSpan.Zero, Assert.ContainsSingle(saved.CaptureProfiles![0].Variants).MaximumAge);

            foreach (var (name, change) in new (string, Action<JsonObject, JsonObject, JsonObject>)[]
            {
                ("negative age", (_, _, variant) => variant["maximumAge"] = "-00:00:01"),
                ("excessive age", (_, _, variant) => variant["maximumAge"] = "36501.00:00:00"),
                ("empty selectors", (_, profile, _) => { profile["windowIds"] = new JsonArray(); profile["skyConditions"] = new JsonArray(); }),
                ("invalid condition", (_, profile, _) => profile["skyConditions"] = new JsonArray("rain")),
                ("malformed name", (_, profile, _) => profile["name"] = "not a token"),
                ("malformed role", (_, _, variant) => variant["role"] = "not a token"),
                ("malformed media type", (_, _, variant) => variant["mediaType"] = "image/\nraw"),
                ("malformed variant", (_, _, variant) => variant["variant"] = "bad\nvariant"),
                ("duplicate window", (_, profile, _) => profile["windowIds"] = new JsonArray("morning", "morning")),
                ("duplicate variant", (_, profile, variant) =>
                {
                    variant["mediaType"] = "image/jpeg";
                    variant["variant"] = "display";
                    profile["variants"]!.AsArray().Add(variant.DeepClone());
                }),
                ("unknown policy field", (policy, _, _) => policy["expiry_held"] = true),
                ("unknown role rule field", (policy, _, _) => policy["roles"]!.AsArray().Add(JsonNode.Parse("""{"role":"raw","expire_under_pressure":true}"""))),
                ("unknown profile field", (_, profile, _) => profile["windowId"] = "morning"),
                ("unknown variant field", (_, _, variant) => variant["media_type"] = "image/jpeg"),
                ("omitted variant age", (_, _, variant) => variant.Remove("maximumAge"))
            })
            {
                var request = JsonNode.Parse(CapturePolicyJson)!.AsObject();
                request["expiryHeld"] = true;
                var profile = request["captureProfiles"]![0]!.AsObject();
                change(request, profile, profile["variants"]![0]!.AsObject());
                using var response = await client.PutAsJsonAsync(uri, request, token).ConfigureAwait(false);
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, name);
            }

            var after = await storage.Index.ReadRetentionPolicyAsync(token).ConfigureAwait(false);
            Assert.AreEqual(saved.ToRecord().Policy.GetRawText(), after.ToRecord().Policy.GetRawText());
        }
        finally { root.Delete(recursive: true); }
    }

    [TestMethod]
    public async Task CapturePolicyJournalReplayIgnoresFutureFieldsAtEveryLevel()
    {
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        using var json = JsonDocument.Parse("""
            {"expiryHeld":false,"futurePolicy":true,"roles":[{"role":"raw","futureRole":true}],
             "captureProfiles":[{"name":"day","skyConditions":["day"],"futureProfile":true,
               "variants":[{"role":"raw","maximumAge":"00:00:00","futureVariant":true}]}]}
            """);
        await silo.AppendAsync(new SiloPolicyRecord(SiloRetentionPolicy.PolicyId, json.RootElement)).ConfigureAwait(false);
        await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);

        var policy = await silo.Index.ReadRetentionPolicyAsync(token).ConfigureAwait(false);
        Assert.IsFalse(policy.ExpiryHeld);
        Assert.AreEqual("raw", Assert.ContainsSingle(policy.Roles).Role);
        var profile = Assert.ContainsSingle(policy.CaptureProfiles!);
        Assert.AreEqual("day", profile.Name);
        Assert.AreEqual(TimeSpan.Zero, Assert.ContainsSingle(profile.Variants).MaximumAge);
    }

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
        var profiles = new SiloCaptureRetentionProfile[]
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
