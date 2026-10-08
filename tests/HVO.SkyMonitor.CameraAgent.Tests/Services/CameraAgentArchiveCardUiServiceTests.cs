using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Services;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentArchiveCardUiServiceTests
{
    [TestMethod]
    public async Task ExactCaptureResolvesItsLiveRunAndDistinctLocalCandidatesAsync()
    {
        var captureId = Guid.NewGuid();
        var otherCaptureId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        var processing = new Mock<ICameraAgentProcessingGraphUiService>(MockBehavior.Strict);
        processing.Setup(service => service.GetLiveExecutionIdAsync(captureId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperatorUiResult<CameraAgentLiveRunLink>.Success(new(executionId)));
        var transients = new Mock<ICameraAgentTransientUiService>(MockBehavior.Strict);
        transients.Setup(service => service.GetCaptureStagesAsync(captureId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperatorUiResult<TransientCaptureStageView>.Success(new(captureId,
            [
                new("causal", candidateId, "Succeeded", "local", DateTimeOffset.UtcNow),
                new("centered", candidateId, "Succeeded", "local", DateTimeOffset.UtcNow),
                new("none", null, "Succeeded", "local", DateTimeOffset.UtcNow),
                new("invalid", Guid.Empty, "Succeeded", "local", DateTimeOffset.UtcNow)
            ])));

        var links = await new CameraAgentArchiveCardUiService(processing.Object, transients.Object)
            .GetLinksAsync(captureId, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(executionId, links.ExecutionId);
        CollectionAssert.AreEqual(new[] { candidateId }, links.CandidateIds.ToArray());
        Assert.IsTrue(links.CandidateLinksAvailable);
        processing.Verify(service => service.GetLiveExecutionIdAsync(otherCaptureId, It.IsAny<CancellationToken>()), Times.Never);
        transients.Verify(service => service.GetCaptureStagesAsync(otherCaptureId, It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task MissingRunAndMismatchedCaptureDoNotBorrowEvidenceAsync()
    {
        var captureId = Guid.NewGuid();
        var processing = new Mock<ICameraAgentProcessingGraphUiService>();
        processing.Setup(service => service.GetLiveExecutionIdAsync(captureId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperatorUiResult<CameraAgentLiveRunLink>.Failure(OperatorUiResultKind.NotFound, "internal detail"));
        var transients = new Mock<ICameraAgentTransientUiService>();
        transients.Setup(service => service.GetCaptureStagesAsync(captureId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperatorUiResult<TransientCaptureStageView>.Success(new(Guid.NewGuid(),
                [new("causal", Guid.NewGuid(), "Succeeded", "local", DateTimeOffset.UtcNow)])));

        var links = await new CameraAgentArchiveCardUiService(processing.Object, transients.Object)
            .GetLinksAsync(captureId, CancellationToken.None).ConfigureAwait(false);

        Assert.IsNull(links.ExecutionId);
        Assert.AreEqual("No live pipeline run was recorded for this capture.", links.RunUnavailableReason);
        Assert.HasCount(0, links.CandidateIds);
        Assert.IsFalse(links.CandidateLinksAvailable);
    }

    [TestMethod]
    public async Task UnavailableReadsDoNotExposeInternalMessagesOrInventZeroReviewAsync()
    {
        var captureId = Guid.NewGuid();
        var processing = new Mock<ICameraAgentProcessingGraphUiService>();
        processing.Setup(service => service.GetLiveExecutionIdAsync(captureId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperatorUiResult<CameraAgentLiveRunLink>.Failure(OperatorUiResultKind.Unavailable, "/private/state"));
        var transients = new Mock<ICameraAgentTransientUiService>();
        transients.Setup(service => service.GetCaptureStagesAsync(captureId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperatorUiResult<TransientCaptureStageView>.Failure(OperatorUiResultKind.Unauthorized, "/private/state"));

        var links = await new CameraAgentArchiveCardUiService(processing.Object, transients.Object)
            .GetLinksAsync(captureId, CancellationToken.None).ConfigureAwait(false);

        Assert.IsNull(links.ExecutionId);
        Assert.AreEqual("The pipeline run identity is unavailable.", links.RunUnavailableReason);
        Assert.IsFalse(links.CandidateLinksAvailable);
        Assert.IsTrue(links.AuthorizationDenied);
    }

    [TestMethod]
    public async Task CancellationStopsBeforeTheNextEvidenceReadAsync()
    {
        var captureId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        var processing = new Mock<ICameraAgentProcessingGraphUiService>(MockBehavior.Strict);
        processing.Setup(service => service.GetLiveExecutionIdAsync(captureId, cancellation.Token))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        var transients = new Mock<ICameraAgentTransientUiService>(MockBehavior.Strict);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await new CameraAgentArchiveCardUiService(processing.Object, transients.Object)
                .GetLinksAsync(captureId, cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
        transients.VerifyNoOtherCalls();
    }
}
