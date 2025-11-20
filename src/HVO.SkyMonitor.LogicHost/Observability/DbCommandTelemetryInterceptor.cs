using System;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.Common.Observability;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace HVO.SkyMonitor.LogicHost.Observability;

/// <summary>
/// Emits dependency Activities for every EF Core command so Application Insights can visualize PostgreSQL usage.
/// </summary>
internal sealed class DbCommandTelemetryInterceptor : DbCommandInterceptor
{
    private readonly ConcurrentDictionary<Guid, Activity?> _activeOperations = new();

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        StartActivity(command, eventData);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        StartActivity(command, eventData);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override DbDataReader ReaderExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result)
    {
        StopActivity(eventData);
        return base.ReaderExecuted(command, eventData, result);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        StopActivity(eventData);
        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        StartActivity(command, eventData);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        StartActivity(command, eventData);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override int NonQueryExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result)
    {
        StopActivity(eventData);
        return base.NonQueryExecuted(command, eventData, result);
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        StopActivity(eventData);
        return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        StartActivity(command, eventData);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        StartActivity(command, eventData);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override object? ScalarExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result)
    {
        StopActivity(eventData);
        return base.ScalarExecuted(command, eventData, result);
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result,
        CancellationToken cancellationToken = default)
    {
        StopActivity(eventData);
        return base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
    {
        StopActivity(eventData, eventData.Exception);
        base.CommandFailed(command, eventData);
    }

    public override Task CommandFailedAsync(
        DbCommand command,
        CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        StopActivity(eventData, eventData.Exception);
        return base.CommandFailedAsync(command, eventData, cancellationToken);
    }

    private void StartActivity(DbCommand command, CommandEventData eventData)
    {
        var activity = DependencyTelemetry.StartPostgreSqlActivity(
            operation: "PostgreSQL",
            dataSource: command.Connection?.DataSource ?? command.Connection?.ConnectionString,
            database: command.Connection?.Database,
            statement: command.CommandText);

        if (activity is null)
        {
            return;
        }

        activity.SetTag("db.operation", eventData.ExecuteMethod.ToString());
        _activeOperations[eventData.CommandId] = activity;
    }

    private void StopActivity(CommandErrorEventData eventData, Exception? exception)
    {
        StopActivity(eventData.CommandId, exception);
    }

    private void StopActivity(CommandExecutedEventData eventData)
    {
        StopActivityInternal(eventData.CommandId, null);
    }

    private void StopActivity(Guid commandId, Exception? exception)
    {
        StopActivityInternal(commandId, exception);
    }

#pragma warning disable CA2000 // Activities are stored until we explicitly dispose them when the command finishes.
    private void StopActivityInternal(Guid commandId, Exception? exception)
    {
        var removed = _activeOperations.TryRemove(commandId, out Activity? activity);
        if (!removed || activity is null)
        {
            return;
        }

        try
        {
            if (exception is not null)
            {
                DependencyTelemetry.RecordException(activity, exception);
            }
        }
        finally
        {
            activity.Dispose();
        }
    }
#pragma warning restore CA2000
}
