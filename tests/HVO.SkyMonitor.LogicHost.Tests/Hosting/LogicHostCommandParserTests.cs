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

    [TestMethod]
    [DataRow("--host-mode=object-store-backup", "ObjectStoreBackup")]
    [DataRow("--host-mode=object-store-restore", "ObjectStoreRestore")]
    [DataRow("--host-mode=object-store-verify", "ObjectStoreVerify")]
    public void ObjectStoreModes_RequirePathAndKeepItOutOfForwardedArguments(string argument, string expected)
    {
        var command = LogicHostCommandParser.Parse([argument, "--path=/var/backups/objects", "--environment=Production"]);
        Assert.AreEqual(expected, command.Mode.ToString());
        Assert.AreEqual("/var/backups/objects", command.Path);
        CollectionAssert.AreEqual(new[] { "--environment=Production" }, command.ForwardedArguments.ToArray());

        var missing = Assert.ThrowsExactly<ArgumentException>(() => LogicHostCommandParser.Parse([argument]));
        StringAssert.Contains(missing.Message, "--path");
    }

    [TestMethod]
    [DataRow("--path=/x")]
    [DataRow("--host-mode=database-initialize", "--path=/x")]
    [DataRow("--host-mode=object-store-backup", "--path=")]
    [DataRow("--host-mode=object-store-backup", "--path=/a", "--path=/b")]
    public void PathArgument_IsOnlyForObjectStoreModesAndOnlyOnce(params string[] arguments)
    {
        Assert.ThrowsExactly<ArgumentException>(() => LogicHostCommandParser.Parse(arguments));
    }
}
