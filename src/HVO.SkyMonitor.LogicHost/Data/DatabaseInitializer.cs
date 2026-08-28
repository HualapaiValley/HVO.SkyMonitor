using System.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Data;

internal enum DatabaseInitializationStage
{
    Migrate,
    MarkRunning,
    Seed,
    Complete
}

internal sealed record DatabaseInitializationResult(
    Guid AttemptId,
    string TargetMigrationId,
    TimeSpan Elapsed);

internal sealed partial class DatabaseInitializer(
    ApplicationDbContext dbContext,
    IServiceProvider services,
    TimeProvider timeProvider,
    ILogger<DatabaseInitializer> logger)
{
    internal const int CurrentInitializationVersion = 1;
    internal const string LockResource = "HVO.SkyMonitor.LogicHost.DatabaseInitialization.v1";
    private static readonly TimeSpan LockTimeout = TimeSpan.Zero;

    internal async Task<DatabaseInitializationResult> RunAsync(CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        var attemptId = Guid.NewGuid();
        var connectionString = dbContext.Database.GetConnectionString()
            ?? throw new InvalidOperationException("The database connection string is unavailable.");
        await using var lockConnection = new SqlConnection(connectionString);
        await lockConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await AcquireLockAsync(lockConnection, cancellationToken).ConfigureAwait(false);
        var stage = DatabaseInitializationStage.Migrate;
        string? targetMigration = null;
        try
        {
            await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            targetMigration = dbContext.Database.GetMigrations().Last();
            stage = DatabaseInitializationStage.MarkRunning;
            var now = timeProvider.GetUtcNow();
            var state = await dbContext.DatabaseInitializationState.SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (state is null)
            {
                state = new DatabaseInitializationState();
                dbContext.DatabaseInitializationState.Add(state);
            }
            SetRunning(state, attemptId, targetMigration, now);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            stage = DatabaseInitializationStage.Seed;
            await DatabaseSeeder.SeedAsync(
                services,
                logger).ConfigureAwait(false);
            stage = DatabaseInitializationStage.Complete;
            state = await dbContext.DatabaseInitializationState.SingleAsync(cancellationToken)
                .ConfigureAwait(false);
            state.Status = DatabaseInitializationStatus.Completed;
            state.UpdatedAtUtc = timeProvider.GetUtcNow();
            state.CompletedAtUtc = state.UpdatedAtUtc;
            state.FailureStage = null;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new(
                attemptId,
                targetMigration,
                timeProvider.GetElapsedTime(started));
        }
        catch (Exception exception)
        {
            Log.Failed(logger, attemptId, stage, targetMigration ?? "not-applied", exception);
            await RecordFailureAsync(attemptId, targetMigration, stage).ConfigureAwait(false);
            throw;
        }
    }

    private async Task RecordFailureAsync(
        Guid attemptId,
        string? targetMigration,
        DatabaseInitializationStage stage)
    {
        if (targetMigration is null)
        {
            return;
        }
        try
        {
            dbContext.ChangeTracker.Clear();
            var state = await dbContext.DatabaseInitializationState.SingleOrDefaultAsync(CancellationToken.None)
                .ConfigureAwait(false);
            if (state is null || state.AttemptId != attemptId)
            {
                return;
            }
            state.Status = DatabaseInitializationStatus.Failed;
            state.UpdatedAtUtc = timeProvider.GetUtcNow();
            state.CompletedAtUtc = null;
            state.FailureStage = stage.ToString();
            await dbContext.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException or DbUpdateException)
        {
            // A durable Running state is already fail-closed if recording Failed is unavailable.
            Log.FailureStateUnavailable(logger, attemptId, exception);
        }
    }

    private static void SetRunning(
        DatabaseInitializationState state,
        Guid attemptId,
        string targetMigration,
        DateTimeOffset now)
    {
        state.InitializationVersion = CurrentInitializationVersion;
        state.Status = DatabaseInitializationStatus.Running;
        state.AttemptId = attemptId;
        state.TargetMigrationId = targetMigration;
        state.StartedAtUtc = now;
        state.UpdatedAtUtc = now;
        state.CompletedAtUtc = null;
        state.FailureStage = null;
    }

    private static async Task AcquireLockAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("sys.sp_getapplock", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 15
        };
        _ = command.Parameters.AddWithValue("@Resource", LockResource);
        _ = command.Parameters.AddWithValue("@LockMode", "Exclusive");
        _ = command.Parameters.AddWithValue("@LockOwner", "Session");
        _ = command.Parameters.AddWithValue("@LockTimeout", (int)LockTimeout.TotalMilliseconds);
        var result = command.Parameters.Add("@RETURN_VALUE", SqlDbType.Int);
        result.Direction = ParameterDirection.ReturnValue;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (result.Value is not int status || status < 0)
        {
            throw new InvalidOperationException(
                "Another LogicHost database initialization command owns the target database.");
        }
    }

    private static partial class Log
    {
        [LoggerMessage(1010, LogLevel.Error,
            "Database initialization failed: AttemptId={AttemptId}, Stage={Stage}, TargetMigrationId={TargetMigrationId}")]
        internal static partial void Failed(
            ILogger logger,
            Guid attemptId,
            DatabaseInitializationStage stage,
            string targetMigrationId,
            Exception exception);

        [LoggerMessage(1011, LogLevel.Warning,
            "Database initialization failure state could not be persisted: AttemptId={AttemptId}")]
        internal static partial void FailureStateUnavailable(ILogger logger, Guid attemptId, Exception exception);
    }
}
