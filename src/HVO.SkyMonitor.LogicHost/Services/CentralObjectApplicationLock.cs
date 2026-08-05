using System.Data;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed class CentralObjectApplicationLock : IAsyncDisposable
{
    internal static readonly TimeSpan AcquisitionTimeout = TimeSpan.FromSeconds(10);
    private const string ResourcePrefix = "hvo-central-object:";
    private readonly SqlConnection connection;
    private readonly string resource;
    private bool released;

    private CentralObjectApplicationLock(SqlConnection connection, string resource)
    {
        this.connection = connection;
        this.resource = resource;
    }

    public static async Task<CentralObjectApplicationLock> AcquireAsync(
        ApplicationDbContext dbContext,
        string canonicalStorageReference,
        CancellationToken cancellationToken)
        => await AcquireCoreAsync(
            dbContext, canonicalStorageReference, AcquisitionTimeout, cancellationToken).ConfigureAwait(false)
           ?? throw new InvalidOperationException("The central object application lock was not acquired.");

    public static Task<CentralObjectApplicationLock?> TryAcquireAsync(
        ApplicationDbContext dbContext,
        string canonicalStorageReference,
        CancellationToken cancellationToken)
        => AcquireCoreAsync(dbContext, canonicalStorageReference, TimeSpan.Zero, cancellationToken);

    private static async Task<CentralObjectApplicationLock?> AcquireCoreAsync(
        ApplicationDbContext dbContext,
        string canonicalStorageReference,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalStorageReference);
        var connectionString = dbContext.Database.GetConnectionString()
            ?? throw new InvalidOperationException("The central object application lock requires SQL Server.");
        var resource = CreateResource(canonicalStorageReference);
        SqlConnection? connection = null;
        try
        {
            connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new SqlCommand("sys.sp_getapplock", connection)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = checked((int)Math.Ceiling(timeout.TotalSeconds) + 5)
            };
            _ = command.Parameters.AddWithValue("@Resource", resource);
            _ = command.Parameters.AddWithValue("@LockMode", "Exclusive");
            _ = command.Parameters.AddWithValue("@LockOwner", "Session");
            _ = command.Parameters.AddWithValue("@LockTimeout", (int)timeout.TotalMilliseconds);
            var result = command.Parameters.Add("@RETURN_VALUE", SqlDbType.Int);
            result.Direction = ParameterDirection.ReturnValue;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            var lockResult = result.Value is int status ? status : int.MinValue;
            if (lockResult < 0)
            {
                if (timeout == TimeSpan.Zero && lockResult == -1)
                {
                    return null;
                }
                throw new InvalidOperationException(
                    $"The central object application lock failed with status {lockResult} within the {timeout.TotalMilliseconds:0} ms timeout.");
            }
            var resultLock = new CentralObjectApplicationLock(connection, resource);
            connection = null;
            return resultLock;
        }
        finally
        {
            connection?.Dispose();
        }
    }

    internal static string CreateResource(string canonicalStorageReference)
        => ResourcePrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalStorageReference)));

    internal async Task EnsureHeldAsync(CancellationToken cancellationToken)
    {
        if (released || connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException("The central object application lock is no longer held.");
        }
        await using var command = new SqlCommand(
            "SELECT APPLOCK_MODE(N'public', @resource, N'Session');", connection);
        _ = command.Parameters.AddWithValue("@resource", resource);
        var mode = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (!string.Equals(mode, "Exclusive", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The central object application lock is no longer held.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (released)
        {
            return;
        }
        released = true;
        try
        {
            if (connection.State == ConnectionState.Open)
            {
                await using var command = new SqlCommand("sys.sp_releaseapplock", connection)
                {
                    CommandType = CommandType.StoredProcedure,
                    CommandTimeout = checked((int)Math.Ceiling(AcquisitionTimeout.TotalSeconds) + 5)
                };
                _ = command.Parameters.AddWithValue("@Resource", resource);
                _ = command.Parameters.AddWithValue("@LockOwner", "Session");
                await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (SqlException)
        {
            // Closing the dedicated session below is the fail-safe lock release.
        }
        catch (InvalidOperationException)
        {
            // A broken connection has already released its session-owned locks.
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}

internal sealed class CentralObjectApplicationLockSet : IAsyncDisposable
{
    private readonly ApplicationDbContext dbContext;
    private readonly SqlConnection connection;
    private readonly IReadOnlyList<string> resources;
    private readonly bool closeConnection;
    private bool released;

    private CentralObjectApplicationLockSet(
        ApplicationDbContext dbContext,
        SqlConnection connection,
        IReadOnlyList<string> resources,
        bool closeConnection)
    {
        this.dbContext = dbContext;
        this.connection = connection;
        this.resources = resources;
        this.closeConnection = closeConnection;
    }

    internal static async Task<CentralObjectApplicationLockSet> AcquireAsync(
        ApplicationDbContext dbContext,
        IEnumerable<string> canonicalStorageReferences,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        var resources = canonicalStorageReferences
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(CentralObjectApplicationLock.CreateResource)
            .ToArray();
        if (resources.Length == 0)
        {
            throw new InvalidOperationException("At least one central object application lock is required.");
        }

        var connection = dbContext.Database.GetDbConnection() as SqlConnection
            ?? throw new InvalidOperationException("The central object application lock requires SQL Server.");
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        var acquired = new List<string>(resources.Length);
        try
        {
            foreach (var resource in resources)
            {
                await AcquireResourceAsync(connection, resource, cancellationToken).ConfigureAwait(false);
                acquired.Add(resource);
            }
            return new CentralObjectApplicationLockSet(dbContext, connection, acquired, closeConnection);
        }
        catch
        {
            await ReleaseAsync(connection, acquired).ConfigureAwait(false);
            if (closeConnection)
            {
                await dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    internal async Task EnsureHeldAsync(CancellationToken cancellationToken)
    {
        if (released || connection.State != ConnectionState.Open)
        {
            throw new CentralObjectApplicationLockLostException();
        }
        // The generated SQL contains parameter placeholders only; every resource remains a SQL parameter.
#pragma warning disable CA2100
        await using var command = new SqlCommand(
            $"SELECT COUNT(*) FROM (VALUES {string.Join(", ", resources.Select((_, index) => $"(@resource{index})"))}) AS expected([resource]) WHERE APPLOCK_MODE(N'public', expected.[resource], N'Session') = N'Exclusive';",
            connection)
#pragma warning restore CA2100
        {
            Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction() as SqlTransaction
        };
        for (var index = 0; index < resources.Count; index++)
        {
            _ = command.Parameters.AddWithValue($"@resource{index}", resources[index]);
        }
        int held;
        try
        {
            held = (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }
        catch (InvalidOperationException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CentralObjectApplicationLockLostException(
                "The central object application lock session closed during ownership verification.", exception);
        }
        if (held != resources.Count)
        {
            throw new CentralObjectApplicationLockLostException();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (released)
        {
            return;
        }
        released = true;
        try
        {
            await ReleaseAsync(connection, resources).ConfigureAwait(false);
        }
        finally
        {
            if (closeConnection)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task AcquireResourceAsync(
        SqlConnection connection,
        string resource,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("sys.sp_getapplock", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = checked((int)Math.Ceiling(CentralObjectApplicationLock.AcquisitionTimeout.TotalSeconds) + 5)
        };
        _ = command.Parameters.AddWithValue("@Resource", resource);
        _ = command.Parameters.AddWithValue("@LockMode", "Exclusive");
        _ = command.Parameters.AddWithValue("@LockOwner", "Session");
        _ = command.Parameters.AddWithValue(
            "@LockTimeout", (int)CentralObjectApplicationLock.AcquisitionTimeout.TotalMilliseconds);
        var result = command.Parameters.Add("@RETURN_VALUE", SqlDbType.Int);
        result.Direction = ParameterDirection.ReturnValue;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var lockResult = result.Value is int status ? status : int.MinValue;
        if (lockResult < 0)
        {
            throw new InvalidOperationException(
                $"The central object application lock failed with status {lockResult} within the {CentralObjectApplicationLock.AcquisitionTimeout.TotalMilliseconds:0} ms timeout.");
        }
    }

    private static async Task ReleaseAsync(SqlConnection connection, IReadOnlyList<string> resources)
    {
        if (connection.State != ConnectionState.Open)
        {
            return;
        }
        for (var index = resources.Count - 1; index >= 0; index--)
        {
            try
            {
                await using var command = new SqlCommand("sys.sp_releaseapplock", connection)
                {
                    CommandType = CommandType.StoredProcedure,
                    CommandTimeout = checked((int)Math.Ceiling(CentralObjectApplicationLock.AcquisitionTimeout.TotalSeconds) + 5)
                };
                _ = command.Parameters.AddWithValue("@Resource", resources[index]);
                _ = command.Parameters.AddWithValue("@LockOwner", "Session");
                await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (SqlException)
            {
                // A broken session has already released all of its application locks.
                return;
            }
            catch (InvalidOperationException)
            {
                // A closed connection has already released all of its application locks.
                return;
            }
        }
    }
}

internal sealed class CentralObjectApplicationLockLostException : InvalidOperationException
{
    public CentralObjectApplicationLockLostException()
        : base("The central object application lock set is no longer held.")
    {
    }

    public CentralObjectApplicationLockLostException(string message)
        : base(message)
    {
    }

    public CentralObjectApplicationLockLostException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
