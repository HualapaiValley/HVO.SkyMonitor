using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Evidence;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Evidence;

/// <summary>
/// Ties the shared golden revision fixture's frozen plan to the durable CameraAgent node seed it mirrors.
/// </summary>
/// <remarks>
/// The golden fixture in the shared processing test project is a hand-written literal, because the test-ownership
/// boundary forbids that project from running the CameraAgent coordinator. #536 recorded that gap: a new member on
/// <c>ProcessingExecutionNodeSeed</c> would silently desynchronize the fixture. This test closes it from the side
/// that does own the durable type, so adding, renaming, or removing a seed member fails here rather than shipping a
/// golden that no longer describes what the exporter actually sends.
/// </remarks>
[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class ExecutionEvidenceFrozenPlanTieTests
{
    private static readonly string[] FrozenPlanMembers =
    [
        "schemaVersion",
        "revisionId",
        "definitionIdentitySha256",
        "sharedPlanIdentitySha256",
        "localPlanIdentitySha256",
        "nodes"
    ];

    [TestMethod]
    public void TheGoldenFrozenPlanNodeCarriesExactlyTheDurableNodeSeedMembers()
    {
        var expected = typeof(ProcessingExecutionNodeSeed)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(static property => property.Name != "EqualityContract")
            .Select(static property => JsonNamingPolicy.CamelCase.ConvertName(property.Name))
            .Order(StringComparer.Ordinal)
            .ToArray();

        using var document = JsonDocument.Parse(File.ReadAllBytes(FixturePath()));
        var node = document.RootElement
            .GetProperty("graphRevision")
            .GetProperty("frozenPlan")
            .GetProperty("nodes")[0];
        var actual = node.EnumerateObject()
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(
            expected,
            actual,
            "The golden frozen-plan node must mirror ProcessingExecutionNodeSeed exactly. Regenerate the fixture " +
            "and update its pinned SHA-256 whenever the durable seed changes.");
    }

    [TestMethod]
    public void TheGoldenFrozenPlanDocumentCarriesExactlyTheMembersTheCoordinatorPersists()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(FixturePath()));
        var frozenPlan = document.RootElement.GetProperty("graphRevision").GetProperty("frozenPlan");
        CollectionAssert.AreEqual(
            FrozenPlanMembers.Order(StringComparer.Ordinal).ToArray(),
            frozenPlan.EnumerateObject().Select(static property => property.Name)
                .Order(StringComparer.Ordinal).ToArray());
        Assert.AreEqual(
            "cameraagent-processing-frozen-plan-v1",
            frozenPlan.GetProperty("schemaVersion").GetString());
    }

    [TestMethod]
    public void ARealProjectedRevisionCarriesTheSameFrozenPlanMemberSetAsTheGolden()
    {
        var revision = ProcessingGraphEvidenceProjection
            .CreateRevisionEvidence(ExecutionEvidenceTestFactory.CreateSnapshot(), assignment: null);
        Assert.AreEqual(ProcessingGraphEvidenceProjectionOutcome.Projected, revision.Outcome);
        var projected = revision.Value!.FrozenPlan.EnumerateObject()
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(FrozenPlanMembers.Order(StringComparer.Ordinal).ToArray(), projected);
    }

    private static string FixturePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
            !Directory.Exists(Path.Combine(directory.FullName, "tests", "fixtures", "processing")))
        {
            directory = directory.Parent;
        }
        Assert.IsNotNull(directory, "The repository fixture directory was not found.");
        return Path.Combine(
            directory.FullName,
            "tests",
            "fixtures",
            "processing",
            "cameraagent-execution-evidence-revision-local-v1.json");
    }
}

/// <summary>Framing invariants for the bounded evidence batch a submission carries.</summary>
[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class ExecutionEvidenceBatchCodecTests
{
    [TestMethod]
    public void CanonicalEnvelopesSurviveFramingByteForByte()
    {
        var origin = ExecutionEvidenceTestFactory.CreateOrigin();
        var envelopes = new[]
        {
            ExecutionEvidenceTestFactory.SealExecution(origin, 1, 1).Payload.ToArray(),
            ExecutionEvidenceTestFactory.SealExecution(origin, 2, 2).Payload.ToArray()
        };

        var decoded = ExecutionEvidenceBatchCodec.Decode(
            ExecutionEvidenceBatchCodec.Encode(envelopes), GraphExecutionEvidenceLimits.MaximumResyncUnits);

        Assert.AreEqual(2, decoded.Count);
        for (var index = 0; index < envelopes.Length; index++)
        {
            CollectionAssert.AreEqual(envelopes[index], decoded[index].ToArray());
            var parsed = GraphExecutionEvidenceJson.ParseEnvelope(decoded[index]);
            Assert.IsNotNull(parsed.Value, parsed.Validation.ReasonCode);
        }
    }

    [TestMethod]
    public void AnEmptyBatchIsEmptyAndATruncatedBatchIsRefused()
    {
        Assert.IsEmpty(ExecutionEvidenceBatchCodec.Decode([], 4));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ExecutionEvidenceBatchCodec.Decode("{}"u8, 4));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ExecutionEvidenceBatchCodec.Decode("{}\n\n"u8, 4));
    }

    [TestMethod]
    public void ABatchAboveTheNegotiatedUnitLimitIsRefused()
    {
        var payload = ExecutionEvidenceBatchCodec.Encode([[(byte)'{', (byte)'}'], [(byte)'{', (byte)'}']]);
        Assert.ThrowsExactly<InvalidDataException>(() => ExecutionEvidenceBatchCodec.Decode(payload, 1));
    }

    [TestMethod]
    public void AnEnvelopeCarryingARawSeparatorIsRefusedRatherThanCorrupted()
        => Assert.ThrowsExactly<ArgumentException>(() =>
            ExecutionEvidenceBatchCodec.Encode([[(byte)'{', (byte)'\n', (byte)'}']]));
}
