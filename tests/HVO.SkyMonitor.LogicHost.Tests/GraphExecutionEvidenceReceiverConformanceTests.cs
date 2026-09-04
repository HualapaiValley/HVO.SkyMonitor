using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Tests.LogicHost.Processing;

/// <summary>
/// Proves that the central host consumes the CameraAgent graph-execution evidence contract from the shared
/// <c>HVO.SkyMonitor.Processing</c> vocabulary alone: it reads the canonical golden fixtures, validates them,
/// produces the receiver facts, and rejects a forward-incompatible payload without referencing CameraAgent.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class GraphExecutionEvidenceReceiverConformanceTests
{
    private static readonly string[] EnvelopeFixtures =
    [
        "cameraagent-execution-evidence-revision-local-v1.json",
        "cameraagent-execution-evidence-revision-assigned-v1.json",
        "cameraagent-execution-evidence-execution-live-v1.json",
        "cameraagent-execution-evidence-execution-replay-v1.json",
        "cameraagent-execution-evidence-availability-v1.json",
        "cameraagent-execution-evidence-correction-v1.json"
    ];

    private static readonly ExecutionEvidenceFactKind[] AcceptedFactKinds =
    [
        ExecutionEvidenceFactKind.Received,
        ExecutionEvidenceFactKind.Validated,
        ExecutionEvidenceFactKind.Accepted,
        ExecutionEvidenceFactKind.Acknowledged
    ];

    [TestMethod]
    public async Task CentralHostAcceptsEveryCanonicalEnvelopeFixture()
    {
        var received = new Dictionary<long, string>();
        foreach (var fixture in EnvelopeFixtures)
        {
            var bytes = await ReadFixtureAsync(fixture).ConfigureAwait(false);
            var parsed = GraphExecutionEvidenceJson.ParseEnvelope(bytes);
            Assert.IsNotNull(parsed.Value, fixture);
            Assert.IsTrue(parsed.Validation.IsValid, fixture);

            var envelope = parsed.Value;
            Assert.AreEqual(
                envelope.PayloadSha256,
                GraphExecutionEvidenceJson.ComputeCanonicalPayloadSha256(envelope),
                fixture);
            CollectionAssert.AreEqual(bytes, GraphExecutionEvidenceJson.Serialize(envelope), fixture);

            var facts = GraphExecutionEvidenceJson.CreateReceiverFacts(
                envelope,
                received.TryGetValue(envelope.OriginSequence, out var stored) ? stored : null,
                DateTimeOffset.UnixEpoch.AddDays(1));
            CollectionAssert.AreEqual(
                AcceptedFactKinds, facts.Select(static fact => fact.Kind).ToArray(), fixture);
            received[envelope.OriginSequence] = envelope.PayloadSha256;
        }

        Assert.AreEqual(0, GraphExecutionEvidenceJson.DetectGaps(3, received.Keys, out var truncated).Length);
        Assert.IsFalse(truncated);

        var gaps = GraphExecutionEvidenceJson.DetectGaps(
            3, received.Keys.Where(static sequence => sequence != 5), out truncated);
        Assert.IsFalse(truncated);
        Assert.AreEqual(1, gaps.Length);
        Assert.AreEqual(5, gaps[0].FromSequence);
        Assert.AreEqual(5, gaps[0].ToSequence);
    }

    [TestMethod]
    public async Task CentralHostConsumesFeedbackResyncAndNegotiationFixtures()
    {
        var feedback = GraphExecutionEvidenceJson.ParseFeedback(
            await ReadFixtureAsync("cameraagent-execution-evidence-feedback-v1.json").ConfigureAwait(false));
        Assert.IsNotNull(feedback.Value);
        Assert.AreEqual(3, feedback.Value.Retention.AcknowledgedThroughSequence);
        Assert.IsTrue(feedback.Value.Facts.Any(static fact =>
            fact.Kind == ExecutionEvidenceFactKind.Rejected &&
            string.Equals(
                fact.ReasonCode,
                GraphExecutionEvidenceReasonCodes.SequenceConflict,
                StringComparison.Ordinal)));

        var resyncBytes = await ReadFixtureAsync("cameraagent-execution-evidence-resync-v1.json")
            .ConfigureAwait(false);
        var resync = GraphExecutionEvidenceJson.ParseResyncRequest(resyncBytes);
        Assert.IsNotNull(resync.Value);
        CollectionAssert.AreEqual(
            resyncBytes, GraphExecutionEvidenceJson.Serialize(resync.Value));
        var derived = GraphExecutionEvidenceJson.CreateResyncRequest(feedback.Value);
        Assert.IsNotNull(derived);
        CollectionAssert.AreEqual(resyncBytes, GraphExecutionEvidenceJson.Serialize(derived));

        var negotiation = GraphExecutionEvidenceJson.ParseNegotiationResponse(
            await ReadFixtureAsync("cameraagent-execution-evidence-negotiation-v1.json").ConfigureAwait(false));
        Assert.IsNotNull(negotiation.Value);
        Assert.AreEqual(ExecutionEvidenceNegotiationDisposition.Supported, negotiation.Value.Disposition);
        Assert.AreEqual(GraphExecutionEvidenceSchemaVersions.V1, negotiation.Value.SelectedSchemaVersion);
        Assert.AreEqual(ExecutionEvidenceLimitsV1.Current, negotiation.Value.Limits);
    }

    [TestMethod]
    public async Task CentralHostRejectsAnUnknownFutureVersionWholeAndRecordsNoFact()
    {
        var bytes = await ReadFixtureAsync("cameraagent-execution-evidence-unknown-future-v1.json")
            .ConfigureAwait(false);
        var parsed = GraphExecutionEvidenceJson.ParseEnvelope(bytes);
        Assert.IsNull(parsed.Value);
        Assert.AreEqual(GraphExecutionEvidenceReasonCodes.UnsupportedSchema, parsed.Validation.ReasonCode);
        Assert.AreEqual("schemaVersion", parsed.Validation.FieldPath);
    }

    [TestMethod]
    public void CentralHostConsumesTheContractWithoutReferencingCameraAgent()
    {
        Assert.AreEqual(
            typeof(ProcessingIdentity).Assembly,
            typeof(ExecutionEvidenceEnvelopeV1).Assembly,
            "The evidence contract must live in the shared processing assembly.");

        var references = typeof(global::HVO.SkyMonitor.LogicHost.Program).Assembly
            .GetReferencedAssemblies()
            .Select(static reference => reference.Name ?? string.Empty)
            .ToArray();
        Assert.IsFalse(
            references.Any(static name => name.StartsWith("HVO.SkyMonitor.CameraAgent", StringComparison.Ordinal)),
            string.Join(", ", references));

        Assert.IsFalse(typeof(ExecutionEvidenceEnvelopeV1).Assembly.GetReferencedAssemblies()
            .Select(static reference => reference.Name ?? string.Empty)
            .Any(static name =>
                name.StartsWith("HVO.SkyMonitor.CameraAgent", StringComparison.Ordinal) ||
                name.StartsWith("HVO.SkyMonitor.LogicHost", StringComparison.Ordinal)));
    }

    private static async Task<byte[]> ReadFixtureAsync(string fileName)
    {
        var fixture = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName)).ConfigureAwait(false);
        Assert.AreEqual((byte)'\n', fixture[^1], fileName);
        return fixture[..^1];
    }
}
