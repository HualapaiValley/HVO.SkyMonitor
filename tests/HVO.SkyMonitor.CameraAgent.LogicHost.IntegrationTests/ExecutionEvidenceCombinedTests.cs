using System.Net;
using HVO.SkyMonitor.CameraAgent.Common.Evidence;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class ExecutionEvidenceCombinedTests
{
    private static CameraAgentIntegrationFixture Fixture => AssemblyHooks.Fixture;

    [TestMethod]
    public async Task LocalOriginRequiresOperatorBindingAndRealExporterAcknowledgesExactCanonicalBytes()
    {
        await using var edge = await ExecutionEvidenceCombinedHarness.CreateAsync(Fixture).ConfigureAwait(false);
        await edge.StopCaptureAndWaitForTerminalAsync().ConfigureAwait(false);
        await edge.RunCycleAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Forbidden, edge.Http.Exchanges.Last().Status);
        Assert.IsFalse(edge.ReadLocalRows().Any(row => row.Status == "acknowledged"));

        var central = await ExecutionEvidenceCentralWorkload.BindAsync(Fixture, edge).ConfigureAwait(false);
        await edge.DrainAsync().ConfigureAwait(false);
        var local = edge.ReadLocalRows();
        Assert.IsTrue(local.Any(row => row.Envelope.Execution is { Nodes.Length: > 0 }));
        Assert.IsTrue(local.Any(row => row.Envelope.GraphRevision is not null));
        Assert.IsTrue(local.Any(row => row.Envelope.Availability is not null));
        Assert.IsTrue(local.All(row => row.Status == "acknowledged"));
        AssertExactAcknowledgements(edge, local);
        await central.AssertPersistedAsync(local).ConfigureAwait(false);
        var negotiation = edge.Http.Exchanges.Last(exchange =>
            exchange.Route.EndsWith("/negotiate", StringComparison.Ordinal) && exchange.Status == HttpStatusCode.OK);
        var limits = GraphExecutionEvidenceJson.ParseNegotiationResponse(negotiation.Response).Value!.Limits;
        Assert.AreEqual(64, limits!.MaximumResyncUnits);
        Assert.AreEqual(256, limits.MaximumFactsPerFeedback); // Received/validated/accepted/acknowledged per unit.
        Assert.AreEqual(1, edge.Http.MaximumInFlight);
        var artifactBacklog = await edge.Agent.Services.GetRequiredService<IArtifactOutbox>()
            .GetSnapshotAsync(edge.Root, CancellationToken.None).ConfigureAwait(false);
        Assert.IsGreaterThan(0, artifactBacklog.PendingCount,
            "Evidence acknowledgements must not acknowledge the separate artifact upload lane.");
    }

    [TestMethod]
    public async Task MoreThan256PendingUnitsDrainAcrossExporterRestartWithOldOriginBytesPreserved()
    {
        await using var edge = await ExecutionEvidenceCombinedHarness.CreateAsync(Fixture).ConfigureAwait(false);
        await edge.StopCaptureAndWaitForTerminalAsync().ConfigureAwait(false);
        var central = await ExecutionEvidenceCentralWorkload.BindAsync(Fixture, edge).ConfigureAwait(false);
        edge.Http.Fault = EvidenceHttpFault.Unavailable;
        await edge.RunCycleAsync().ConfigureAwait(false);
        await edge.EnlistAvailabilityHistoryAsync(300, "restart").ConfigureAwait(false);
        var old = edge.ReadLocalRows();
        Assert.IsGreaterThan(256, old.Count);
        var oldOrigin = old[0].Envelope.Origin.IdentitySha256;
        edge.RestartExporter();
        edge.Http.Fault = EvidenceHttpFault.None;
        await edge.DrainAsync().ConfigureAwait(false);
        var final = edge.ReadLocalRows();
        foreach (var before in old)
        {
            var after = final.Single(row => row.Envelope.EvidenceId == before.Envelope.EvidenceId);
            CollectionAssert.AreEqual(before.Bytes, after.Bytes);
            Assert.AreEqual("acknowledged", after.Status);
        }
        var negotiatedOrigin = GraphExecutionEvidenceJson.ParseNegotiationRequest(
            edge.Http.Exchanges.Last(exchange => exchange.Route.EndsWith("/negotiate", StringComparison.Ordinal)).Request)
            .Value!.Origin.IdentitySha256;
        Assert.AreNotEqual(oldOrigin, negotiatedOrigin, "Restart negotiates a new boot but must drain the old origin.");
        var submits = Submissions(edge).ToArray();
        Assert.IsTrue(submits.Any(exchange => ExecutionEvidenceBatchCodec.Decode(exchange.Request, 64).Count == 64));
        Assert.IsTrue(submits.All(exchange => GraphExecutionEvidenceJson.ParseFeedback(exchange.Response).Value!.Facts.Length <= 256));
        AssertExactAcknowledgements(edge, final);
        await central.AssertPersistedAsync(final).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ResponseLostAfterReceiverCommitReplaysIdenticalBytesAndReceivesDuplicateFacts()
    {
        await using var edge = await ExecutionEvidenceCombinedHarness.CreateAsync(Fixture).ConfigureAwait(false);
        await edge.StopCaptureAndWaitForTerminalAsync().ConfigureAwait(false);
        var central = await ExecutionEvidenceCentralWorkload.BindAsync(Fixture, edge).ConfigureAwait(false);
        edge.Http.Fault = EvidenceHttpFault.LoseCommittedResponse;
        await edge.RunCycleAsync().ConfigureAwait(false);
        var first = Submissions(edge).Single();
        Assert.IsTrue(GraphExecutionEvidenceJson.ParseFeedback(first.Response).Value!.Facts
            .Any(fact => fact.Kind == ExecutionEvidenceFactKind.Acknowledged));
        Assert.IsTrue(edge.ReadLocalRows().Any(row => row.Status == "retry"));
        edge.RestartExporter();
        await edge.DrainAsync().ConfigureAwait(false);
        var replay = Submissions(edge).Skip(1).First();
        CollectionAssert.AreEqual(first.Request, replay.Request,
            "Loss after SQL commit must replay the exact sealed units, even across exporter restart.");
        Assert.IsTrue(GraphExecutionEvidenceJson.ParseFeedback(replay.Response).Value!.Facts
            .Where(fact => fact.Kind == ExecutionEvidenceFactKind.Acknowledged).All(fact => fact.Duplicate));
        AssertExactAcknowledgements(edge, edge.ReadLocalRows());
        await central.AssertPersistedAsync(edge.ReadLocalRows()).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WrongHashOrStaleOriginAcknowledgementCannotReleaseLocalRetention(bool staleOrigin)
    {
        await using var edge = await ExecutionEvidenceCombinedHarness.CreateAsync(Fixture).ConfigureAwait(false);
        await edge.StopCaptureAndWaitForTerminalAsync().ConfigureAwait(false);
        _ = await ExecutionEvidenceCentralWorkload.BindAsync(Fixture, edge).ConfigureAwait(false);
        edge.Http.Fault = staleOrigin ? EvidenceHttpFault.StaleOriginAcknowledgement : EvidenceHttpFault.WrongAcknowledgementHash;
        await edge.RunCycleAsync().ConfigureAwait(false);
        var before = edge.ReadLocalRows();
        Assert.IsNotEmpty(before);
        Assert.IsTrue(before.All(row => row.Status != "acknowledged"));
        Assert.IsTrue(before.Any(row => row.Status == (staleOrigin ? "retry" : "quarantined")));
        edge.Clock.Advance(TimeSpan.FromDays(10));
        await edge.Outbox.RetainAsync(edge.Root, TimeSpan.Zero, 0, null, CancellationToken.None).ConfigureAwait(false);
        var retained = edge.ReadLocalRows();
        CollectionAssert.AreEqual(before.Select(row => row.Envelope.PayloadSha256).ToArray(),
            retained.Select(row => row.Envelope.PayloadSha256).ToArray());
        if (staleOrigin)
        {
            edge.Http.Fault = EvidenceHttpFault.None;
            await edge.DrainAsync().ConfigureAwait(false);
            AssertExactAcknowledgements(edge, edge.ReadLocalRows());
        }
    }

    [TestMethod]
    public async Task DeniedEvidenceSinkCannotStopLiveCaptureArtifactUploadOrCentralPreview()
    {
        await using var edge = await ExecutionEvidenceCombinedHarness.CreateAsync(Fixture).ConfigureAwait(false);
        var central = await ExecutionEvidenceCentralWorkload.BindAsync(Fixture, edge).ConfigureAwait(false);
        await edge.WaitForRealExecutionAsync().ConfigureAwait(false);
        var telemetry = edge.Agent.Services.GetRequiredService<ICaptureTelemetryProvider>();
        var latestBefore = telemetry.GetSnapshot().Samples.Max(sample => sample.StartedUtc);
        edge.Http.Fault = EvidenceHttpFault.Unavailable;
        await edge.RunCycleAsync().ConfigureAwait(false);
        var raw = await central.UploadNextRawAsync().ConfigureAwait(false);
        var job = await central.ExecutePreviewAsync(raw).ConfigureAwait(false);
        Assert.AreEqual(raw.ArtifactId, job.SourceArtifactId);
        await ExecutionEvidenceCombinedHarness.WaitUntilAsync(() => Task.FromResult(
            telemetry.GetSnapshot().Samples.Count(sample => sample.StartedUtc > latestBefore && sample.FrameStored) >= 3),
            "three further live captures while the evidence sink denies requests").ConfigureAwait(false);
        await edge.RunCycleAsync().ConfigureAwait(false);
        var failed = await edge.Outbox.ReadBacklogAsync(edge.Root, CancellationToken.None).ConfigureAwait(false);
        Assert.IsGreaterThan(0, failed.PendingCount + failed.RetryCount);
        Assert.AreEqual(0, failed.QuarantinedCount);
        edge.Http.Fault = EvidenceHttpFault.None;
        // Stop only after the live independence assertions; this delimits a finite recovery backlog.
        await edge.StopCaptureAndWaitForTerminalAsync().ConfigureAwait(false);
        await edge.DrainAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task AnotherProvisionedDeviceCannotSubmitTheBoundOriginsEvidence()
    {
        await using var edge = await ExecutionEvidenceCombinedHarness.CreateAsync(Fixture).ConfigureAwait(false);
        await edge.StopCaptureAndWaitForTerminalAsync().ConfigureAwait(false);
        _ = await ExecutionEvidenceCentralWorkload.BindAsync(Fixture, edge).ConfigureAwait(false);
        await edge.DrainAsync().ConfigureAwait(false);
        await using var other = await Fixture.StartIsolatedCameraAgentAsync().ConfigureAwait(false);
        var row = edge.ReadLocalRows()[0];
        edge.Http.ModifyRequest = request =>
        {
            request.Headers.Remove("X-HVO-Device-Id");
            request.Headers.TryAddWithoutValidation("X-HVO-Device-Id", other.DeviceId);
        };
        var crossOrigin = await edge.Transport.SubmitAsync(row.Envelope.Origin.IdentitySha256, [row.Bytes],
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(ExecutionEvidenceTransportDisposition.AuthenticationBlocked, crossOrigin.Disposition);
        Assert.AreEqual(HttpStatusCode.Forbidden, edge.Http.Exchanges.Last().Status);
        edge.Http.ModifyRequest = request =>
        {
            request.Headers.Remove("X-HVO-Device-Key");
            request.Headers.TryAddWithoutValidation("X-HVO-Device-Key", "wrong-key");
        };
        var wrongKey = await edge.Transport.SubmitAsync(row.Envelope.Origin.IdentitySha256, [row.Bytes],
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(ExecutionEvidenceTransportDisposition.AuthenticationBlocked, wrongKey.Disposition);
        Assert.AreEqual(HttpStatusCode.Unauthorized, edge.Http.Exchanges.Last().Status);
    }

    [TestMethod]
    public async Task EvidenceBeforeActualArtifactUploadReconcilesLaterWithoutRewritingProducerFacts()
    {
        await using var edge = await ExecutionEvidenceCombinedHarness.CreateAsync(Fixture).ConfigureAwait(false);
        await edge.StopCaptureAndWaitForTerminalAsync().ConfigureAwait(false);
        var central = await ExecutionEvidenceCentralWorkload.BindAsync(Fixture, edge).ConfigureAwait(false);
        await edge.DrainAsync().ConfigureAwait(false);
        var sealedBeforeUpload = edge.ReadLocalRows();
        await central.AssertPersistedAsync(sealedBeforeUpload).ConfigureAwait(false);
        using (var scope = Fixture.CreateHostScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.AreEqual(0, await db.CentralArtifacts.CountAsync(item => item.DevicePublicId == edge.Agent.DevicePublicId)
                .ConfigureAwait(false), "An evidence receipt never uploads image bytes.");
            var ownReferences = await db.Set<ImportedExecutionEvidenceArtifact>()
                .Where(item => item.BindingId == central.Binding.Id).Select(item => item.Id).ToArrayAsync().ConfigureAwait(false);
            await db.Set<ExecutionEvidenceArtifactCheck>().Where(item => ownReferences.Contains(item.ArtifactReferenceId))
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.NextCheckAtUtc, DateTimeOffset.UnixEpoch))
                .ConfigureAwait(false);
            for (var batch = 0; batch <= ownReferences.Length / 32; batch++)
            {
                await scope.ServiceProvider.GetRequiredService<ExecutionEvidenceReconciler>()
                    .ReconcileAsync(32).ConfigureAwait(false);
            }
        }
        var raw = await central.UploadNextRawAsync().ConfigureAwait(false);
        using (var scope = Fixture.CreateHostScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var references = await db.Set<ImportedExecutionEvidenceArtifact>()
                .Where(item => item.BindingId == central.Binding.Id && item.ArtifactId == raw.ArtifactId)
                .Select(item => item.Id).ToArrayAsync().ConfigureAwait(false);
            Assert.IsNotEmpty(references, "The uploaded raw must be named by actual edge execution evidence.");
            var before = await db.Set<ImportedExecutionEvidenceObservation>().AsNoTracking()
                .Where(item => references.Contains(item.ArtifactReferenceId)).ToArrayAsync().ConfigureAwait(false);
            Assert.IsTrue(before.Any(item => item.State != "Available"));
            await db.Set<ExecutionEvidenceArtifactCheck>().Where(item => references.Contains(item.ArtifactReferenceId))
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.NextCheckAtUtc, DateTimeOffset.UnixEpoch))
                .ConfigureAwait(false);
            for (var batch = 0; batch <= references.Length / 32; batch++)
            {
                await scope.ServiceProvider.GetRequiredService<ExecutionEvidenceReconciler>()
                    .ReconcileAsync(32).ConfigureAwait(false);
            }
            var after = await db.Set<ImportedExecutionEvidenceObservation>().AsNoTracking()
                .Where(item => references.Contains(item.ArtifactReferenceId)).ToArrayAsync().ConfigureAwait(false);
            foreach (var reference in references)
            {
                var latest = after.Where(item => item.ArtifactReferenceId == reference).MaxBy(item => item.Id);
                Assert.IsNotNull(latest);
                Assert.AreEqual("Available", latest.State, latest.ReasonCode);
                Assert.IsNotNull(latest.ObjectGeneration);
            }
            Assert.IsTrue(before.All(old => after.Any(item => item.Id == old.Id && item.State == old.State &&
                item.ReasonCode == old.ReasonCode && item.ObservedAtUtc == old.ObservedAtUtc)),
                "Central availability is append-only and keeps the earlier missing-object observation.");
        }
        await central.AssertPersistedAsync(sealedBeforeUpload).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ReceiverSqlOutageReturnsRetryWhileNormalCaptureUploadAndProcessingContinue()
    {
        var unavailable = true;
        await using var receiver = Fixture.HostFixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ExecutionEvidenceDatabase>();
            services.AddScoped(provider =>
            {
                if (!Volatile.Read(ref unavailable))
                {
                    return new ExecutionEvidenceDatabase(provider.GetRequiredService<ApplicationDbContext>(),
                        provider.GetRequiredService<IOptions<ExecutionEvidenceOptions>>());
                }
                using var unreachable = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                    .UseSqlServer("Server=127.0.0.1,1;Database=issue538-unavailable;User Id=unavailable;Password=unavailable;TrustServerCertificate=True;Connect Timeout=1")
                    .Options);
                return new ExecutionEvidenceDatabase(unreachable, Options.Create(new ExecutionEvidenceOptions { SqlTimeoutSeconds = 1 }));
            });
        }));
        using var receiverHttp = new HttpMessageInvoker(receiver.Server.CreateHandler());
        await using var edge = await ExecutionEvidenceCombinedHarness.CreateAsync(Fixture).ConfigureAwait(false);
        var central = await ExecutionEvidenceCentralWorkload.BindAsync(Fixture, edge).ConfigureAwait(false);
        await edge.WaitForRealExecutionAsync().ConfigureAwait(false);
        edge.Http.SendOverride = receiverHttp.SendAsync;
        var telemetry = edge.Agent.Services.GetRequiredService<ICaptureTelemetryProvider>();
        var started = telemetry.GetSnapshot().Samples.Max(sample => sample.StartedUtc);
        await edge.RunCycleAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, edge.Http.Exchanges.Last().Status);
        Assert.IsTrue(edge.ReadLocalRows().All(row => row.Status != "acknowledged" && row.Status != "quarantined"));
        var raw = await central.UploadNextRawAsync().ConfigureAwait(false);
        _ = await central.ExecutePreviewAsync(raw).ConfigureAwait(false);
        await ExecutionEvidenceCombinedHarness.WaitUntilAsync(() => Task.FromResult(
            telemetry.GetSnapshot().Samples.Count(sample => sample.StartedUtc > started && sample.FrameStored) >= 3),
            "live capture during receiver-only SQL connection failure").ConfigureAwait(false);
        Volatile.Write(ref unavailable, false);
        await edge.StopCaptureAndWaitForTerminalAsync().ConfigureAwait(false);
        await edge.DrainAsync().ConfigureAwait(false);
        await central.AssertPersistedAsync(edge.ReadLocalRows()).ConfigureAwait(false);
    }

    internal static IEnumerable<EvidenceHttpExchange> Submissions(ExecutionEvidenceCombinedHarness edge)
        => edge.Http.Exchanges.Where(exchange => exchange.Route == "/api/device/execution-evidence" &&
            exchange.Status == HttpStatusCode.OK);

    internal static void AssertExactAcknowledgements(ExecutionEvidenceCombinedHarness edge, IReadOnlyList<EdgeEvidenceRow> rows)
    {
        var facts = Submissions(edge).SelectMany(exchange =>
            GraphExecutionEvidenceJson.ParseFeedback(exchange.Response).Value!.Facts).ToArray();
        foreach (var row in rows.Where(row => row.Status == "acknowledged"))
        {
            Assert.AreEqual(row.Envelope.PayloadSha256, GraphExecutionEvidenceJson.ComputeCanonicalPayloadSha256(row.Envelope));
            CollectionAssert.AreEqual(row.Bytes, GraphExecutionEvidenceJson.Serialize(row.Envelope));
            Assert.IsTrue(facts.Any(fact => fact.Kind == ExecutionEvidenceFactKind.Acknowledged &&
                fact.EvidenceId == row.Envelope.EvidenceId && fact.OriginSequence == row.Envelope.OriginSequence &&
                fact.PayloadSha256 == row.Envelope.PayloadSha256));
        }
    }
}
