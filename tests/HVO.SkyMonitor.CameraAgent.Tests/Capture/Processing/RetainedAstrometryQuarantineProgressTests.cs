using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    [TestMethod]
    [TestCategory("Integration")]
    public async Task RetainedAstrometry_ConsecutiveDeadlinesAdvancePastConfirmedQuarantines()
    {
        var root = CreateTestRoot();
        try
        {
            var token = CancellationToken.None;
            var first = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(first.Fixture.Options);
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            using var gate = new CaptureAstrometryExecutionGate();
            var persistence = CreatePersistence(first.Fixture.Options, store, storage, telemetry);
            var oldest = await ProduceLatestReadFixtureAsync(first, store, persistence, telemetry, gate).ConfigureAwait(false);
            var solutions = new List<DurableProcessingOutput> { oldest };
            for (var index = 1; index <= 3; index++)
            {
                var reference = await MeasuredAstrometryFixture.CreateAtAsync(index * 20).ConfigureAwait(false);
                var sequence = 1 + index * 60;
                var next = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true,
                    captureSequence: sequence, reference: reference, prior: (sequence - 60, index)).ConfigureAwait(false);
                solutions.Add(await ProduceLatestReadFixtureAsync(next, store, persistence, telemetry, gate).ConfigureAwait(false));
            }

            using var artifacts = new CameraAgentArtifactService(first.Fixture.Options, store, new CameraAgentPreviewEncoder());
            using var selectedReader = new CameraAgentRetainedAstrometryService(store, artifacts);
            var previousSummaries = new List<CameraAgentLatestAstrometry>();
            foreach (var solution in solutions)
            {
                var retained = await selectedReader.GetAsync(solution.Capture.CaptureId, token).ConfigureAwait(false);
                var summary = CameraAgentLatestAstrometry.FromVerified(retained);
                Assert.IsNotNull(summary);
                previousSummaries.Add(summary);
            }
            var expectedOld = previousSummaries[0];

            // Both context rows remain durably quarantined. The newest assessment is quarantined directly.
            List<DurableProcessingOutput> targets = [];
            foreach (var solution in solutions.Skip(1).Take(2))
                targets.Add((await store.ReadOutputByArtifactIdAsync(solution.Artifact.SourceArtifactIds[3], token).ConfigureAwait(false))!);
            targets.Add(solutions[3]);
            var savedPayloads = new Dictionary<Guid, byte[]>();
            var savedSidecars = new Dictionary<Guid, byte[]>();
            foreach (var target in targets)
            {
                var path = Path.Combine(root, target.PayloadRelativePath);
                var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
                savedPayloads.Add(target.ArtifactId, bytes);
                savedSidecars.Add(target.ArtifactId, await File.ReadAllBytesAsync(Path.Combine(root, target.SidecarRelativePath), token).ConfigureAwait(false));
                var corrupted = (byte[])bytes.Clone();
                corrupted[0] ^= 1;
                await File.WriteAllBytesAsync(path, corrupted, token).ConfigureAwait(false);
            }
            var reconciliation = await new DerivedProductReconciler(root, store).RunAsync(token).ConfigureAwait(false);
            Assert.AreEqual(3, reconciliation.Quarantined);
            foreach (var target in targets)
            {
                var persisted = await store.ReadOutputByArtifactIdAsync(target.ArtifactId, token).ConfigureAwait(false);
                Assert.AreEqual("Quarantined", persisted?.AvailabilityState);
                Assert.AreEqual("payload-checksum-mismatch", persisted?.AvailabilityReason);
            }

            var delayed = new Mock<ICameraAgentArtifactService>(MockBehavior.Strict);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var probes = new List<Guid>();
            var solutionIds = solutions.Select(static output => output.ArtifactId).ToHashSet();
            var pause = true;
            var failOldestOpen = false;
            var temporaryFailures = 0;
            delayed.Setup(service => service.OpenContentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(async (Guid artifactId, CancellationToken requestToken) =>
                {
                    if (pause && requestToken.CanBeCanceled && solutionIds.Contains(artifactId))
                    {
                        probes.Add(artifactId);
                        if (probes.Count == 2)
                        {
                            entered.TrySetResult();
                            await Task.Delay(Timeout.InfiniteTimeSpan, requestToken).ConfigureAwait(false);
                        }
                    }
                    if (!failOldestOpen || artifactId != oldest.ArtifactId)
                        return await artifacts.OpenContentAsync(artifactId, requestToken).ConfigureAwait(false);

                    failOldestOpen = false;
                    using var exclusive = File.Open(Path.Combine(root, oldest.PayloadRelativePath), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    var result = await artifacts.OpenContentAsync(artifactId, requestToken).ConfigureAwait(false);
                    Assert.AreEqual(CameraAgentArtifactReadStatus.Unavailable, result.Status);
                    temporaryFailures++;
                    return result;
                });

            using var service = new CameraAgentRetainedAstrometryService(store, delayed.Object);
            ICameraAgentRetainedAstrometryService reader = service;
            var attempts = new[]
            {
                new[] { solutions[2].ArtifactId, solutions[1].ArtifactId },
                new[] { solutions[1].ArtifactId, oldest.ArtifactId },
            };
            for (var attempt = 0; attempt < attempts.Length; attempt++)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                probes.Clear();
                entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var previous = previousSummaries[3 - attempt];
                var pending = reader.ReadLatestAcceptedAsync(previous, deadline.Token).AsTask();
                await entered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
                var exact = await reader.GetAsync(oldest.Capture.CaptureId, token).ConfigureAwait(false);
                Assert.AreEqual(AstrometricAssessmentStatus.Accepted, exact.Solution?.Assessment.Status);
                deadline.CancelAfter(TimeSpan.Zero);
                var expired = await pending.ConfigureAwait(false);
                Assert.IsFalse(expired.Completed);
                Assert.IsTrue(expired.PreviousSummaryQuarantined, $"Deadline {attempt + 1} must carry the previous fit's known quarantine.");
                Assert.IsNull(expired.Summary);
                CollectionAssert.AreEqual(attempts[attempt], probes, $"Deadline {attempt + 1} must continue at the next unread assessment.");
            }

            pause = false;
            failOldestOpen = true;
            var incomplete = await reader.ReadLatestAcceptedAsync(previousSummaries[2], token).ConfigureAwait(false);
            Assert.IsFalse(incomplete.Completed);
            Assert.IsTrue(incomplete.PreviousSummaryQuarantined);
            Assert.AreEqual(1, temporaryFailures);
            Assert.AreEqual(expectedOld, (await reader.ReadLatestAcceptedAsync(token).ConfigureAwait(false)).Summary);

            await store.SetOutputAvailabilityAsync(oldest.OutputIdentitySha256, "Missing", "fixture-missing", token).ConfigureAwait(false);
            foreach (var previous in new[]
            {
                expectedOld,
                previousSummaries[2] with { ArtifactId = oldest.ArtifactId },
                previousSummaries[2] with { SolutionIdentitySha256 = expectedOld.SolutionIdentitySha256 },
            })
            {
                var missing = await reader.ReadLatestAcceptedAsync(previous, token).ConfigureAwait(false);
                Assert.IsFalse(missing.Completed);
                Assert.IsFalse(missing.PreviousSummaryQuarantined);
            }
            await store.RestoreMissingOutputAvailableAsync(oldest.OutputIdentitySha256, token).ConfigureAwait(false);

            // Restore a required input with its exact previously committed bytes and make it available.
            // Its assessment identity is unchanged, so invalidation must include the dependent output.
            var repaired = targets[1];
            await File.WriteAllBytesAsync(Path.Combine(root, repaired.PayloadRelativePath), savedPayloads[repaired.ArtifactId], token).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(root, repaired.SidecarRelativePath), savedSidecars[repaired.ArtifactId], token).ConfigureAwait(false);
            await store.SetOutputAvailabilityAsync(repaired.OutputIdentitySha256, "Available", null, token).ConfigureAwait(false);
            await store.SetOutputAvailabilityAsync(repaired.OutputIdentitySha256, "Missing", "fixture-missing", token).ConfigureAwait(false);
            var awaitingInput = await reader.ReadLatestAcceptedAsync(previousSummaries[2], token).ConfigureAwait(false);
            Assert.IsFalse(awaitingInput.Completed);
            Assert.IsFalse(awaitingInput.PreviousSummaryQuarantined);
            await store.RestoreMissingOutputAvailableAsync(repaired.OutputIdentitySha256, token).ConfigureAwait(false);
            var recovered = await reader.ReadLatestAcceptedAsync(token).ConfigureAwait(false);
            Assert.IsTrue(recovered.Completed);
            Assert.AreEqual(previousSummaries[2], recovered.Summary);

            // A fresh store must find that same fit through the unchanged cold seed.
            using var restartedStore = new SqliteCaptureProcessingStore(first.Fixture.Options);
            using var restartedArtifacts = new CameraAgentArtifactService(first.Fixture.Options, restartedStore, new CameraAgentPreviewEncoder());
            using var restartedReader = new CameraAgentRetainedAstrometryService(restartedStore, restartedArtifacts);
            Assert.AreEqual(recovered.Summary, (await restartedReader.ReadLatestAcceptedAsync(token).ConfigureAwait(false)).Summary);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
