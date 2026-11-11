using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Endpoints;

/// <summary>
/// The run page decides whether the manifest can be downloaded without building it. These tests hold that decision
/// against the download's own, hold the size bound that lets the page skip all serialization against the encoder,
/// and hold the counted length that decides the byte limit against the payload it never builds.
/// </summary>
public sealed partial class CameraAgentProcessingGraphOperationsEndpointsTests
{
    // Every character takes the longest JSON escape, including a surrogate pair.
    private const string WorstText = "\"<>&'+`\u00e9\u2028\ud83d\ude00";

    private static readonly DateTimeOffset WidestInstant =
        new DateTimeOffset(2026, 12, 31, 23, 59, 59, 999, TimeSpan.FromHours(14)).AddTicks(9999);

    private static long NeverCounted(ProcessingGraphExecutionDetail detail)
        => throw new AssertFailedException("The manifest was serialized to decide availability.");

    /// <summary>
    /// The least the action allocates on this thread once its caches are warm. A payload is an array of its own
    /// length, so building one allocates at least that much and an action that allocates less built none.
    /// </summary>
    private static long AllocatedBy(Action action)
    {
        action();
        var least = long.MaxValue;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            action();
            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        return least;
    }

    /// <summary>
    /// A valid manifest of exactly the given encoded length: one node at its row limit, with the remainder spread
    /// over its inputs' descriptor checksums as letters, which are never escaped.
    /// </summary>
    private static ProcessingGraphExecutionDetail ManifestOfEncodedLength(int length)
    {
        var detail = ManifestDetail();
        var node = detail.Nodes[0];
        ProcessingGraphExecutionDetail Padded(Func<int, int> padding) => detail with
        {
            Nodes =
            [
                node with
                {
                    Inputs = Enumerable.Range(0, ProcessingRunManifestBounds.MaximumRows).Select(index => node.Inputs[0] with
                    {
                        Ordinal = index,
                        DescriptorSha256 = new string('d', 64 + padding(index))
                    }).ToArray()
                }
            ]
        };
        var missing = length - CameraAgentRunManifest.Encode(Padded(static _ => 0)).Length;
        Assert.IsTrue(missing >= 0, $"The unpadded manifest is already {-missing} bytes over {length}.");
        var (each, extra) = Math.DivRem(missing, ProcessingRunManifestBounds.MaximumRows);
        var manifest = Padded(index => each + (index < extra ? 1 : 0));
        Assert.AreEqual(length, CameraAgentRunManifest.Encode(manifest).Length);
        return manifest;
    }

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
            // The page reaches the same verdict, and any serialization would fail the test.
            Assert.AreEqual(reason, CameraAgentRunManifest.UnavailableReason(candidate, NeverCounted), name);
            Assert.AreEqual(reason, CameraAgentRunManifest.UnavailableReason(candidate), name);
        }
    }

    [TestMethod]
    public void ManifestsWithinTheSizeBoundAreOfferedWithoutAnySerialization()
    {
        var detail = ManifestDetail();
        var node = detail.Nodes[0];

        // An ordinary run is far below the limit by its upper bound alone.
        Assert.IsTrue(CameraAgentRunManifest.MaximumEncodedBytes(detail) < ProcessingRunManifestBounds.MaximumBytes / 100);
        Assert.IsNull(CameraAgentRunManifest.UnavailableReason(detail, NeverCounted));

        // So is half a node's row limit.
        var half = detail with
        {
            Nodes =
            [
                node with
                {
                    Inputs = Enumerable.Range(0, ProcessingRunManifestBounds.MaximumRows / 2)
                        .Select(index => node.Inputs[0] with { Ordinal = index }).ToArray()
                }
            ]
        };
        Assert.IsNull(CameraAgentRunManifest.UnavailableReason(half, NeverCounted));
    }

    [TestMethod]
    public void ByteLimitIsDecidedOnBothSidesOfTheLimitWithoutBuildingThePayload()
    {
        var detail = ManifestDetail();
        var node = detail.Nodes[0];
        // A node at its row limit: the upper bound is over the byte limit and the payload is well inside it.
        var inside = detail with
        {
            Nodes =
            [
                node with
                {
                    Inputs = Enumerable.Range(0, ProcessingRunManifestBounds.MaximumRows)
                        .Select(index => node.Inputs[0] with { Ordinal = index }).ToArray()
                }
            ]
        };
        // The last manifest the download serves, and the first it refuses.
        var atLimit = ManifestOfEncodedLength(ProcessingRunManifestBounds.MaximumBytes);
        var overLimit = ManifestOfEncodedLength(ProcessingRunManifestBounds.MaximumBytes + 1);
        // Far over the limit through output lineage rather than rows.
        var sources = Enumerable.Range(0, 512).Select(static _ => Guid.NewGuid()).ToArray();
        var farOver = detail with
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
        var cases = new (string Name, ProcessingGraphExecutionDetail Detail, string? Reason)[]
        {
            ("inside the limit", inside, null),
            ("exactly at the limit", atLimit, null),
            ("one byte over the limit", overLimit, ProcessingRunManifestBounds.UnavailableReason),
            ("far over the limit", farOver, ProcessingRunManifestBounds.UnavailableReason)
        };

        foreach (var (name, candidate, reason) in cases)
        {
            // Only the test builds the payload, to know what the download does with this manifest.
            var download = CameraAgentRunManifest.Create(candidate);
            var payloadLength = CameraAgentRunManifest.Encode(candidate).LongLength;
            Assert.AreEqual(reason, download.Reason, name);
            Assert.AreEqual(reason is null, download.Payload is not null, name);
            Assert.AreEqual(reason is null, payloadLength <= ProcessingRunManifestBounds.MaximumBytes, name);
            // None of these is decided by the upper bound, so each is decided by its length.
            Assert.IsTrue(CameraAgentRunManifest.MaximumEncodedBytes(candidate) > ProcessingRunManifestBounds.MaximumBytes, name);

            string? verdict = "not decided";
            var allocated = AllocatedBy(() => verdict = CameraAgentRunManifest.UnavailableReason(candidate));

            Assert.AreEqual(reason, verdict, name);
            // The length that decided it is the payload's, to the byte, and no payload was built to learn it.
            Assert.AreEqual(payloadLength, CameraAgentRunManifest.EncodedLength(candidate), name);
            Assert.IsTrue(allocated < payloadLength,
                $"{name}: deciding availability allocated {allocated} bytes, which is room for its payload of {payloadLength} bytes.");
        }

        Assert.AreEqual(ProcessingRunManifestBounds.MaximumBytes, CameraAgentRunManifest.EncodedLength(atLimit));
        Assert.AreEqual(ProcessingRunManifestBounds.MaximumBytes + 1L, CameraAgentRunManifest.EncodedLength(overLimit));
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

        var encoded = CameraAgentRunManifest.Encode(detail).LongLength;
        var bound = CameraAgentRunManifest.MaximumEncodedBytes(detail);

        Assert.IsTrue(bound >= encoded, $"The bound {bound} understates the encoded manifest of {encoded} bytes.");
        // Escapes and widest scalars included, the counted length is the payload's.
        Assert.AreEqual(encoded, CameraAgentRunManifest.EncodedLength(detail));
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
