using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Endpoints;

/// <summary>
/// The run page decides whether the manifest can be downloaded without building it. These tests hold that decision
/// against the download's own, and hold the size bound that lets the page skip the encoding against the encoder.
/// </summary>
public sealed partial class CameraAgentProcessingGraphOperationsEndpointsTests
{
    // Every character takes the longest JSON escape, including a surrogate pair.
    private const string WorstText = "\"<>&'+`\u00e9\u2028\ud83d\ude00";

    private static readonly DateTimeOffset WidestInstant =
        new DateTimeOffset(2026, 12, 31, 23, 59, 59, 999, TimeSpan.FromHours(14)).AddTicks(9999);

    private static string? NeverMeasured(ProcessingGraphExecutionDetail detail)
        => throw new AssertFailedException("The payload was encoded to decide availability.");

    [TestMethod]
    public void ManifestAvailabilityIsTheDownloadsVerdictWithoutBuildingThePayload()
    {
        var detail = ManifestDetail();
        var node = detail.Nodes[0];
        var cases = new (string Name, ProcessingGraphExecutionDetail Detail, string? Reason)[]
        {
            ("retained", detail, null),
            ("retained with its frozen node count", detail with { FrozenNodeCount = 1 }, null),
            ("retained empty graph", detail with { Nodes = [], FrozenNodeCount = 0 }, null),
            ("one node missing", detail with { FrozenNodeCount = 2 }, CameraAgentRunManifest.MissingReason),
            ("every node missing", detail with { Nodes = [], FrozenNodeCount = 1 }, CameraAgentRunManifest.MissingReason),
            ("too many nodes", detail with { Nodes = Enumerable.Repeat(node, 65).ToArray() }, ProcessingRunManifestBounds.UnavailableReason),
            ("expired input binding", detail with { Nodes = [node with { ArtifactBindingsRetained = false }] },
                CameraAgentRunManifest.ArtifactEvidenceUnavailableReason),
            ("expired output", detail with { Nodes = [node with { Outputs = [] }] },
                CameraAgentRunManifest.ArtifactEvidenceUnavailableReason),
            ("expired lineage source", detail with { Nodes = [node with { Inputs = [] }] },
                CameraAgentRunManifest.ArtifactEvidenceUnavailableReason),
            ("unrecorded input", detail with { Nodes = [node with { InputEvidenceIncomplete = true }] },
                CameraAgentRunManifest.UnrecordedInputReason),
            ("unrecorded output", detail with { Nodes = [node with { CommittedOutputCount = null }] },
                CameraAgentRunManifest.UnrecordedOutputReason),
            ("unsafe identifier", detail with { Nodes = [node with { NodeId = "/private" }] },
                CameraAgentRunManifest.IdentifierUnavailableReason),
            ("incomplete checksum", detail with { Nodes = [node with { Inputs = [node.Inputs[0] with { PayloadSha256 = null }] }] },
                CameraAgentRunManifest.IncompleteReason),
            ("incomplete plan identity", detail with { Execution = detail.Execution with { LocalPlanIdentitySha256 = "short" } },
                CameraAgentRunManifest.IncompleteReason)
        };

        foreach (var (name, candidate, reason) in cases)
        {
            var download = CameraAgentRunManifest.Create(candidate);
            Assert.AreEqual(reason, download.Reason, name);
            Assert.AreEqual(reason is null, download.Payload is not null, name);
            // The page reaches the same verdict, and an encoding would fail the test.
            Assert.AreEqual(reason, CameraAgentRunManifest.UnavailableReason(candidate, NeverMeasured), name);
            Assert.AreEqual(reason, CameraAgentRunManifest.UnavailableReason(candidate), name);
        }
    }

    [TestMethod]
    public void ManifestIsEncodedToMeasureItOnlyWhenItCouldExceedTheByteLimit()
    {
        var detail = ManifestDetail();
        var node = detail.Nodes[0];
        var measured = 0;
        string? Measure(ProcessingGraphExecutionDetail value)
        {
            measured++;
            return CameraAgentRunManifest.Create(value).Reason;
        }

        // An ordinary run is far below the limit by its upper bound alone.
        Assert.IsTrue(CameraAgentRunManifest.MaximumEncodedBytes(detail) < ProcessingRunManifestBounds.MaximumBytes / 100);
        Assert.IsNull(CameraAgentRunManifest.UnavailableReason(detail, Measure));
        Assert.AreEqual(0, measured);

        // Half a node's row limit is still decided from the bound alone.
        ProcessingGraphExecutionDetail WithInputs(int count) => detail with
        {
            Nodes = [node with { Inputs = Enumerable.Range(0, count).Select(index => node.Inputs[0] with { Ordinal = index }).ToArray() }]
        };
        Assert.IsNull(CameraAgentRunManifest.UnavailableReason(WithInputs(ProcessingRunManifestBounds.MaximumRows / 2), Measure));
        Assert.AreEqual(0, measured);

        // A node at its row limit puts the upper bound over the byte limit while the payload still fits: it is
        // measured, and offered.
        var fits = WithInputs(ProcessingRunManifestBounds.MaximumRows);
        Assert.IsTrue(CameraAgentRunManifest.MaximumEncodedBytes(fits) > ProcessingRunManifestBounds.MaximumBytes);
        Assert.IsTrue(CameraAgentRunManifest.Encode(fits).Length <= ProcessingRunManifestBounds.MaximumBytes);
        Assert.IsNull(CameraAgentRunManifest.UnavailableReason(fits, Measure));
        Assert.AreEqual(1, measured);
        Assert.IsNull(CameraAgentRunManifest.UnavailableReason(fits));
        Assert.IsNotNull(CameraAgentRunManifest.Create(fits).Payload);

        // A payload over the limit is refused by the page exactly as the download refuses it.
        var sources = Enumerable.Range(0, 512).Select(static _ => Guid.NewGuid()).ToArray();
        var oversized = detail with
        {
            Nodes =
            [
                node with
                {
                    Inputs = sources.Select((id, index) => node.Inputs[0] with { ArtifactId = id, Ordinal = index }).ToArray(),
                    Outputs = Enumerable.Range(0, 120).Select(index => node.Outputs[0] with { Ordinal = index, SourceArtifactIds = sources }).ToArray(),
                    CommittedOutputCount = 120
                }
            ]
        };
        Assert.IsTrue(CameraAgentRunManifest.Encode(oversized).Length > ProcessingRunManifestBounds.MaximumBytes);
        Assert.AreEqual(ProcessingRunManifestBounds.UnavailableReason, CameraAgentRunManifest.UnavailableReason(oversized, Measure));
        Assert.AreEqual(2, measured);
        Assert.AreEqual(ProcessingRunManifestBounds.UnavailableReason, CameraAgentRunManifest.UnavailableReason(oversized));
        Assert.AreEqual(ProcessingRunManifestBounds.UnavailableReason, CameraAgentRunManifest.Create(oversized).Reason);
    }

    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(1, 1)]
    [DataRow(3, 7)]
    public void EncodedSizeBoundNeverUnderstatesAManifestOfWorstCaseText(int nodeCount, int rowsPerKind)
    {
        // Every free string is made of characters that take the longest escape, and every scalar is at its widest.
        static string Worst(int length) => string.Concat(Enumerable.Repeat(WorstText, length / WorstText.Length + 1))[..length];
        var template = ManifestDetail();
        var execution = template.Execution with
        {
            GraphRevisionId = Worst(64),
            GraphDefinitionIdentitySha256 = Worst(64),
            SharedPlanIdentitySha256 = Worst(64),
            LocalPlanIdentitySha256 = Worst(64),
            AcceptedUtc = WidestInstant,
            StartedUtc = WidestInstant,
            CompletedUtc = WidestInstant,
            AttemptCount = int.MinValue
        };
        var sources = Enumerable.Range(0, 512).Select(static _ => Guid.NewGuid()).ToArray();
        var nodes = Enumerable.Range(0, nodeCount).Select(_ => new ProcessingGraphExecutionNodeState(
            Worst(128), false, Worst(64), Worst(32), null, int.MinValue, WidestInstant, WidestInstant,
            Enumerable.Range(0, rowsPerKind).SelectMany(_ => Enum.GetValues<ProcessingGraphExecutionInputKind>()).Select(kind =>
                new ProcessingGraphExecutionInputState(int.MinValue, int.MinValue, kind, Guid.NewGuid(), Guid.NewGuid(), Worst(64), Worst(64), Worst(64))).ToArray(),
            Enumerable.Range(0, rowsPerKind).Select(_ => new ProcessingGraphNodeAttemptState(int.MinValue, "lease", WidestInstant, WidestInstant, Worst(32),
                Longest<ProcessingOutcomeStatus>(), null, TimeSpan.MinValue, Longest<ProcessingNodeExecutionRoute>())).ToArray(),
            Enumerable.Range(0, rowsPerKind).Select(_ => new ProcessingGraphExecutionOutputState(int.MinValue, Worst(64), Guid.NewGuid(),
                Longest<FrameArtifactRole>(), "variant", "Available", null, false)
            { PayloadSha256 = Worst(64), SourceArtifactIds = sources }).ToArray())
        {
            NormalizationPlanIdentitySha256 = Worst(64),
            CalibrationInputs = Enumerable.Range(0, rowsPerKind).Select(_ =>
                new ProcessingGraphExecutionCalibrationInputState(Guid.NewGuid(), Worst(64), Worst(32))).ToArray()
        }).ToArray();
        var detail = new ProcessingGraphExecutionDetail(execution, nodes);

        var encoded = CameraAgentRunManifest.Encode(detail).Length;
        var bound = CameraAgentRunManifest.MaximumEncodedBytes(detail);

        Assert.IsTrue(bound >= encoded, $"The bound {bound} understates the encoded manifest of {encoded} bytes.");
    }

    [TestMethod]
    public void EncodedSizeAllowancesCoverEachMemberAtItsWidest()
    {
        // Free strings are empty here, so each difference is what one member costs beyond its free strings.
        static long Size(ProcessingGraphExecutionDetail detail) => CameraAgentRunManifest.Encode(detail).Length;
        var template = ManifestDetail();
        var header = new ProcessingGraphExecutionDetail(template.Execution with
        {
            GraphRevisionId = string.Empty,
            GraphDefinitionIdentitySha256 = string.Empty,
            SharedPlanIdentitySha256 = string.Empty,
            LocalPlanIdentitySha256 = string.Empty,
            AcceptedUtc = WidestInstant,
            StartedUtc = WidestInstant,
            CompletedUtc = WidestInstant,
            AttemptCount = int.MinValue
        }, []);
        var node = new ProcessingGraphExecutionNodeState(
            string.Empty, false, string.Empty, string.Empty, null, int.MinValue, WidestInstant, WidestInstant, [], [], [])
        { NormalizationPlanIdentitySha256 = string.Empty };
        ProcessingGraphExecutionDetail With(ProcessingGraphExecutionNodeState value) => header with { Nodes = [value] };
        var empty = Size(With(node));

        Assert.IsTrue(Size(header) <= CameraAgentRunManifest.EncodedHeaderBytes, $"header {Size(header)}");
        Assert.IsTrue(empty - Size(header) <= CameraAgentRunManifest.EncodedNodeBytes, $"node {empty - Size(header)}");
        foreach (var kind in Enum.GetValues<ProcessingGraphExecutionInputKind>())
        {
            var input = Size(With(node with
            {
                Inputs = [new(int.MinValue, int.MinValue, kind, Guid.NewGuid(), Guid.NewGuid(), string.Empty, string.Empty, string.Empty)]
            })) - empty;
            Assert.IsTrue(input <= CameraAgentRunManifest.EncodedRowBytes, $"{kind} input {input}");
        }
        var master = Size(With(node with { CalibrationInputs = [new(Guid.NewGuid(), string.Empty, string.Empty)] })) - empty;
        Assert.IsTrue(master <= CameraAgentRunManifest.EncodedRowBytes, $"calibration input {master}");
        var attempt = Size(With(node with
        {
            Attempts = [new(int.MinValue, "lease", WidestInstant, WidestInstant, string.Empty, Longest<ProcessingOutcomeStatus>(), null,
                TimeSpan.MinValue, Longest<ProcessingNodeExecutionRoute>())]
        })) - empty;
        Assert.IsTrue(attempt <= CameraAgentRunManifest.EncodedRowBytes, $"attempt {attempt}");
        ProcessingGraphExecutionOutputState Output(int sourceCount) => new(int.MinValue, string.Empty, Guid.NewGuid(),
            Longest<FrameArtifactRole>(), "variant", "Available", null, false)
        { PayloadSha256 = string.Empty, SourceArtifactIds = Enumerable.Range(0, sourceCount).Select(static _ => Guid.NewGuid()).ToArray() };
        var output = Size(With(node with { Outputs = [Output(0)] })) - empty;
        Assert.IsTrue(output <= CameraAgentRunManifest.EncodedRowBytes, $"output {output}");
        // One more source identifier costs exactly its quoted form and a comma.
        Assert.AreEqual(CameraAgentRunManifest.EncodedSourceArtifactIdBytes,
            Size(With(node with { Outputs = [Output(2)] })) - Size(With(node with { Outputs = [Output(1)] })));
    }

    [TestMethod]
    public void RunPageMapsEveryEvidenceStateToItsOwnReason()
    {
        var detail = ManifestDetail();
        var expired = detail with { Nodes = [detail.Nodes[0] with { ArtifactBindingsRetained = false }] };

        Assert.IsNull(CameraAgentRunManifest.UnavailableReason(new ProcessingGraphExecutionDetailWithRunManifest(detail, detail)));
        Assert.AreEqual(CameraAgentRunManifest.ArtifactEvidenceUnavailableReason,
            CameraAgentRunManifest.UnavailableReason(new ProcessingGraphExecutionDetailWithRunManifest(detail, expired)));
        Assert.AreEqual(CameraAgentRunManifest.MissingReason,
            CameraAgentRunManifest.UnavailableReason(new ProcessingGraphExecutionDetailWithRunManifest(detail, detail with { Nodes = [], FrozenNodeCount = 1 })));
        // No evidence and no recorded failure is still reported as missing, never as available.
        Assert.AreEqual(CameraAgentRunManifest.MissingReason,
            CameraAgentRunManifest.UnavailableReason(new ProcessingGraphExecutionDetailWithRunManifest(detail, null)));
        Assert.AreEqual(ProcessingRunManifestBounds.UnavailableReason, CameraAgentRunManifest.UnavailableReason(
            new ProcessingGraphExecutionDetailWithRunManifest(detail, null, ProcessingRunManifestEvidenceFailure.ExceedsBounds)));
        Assert.AreEqual(CameraAgentRunManifest.CorruptReason, CameraAgentRunManifest.UnavailableReason(
            new ProcessingGraphExecutionDetailWithRunManifest(detail, null, ProcessingRunManifestEvidenceFailure.Corrupt)));
        Assert.AreEqual(CameraAgentRunManifest.TemporarilyUnavailableReason, CameraAgentRunManifest.UnavailableReason(
            new ProcessingGraphExecutionDetailWithRunManifest(detail, null, ProcessingRunManifestEvidenceFailure.Unavailable)));
        // A recorded failure outranks evidence that happens to be present.
        Assert.AreEqual(CameraAgentRunManifest.CorruptReason, CameraAgentRunManifest.UnavailableReason(
            new ProcessingGraphExecutionDetailWithRunManifest(detail, detail, ProcessingRunManifestEvidenceFailure.Corrupt)));
    }

    private static TEnum Longest<TEnum>() where TEnum : struct, Enum
        => Enum.GetValues<TEnum>().MaxBy(static value => value.ToString().Length);
}
