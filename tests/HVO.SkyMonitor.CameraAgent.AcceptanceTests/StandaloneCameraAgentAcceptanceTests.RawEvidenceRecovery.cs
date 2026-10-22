using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
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
        var owner = await ReadOwnerAsync(fixture.Services).ConfigureAwait(false);
        var control = fixture.Services.GetRequiredService<CaptureAdmissionCoordinator>();
        _ = await control.PauseAsync("recovery-pause", null, owner.Id, "recovery restart test", CancellationToken.None).ConfigureAwait(false);
        await fixture.StopAsync().ConfigureAwait(false);
        var database = Path.Combine(fixture.Root, "journal", "raw-ingress.db");
        string payload;
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload_relative_path FROM raw_captures WHERE capture_sequence=$sequence;";
            command.Parameters.AddWithValue("$sequence", capture.CaptureSequence);
            payload = Path.Combine(fixture.Root, (string)(await command.ExecuteScalarAsync().ConfigureAwait(false))!);
            command.CommandText = """
                UPDATE raw_captures SET state='missing_evidence', retention_hold=1, failure_reason='required-file-missing'
                WHERE capture_sequence=$sequence;
                """;
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
        }
        var checksum = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(payload).ConfigureAwait(false)));
        var backup = Path.Combine(fixture.Root, "operator-recovered-original.bin");
        File.Move(payload, backup);

        // This fixture starts Program and keeps every AddCameraAgentInfrastructure hosted service registered.
        await fixture.StartStoppedHostAsync().ConfigureAwait(false);
        var services = fixture.Services.GetServices<IHostedService>().ToArray();
        var configurationIndex = Array.FindIndex(services, service => service is CameraAgentConfigurationInitializer);
        var admissionIndex = Array.FindIndex(services, service => service is CaptureAdmissionInitializationService);
        Assert.IsGreaterThanOrEqualTo(0, configurationIndex);
        Assert.IsGreaterThan(configurationIndex, admissionIndex);
        control = fixture.Services.GetRequiredService<CaptureAdmissionCoordinator>();
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
        if (!writeOff) File.Move(backup, payload);
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
        Assert.AreEqual(checksum, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(writeOff ? backup : payload).ConfigureAwait(false))));
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
        await fixture.StopAsync().ConfigureAwait(false);
        string payload;
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(fixture.Root, "journal", "raw-ingress.db"),
            Pooling = false
        }.ToString()))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload_relative_path FROM raw_captures WHERE capture_sequence=$sequence;";
            command.Parameters.AddWithValue("$sequence", capture.CaptureSequence);
            payload = Path.Combine(fixture.Root, (string)(await command.ExecuteScalarAsync().ConfigureAwait(false))!);
            command.Parameters.Clear();
            if (defect == "future-schema") command.CommandText = "PRAGMA user_version=2147483647;";
            else command.CommandText = "CREATE TABLE recovery_schema_drift(id INTEGER);";
            _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        var checksum = SHA256.HashData(await File.ReadAllBytesAsync(payload).ConfigureAwait(false));
        var failure = await Assert.ThrowsAsync<Exception>(() => fixture.StartStoppedHostAsync()).ConfigureAwait(false);
        Assert.Contains("Raw ingress", failure.ToString());
        Assert.Contains("schema", failure.ToString());
        Assert.ThrowsExactly<InvalidOperationException>(() => { _ = fixture.Services; });
        CollectionAssert.AreEqual(checksum, SHA256.HashData(await File.ReadAllBytesAsync(payload).ConfigureAwait(false)));
    }
}
