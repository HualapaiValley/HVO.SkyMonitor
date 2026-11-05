using System.Data.Common;
using FluentAssertions;
using HVO.SkyMonitor.IntegrationTests.Infrastructure;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class DerivativeResetSqlShapeTests
{
    [TestMethod]
    [DataRow("entitlement")]
    [DataRow("runner")]
    public async Task ActualResetUpdateMatchesCachedPlanPredicatesWithoutOpeningSql(string route)
    {
        var capture = new ResetCommandCapture();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer("Server=invalid.example;Database=reset-shape;Integrated Security=True;Encrypt=False")
            .AddInterceptors(new SuppressConnectionOpen(), capture)
            .Options;
        await using var db = new ApplicationDbContext(options);
        var diagnosticsDirectory = Environment.GetEnvironmentVariable("HVO_RESET_DIAGNOSTICS_DIRECTORY");
        try
        {
            // This is SQL-generation evidence, not an activation of the opt-in observer.
            Environment.SetEnvironmentVariable("HVO_RESET_DIAGNOSTICS_DIRECTORY", null);
            if (route == "entitlement")
            {
                await ProcessingEntitlementIntegrationTests.DisableClaimableJobsAsync(db).ConfigureAwait(false);
            }
            else
            {
                await ProcessingRunnerProtocolIntegrationTests.DisableClaimableJobsAsync(db).ConfigureAwait(false);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("HVO_RESET_DIAGNOSTICS_DIRECTORY", diagnosticsDirectory);
        }
        var reset = capture.Commands.Single(sql => sql.Contains("[CentralDerivativeJobs]", StringComparison.Ordinal));
        reset.Should().Contain(DerivativeResetDiagnostics.ResetStatusPredicate);
        reset.Should().Contain(DerivativeResetDiagnostics.ResetLeasePredicate);
        reset.Should().Contain("UPDATE").And.Contain("[ExpandedAtUtc] IS NOT NULL");
        capture.Commands.Should().HaveCount(route == "runner" ? 2 : 1, "runner retirement must remain part of its reset");
    }

    private sealed class SuppressConnectionOpen : DbConnectionInterceptor
    {
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(InterceptionResult.Suppress());
    }

    private sealed class ResetCommandCapture : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(0));
        }
    }
}
