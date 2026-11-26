using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;
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
        fault.InterruptReclamation();
        var interrupted = await ingress.PublishSiloAdmissionsAsync(16, token).ConfigureAwait(false);
        Assert.AreEqual("silo-raw-admission-failed", interrupted.Status);
        var row = await journal.ReadSiloAdmissionByArtifactAsync(id, token).ConfigureAwait(false);
        Assert.IsNotNull(row);
        Assert.IsTrue(await journal.HasPendingSiloAdmissionAsync(row.CaptureId, token).ConfigureAwait(false));
        var group = await storage.State.ReadGroupAsync(row.CaptureId.ToString("N"), "raw-1", token).ConfigureAwait(false);
        Assert.AreEqual(SiloStagedState.Visible, group?.State);
        Assert.IsFalse(File.Exists(capture.StoredFrame.AbsolutePath));
        Assert.IsFalse(File.Exists(Path.ChangeExtension(capture.StoredFrame.AbsolutePath, ".json")));
        var intent = ReclaimIntentPath(storage.PhysicalState.Resolve("raw-admission-intents"), id);
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(intent, token).ConfigureAwait(false));
        Assert.IsTrue(document.RootElement.GetProperty("Reclaim").GetBoolean());
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
        Assert.AreEqual("committed", (await journal.ReadSiloAdmissionByArtifactAsync(row.ArtifactId, token).ConfigureAwait(false))?.State);
        Assert.IsTrue((await storage.Catalog.ReopenAsync(row.CaptureId.ToString("N"), "raw-1", token).ConfigureAwait(false)).Reopenable);

        await AssertCommittedRawRecoversAsync("written-off", SiloFaultPoint.AfterSegmentPut, token).ConfigureAwait(false);
        await AssertCommittedRawRecoversAsync("written-off", SiloFaultPoint.AfterSegmentCommitted, token).ConfigureAwait(false);
        await AssertCommittedRawRecoversAsync("missing_evidence", SiloFaultPoint.AfterSegmentPut, token).ConfigureAwait(false);
        await AssertCommittedRawRecoversAsync("missing_evidence", SiloFaultPoint.AfterSegmentCommitted, token).ConfigureAwait(false);
        await AssertCommittedRawRecoversAsync("quarantined", SiloFaultPoint.AfterSegmentCommitted, token).ConfigureAwait(false);

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
            await journal.MarkEvidenceFailureAsync(capture, disposition == "quarantined" ? disposition : "missing_evidence",
                "silo-test-interrupted-evidence", cancellationToken).ConfigureAwait(false);
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
        }
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
