using HVO.SkyMonitor.LogicHost.Hosting;

namespace HVO.SkyMonitor.Tests.LogicHost.Hosting;

[TestClass]
[TestCategory("Unit")]
public sealed class LogicHostCommandParserTests
{
    [TestMethod]
    public void EmptyArguments_SelectRuntime()
    {
        var command = LogicHostCommandParser.Parse([]);

        Assert.AreEqual(LogicHostHostMode.Runtime, command.Mode);
        Assert.HasCount(0, command.ForwardedArguments);
    }

    [TestMethod]
    public void DatabaseInitializationArgument_IsRemovedAndPreservesOtherArguments()
    {
        var command = LogicHostCommandParser.Parse([
            "--urls=http://localhost:5000",
            "--host-mode=database-initialize",
            "--environment=Production"]);

        Assert.AreEqual(LogicHostHostMode.DatabaseInitialize, command.Mode);
        CollectionAssert.AreEqual(
            new[] { "--urls=http://localhost:5000", "--environment=Production" },
            command.ForwardedArguments.ToArray());
    }

    [TestMethod]
    [DataRow("--host-mode=runtime")]
    [DataRow("--host-mode=Database-Initialize")]
    [DataRow("--host-mode")]
    [DataRow("--host-mode=database-initialize", "--host-mode=database-initialize")]
    public void InvalidOrDuplicateHostMode_IsRejected(params string[] arguments)
    {
        var exception = Assert.ThrowsExactly<ArgumentException>(() => LogicHostCommandParser.Parse(arguments));

        StringAssert.Contains(exception.Message, "host mode", StringComparison.OrdinalIgnoreCase);
    }
}
