using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Components;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentEventEvidenceUiServiceTests
{
    [TestMethod]
    public async Task CausalSourcesKeepFutureGapsAndUseExactCaptureRunIdentities()
    {
        var detail = Detail();
        var captures = Captures(detail.Sources!);
        var runs = Runs(detail.Sources!);
        var result = await Service(detail, captures, runs).GetAsync(detail.Candidate.CandidateId, true, CancellationToken.None).ConfigureAwait(false);

        Assert.IsNotNull(result.Value);
        CollectionAssert.AreEqual(new[] { -2, -1, 0 }, result.Value.Frames.Where(static frame => frame.Source is not null).Select(static frame => frame.Position).ToArray());
        Assert.IsTrue(result.Value.Frames.Where(static frame => frame.Position > 0).All(static frame => frame.Source is null && frame.Image is null && frame.ExecutionId is null));
        Assert.AreEqual(detail.CandidateEvidence.CenterEvidenceId, result.Value.Reference?.Source?.EvidenceId);
        Assert.IsNotNull(result.Value.Reference?.Image);
        Assert.IsNotNull(result.Value.Reference.ExecutionId);
        Assert.AreEqual(3, captures.Invocations.Count);
        Assert.AreEqual(3, runs.Invocations.Count);
    }

    [TestMethod]
    public async Task CenteredReceiptPositionsDoNotDependOnSequenceArithmetic()
    {
        var centered = Enumerable.Range(-2, 5).Select(position => Source(position + 2, position) with { CaptureSequence = 900 - position * 137 }).ToArray();
        var detail = Detail() with { CenteredEvidence = new("Available", true, 5, 1), CenteredSources = centered.Reverse().ToArray() };
        var captures = Captures(centered);
        var result = await Service(detail, captures, Runs(centered)).GetAsync(detail.Candidate.CandidateId, true, CancellationToken.None).ConfigureAwait(false);

        Assert.IsNotNull(result.Value);
        CollectionAssert.AreEqual(centered.Select(static source => source.CaptureId).ToArray(), result.Value.Frames.Select(static frame => frame.Source!.CaptureId).ToArray());
        Assert.AreEqual(5, captures.Invocations.Count);
        Assert.IsTrue(result.Value.Frames.All(static frame => frame.Image is not null));
    }

    [TestMethod]
    public async Task ListReadsOnlyTheEndpointAndDoesNotRequestRuns()
    {
        var detail = Detail();
        var captures = Captures(detail.Sources!);
        var runs = new Mock<ICameraAgentProcessingGraphUiService>(MockBehavior.Strict);
        var result = await Service(detail, captures, runs).GetAsync(detail.Candidate.CandidateId, false, CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, captures.Invocations.Count);
        Assert.AreEqual(detail.Sources![2].CaptureId, captures.Invocations[0].Arguments[0]);
        Assert.AreEqual(nameof(ICameraAgentOperatorUiService.GetGalleryCaptureAsync), captures.Invocations[0].Method.Name);
        runs.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task ForeignCandidateIsNotSubstitutedAndInternalMessagesStayPrivate()
    {
        var detail = Detail();
        var transients = new Mock<ICameraAgentTransientUiService>();
        transients.Setup(service => service.GetCandidateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperatorUiResult<CameraAgentTransientOperatorDetail>.Success(detail));
        var captures = new Mock<ICameraAgentOperatorUiService>(MockBehavior.Strict);
        var runs = new Mock<ICameraAgentProcessingGraphUiService>(MockBehavior.Strict);
        var result = await new CameraAgentEventEvidenceUiService(transients.Object, captures.Object, runs.Object, new TestOperatorUiService())
            .GetAsync(Guid.NewGuid(), true, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.NotFound, result.Kind);
        Assert.IsNull(result.Value);
        captures.VerifyNoOtherCalls();
        runs.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task ForeignCaptureDoesNotSupplyAnImage()
    {
        var detail = Detail();
        var captures = new Mock<ICameraAgentOperatorUiService>();
        captures.Setup(service => service.GetGalleryCaptureAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperatorUiResult<CameraAgentGalleryCapture>.Success(OperatorUiTestData.Capture(Guid.NewGuid())));
        var result = await Service(detail, captures, new Mock<ICameraAgentProcessingGraphUiService>()).GetAsync(detail.Candidate.CandidateId, false, CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNull(result.Value?.Reference?.Image);
    }

    [TestMethod]
    public async Task RevokedCaptureReadFailsTheWholeEvidenceViewClosed()
    {
        var detail = Detail();
        var captures = new Mock<ICameraAgentOperatorUiService>();
        captures.Setup(service => service.GetGalleryCaptureAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperatorUiResult<CameraAgentGalleryCapture>.Failure(OperatorUiResultKind.Unauthorized, "/private/credential"));
        var result = await Service(detail, captures, new Mock<ICameraAgentProcessingGraphUiService>()).GetAsync(detail.Candidate.CandidateId, false, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unauthorized, result.Kind);
        Assert.IsNull(result.Value);
        Assert.AreEqual("Authorization is required.", result.Message);
    }

    private static CameraAgentEventEvidenceUiService Service(CameraAgentTransientOperatorDetail detail, Mock<ICameraAgentOperatorUiService> captures, Mock<ICameraAgentProcessingGraphUiService> runs)
    {
        var transients = new Mock<ICameraAgentTransientUiService>(MockBehavior.Strict);
        transients.Setup(service => service.GetCandidateAsync(detail.Candidate.CandidateId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperatorUiResult<CameraAgentTransientOperatorDetail>.Success(detail));
        return new(transients.Object, captures.Object, runs.Object, new TestOperatorUiService());
    }

    private static Mock<ICameraAgentOperatorUiService> Captures(IReadOnlyList<CameraAgentTransientOperatorSource> sources)
    {
        var service = new Mock<ICameraAgentOperatorUiService>(MockBehavior.Strict);
        foreach (var source in sources)
        {
            service.Setup(value => value.GetGalleryCaptureAsync(source.CaptureId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(OperatorUiResult<CameraAgentGalleryCapture>.Success(View(source).Capture));
        }
        return service;
    }

    private static Mock<ICameraAgentProcessingGraphUiService> Runs(IReadOnlyList<CameraAgentTransientOperatorSource> sources)
    {
        var service = new Mock<ICameraAgentProcessingGraphUiService>(MockBehavior.Strict);
        foreach (var source in sources)
        {
            service.Setup(value => value.GetLiveExecutionIdAsync(source.CaptureId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(OperatorUiResult<CameraAgentLiveRunLink>.Success(new(Guid.NewGuid())));
        }
        return service;
    }

    private static CameraAgentCaptureDetailView View(CameraAgentTransientOperatorSource source)
    {
        var capture = OperatorUiTestData.Capture(source.CaptureId);
        return new(capture, new(CameraAgentPresentationStage.Raw,
        [new(CameraAgentPresentationStage.Raw, "Raw", CameraAgentPresentationSlotAvailability.Available, "Available", source.ArtifactId,
            FrameArtifactRole.Raw, PreviewUrl: new Uri($"/cameraagent/operator/gallery/{source.CaptureId:D}/artifacts/{source.ArtifactId:D}/preview", UriKind.Relative))]));
    }

    private static CameraAgentTransientOperatorSource Source(int ordinal, int? position = null) => new(
        ordinal, Guid.NewGuid(), Guid.Parse("00000000-0000-0000-0000-000000000101"), FrameArtifactRole.Raw, Guid.NewGuid(),
        42 + ordinal, OperatorUiTestData.Now.AddSeconds(ordinal * 3), OperatorUiTestData.Now.AddSeconds(ordinal * 3), OperatorUiTestData.Now.AddSeconds(ordinal * 3 + 1), position);

    private static CameraAgentTransientOperatorDetail Detail()
    {
        var sources = Enumerable.Range(0, 3).Select(ordinal => Source(ordinal)).ToArray();
        var candidate = new CameraAgentTransientOperatorCandidate(Guid.NewGuid(), Guid.NewGuid(), "Provisional", "NeedsReview", "finalized", OperatorUiTestData.Now, OperatorUiTestData.Now, "Available", "Available", "Available", "Available");
        return new(candidate, new("Available", candidate.CreatedUtc, sources[2].EvidenceId, 3, null, null, []), new("Available", false, 3, 1),
            new("Available", false, 3, 1), new("Available"), new("Available"), sources);
    }
}
