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
    /// without the manifest read or its payload. Every state a test puts the download in is held against that
    /// decision here: the same reason, and the same evidence the manifest read returns.
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
                CollectionAssert.AreEqual(CameraAgentRunManifest.Encode(manifest), CameraAgentRunManifest.Encode(read.RunManifest));
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
}
