using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

/// <summary>
/// Which failures graph convergence treats as its own deadlock and reruns. Each wrapped shape is one SQL Server or EF
/// Core produced when convergence was the victim.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CentralProcessingGraphSchedulerDeadlockTests
{
    [TestMethod]
    public void ADeadlockIsRecognizedHoweverItIsWrapped()
    {
        Assert.IsTrue(CentralProcessingGraphScheduler.IsDeadlock(CreateSqlException(1205)), "raw");
        Assert.IsTrue(CentralProcessingGraphScheduler.IsDeadlock(
            new InvalidOperationException("transient", CreateSqlException(1205))), "execution strategy");
        Assert.IsTrue(CentralProcessingGraphScheduler.IsDeadlock(new InvalidOperationException(
            "transient", new DbUpdateException("save failed", CreateSqlException(1205)))), "save through strategy");
        Assert.IsTrue(CentralProcessingGraphScheduler.IsDeadlock(CreateSqlException(3621, 1205)), "later error");
    }

    [TestMethod]
    public void OtherFailuresAreNotRetried()
    {
        Assert.IsFalse(CentralProcessingGraphScheduler.IsDeadlock(CreateSqlException(1222)), "lock timeout");
        Assert.IsFalse(CentralProcessingGraphScheduler.IsDeadlock(
            new InvalidOperationException("transient", CreateSqlException(-2))), "wrapped timeout");
        Assert.IsFalse(CentralProcessingGraphScheduler.IsDeadlock(new InvalidOperationException("plain")), "plain");
        Assert.IsFalse(CentralProcessingGraphScheduler.IsDeadlock(
            new InvalidOperationException("1205", new TimeoutException("1205"))), "number only in text");
    }

    /// <summary>
    /// <see cref="SqlException"/> has no public constructor; build one carrying the requested errors, in order, through
    /// SqlClient's internal factory so the predicate can be tested without SQL Server.
    /// </summary>
    private static SqlException CreateSqlException(params int[] numbers)
    {
        const System.Reflection.BindingFlags nonPublic =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public;
        var errorType = typeof(SqlError);
        var errorConstructor = errorType.GetConstructors(nonPublic)
            .Where(candidate => candidate.GetParameters().Length > 0 &&
                candidate.GetParameters()[0].ParameterType == typeof(int))
            .OrderByDescending(candidate => candidate.GetParameters().Length)
            .First();
        var collectionType = typeof(SqlErrorCollection);
        var collection = Activator.CreateInstance(collectionType, nonPublic, null, null, null)!;
        var add = collectionType.GetMethod("Add", nonPublic, [errorType])!;
        foreach (var number in numbers)
        {
            var arguments = errorConstructor.GetParameters().Select((parameter, index) => index == 0
                ? number
                : parameter.ParameterType == typeof(string)
                    ? "test"
                    : parameter.ParameterType.IsValueType
                        ? Activator.CreateInstance(parameter.ParameterType)
                        : null).ToArray();
            _ = add.Invoke(collection, [errorConstructor.Invoke(arguments)]);
        }
        var factory = typeof(SqlException).GetMethod("CreateException", nonPublic, [collectionType, typeof(string)])!;
        return (SqlException)factory.Invoke(null, [collection, "16.0"])!;
    }
}
