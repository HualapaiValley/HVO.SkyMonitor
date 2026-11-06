using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;

namespace HVO.SkyMonitor.IntegrationTests.Infrastructure;

/// <summary>Retains executed plans for one fixture marker independently of plan-cache residency.</summary>
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
    Justification = "Session names and predicates contain only fixture-generated GUIDs and constant SQL.")]
internal sealed class ProposalPayloadPlanCapture(SqlConnection connection, string session) : IAsyncDisposable
{
    internal static async Task<ProposalPayloadPlanCapture> StartAsync(string connectionString, Guid token)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        var session = $"hvo_proposal_{token:N}";
        var capture = new ProposalPayloadPlanCapture(connection, session);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE EVENT SESSION [{session}] ON SERVER
                ADD EVENT sqlserver.query_post_execution_showplan(
                    WHERE ([sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text], N'%proposal-payload-cache:{token:N}%')))
                ADD TARGET package0.ring_buffer(SET max_memory = 4096)
                WITH (MAX_MEMORY = 4096 KB, EVENT_RETENTION_MODE = NO_EVENT_LOSS,
                    MAX_DISPATCH_LATENCY = 1 SECONDS, STARTUP_STATE = OFF);
                ALTER EVENT SESSION [{session}] ON SERVER STATE = START;
                """;
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            return capture;
        }
        catch
        {
            await capture.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async Task<XDocument[]> ReadAsync(int expectedCount, string parameterName)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 2;
            command.CommandText = """
                SELECT target.target_data
                FROM sys.dm_xe_session_targets target
                JOIN sys.dm_xe_sessions session ON session.address = target.event_session_address
                WHERE session.name = @name AND target.target_name = N'ring_buffer';
                """;
            command.Parameters.AddWithValue("@name", session);
            var text = await command.ExecuteScalarAsync().ConfigureAwait(false) as string
                ?? throw new InvalidOperationException("The fixture plan-capture target is missing.");
            var target = XDocument.Parse(text);
            if ((string?)target.Root?.Attribute("truncated") == "1")
                throw new InvalidOperationException("The fixture plan-capture target was truncated.");
            XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
            // Auto-statistics statements can inherit the parent marker. Keep only
            // the parameterized application statement, not its internal STATMAN work.
            var plans = target.Descendants(ns + "ShowPlanXML")
                .Where(plan => plan.Descendants(ns + "ParameterList").Elements(ns + "ColumnReference")
                    .Any(parameter => (string?)parameter.Attribute("Column") == parameterName))
                .Select(plan => new XDocument(plan)).ToArray();
            if (plans.Length >= expectedCount || clock.Elapsed >= TimeSpan.FromSeconds(5)) return plans;
            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = @name)
                    DROP EVENT SESSION [{session}] ON SERVER;
                """;
            command.Parameters.AddWithValue("@name", session);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally { await connection.DisposeAsync().ConfigureAwait(false); }
    }
}
