using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace HVO.SkyMonitor.IntegrationTests;

// Only enum names and numbers cross the MSTest boundary. Never retain a raw exception.
internal enum ToolkitStage
{
    ContainerStart, Body, BodyComplete, DatabaseOperatorDisposal, EventOperatorDisposal,
    MaintenanceOperatorDisposal, CustomAdminDisposal,
    BlockedThresholdRestoration, BlockedThresholdVerification, BlockedThresholdConnectionDisposal,
    AdvancedOptionsRestoration, AdvancedOptionsVerification, AdvancedOptionsConnectionDisposal,
    SqlCleanupConnection, EventSessionCleanup, VerificationDatabaseCleanup, DatabaseCleanup,
    PrincipalSessionCleanup, InstanceLoginCleanup, DatabaseLoginCleanup, RecoveryLoginCleanup,
    RuntimeLoginCleanup, MigrationLoginCleanup, SqlCleanupConnectionDisposal,
    ExactFileCleanup, ExactFileVerification, ContainerDisposal
}

internal enum ToolkitBodyStep
{
    IsolatedSetup, RealSqlcmdAdversarial, QueryStore, ExtendedEvents, XeWrapperTraversalExecution, XeWrapperTraversalResult, XeInvalidInstanceWideSelection, XeInvalidPath, XeNoncanonicalPath, XeFailedStartCleanup, XeWrapperReplacedTargetPreflight, XeWrapperReusedRun, XeWrapperMainProduction, Capacity, Maintenance, MaintenanceSchemaContent, BackupRestore, MaintenanceResultCount, MaintenanceStatisticsSchema, MaintenanceStatisticsContent, MaintenanceIndexSchema, MaintenanceIndexPartitions, MaintenanceIndexPages, ContainerStart, XeWrapperProductionPostSqlParse, XeWrapperProductionPostcheckOwnership
}

internal enum ToolkitPreflightReason
{
    None, CanonicalRoot, RootOwner, RootMode, ServiceAccess, Lock, ExistingRun, CreatedDirectory, Prefix, WrapperIdentity, Usage, MissingCommand, Permission, InvalidCommandInput, Unclassified
}

internal sealed class SqlToolkitDiagnostics
{
    internal const int MaximumFacts = 32;
    private const int MaximumExceptionNodes = 32;
    private readonly List<FailureFact> failures = [];
    internal ToolkitStage Stage { get; set; } = ToolkitStage.ContainerStart;
    internal ToolkitBodyStep BodyStep { get; set; } = ToolkitBodyStep.ContainerStart;
    internal ToolkitPreflightReason PreflightReason { get; set; }
    internal bool BodyCompleted { get; private set; }
    private bool truncated;

    internal void CompleteBody()
    {
        BodyCompleted = true;
        Stage = ToolkitStage.BodyComplete;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Independent cleanup and disposal must continue; only bounded sanitized facts survive.")]
    internal async Task AttemptAsync(ToolkitStage stage, Func<Task> action)
    {
        Stage = stage;
        try { await action().ConfigureAwait(false); }
        catch (Exception exception) { Record(exception); }
    }

    internal async Task CleanupSqlArtifactsAsync(Func<ToolkitStage, Task> execute)
    {
        foreach (var stage in new[]
                 {
                     ToolkitStage.EventSessionCleanup, ToolkitStage.VerificationDatabaseCleanup,
                     ToolkitStage.DatabaseCleanup, ToolkitStage.PrincipalSessionCleanup,
                     ToolkitStage.InstanceLoginCleanup, ToolkitStage.DatabaseLoginCleanup,
                     ToolkitStage.RecoveryLoginCleanup, ToolkitStage.RuntimeLoginCleanup,
                     ToolkitStage.MigrationLoginCleanup
                 })
            await AttemptAsync(stage, () => execute(stage)).ConfigureAwait(false);
    }

    internal IAsyncDisposable ObserveDisposal(ToolkitStage stage, IAsyncDisposable resource) => new ObservedDisposal(this, stage, resource);

    private sealed class ObservedDisposal(SqlToolkitDiagnostics diagnostics, ToolkitStage stage, IAsyncDisposable resource) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(diagnostics.AttemptAsync(stage, () => resource.DisposeAsync().AsTask()));
    }

    internal void Record(Exception exception)
    {
        var pending = new Stack<Exception>();
        pending.Push(exception);
        var visited = 0;
        while (pending.Count > 0 && visited++ < MaximumExceptionNodes)
        {
            var current = pending.Pop();
            var safeStage = Enum.IsDefined(Stage) ? Stage.ToString() : "Body";
            if (failures.Count == MaximumFacts)
            {
                truncated = true;
                // Preserve a root fact for a newly failing stage even after a large body aggregate.
                // MaximumFacts exceeds the closed stage count, so a full list has a duplicate stage.
                var repeatedStage = failures.GroupBy(fact => fact.Stage).First(group => group.Count() > 1).Key;
                if (visited != 1 || failures.Any(fact => fact.Stage == safeStage)) break;
                failures.RemoveAt(failures.FindLastIndex(fact => fact.Stage == repeatedStage));
            }
            var category = current switch
            {
                SqlException => "sql",
                SanitizedScriptException => "sql-script",
                AggregateException => "aggregate",
                AssertFailedException => "assertion",
                OperationCanceledException => "cancellation",
                TimeoutException => "timeout",
                IOException => "io",
                UnauthorizedAccessException => "access",
                ObjectDisposedException => "disposed",
                InvalidOperationException => "invalid-operation",
                _ => "other"
            };
            var number = current switch
            {
                SqlException sql => sql.Number,
                SanitizedScriptException script => script.SqlErrorNumber,
                _ => 0
            };
            failures.Add(new(safeStage, category, number,
                Stage == ToolkitStage.Body && Enum.IsDefined(BodyStep) ? BodyStep.ToString() : null,
                Stage == ToolkitStage.Body && Enum.IsDefined(PreflightReason) ? PreflightReason.ToString() : null));
            if (current is AggregateException aggregate)
            {
                // Bound traversal as well as output; do not Flatten an unbounded tree.
                var count = Math.Min(aggregate.InnerExceptions.Count, MaximumExceptionNodes);
                truncated |= count != aggregate.InnerExceptions.Count;
                for (var index = count - 1; index >= 0; index--) pending.Push(aggregate.InnerExceptions[index]);
            }
            else if (current.InnerException is { } inner) pending.Push(inner);
        }
        truncated |= pending.Count > 0;
    }

    internal string Serialize() => JsonSerializer.Serialize(new
    {
        Body = BodyCompleted ? "body-complete" : "body-incomplete",
        Failures = failures,
        Truncated = truncated
    });

    internal void ThrowIfFailed()
    {
        if (failures.Count > 0)
            throw new AssertFailedException("Issue 255 sanitized lifecycle " + Serialize());
    }

    private sealed record FailureFact(string Stage, string Category, int SqlErrorNumber, string? BodyStep, string? PreflightReason);
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1032:Implement standard exception constructors",
    Justification = "This test-only exception deliberately accepts only a SQL error number and retains no raw inner exception.")]
internal sealed class SanitizedScriptException(int sqlErrorNumber)
    : Exception("SQL operator script failed with bounded error " + sqlErrorNumber.ToString(CultureInfo.InvariantCulture) + ".")
{
    internal int SqlErrorNumber { get; } = sqlErrorNumber;
}
