using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.Tests.LogicHost.Processing;

[TestClass]
[TestCategory("Unit")]
public sealed class ExecutionEvidenceReceiverTests
{
    [TestMethod]
    public async Task GoldenFrozenPlansValidateWithoutRecomputingUnexportedPublicationHashes()
    {
        var revision = (await FixtureAsync("revision-local")).GraphRevision!;
        Assert.IsTrue(ExecutionEvidencePlanValidator.ValidateRevision(revision).IsValid);
        var execution = (await FixtureAsync("execution-live")).Execution!;
        Assert.IsTrue(ExecutionEvidencePlanValidator.ValidateExecution(execution, revision).IsValid);
        // Golden definition has one node; persisted frozen plan legitimately has two.
        Assert.AreNotEqual(revision.CanonicalDefinition.GetProperty("nodes").GetArrayLength(), execution.Nodes.Length);
    }

    [TestMethod]
    public async Task FrozenRootIdentityAndSchemaCannotBeSubstitutedEvenWhenEnvelopeIsResealed()
    {
        var revision = (await FixtureAsync("revision-local")).GraphRevision!;
        foreach (var key in new[] { "schemaVersion", "revisionId", "definitionIdentitySha256", "sharedPlanIdentitySha256", "localPlanIdentitySha256" })
        {
            var root = JsonNode.Parse(revision.FrozenPlan.GetRawText())!;
            root[key] = "substituted";
            var changed = revision with { FrozenPlan = JsonSerializer.SerializeToElement(root) };
            Assert.IsFalse(ExecutionEvidencePlanValidator.ValidateRevision(changed).IsValid, key);
        }
    }

    [TestMethod]
    public async Task ExecutionMustMatchFrozenNodeNamesRequiredFlagsAndPlanHashes()
    {
        var revision = (await FixtureAsync("revision-local")).GraphRevision!;
        var execution = (await FixtureAsync("execution-live")).Execution!;
        var node = execution.Nodes[0];
        foreach (var changed in new[] { node with { NodeId = "impostor" }, node with { Required = !node.Required }, node with { PlanSha256 = new string('A', 64) } })
        {
            Assert.IsFalse(ExecutionEvidencePlanValidator.ValidateExecution(execution with { Nodes = execution.Nodes.SetItem(0, changed) }, revision).IsValid);
        }
        Assert.IsFalse(ExecutionEvidencePlanValidator.ValidateExecution(execution with { Nodes = [node] }, revision).IsValid);
        Assert.IsFalse(ExecutionEvidencePlanValidator.ValidateExecution(execution with { SharedPlanIdentitySha256 = new string('B', 64) }, revision).IsValid);
    }

    [TestMethod]
    public async Task DuplicateOrInvalidFrozenNodesAreRejected()
    {
        var revision = (await FixtureAsync("revision-local")).GraphRevision!;
        var root = JsonNode.Parse(revision.FrozenPlan.GetRawText())!;
        root["nodes"]!.AsArray().Add(root["nodes"]![0]!.DeepClone());
        Assert.IsFalse(ExecutionEvidencePlanValidator.ValidateRevision(revision with { FrozenPlan = JsonSerializer.SerializeToElement(root) }).IsValid);
        root["nodes"] = new JsonArray();
        Assert.IsFalse(ExecutionEvidencePlanValidator.ValidateRevision(revision with { FrozenPlan = JsonSerializer.SerializeToElement(root) }).IsValid);
    }

    [TestMethod]
    public async Task HostFramingPreservesExactGoldenBytesAndRejectsTruncationEmptyUnitsAndExcessCardinality()
    {
        var unit = GraphExecutionEvidenceJson.Serialize(await FixtureAsync("revision-local"));
        var framed = unit.Append((byte)'\n').ToArray();
        var decoded = ExecutionEvidenceFraming.Decode(framed);
        CollectionAssert.AreEqual(unit, decoded.Single().ToArray());
        Assert.ThrowsExactly<ExecutionEvidenceInvalidException>(() => ExecutionEvidenceFraming.Decode(unit));
        Assert.ThrowsExactly<ExecutionEvidenceInvalidException>(() => ExecutionEvidenceFraming.Decode(ReadOnlyMemory<byte>.Empty));
        Assert.ThrowsExactly<ExecutionEvidenceInvalidException>(() => ExecutionEvidenceFraming.Decode(framed.Concat(new byte[] { 10 }).ToArray()));
        Assert.ThrowsExactly<ExecutionEvidenceInvalidException>(() => ExecutionEvidenceFraming.Decode(Enumerable.Repeat(framed, 65).SelectMany(x => x).ToArray()));
    }

    [TestMethod]
    public async Task StreamingLimitAppliesWhenContentLengthIsMissingOrFalse()
    {
        await using var input = new MemoryStream(new byte[17]);
        await Assert.ThrowsExactlyAsync<ExecutionEvidenceInvalidException>(() => ExecutionEvidenceFraming.ReadAsync(input, null, 16, CancellationToken.None));
        input.Position = 0;
        await Assert.ThrowsExactlyAsync<ExecutionEvidenceInvalidException>(() => ExecutionEvidenceFraming.ReadAsync(input, 1, 16, CancellationToken.None));
    }

    [TestMethod]
    public void ReceiverCapacityDoesNotQueueRequestsBehindActiveImports()
    {
        using var capacity = new ExecutionEvidenceCapacity(Options.Create(new ExecutionEvidenceOptions { MaximumConcurrentRequests = 1 }));
        Assert.IsTrue(capacity.TryEnter());
        Assert.IsFalse(capacity.TryEnter());
        capacity.Exit();
        Assert.IsTrue(capacity.TryEnter());
        capacity.Exit();
    }

    private static async Task<ExecutionEvidenceEnvelopeV1> FixtureAsync(string name)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"cameraagent-execution-evidence-{name}-v1.json"));
        return GraphExecutionEvidenceJson.ParseEnvelope(bytes).Value!;
    }
}
