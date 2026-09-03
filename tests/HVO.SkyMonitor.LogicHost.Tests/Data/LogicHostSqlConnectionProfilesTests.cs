using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace HVO.SkyMonitor.Tests.LogicHost.Data;

[TestClass]
[TestCategory("Unit")]
public sealed class LogicHostSqlConnectionProfilesTests
{
    private const string Runtime = "Server=runtime;Database=SkyMonitor;User Id=runtime-user;Password=runtime-secret";
    private const string Migration = "Server=migration;Database=SkyMonitor;User Id=migration-user;Password=migration-secret";

    [TestMethod]
    public void Runtime_UsesRuntimeConnectionAndStableApplicationName()
    {
        var profile = LogicHostSqlConnectionProfiles.Resolve(
            Configuration(("ConnectionStrings:skymonitordb", Runtime)),
            Environment("Production"),
            LogicHostSqlConnectionPurpose.Runtime);

        Assert.AreEqual(LogicHostSqlConnectionProfiles.RuntimeApplicationName, profile.ApplicationName);
        StringAssert.Contains(profile.ConnectionString, "Data Source=runtime", StringComparison.OrdinalIgnoreCase);
        StringAssert.Contains(profile.ConnectionString,
            $"Application Name={LogicHostSqlConnectionProfiles.RuntimeApplicationName}",
            StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("conflicting-client")]
    [DataRow("   ")]
    public void Runtime_NormalizesAbsentConflictingAndBlankApplicationNames(string configuredApplicationName)
    {
        var configured = new SqlConnectionStringBuilder(Runtime)
        {
            ApplicationName = configuredApplicationName,
            MaxPoolSize = 23,
            ConnectTimeout = 7,
            LoadBalanceTimeout = 41
        };

        var profile = LogicHostSqlConnectionProfiles.Resolve(
            Configuration(("ConnectionStrings:skymonitordb", configured.ConnectionString)),
            Environment("Production"),
            LogicHostSqlConnectionPurpose.Runtime);
        var normalized = new SqlConnectionStringBuilder(profile.ConnectionString);

        Assert.AreEqual(LogicHostSqlConnectionProfiles.RuntimeApplicationName, normalized.ApplicationName);
        Assert.AreEqual(23, normalized.MaxPoolSize);
        Assert.AreEqual(7, normalized.ConnectTimeout);
        Assert.AreEqual(41, normalized.LoadBalanceTimeout);
    }

    [TestMethod]
    [DataRow(false, "runtime")]
    [DataRow(true, "migration")]
    public void MalformedConnectionString_FailsWithoutDisclosingConfiguredSecret(
        bool migration,
        string expectedPurpose)
    {
        const string secret = "Issue254_Do_Not_Disclose!42";
        var malformed = $"Server=sql;Password={secret};Application Name=\"unterminated";
        var purpose = migration
            ? LogicHostSqlConnectionPurpose.DatabaseInitialization
            : LogicHostSqlConnectionPurpose.Runtime;
        var key = migration
            ? "ConnectionStrings:skymonitordb-migrations"
            : "ConnectionStrings:skymonitordb";

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            LogicHostSqlConnectionProfiles.Resolve(
                Configuration((key, malformed)),
                Environment("Production"),
                purpose));

        StringAssert.Contains(exception.Message, expectedPurpose, StringComparison.Ordinal);
        Assert.IsNull(exception.InnerException);
        Assert.IsFalse(exception.ToString().Contains(secret, StringComparison.Ordinal));
        Assert.IsFalse(exception.ToString().Contains("unterminated", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ProductionInitialization_RequiresAndUsesDedicatedConnection()
    {
        var configuration = Configuration(
            ("ConnectionStrings:skymonitordb", Runtime),
            ("ConnectionStrings:skymonitordb-migrations", Migration));

        var profile = LogicHostSqlConnectionProfiles.Resolve(
            configuration, Environment("Production"), LogicHostSqlConnectionPurpose.DatabaseInitialization);

        Assert.AreEqual(LogicHostSqlConnectionProfiles.InitializationApplicationName, profile.ApplicationName);
        StringAssert.Contains(profile.ConnectionString, "Data Source=migration", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void ProductionInitialization_ReplacesConflictingApplicationName()
    {
        var configured = new SqlConnectionStringBuilder(Migration)
        {
            ApplicationName = "conflicting-migration-client"
        };

        var profile = LogicHostSqlConnectionProfiles.Resolve(
            Configuration(("ConnectionStrings:skymonitordb-migrations", configured.ConnectionString)),
            Environment("Production"),
            LogicHostSqlConnectionPurpose.DatabaseInitialization);

        Assert.AreEqual(
            LogicHostSqlConnectionProfiles.InitializationApplicationName,
            new SqlConnectionStringBuilder(profile.ConnectionString).ApplicationName);
    }

    [TestMethod]
    public void ProductionInitialization_DoesNotFallBackToRuntimeCredential()
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            LogicHostSqlConnectionProfiles.Resolve(
                Configuration(("ConnectionStrings:skymonitordb", Runtime)),
                Environment("Production"),
                LogicHostSqlConnectionPurpose.DatabaseInitialization));

        Assert.IsFalse(exception.Message.Contains("secret", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("Development")]
    [DataRow("Testing")]
    public void LocalInitialization_FallsBackToRuntimeCredential(string environmentName)
    {
        var profile = LogicHostSqlConnectionProfiles.Resolve(
            Configuration(("ConnectionStrings:skymonitordb", Runtime)),
            Environment(environmentName),
            LogicHostSqlConnectionPurpose.DatabaseInitialization);

        StringAssert.Contains(profile.ConnectionString, "Data Source=runtime", StringComparison.OrdinalIgnoreCase);
        StringAssert.Contains(profile.ConnectionString,
            $"Application Name={LogicHostSqlConnectionProfiles.InitializationApplicationName}",
            StringComparison.OrdinalIgnoreCase);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(
            values.ToDictionary(item => item.Key, item => (string?)item.Value, StringComparer.Ordinal)).Build();

    private static IHostEnvironment Environment(string name) => new TestHostEnvironment { EnvironmentName = name };

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = string.Empty;
        public string ApplicationName { get; set; } = string.Empty;
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
