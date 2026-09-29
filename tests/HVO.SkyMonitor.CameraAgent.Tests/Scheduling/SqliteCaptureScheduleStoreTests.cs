using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Scheduling;

[TestClass]
[TestCategory("Unit")]
public sealed class SqliteCaptureScheduleStoreTests
{
    [TestMethod]
    public async Task PendingNamedRig_SecondModuleInitializationFailureKeepsCommittedSelection()
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var ingress = new JournalInitializer(root);
            using var schedule = new SqliteCaptureScheduleStore(ingress, options, TimeProvider.System);
            var initial = await schedule.InitializeAsync(Configuration(), CancellationToken.None).ConfigureAwait(false);
            var named = new SqliteNamedRigProfileStore(ingress, options, TimeProvider.System, schedule);
            var catalog = await named.ImportActiveAsync(initial.ActiveRevision, CancellationToken.None).ConfigureAwait(false);
            var selected = catalog.Selection.ActiveRevisionId!;
            var staged = await named.StageAsync(selected, catalog.Selection.Version, "serial-stage", "owner", true,
                CancellationToken.None).ConfigureAwait(false);
            var candidate = await named.ReconcileAtStartupAsync(Configuration(), new WorkingModuleFactory(),
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(staged.ScheduleRevisionId, candidate?.RevisionId);
            await named.CommitInitializedAsync(selected, candidate!.Profile.ApplyTo(Configuration()), CancellationToken.None)
                .ConfigureAwait(false);
            var captureModule = new TestModule(true);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                captureModule.InitializeAsync(candidate!.Profile.ApplyTo(Configuration()), CancellationToken.None))
                .ConfigureAwait(false);
            Assert.AreEqual(0, captureModule.CaptureCalls);
            named.ReportActiveRuntimeFailure("Camera serial mismatch; check connection and restart.");
            Assert.IsNotNull(named.ActiveRuntimeFailure);
            Assert.IsNull((await named.GetAsync(CancellationToken.None).ConfigureAwait(false)).Selection.PendingRevisionId);
            var after = await schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(staged.ScheduleRevisionId, after.ActiveRevision.RevisionId);
            Assert.IsNull(after.PendingRevision);
            await Assert.ThrowsExactlyAsync<CaptureScheduleStoreConflictException>(() => schedule.ActivateAsync(
                staged.ScheduleRevisionId!, "bypass", after.Version, "owner", null, CancellationToken.None))
                .ConfigureAwait(false);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task PendingNamedRig_FailedPhysicalCheckPublishesVerifiedActiveAndKeepsPendingCancelable()
    {
        var root = CreateRoot();
        try
        {
            var configuration = HostConfiguration();
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var ingress = new JournalInitializer(root);
            using var schedule = new SqliteCaptureScheduleStore(ingress, options, TimeProvider.System);
            var initial = await schedule.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
            var named = new SqliteNamedRigProfileStore(ingress, options, TimeProvider.System, schedule);
            var catalog = await named.ImportActiveAsync(initial.ActiveRevision, CancellationToken.None).ConfigureAwait(false);
            var revisionId = catalog.Selection.ActiveRevisionId!;
            var receipt = await named.StageAsync(revisionId, catalog.Selection.Version, "failed-stage", "owner", true,
                CancellationToken.None).ConfigureAwait(false);

            var accessor = new CameraAgentConfigurationAccessor();
            var initializer = new CameraAgentConfigurationInitializer(new StaticConfigurationLoader(configuration),
                accessor, new EmptyPipelineFactory(), NullLogger<CameraAgentConfigurationInitializer>.Instance,
                scheduleStore: schedule, namedRigProfiles: named);
            await initializer.StartAsync(CancellationToken.None).ConfigureAwait(false);
            var after = await schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevision.RevisionId, after.ActiveRevision.RevisionId);
            Assert.AreEqual(initial.ActiveRevision.ProfileSha256, after.ActiveRevision.ProfileSha256);
            Assert.AreEqual(initial.ActiveRevision.ScheduleSha256, after.ActiveRevision.ScheduleSha256);
            Assert.AreEqual(receipt.ScheduleRevisionId, after.PendingRevision?.RevisionId);
            Assert.AreEqual(revisionId, (await named.GetAsync(CancellationToken.None).ConfigureAwait(false)).Selection.PendingRevisionId);
            Assert.IsTrue(accessor.IsConfigured);
            Assert.AreEqual(configuration.ModuleType,
                (await accessor.WaitForConfigurationAsync(CancellationToken.None).ConfigureAwait(false)).ModuleType);
            Assert.IsNull(named.PendingRuntimeFailure);
            var cancelled = await named.CancelPendingAsync(revisionId, catalog.Selection.Version + 1,
                "failed-cancel", "owner", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("cancelled", cancelled.Disposition);
            Assert.AreEqual(cancelled, await named.CancelPendingAsync(revisionId, catalog.Selection.Version + 1,
                "failed-cancel", "owner", CancellationToken.None).ConfigureAwait(false));
            await Assert.ThrowsExactlyAsync<CaptureScheduleStoreConflictException>(() => named.CancelPendingAsync(
                revisionId, catalog.Selection.Version + 1, "other-cancel", "owner", CancellationToken.None))
                .ConfigureAwait(false);
            Assert.IsNull((await named.GetAsync(CancellationToken.None).ConfigureAwait(false)).Selection.PendingRevisionId);
            Assert.IsNull((await schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).PendingRevision);
            Assert.IsNull(named.PendingRuntimeFailure);
            Assert.IsNull(await named.ReconcileAtStartupAsync(Configuration(), new FailingModuleFactory(),
                CancellationToken.None).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task PendingNamedRig_ValidatedStartupCommitsBeforePublishingAndSurvivesRestart()
    {
        var root = CreateRoot();
        try
        {
            var configuration = HostConfiguration();
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var ingress = new JournalInitializer(root);
            using var schedule = new SqliteCaptureScheduleStore(ingress, options, TimeProvider.System);
            var initial = await schedule.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
            var named = new SqliteNamedRigProfileStore(ingress, options, TimeProvider.System, schedule);
            var imported = await named.ImportActiveAsync(initial.ActiveRevision, CancellationToken.None).ConfigureAwait(false);
            var selected = imported.Selection.ActiveRevisionId!;
            var staged = await named.StageAsync(selected, imported.Selection.Version, "closed-stage", "owner", true,
                CancellationToken.None).ConfigureAwait(false);
            var accessor = new CameraAgentConfigurationAccessor();
            var initializer = new CameraAgentConfigurationInitializer(new StaticConfigurationLoader(configuration),
                accessor, new EmptyPipelineFactory(), NullLogger<CameraAgentConfigurationInitializer>.Instance,
                scheduleStore: schedule, namedRigProfiles: named);

            await initializer.StartAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(accessor.IsConfigured);
            var committed = await schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevision.RevisionId, committed.ActiveRevision.RevisionId);
            Assert.AreEqual(staged.ScheduleRevisionId, committed.PendingRevision?.RevisionId);
            Assert.AreEqual(selected, (await named.GetAsync(CancellationToken.None).ConfigureAwait(false)).Selection.ActiveRevisionId);
            Assert.AreEqual(selected, (await named.GetAsync(CancellationToken.None).ConfigureAwait(false)).Selection.PendingRevisionId);
            var restartedAccessor = new CameraAgentConfigurationAccessor();
            var restarted = new CameraAgentConfigurationInitializer(new StaticConfigurationLoader(configuration),
                restartedAccessor, new EmptyPipelineFactory(), NullLogger<CameraAgentConfigurationInitializer>.Instance,
                scheduleStore: schedule, namedRigProfiles: named);
            await restarted.StartAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(restartedAccessor.IsConfigured);
            Assert.AreEqual(committed.Version, (await schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Version);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task NamedRigRollback_StagesFormerActiveRevisionWithoutChangingSchedule()
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var ingress = new JournalInitializer(root);
            using var schedule = new SqliteCaptureScheduleStore(ingress, options, TimeProvider.System);
            var initial = await schedule.InitializeAsync(Configuration(), CancellationToken.None).ConfigureAwait(false);
            var named = new SqliteNamedRigProfileStore(ingress, options, TimeProvider.System, schedule);
            var catalog = await named.ImportActiveAsync(initial.ActiveRevision, CancellationToken.None).ConfigureAwait(false);
            var former = catalog.Selection.ActiveRevisionId!;
            var staged = await named.StageAsync(former, catalog.Selection.Version, "forward", "owner", true,
                CancellationToken.None).ConfigureAwait(false);
            await named.CommitInitializedAsync(former, Configuration(), CancellationToken.None).ConfigureAwait(false);
            var current = await named.GetAsync(CancellationToken.None).ConfigureAwait(false);
            var rollback = await named.StageAsync(former, current.Selection.Version, "rollback", "owner", true,
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("restart_required", rollback.Disposition);
            Assert.AreEqual(former, (await named.GetAsync(CancellationToken.None).ConfigureAwait(false)).Selection.PendingRevisionId);
            var beforeRestart = await schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(staged.ScheduleRevisionId, beforeRestart.ActiveRevision.RevisionId);
            Assert.AreEqual(beforeRestart.ActiveRevision.ScheduleSha256, beforeRestart.PendingRevision!.ScheduleSha256);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task NamedRigImportAndDrafts_PreserveScheduleAndRejectUnsafeStage()
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var ingress = new JournalInitializer(root);
            using var schedule = new SqliteCaptureScheduleStore(ingress, options, TimeProvider.System);
            var active = await schedule.InitializeAsync(Configuration(), CancellationToken.None).ConfigureAwait(false);
            var profiles = new SqliteNamedRigProfileStore(ingress, options, TimeProvider.System, schedule);
            var initial = await profiles.ImportActiveAsync(active.ActiveRevision, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1, initial.Revisions.Count);
            Assert.AreEqual(active.ActiveRevision.RevisionId, initial.Revisions[0].SourceScheduleRevisionId);
            Assert.AreEqual(active.ActiveRevision.Profile.Module, initial.Revisions[0].Module);
            Assert.AreEqual(active.ActiveRevision.Profile.Rig, initial.Revisions[0].Rig);
            Assert.IsNotNull(initial.Selection.ActiveRevisionId);
            Assert.AreEqual(1, initial.Selection.Version);
            await Assert.ThrowsExactlyAsync<CaptureScheduleStoreConflictException>(() => schedule.StageAsync(
                active.ActiveRevision.Profile with
                {
                    Module = active.ActiveRevision.Profile.Module with { Type = "another-camera" }
                }, "equipment-reversal", active.Version, "owner", null, CancellationToken.None))
                .ConfigureAwait(false);

            var inventory = await profiles.GetInventoryAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1, inventory.Profiles.Count);
            await Assert.ThrowsExactlyAsync<CaptureScheduleStoreConflictException>(() => profiles.SaveProfileNameAsync(
                inventory.Profiles[0].ProfileId, "Changed installed rig", CancellationToken.None)).ConfigureAwait(false);
            foreach (var installed in inventory.Equipment)
            {
                await Assert.ThrowsExactlyAsync<CaptureScheduleStoreConflictException>(() => profiles.SaveEquipmentAsync(
                    installed.DefinitionId, installed.Kind, "Changed installed equipment",
                    System.Text.Json.JsonSerializer.SerializeToElement(installed.Kind switch
                    {
                        "camera" => (object)new
                        {
                            module = new { type = active.ActiveRevision.Profile.Module.Type },
                            sensor = active.ActiveRevision.Profile.Rig.Sensor,
                            readout = active.ActiveRevision.Profile.Rig.Readout
                        },
                        "optics" => active.ActiveRevision.Profile.Rig.Optics,
                        _ => active.ActiveRevision.Profile.Rig.Orientation
                    }), CancellationToken.None, installed.RevisionId, installed.RevisionId))
                    .ConfigureAwait(false);
            }
            var draft = await profiles.SaveProfileNameAsync(null, "Other rig", CancellationToken.None).ConfigureAwait(false);
            var camera = await profiles.SaveEquipmentAsync(null, "camera", "ASI120MM",
                System.Text.Json.JsonSerializer.SerializeToElement(new NamedCameraEquipment(active.ActiveRevision.Profile.Module with
                { Options = System.Text.Json.JsonSerializer.SerializeToElement(new { secret = "private-camera-token" }) },
                    active.ActiveRevision.Profile.Rig.Sensor, active.ActiveRevision.Profile.Rig.Readout)),
                CancellationToken.None).ConfigureAwait(false);
            var cameraDetail = await profiles.GetEquipmentAsync(camera.RevisionId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(camera.DefinitionId, cameraDetail.DefinitionId);
            Assert.IsFalse(System.Text.Json.JsonSerializer.Serialize(cameraDetail).Contains("options", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(System.Text.Json.JsonSerializer.Serialize(cameraDetail).Contains("private-camera-token", StringComparison.Ordinal));
            await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => profiles.GetEquipmentAsync("missing", CancellationToken.None)).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => profiles.SaveEquipmentAsync(camera.DefinitionId,
                "camera", "Overwrite", System.Text.Json.JsonSerializer.SerializeToElement(new NamedCameraEquipment(
                    active.ActiveRevision.Profile.Module, active.ActiveRevision.Profile.Rig.Sensor,
                    active.ActiveRevision.Profile.Rig.Readout)), CancellationToken.None, camera.RevisionId, camera.RevisionId)).ConfigureAwait(false);
            var revised = await profiles.SaveEquipmentAsync(camera.DefinitionId, "camera", "ASI120MM draft",
                System.Text.Json.JsonSerializer.SerializeToElement(new
                {
                    module = new { type = active.ActiveRevision.Profile.Module.Type },
                    sensor = active.ActiveRevision.Profile.Rig.Sensor,
                    readout = active.ActiveRevision.Profile.Rig.Readout
                }),
                CancellationToken.None, camera.RevisionId, camera.RevisionId).ConfigureAwait(false);
            Assert.AreEqual(2, revised.RevisionNumber);
            await Assert.ThrowsExactlyAsync<CaptureScheduleStoreConflictException>(() => profiles.SaveEquipmentAsync(
                camera.DefinitionId, "camera", "Stale", System.Text.Json.JsonSerializer.SerializeToElement(new
                { module = new { type = active.ActiveRevision.Profile.Module.Type }, sensor = active.ActiveRevision.Profile.Rig.Sensor }),
                CancellationToken.None, camera.RevisionId, camera.RevisionId)).ConfigureAwait(false);
            var optics = await profiles.SaveEquipmentAsync(null, "optics", "Draft optics",
                System.Text.Json.JsonSerializer.SerializeToElement(active.ActiveRevision.Profile.Rig.Optics), CancellationToken.None).ConfigureAwait(false);
            var mount = await profiles.SaveEquipmentAsync(null, "mount", "Draft mount",
                System.Text.Json.JsonSerializer.SerializeToElement(active.ActiveRevision.Profile.Rig.Orientation), CancellationToken.None).ConfigureAwait(false);
            var draftRig = await profiles.ComposeAsync(draft.ProfileId, revised.RevisionId,
                optics.RevisionId, mount.RevisionId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("private-camera-token", draftRig.Module.Options!.Value.GetProperty("secret").GetString());
            var preview = await profiles.PreviewAsync(draftRig.RevisionId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(preview.Valid);
            Assert.IsFalse(preview.RuntimeVerified);
            Assert.AreEqual(active.ActiveRevision.RevisionId, preview.ScheduleRevisionId);
            var current = active.ActiveRevision.Profile;
            var changedPolicy = current.Rig.ControlPolicy! with
            {
                ExposureControl = AutomaticControlOwnership.CameraNative
            };
            var changedPipeline = current.Rig.Pipeline with
            {
                Envelope = new ExposureEnvelope(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10),
                    2, 10, new ExposureDefaults(TimeSpan.FromSeconds(1), 2),
                    new ExposureDefaults(TimeSpan.FromSeconds(10), 10), 0.5)
            };
            var changedProfile = current with
            {
                Rig = current.Rig with { ControlPolicy = changedPolicy, Pipeline = changedPipeline }
            };
            var pending = await schedule.StageAsync(changedProfile, "schedule-owned-change", active.Version,
                "owner", null, CancellationToken.None).ConfigureAwait(false);
            var activated = await schedule.ActivateAsync(pending.PendingRevision!.RevisionId,
                "schedule-owned-activation", pending.Version, "owner", null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(changedPolicy, activated.ActiveRevision.Profile.Rig.ControlPolicy);
            var changedPreview = await profiles.PreviewAsync(draftRig.RevisionId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(activated.ActiveRevision.RevisionId, changedPreview.ScheduleRevisionId);
            Assert.IsFalse(changedPreview.Valid);
            Assert.AreEqual("rig.pipeline.envelope", changedPreview.Failure);
            await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => profiles.ComposeAsync(
                draft.ProfileId, optics.RevisionId, revised.RevisionId, mount.RevisionId,
                CancellationToken.None)).ConfigureAwait(false);
            var restarted = new SqliteNamedRigProfileStore(ingress, options, TimeProvider.System, schedule);
            var restoredInventory = await restarted.GetInventoryAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(restoredInventory.Profiles.Contains(draft));
            Assert.IsTrue(restoredInventory.Equipment.Contains(revised));
            var receipt = await restarted.StageAsync(draftRig.RevisionId, 1, "stage-key", "owner", true, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.AreEqual("restart_required", receipt.Disposition);
            Assert.AreEqual(receipt, await restarted.StageAsync(draftRig.RevisionId, 1, "stage-key", "owner", true,
                CancellationToken.None).ConfigureAwait(false));
            var after = await restarted.GetAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(initial.Selection.ActiveRevisionId, after.Selection.ActiveRevisionId);
            Assert.AreEqual(draftRig.RevisionId, after.Selection.PendingRevisionId);
            Assert.AreEqual(initial.Selection.Version + 1, after.Selection.Version);
            Assert.AreEqual(activated.ActiveRevision.ProfileSha256,
                (await schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).ActiveRevision.ProfileSha256);
            Assert.AreEqual(activated.ActiveRevision.ScheduleSha256,
                (await schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).ActiveRevision.ScheduleSha256);
            await Assert.ThrowsExactlyAsync<CaptureScheduleStoreConflictException>(() => restarted.ImportActiveAsync(
                active.ActiveRevision, CancellationToken.None)).ConfigureAwait(false);
            var reconciled = await restarted.ReconcileAtStartupAsync(Configuration(), new WorkingModuleFactory(),
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(receipt.ScheduleRevisionId, reconciled?.RevisionId);
            Assert.AreEqual(draftRig.RevisionId, (await restarted.GetAsync(CancellationToken.None).ConfigureAwait(false)).Selection.PendingRevisionId);
            await restarted.CommitInitializedAsync(draftRig.RevisionId, reconciled!.Profile.ApplyTo(Configuration()), CancellationToken.None).ConfigureAwait(false);
            using (var verification = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}"))
            {
                await verification.OpenAsync().ConfigureAwait(false);
                using var command = verification.CreateCommand();
                command.CommandText = "SELECT from_revision_id, to_revision_id, state_version FROM capture_schedule_activations ORDER BY activation_id DESC LIMIT 1;";
                using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
                Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
                Assert.AreEqual(activated.ActiveRevision.RevisionId, reader.GetString(0));
                Assert.AreEqual(reconciled!.RevisionId, reader.GetString(1));
                Assert.AreEqual((await schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Version, reader.GetInt64(2));
            }
            var repeated = await restarted.ImportActiveAsync(reconciled!, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(draftRig.RevisionId, repeated.Selection.ActiveRevisionId);
            Assert.IsNull(repeated.Selection.PendingRevisionId);
            Assert.AreEqual(2, repeated.Revisions.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task NamedRigActivation_UnchangedFileDoesNotRestageAfterRestart_ButChangedFileDoes()
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var ingress = new JournalInitializer(root);
            using var schedule = new SqliteCaptureScheduleStore(ingress, options, TimeProvider.System);
            var initial = await schedule.InitializeAsync(Configuration(), CancellationToken.None).ConfigureAwait(false);
            var named = new SqliteNamedRigProfileStore(ingress, options, TimeProvider.System, schedule);
            var imported = await named.ImportActiveAsync(initial.ActiveRevision, CancellationToken.None).ConfigureAwait(false);
            var staged = await named.StageAsync(imported.Selection.ActiveRevisionId!, imported.Selection.Version,
                "restart-stage", "owner", true, CancellationToken.None).ConfigureAwait(false);
            _ = await named.ReconcileAtStartupAsync(Configuration(), new WorkingModuleFactory(), CancellationToken.None)
                .ConfigureAwait(false);
            await named.CommitInitializedAsync(imported.Selection.ActiveRevisionId!, Configuration(), CancellationToken.None).ConfigureAwait(false);
            using var restarted = new SqliteCaptureScheduleStore(ingress, options, TimeProvider.System);
            var unchanged = await restarted.InitializeAsync(Configuration(), CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(staged.ScheduleRevisionId, unchanged.ActiveRevision.RevisionId);
            Assert.IsNull(unchanged.PendingRevision);
            var repeated = await restarted.InitializeAsync(Configuration(), CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(unchanged.Version, repeated.Version);
            var changed = await restarted.InitializeAsync(Configuration() with { Schedule = Definition("new-file", 2) },
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("file-draft", changed.PendingRevision?.Source);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task NamedRigSelection_LookupsFindRevisionBeyondCatalogPage()
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var ingress = new JournalInitializer(root);
            using var schedule = new SqliteCaptureScheduleStore(ingress, options, TimeProvider.System);
            var active = await schedule.InitializeAsync(Configuration(), CancellationToken.None).ConfigureAwait(false);
            var named = new SqliteNamedRigProfileStore(ingress, options, TimeProvider.System, schedule);
            var catalog = await named.ImportActiveAsync(active.ActiveRevision, CancellationToken.None).ConfigureAwait(false);
            var oldId = catalog.Selection.ActiveRevisionId!;
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO named_rig_revisions
                    SELECT $id, profile_id, $number, camera_revision_id, optics_revision_id,
                        mount_revision_id, rig_json, rig_sha256, NULL, created_unix_ms + $number
                    FROM named_rig_revisions WHERE revision_id = $old;
                    """;
                command.Parameters.AddWithValue("$old", oldId);
                var id = command.Parameters.Add("$id", SqliteType.Text);
                var number = command.Parameters.Add("$number", SqliteType.Integer);
                for (var index = 2; index <= 103; index++)
                {
                    id.Value = $"draft-{index}";
                    number.Value = index;
                    Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
                }
            }
            Assert.IsTrue((await named.GetAsync(CancellationToken.None).ConfigureAwait(false)).Revisions
                .Any(revision => revision.RevisionId == oldId));
            var preview = await named.PreviewAsync(oldId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(preview.Valid);
            var receipt = await named.StageAsync(oldId, catalog.Selection.Version, "old-stage", "owner", true,
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(oldId, receipt.RevisionId);
            Assert.AreEqual(receipt.ScheduleRevisionId, (await named.ReconcileAtStartupAsync(Configuration(),
                new WorkingModuleFactory(), CancellationToken.None).ConfigureAwait(false))?.RevisionId);
            await named.CommitInitializedAsync(oldId, Configuration(), CancellationToken.None).ConfigureAwait(false);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task NamedRigSelection_CancelRestageSameRigUsesCurrentCommandAndSchedule()
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var ingress = new JournalInitializer(root);
            using var schedule = new SqliteCaptureScheduleStore(ingress, options, TimeProvider.System);
            var initial = await schedule.InitializeAsync(Configuration(), CancellationToken.None).ConfigureAwait(false);
            var named = new SqliteNamedRigProfileStore(ingress, options, TimeProvider.System, schedule);
            var imported = await named.ImportActiveAsync(initial.ActiveRevision, CancellationToken.None).ConfigureAwait(false);
            var id = imported.Selection.ActiveRevisionId!;
            var first = await named.StageAsync(id, imported.Selection.Version, "first-stage", "owner", true,
                CancellationToken.None).ConfigureAwait(false);
            var cancelled = await named.CancelPendingAsync(id, first.Version, "cancel-stage", "owner",
                CancellationToken.None).ConfigureAwait(false);
            var second = await named.StageAsync(id, cancelled.Version, "second-stage", "owner", true,
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreNotEqual(first.ScheduleRevisionId, second.ScheduleRevisionId);
            var pending = (await named.GetAsync(CancellationToken.None).ConfigureAwait(false)).Selection;
            Assert.AreEqual("second-stage", pending.PendingCommandKey);
            Assert.AreEqual(second.ScheduleRevisionId, pending.PendingScheduleRevisionId);
            Assert.AreEqual((await schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Version,
                pending.PendingScheduleVersion);
            Assert.AreEqual(second.ScheduleRevisionId, (await named.ReconcileAtStartupAsync(Configuration(),
                new WorkingModuleFactory(), CancellationToken.None).ConfigureAwait(false))?.RevisionId);
            await named.CommitInitializedAsync(id, Configuration(), CancellationToken.None).ConfigureAwait(false);
            using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT c.actor, c.acknowledged_unvalidated, r.from_revision_id, r.to_revision_id,
                       r.state_version, c.result_json, r.stage_command_key
                FROM named_rig_selection_receipts r JOIN named_rig_selection_commands c
                  ON c.idempotency_key = r.idempotency_key
                WHERE r.disposition = 'active';
                """;
            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
            Assert.AreEqual("system", reader.GetString(0));
            Assert.AreEqual(1L, reader.GetInt64(1));
            Assert.AreEqual(id, reader.GetString(2));
            Assert.AreEqual(id, reader.GetString(3));
            Assert.AreEqual(second.Version + 1, reader.GetInt64(4));
            StringAssert.Contains(System.Text.Encoding.UTF8.GetString(await reader.GetFieldValueAsync<byte[]>(5).ConfigureAwait(false)),
                second.ScheduleRevisionId!, StringComparison.Ordinal);
            Assert.AreEqual("second-stage", reader.GetString(6));
            Assert.IsFalse(await reader.ReadAsync().ConfigureAwait(false));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task NamedRigActivation_CarriesEffectiveOverridesAndRetainsHistoricalAdmissions()
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var ingress = new JournalInitializer(root);
            using var schedule = new SqliteCaptureScheduleStore(ingress, options, TimeProvider.System);
            var initial = await schedule.InitializeAsync(Configuration(), CancellationToken.None).ConfigureAwait(false);
            var named = new SqliteNamedRigProfileStore(ingress, options, TimeProvider.System, schedule);
            var catalog = await named.ImportActiveAsync(initial.ActiveRevision, CancellationToken.None).ConfigureAwait(false);
            var old = initial.ActiveRevision.RevisionId;
            var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var closed = new CaptureScheduleOverride("carry-closed", old, initial.ActiveRevision.ScheduleSha256,
                CaptureScheduleOverrideMode.ForceClosed, now.AddMinutes(-1), now.AddMinutes(10), null, false);
            var before = await schedule.AddOverrideAsync(closed, "carry-closed-command", initial.Version, "owner", null,
                CancellationToken.None).ConfigureAwait(false);
            var staged = await named.StageAsync(catalog.Selection.ActiveRevisionId!, catalog.Selection.Version,
                "carry-stage", "owner", true, CancellationToken.None).ConfigureAwait(false);
            var shot = new CaptureScheduleOverride("carry-shot", old, initial.ActiveRevision.ScheduleSha256,
                CaptureScheduleOverrideMode.ForceOpen, now.AddMinutes(-1), now.AddMinutes(10), "initial", true);
            _ = await schedule.AddOverrideAsync(shot, "carry-shot-command", before.Version + 1, "owner", null,
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(await schedule.GrantAdmissionAsync("carry-admission", old, shot.Id, now,
                CancellationToken.None).ConfigureAwait(false));
            await named.CommitInitializedAsync(catalog.Selection.ActiveRevisionId!, Configuration(), CancellationToken.None)
                .ConfigureAwait(false);
            var current = await schedule.GetActiveOverridesAsync(staged.ScheduleRevisionId!, now, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.HasCount(2, current);
            Assert.IsTrue(current.Any(item => item.Mode == CaptureScheduleOverrideMode.ForceClosed && item.Id == closed.Id));
            Assert.IsTrue(current.Any(item => item.OneShot && item.ConsumedUtc is not null && item.Id == shot.Id));
            var original = await schedule.GetActiveOverridesAsync(old, now, CancellationToken.None).ConfigureAwait(false);
            Assert.IsEmpty(original);
            using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM capture_schedule_override_events WHERE reason = 'transferred from revision ' || $old || ' to ' || $new;";
            command.Parameters.AddWithValue("$old", old);
            command.Parameters.AddWithValue("$new", staged.ScheduleRevisionId!);
            Assert.AreEqual(2L, (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!);
            command.CommandText = "SELECT override_id FROM capture_schedule_admissions WHERE admission_id = 'carry-admission';";
            Assert.AreEqual(shot.Id, await command.ExecuteScalarAsync().ConfigureAwait(false));
            var state = await schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            _ = await schedule.ClearOverrideAsync(closed.Id, "clear-carried-closed", state.Version, "owner", null,
                CancellationToken.None).ConfigureAwait(false);
            state = await schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            _ = await schedule.ClearOverrideAsync(shot.Id, "clear-carried-open", state.Version, "owner", null,
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsEmpty(await schedule.GetActiveOverridesAsync(staged.ScheduleRevisionId!, now, CancellationToken.None)
                .ConfigureAwait(false));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow(128)]
    [DataRow(16)]
    public async Task NamedRigActivation_TransferEventsRemainBoundedAndUniqueOnReturnTrips(int overrideIdLength)
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var ingress = new JournalInitializer(root);
            using var schedule = new SqliteCaptureScheduleStore(ingress, options, TimeProvider.System);
            var initial = await schedule.InitializeAsync(Configuration(), CancellationToken.None).ConfigureAwait(false);
            var named = new SqliteNamedRigProfileStore(ingress, options, TimeProvider.System, schedule);
            var catalog = await named.ImportActiveAsync(initial.ActiveRevision, CancellationToken.None).ConfigureAwait(false);
            var rigA = catalog.Selection.ActiveRevisionId!;
            const string rigB = "return-trip-rig-b";
            var database = $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}";
            using (var connection = new SqliteConnection(database))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO named_rig_revisions
                    SELECT $new, profile_id, revision_number + 1, camera_revision_id, optics_revision_id,
                        mount_revision_id, rig_json, rig_sha256, NULL, created_unix_ms
                    FROM named_rig_revisions WHERE revision_id = $old;
                    """;
                command.Parameters.AddWithValue("$new", rigB);
                command.Parameters.AddWithValue("$old", rigA);
                Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
            }
            var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var overrideId = new string('x', overrideIdLength);
            var scheduleOverride = new CaptureScheduleOverride(overrideId, initial.ActiveRevision.RevisionId,
                initial.ActiveRevision.ScheduleSha256, CaptureScheduleOverrideMode.ForceClosed,
                now.AddMinutes(-1), now.AddMinutes(10), null, false);
            _ = await schedule.AddOverrideAsync(scheduleOverride, "transfer-override-add", initial.Version,
                "owner", null, CancellationToken.None).ConfigureAwait(false);

            var previousRevision = initial.ActiveRevision.RevisionId;
            var eventIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (rig, index) in new[] { rigB, rigA, rigB }.Select((rig, index) => (rig, index)))
            {
                var selection = (await named.GetAsync(CancellationToken.None).ConfigureAwait(false)).Selection;
                var staged = await named.StageAsync(rig, selection.Version, $"return-trip-{index}", "owner", true,
                    CancellationToken.None).ConfigureAwait(false);
                await named.CommitInitializedAsync(rig, Configuration(), CancellationToken.None).ConfigureAwait(false);
                using var connection = new SqliteConnection(database);
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT event_id, override_id, event_kind, actor, reason
                    FROM capture_schedule_override_events
                    WHERE reason = 'transferred from revision ' || $from || ' to ' || $to;
                    """;
                command.Parameters.AddWithValue("$from", previousRevision);
                command.Parameters.AddWithValue("$to", staged.ScheduleRevisionId!);
                using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
                Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
                var eventId = reader.GetString(0);
                Assert.IsTrue(eventId.Length <= 128);
                Assert.IsTrue(eventIds.Add(eventId));
                Assert.AreEqual(overrideId, reader.GetString(1));
                Assert.AreEqual("created", reader.GetString(2));
                Assert.AreEqual("system", reader.GetString(3));
                Assert.AreEqual($"transferred from revision {previousRevision} to {staged.ScheduleRevisionId}", reader.GetString(4));
                Assert.IsFalse(await reader.ReadAsync().ConfigureAwait(false));
                previousRevision = staged.ScheduleRevisionId!;
            }
            Assert.HasCount(3, eventIds);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task PendingNamedRig_OverrideMutationsAndAdmissionPreserveCancelAndReconciliation()
    {
        foreach (var reconcile in new[] { false, true })
        {
            var root = CreateRoot();
            try
            {
                var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
                var ingress = new JournalInitializer(root);
                using var schedule = new SqliteCaptureScheduleStore(ingress, options, TimeProvider.System);
                var initial = await schedule.InitializeAsync(Configuration(), CancellationToken.None).ConfigureAwait(false);
                var named = new SqliteNamedRigProfileStore(ingress, options, TimeProvider.System, schedule);
                var imported = await named.ImportActiveAsync(initial.ActiveRevision, CancellationToken.None).ConfigureAwait(false);
                var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                CaptureScheduleOverride Override(string id, bool oneShot) => new(id, initial.ActiveRevision.RevisionId,
                    initial.ActiveRevision.ScheduleSha256, CaptureScheduleOverrideMode.ForceOpen,
                    now.AddMinutes(-1), now.AddMinutes(10), "initial", oneShot);

                var before = Override("before", false);
                var addedBefore = await schedule.AddOverrideAsync(before, "add-before", initial.Version, "owner", null,
                    CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(addedBefore.Version, (await schedule.AddOverrideAsync(before, "add-before", initial.Version,
                    "owner", null, CancellationToken.None).ConfigureAwait(false)).Version);
                var clearedBefore = await schedule.ClearOverrideAsync(before.Id, "clear-before", addedBefore.Version,
                    "owner", null, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(clearedBefore.Version, (await schedule.ClearOverrideAsync(before.Id, "clear-before",
                    addedBefore.Version, "owner", null, CancellationToken.None).ConfigureAwait(false)).Version);

                var staged = await named.StageAsync(imported.Selection.ActiveRevisionId!, imported.Selection.Version,
                    "stage-with-overrides", "owner", true, CancellationToken.None).ConfigureAwait(false);
                var active = Override("active", false);
                var afterAdd = await schedule.AddOverrideAsync(active, "add-active", clearedBefore.Version + 1,
                    "owner", null, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(afterAdd.Version, (await schedule.AddOverrideAsync(active, "add-active", clearedBefore.Version + 1,
                    "owner", null, CancellationToken.None).ConfigureAwait(false)).Version);
                var oneShot = Override("one-shot", true);
                var afterOneShot = await schedule.AddOverrideAsync(oneShot, "add-one-shot", afterAdd.Version,
                    "owner", null, CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(await schedule.GrantAdmissionAsync("pending-admission", initial.ActiveRevision.RevisionId,
                    oneShot.Id, now, CancellationToken.None).ConfigureAwait(false));
                Assert.IsTrue(await schedule.GrantAdmissionAsync("pending-admission", initial.ActiveRevision.RevisionId,
                    oneShot.Id, now, CancellationToken.None).ConfigureAwait(false));
                var afterClear = await schedule.ClearOverrideAsync(active.Id, "clear-active", afterOneShot.Version + 1,
                    "owner", null, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(afterClear.Version, (await schedule.ClearOverrideAsync(active.Id, "clear-active",
                    afterOneShot.Version + 1, "owner", null, CancellationToken.None).ConfigureAwait(false)).Version);
                var pending = (await named.GetAsync(CancellationToken.None).ConfigureAwait(false)).Selection;
                Assert.AreEqual(afterClear.Version, pending.PendingScheduleVersion);
                Assert.AreEqual(staged.ScheduleRevisionId, (await schedule.GetSnapshotAsync(CancellationToken.None)
                    .ConfigureAwait(false)).PendingRevision?.RevisionId);
                if (reconcile)
                {
                    var candidate = await named.ReconcileAtStartupAsync(Configuration(), new WorkingModuleFactory(),
                        CancellationToken.None).ConfigureAwait(false);
                    Assert.AreEqual(staged.ScheduleRevisionId, candidate?.RevisionId);
                    await named.CommitInitializedAsync(staged.RevisionId, candidate!.Profile.ApplyTo(Configuration()),
                        CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    var cancelled = await named.CancelPendingAsync(staged.RevisionId, staged.Version, "cancel-overrides",
                        "owner", CancellationToken.None).ConfigureAwait(false);
                    Assert.AreEqual(cancelled, await named.CancelPendingAsync(staged.RevisionId, staged.Version,
                        "cancel-overrides", "owner", CancellationToken.None).ConfigureAwait(false));
                }
                Assert.IsNull((await schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).PendingRevision);
                Assert.IsNull((await named.GetAsync(CancellationToken.None).ConfigureAwait(false)).Selection.PendingRevisionId);
                var effectiveRevision = reconcile ? staged.ScheduleRevisionId! : initial.ActiveRevision.RevisionId;
                var overrides = await schedule.GetActiveOverridesAsync(effectiveRevision, now,
                    CancellationToken.None).ConfigureAwait(false);
                Assert.HasCount(1, overrides);
                Assert.AreEqual(oneShot.Id, overrides[0].Id);
                Assert.IsNotNull(overrides[0].ConsumedUtc);
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }

    [TestMethod]
    public async Task NamedRigSelection_ScheduleVersionChangeRejectsCancelAndActivation()
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var ingress = new JournalInitializer(root);
            using var schedule = new SqliteCaptureScheduleStore(ingress, options, TimeProvider.System);
            var initial = await schedule.InitializeAsync(Configuration(), CancellationToken.None).ConfigureAwait(false);
            var named = new SqliteNamedRigProfileStore(ingress, options, TimeProvider.System, schedule);
            var imported = await named.ImportActiveAsync(initial.ActiveRevision, CancellationToken.None).ConfigureAwait(false);
            var id = imported.Selection.ActiveRevisionId!;
            var stage = await named.StageAsync(id, imported.Selection.Version, "version-stage", "owner", true,
                CancellationToken.None).ConfigureAwait(false);
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE capture_schedule_state SET version = version + 1 WHERE state_key = 1;";
                Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
            }
            await Assert.ThrowsExactlyAsync<CaptureScheduleStoreConflictException>(() => named.CancelPendingAsync(
                id, stage.Version, "version-cancel", "owner", CancellationToken.None)).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<CaptureScheduleStoreConflictException>(() => named.CommitInitializedAsync(
                id, Configuration(), CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual("version-stage", (await named.GetAsync(CancellationToken.None).ConfigureAwait(false))
                .Selection.PendingCommandKey);
        }
        finally { Directory.Delete(root, recursive: true); }
    }


    [TestMethod]
    public async Task InitializeAsync_ExplicitBootstrapAndFileChangePreserveActiveRevision()
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            using var store = new SqliteCaptureScheduleStore(
                new JournalInitializer(root), options, TimeProvider.System);
            var initial = await store.InitializeAsync(Configuration(), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, initial.ActiveRevision.RevisionNumber);
            Assert.AreEqual("initial", initial.ActiveRevision.Definition.SetpointProfiles.Single().Id);
            Assert.IsNull(initial.PendingRevision);

            var changed = await store.InitializeAsync(
                Configuration() with { Schedule = Definition("night", 2) },
                CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(initial.ActiveRevision.RevisionId, changed.ActiveRevision.RevisionId);
            Assert.IsNotNull(changed.PendingRevision);
            Assert.AreEqual("night", changed.PendingRevision.Definition.SetpointProfiles.Single().Id);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_ExplicitPipelinePreservesV2ProfileAndHash()
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            using var store = new SqliteCaptureScheduleStore(
                new JournalInitializer(root), options, TimeProvider.System);
            var baseline = Configuration();
            var configuration = baseline with
            {
                Pipeline = new CapturePipelineConfig(
                    [new CaptureProcessingStepConfig("Calibration", "calibration", DependsOn: ["$raw"])],
                    CapturePipelineSchemaVersions.ExplicitV2,
                    CapturePipelineDependencyPolicy.RejectEnabledDependent),
                Schedule = Definition("night", 2)
            };

            var snapshot = await store.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
            var effective = snapshot.ActiveRevision.Profile.ApplyTo(configuration);

            Assert.AreEqual(LocalCaptureProfileDefinition.CurrentSchemaVersion, snapshot.ActiveRevision.Profile.SchemaVersion);
            Assert.AreEqual(
                LocalCaptureProfileContract.ComputeSha256(
                    LocalCaptureProfileDefinition.CreateForConfiguration(configuration, configuration.Schedule)),
                snapshot.ActiveRevision.ProfileSha256);
            Assert.AreEqual(CapturePipelineSchemaVersions.ExplicitV2, effective.Pipeline.SchemaVersion);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_PersistedCurrentV2LegacyControlPoliciesNormalizeDuringHostRestart()
    {
        var cases = new[]
        {
            new LegacyControlCase(
                LocalCaptureProfileDefinition.CurrentSchemaVersion,
                null,
                AutomaticControlOwnership.Disabled,
                AutomaticControlOwnership.Disabled,
                CapturePipelineSchemaVersions.ExplicitV2),
            new LegacyControlCase(
                LocalCaptureProfileDefinition.CurrentSchemaVersion,
                new CameraControlPolicy
                {
                    AutoExposure = CameraFeatureDirective.Enabled,
                    AutoGain = CameraFeatureDirective.Disabled
                },
                AutomaticControlOwnership.HostMetered,
                AutomaticControlOwnership.Disabled,
                CapturePipelineSchemaVersions.ExplicitV2),
            new LegacyControlCase(
                LocalCaptureProfileDefinition.CurrentSchemaVersion,
                new CameraControlPolicy
                {
                    ExposureControl = AutomaticControlOwnership.CameraNative,
                    GainControl = AutomaticControlOwnership.CameraNative,
                    AutoExposure = CameraFeatureDirective.Enabled,
                    AutoGain = CameraFeatureDirective.Disabled
                },
                AutomaticControlOwnership.CameraNative,
                AutomaticControlOwnership.CameraNative,
                CapturePipelineSchemaVersions.ExplicitV2)
        };
        foreach (var testCase in cases)
        {
            var root = CreateRoot();
            try
            {
                var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
                var journalInitializer = new JournalInitializer(root);
                var configuration = HostConfiguration();
                string revisionId;
                byte[] profileJson;
                using (var store = new SqliteCaptureScheduleStore(
                    journalInitializer, options, TimeProvider.System))
                {
                    var initial = await store.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
                    var legacyConfiguration = configuration with
                    {
                        Rig = configuration.Rig with { ControlPolicy = testCase.Policy }
                    };
                    var profile = testCase.ProfileSchemaVersion == LocalCaptureProfileDefinition.CurrentSchemaVersion
                        ? LocalCaptureProfileDefinition.CreateV2(legacyConfiguration, Definition("legacy", 2))
                        : LocalCaptureProfileDefinition.Create(legacyConfiguration, Definition("legacy", 2));
                    revisionId = initial.ActiveRevision.RevisionId;
                    profileJson = System.Text.Encoding.UTF8.GetBytes(
                        CaptureContractJson.SerializeToElement(profile).GetRawText());
                    using var connection = new SqliteConnection(
                        $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
                    await connection.OpenAsync().ConfigureAwait(false);
                    using var command = connection.CreateCommand();
                    command.CommandText = """
                        UPDATE capture_schedule_revisions
                        SET profile_json = $json, profile_sha256 = $profile_sha, schedule_sha256 = $schedule_sha
                        WHERE revision_id = $id;
                        """;
                    command.Parameters.AddWithValue("$json", profileJson);
                    command.Parameters.AddWithValue(
                        "$profile_sha",
                        LocalCaptureProfileContract.ComputePersistedRevisionSha256(profile));
                    command.Parameters.AddWithValue("$schedule_sha", CaptureScheduleContract.ComputeSha256(profile.Schedule));
                    command.Parameters.AddWithValue("$id", revisionId);
                    Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
                }
                using (var document = System.Text.Json.JsonDocument.Parse(profileJson))
                {
                    var serializedPolicy = document.RootElement.GetProperty("rig").GetProperty("controlPolicy");
                    if (testCase.Policy is null)
                    {
                        Assert.AreEqual(System.Text.Json.JsonValueKind.Null, serializedPolicy.ValueKind);
                    }
                    else if (testCase.ProfileSchemaVersion == LocalCaptureProfileDefinition.CurrentSchemaVersion)
                    {
                        Assert.AreEqual(
                            testCase.Policy!.ExposureControl == AutomaticControlOwnership.Unspecified,
                            !serializedPolicy.TryGetProperty("exposureControl", out _));
                        Assert.AreEqual(
                            testCase.Policy.GainControl == AutomaticControlOwnership.Unspecified,
                            !serializedPolicy.TryGetProperty("gainControl", out _));
                        Assert.AreEqual("Enabled", serializedPolicy.GetProperty("autoExposure").GetString());
                        Assert.AreEqual("Disabled", serializedPolicy.GetProperty("autoGain").GetString());
                    }
                }

                using var restarted = new SqliteCaptureScheduleStore(
                    journalInitializer, options, TimeProvider.System);
                var accessor = new CameraAgentConfigurationAccessor();
                var hostInitializer = new CameraAgentConfigurationInitializer(
                    new StaticConfigurationLoader(configuration),
                    accessor,
                    new EmptyPipelineFactory(),
                    NullLogger<CameraAgentConfigurationInitializer>.Instance,
                    restarted);

                await hostInitializer.StartAsync(CancellationToken.None).ConfigureAwait(false);
                var effective = await accessor.WaitForConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
                var recovered = await restarted.GetRevisionAsync(revisionId, CancellationToken.None).ConfigureAwait(false);
                var named = new SqliteNamedRigProfileStore(journalInitializer, options, TimeProvider.System, restarted);
                var imported = await named.ImportActiveAsync(recovered, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(revisionId, imported.Revisions.Single().SourceScheduleRevisionId);

                Assert.AreEqual(testCase.ExpectedPipelineSchemaVersion, effective.Pipeline.SchemaVersion);
                Assert.AreEqual(
                    testCase.ExpectedExposure,
                    effective.Rig.ControlPolicy!.ExposureControl);
                Assert.AreEqual(
                    testCase.ExpectedGain,
                    effective.Rig.ControlPolicy.GainControl);
                Assert.AreEqual(testCase.ExpectedExposure, recovered.Profile.Rig.ControlPolicy!.ExposureControl);
                Assert.AreEqual(testCase.ExpectedGain, recovered.Profile.Rig.ControlPolicy.GainControl);
                Assert.IsNull(recovered.Profile.Rig.ControlPolicy.AutoExposure);
                Assert.IsNull(recovered.Profile.Rig.ControlPolicy.AutoGain);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task InitializeAsync_InactiveChecksumValidPersistedV1ProfileFailsClosed()
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var initializer = new JournalInitializer(root);
            var configuration = HostConfiguration();
            using (var store = new SqliteCaptureScheduleStore(initializer, options, TimeProvider.System))
            {
                _ = await store.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
                var legacy = LocalCaptureProfileDefinition.Create(configuration, configuration.Schedule!);
                var profileJson = System.Text.Encoding.UTF8.GetBytes(
                    CaptureContractJson.SerializeToElement(legacy).GetRawText());
                using var document = System.Text.Json.JsonDocument.Parse(profileJson);
                var rawProfileSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(document.RootElement);
                using var connection = new SqliteConnection(
                    $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO capture_schedule_revisions(
                        revision_id, revision_number, profile_json, profile_sha256, schedule_sha256,
                        source, actor, reason, created_unix_ms)
                    VALUES ('inactive-legacy-v1', 2, $json, $profile_sha, $schedule_sha,
                            'test', 'test', 'inactive legacy row', 1);
                    """;
                command.Parameters.AddWithValue("$json", profileJson);
                command.Parameters.AddWithValue("$profile_sha", rawProfileSha256);
                command.Parameters.AddWithValue(
                    "$schedule_sha",
                    CaptureScheduleContract.ComputeSha256(legacy.Schedule));
                Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
                var validation = LocalCaptureProfileContract.ValidatePersistedRevision(legacy);
                Assert.IsFalse(validation.IsValid);
                Assert.AreEqual("localProfile.schemaVersion", validation.FieldPath);
            }
            using var restarted = new SqliteCaptureScheduleStore(initializer, options, TimeProvider.System);

            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                restarted.InitializeAsync(configuration, CancellationToken.None)).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record LegacyControlCase(
        string ProfileSchemaVersion,
        CameraControlPolicy? Policy,
        AutomaticControlOwnership ExpectedExposure,
        AutomaticControlOwnership ExpectedGain,
        string ExpectedPipelineSchemaVersion);

    [TestMethod]
    public async Task InitializeAsync_CollapsesNormalizedDuplicatePendingRevisionDuringRecovery()
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var initializer = new JournalInitializer(root);
            var configuration = HostConfiguration();
            var timeProvider = new SettableTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var seed = await SeedNormalizedDuplicateRevisionsAsync(
                root, options, initializer, configuration, timeProvider).ConfigureAwait(false);
            var staged = seed.Snapshot;
            var activePersistedSha256 = LocalCaptureProfileContract.ComputePersistedRevisionSha256(
                seed.ActivePersistedProfile);
            var pendingPersistedSha256 = LocalCaptureProfileContract.ComputePersistedRevisionSha256(
                seed.PendingPersistedProfile);
            Assert.AreNotEqual(activePersistedSha256, pendingPersistedSha256);
            Assert.AreEqual(
                LocalCaptureProfileContract.ComputeEffectiveSha256(
                    seed.ActivePersistedProfile.NormalizePersistedRevisionForRead()),
                LocalCaptureProfileContract.ComputeEffectiveSha256(
                    seed.PendingPersistedProfile.NormalizePersistedRevisionForRead()));
            Assert.AreEqual(
                CaptureScheduleContract.ComputeSha256(seed.ActivePersistedProfile.Schedule),
                CaptureScheduleContract.ComputeSha256(seed.PendingPersistedProfile.Schedule));
            var recoveryUtc = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
            timeProvider.UtcNow = recoveryUtc;
            using var restarted = new SqliteCaptureScheduleStore(initializer, options, timeProvider);

            var recovered = await restarted.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
            var operatorState = await restarted.GetOperatorStateAsync(10, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(staged.ActiveRevision.RevisionId, recovered.ActiveRevision.RevisionId);
            Assert.IsNull(recovered.PendingRevision);
            Assert.AreEqual(staged.Version + 1, recovered.Version);
            Assert.AreEqual(recoveryUtc, recovered.UpdatedUtc);
            Assert.AreEqual(2, operatorState.History.Count);
            CollectionAssert.AreEquivalent(
                new[] { staged.ActiveRevision.RevisionId, staged.PendingRevision!.RevisionId },
                operatorState.History.Select(revision => revision.RevisionId).ToArray());
            Assert.AreEqual(
                LocalCaptureProfileContract.ComputeSha256(SqliteCaptureScheduleStore.CreateFileProfile(configuration)),
                operatorState.FileConfigurationProfileSha256);
            using (var verification = new SqliteConnection(
                $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}"))
            {
                await verification.OpenAsync().ConfigureAwait(false);
                Assert.AreEqual(2, await CountRevisionsAsync(verification).ConfigureAwait(false));
                Assert.AreEqual(1, await CountActivationsAsync(verification).ConfigureAwait(false));
                using var command = verification.CreateCommand();
                command.CommandText = "SELECT profile_sha256 FROM capture_schedule_revisions ORDER BY revision_number;";
                using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
                Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
                Assert.AreEqual(activePersistedSha256, reader.GetString(0));
                Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
                Assert.AreEqual(pendingPersistedSha256, reader.GetString(0));
                Assert.IsFalse(await reader.ReadAsync().ConfigureAwait(false));
            }
            timeProvider.UtcNow = recoveryUtc.AddDays(1);

            var repeated = await restarted.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(recovered.ActiveRevision.RevisionId, repeated.ActiveRevision.RevisionId);
            Assert.IsNull(repeated.PendingRevision);
            Assert.AreEqual(recovered.Version, repeated.Version);
            Assert.AreEqual(recovered.UpdatedUtc, repeated.UpdatedUtc);
            using var repeatedVerification = new SqliteConnection(
                $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
            await repeatedVerification.OpenAsync().ConfigureAwait(false);
            Assert.AreEqual(2, await CountRevisionsAsync(repeatedVerification).ConfigureAwait(false));
            Assert.AreEqual(1, await CountActivationsAsync(repeatedVerification).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_ReplacesNormalizedDuplicatePendingWithDistinctFileDraftOnce()
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var initializer = new JournalInitializer(root);
            var persistedConfiguration = HostConfiguration();
            var fileConfiguration = persistedConfiguration with { Schedule = Definition("file-current", 3) };
            var timeProvider = new SettableTimeProvider(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
            var seed = await SeedNormalizedDuplicateRevisionsAsync(
                root, options, initializer, persistedConfiguration, timeProvider).ConfigureAwait(false);
            var recoveryUtc = new DateTimeOffset(2026, 2, 2, 0, 0, 0, TimeSpan.Zero);
            timeProvider.UtcNow = recoveryUtc;
            var expectedFileProfile = SqliteCaptureScheduleStore.CreateFileProfile(fileConfiguration);
            var expectedFileSha256 = LocalCaptureProfileContract.ComputeSha256(expectedFileProfile);
            using var restarted = new SqliteCaptureScheduleStore(initializer, options, timeProvider);

            var recovered = await restarted.InitializeAsync(
                fileConfiguration, CancellationToken.None).ConfigureAwait(false);
            var operatorState = await restarted.GetOperatorStateAsync(10, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(seed.Snapshot.ActiveRevision.RevisionId, recovered.ActiveRevision.RevisionId);
            Assert.IsNotNull(recovered.PendingRevision);
            Assert.AreEqual(expectedFileSha256, recovered.PendingRevision.ProfileSha256);
            Assert.AreEqual("file-draft", recovered.PendingRevision.Source);
            Assert.AreEqual(3, recovered.PendingRevision.RevisionNumber);
            Assert.AreEqual($"profile-00000003-{expectedFileSha256[..12]}", recovered.PendingRevision.RevisionId);
            Assert.AreEqual(seed.Snapshot.Version + 1, recovered.Version);
            Assert.AreEqual(recoveryUtc, recovered.UpdatedUtc);
            Assert.AreEqual(expectedFileSha256, operatorState.FileConfigurationProfileSha256);
            Assert.AreEqual(3, operatorState.History.Count);
            CollectionAssert.AreEquivalent(
                new[]
                {
                    seed.Snapshot.ActiveRevision.RevisionId,
                    seed.Snapshot.PendingRevision!.RevisionId,
                    recovered.PendingRevision.RevisionId
                },
                operatorState.History.Select(revision => revision.RevisionId).ToArray());
            using (var verification = new SqliteConnection(
                $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}"))
            {
                await verification.OpenAsync().ConfigureAwait(false);
                Assert.AreEqual(3, await CountRevisionsAsync(verification).ConfigureAwait(false));
                Assert.AreEqual(1, await CountActivationsAsync(verification).ConfigureAwait(false));
            }
            timeProvider.UtcNow = recoveryUtc.AddDays(1);

            var repeated = await restarted.InitializeAsync(
                fileConfiguration, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(recovered.ActiveRevision.RevisionId, repeated.ActiveRevision.RevisionId);
            Assert.AreEqual(recovered.PendingRevision.RevisionId, repeated.PendingRevision!.RevisionId);
            Assert.AreEqual(recovered.Version, repeated.Version);
            Assert.AreEqual(recovered.UpdatedUtc, repeated.UpdatedUtc);
            using var repeatedVerification = new SqliteConnection(
                $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
            await repeatedVerification.OpenAsync().ConfigureAwait(false);
            Assert.AreEqual(3, await CountRevisionsAsync(repeatedVerification).ConfigureAwait(false));
            Assert.AreEqual(1, await CountActivationsAsync(repeatedVerification).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_DoesNotCollapsePendingRevisionForMatchingScheduleOnly()
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var initializer = new JournalInitializer(root);
            var configuration = HostConfiguration();
            CaptureScheduleStoreSnapshot staged;
            using (var seedStore = new SqliteCaptureScheduleStore(initializer, options, TimeProvider.System))
            {
                var initial = await seedStore.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
                var pendingProfile = LocalCaptureProfileDefinition.CreateV2(
                    configuration with
                    {
                        Rig = configuration.Rig with
                        {
                            ControlPolicy = new CameraControlPolicy
                            {
                                ExposureControl = AutomaticControlOwnership.CameraNative,
                                GainControl = AutomaticControlOwnership.Disabled
                            }
                        }
                    },
                    configuration.Schedule!);
                staged = await seedStore.StageAsync(
                    pendingProfile,
                    "matching-schedule",
                    initial.Version,
                    "test",
                    null,
                    CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(staged.ActiveRevision.ScheduleSha256, staged.PendingRevision!.ScheduleSha256);
                Assert.AreNotEqual(staged.ActiveRevision.ProfileSha256, staged.PendingRevision.ProfileSha256);
            }
            using var restarted = new SqliteCaptureScheduleStore(initializer, options, TimeProvider.System);

            var recovered = await restarted.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(staged.ActiveRevision.RevisionId, recovered.ActiveRevision.RevisionId);
            Assert.AreEqual(staged.PendingRevision!.RevisionId, recovered.PendingRevision!.RevisionId);
            Assert.AreEqual(staged.Version, recovered.Version);
            Assert.AreEqual(staged.UpdatedUtc, recovered.UpdatedUtc);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(LocalCaptureProfileDefinition.CurrentSchemaVersion, true)]
    [DataRow(LocalCaptureProfileDefinition.CurrentSchemaVersion, false)]
    public async Task InitializeAsync_ChecksumValidUndefinedLegacyDirectiveFailsBeforeNormalization(
        string schemaVersion,
        bool invalidExposure)
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var initializer = new JournalInitializer(root);
            var configuration = HostConfiguration();
            LocalCaptureProfileDefinition malformedProfile;
            string revisionId;
            using (var seedStore = new SqliteCaptureScheduleStore(initializer, options, TimeProvider.System))
            {
                var initial = await seedStore.InitializeAsync(
                    configuration, CancellationToken.None).ConfigureAwait(false);
                var malformedConfiguration = configuration with
                {
                    Rig = configuration.Rig with
                    {
                        ControlPolicy = new CameraControlPolicy
                        {
                            AutoExposure = invalidExposure
                                ? (CameraFeatureDirective)int.MaxValue
                                : CameraFeatureDirective.Enabled,
                            AutoGain = invalidExposure
                                ? CameraFeatureDirective.Disabled
                                : (CameraFeatureDirective)int.MaxValue
                        }
                    }
                };
                malformedProfile = schemaVersion == LocalCaptureProfileDefinition.LegacySchemaVersion
                    ? LocalCaptureProfileDefinition.Create(malformedConfiguration, configuration.Schedule!)
                    : LocalCaptureProfileDefinition.CreateV2(malformedConfiguration, configuration.Schedule!);
                var serializableConfiguration = malformedConfiguration with
                {
                    Rig = malformedConfiguration.Rig with
                    {
                        ControlPolicy = new CameraControlPolicy
                        {
                            AutoExposure = CameraFeatureDirective.Enabled,
                            AutoGain = CameraFeatureDirective.Disabled
                        }
                    }
                };
                var serializableProfile = schemaVersion == LocalCaptureProfileDefinition.LegacySchemaVersion
                    ? LocalCaptureProfileDefinition.Create(serializableConfiguration, configuration.Schedule!)
                    : LocalCaptureProfileDefinition.CreateV2(serializableConfiguration, configuration.Schedule!);
                revisionId = initial.ActiveRevision.RevisionId;
                var profileNode = System.Text.Json.Nodes.JsonNode.Parse(
                    CaptureContractJson.SerializeToElement(serializableProfile).GetRawText())!.AsObject();
                profileNode["rig"]!["controlPolicy"]![invalidExposure ? "autoExposure" : "autoGain"] = int.MaxValue;
                var profileJson = System.Text.Encoding.UTF8.GetBytes(profileNode.ToJsonString());
                using var profileDocument = System.Text.Json.JsonDocument.Parse(profileJson);
                var rawProfileSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(profileDocument.RootElement);
                using var connection = new SqliteConnection(
                    $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE capture_schedule_revisions
                    SET profile_json = $json, profile_sha256 = $profile_sha
                    WHERE revision_id = $revision;
                    """;
                command.Parameters.AddWithValue("$json", profileJson);
                command.Parameters.AddWithValue("$profile_sha", rawProfileSha256);
                command.Parameters.AddWithValue("$revision", revisionId);
                Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
            }
            var validation = LocalCaptureProfileContract.ValidatePersistedRevision(malformedProfile);
            Assert.IsFalse(validation.IsValid);
            Assert.AreEqual(
                invalidExposure
                    ? "localProfile.rig.controlPolicy.autoExposure"
                    : "localProfile.rig.controlPolicy.autoGain",
                validation.FieldPath);
            using var restarted = new SqliteCaptureScheduleStore(initializer, options, TimeProvider.System);

            _ = await Assert.ThrowsAsync<InvalidDataException>(() => restarted.GetRevisionAsync(
                revisionId, CancellationToken.None)).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task StageAsync_RejectsLegacyAndMalformedCurrentProfiles()
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            using var store = new SqliteCaptureScheduleStore(
                new JournalInitializer(root), options, TimeProvider.System);
            var configuration = HostConfiguration();
            var initial = await store.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
            var legacy = LocalCaptureProfileDefinition.Create(configuration, Definition("legacy", 2));
            var malformedCurrentProfiles = new[]
            {
                LocalCaptureProfileDefinition.CreateV2(
                    configuration with
                    {
                        Rig = configuration.Rig with { ControlPolicy = new CameraControlPolicy() }
                    },
                    Definition("unspecified", 2)),
                LocalCaptureProfileDefinition.CreateV2(
                    configuration with
                    {
                        Rig = configuration.Rig with
                        {
                            ControlPolicy = configuration.Rig.ControlPolicy! with
                            {
                                AutoExposure = CameraFeatureDirective.Enabled,
                                AutoGain = CameraFeatureDirective.Disabled
                            }
                        }
                    },
                    Definition("legacy-directives", 2)),
                LocalCaptureProfileDefinition.CreateV2(
                    configuration,
                    Definition("legacy-schedule", 2) with
                    {
                        WeeklyWindows = [],
                        LegacyAlwaysOpen = true,
                        LegacySetpointProfileId = "legacy-schedule"
                    })
            };

            _ = await Assert.ThrowsAsync<ArgumentException>(() => store.StageAsync(
                legacy, "stage-v1", initial.Version, "test", null, CancellationToken.None)).ConfigureAwait(false);
            foreach (var (malformedCurrent, index) in malformedCurrentProfiles.Select((profile, index) => (profile, index)))
            {
                _ = await Assert.ThrowsAsync<ArgumentException>(() => store.StageAsync(
                    malformedCurrent,
                    $"stage-malformed-v2-{index}",
                    initial.Version,
                    "test",
                    null,
                    CancellationToken.None)).ConfigureAwait(false);
            }

            var unchanged = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(initial.Version, unchanged.Version);
            Assert.IsNull(unchanged.PendingRevision);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(LocalCaptureProfileDefinition.CurrentSchemaVersion)]
    public async Task StageAsync_PreUpgradeLegacyCommandReplaysButNewKeyIsRejected(string schemaVersion)
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var initializer = new JournalInitializer(root);
            var configuration = HostConfiguration();
            const string idempotencyKey = "pre-upgrade-stage";
            const string actor = "test";
            const string reason = "pre-upgrade";
            long? expectedVersion;
            CaptureScheduleStoreSnapshot staged;
            LocalCaptureProfileDefinition persistedProfile;
            using (var store = new SqliteCaptureScheduleStore(initializer, options, TimeProvider.System))
            {
                var initial = await store.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
                expectedVersion = initial.Version;
                var schedule = Definition("upgrade", 2);
                staged = await store.StageAsync(
                    LocalCaptureProfileDefinition.CreateV2(configuration, schedule),
                    idempotencyKey,
                    expectedVersion,
                    actor,
                    reason,
                    CancellationToken.None).ConfigureAwait(false);
                var legacyConfiguration = configuration with
                {
                    Rig = configuration.Rig with
                    {
                        ControlPolicy = new CameraControlPolicy
                        {
                            AutoExposure = CameraFeatureDirective.Enabled,
                            AutoGain = CameraFeatureDirective.Disabled
                        }
                    }
                };
                persistedProfile = schemaVersion == LocalCaptureProfileDefinition.LegacySchemaVersion
                    ? LocalCaptureProfileDefinition.Create(legacyConfiguration, schedule)
                    : LocalCaptureProfileDefinition.CreateV2(legacyConfiguration, schedule);
            }
            var profileJson = System.Text.Encoding.UTF8.GetBytes(
                CaptureContractJson.SerializeToElement(persistedProfile).GetRawText());
            var commandSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(new
            {
                CommandKind = "stage",
                Payload = persistedProfile,
                expectedVersion,
                actor,
                reason
            });
            using (var connection = new SqliteConnection(
                $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE capture_schedule_revisions
                    SET profile_json = $json, profile_sha256 = $profile_sha, schedule_sha256 = $schedule_sha
                    WHERE revision_id = $revision;
                    UPDATE capture_schedule_commands
                    SET payload_sha256 = $command_sha
                    WHERE idempotency_key = $operation;
                    """;
                command.Parameters.AddWithValue("$json", profileJson);
                command.Parameters.AddWithValue(
                    "$profile_sha",
                    LocalCaptureProfileContract.ComputePersistedRevisionSha256(persistedProfile));
                command.Parameters.AddWithValue(
                    "$schedule_sha",
                    CaptureScheduleContract.ComputeSha256(persistedProfile.Schedule));
                command.Parameters.AddWithValue("$revision", staged.PendingRevision!.RevisionId);
                command.Parameters.AddWithValue("$command_sha", commandSha256);
                command.Parameters.AddWithValue("$operation", idempotencyKey);
                Assert.AreEqual(2, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
            }

            using var restarted = new SqliteCaptureScheduleStore(initializer, options, TimeProvider.System);
            var replay = await restarted.StageAsync(
                persistedProfile,
                idempotencyKey,
                expectedVersion,
                actor,
                reason,
                CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(staged.Version, replay.Version);
            Assert.IsNotNull(replay.PendingRevision);
            Assert.AreEqual(schemaVersion, replay.PendingRevision.Profile.SchemaVersion);
            Assert.AreEqual(
                LocalCaptureProfileContract.ComputeEffectiveSha256(replay.PendingRevision.Profile),
                replay.PendingRevision.ProfileSha256);
            _ = await Assert.ThrowsAsync<ArgumentException>(() => restarted.StageAsync(
                persistedProfile,
                $"{idempotencyKey}-new",
                staged.Version,
                actor,
                reason,
                CancellationToken.None)).ConfigureAwait(false);
            _ = await Assert.ThrowsAsync<CaptureScheduleStoreConflictException>(() => restarted.StageAsync(
                persistedProfile with { Schedule = Definition("mismatch", 3) },
                idempotencyKey,
                expectedVersion,
                actor,
                reason,
                CancellationToken.None)).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task StageAsync_RecoveredNormalizedActiveProfileIsEffectiveNoOp()
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var initializer = new JournalInitializer(root);
            var configuration = HostConfiguration();
            using (var seedStore = new SqliteCaptureScheduleStore(initializer, options, TimeProvider.System))
            {
                var initial = await seedStore.InitializeAsync(
                    configuration, CancellationToken.None).ConfigureAwait(false);
                var persistedConfiguration = configuration with
                {
                    Rig = configuration.Rig with
                    {
                        ControlPolicy = new CameraControlPolicy
                        {
                            AutoExposure = CameraFeatureDirective.Disabled,
                            AutoGain = CameraFeatureDirective.Disabled
                        }
                    }
                };
                var persistedProfile = LocalCaptureProfileDefinition.CreateV2(
                    persistedConfiguration, configuration.Schedule!);
                var profileJson = System.Text.Encoding.UTF8.GetBytes(
                    CaptureContractJson.SerializeToElement(persistedProfile).GetRawText());
                using var connection = new SqliteConnection(
                    $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE capture_schedule_revisions
                    SET profile_json = $json, profile_sha256 = $profile_sha
                    WHERE revision_id = $revision;
                    """;
                command.Parameters.AddWithValue("$json", profileJson);
                command.Parameters.AddWithValue(
                    "$profile_sha",
                    LocalCaptureProfileContract.ComputePersistedRevisionSha256(persistedProfile));
                command.Parameters.AddWithValue("$revision", initial.ActiveRevision.RevisionId);
                Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
            }

            using var restarted = new SqliteCaptureScheduleStore(initializer, options, TimeProvider.System);
            var recovered = await restarted.InitializeAsync(
                configuration, CancellationToken.None).ConfigureAwait(false);
            using var before = new SqliteConnection(
                $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
            await before.OpenAsync().ConfigureAwait(false);
            var revisionCount = await CountRevisionsAsync(before).ConfigureAwait(false);
            await before.CloseAsync().ConfigureAwait(false);

            var replayed = await restarted.StageAsync(
                recovered.ActiveRevision.Profile,
                "stage-normalized-active",
                recovered.Version,
                "test",
                null,
                CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(recovered.ActiveRevision.RevisionId, replayed.ActiveRevision.RevisionId);
            Assert.AreEqual(recovered.Version, replayed.Version);
            Assert.IsNull(replayed.PendingRevision);
            using var verification = new SqliteConnection(
                $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
            await verification.OpenAsync().ConfigureAwait(false);
            Assert.AreEqual(
                revisionCount,
                await CountRevisionsAsync(verification).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CameraModuleConfig HostConfiguration()
    {
        var configuration = Configuration();
        return configuration with
        {
            AgentId = "test-agent",
            Rig = configuration.Rig with
            {
                Optics = configuration.Rig.Optics with { ImageCircleRadiusPixels = 1 },
                Pipeline = configuration.Rig.Pipeline with
                {
                    Envelope = new ExposureEnvelope(
                        TimeSpan.FromSeconds(1),
                        TimeSpan.FromSeconds(5),
                        1,
                        10,
                        new ExposureDefaults(TimeSpan.FromSeconds(1), 1),
                        new ExposureDefaults(TimeSpan.FromSeconds(5), 10),
                        0.5)
                },
                ControlPolicy = new CameraControlPolicy
                {
                    ExposureControl = AutomaticControlOwnership.Disabled,
                    GainControl = AutomaticControlOwnership.Disabled
                }
            },
            DeploymentLocation = DeploymentLocationSnapshot.Create(
                "test-location",
                1,
                "test",
                null,
                DateTimeOffset.UnixEpoch,
                null,
                configuration.Observatory.LatitudeDegrees,
                configuration.Observatory.LongitudeDegrees,
                configuration.Observatory.ElevationMeters,
                configuration.Observatory.TimeZoneId)
        };
    }

    private sealed class StaticConfigurationLoader(CameraModuleConfig configuration)
        : ICameraAgentConfigurationLoader
    {
        public Task<CameraModuleConfig> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(configuration);
    }

    private sealed class EmptyPipelineFactory : ICaptureProcessingPipelineFactory
    {
        public CaptureProcessingGraph CreateGraph(CameraModuleConfig config) => new([]);

        public CaptureProcessingPlanPreview PreviewPlan(CameraModuleConfig config)
            => throw new NotSupportedException();
    }

    private sealed class FailingModuleFactory : ICameraModuleFactory
    {
        public ICameraModule Create(string moduleType) => new TestModule(true);
    }

    private sealed class WorkingModuleFactory : ICameraModuleFactory
    {
        public ICameraModule Create(string moduleType) => new TestModule(false);
    }

    private sealed class TestModule(bool fail) : ICameraModule
    {
        public int CaptureCalls { get; private set; }
        public string Id => "test";
        public string DisplayName => "test";
        public string ModuleType => "test";
        public CameraModuleCapabilities Capabilities => throw new NotSupportedException();
        public Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken)
            => fail ? throw new InvalidOperationException("SDK mismatch") : Task.CompletedTask;
        public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
        {
            CaptureCalls++;
            throw new NotSupportedException();
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [TestMethod]
    public async Task Mutations_RequireExpectedDurableVersion()
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            using var store = new SqliteCaptureScheduleStore(
                new JournalInitializer(root), options, TimeProvider.System);
            _ = await store.InitializeAsync(Configuration(), CancellationToken.None).ConfigureAwait(false);

            _ = await Assert.ThrowsAsync<ArgumentException>(() => store.StageAsync(
                LocalCaptureProfileDefinition.CreateV2(Configuration(), Definition("night", 2)),
                "missing-version",
                null,
                "owner",
                null,
                CancellationToken.None)).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task PersistedPreview_WhenChecksumIsCorrupted_FailsClosed()
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            using var store = new SqliteCaptureScheduleStore(
                new JournalInitializer(root), options, TimeProvider.System);
            var state = await store.InitializeAsync(Configuration(), CancellationToken.None).ConfigureAwait(false);
            var location = new CaptureLocationProvenance(
                "test-location", 1, "test", null, DateTimeOffset.UnixEpoch, null);
            var now = DateTimeOffset.UtcNow;
            var preview = CaptureScheduleIntervalExpander.Expand(
                state.ActiveRevision.Definition,
                DateOnly.FromDateTime(now.UtcDateTime),
                1,
                TimeZoneInfo.Utc,
                Configuration().Observatory,
                new AstronomyEngineSolarEventCalculator());
            await store.PersistPreviewAsync(
                state.ActiveRevision, location, preview, CancellationToken.None).ConfigureAwait(false);
            using (var connection = new SqliteConnection(
                $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE capture_schedule_expansions SET expansion_sha256 = $sha;";
                command.Parameters.AddWithValue("$sha", new string('F', 64));
                _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            _ = await Assert.ThrowsAsync<InvalidDataException>(() => store.TryReadPreviewAsync(
                state.ActiveRevision, location, now, CancellationToken.None)).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task StageActivateAndOneShotConsumption_AreDurableAndIdempotent()
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var initializer = new JournalInitializer(root);
            CaptureScheduleStoreSnapshot activated;
            using (var store = new SqliteCaptureScheduleStore(initializer, options, TimeProvider.System))
            {
                var initial = await store.InitializeAsync(Configuration(), CancellationToken.None).ConfigureAwait(false);
                var profile = LocalCaptureProfileDefinition.CreateV2(Configuration(), Definition("night", 2));
                var staged = (await store.StageWithCurrentFromBasisAsync(
                    profile,
                    initial.ActiveRevision.RevisionId,
                    "stage-1",
                    initial.Version,
                    "owner",
                    "night operations",
                    CancellationToken.None).ConfigureAwait(false)).Receipt;
                var replay = (await store.StageWithCurrentFromBasisAsync(
                    profile,
                    initial.ActiveRevision.RevisionId,
                    "stage-1",
                    initial.Version,
                    "owner",
                    "night operations",
                    CancellationToken.None).ConfigureAwait(false)).Receipt;
                Assert.AreEqual(staged.Version, replay.Version);

                activated = await store.ActivateAsync(
                    staged.PendingRevision!.RevisionId,
                    "activate-1",
                    staged.Version,
                    "owner",
                    "approved",
                    CancellationToken.None).ConfigureAwait(false);
                var lateReplay = (await store.StageWithCurrentFromBasisAsync(
                    profile,
                    initial.ActiveRevision.RevisionId,
                    "stage-1",
                    initial.Version,
                    "owner",
                    "night operations",
                    CancellationToken.None).ConfigureAwait(false)).Receipt;
                Assert.AreEqual(staged.Version, lateReplay.Version);
                Assert.AreEqual(staged.ActiveRevision.RevisionId, lateReplay.ActiveRevision.RevisionId);
                Assert.AreEqual(staged.PendingRevision!.RevisionId, lateReplay.PendingRevision!.RevisionId);
                _ = await Assert.ThrowsAsync<CaptureScheduleStoreConflictException>(() =>
                    store.StageWithCurrentFromBasisAsync(
                        profile,
                        initial.ActiveRevision.RevisionId,
                        "stage-stale-basis",
                        activated.Version,
                        "owner",
                        "stale basis",
                        CancellationToken.None)).ConfigureAwait(false);
                var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                var scheduleOverride = new CaptureScheduleOverride(
                    "override-1",
                    activated.ActiveRevision.RevisionId,
                    activated.ActiveRevision.ScheduleSha256,
                    CaptureScheduleOverrideMode.ForceOpen,
                    now.AddMinutes(-1),
                    now.AddMinutes(10),
                    "night",
                    OneShot: true);
                await store.AddOverrideAsync(
                    scheduleOverride,
                    "override-add-1",
                    activated.Version,
                    "owner",
                    "test",
                    CancellationToken.None).ConfigureAwait(false);
                using var competing = new SqliteCaptureScheduleStore(initializer, options, TimeProvider.System);
                var grants = await Task.WhenAll(
                    store.GrantAdmissionAsync(
                        "admission-1", activated.ActiveRevision.RevisionId,
                        scheduleOverride.Id, now, CancellationToken.None),
                    competing.GrantAdmissionAsync(
                        "admission-2", activated.ActiveRevision.RevisionId,
                        scheduleOverride.Id, now, CancellationToken.None)).ConfigureAwait(false);
                Assert.AreEqual(1, grants.Count(static granted => granted));

                var location = new CaptureLocationProvenance(
                    "test-location", 1, "test", null, DateTimeOffset.UnixEpoch, null);
                var preview = CaptureScheduleIntervalExpander.Expand(
                    activated.ActiveRevision.Definition,
                    DateOnly.FromDateTime(now.UtcDateTime),
                    1,
                    TimeZoneInfo.Utc,
                    Configuration().Observatory,
                    new AstronomyEngineSolarEventCalculator());
                await store.PersistPreviewAsync(
                    activated.ActiveRevision, location, preview, CancellationToken.None).ConfigureAwait(false);
                var persistedPreview = await competing.TryReadPreviewAsync(
                    activated.ActiveRevision, location, now, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(persistedPreview);
                Assert.AreEqual(preview.ExpansionSha256, persistedPreview.ExpansionSha256);
            }

            using var restarted = new SqliteCaptureScheduleStore(initializer, options, TimeProvider.System);
            var recovered = await restarted.InitializeAsync(Configuration(), CancellationToken.None).ConfigureAwait(false);
            var overrides = await restarted.GetActiveOverridesAsync(
                recovered.ActiveRevision.RevisionId,
                DateTimeOffset.UtcNow,
                CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(activated.ActiveRevision.RevisionId, recovered.ActiveRevision.RevisionId);
            Assert.HasCount(1, overrides);
            Assert.IsNotNull(overrides[0].ConsumedUtc);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RollbackIsDurableIdempotentAndRejectsNonHistoricalTargets()
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var initializer = new JournalInitializer(root);
            using var store = new SqliteCaptureScheduleStore(initializer, options, TimeProvider.System);
            var initial = await store.InitializeAsync(Configuration(), CancellationToken.None).ConfigureAwait(false);
            var profile = LocalCaptureProfileDefinition.CreateV2(Configuration(), Definition("night", 2));
            var staged = await store.StageAsync(
                profile, "stage-for-rollback", initial.Version, "owner", null, CancellationToken.None)
                .ConfigureAwait(false);
            var activated = await store.ActivateAsync(
                staged.PendingRevision!.RevisionId,
                "activate-before-rollback",
                staged.Version,
                "owner",
                null,
                CancellationToken.None).ConfigureAwait(false);
            var unchanged = await store.StageAsync(
                activated.ActiveRevision.Profile,
                "stage-active-noop",
                activated.Version,
                "owner",
                null,
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(activated.Version, unchanged.Version);
            Assert.IsNull(unchanged.PendingRevision);
            _ = await Assert.ThrowsExactlyAsync<CaptureScheduleStoreConflictException>(() =>
                store.ActivateWithCurrentAsync(
                    initial.ActiveRevision.RevisionId,
                    "activate-backward",
                    activated.Version,
                    "owner",
                    null,
                    CancellationToken.None)).ConfigureAwait(false);
            var historical = await store.StageAsync(
                initial.ActiveRevision.Profile,
                "stage-historical",
                activated.Version,
                "owner",
                null,
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevision.RevisionId, historical.PendingRevision!.RevisionId);
            var cancelled = await store.StageAsync(
                activated.ActiveRevision.Profile,
                "cancel-pending",
                historical.Version,
                "owner",
                null,
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(cancelled.PendingRevision);
            Assert.AreEqual(historical.Version + 1, cancelled.Version);
            historical = await store.StageAsync(
                initial.ActiveRevision.Profile,
                "stage-historical-again",
                cancelled.Version,
                "owner",
                null,
                CancellationToken.None).ConfigureAwait(false);

            var rolledBack = (await store.RollbackWithCurrentAsync(
                initial.ActiveRevision.RevisionId,
                "rollback-1",
                historical.Version,
                "owner",
                "restore previous schedule",
                CancellationToken.None).ConfigureAwait(false)).Receipt;
            var replay = (await store.RollbackWithCurrentAsync(
                initial.ActiveRevision.RevisionId,
                "rollback-1",
                historical.Version,
                "owner",
                "restore previous schedule",
                CancellationToken.None).ConfigureAwait(false)).Receipt;

            Assert.AreEqual(initial.ActiveRevision.RevisionId, rolledBack.ActiveRevision.RevisionId);
            Assert.AreEqual(rolledBack.Version, replay.Version);
            _ = await Assert.ThrowsExactlyAsync<CaptureScheduleStoreConflictException>(() =>
                store.RollbackWithCurrentAsync(
                    activated.ActiveRevision.RevisionId,
                    "rollback-forward",
                    rolledBack.Version,
                    "owner",
                    null,
                    CancellationToken.None)).ConfigureAwait(false);
            _ = await Assert.ThrowsExactlyAsync<CaptureScheduleStoreConflictException>(() =>
                store.ActivateWithCurrentAsync(
                    initial.ActiveRevision.RevisionId,
                    "rollback-1",
                    rolledBack.Version,
                    "owner",
                    "restore previous schedule",
                    CancellationToken.None)).ConfigureAwait(false);

            using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT command_kind FROM capture_schedule_commands WHERE idempotency_key = 'rollback-1';";
            Assert.AreEqual("rollback", await command.ExecuteScalarAsync().ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-schedule-store", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static CameraModuleConfig Configuration()
        => new(
            new ObservatoryLocation(35, -114, 1000, "UTC"),
            new CameraModuleDescriptor("test"),
            new CameraRigConfig(
                new SensorProfile("test", 2, 2, 5, SensorColorMode.Mono, CameraPixelFormat.Mono16),
                new OpticsProfile("Perspective", 50, 10, 0),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(10),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(5),
                    1,
                    10),
                new CameraControlPolicy
                {
                    ExposureControl = AutomaticControlOwnership.Disabled,
                    GainControl = AutomaticControlOwnership.Disabled
                }),
            CapturePipelineConfig.Empty)
        {
            Schedule = Definition("initial", 1)
        };

    private static CaptureScheduleDefinition Definition(string profileId, double gain)
        => new(
            "capture-schedule-v1",
            [new CaptureScheduleSetpointProfile(
                profileId,
                TimeSpan.FromSeconds(5),
                gain,
                TimeSpan.FromSeconds(10))],
            [new CaptureWeeklyScheduleWindow(
                "monday",
                DayOfWeek.Monday,
                new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(18, 0)),
                new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(6, 0), DayOffset: 1),
                profileId)]);

    private static async Task<long> CountRevisionsAsync(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM capture_schedule_revisions;";
        return (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private static async Task<long> CountActivationsAsync(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM capture_schedule_activations;";
        return (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private static async Task<NormalizedDuplicateSeed> SeedNormalizedDuplicateRevisionsAsync(
        string root,
        IOptions<CameraAgentHostOptions> options,
        JournalInitializer initializer,
        CameraModuleConfig configuration,
        TimeProvider timeProvider)
    {
        CaptureScheduleStoreSnapshot staged;
        using (var seedStore = new SqliteCaptureScheduleStore(initializer, options, timeProvider))
        {
            var initial = await seedStore.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
            staged = await seedStore.StageAsync(
                LocalCaptureProfileDefinition.CreateV2(configuration, Definition("temporary-draft", 2)),
                "seed-pending",
                initial.Version,
                "test",
                null,
                CancellationToken.None).ConfigureAwait(false);
        }
        var activePersistedProfile = LocalCaptureProfileDefinition.CreateV2(
            configuration with
            {
                Rig = configuration.Rig with
                {
                    ControlPolicy = new CameraControlPolicy
                    {
                        AutoExposure = CameraFeatureDirective.Disabled,
                        AutoGain = CameraFeatureDirective.Disabled
                    }
                }
            },
            configuration.Schedule!);
        var pendingPersistedProfile = LocalCaptureProfileDefinition.CreateV2(
            configuration with
            {
                Rig = configuration.Rig with
                {
                    ControlPolicy = new CameraControlPolicy
                    {
                        ExposureControl = AutomaticControlOwnership.Disabled,
                        GainControl = AutomaticControlOwnership.Disabled,
                        AutoExposure = CameraFeatureDirective.Enabled,
                        AutoGain = CameraFeatureDirective.Enabled
                    }
                }
            },
            configuration.Schedule!);
        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE capture_schedule_revisions
            SET profile_json = $active_json,
                profile_sha256 = $active_profile_sha,
                schedule_sha256 = $schedule_sha
            WHERE revision_id = $active;
            UPDATE capture_schedule_revisions
            SET profile_json = $pending_json,
                profile_sha256 = $pending_profile_sha,
                schedule_sha256 = $schedule_sha
            WHERE revision_id = $pending;
            """;
        command.Parameters.AddWithValue(
            "$active_json",
            System.Text.Encoding.UTF8.GetBytes(
                CaptureContractJson.SerializeToElement(activePersistedProfile).GetRawText()));
        command.Parameters.AddWithValue(
            "$active_profile_sha",
            LocalCaptureProfileContract.ComputePersistedRevisionSha256(activePersistedProfile));
        command.Parameters.AddWithValue(
            "$pending_json",
            System.Text.Encoding.UTF8.GetBytes(
                CaptureContractJson.SerializeToElement(pendingPersistedProfile).GetRawText()));
        command.Parameters.AddWithValue(
            "$pending_profile_sha",
            LocalCaptureProfileContract.ComputePersistedRevisionSha256(pendingPersistedProfile));
        command.Parameters.AddWithValue(
            "$schedule_sha",
            CaptureScheduleContract.ComputeSha256(configuration.Schedule!));
        command.Parameters.AddWithValue("$active", staged.ActiveRevision.RevisionId);
        command.Parameters.AddWithValue("$pending", staged.PendingRevision!.RevisionId);
        if (await command.ExecuteNonQueryAsync().ConfigureAwait(false) != 2)
        {
            throw new InvalidOperationException("The duplicate recovery seed revisions were not updated.");
        }
        return new NormalizedDuplicateSeed(staged, activePersistedProfile, pendingPersistedProfile);
    }

    private sealed record NormalizedDuplicateSeed(
        CaptureScheduleStoreSnapshot Snapshot,
        LocalCaptureProfileDefinition ActivePersistedProfile,
        LocalCaptureProfileDefinition PendingPersistedProfile);

    private sealed class SettableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class JournalInitializer(string root) : IRawCaptureIngress
    {
        public async ValueTask InitializeAsync(CancellationToken cancellationToken)
        {
            var journal = new SqliteRawCaptureJournal(
                Path.Combine(root, "journal", "raw-ingress.db"),
                busyTimeoutSeconds: 5);
            await journal.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<RawCaptureReceipt?> AcceptAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
