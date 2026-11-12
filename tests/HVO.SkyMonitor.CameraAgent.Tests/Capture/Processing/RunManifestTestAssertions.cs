using System.Collections;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Endpoints;
using Microsoft.AspNetCore.Http;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

internal static class RunManifestTestAssertions
{
    internal static async Task<string> ResponseAsync(IProcessingGraphOperations operations, Guid executionId, string? unavailableReason = null)
    {
        var app = CameraAgentProcessingGraphOperationsEndpointsTests.CreateApp(operations, Mock.Of<ICameraAgentArtifactService>());
        await using var lifetime = app.ConfigureAwait(false);
        var (status, body) = await CameraAgentProcessingGraphOperationsEndpointsTests.InvokeForBodyAsync(
            app, "DownloadCameraAgentProcessingRunManifest", HttpMethods.Get,
            routeValues: new() { ["executionId"] = executionId.ToString() }).ConfigureAwait(false);
        Assert.AreEqual(unavailableReason is null ? 200 : 409, status, body);
        using var document = JsonDocument.Parse(body);
        if (unavailableReason is null)
            Assert.AreEqual(executionId, document.RootElement.GetProperty("executionId").GetGuid());
        else
        {
            Assert.AreEqual(unavailableReason, document.RootElement.GetProperty("title").GetString());
            Assert.IsFalse(body.Contains("schemaVersion", StringComparison.Ordinal));
        }
        await RunPageAgreesAsync(operations, executionId, unavailableReason).ConfigureAwait(false);
        return body;
    }

    /// <summary>
    /// The run page decides whether to offer the download from one read of the detail and its manifest evidence,
    /// without the manifest read, its payload or any serialization. Every state a test puts the download in is held
    /// against that decision here: the same reason, the same evidence the manifest read returns, a computed length
    /// equal to the payload's, and no read of what only a written payload needs.
    /// </summary>
    private static async Task RunPageAgreesAsync(IProcessingGraphOperations operations, Guid executionId, string? unavailableReason)
    {
        InvalidDataException? unreadableJournal = null;
        try
        {
            await operations.ReadExecutionDetailAsync(executionId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            unreadableJournal = exception;
        }
        if (unreadableJournal is not null)
        {
            // Corruption that reaches the journal itself leaves the page nothing to show. Its read fails with the
            // journal's own failure, as the journal read always has, and no manifest verdict is reached.
            var failure = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await operations.ReadExecutionDetailWithRunManifestAsync(executionId, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual(unreadableJournal.Message, failure.Message);
            return;
        }
        var read = await operations.ReadExecutionDetailWithRunManifestAsync(executionId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(read);
        Assert.AreEqual(unavailableReason, CameraAgentRunManifest.UnavailableReason(read));
        switch (read.RunManifestFailure)
        {
            case ProcessingRunManifestEvidenceFailure.None:
                var manifest = await operations.ReadRunManifestAsync(executionId, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(manifest);
                Assert.IsNotNull(read.RunManifest);
                Assert.AreEqual(manifest.FrozenNodeCount, read.RunManifest.FrozenNodeCount);
                // What the verdict is decided from but the payload does not carry.
                CollectionAssert.AreEqual(Verdict(manifest), Verdict(read.RunManifest));
                // Everything the payload carries.
                var payload = CameraAgentRunManifest.Encode(manifest);
                CollectionAssert.AreEqual(payload, CameraAgentRunManifest.Encode(read.RunManifest));
                // The length the page decides the byte limit from is this payload's, computed and not serialized.
                Assert.AreEqual(payload.LongLength, CameraAgentRunManifest.EncodedLength(read.RunManifest));
                var (counted, lineage) = WithCountedLineage(read.RunManifest);
                Assert.AreEqual(unavailableReason, CameraAgentRunManifest.UnavailableReason(counted));
                Assert.IsTrue(lineage.All(static ids => ids.Reads <= 1),
                    "Deciding availability read an output's lineage more than once; once validates it, and only writing the payload needs it again.");
                var validated = lineage.Sum(static ids => ids.Reads);
                Assert.AreEqual(payload.LongLength, CameraAgentRunManifest.EncodedLength(counted));
                Assert.AreEqual(validated, lineage.Sum(static ids => ids.Reads),
                    "Computing the payload's length read an output's lineage; only writing the payload needs it.");
                break;
            case ProcessingRunManifestEvidenceFailure.ExceedsBounds:
                Assert.IsNull(read.RunManifest);
                await Assert.ThrowsAsync<ProcessingRunManifestBoundsException>(async () =>
                    await operations.ReadRunManifestAsync(executionId, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                break;
            case ProcessingRunManifestEvidenceFailure.Corrupt:
                Assert.IsNull(read.RunManifest);
                await Assert.ThrowsAsync<InvalidDataException>(async () =>
                    await operations.ReadRunManifestAsync(executionId, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                break;
            default:
                Assert.Fail($"The manifest evidence read failed as {read.RunManifestFailure}.");
                break;
        }
    }

    private static string[] Verdict(ProcessingGraphExecutionDetail detail)
        => detail.Nodes.Select(static node =>
            $"{node.NodeId}|{node.ArtifactBindingsRetained}|{node.InputEvidenceIncomplete}|{node.CommittedOutputCount}|" +
            $"{node.Inputs.Count}|{node.CalibrationInputs.Count}|{node.Attempts.Count}|{node.Outputs.Count}").ToArray();

    /// <summary>
    /// The same manifest with each output's lineage behind a list that counts how often its identifiers are read.
    /// Deciding availability reads them once, to check that each names a retained input. Whatever writes the
    /// payload, the serializer included, has to read them again, while the payload's length needs only how many
    /// there are. A second read is therefore a payload being written.
    /// </summary>
    internal static (ProcessingGraphExecutionDetail Detail, IReadOnlyList<ReadCountingIds> Lineage) WithCountedLineage(
        ProcessingGraphExecutionDetail detail)
    {
        var lineage = new List<ReadCountingIds>();
        var nodes = detail.Nodes.Select(node => node with
        {
            Outputs = node.Outputs.Select(output =>
            {
                var counted = new ReadCountingIds(output.SourceArtifactIds);
                lineage.Add(counted);
                return output with { SourceArtifactIds = counted };
            }).ToArray()
        }).ToArray();
        return (detail with { Nodes = nodes }, lineage);
    }

    /// <summary>Lineage identifiers that count each enumeration and each indexed read; their number is free.</summary>
    internal sealed class ReadCountingIds(IReadOnlyList<Guid> ids) : IReadOnlyList<Guid>
    {
        internal int Reads { get; private set; }

        public int Count => ids.Count;

        public Guid this[int index]
        {
            get
            {
                Reads++;
                return ids[index];
            }
        }

        public IEnumerator<Guid> GetEnumerator()
        {
            Reads++;
            return ids.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        internal void Reset() => Reads = 0;
    }
}
