using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Endpoints;

/// <summary>
/// The run page decides whether the manifest can be downloaded without building it and without serializing
/// anything. These tests hold that decision against the download's own, hold the computed length that decides the
/// byte limit against the payload's length to the byte, and fail if deciding reads what only a written payload needs.
/// </summary>
public sealed partial class CameraAgentProcessingGraphOperationsEndpointsTests
{
    // Every character takes the longest JSON escape, including a surrogate pair.
    private const string WorstText = "\"<>&'+`\u00e9\u2028\ud83d\ude00";

    private static readonly DateTimeOffset WidestInstant =
        new DateTimeOffset(2026, 12, 31, 23, 59, 59, 999, TimeSpan.FromHours(14)).AddTicks(9999);

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
    public void ManifestAvailabilityIsTheDownloadsVerdictForEveryEvidenceState()
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
            Assert.AreEqual(reason, CameraAgentRunManifest.UnavailableReason(candidate), name);
        }
    }

    [TestMethod]
    public void ByteLimitIsDecidedOnBothSidesOfTheLimitWithoutSerializingThePayload()
    {
        var detail = ManifestDetail();
        var node = detail.Nodes[0];
        // A node at its row limit, well inside the byte limit.
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
            ("an ordinary run", detail, null),
            ("inside the limit", inside, null),
            // The last manifest the download serves, and the first it refuses.
            ("exactly at the limit", ManifestOfEncodedLength(ProcessingRunManifestBounds.MaximumBytes), null),
            ("one byte over the limit", ManifestOfEncodedLength(ProcessingRunManifestBounds.MaximumBytes + 1),
                ProcessingRunManifestBounds.UnavailableReason),
            ("far over the limit", farOver, ProcessingRunManifestBounds.UnavailableReason)
        };

        var serialized = new List<string>();
        foreach (var (name, original, reason) in cases)
        {
            // Only the test builds the payload, to know what the download does with this manifest.
            var payloadLength = CameraAgentRunManifest.Encode(original).LongLength;
            Assert.AreEqual(reason is null, payloadLength <= ProcessingRunManifestBounds.MaximumBytes, name);
            var (candidate, lineage) = RunManifestTestAssertions.WithCountedLineage(original);
            Assert.IsNotEmpty(lineage, name);

            // The download validates each output's lineage and then writes it, so it reads every list twice or more.
            var download = CameraAgentRunManifest.Create(candidate);
            Assert.AreEqual(reason, download.Reason, name);
            Assert.AreEqual(reason is null, download.Payload is not null, name);
            Assert.IsTrue(lineage.All(static ids => ids.Reads >= 2), $"{name}: the download did not read the lineage it wrote.");
            foreach (var ids in lineage) ids.Reset();

            // Availability validates it and writes nothing, so it reads every list once. Running the serializer
            // over the manifest, whether into a payload, a counting sink or a pooled buffer, reads each again.
            Assert.AreEqual(reason, CameraAgentRunManifest.UnavailableReason(candidate), name);
            if (lineage.Any(static ids => ids.Reads != 1))
                serialized.Add($"{name}: deciding availability read an output's lineage {lineage.Max(static ids => ids.Reads)} times");
            foreach (var ids in lineage) ids.Reset();

            // The length that decided it is the payload's, to the byte, and computing it reads no identifier.
            Assert.AreEqual(payloadLength, CameraAgentRunManifest.EncodedLength(candidate), name);
            if (lineage.Any(static ids => ids.Reads != 0))
                serialized.Add($"{name}: computing the length read an output's lineage {lineage.Max(static ids => ids.Reads)} times");
        }
        Assert.IsEmpty(serialized, "Validating a manifest reads each output's lineage once, and only writing its payload reads it again. " +
            string.Join("; ", serialized) + ".");

        Assert.AreEqual(ProcessingRunManifestBounds.MaximumBytes, CameraAgentRunManifest.EncodedLength(cases[2].Detail));
        Assert.AreEqual(ProcessingRunManifestBounds.MaximumBytes + 1L, CameraAgentRunManifest.EncodedLength(cases[3].Detail));

        // Nor is anything the size of the payload allocated on the way.
        foreach (var (name, candidate, _) in cases.Skip(1))
        {
            var payloadLength = CameraAgentRunManifest.Encode(candidate).LongLength;
            var allocated = AllocatedBy(() => CameraAgentRunManifest.UnavailableReason(candidate));
            Assert.IsTrue(allocated < payloadLength,
                $"{name}: deciding availability allocated {allocated} bytes, which is room for its payload of {payloadLength} bytes.");
        }
    }

    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(1, 1)]
    [DataRow(3, 7)]
    public void EncodedLengthIsThePayloadsLengthForWorstCaseText(int nodeCount, int rowsPerKind)
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

        Assert.AreEqual(CameraAgentRunManifest.Encode(detail).LongLength, CameraAgentRunManifest.EncodedLength(detail));
    }

    [TestMethod]
    public void EncodedLengthFollowsTheEncoderForEveryCharacter()
    {
        var template = ManifestDetail();
        var node = template.Nodes[0] with { Inputs = [], Attempts = [], Outputs = [] };
        // One manifest carries a text in each position the encoder could treat differently: alone, after and before
        // a character that is always escaped, doubled, and between letters.
        ProcessingGraphExecutionDetail With(string alone, string after, string before, string doubled, string between) => template with
        {
            Execution = template.Execution with { GraphRevisionId = alone },
            Nodes = [node with { NodeId = after, PlanSha256 = before, Status = doubled, NormalizationPlanIdentitySha256 = between }]
        };
        static bool Agrees(ProcessingGraphExecutionDetail detail)
            => CameraAgentRunManifest.Encode(detail).LongLength == CameraAgentRunManifest.EncodedLength(detail);
        var disagreements = new List<string>();
        void Check(string text)
        {
            string[] positions = [text, "\u00e9" + text, text + "\u00e9", text + text, "a" + text + "b"];
            if (Agrees(With(positions[0], positions[1], positions[2], positions[3], positions[4]))) return;
            // Name the position that disagrees.
            foreach (var position in positions.Where(position => !Agrees(With(position, "", "", "", ""))).Take(20 - Math.Min(20, disagreements.Count)))
                disagreements.Add(string.Join(' ', position.Select(static unit => ((int)unit).ToString("X4", CultureInfo.InvariantCulture))));
        }

        // Every UTF-16 unit, lone surrogates included.
        for (var unit = 0; unit <= char.MaxValue; unit++) Check(((char)unit).ToString());
        // Surrogates in and out of order, and the ASCII the encoder leaves alone.
        foreach (var text in new[]
        {
            "\ud83d\ude00", "\ude00\ud83d", "\ud83d\ud83d", "\ude00\ude00", "x\ud83d", "\ude00x", string.Empty,
            "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ", " !#$%()*,-./:;=?@[]^_{|}~", WorstText
        })
            Check(text);

        Assert.IsEmpty(disagreements, "The computed length differs from the payload for the UTF-16 units: " + string.Join("; ", disagreements));
    }

    [TestMethod]
    public void EncodedLengthIsThePayloadsLengthForEveryMemberAtItsExtremes()
    {
        var template = ManifestDetail();
        var execution = template.Execution;
        var node = template.Nodes[0];
        var input = node.Inputs[0];
        var attempt = node.Attempts[0];
        var output = node.Outputs[0];
        var master = new ProcessingGraphExecutionCalibrationInputState(Guid.NewGuid(), new string('a', 64), "Dark");
        var variants = new List<(string Name, ProcessingGraphExecutionDetail Detail)>();
        void Add(string name, ProcessingGraphExecutionDetail detail) => variants.Add((name, detail));
        ProcessingGraphExecutionDetail OfExecution(ProcessingGraphExecutionState value) => template with { Execution = value };
        ProcessingGraphExecutionDetail OfNode(ProcessingGraphExecutionNodeState value) => template with { Nodes = [value] };
        ProcessingGraphExecutionDetail OfInput(ProcessingGraphExecutionInputState value) => OfNode(node with { Inputs = [value] });
        ProcessingGraphExecutionDetail OfAttempt(ProcessingGraphNodeAttemptState value) => OfNode(node with { Attempts = [value] });
        ProcessingGraphExecutionDetail OfOutput(ProcessingGraphExecutionOutputState value) => OfNode(node with { Outputs = [value] });
        ProcessingGraphExecutionDetail OfMaster(ProcessingGraphExecutionCalibrationInputState value) => OfNode(node with { CalibrationInputs = [value] });

        // Shapes: empty and repeated objects and arrays.
        Add("no nodes", template with { Nodes = [] });
        Add("an empty node", OfNode(node with { Inputs = [], Attempts = [], Outputs = [] }));
        Add("three nodes", template with { Nodes = [node, node with { NodeId = "second" }, node with { Inputs = [], Attempts = [], Outputs = [] }] });
        Add("three of every row", OfNode(node with
        {
            Inputs = [input, input, input],
            Attempts = [attempt, attempt, attempt],
            Outputs = [output, output, output],
            CalibrationInputs = [master, master, master]
        }));

        // Numbers: every digit count, both signs.
        foreach (var value in new[] { 0, 1, 9, 10, 99, 100, 4096, 999_999_999, 1_000_000_000, int.MaxValue, -1, -9, -10, -2_147_483_647, int.MinValue })
        {
            Add($"execution attempt count {value}", OfExecution(execution with { AttemptCount = value }));
            Add($"node attempt count {value}", OfNode(node with { AttemptCount = value }));
            Add($"input ordinal {value}", OfInput(input with { Ordinal = value }));
            Add($"input window position {value}", OfInput(input with { WindowPosition = value }));
            Add($"attempt number {value}", OfAttempt(attempt with { AttemptNumber = value }));
            Add($"output ordinal {value}", OfOutput(output with { Ordinal = value }));
            // A calibration input's ordinal continues from the largest input ordinal.
            Add($"calibration ordinals after {value}", OfNode(node with { Inputs = [input with { Ordinal = value }], CalibrationInputs = [master, master] }));
        }
        Add("calibration ordinals from zero", OfNode(node with { Inputs = [], Outputs = [], CommittedOutputCount = 0, CalibrationInputs = [master, master] }));
        Add("calibration ordinals across a digit", OfNode(node with { Inputs = [input with { Ordinal = 7 }], CalibrationInputs = Enumerable.Repeat(master, 5).ToArray() }));

        // Text: absent, empty, plain, and every kind of escape.
        foreach (var value in new[] { null, string.Empty, "a", new string('f', 64), "\t\n\r\b\f\\\"/ ~\u007f\u0000\u001f", WorstText, "plain \u00e9 text", "\ud83d" })
        {
            var shown = value is null ? "null" : $"[{value.Length}]";
            Add($"graph revision {shown}", OfExecution(execution with { GraphRevisionId = value! }));
            Add($"graph definition identity {shown}", OfExecution(execution with { GraphDefinitionIdentitySha256 = value! }));
            Add($"shared plan identity {shown}", OfExecution(execution with { SharedPlanIdentitySha256 = value! }));
            Add($"local plan identity {shown}", OfExecution(execution with { LocalPlanIdentitySha256 = value! }));
            Add($"node id {shown}", OfNode(node with { NodeId = value! }));
            Add($"node plan {shown}", OfNode(node with { PlanSha256 = value! }));
            Add($"node status {shown}", OfNode(node with { Status = value! }));
            Add($"normalization plan identity {shown}", OfNode(node with { NormalizationPlanIdentitySha256 = value }));
            Add($"input descriptor {shown}", OfInput(input with { DescriptorSha256 = value }));
            Add($"input payload {shown}", OfInput(input with { PayloadSha256 = value }));
            Add($"input output identity {shown}", OfInput(input with { OutputIdentitySha256 = value }));
            Add($"attempt status {shown}", OfAttempt(attempt with { Status = value! }));
            Add($"output identity {shown}", OfOutput(output with { OutputIdentitySha256 = value! }));
            Add($"output payload {shown}", OfOutput(output with { PayloadSha256 = value }));
            Add($"calibration payload {shown}", OfMaster(master with { PayloadSha256 = value! }));
            Add($"calibration kind {shown}", OfMaster(master with { Kind = value! }));
        }

        // Instants: absent, every count of fraction digits, and offsets on both sides of UTC.
        var second = new DateTime(2026, 11, 11, 17, 5, 18, DateTimeKind.Unspecified);
        var instants = new List<DateTimeOffset?> { null, WidestInstant, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, DateTimeOffset.FromUnixTimeMilliseconds(1_794_416_718_120) };
        foreach (var fraction in new long[] { 0, 1, 10, 100, 1_000, 10_000, 100_000, 1_000_000, 9_999_999, 1_234_567, 1_230_000, 5_000_000, 3_004_000 })
            foreach (var offset in new[] { TimeSpan.Zero, TimeSpan.FromHours(14), TimeSpan.FromHours(-12), new TimeSpan(5, 30, 0) })
                instants.Add(new DateTimeOffset(second.AddTicks(fraction), offset));
        foreach (var value in instants)
        {
            var shown = value?.ToString("O", CultureInfo.InvariantCulture) ?? "null";
            if (value is { } instant)
            {
                Add($"accepted {shown}", OfExecution(execution with { AcceptedUtc = instant }));
                Add($"attempt started {shown}", OfAttempt(attempt with { StartedUtc = instant }));
            }
            Add($"execution started {shown}", OfExecution(execution with { StartedUtc = value }));
            Add($"execution completed {shown}", OfExecution(execution with { CompletedUtc = value }));
            Add($"node started {shown}", OfNode(node with { StartedUtc = value }));
            Add($"node completed {shown}", OfNode(node with { CompletedUtc = value }));
            Add($"attempt completed {shown}", OfAttempt(attempt with { CompletedUtc = value }));
        }

        // Durations: absent, with and without days, a fraction and a sign.
        foreach (var value in new TimeSpan?[]
        {
            null, TimeSpan.Zero, TimeSpan.FromTicks(1), TimeSpan.FromTicks(-1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.5),
            TimeSpan.FromMilliseconds(250), TimeSpan.FromDays(1), TimeSpan.FromDays(10) + TimeSpan.FromTicks(1), TimeSpan.FromDays(-400),
            new TimeSpan(0, 23, 59, 59, 999), TimeSpan.MinValue, TimeSpan.MaxValue
        })
            Add($"attempt duration {value?.ToString("c", CultureInfo.InvariantCulture) ?? "null"}", OfAttempt(attempt with { Duration = value }));

        // Enumerations: every named value, an absent one, and numbers that name nothing.
        foreach (var value in Enum.GetValues<ProcessingGraphExecutionClass>().Append((ProcessingGraphExecutionClass)12_345).Append((ProcessingGraphExecutionClass)(-7)))
            Add($"execution class {value}", OfExecution(execution with { ExecutionClass = value }));
        foreach (var value in Enum.GetValues<ProcessingGraphExecutionStatus>().Append((ProcessingGraphExecutionStatus)12_345).Append((ProcessingGraphExecutionStatus)(-7)))
            Add($"execution status {value}", OfExecution(execution with { Status = value }));
        foreach (var value in Enum.GetValues<ProcessingOutcomeStatus>().Cast<ProcessingOutcomeStatus?>().Append(null).Append((ProcessingOutcomeStatus)12_345).Append((ProcessingOutcomeStatus)(-7)))
            Add($"attempt outcome {value?.ToString() ?? "null"}", OfAttempt(attempt with { Outcome = value }));
        foreach (var value in Enum.GetValues<ProcessingNodeExecutionRoute>().Append((ProcessingNodeExecutionRoute)12_345).Append((ProcessingNodeExecutionRoute)(-7)))
            Add($"attempt route {value}", OfAttempt(attempt with { ExecutionRoute = value }));
        foreach (var value in Enum.GetValues<FrameArtifactRole>().Append((FrameArtifactRole)12_345).Append((FrameArtifactRole)(-7)))
            Add($"output role {value}", OfOutput(output with { Role = value }));
        // An auxiliary input carries no window position.
        foreach (var value in Enum.GetValues<ProcessingGraphExecutionInputKind>().Append((ProcessingGraphExecutionInputKind)12_345).Append((ProcessingGraphExecutionInputKind)(-7)))
            Add($"input kind {value}", OfInput(input with { Kind = value, WindowPosition = 123 }));

        // Flags and lineage.
        foreach (var value in new[] { false, true })
        {
            Add($"cancellation requested {value}", OfExecution(execution with { CancellationRequested = value }));
            Add($"node required {value}", OfNode(node with { Required = value }));
            Add($"output published {value}", OfOutput(output with { Published = value }));
        }
        foreach (var count in new[] { 0, 1, 2, 3, 512, 513 })
            Add($"{count} lineage sources", OfOutput(output with { SourceArtifactIds = Enumerable.Range(0, count).Select(static _ => Guid.NewGuid()).ToArray() }));

        var disagreements = variants
            .Select(static variant => (variant.Name, Payload: CameraAgentRunManifest.Encode(variant.Detail).LongLength, Computed: CameraAgentRunManifest.EncodedLength(variant.Detail)))
            .Where(static variant => variant.Payload != variant.Computed)
            .Select(static variant => $"{variant.Name}: payload {variant.Payload}, computed {variant.Computed}")
            .ToArray();
        Assert.IsEmpty(disagreements, string.Join(Environment.NewLine, disagreements));
        Assert.IsTrue(variants.Count > 600, $"Only {variants.Count} variants were built.");
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394:Do not use insecure randomness",
        Justification = "A fixed seed defines reproducible synthetic manifests, never security material.")]
    public void EncodedLengthIsThePayloadsLengthForArbitraryManifests()
    {
        // Seeded, so a disagreement names an iteration that can be run again.
        var random = new Random(1781);
        T Pick<T>(params T[] values) => values[random.Next(values.Length)];
        TEnum PickEnum<TEnum>() where TEnum : struct, Enum
            => random.Next(8) == 0 ? (TEnum)Enum.ToObject(typeof(TEnum), random.Next(-50, 5_000)) : Pick(Enum.GetValues<TEnum>());
        string Text() => Pick(string.Empty, "a", new string('0', 64), new string('F', 64), "Completed", WorstText, "\t\\\"", "node main", "pr\u00e9visualisation", "\ud83d");
        string? Optional() => random.Next(4) == 0 ? null : Text();
        int Number() => Pick(0, 1, 7, 10, 123, 4_095, 65_536, int.MaxValue, -1, -42, int.MinValue, random.Next());
        DateTimeOffset Instant()
        {
            // Whole ticks, microseconds, milliseconds and seconds, so every count of fraction digits occurs.
            var granularity = Pick(1L, 10, 1_000, 10_000, 10_000_000);
            var ticks = random.NextInt64(TimeSpan.TicksPerDay, DateTime.MaxValue.Ticks - TimeSpan.TicksPerDay) / granularity * granularity;
            return new DateTimeOffset(ticks, TimeSpan.Zero).ToOffset(TimeSpan.FromMinutes(random.Next(-720, 721)));
        }
        DateTimeOffset? OptionalInstant() => random.Next(4) == 0 ? null : Instant();
        TimeSpan? Duration() => random.Next(4) == 0 ? null : TimeSpan.FromTicks(random.NextInt64(long.MinValue + 1, long.MaxValue) >> random.Next(0, 62));
        Guid[] Ids(int most) => Enumerable.Range(0, random.Next(most + 1)).Select(static _ => Guid.NewGuid()).ToArray();

        var template = ManifestDetail();
        for (var iteration = 0; iteration < 1_500; iteration++)
        {
            var execution = template.Execution with
            {
                ExecutionClass = PickEnum<ProcessingGraphExecutionClass>(),
                Status = PickEnum<ProcessingGraphExecutionStatus>(),
                GraphRevisionId = Text(),
                GraphDefinitionIdentitySha256 = Text(),
                SharedPlanIdentitySha256 = Text(),
                LocalPlanIdentitySha256 = Text(),
                AcceptedUtc = Instant(),
                StartedUtc = OptionalInstant(),
                CompletedUtc = OptionalInstant(),
                CancellationRequested = random.Next(2) == 0,
                AttemptCount = Number()
            };
            var nodes = Enumerable.Range(0, random.Next(4)).Select(_ => new ProcessingGraphExecutionNodeState(
                Text(), random.Next(2) == 0, Text(), Text(), Optional(), Number(), OptionalInstant(), OptionalInstant(),
                Enumerable.Range(0, random.Next(5)).Select(_ => new ProcessingGraphExecutionInputState(
                    Number(), Number(), PickEnum<ProcessingGraphExecutionInputKind>(), Guid.NewGuid(), Guid.NewGuid(), Optional(), Optional(), Optional())).ToArray(),
                Enumerable.Range(0, random.Next(5)).Select(_ => new ProcessingGraphNodeAttemptState(
                    Number(), "lease", Instant(), OptionalInstant(), Text(), random.Next(4) == 0 ? null : PickEnum<ProcessingOutcomeStatus>(),
                    Optional(), Duration(), PickEnum<ProcessingNodeExecutionRoute>())).ToArray(),
                Enumerable.Range(0, random.Next(5)).Select(_ => new ProcessingGraphExecutionOutputState(
                    Number(), Text(), Guid.NewGuid(), PickEnum<FrameArtifactRole>(), "variant", "Available", Optional(), random.Next(2) == 0)
                { PayloadSha256 = Optional(), SourceArtifactIds = Ids(6) }).ToArray())
            {
                NormalizationPlanIdentitySha256 = Optional(),
                CalibrationInputs = Enumerable.Range(0, random.Next(4)).Select(_ =>
                    new ProcessingGraphExecutionCalibrationInputState(Guid.NewGuid(), Text(), Text())).ToArray()
            }).ToArray();
            var detail = new ProcessingGraphExecutionDetail(execution, nodes);

            Assert.AreEqual(CameraAgentRunManifest.Encode(detail).LongLength, CameraAgentRunManifest.EncodedLength(detail), $"iteration {iteration}");
        }
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
