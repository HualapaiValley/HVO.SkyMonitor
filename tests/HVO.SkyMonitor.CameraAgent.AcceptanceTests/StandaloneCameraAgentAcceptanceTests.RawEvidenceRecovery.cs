using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Tests.Silo;
using HVO.SkyMonitor.AgentCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

public sealed partial class StandaloneCameraAgentAcceptanceTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public async Task RawEvidenceRecoveryThroughRegisteredHostStartupPreservesPausedAdmission(bool writeOff, bool calibration)
    {
        await using var fixture = await StandaloneCameraAgentKestrelFixture.CreateAsync(useSyntheticCalibration: calibration).ConfigureAwait(false);
        var capture = await WaitForCompleteCaptureAsync(fixture.Services, fixture.Root).ConfigureAwait(false);
        var raw = capture.Artifacts.Single(static artifact => artifact.Role == FrameArtifactRole.Raw);
        var retainedPayload = await ReadArtifactBytesAsync(fixture.Services, raw.ArtifactId).ConfigureAwait(false);
        var retainedEvidence = await ReadSiloEvidenceAsync(fixture.Services, raw.ArtifactId, SiloArtifactStorage.RawEvidenceRevision)
            .ConfigureAwait(false);
        Assert.IsNotNull(retainedEvidence);
        var stage = RawIngressFileStore.GetPaths(fixture.Root, raw.ArtifactId);
        var owner = await ReadOwnerAsync(fixture.Services).ConfigureAwait(false);
        var control = fixture.Services.GetRequiredService<CaptureAdmissionCoordinator>();
        _ = await control.PauseAsync("recovery-pause", null, owner.Id, "recovery restart test", CancellationToken.None).ConfigureAwait(false);
        await fixture.StopAsync().ConfigureAwait(false);
        var database = Path.Combine(fixture.Root, "journal", "raw-ingress.db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.Parameters.AddWithValue("$sequence", capture.CaptureSequence);
            command.CommandText = """
                UPDATE raw_captures SET state='missing_evidence', retention_hold=1, failure_reason='required-file-missing'
                WHERE capture_sequence=$sequence;
                """;
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
        }
        var checksum = Convert.ToHexString(SHA256.HashData(retainedPayload));
        var backup = Path.Combine(fixture.Root, "operator-recovered-original.bin");
        await File.WriteAllBytesAsync(backup, retainedPayload).ConfigureAwait(false);

        // This fixture starts Program and keeps every AddCameraAgentInfrastructure hosted service registered.
        await fixture.StartStoppedHostAsync().ConfigureAwait(false);
        var services = fixture.Services.GetServices<IHostedService>().ToArray();
        var configurationIndex = Array.FindIndex(services, service => service is CameraAgentConfigurationInitializer);
        var admissionIndex = Array.FindIndex(services, service => service is AfterApplicationStartedService<CaptureAdmissionInitializationService>);
        Assert.IsGreaterThanOrEqualTo(0, configurationIndex);
        Assert.IsGreaterThan(configurationIndex, admissionIndex);
        control = fixture.Services.GetRequiredService<CaptureAdmissionCoordinator>();
        await WaitForConditionAsync(() => control.Snapshot.State == CaptureAdmissionState.Unavailable,
            "deferred admission to observe unavailable evidence").ConfigureAwait(false);
        Assert.AreEqual(CaptureAdmissionState.Unavailable, control.Snapshot.State);
        Assert.IsFalse(control.Snapshot.IsInitialized);
        var before = await ReadMaximumSequenceAsync(fixture.Services).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        Assert.AreEqual(before, await ReadMaximumSequenceAsync(fixture.Services).ConfigureAwait(false));

        using var client = await fixture.CreateOwnerClientAsync().ConfigureAwait(false);
        using var response = await client.GetAsync(new Uri("/api/v1/operations/raw-evidence", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var item = page.RootElement.GetProperty("items").EnumerateArray()
            .Single(value => value.GetProperty("captureId").GetGuid() == capture.CaptureId);
        var token = item.GetProperty(writeOff ? "writeOffToken" : "revalidateToken").GetString()!;
        if (!writeOff)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(stage.PayloadAbsolutePath)!);
            await File.WriteAllBytesAsync(stage.PayloadAbsolutePath, retainedPayload).ConfigureAwait(false);
            await File.WriteAllBytesAsync(stage.SidecarAbsolutePath, retainedEvidence).ConfigureAwait(false);
        }
        using var account = await client.GetAsync(new Uri("/Account/Manage/ChangePassword", UriKind.Relative)).ConfigureAwait(false);
        account.EnsureSuccessStatusCode();
        var antiforgery = OwnerBootstrapSession.ExtractAntiforgeryToken(
            await account.Content.ReadAsStringAsync().ConfigureAwait(false), "registered-host recovery");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri($"/api/v1/operations/raw-evidence/{(writeOff ? "write-off" : "revalidate")}", UriKind.Relative));
        request.Headers.Add("Idempotency-Key", "registered-host-recovery");
        request.Headers.Add("RequestVerificationToken", antiforgery);
        request.Content = JsonContent.Create(new { actionToken = token, reasonCode = writeOff ? "operator-approved-loss" : "evidence-restored" });
        using var released = await client.SendAsync(request).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, released.StatusCode, await released.Content.ReadAsStringAsync().ConfigureAwait(false));
        using var audit = JsonDocument.Parse(await released.Content.ReadAsStringAsync().ConfigureAwait(false));
        Assert.AreEqual(owner.Id, audit.RootElement.GetProperty("audit").GetProperty("actor").GetString());
        await control.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CaptureAdmissionState.Paused, control.Snapshot.State, "Evidence recovery must preserve the persisted pause.");
        var recoveredPayload = writeOff ? await File.ReadAllBytesAsync(backup).ConfigureAwait(false) :
            await ReadArtifactBytesAsync(fixture.Services, raw.ArtifactId).ConfigureAwait(false);
        Assert.AreEqual(checksum, Convert.ToHexString(SHA256.HashData(recoveredPayload)));
        _ = await control.ResumeAsync("recovery-resume", null, owner.Id, "recovered evidence", CancellationToken.None).ConfigureAwait(false);
        _ = await WaitForCompleteCaptureAsync(fixture.Services, fixture.Root, before).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("future-schema")]
    [DataRow("schema-drift")]
    public async Task RawEvidenceRecoveryStillRejectsInvalidJournalAtRegisteredHostStartup(string defect)
    {
        await using var fixture = await StandaloneCameraAgentKestrelFixture.CreateAsync().ConfigureAwait(false);
        var capture = await WaitForCompleteCaptureAsync(fixture.Services, fixture.Root).ConfigureAwait(false);
        var raw = capture.Artifacts.Single(static artifact => artifact.Role == FrameArtifactRole.Raw);
        var manifest = await ReadManifestAsync(fixture.Services, raw.ArtifactId).ConfigureAwait(false);
        var locator = await SiloInfrastructureFixture.ReadLocalLocatorAsync(fixture.Services, raw.ArtifactId,
            CaptureContractJson.ComputeDescriptorSha256(manifest.Descriptor), CancellationToken.None).ConfigureAwait(false);
        var objects = fixture.Services.GetRequiredService<SiloTestStore>();
        var checksum = SHA256.HashData(objects.Read(locator.Key, locator.VersionId));
        await fixture.StopAsync().ConfigureAwait(false);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(fixture.Root, "journal", "raw-ingress.db"),
            Pooling = false
        }.ToString()))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            if (defect == "future-schema") command.CommandText = "PRAGMA user_version=2147483647;";
            else command.CommandText = "CREATE TABLE recovery_schema_drift(id INTEGER);";
            _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        _ = await Assert.ThrowsAsync<Exception>(() => fixture.StartStoppedHostAsync()).ConfigureAwait(false);
        Assert.ThrowsExactly<InvalidOperationException>(() => { _ = fixture.Services; });
        // Deferred recovery can stop the host before Kestrel binds, surfacing a
        // cancellation wrapper. Verify the exact schema refusal independently.
        var journal = new SqliteRawCaptureJournal(Path.Combine(fixture.Root, "journal", "raw-ingress.db"), 5, usesSilo: true);
        var failure = await Assert.ThrowsAsync<Exception>(async () =>
            await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.Contains("Raw ingress", failure.ToString());
        Assert.Contains("schema", failure.ToString());
        CollectionAssert.AreEqual(checksum, SHA256.HashData(objects.Read(locator.Key, locator.VersionId)));
    }
}
