using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services.Elastic;

/// <summary>
/// Per-runner database application lock (#430) that serializes the abandonment or retirement of an elastic instance
/// with the registry's registration of the same runner id. Both sides take the lock inside their transaction, so a
/// registration whose abandonment check passed can never overwrite a retirement committed before its own save: the
/// check and the save happen entirely before or entirely after the abandonment.
/// </summary>
internal static class ElasticRunnerRegistrationLock
{
    private const int LockTimeoutMilliseconds = 5000;

    /// <summary>Acquires the transaction-scoped lock for <paramref name="runnerId"/>; the caller must hold an open transaction.</summary>
    public static async Task AcquireAsync(ApplicationDbContext dbContext, string runnerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(runnerId);
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("The runner registration lock requires an open transaction.");
        }
        var result = new SqlParameter("@result", System.Data.SqlDbType.Int) { Direction = System.Data.ParameterDirection.Output };
        await dbContext.Database.ExecuteSqlRawAsync(
            "EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = @timeout;",
            [new SqlParameter("@resource", $"hvo-elastic-runner:{runnerId}"), new SqlParameter("@timeout", LockTimeoutMilliseconds), result], cancellationToken)
            .ConfigureAwait(false);
        if (result.Value is not int acquired || acquired < 0)
        {
            throw new TimeoutException($"The registration lock for runner '{runnerId}' was not acquired.");
        }
    }
}
