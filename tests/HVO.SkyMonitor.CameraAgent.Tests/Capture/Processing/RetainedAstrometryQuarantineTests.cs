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
using HVO.SkyMonitor.CameraAgent.Common.Silo;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    [TestMethod]
    [TestCategory("Integration")]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public async Task RetainedAstrometry_ReconciledQuarantineCompletesLatestRead(bool hasOlder, bool quarantineInput)
    {
        var root = CreateTestRoot();
        try
        {
            var token = CancellationToken.None;
            var fixture = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = SiloInfrastructureFixture.OpenProcessingStore(fixture.Fixture.Options);
            using var gate = new CaptureAstrometryExecutionGate();
            var persistence = CreatePersistence(fixture.Fixture.Options, store, telemetry);
            var latestOutput = await ProduceLatestReadFixtureAsync(fixture, store, persistence, telemetry, gate).ConfigureAwait(false);
            DurableProcessingOutput? olderOutput = null;
            if (hasOlder)
            {
                olderOutput = latestOutput;
                var reference = await MeasuredAstrometryFixture.CreateAtAsync(20).ConfigureAwait(false);
                var next = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true,
                    captureSequence: 61, reference: reference, prior: (1, 1)).ConfigureAwait(false);
                latestOutput = await ProduceLatestReadFixtureAsync(next, store, persistence, telemetry, gate).ConfigureAwait(false);
            }

            var captureId = latestOutput.Capture.CaptureId;
            var target = latestOutput;
            if (quarantineInput)
            {
                var contextId = latestOutput.Artifact.SourceArtifactIds[3];
                target = (await store.ReadOutputByArtifactIdAsync(contextId, token).ConfigureAwait(false))!;
            }

            var member = await SiloInfrastructureFixture.OpenMemberFixtureAsync(root, target, token).ConfigureAwait(false);
            using var artifacts = SiloInfrastructureFixture.OpenArtifactReader(fixture.Fixture.Options, store);
            var intermittent = new Mock<ICameraAgentArtifactService>(MockBehavior.Strict);
            var failNextOpen = false;
            var faults = 0;
            intermittent.Setup(service => service.OpenContentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(async (Guid artifactId, CancellationToken cancellationToken) =>
                {
                    if (!failNextOpen || artifactId != target.ArtifactId)
                        return await artifacts.OpenContentAsync(artifactId, cancellationToken).ConfigureAwait(false);

                    failNextOpen = false;
                    member.SetUnavailable(true);
                    try
                    {
                        var result = await artifacts.OpenContentAsync(artifactId, cancellationToken).ConfigureAwait(false);
                        Assert.AreEqual(CameraAgentArtifactReadStatus.Unavailable, result.Status);
                        Assert.IsNull(result.Content);
                        faults++;
                        return result;
                    }
                    finally { member.SetUnavailable(false); }
                });

            using var service = CreateUnpooledAstrometryReader(store, intermittent.Object);
            ICameraAgentRetainedAstrometryService reader = service;
            var accepted = await reader.ReadLatestAcceptedAsync(token).ConfigureAwait(false);
            Assert.IsTrue(accepted.Completed);
            Assert.AreEqual(captureId, accepted.Summary?.CaptureId);

            failNextOpen = true;
            Assert.AreEqual(new CameraAgentLatestAstrometryRead(false),
                await reader.ReadLatestAcceptedAsync(token).ConfigureAwait(false),
                "A one-shot physical open failure must not replace or clear the last verified fit.");
            Assert.AreEqual(1, faults);
            Assert.AreEqual(accepted, await reader.ReadLatestAcceptedAsync(token).ConfigureAwait(false));

            await member.RecordCopyStateAsync(SiloAvailabilityState.Missing, token).ConfigureAwait(false);
            Assert.AreEqual(new CameraAgentLatestAstrometryRead(false),
                await reader.ReadLatestAcceptedAsync(token).ConfigureAwait(false));

            await member.RecordCopyStateAsync(SiloAvailabilityState.Restored, token).ConfigureAwait(false);
            Assert.AreEqual(accepted, await reader.ReadLatestAcceptedAsync(token).ConfigureAwait(false));

            member.FlipByte();
            await member.RecordCopyStateAsync(SiloAvailabilityState.Quarantined, token).ConfigureAwait(false);

            var expectedCapture = olderOutput?.Capture.CaptureId;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var latest = await reader.ReadLatestAcceptedAsync(token).ConfigureAwait(false);
                Assert.IsTrue(latest.Completed, "A durably quarantined fit must not leave the newest-result search pending.");
                Assert.AreEqual(expectedCapture, latest.Summary?.CaptureId);
            }

            var selected = await reader.GetAsync(captureId, token).ConfigureAwait(false);
            Assert.AreEqual("Unavailable", selected.Availability);
            Assert.AreEqual("retained-evidence-invalid", selected.Reason);
            Assert.IsNull(selected.Solution);

            using var restartedStore = SiloInfrastructureFixture.OpenProcessingStore(fixture.Fixture.Options);
            using var restartedArtifacts = SiloInfrastructureFixture.OpenArtifactReader(fixture.Fixture.Options, restartedStore);
            using var restartedReader = new CameraAgentRetainedAstrometryService(restartedStore, restartedArtifacts);
            var restarted = await restartedReader.ReadLatestAcceptedAsync(token).ConfigureAwait(false);
            Assert.IsTrue(restarted.Completed);
            Assert.AreEqual(expectedCapture, restarted.Summary?.CaptureId);

            if (olderOutput is not null)
            {
                var olderMember = await SiloInfrastructureFixture.OpenMemberFixtureAsync(root, olderOutput, token).ConfigureAwait(false);
                await olderMember.ExpireAsync(token).ConfigureAwait(false);
                Assert.AreEqual(new CameraAgentLatestAstrometryRead(true),
                    await restartedReader.ReadLatestAcceptedAsync(token).ConfigureAwait(false));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [DataRow("assessment")]
    [DataRow("context")]
    [DataRow("raw")]
    public async Task CurrentSkyHidesQuarantinedFitWhileEarlierFitIsTemporarilyUnreadable(string quarantinedSource)
    {
        var root = CreateTestRoot();
        try
        {
            var token = CancellationToken.None;
            var fixture = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = SiloInfrastructureFixture.OpenProcessingStore(fixture.Fixture.Options);
            using var gate = new CaptureAstrometryExecutionGate();
            var persistence = CreatePersistence(fixture.Fixture.Options, store, telemetry);
            var older = await ProduceLatestReadFixtureAsync(fixture, store, persistence, telemetry, gate).ConfigureAwait(false);
            var reference = await MeasuredAstrometryFixture.CreateAtAsync(20).ConfigureAwait(false);
            var next = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true,
                captureSequence: 61, reference: reference, prior: (1, 1)).ConfigureAwait(false);
            var newest = await ProduceLatestReadFixtureAsync(next, store, persistence, telemetry, gate).ConfigureAwait(false);

            using var artifacts = SiloInfrastructureFixture.OpenArtifactReader(fixture.Fixture.Options, store);
            using var reader = new CameraAgentRetainedAstrometryService(store, artifacts);
            var service = CameraAgentOperatorUiServiceTests.CreateAuthorizedRetainedAstrometryService(reader);
            var evidence = (await service.GetRetainedAstrometryAsync(newest.Capture.CaptureId, token).ConfigureAwait(false)).Value!;
            var latest = CameraAgentLatestAstrometry.FromVerified(evidence)!;
            var earlier = CameraAgentLatestAstrometry.FromVerified(
                (await service.GetRetainedAstrometryAsync(older.Capture.CaptureId, token).ConfigureAwait(false)).Value!)!;
            Assert.AreEqual(newest.ArtifactId, latest.ArtifactId);
            Assert.AreEqual(older.ArtifactId, earlier.ArtifactId);
            Assert.IsTrue(earlier.MidpointUtc < latest.MidpointUtc);

            using var serializedReads = new SemaphoreSlim(1, 1);
            using var context = new BunitContext();
            var ui = CurrentSkyPageTests.Configure(context);
            ui.CurrentImageHandler = _ => ValueTask.FromResult(
                OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(CurrentSkyPageTests.AstrometryImage(evidence)));
            var initialSelectedRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // The two optional page reads share a raw reader. Control their ordering so read-capacity
            // contention does not mask the older artifact's deliberately injected access failure.
            ui.AstrometryHandler = async (captureId, cancellation) =>
            {
                await serializedReads.WaitAsync(cancellation).ConfigureAwait(false);
                try { return await service.GetRetainedAstrometryAsync(captureId, cancellation).ConfigureAwait(false); }
                finally
                {
                    serializedReads.Release();
                    initialSelectedRead.TrySetResult();
                }
            };
            ui.LatestAstrometryHandler = async (cancellation, previous) =>
            {
                await initialSelectedRead.Task.WaitAsync(cancellation).ConfigureAwait(false);
                await serializedReads.WaitAsync(cancellation).ConfigureAwait(false);
                try { return await service.GetLatestAcceptedAstrometryAsync(cancellation, previous).ConfigureAwait(false); }
                finally { serializedReads.Release(); }
            };
            var page = context.Render<CurrentSkyPage>();
            page.WaitForAssertion(() =>
            {
                var summary = page.Find(".latest-measured-solution").TextContent;
                StringAssert.Contains(summary, "For the selected capture", StringComparison.Ordinal);
                StringAssert.Contains(summary, latest.SolutionIdentitySha256, StringComparison.Ordinal);
                Assert.IsFalse(page.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
            });

            var newestMember = await SiloInfrastructureFixture.OpenMemberFixtureAsync(root, newest, token).ConfigureAwait(false);
            newestMember.SetUnavailable(true);
            try
            {
                await page.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
                page.WaitForAssertion(() =>
                {
                    var summary = page.Find(".latest-measured-solution").TextContent;
                    StringAssert.Contains(summary, "Showing the previously verified result", StringComparison.Ordinal);
                    StringAssert.Contains(summary, latest.SolutionIdentitySha256, StringComparison.Ordinal);
                    Assert.IsFalse(page.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
                });
            }

            finally { newestMember.SetUnavailable(false); }
            if (quarantinedSource == "raw")
            {
                var frame = evidence.Solution!.Assessment.Frame;
                var paths = RawIngressFileStore.GetPaths(root, frame.ExposureStartUtc, frame.SourceArtifactId);
                var payload = File.Open(paths.PayloadAbsolutePath, FileMode.Append, FileAccess.Write);
                await using (payload.ConfigureAwait(false))
                    await payload.WriteAsync(new byte[1], token).ConfigureAwait(false);
                var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1);
                var reconciliation = await new RawIngressReconciler(root, journal)
                    .RunAsync(_ => ValueTask.FromResult(default(CaptureMaintenanceEntry)), token).ConfigureAwait(false);
                Assert.AreEqual(1, reconciliation.Quarantined);
                Assert.IsFalse(File.Exists(paths.PayloadAbsolutePath));
                var rawRead = await artifacts.OpenContentAsync(frame.SourceArtifactId, token).ConfigureAwait(false);
                Assert.AreEqual(CameraAgentArtifactReadStatus.Conflict, rawRead.Status);
                Assert.IsNull(rawRead.Content);
            }
            else
            {
                var target = quarantinedSource == "context"
                    ? (await store.ReadOutputByArtifactIdAsync(newest.Artifact.SourceArtifactIds[3], token).ConfigureAwait(false))!
                    : newest;
                var damaged = await SiloInfrastructureFixture.OpenMemberFixtureAsync(root, target, token).ConfigureAwait(false);
                damaged.FlipByte();
                await damaged.RecordCopyStateAsync(SiloAvailabilityState.Quarantined, token).ConfigureAwait(false);
            }
            Assert.AreEqual("retained-evidence-invalid",
                (await reader.GetAsync(latest.CaptureId, token).ConfigureAwait(false)).Reason);

            var fallback = await SiloInfrastructureFixture.OpenMemberFixtureAsync(root, older, token).ConfigureAwait(false);
            var missingFallback = quarantinedSource == "assessment";
            if (missingFallback)
                await fallback.RecordCopyStateAsync(SiloAvailabilityState.Missing, token).ConfigureAwait(false);
            else
                fallback.SetUnavailable(true);
            try
            {
                var fault = await artifacts.OpenContentAsync(older.ArtifactId, token).ConfigureAwait(false);
                Assert.AreEqual(CameraAgentArtifactReadStatus.Unavailable, fault.Status);
                Assert.IsNull(fault.Content);
                var pending = await service.GetLatestAcceptedAstrometryAsync(token, latest).ConfigureAwait(false);
                Assert.AreEqual(OperatorUiResultKind.Unavailable, pending.Kind);

                await page.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
                page.WaitForAssertion(() =>
                {
                    var summary = page.Find(".latest-measured-solution").TextContent;
                    StringAssert.Contains(summary, "temporarily unavailable", StringComparison.Ordinal);
                    Assert.IsFalse(summary.Contains(latest.SolutionIdentitySha256, StringComparison.Ordinal),
                        $"The page still displays the known-invalid fit while fallback is temporarily unavailable. Displayed: {summary}");
                    Assert.IsEmpty(page.FindAll(".latest-measured-solution time"));
                    Assert.IsTrue(page.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
                }, TimeSpan.FromSeconds(5));
            }
            finally { fallback.SetUnavailable(false); }
            if (quarantinedSource == "context")
            {
                using var cancellation = new CancellationTokenSource();
                var interrupted = new Mock<ICameraAgentArtifactService>(MockBehavior.Strict);
                interrupted.Setup(value => value.OpenContentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                    .Returns(async (Guid id, CancellationToken ct) =>
                    {
                        if (id != earlier.ArtifactId) return await artifacts.OpenContentAsync(id, ct).ConfigureAwait(false);
                        await cancellation.CancelAsync().ConfigureAwait(false);
                        return await ValueTask.FromCanceled<CameraAgentArtifactContentResult>(ct).ConfigureAwait(false);
                    });
                using var deadlineReader = CreateUnpooledAstrometryReader(store, interrupted.Object);
                var deadline = await deadlineReader.ReadLatestAcceptedAsync(latest, cancellation.Token).ConfigureAwait(false);
                Assert.IsTrue(cancellation.IsCancellationRequested);
                Assert.IsFalse(deadline.Completed);
                Assert.IsTrue(deadline.PreviousSummaryQuarantined);
                Assert.IsNull(deadline.Summary);
            }

            if (missingFallback)
                await fallback.RecordCopyStateAsync(SiloAvailabilityState.Restored, token).ConfigureAwait(false);

            await page.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
            page.WaitForAssertion(() =>
            {
                var summary = page.Find(".latest-measured-solution").TextContent;
                StringAssert.Contains(summary, "For a different capture", StringComparison.Ordinal);
                StringAssert.Contains(summary, earlier.SolutionIdentitySha256, StringComparison.Ordinal);
                StringAssert.Contains(summary, earlier.ArtifactId.ToString(), StringComparison.Ordinal);
                StringAssert.Contains(summary, earlier.MidpointUtc.ToString("O"), StringComparison.Ordinal);
                Assert.IsTrue(page.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
            }, TimeSpan.FromSeconds(5));

            fallback.SetUnavailable(true);
            try
            {
                await page.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
                page.WaitForAssertion(() =>
                {
                    var summary = page.Find(".latest-measured-solution").TextContent;
                    StringAssert.Contains(summary, "Showing the previously verified result", StringComparison.Ordinal);
                    StringAssert.Contains(summary, earlier.SolutionIdentitySha256, StringComparison.Ordinal);
                }, TimeSpan.FromSeconds(5));
            }
            finally
            {
                fallback.SetUnavailable(false);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
