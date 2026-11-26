using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests.Silo;

public sealed partial class SiloStockStoreTests
{
    private static async Task<RawIngressJournalEntry> InterruptRawReclamationAsync(IServiceProvider services,
        SiloInstalledStorage storage, SqliteRawCaptureJournal journal, ReconstructionDescriptor template,
        InterruptRawRecovery fault, CancellationToken token)
    {
        var ingress = services.GetRequiredService<RawCaptureIngress>();
        var config = await services.GetRequiredService<ICameraAgentConfigurationLoader>().LoadAsync(token).ConfigureAwait(false);
        var capture = await ingress.AcceptAsync(config, MakeRawSubmission(template, RecoveryTimestamp(template)), token).ConfigureAwait(false);
        Assert.IsNotNull(capture);
        var id = capture.Manifest.Descriptor.Artifact.ArtifactId;
        fault.InterruptPublication(SiloFaultPoint.AfterSegmentPut);
        Assert.AreEqual("silo-raw-admission-failed", (await ingress.PublishSiloAdmissionsAsync(16, token).ConfigureAwait(false)).Status);
        File.Delete(capture.StoredFrame.AbsolutePath);
        ingress.InvalidateEvidence();
        Assert.IsFalse(await ((IRawCaptureIngress)ingress).InitializeForOperationsAsync(token).ConfigureAwait(false));
        fault.InterruptReclamation();
        var interrupted = await ingress.PublishSiloAdmissionsAsync(16, token).ConfigureAwait(false);
        Assert.AreEqual("silo-raw-admission-failed", interrupted.Status);
        var row = await journal.ReadSiloAdmissionByArtifactAsync(id, token).ConfigureAwait(false);
        Assert.IsNotNull(row);
        Assert.AreEqual("missing_evidence", row.State);
        Assert.IsTrue(await journal.HasPendingSiloAdmissionAsync(row.CaptureId, token).ConfigureAwait(false));
        var group = await storage.State.ReadGroupAsync(row.CaptureId.ToString("N"), "raw-1", token).ConfigureAwait(false);
        Assert.AreEqual(SiloStagedState.Visible, group?.State);
        Assert.IsFalse(File.Exists(capture.StoredFrame.AbsolutePath));
        Assert.IsFalse(File.Exists(Path.ChangeExtension(capture.StoredFrame.AbsolutePath, ".json")));
        var intent = ReclaimIntentPath(storage.PhysicalState.Resolve("raw-admission-intents"), id);
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(intent, token).ConfigureAwait(false));
        Assert.IsTrue(document.RootElement.GetProperty("Reclaim").GetBoolean());
        Assert.IsTrue(document.RootElement.GetProperty("SiloVisible").GetBoolean());
        return row;
    }

    private static async Task AssertStartupReclaimsRawAsync(Dictionary<string, string?> settings, string root,
        RawIngressJournalEntry row, SqliteRawCaptureJournal journal, ReconstructionDescriptor template, CancellationToken token)
    {
        var fault = new InterruptRawRecovery();
        using var restarted = new CameraAgentIntegrationFixture { KeepStorageOnDispose = true };
        await restarted.InitializeAsync(services =>
        {
            // Isolate raw-ingress startup from the background publication sweep.
            services.RemoveAll<IHostedService>();
            services.AddSingleton<ISiloFaults>(fault);
            services.AddSingleton<IRawIngressFaultInjector>(fault);
        }, settings, root).ConfigureAwait(false);
        using var scope = restarted.CreateCameraAgentScope();
        var services = scope.ServiceProvider;
        var storage = services.GetRequiredService<SiloInstalledStorage>();
        var ingress = services.GetRequiredService<RawCaptureIngress>();
        var config = await services.GetRequiredService<ICameraAgentConfigurationLoader>().LoadAsync(token).ConfigureAwait(false);
        services.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(config);
        await storage.VerifyReadinessAsync(token).ConfigureAwait(false);
        await ingress.InitializeAsync(token).ConfigureAwait(false);
        Assert.IsFalse(await journal.HasPendingSiloAdmissionAsync(row.CaptureId, token).ConfigureAwait(false));
        Assert.IsFalse(File.Exists(ReclaimIntentPath(storage.PhysicalState.Resolve("raw-admission-intents"), row.ArtifactId)));
        Assert.IsTrue((await storage.Catalog.ReopenAsync(row.CaptureId.ToString("N"), "raw-1", token).ConfigureAwait(false)).Reopenable);
        await AssertRecoveredRawAsync(row.ArtifactId, token).ConfigureAwait(false);
        await AssertLiveAdmissionAsync(token).ConfigureAwait(false);

        await AssertCommittedRawRecoversAsync("written-off", SiloFaultPoint.AfterSegmentPut, token).ConfigureAwait(false);
        await AssertCommittedRawRecoversAsync("written-off", SiloFaultPoint.AfterSegmentCommitted, token).ConfigureAwait(false);
        await AssertCommittedRawRecoversAsync("missing_evidence", SiloFaultPoint.AfterSegmentPut, token).ConfigureAwait(false);
        await AssertCommittedRawRecoversAsync("missing_evidence", SiloFaultPoint.AfterSegmentCommitted, token).ConfigureAwait(false);
        await AssertCommittedRawRecoversAsync("quarantined", SiloFaultPoint.AfterSegmentPut, token).ConfigureAwait(false);
        await AssertCommittedRawRecoversAsync("quarantined", SiloFaultPoint.AfterSegmentCommitted, token).ConfigureAwait(false);
        await AssertQuarantinedWriteOffReclaimsAsync(services, storage, journal, template, root, fault, token).ConfigureAwait(false);
        await AssertRawPublicationIsolatesFailuresAsync(services, storage, journal, template, fault, token).ConfigureAwait(false);

        async Task AssertRecoveredRawAsync(Guid artifactId, CancellationToken cancellationToken)
        {
            var recovered = await journal.ReadSiloAdmissionByArtifactAsync(artifactId, cancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(recovered);
            Assert.IsFalse(recovered.EvidenceWrittenOff);
            Assert.AreEqual(("committed", (string?)null),
                await ReadRawDispositionAsync(root, recovered.CaptureId, cancellationToken).ConfigureAwait(false));
            Assert.AreEqual(0L, (await journal.ReadHealthTotalsAsync(cancellationToken).ConfigureAwait(false)).FailureCount);
            var result = await services.GetRequiredService<ICameraAgentArtifactService>().OpenContentAsync(artifactId, cancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Found, result.Status);
            Assert.IsNotNull(result.Content);
            await using (result.Content.ConfigureAwait(false))
                Assert.AreEqual(recovered.PayloadSha256,
                    Convert.ToHexString(await SHA256.HashDataAsync(result.Content, cancellationToken).ConfigureAwait(false)));
        }

        async Task AssertLiveAdmissionAsync(CancellationToken cancellationToken)
        {
            // The live coordinator uses the operations readiness path, which can cache a failed initialization.
            Assert.IsTrue(await ((IRawCaptureIngress)ingress).InitializeForOperationsAsync(cancellationToken).ConfigureAwait(false));
            await ingress.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var admitted = await ingress.AcceptAsync(config, MakeRawSubmission(template, RecoveryTimestamp(template)), cancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual(RawIngressOutcome.Committed, admitted?.Outcome);
            Assert.IsNull((await ingress.PublishSiloAdmissionsAsync(16, cancellationToken).ConfigureAwait(false)).Status);
        }

        async Task AssertCommittedRawRecoversAsync(string disposition, SiloFaultPoint point, CancellationToken cancellationToken)
        {
            var receipt = await ingress.AcceptAsync(config, MakeRawSubmission(template, RecoveryTimestamp(template)), cancellationToken)
                .ConfigureAwait(false);
            Assert.IsNotNull(receipt);
            var descriptor = receipt.Manifest.Descriptor;
            var capture = descriptor.Capture.CaptureId;
            var groupId = capture.ToString("N");
            fault.InterruptPublication(point);
            Assert.AreEqual("silo-raw-admission-failed",
                (await ingress.PublishSiloAdmissionsAsync(16, cancellationToken).ConfigureAwait(false)).Status);
            var group = await storage.State.ReadGroupAsync(groupId, "raw-1", cancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(group);
            Assert.AreEqual(point == SiloFaultPoint.AfterSegmentPut ? SiloStagedState.ManifestQualified : SiloStagedState.Recorded, group.State);
            Assert.HasCount(2, group.Members);
            foreach (var member in group.Members)
            {
                using var staged = await storage.Spool.OpenAsync(member.Draft.SourceToken, cancellationToken).ConfigureAwait(false);
                Assert.AreEqual(member.Draft.Length, staged.Length);
            }

            var sidecar = Path.ChangeExtension(receipt.StoredFrame.AbsolutePath, ".json");
            if (disposition == "missing_evidence" && point == SiloFaultPoint.AfterSegmentPut)
                File.Delete(sidecar);
            else
                File.Delete(receipt.StoredFrame.AbsolutePath);
            if (disposition == "quarantined")
                await File.WriteAllTextAsync(sidecar, "damaged staging", cancellationToken).ConfigureAwait(false);
            if (disposition == "quarantined")
                await journal.MarkEvidenceFailureAsync(capture, disposition, "silo-test-interrupted-evidence", cancellationToken)
                    .ConfigureAwait(false);
            ingress.InvalidateEvidence();
            Assert.IsFalse(await ((IRawCaptureIngress)ingress).InitializeForOperationsAsync(cancellationToken).ConfigureAwait(false));
            var original = await ReadRawDispositionAsync(root, capture, cancellationToken).ConfigureAwait(false);
            Assert.AreEqual(disposition == "quarantined" ? disposition : "missing_evidence", original.State);
            Assert.IsNotNull(original.FailureReason);
            if (disposition == "written-off")
            {
                var evidence = await ingress.ReadEvidencePageAsync(10, null, cancellationToken).ConfigureAwait(false);
                var target = evidence.Items.Single(item => item.Target.CaptureId == capture).Target;
                await ingress.ReleaseEvidenceAsync(target, RawEvidenceReleaseAction.WriteOff, "silo-write-off-" + groupId, "operator",
                    "operator-approved-loss", cancellationToken).ConfigureAwait(false);
            }

            Assert.IsTrue(await journal.HasPendingSiloAdmissionAsync(capture, cancellationToken).ConfigureAwait(false));
            Assert.IsNull((await ingress.PublishSiloAdmissionsAsync(16, cancellationToken).ConfigureAwait(false)).Status);
            var settled = await storage.State.ReadGroupAsync(groupId, "raw-1", cancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(settled);
            Assert.AreEqual(SiloStagedState.Visible, settled.State);
            foreach (var member in settled.Members)
            {
                Assert.IsTrue(member.Released);
                await Assert.ThrowsExactlyAsync<FileNotFoundException>(async () =>
                {
                    using var leaked = await storage.Spool.OpenAsync(member.Draft.SourceToken, cancellationToken).ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
            Assert.IsFalse(await journal.HasPendingSiloAdmissionAsync(capture, cancellationToken).ConfigureAwait(false));
            Assert.IsFalse(File.Exists(receipt.StoredFrame.AbsolutePath));
            Assert.IsFalse(File.Exists(sidecar));
            Assert.IsTrue((await storage.Catalog.ReopenAsync(groupId, "raw-1", cancellationToken).ConfigureAwait(false)).Reopenable);
            if (disposition == "written-off")
            {
                var writtenOff = await journal.ReadSiloAdmissionByArtifactAsync(descriptor.Artifact.ArtifactId, cancellationToken)
                    .ConfigureAwait(false);
                Assert.IsTrue(writtenOff?.EvidenceWrittenOff);
                Assert.AreEqual(original, await ReadRawDispositionAsync(root, capture, cancellationToken).ConfigureAwait(false));
            }
            else
            {
                await AssertRecoveredRawAsync(descriptor.Artifact.ArtifactId, cancellationToken).ConfigureAwait(false);
                await AssertLiveAdmissionAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task AssertQuarantinedWriteOffReclaimsAsync(IServiceProvider services, SiloInstalledStorage storage,
        SqliteRawCaptureJournal journal, ReconstructionDescriptor template, string root, InterruptRawRecovery fault, CancellationToken token)
    {
        var ingress = services.GetRequiredService<RawCaptureIngress>();
        var config = await services.GetRequiredService<ICameraAgentConfigurationLoader>().LoadAsync(token).ConfigureAwait(false);
        var receipt = await ingress.AcceptAsync(config, MakeRawSubmission(template, RecoveryTimestamp(template)), token).ConfigureAwait(false);
        Assert.IsNotNull(receipt);
        var descriptor = receipt.Manifest.Descriptor;
        var capture = descriptor.Capture.CaptureId;
        var artifact = descriptor.Artifact.ArtifactId;
        fault.InterruptPublication(SiloFaultPoint.AfterManifestQualified);
        Assert.AreEqual("silo-raw-admission-failed", (await ingress.PublishSiloAdmissionsAsync(16, token).ConfigureAwait(false)).Status);
        await journal.MarkEvidenceFailureAsync(capture, "quarantined", "silo-test-lane-quarantine", token).ConfigureAwait(false);
        var evidence = await ingress.ReadEvidencePageAsync(10, null, token).ConfigureAwait(false);
        var target = evidence.Items.Single(item => item.Target.CaptureId == capture).Target;
        await ingress.ReleaseEvidenceAsync(target, RawEvidenceReleaseAction.WriteOff, "silo-pre-p3-write-off", "operator",
            "operator-approved-loss", token).ConfigureAwait(false);
        var payload = receipt.StoredFrame.AbsolutePath;
        var sidecar = Path.ChangeExtension(payload, ".json");
        Assert.IsTrue(File.Exists(payload));
        Assert.IsTrue(File.Exists(sidecar));

        fault.InterruptReclamation();
        Assert.AreEqual("silo-raw-admission-failed", (await ingress.PublishSiloAdmissionsAsync(16, token).ConfigureAwait(false)).Status);
        Assert.IsTrue(await journal.HasPendingSiloAdmissionAsync(capture, token).ConfigureAwait(false));
        Assert.AreEqual(SiloStagedState.Abandoned,
            (await storage.State.ReadGroupAsync(capture.ToString("N"), "raw-1", token).ConfigureAwait(false))?.State);
        Assert.IsFalse(File.Exists(payload));
        Assert.IsFalse(File.Exists(sidecar));

        // Exercise startup replay of the previous intent format for a pre-P3 abandonment.
        var intent = ReclaimIntentPath(storage.PhysicalState.Resolve("raw-admission-intents"), artifact);
        var document = JsonNode.Parse(await File.ReadAllBytesAsync(intent, token).ConfigureAwait(false))!.AsObject();
        Assert.IsFalse(document["SiloVisible"]!.GetValue<bool>());
        document.Remove("SiloVisible");
        await File.WriteAllTextAsync(intent, document.ToJsonString(), token).ConfigureAwait(false);
        await ingress.InitializeAsync(token).ConfigureAwait(false);
        Assert.IsFalse(await journal.HasPendingSiloAdmissionAsync(capture, token).ConfigureAwait(false));
        Assert.IsFalse(File.Exists(intent));
        Assert.IsTrue((await journal.ReadSiloAdmissionByArtifactAsync(artifact, token).ConfigureAwait(false))?.EvidenceWrittenOff);
        Assert.AreEqual(("quarantined", "silo-test-lane-quarantine"), await ReadRawDispositionAsync(root, capture, token).ConfigureAwait(false));
    }

    private static async Task AssertRawPublicationIsolatesFailuresAsync(IServiceProvider services, SiloInstalledStorage storage,
        SqliteRawCaptureJournal journal, ReconstructionDescriptor template, InterruptRawRecovery fault, CancellationToken token)
    {
        var ingress = services.GetRequiredService<RawCaptureIngress>();
        var config = await services.GetRequiredService<ICameraAgentConfigurationLoader>().LoadAsync(token).ConfigureAwait(false);
        var at = RecoveryTimestamp(template);
        var first = await ingress.AcceptAsync(config, MakeRawSubmission(template, at.AddMilliseconds(-100)), token).ConfigureAwait(false);
        var second = await ingress.AcceptAsync(config, MakeRawSubmission(template, at), token).ConfigureAwait(false);
        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        var firstId = first.Manifest.Descriptor.Capture.CaptureId;
        var secondId = second.Manifest.Descriptor.Capture.CaptureId;
        Assert.IsTrue(await journal.HasPendingSiloAdmissionAsync(firstId, token).ConfigureAwait(false));
        Assert.IsTrue(await journal.HasPendingSiloAdmissionAsync(secondId, token).ConfigureAwait(false));

        fault.InterruptPublication(SiloFaultPoint.AfterManifestQualified);
        var scan = await ingress.PublishSiloAdmissionsAsync(16, token).ConfigureAwait(false);
        Assert.AreEqual((false, "silo-raw-admission-failed"), scan);
        Assert.IsTrue(await journal.HasPendingSiloAdmissionAsync(firstId, token).ConfigureAwait(false));
        Assert.IsTrue(File.Exists(first.StoredFrame.AbsolutePath));
        Assert.IsTrue(File.Exists(Path.ChangeExtension(first.StoredFrame.AbsolutePath, ".json")));
        await AssertSettledAsync(secondId, second.StoredFrame.AbsolutePath).ConfigureAwait(false);

        Assert.IsNull((await ingress.PublishSiloAdmissionsAsync(16, token).ConfigureAwait(false)).Status);
        await AssertSettledAsync(firstId, first.StoredFrame.AbsolutePath).ConfigureAwait(false);

        async Task AssertSettledAsync(Guid captureId, string payload)
        {
            Assert.IsFalse(await journal.HasPendingSiloAdmissionAsync(captureId, token).ConfigureAwait(false));
            Assert.IsFalse(File.Exists(payload));
            Assert.IsFalse(File.Exists(Path.ChangeExtension(payload, ".json")));
            Assert.AreEqual(SiloStagedState.Visible,
                (await storage.State.ReadGroupAsync(captureId.ToString("N"), "raw-1", token).ConfigureAwait(false))?.State);
        }
    }

    private static async Task<(string State, string? FailureReason)> ReadRawDispositionAsync(string root, Guid captureId,
        CancellationToken token)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "journal", "raw-ingress.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(token).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state, failure_reason FROM raw_captures WHERE capture_id = $capture;";
        command.Parameters.AddWithValue("$capture", captureId.ToString("N"));
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync(token).ConfigureAwait(false));
        var reason = await reader.IsDBNullAsync(1, token).ConfigureAwait(false) ? null : reader.GetString(1);
        return (reader.GetString(0), reason);
    }

    private static string ReclaimIntentPath(string directory, Guid artifactId) => Path.Combine(directory, artifactId.ToString("N") + ".json");

    private static DateTimeOffset RecoveryTimestamp(ReconstructionDescriptor template) =>
        DateTimeOffset.UtcNow.Subtract(template.Controls.EffectiveExposure).AddMilliseconds(-250);

    private sealed class InterruptRawRecovery : ISiloFaults, IRawIngressFaultInjector
    {
        private int _publicationPoint = -1;
        private int _reclaim;

        public void InterruptPublication(SiloFaultPoint point) => Volatile.Write(ref _publicationPoint, (int)point);
        public void InterruptReclamation() => Volatile.Write(ref _reclaim, 1);

        public void Hit(SiloFaultPoint point)
        {
            if (Interlocked.CompareExchange(ref _publicationPoint, -1, (int)point) == (int)point)
                throw new IOException("Interrupted raw journal publication.");
        }

        public void Inject(RawIngressFaultPoint point) { }

        public void AfterSiloStagingReclaimed()
        {
            if (Interlocked.Exchange(ref _reclaim, 0) == 1)
                throw new IOException("Interrupted raw reclamation before settlement.");
        }
    }
}
