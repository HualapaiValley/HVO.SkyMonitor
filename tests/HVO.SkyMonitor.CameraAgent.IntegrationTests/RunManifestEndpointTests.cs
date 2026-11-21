using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[TestClass]
[TestCategory("Integration")]
public sealed partial class RunManifestEndpointTests
{
    [TestMethod]
    [DataRow("anonymous", HttpStatusCode.Unauthorized)]
    [DataRow("outsider", HttpStatusCode.Forbidden)]
    [DataRow("owner", HttpStatusCode.NotFound)]
    public async Task ManifestDownloadEnforcesOperationsReadPolicy(string account, HttpStatusCode expected)
    {
        using var scope = AssemblyHooks.Fixture.CreateCameraAgentScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        using var client = AssemblyHooks.Fixture.CreateCameraAgentClient();
        if (account != "anonymous")
        {
            var user = account == "owner"
                ? await users.FindByEmailAsync("owner@cameraagent.integration").ConfigureAwait(false)
                : new ApplicationUser { UserName = $"manifest-outsider-{Guid.NewGuid():N}", EmailConfirmed = true };
            Assert.IsNotNull(user);
            if (account == "outsider") Assert.IsTrue((await users.CreateAsync(user).ConfigureAwait(false)).Succeeded);
            client.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, user.Id);
        }
        using var response = await client.GetAsync(new Uri(
            $"/api/v1/operations/processing-graphs/executions/{Guid.NewGuid():D}/manifest", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(expected, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.IsFalse(body.Contains("schemaVersion", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task VirtualCaptureDownloadsItsRetainedFrozenEvidenceWithoutStorageDetails()
    {
        var (fixture, capture) = await GetSiblingCaptureAsync().ConfigureAwait(false);
        var detail = capture.Detail;
        using var scope = fixture.CreateCameraAgentScope();
        var owner = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
            .FindByEmailAsync("owner@cameraagent.integration").ConfigureAwait(false);
        Assert.IsNotNull(owner);
        using var client = fixture.CreateCameraAgentClient();
        client.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, owner.Id);
        using var response = await client.GetAsync(new Uri(
            $"/api/v1/operations/processing-graphs/executions/{detail.Execution.ExecutionId:D}/manifest", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.IsTrue(response.Headers.CacheControl?.NoStore);
        var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        Assert.IsTrue(bytes.Length <= ProcessingRunManifestBounds.MaximumBytes);
        using var document = JsonDocument.Parse(bytes);
        var json = document.RootElement;
        Assert.AreEqual("hvo-cameraagent-run-manifest-v1", json.GetProperty("schemaVersion").GetString());
        Assert.AreEqual(detail.Execution.ExecutionId, json.GetProperty("executionId").GetGuid());
        Assert.AreEqual(detail.Execution.CaptureId, json.GetProperty("captureId").GetGuid());
        Assert.AreEqual(detail.Execution.LocalPlanIdentitySha256, json.GetProperty("localPlanIdentitySha256").GetString());
        Assert.AreEqual(detail.Execution.GraphRevisionId, json.GetProperty("graphRevisionId").GetString());
        var nodes = json.GetProperty("nodes");
        Assert.AreEqual(detail.Nodes.Count, nodes.GetArrayLength());
        foreach (var stored in detail.Nodes)
        {
            var outputs = nodes.EnumerateArray().Single(value => value.GetProperty("nodeId").GetString() == stored.NodeId)
                .GetProperty("outputs");
            Assert.AreEqual(stored.Outputs.Count, outputs.GetArrayLength());
            foreach (var artifact in stored.Outputs)
            {
                var output = outputs.EnumerateArray().Single(value => value.GetProperty("artifactId").GetGuid() == artifact.ArtifactId);
                Assert.AreEqual(artifact.PayloadSha256, output.GetProperty("payloadSha256").GetString());
                CollectionAssert.AreEqual(artifact.SourceArtifactIds.ToArray(), output.GetProperty("sourceArtifactIds").EnumerateArray().Select(value => value.GetGuid()).ToArray());
            }
        }
        var text = json.GetRawText();
        foreach (var forbidden in new[] { "relativePath", "leaseOwner", "diagnostics", "password", "triggerReference", "failureReason" })
            Assert.IsFalse(text.Contains(forbidden, StringComparison.OrdinalIgnoreCase), forbidden);
    }

    [TestMethod]
    [DataRow("Inputs")]
    [DataRow("Outputs")]
    public async Task CorruptCountLedgerRefusesOnlyManifestAndRecovers(string ledgerKind)
    {
        var (fixture, capture) = await GetSiblingCaptureAsync().ConfigureAwait(false);
        var executionId = capture.Detail.Execution.ExecutionId;
        using var scope = fixture.CreateCameraAgentScope();
        var owner = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
            .FindByEmailAsync("owner@cameraagent.integration").ConfigureAwait(false);
        Assert.IsNotNull(owner);
        using var client = fixture.CreateCameraAgentClient();
        client.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, owner.Id);
        var path = $"/api/v1/operations/processing-graphs/executions/{executionId:D}";
        using var before = await client.GetAsync(new Uri(path + "/manifest", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, before.StatusCode);
        var expected = await before.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        var ordinary = await client.GetStringAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(false);
        using var db = OpenManifestJournal(fixture);
        await db.OpenAsync().ConfigureAwait(false);
        var original = await ReadManifestLedgerRowsAsync(db, executionId, ledgerKind).ConfigureAwait(false);
        Assert.IsTrue(original.Length > 1, "The completed execution must retain its ledger header and typed count rows.");
        var header = original.Single(static row => row.NodeId.Length == 0);
        Assert.AreEqual("Recorded", header.EvidenceStatus);
        using var command = db.CreateCommand();
        command.Parameters.AddWithValue("$id", executionId.ToString("N"));
        command.Parameters.AddWithValue("$kind", ledgerKind);
        try
        {
            // Schema 9 reads the independent ledger, not optional counters in the frozen plan JSON.
            command.CommandText = "UPDATE processing_execution_manifest_counts SET evidence_status='Corrupt' WHERE execution_id=$id AND ledger_kind=$kind AND node_id='';";
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
            var corrupt = original.Select(static row => row.NodeId.Length == 0 ? row with { EvidenceStatus = "Corrupt" } : row).ToArray();
            CollectionAssert.AreEqual(corrupt, await ReadManifestLedgerRowsAsync(db, executionId, ledgerKind).ConfigureAwait(false));
            using var refused = await client.GetAsync(new Uri(path + "/manifest", UriKind.Relative)).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.Conflict, refused.StatusCode);
            using var problem = JsonDocument.Parse(await refused.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
            Assert.AreEqual("The retained run evidence is corrupt and cannot be downloaded.", problem.RootElement.GetProperty("title").GetString());
            Assert.IsFalse(problem.RootElement.TryGetProperty("schemaVersion", out _));
            Assert.AreEqual(ordinary, await client.GetStringAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(false));
        }
        finally
        {
            command.CommandText = "UPDATE processing_execution_manifest_counts SET evidence_status=$status WHERE execution_id=$id AND ledger_kind=$kind AND node_id='';";
            command.Parameters.AddWithValue("$status", header.EvidenceStatus);
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
        }
        CollectionAssert.AreEqual(original, await ReadManifestLedgerRowsAsync(db, executionId, ledgerKind).ConfigureAwait(false));
        using var recovered = await client.GetAsync(new Uri(path + "/manifest", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, recovered.StatusCode);
        CollectionAssert.AreEqual(expected, await recovered.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
    }

    private static async Task<ManifestLedgerRow[]> ReadManifestLedgerRowsAsync(SqliteConnection connection, Guid executionId, string ledgerKind)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT node_id, evidence_count, evidence_status FROM processing_execution_manifest_counts WHERE execution_id=$id AND ledger_kind=$kind ORDER BY node_id;";
        command.Parameters.AddWithValue("$id", executionId.ToString("N"));
        command.Parameters.AddWithValue("$kind", ledgerKind);
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var rows = new List<ManifestLedgerRow>();
        while (await reader.ReadAsync().ConfigureAwait(false))
            rows.Add(new(reader.GetString(0), await reader.IsDBNullAsync(1).ConfigureAwait(false) ? null : reader.GetInt64(1), reader.GetString(2)));
        return [.. rows];
    }

    private sealed record ManifestLedgerRow(string NodeId, long? EvidenceCount, string EvidenceStatus);
}
