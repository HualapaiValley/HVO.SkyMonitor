using Microsoft.Data.SqlClient;

namespace HVO.SkyMonitor.LogicHost.Data;

internal enum LogicHostSqlConnectionPurpose
{
    Runtime,
    DatabaseInitialization
}

internal sealed record LogicHostSqlConnectionProfile(string ConnectionString, string ApplicationName);

internal static class LogicHostSqlConnectionProfiles
{
    internal const string RuntimeApplicationName = "HVO.SkyMonitor.LogicHost";
    internal const string InitializationApplicationName = "HVO.SkyMonitor.LogicHost.DatabaseInitialization";
    private const string RuntimeConnectionName = "skymonitordb";
    private const string MigrationConnectionName = "skymonitordb-migrations";

    internal static LogicHostSqlConnectionProfile Resolve(
        IConfiguration configuration,
        IHostEnvironment environment,
        LogicHostSqlConnectionPurpose purpose)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        var runtime = configuration.GetConnectionString(RuntimeConnectionName)
            ?? configuration["ConnectionStrings:skymonitordb"];
        if (string.IsNullOrWhiteSpace(runtime) &&
            (environment.IsDevelopment() || environment.IsEnvironment("Testing")))
        {
            runtime = configuration.GetConnectionString("DefaultConnection")
                ?? configuration["ConnectionStrings:DefaultConnection"];
        }

        string? selected;
        string applicationName;
        if (purpose == LogicHostSqlConnectionPurpose.DatabaseInitialization)
        {
            selected = configuration.GetConnectionString(MigrationConnectionName)
                ?? configuration["ConnectionStrings:skymonitordb-migrations"];
            if (string.IsNullOrWhiteSpace(selected) &&
                (environment.IsDevelopment() || environment.IsEnvironment("Testing")))
            {
                selected = runtime;
            }
            applicationName = InitializationApplicationName;
        }
        else
        {
            selected = runtime;
            applicationName = RuntimeApplicationName;
        }

        if (string.IsNullOrWhiteSpace(selected))
        {
            throw new InvalidOperationException(purpose == LogicHostSqlConnectionPurpose.DatabaseInitialization
                ? "A dedicated SkyMonitor SQL Server migration connection string must be configured."
                : "A SkyMonitor SQL Server runtime connection string must be configured.");
        }
        try
        {
            var normalized = new SqlConnectionStringBuilder(selected) { ApplicationName = applicationName };
            return new(normalized.ConnectionString, applicationName);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            // Do not retain the parser exception: it can include fragments of the configured secret.
            throw new InvalidOperationException(purpose == LogicHostSqlConnectionPurpose.DatabaseInitialization
                ? "The configured SkyMonitor SQL Server migration connection string is malformed."
                : "The configured SkyMonitor SQL Server runtime connection string is malformed.");
        }
    }
}
