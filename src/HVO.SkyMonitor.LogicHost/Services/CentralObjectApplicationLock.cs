using System.Data;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

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
                CommandTimeout = checked((int)Math.Ceiling(AcquisitionTimeout.TotalSeconds) + 5)
            };
            _ = command.Parameters.AddWithValue("@Resource", resource);
            _ = command.Parameters.AddWithValue("@LockMode", "Exclusive");
            _ = command.Parameters.AddWithValue("@LockOwner", "Session");
            _ = command.Parameters.AddWithValue("@LockTimeout", (int)AcquisitionTimeout.TotalMilliseconds);
            var result = command.Parameters.Add("@RETURN_VALUE", SqlDbType.Int);
            result.Direction = ParameterDirection.ReturnValue;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            var lockResult = result.Value is int status ? status : int.MinValue;
            if (lockResult < 0)
            {
                throw new InvalidOperationException(
                    $"The central object application lock failed with status {lockResult} within the {AcquisitionTimeout.TotalMilliseconds:0} ms timeout.");
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
