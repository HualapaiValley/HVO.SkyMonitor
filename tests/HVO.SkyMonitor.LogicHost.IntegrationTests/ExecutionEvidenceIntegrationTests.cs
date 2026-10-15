using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed partial class ExecutionEvidenceIntegrationTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 8, 31, 1, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task ExactGoldenUnitsIncludingAssignmentAndCorrectionCommitAndRemainReproducible()
    {
        var device = await SeedAsync(golden: true).ConfigureAwait(false);
        await SeedGoldenAssignmentAsync(device).ConfigureAwait(false);
        using var client = AssemblyHooks.Fixture.Factory.CreateClient();
        foreach (var name in new[] { "revision-local", "revision-assigned", "execution-live", "execution-replay", "availability", "correction" })
        {
            var envelope = await FixtureAsync(name).ConfigureAwait(false);
            using var response = await PostAsync(client, device, envelope).ConfigureAwait(false);
            var feedback = await FeedbackAsync(response).ConfigureAwait(false);
            Assert.AreEqual(4, feedback.Facts.Length, name);
            Assert.AreEqual(ExecutionEvidenceFactKind.Acknowledged, feedback.Facts[^1].Kind, name);
        }
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var units = await db.Set<ImportedExecutionEvidenceUnit>().Where(u => u.BindingId == device.BindingId).OrderBy(u => u.OriginSequence).ToListAsync().ConfigureAwait(false);
        Assert.AreEqual(6, units.Count);
        foreach (var unit in units)
        {
            var parsed = GraphExecutionEvidenceJson.ParseEnvelope(unit.CanonicalBytes);
            Assert.IsTrue(parsed.Validation.IsValid);
            Assert.AreEqual(unit.PayloadSha256, parsed.Value!.PayloadSha256);
        }
        Assert.AreEqual(6, (await db.Set<ImportedExecutionEvidenceOrigin>().SingleAsync(o => o.BindingId == device.BindingId).ConfigureAwait(false)).ContiguousThroughSequence);
        Assert.IsTrue(await db.Set<ImportedExecutionEvidenceArtifact>().AnyAsync(a => a.BindingId == device.BindingId).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task DuplicateRestartAndConflictReturnOnlyExactDurableFactsWithoutDisclosureBeforeAuthentication()
    {
        var device = await SeedAsync().ConfigureAwait(false);
        var unit = Reorigin(await FixtureAsync("revision-local").ConfigureAwait(false), device);
        using var client = AssemblyHooks.Fixture.Factory.CreateClient();
        using var first = await PostAsync(client, device, unit).ConfigureAwait(false);
        var accepted = await FeedbackAsync(first).ConfigureAwait(false);
        // A fresh request creates a fresh SQL context: no in-memory receiver state can manufacture this receipt.
        using var second = await PostAsync(client, device, unit).ConfigureAwait(false);
        var duplicate = await FeedbackAsync(second).ConfigureAwait(false);
        Assert.IsTrue(duplicate.Facts.All(f => f.Duplicate));
        CollectionAssert.AreEqual(accepted.Facts.Select(f => f.OccurredAtUtc).ToArray(), duplicate.Facts.Select(f => f.OccurredAtUtc).ToArray());
        var changed = GraphExecutionEvidenceJson.Seal(unit with { EvidenceId = Guid.NewGuid() });
        using var conflict = await PostAsync(client, device, changed).ConfigureAwait(false);
        var rejected = await FeedbackAsync(conflict).ConfigureAwait(false);
        Assert.AreEqual(1, rejected.Facts.Length);
        Assert.AreEqual(GraphExecutionEvidenceReasonCodes.SequenceConflict, rejected.Facts[0].ReasonCode);
        Assert.AreEqual(unit.PayloadSha256, rejected.Facts[0].StoredPayloadSha256);
        using var unauthenticated = await PostAsync(client, device with { DeviceKey = "wrong" }, changed).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.IsFalse((await unauthenticated.Content.ReadAsStringAsync().ConfigureAwait(false)).Contains(unit.PayloadSha256, StringComparison.Ordinal));
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.AreEqual(1, await db.Set<ImportedExecutionEvidenceUnit>().CountAsync(u => u.BindingId == device.BindingId).ConfigureAwait(false));
        Assert.AreEqual(1, await db.Set<ImportedExecutionEvidenceConflict>().CountAsync(c => c.BindingId == device.BindingId).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task MissingDependenciesReorderAndGapsConvergeWithoutTerminalRejection()
    {
        var device = await SeedAsync().ConfigureAwait(false);
        var execution = Reorigin(await FixtureAsync("execution-live").ConfigureAwait(false), device) with { OriginSequence = 2 };
        execution = GraphExecutionEvidenceJson.Seal(execution);
        using var client = AssemblyHooks.Fixture.Factory.CreateClient();
        using var early = await PostAsync(client, device, execution).ConfigureAwait(false);
        var pending = await FeedbackAsync(early).ConfigureAwait(false);
        Assert.AreEqual(0, pending.Facts.Length);
        Assert.AreEqual(1, pending.MissingRanges[0].FromSequence);
        Assert.AreEqual(2, pending.MissingRanges[0].ToSequence);
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var status = await scope.ServiceProvider.GetRequiredService<ExecutionEvidenceService>()
                .GetStatusAsync(device.BindingId, device.OwnerId, null).ConfigureAwait(false);
            Assert.AreEqual(1, status!.OriginsWithGaps);
            Assert.IsNotNull(status.OldestGapDetectedAtUtc);
        }
        using var revision = await PostAsync(client, device, Reorigin(await FixtureAsync("revision-local").ConfigureAwait(false), device)).ConfigureAwait(false);
        Assert.AreEqual(1, (await FeedbackAsync(revision).ConfigureAwait(false)).ContiguousThroughSequence);
        using var retry = await PostAsync(client, device, execution).ConfigureAwait(false);
        var converged = await FeedbackAsync(retry).ConfigureAwait(false);
        Assert.AreEqual(2, converged.ContiguousThroughSequence);
        Assert.AreEqual(0, converged.MissingRanges.Length);
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var status = await scope.ServiceProvider.GetRequiredService<ExecutionEvidenceService>()
                .GetStatusAsync(device.BindingId, device.OwnerId, null).ConfigureAwait(false);
            Assert.AreEqual(0, status!.OriginsWithGaps);
            Assert.IsNull(status.OldestGapDetectedAtUtc);
        }
        var correction = GraphExecutionEvidenceJson.Seal(execution with
        {
            OriginSequence = 4,
            EvidenceId = Guid.NewGuid(),
            Correction = new(ExecutionEvidenceCorrectionV1.CurrentSchemaVersion, Guid.NewGuid(), 3, "corrected")
        });
        using var missingPredecessor = await PostAsync(client, device, correction).ConfigureAwait(false);
        Assert.AreEqual(0, (await FeedbackAsync(missingPredecessor).ConfigureAwait(false)).Facts.Length);
        var predecessor = GraphExecutionEvidenceJson.Seal(execution with { OriginSequence = 3, EvidenceId = correction.Correction!.CorrectsEvidenceId });
        using var supplied = await PostAsync(client, device, predecessor).ConfigureAwait(false);
        Assert.AreEqual(4, (await FeedbackAsync(supplied).ConfigureAwait(false)).Facts.Length);
        using var corrected = await PostAsync(client, device, correction).ConfigureAwait(false);
        Assert.AreEqual(4, (await FeedbackAsync(corrected).ConfigureAwait(false)).ContiguousThroughSequence);

        // A stricter receiver must bound the missing ranges themselves: the shared producer helper's
        // MaximumUnits is 256 and its replay path does not apply the negotiated normal-batch clamp.
        foreach (var (sequence, expectedTruncated) in new[] { (65L, false), (66L, true), (long.MaxValue, true) })
        {
            var wideDevice = await SeedAsync().ConfigureAwait(false);
            var highRevision = GraphExecutionEvidenceJson.Seal(
                Reorigin(await FixtureAsync("revision-local").ConfigureAwait(false), wideDevice) with { OriginSequence = sequence });
            using var high = await PostAsync(client, wideDevice, highRevision).ConfigureAwait(false);
            var gap = await FeedbackAsync(high).ConfigureAwait(false);
            Assert.AreEqual(0L, gap.ContiguousThroughSequence);
            Assert.AreEqual(1, gap.MissingRanges.Length);
            Assert.AreEqual(1L, gap.MissingRanges[0].FromSequence);
            Assert.AreEqual(64L, gap.MissingRanges[0].ToSequence);
            Assert.AreEqual(expectedTruncated, gap.MissingRangesTruncated);
            var resync = GraphExecutionEvidenceJson.CreateResyncRequest(gap);
            Assert.IsNotNull(resync);
            Assert.AreEqual(64L, resync.Ranges.Sum(range => range.ToSequence - range.FromSequence + 1));
        }

        var sparseDevice = await SeedAsync().ConfigureAwait(false);
        var sparseRevision = Reorigin(await FixtureAsync("revision-local").ConfigureAwait(false), sparseDevice);
        foreach (var sequence in new[] { 1L, 34L, 67L, 100L })
        {
            var unit = GraphExecutionEvidenceJson.Seal(sparseRevision with { OriginSequence = sequence, EvidenceId = Guid.NewGuid() });
            using var response = await PostAsync(client, sparseDevice, unit).ConfigureAwait(false);
            var feedback = await FeedbackAsync(response).ConfigureAwait(false);
            if (sequence != 100) { continue; }
            Assert.AreEqual(1L, feedback.ContiguousThroughSequence);
            Assert.AreEqual(2, feedback.MissingRanges.Length);
            Assert.AreEqual(2L, feedback.MissingRanges[0].FromSequence);
            Assert.AreEqual(33L, feedback.MissingRanges[0].ToSequence);
            Assert.AreEqual(35L, feedback.MissingRanges[1].FromSequence);
            Assert.AreEqual(66L, feedback.MissingRanges[1].ToSequence);
            Assert.AreEqual(64L, feedback.MissingRanges.Sum(range => range.ToSequence - range.FromSequence + 1));
            Assert.IsTrue(feedback.MissingRangesTruncated, "A later gap must stay visible as truncation when the first two consume 64 units.");
        }
    }

    [TestMethod]
    public async Task BindingIsExplicitAndWrongOptionalIdentityFailsUniformlyWhileOldBootsRemainAllowed()
    {
        var device = await SeedAsync(createBinding: false).ConfigureAwait(false);
        using var client = AssemblyHooks.Fixture.Factory.CreateClient();
        var envelope = Reorigin(await FixtureAsync("revision-local").ConfigureAwait(false), device);
        using var absent = await PostAsync(client, device, envelope).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Forbidden, absent.StatusCode);
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ExecutionEvidenceService>();
            var binding = await service.CreateBindingAsync(new(device.ObservatoryId, device.RegistrationId, device.InstallationId,
                device.Origin.OriginInstallationId, device.Origin.AgentInstanceId), device.OwnerId, null).ConfigureAwait(false);
            Assert.IsNotNull(binding);
            device = device with { BindingId = binding.Id };
        }
        var currentOrigin = GraphExecutionEvidenceJson.BindIdentity(device.Origin with { BootSessionId = Guid.NewGuid(), SoftwareVersion = "next-version" });
        using var negotiate = new HttpRequestMessage(HttpMethod.Post, "/api/device/execution-evidence/negotiate")
        {
            Content = new ByteArrayContent(GraphExecutionEvidenceJson.Serialize(new ExecutionEvidenceNegotiationRequestV1(
                ExecutionEvidenceNegotiationRequestV1.CurrentSchemaVersion, currentOrigin, GraphExecutionEvidenceSchemaVersions.Supported)))
        };
        Headers(negotiate, device);
        negotiate.Content.Headers.ContentType = new("application/json");
        using var negotiated = await client.SendAsync(negotiate).ConfigureAwait(false);
        negotiated.EnsureSuccessStatusCode();
        Assert.AreEqual(64, GraphExecutionEvidenceJson.ParseNegotiationResponse(await negotiated.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).Value!.Limits.MaximumResyncUnits);
        using var oldBoot = await PostAsync(client, device, envelope).ConfigureAwait(false);
        Assert.AreEqual(4, (await FeedbackAsync(oldBoot).ConfigureAwait(false)).Facts.Length);
        using var newBoot = await PostAsync(client, device, GraphExecutionEvidenceJson.Seal(envelope with { Origin = currentOrigin })).ConfigureAwait(false);
        Assert.AreEqual(4, (await FeedbackAsync(newBoot).ConfigureAwait(false)).Facts.Length);
        foreach (var origin in new[] { device.Origin with { ObservatoryId = Guid.NewGuid() }, device.Origin with { LogicalCameraInstallationId = Guid.NewGuid() },
            device.Origin with { InstallationPublicId = Guid.NewGuid() }, device.Origin with { OriginInstallationId = Guid.NewGuid() }, device.Origin with { AgentInstanceId = Guid.NewGuid() } })
        {
            using var wrong = await PostAsync(client, device, GraphExecutionEvidenceJson.Seal(envelope with { Origin = GraphExecutionEvidenceJson.BindIdentity(origin) })).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.Forbidden, wrong.StatusCode);
        }
        await AssertAssignmentProposalNondisclosureAsync(client, device).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task FramingMalformedHashAndRequestLimitsRejectWithoutDurableAcknowledgement()
    {
        var device = await SeedAsync().ConfigureAwait(false);
        var envelope = Reorigin(await FixtureAsync("revision-local").ConfigureAwait(false), device);
        var bytes = GraphExecutionEvidenceJson.Serialize(envelope);
        using var client = AssemblyHooks.Fixture.Factory.CreateClient();
        foreach (var malformed in new[] { bytes, bytes.Concat(new byte[] { 10, 10 }).ToArray(),
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(envelope.PayloadSha256, new string('A', 64), StringComparison.Ordinal) + "\n"),
            Encoding.UTF8.GetBytes(" " + Encoding.UTF8.GetString(bytes) + "\n"),
            Enumerable.Repeat(bytes.Append((byte)10).ToArray(), 65).SelectMany(x => x).ToArray() })
        {
            using var result = await PostBytesAsync(client, device, envelope.Origin.IdentitySha256, malformed).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.BadRequest, result.StatusCode);
        }
        using var huge = await PostBytesAsync(client, device, envelope.Origin.IdentitySha256, new byte[ExecutionEvidenceOptions.MaximumRequestBytes + 1]).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        Assert.IsFalse(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Set<ImportedExecutionEvidenceUnit>().AnyAsync(u => u.BindingId == device.BindingId).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ReusedEvidenceIdIsDurablyRejectedWithoutViolatingAcceptedIdentityIndex()
    {
        var device = await SeedAsync().ConfigureAwait(false);
        var envelope = Reorigin(await FixtureAsync("revision-local").ConfigureAwait(false), device);
        using var client = AssemblyHooks.Fixture.Factory.CreateClient();
        using var accepted = await PostAsync(client, device, envelope).ConfigureAwait(false);
        _ = await FeedbackAsync(accepted).ConfigureAwait(false);
        using var reused = await PostAsync(client, device, GraphExecutionEvidenceJson.Seal(envelope with { OriginSequence = 2 })).ConfigureAwait(false);
        var facts = (await FeedbackAsync(reused).ConfigureAwait(false)).Facts;
        Assert.AreEqual(2, facts.Length);
        Assert.AreEqual(GraphExecutionEvidenceReasonCodes.InvalidIdentity, facts[1].ReasonCode);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        Assert.AreEqual(2, await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Set<ImportedExecutionEvidenceUnit>().CountAsync(u => u.BindingId == device.BindingId).ConfigureAwait(false));
    }

    private static async Task<ExecutionEvidenceEnvelopeV1> FixtureAsync(string name)
        => GraphExecutionEvidenceJson.ParseEnvelope(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", $"cameraagent-execution-evidence-{name}-v1.json")).ConfigureAwait(false)).Value!;

    private static ExecutionEvidenceEnvelopeV1 Reorigin(ExecutionEvidenceEnvelopeV1 unit, Device device)
        => GraphExecutionEvidenceJson.Seal(unit with { Origin = device.Origin });

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, Device device, ExecutionEvidenceEnvelopeV1 unit)
        => PostBytesAsync(client, device, unit.Origin.IdentitySha256, GraphExecutionEvidenceJson.Serialize(unit).Append((byte)10).ToArray());

    private static async Task<HttpResponseMessage> PostBytesAsync(HttpClient client, Device device, string origin, byte[] bytes)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/device/execution-evidence") { Content = new ByteArrayContent(bytes) };
        Headers(request, device);
        request.Headers.Add("X-HVO-Evidence-Origin", origin);
        request.Content.Headers.ContentType = new(ExecutionEvidenceFraming.MediaType);
        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private static void Headers(HttpRequestMessage request, Device device)
    {
        request.Headers.Add("X-HVO-Device-Id", device.DeviceId);
        request.Headers.Add("X-HVO-Device-Key", device.DeviceKey);
    }

    private static async Task<ExecutionEvidenceFeedbackV1> FeedbackAsync(HttpResponseMessage response)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, Encoding.UTF8.GetString(bytes));
        var parsed = GraphExecutionEvidenceJson.ParseFeedback(bytes);
        Assert.IsTrue(parsed.Validation.IsValid, Encoding.UTF8.GetString(bytes));
        return parsed.Value!;
    }

    private sealed record Device(string DeviceId, string DeviceKey, Guid RegistrationId, Guid ObservatoryId,
        Guid InstallationId, Guid DevicePublicId, Guid BindingId, string OwnerId, ExecutionEvidenceOriginV1 Origin);
}
