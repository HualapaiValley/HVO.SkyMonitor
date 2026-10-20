using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace HVO.SkyMonitor.CameraAgent.Tests.Data;

internal sealed class IdentityRowLimitQueryRecorder : DbCommandInterceptor
{
    internal List<string> Commands { get; } = [];

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Commands.Add(command.CommandText);
        return ValueTask.FromResult(result);
    }

    internal void AssertOrderedBy(string key)
    {
        Assert.HasCount(1, Commands);
        var sql = Commands[0];
        StringAssert.Contains(sql, "LIMIT", StringComparison.Ordinal);
        var orderBy = sql.IndexOf("ORDER BY ", StringComparison.Ordinal);
        Assert.IsTrue(orderBy >= 0, sql);
        var orderLine = sql[(orderBy + "ORDER BY ".Length)..].Split('\n')[0].Trim();
        StringAssert.EndsWith(orderLine, $".\"{key}\"", StringComparison.Ordinal);
    }
}
