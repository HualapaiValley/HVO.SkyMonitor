using FluentAssertions;
using HVO.SkyMonitor.CameraAgent.Common.Deployment;
using Microsoft.Data.Sqlite;
using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.Tests.CameraAgent.Deployment;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Deployment")]
public sealed class DeploymentContinuityReaderTests
{
    private string? root;

    [TestInitialize]
    public void Initialize()
    {
        root = Path.Combine(Path.GetTempPath(), $"hvo-continuity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (root is not null && Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReadAsync_ProjectsExactDurableSequenceMetadata()
    {
        await ExecuteAsync("journal/raw-ingress.db", """
            CREATE TABLE raw_capture_sequences(agent_id TEXT PRIMARY KEY, last_sequence INTEGER NOT NULL);
            INSERT INTO raw_capture_sequences VALUES ('agent-a', 17), ('agent-b', 4);
            """).ConfigureAwait(false);
        var result = await new DeploymentContinuityReader().ReadAsync(root!).ConfigureAwait(false);

        result.RawIngressDatabaseExists.Should().BeTrue();
        result.CaptureSequences.Should().Equal(
            new CaptureSequenceContinuity("agent-a", 17),
            new CaptureSequenceContinuity("agent-b", 4));
    }

    [TestMethod]
    public async Task ReadAsync_WhenStateIsFresh_PreservesMissingDatabaseEvidence()
    {
        var result = await new DeploymentContinuityReader().ReadAsync(root!).ConfigureAwait(false);

        result.RawIngressDatabaseExists.Should().BeFalse();
        result.CaptureSequences.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ReadAsync_WhenDatabaseFilesPrecedeSchema_ReturnsExplicitEmptyMetadata()
    {
        await ExecuteAsync("journal/raw-ingress.db", "CREATE TABLE startup_marker(value INTEGER);").ConfigureAwait(false);

        var result = await new DeploymentContinuityReader().ReadAsync(root!).ConfigureAwait(false);

        result.RawIngressDatabaseExists.Should().BeTrue();
        result.CaptureSequences.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ReadAsync_WithCaptureWindow_ProjectsExactOrderedCaptureIdentities()
    {
        await ExecuteAsync("journal/raw-ingress.db", """
            CREATE TABLE raw_capture_sequences(agent_id TEXT PRIMARY KEY, last_sequence INTEGER NOT NULL);
            INSERT INTO raw_capture_sequences VALUES ('agent-a', 13);
            CREATE TABLE raw_captures(
                capture_sequence INTEGER NOT NULL,
                capture_id TEXT NOT NULL,
                raw_artifact_id TEXT NOT NULL,
                payload_sha256 TEXT NOT NULL,
                payload_length INTEGER NOT NULL,
                state TEXT NOT NULL);
            INSERT INTO raw_captures VALUES
                (12, '11111111111111111111111111111111', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc', 6144, 'committed'),
                (13, '22222222222222222222222222222222', 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', 'dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd', 6144, 'committed');
            """).ConfigureAwait(false);

        var result = await new DeploymentContinuityReader().ReadAsync(root!, 12, 13).ConfigureAwait(false);

        result.CaptureWindow.Should().Equal(
            new LocalCaptureContinuity(12, Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), new string('C', 64), 6144, "committed"),
            new LocalCaptureContinuity(13, Guid.Parse("22222222-2222-2222-2222-222222222222"), Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), new string('D', 64), 6144, "committed"));
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "SQL is fixed test fixture schema and data.")]
    private async Task ExecuteAsync(string relativePath, string sql)
    {
        var path = Path.Combine(root!, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var connection = new SqliteConnection($"Data Source={path}");
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }
    }
}
