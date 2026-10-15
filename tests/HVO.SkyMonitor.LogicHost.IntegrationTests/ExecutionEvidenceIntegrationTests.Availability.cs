using System.Collections.Immutable;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

public sealed partial class ExecutionEvidenceIntegrationTests
{
    [TestMethod]
    public async Task AvailabilityWaitsForItsSameBindingExecutionWithoutRequiringArtifactInExecutionOutputs()
    {
        var device = await SeedAsync().ConfigureAwait(false);
        using var client = AssemblyHooks.Fixture.Factory.CreateClient();
        var report = GraphExecutionEvidenceJson.Seal(Reorigin(await FixtureAsync("availability").ConfigureAwait(false), device) with { OriginSequence = 3 });
        using var early = await PostAsync(client, device, report).ConfigureAwait(false);
        Assert.AreEqual(0, (await FeedbackAsync(early).ConfigureAwait(false)).Facts.Length);
        using var revision = await PostAsync(client, device, Reorigin(await FixtureAsync("revision-local").ConfigureAwait(false), device)).ConfigureAwait(false);
        _ = await FeedbackAsync(revision).ConfigureAwait(false);
        var execution = GraphExecutionEvidenceJson.Seal(Reorigin(await FixtureAsync("execution-live").ConfigureAwait(false), device) with { OriginSequence = 2 });
        using var executed = await PostAsync(client, device, execution).ConfigureAwait(false);
        _ = await FeedbackAsync(executed).ConfigureAwait(false);
        using var retried = await PostAsync(client, device, report).ConfigureAwait(false);
        var accepted = await FeedbackAsync(retried).ConfigureAwait(false);
        Assert.AreEqual(4, accepted.Facts.Length);
        Assert.AreEqual(3, accepted.ContiguousThroughSequence);
        var outputIds = execution.Execution!.Nodes.SelectMany(n => n.Outputs).Select(o => o.Artifact.ArtifactId).ToHashSet();
        Assert.IsTrue(report.Availability!.Observations.Any(o => !outputIds.Contains(o.Artifact.ArtifactId)));
    }

    [TestMethod]
    public async Task AnotherBindingsExecutionCannotAuthorizeAvailabilityAssociation()
    {
        var first = await SeedAsync().ConfigureAwait(false);
        var second = await SeedAsync().ConfigureAwait(false);
        using var client = AssemblyHooks.Fixture.Factory.CreateClient();
        using var revision = await PostAsync(client, first, Reorigin(await FixtureAsync("revision-local").ConfigureAwait(false), first)).ConfigureAwait(false);
        _ = await FeedbackAsync(revision).ConfigureAwait(false);
        using var execution = await PostAsync(client, first, GraphExecutionEvidenceJson.Seal(Reorigin(await FixtureAsync("execution-live").ConfigureAwait(false), first) with { OriginSequence = 2 })).ConfigureAwait(false);
        _ = await FeedbackAsync(execution).ConfigureAwait(false);
        var report = GraphExecutionEvidenceJson.Seal(Reorigin(await FixtureAsync("availability").ConfigureAwait(false), second) with { OriginSequence = 1 });
        using var foreign = await PostAsync(client, second, report).ConfigureAwait(false);
        var feedback = await FeedbackAsync(foreign).ConfigureAwait(false);
        Assert.AreEqual(0, feedback.Facts.Length);
        Assert.AreEqual(0, feedback.ContiguousThroughSequence);
        Assert.AreEqual(1, feedback.MissingRanges.Single().FromSequence);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        Assert.IsFalse(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Set<ImportedExecutionEvidenceUnit>()
            .AnyAsync(u => u.BindingId == second.BindingId).ConfigureAwait(false));
    }
}
