using HVO.SkyMonitor.CameraAgent.Common.Automation;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.Automation;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class LocalAutomationScriptStopAuditTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FirstStopActorAndCauseSurviveConcurrentRequestsReplayAndReopen(bool running)
    {
        using var fixture = new ScriptControlsFixture();
        var entered = ScriptControlsFixture.Signal();
        fixture.Stub.Execute = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return new(LocalAutomationRunOutcome.Succeeded, "unreachable");
        };
        await fixture.Store.InitializeAsync(default).ConfigureAwait(false);
        await fixture.Store.SaveAsync(ScriptControlsFixture.Save(), default).ConfigureAwait(false);
        var run = (await fixture.Store.RunNowAsync(ScriptControlsFixture.RunNow(), default).ConfigureAwait(false)).State.Runs.Single();
        if (running)
        {
            await fixture.Runner.SweepAsync(default).ConfigureAwait(false);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        var request = new LocalAutomationScriptStopRequest("script", run.RunKey, 1, "first-owner");
        // Establish which request committed first without relying on thread scheduling.
        await fixture.Store.StopQueuedScriptAsync(request, default).ConfigureAwait(false);
        var requests = Enumerable.Range(0, 8).Select(index => fixture.Commands.StopAsync(
            request with { Actor = "other-owner-" + index }, default).AsTask());
        await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await fixture.Worker.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var terminal = (await fixture.Store.GetStateAsync(default).ConfigureAwait(false)).Runs.Single();
        Assert.AreEqual(running ? LocalAutomationRunOutcome.Interrupted : LocalAutomationRunOutcome.Skipped, terminal.Outcome);
        await AssertAuditAsync(fixture, run, "first-owner").ConfigureAwait(false);
        fixture.Runner.Dispose();
        fixture.Store.Dispose();
        using var reopened = fixture.NewStore();
        await reopened.InitializeAsync(default).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Unchanged, (await new LocalAutomationScriptCommands(reopened, fixture.Worker)
            .StopAsync(request with { Actor = "replay-owner" }, default).ConfigureAwait(false)).Status);
        Assert.AreEqual(terminal, (await reopened.GetStateAsync(default).ConfigureAwait(false)).Runs.Single());
        await AssertAuditAsync(fixture, run, "first-owner").ConfigureAwait(false);
        using var connection = new SqliteConnection("Data Source=" + fixture.DatabasePath);
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; DELETE FROM automation_runs; SELECT COUNT(*) FROM automation_script_stops;";
        Assert.AreEqual(0L, (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!, "Audit retention follows run retention.");
    }

    [TestMethod]
    public async Task InvalidStaleMissingAndAlreadyTerminalStopsCreateNoAttribution()
    {
        using var fixture = new ScriptControlsFixture();
        await fixture.Store.InitializeAsync(default).ConfigureAwait(false);
        await fixture.Store.SaveAsync(ScriptControlsFixture.Save(), default).ConfigureAwait(false);
        var run = (await fixture.Store.RunNowAsync(ScriptControlsFixture.RunNow(), default).ConfigureAwait(false)).State.Runs.Single();
        var stop = new LocalAutomationScriptStopRequest("script", run.RunKey, 1, "owner");
        foreach (var invalid in new[] { stop with { Actor = "" }, stop with { ExpectedDefinitionVersion = 2 }, stop with { RunKey = "absent" } })
            await fixture.Commands.StopAsync(invalid, default).ConfigureAwait(false);
        await fixture.Runner.SweepAsync(default).ConfigureAwait(false);
        await fixture.Worker.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Unchanged, (await fixture.Commands.StopAsync(stop, default).ConfigureAwait(false)).Status);
        using var connection = new SqliteConnection("Data Source=" + fixture.DatabasePath);
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM automation_script_stops;";
        Assert.AreEqual(0L, (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!);
    }

    [TestMethod]
    public async Task AuditWriteFailureCannotCancelTheRunningScript()
    {
        using var fixture = new ScriptControlsFixture();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = ScriptControlsFixture.Signal();
        fixture.Stub.Execute = async (_, token) =>
        {
            entered.TrySetResult(token);
            await release.Task.ConfigureAwait(false);
            return new(LocalAutomationRunOutcome.Succeeded, "natural completion");
        };
        await fixture.Store.InitializeAsync(default).ConfigureAwait(false);
        await fixture.Store.SaveAsync(ScriptControlsFixture.Save(), default).ConfigureAwait(false);
        var run = (await fixture.Store.RunNowAsync(ScriptControlsFixture.RunNow(), default).ConfigureAwait(false)).State.Runs.Single();
        await fixture.Runner.SweepAsync(default).ConfigureAwait(false);
        try
        {
            var token = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            using var connection = new SqliteConnection("Data Source=" + fixture.DatabasePath);
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE automation_script_stops;";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<SqliteException>(() => fixture.Commands.StopAsync(
                new("script", run.RunKey, 1, "owner"), default).AsTask()).ConfigureAwait(false);
            Assert.IsFalse(token.IsCancellationRequested, "An unaudited stop must not reach the worker.");
        }
        finally { release.TrySetResult(); }
        await fixture.Worker.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, (await fixture.Store.GetStateAsync(default).ConfigureAwait(false)).Runs.Single().Outcome);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SchemaThreeUpgradesAtomicallyAndDriftIsRefused(bool drift)
    {
        using var fixture = new ScriptControlsFixture();
        await fixture.Store.InitializeAsync(default).ConfigureAwait(false);
        await fixture.Store.SaveAsync(ScriptControlsFixture.Save(), default).ConfigureAwait(false);
        var before = (await fixture.Store.RunNowAsync(ScriptControlsFixture.RunNow(), default).ConfigureAwait(false)).State.Runs.Single();
        fixture.Runner.Dispose();
        fixture.Store.Dispose();
        using (var connection = new SqliteConnection("Data Source=" + fixture.DatabasePath))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE automation_script_stops; PRAGMA user_version = 3;" +
                (drift ? "CREATE TABLE unexpected (id INTEGER);" : "");
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        using var reopened = fixture.NewStore();
        if (drift)
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.InitializeAsync(default).AsTask()).ConfigureAwait(false);
        else
        {
            await reopened.InitializeAsync(default).ConfigureAwait(false);
            Assert.AreEqual(before, (await reopened.GetStateAsync(default).ConfigureAwait(false)).Runs.Single());
            Assert.AreEqual(LocalAutomationCommandStatus.Applied, (await new LocalAutomationScriptCommands(reopened, fixture.Worker)
                .StopAsync(new("script", before.RunKey, 1, "owner"), default).ConfigureAwait(false)).Status);
        }
        using var check = new SqliteConnection("Data Source=" + fixture.DatabasePath);
        await check.OpenAsync().ConfigureAwait(false);
        using var version = check.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(drift ? 3L : 4L, (long)(await version.ExecuteScalarAsync().ConfigureAwait(false))!);
    }

    private static async Task AssertAuditAsync(ScriptControlsFixture fixture, LocalAutomationRun run, string actor)
    {
        using var connection = new SqliteConnection("Data Source=" + fixture.DatabasePath);
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_key, revision_sha256, definition_version, actor, cause, requested_unix_ms FROM automation_script_stops;";
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
        Assert.AreEqual(run.RunKey, reader.GetString(0));
        Assert.AreEqual(run.RevisionSha256, reader.GetString(1));
        Assert.AreEqual(1L, reader.GetInt64(2));
        Assert.AreEqual(actor, reader.GetString(3));
        Assert.AreEqual("operator-stop-requested", reader.GetString(4));
        Assert.AreEqual(fixture.Clock.GetUtcNow().ToUnixTimeMilliseconds(), reader.GetInt64(5));
        Assert.IsFalse(await reader.ReadAsync().ConfigureAwait(false));
    }
}
