using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Components;
using HVO.SkyMonitor.CameraAgent.Tests.Services;
using HVO.SkyMonitor.Processing.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    [TestMethod]
    [TestCategory("Integration")]
    [DataRow("raw")]
    [DataRow("missing-associations-and-quarantined-context")]
    public async Task CurrentSkyInvalidatesHeldFitWhenLineageChangesDuringFallback(string damage)
    {
        var root = CreateTestRoot();
        try
        {
            var token = CancellationToken.None;
            var mixedInputs = damage == "missing-associations-and-quarantined-context";
            var fixture = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(fixture.Fixture.Options);
            using var gate = new CaptureAstrometryExecutionGate();
            using var artifacts = SiloInfrastructureFixture.OpenArtifactReader(fixture.Fixture.Options, store);
            var persistence = CreatePersistence(fixture.Fixture.Options, store, telemetry);
            var older = await ProduceLatestReadFixtureAsync(fixture, store, persistence, telemetry, gate).ConfigureAwait(false);
            var reference = await MeasuredAstrometryFixture.CreateAtAsync(20).ConfigureAwait(false);
            var next = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true,
                captureSequence: 61, reference: reference, prior: (1, 1)).ConfigureAwait(false);
            var newest = await ProduceLatestReadFixtureAsync(next, store, persistence, telemetry, gate).ConfigureAwait(false);

            var associations = await store.ReadOutputByArtifactIdAsync(newest.Artifact.SourceArtifactIds[1], token).ConfigureAwait(false);
            var context = await store.ReadOutputByArtifactIdAsync(newest.Artifact.SourceArtifactIds[3], token).ConfigureAwait(false);
            Assert.IsNotNull(associations);
            Assert.IsNotNull(context);
            var associationsPath = Path.Combine(root, associations.PayloadRelativePath);
            var missingPath = Path.Combine(root, "held-associations");
            var contextPath = Path.Combine(root, context.PayloadRelativePath);
            var contextSidecarPath = Path.Combine(root, context.SidecarRelativePath);
            var contextBytes = await File.ReadAllBytesAsync(contextPath, token).ConfigureAwait(false);
            var contextSidecar = await File.ReadAllBytesAsync(contextSidecarPath, token).ConfigureAwait(false);
            var reconciler = new DerivedProductReconciler(root, store);

            var damageNextLatestRead = 0;
            var interruptFallback = 0;
            var fallbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fallbackInterrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var holdSelectedRevalidation = false;
            var selectedRevalidationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSelectedRevalidation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CameraAgentRetainedAstrometry? selected = null;
            var wrapped = new Mock<ICameraAgentArtifactService>(MockBehavior.Strict);
            wrapped.Setup(value => value.OpenContentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(async (Guid id, CancellationToken cancellation) =>
                {
                    if (id == newest.ArtifactId && Interlocked.Exchange(ref damageNextLatestRead, 0) == 1)
                    {
                        if (mixedInputs)
                        {
                            File.Move(associationsPath, missingPath);
                            var corrupted = contextBytes.ToArray();
                            corrupted[0] ^= 1;
                            await File.WriteAllBytesAsync(contextPath, corrupted, cancellation).ConfigureAwait(false);
                            var reconciled = await reconciler.RunAsync(cancellation).ConfigureAwait(false);
                            Assert.AreEqual(1, reconciled.Missing);
                            Assert.AreEqual(1, reconciled.Quarantined);
                        }
                        else
                        {
                            var frame = selected!.Solution!.Assessment.Frame;
                            var paths = RawIngressFileStore.GetPaths(root, frame.ExposureStartUtc, frame.SourceArtifactId);
                            var stream = File.Open(paths.PayloadAbsolutePath, FileMode.Append, FileAccess.Write);
                            await using (stream.ConfigureAwait(false))
                                await stream.WriteAsync(new byte[1], cancellation).ConfigureAwait(false);
                            var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1);
                            var reconciled = await new RawIngressReconciler(root, journal)
                                .RunAsync(_ => ValueTask.FromResult(default(CaptureMaintenanceEntry)), cancellation).ConfigureAwait(false);
                            Assert.AreEqual(1, reconciled.Quarantined);
                        }
                    }
                    if (id == newest.ArtifactId && holdSelectedRevalidation)
                    {
                        // The exact reader already owns its raw-buffer permit when it opens the assessment.
                        selectedRevalidationStarted.TrySetResult();
                        await releaseSelectedRevalidation.Task.WaitAsync(cancellation).ConfigureAwait(false);
                    }
                    if (id == older.ArtifactId && Interlocked.Exchange(ref interruptFallback, 0) == 1)
                    {
                        fallbackStarted.TrySetResult();
                        try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellation).ConfigureAwait(false); }
                        finally { fallbackInterrupted.TrySetResult(); }
                    }
                    return await artifacts.OpenContentAsync(id, cancellation).ConfigureAwait(false);
                });

            using var reader = CreateUnpooledAstrometryReader(store, wrapped.Object);
            var service = CameraAgentOperatorUiServiceTests.CreateAuthorizedRetainedAstrometryService(reader);
            selected = (await service.GetRetainedAstrometryAsync(newest.Capture.CaptureId, token).ConfigureAwait(false)).Value;
            Assert.IsNotNull(selected);
            var latest = CameraAgentLatestAstrometry.FromVerified(selected);
            var earlier = CameraAgentLatestAstrometry.FromVerified(
                (await service.GetRetainedAstrometryAsync(older.Capture.CaptureId, token).ConfigureAwait(false)).Value!);
            Assert.IsNotNull(latest);
            Assert.IsNotNull(earlier);

            using var serializedReads = new SemaphoreSlim(1, 1);
            using var uiContext = new BunitContext();
            var ui = CurrentSkyPageTests.Configure(uiContext);
            ui.CurrentImageHandler = _ => ValueTask.FromResult(
                OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(CurrentSkyPageTests.AstrometryImage(selected)));
            var initialSelectedRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ui.AstrometryHandler = async (captureId, cancellation) =>
            {
                await serializedReads.WaitAsync(cancellation).ConfigureAwait(false);
                try
                {
                    holdSelectedRevalidation = !mixedInputs && initialSelectedRead.Task.IsCompleted;
                    return await service.GetRetainedAstrometryAsync(captureId, cancellation).ConfigureAwait(false);
                }
                finally
                {
                    holdSelectedRevalidation = false;
                    serializedReads.Release();
                    initialSelectedRead.TrySetResult();
                }
            };
            ui.LatestAstrometrySelectionHandler = async (cancellation, previous, selectedAssessment) =>
            {
                await initialSelectedRead.Task.WaitAsync(cancellation).ConfigureAwait(false);
                await serializedReads.WaitAsync(cancellation).ConfigureAwait(false);
                try
                {
                    if (Volatile.Read(ref damageNextLatestRead) == 1)
                    {
                        Assert.AreEqual(latest, previous);
                        Assert.AreEqual(new CameraAgentAstrometryAssessmentIdentity(
                            latest.CaptureId, latest.ArtifactId, latest.SolutionIdentitySha256), selectedAssessment);
                    }
                    return await service.GetLatestAcceptedAstrometryAsync(cancellation, previous, selectedAssessment).ConfigureAwait(false);
                }
                finally { serializedReads.Release(); }
            };
            var page = uiContext.Render<CurrentSkyPage>();
            page.WaitForAssertion(() =>
            {
                StringAssert.Contains(page.Find(".latest-measured-solution").TextContent, latest.SolutionIdentitySha256, StringComparison.Ordinal);
                Assert.IsFalse(page.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
            });
            await page.Find("form.pixel-sky-calculator").SubmitAsync().ConfigureAwait(false);
            Assert.IsNotEmpty(page.FindAll(".astrometry-calculator-result"));

            Volatile.Write(ref interruptFallback, mixedInputs ? 1 : 0);
            Volatile.Write(ref damageNextLatestRead, 1);
            var refresh = page.Find("button.refresh-link").ClickAsync();
            if (mixedInputs)
            {
                await fallbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                using var exactReadDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var exactOlder = await service.GetRetainedAstrometryAsync(older.Capture.CaptureId, exactReadDeadline.Token).ConfigureAwait(false);
                Assert.IsTrue(exactOlder.IsSuccess);
                Assert.AreEqual(earlier, CameraAgentLatestAstrometry.FromVerified(exactOlder.Value!));
                await fallbackInterrupted.Task.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
                await refresh.ConfigureAwait(false);
                page.WaitForAssertion(() =>
                {
                    StringAssert.Contains(page.Find(".latest-measured-solution").TextContent, "temporarily unavailable", StringComparison.Ordinal);
                    AssertSelectedFitCleared();
                }, TimeSpan.FromSeconds(5));
                await page.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
            }
            else
            {
                await refresh.ConfigureAwait(false);
            }

            page.WaitForAssertion(() =>
            {
                var summary = page.Find(".latest-measured-solution").TextContent;
                StringAssert.Contains(summary, earlier.SolutionIdentitySha256, StringComparison.Ordinal);
                StringAssert.Contains(summary, earlier.ArtifactId.ToString(), StringComparison.Ordinal);
                StringAssert.Contains(summary, earlier.MidpointUtc.ToString("O"), StringComparison.Ordinal);
                AssertSelectedFitCleared();
            }, TimeSpan.FromSeconds(5));
            using var verificationDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (!mixedInputs)
            {
                try
                {
                    await selectedRevalidationStarted.Task.WaitAsync(verificationDeadline.Token).ConfigureAwait(false);
                    var busy = await service.GetRetainedAstrometryAsync(older.Capture.CaptureId, verificationDeadline.Token).ConfigureAwait(false);
                    Assert.IsTrue(busy.IsSuccess);
                    Assert.AreEqual("Unavailable", busy.Value?.Availability);
                    Assert.AreEqual("read-capacity", busy.Value?.Reason);
                    Assert.IsNull(CameraAgentLatestAstrometry.FromVerified(busy.Value!));
                }
                finally { releaseSelectedRevalidation.TrySetResult(); }
            }

            // Rendering the fallback can precede completion of the new selected-frame read.
            await serializedReads.WaitAsync(verificationDeadline.Token).ConfigureAwait(false);
            try
            {
                var exactOlder = await service.GetRetainedAstrometryAsync(older.Capture.CaptureId, verificationDeadline.Token).ConfigureAwait(false);
                Assert.IsTrue(exactOlder.IsSuccess);
                Assert.AreEqual(earlier, CameraAgentLatestAstrometry.FromVerified(exactOlder.Value!),
                    $"Exact older fit unavailable: {exactOlder.Value?.Availability}/{exactOlder.Value?.Reason}");
            }
            finally { serializedReads.Release(); }

            if (mixedInputs)
            {
                File.Move(missingPath, associationsPath);
                await File.WriteAllBytesAsync(contextPath, contextBytes, token).ConfigureAwait(false);
                await File.WriteAllBytesAsync(contextSidecarPath, contextSidecar, token).ConfigureAwait(false);
                await store.SetOutputAvailabilityAsync(context.OutputIdentitySha256, "Available", null, token).ConfigureAwait(false);
                await reconciler.RunAsync(token).ConfigureAwait(false);

                await page.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
                page.WaitForAssertion(() => StringAssert.Contains(page.Find(".latest-measured-solution").TextContent,
                    latest.SolutionIdentitySha256, StringComparison.Ordinal), TimeSpan.FromSeconds(5));
                Assert.AreEqual(latest, CameraAgentLatestAstrometry.FromVerified(
                    (await service.GetRetainedAstrometryAsync(newest.Capture.CaptureId, token).ConfigureAwait(false)).Value!));
            }

            void AssertSelectedFitCleared()
            {
                Assert.IsFalse(page.Find(".latest-measured-solution").TextContent.Contains(latest.SolutionIdentitySha256, StringComparison.Ordinal));
                Assert.IsTrue(page.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
                Assert.IsEmpty(page.FindAll(".astrometry-calculator-result"));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
