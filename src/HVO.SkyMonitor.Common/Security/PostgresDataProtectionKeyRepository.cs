using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HVO.SkyMonitor.Common.Security;

/// <summary>
/// Options used by <see cref="PostgresDataProtectionKeyRepository"/> to persist Data Protection keys.
/// </summary>
public sealed class PostgresDataProtectionOptions
{
    public string ConnectionString { get; set; } = string.Empty;

    public string SchemaName { get; set; } = "security";

    public string TableName { get; set; } = "dataprotectionkeys";
}

/// <summary>
/// Stores Data Protection keys in PostgreSQL so that multiple applications can share a single key ring.
/// </summary>
public sealed class PostgresDataProtectionKeyRepository : IXmlRepository
{
    private readonly PostgresDataProtectionOptions _options;
    private readonly ILogger<PostgresDataProtectionKeyRepository>? _logger;
    private readonly object _initializationLock = new();
    private volatile bool _initialized;

    public PostgresDataProtectionKeyRepository(
        IOptions<PostgresDataProtectionOptions> optionsAccessor,
        ILogger<PostgresDataProtectionKeyRepository>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(optionsAccessor);
        _options = optionsAccessor.Value;
        _logger = logger;
    }

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        EnsureInitialized();
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT xml FROM {GetQualifiedTableName()} ORDER BY createdon ASC";

        using var reader = command.ExecuteReader();
        var list = new List<XElement>();
        while (reader.Read())
        {
            var xml = reader.GetString(0);
            list.Add(XElement.Parse(xml, LoadOptions.PreserveWhitespace));
        }

        return list;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        EnsureInitialized();

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"INSERT INTO {GetQualifiedTableName()} (id, friendlyname, xml, createdon) VALUES (@id, @friendlyname, @xml, @createdon)";
        command.Parameters.AddWithValue("@id", Guid.NewGuid());
        command.Parameters.AddWithValue("@friendlyname", string.IsNullOrWhiteSpace(friendlyName) ? Guid.NewGuid().ToString() : friendlyName);
        command.Parameters.AddWithValue("@xml", element.ToString(SaveOptions.DisableFormatting));
        command.Parameters.AddWithValue("@createdon", DateTimeOffset.UtcNow);
        command.ExecuteNonQuery();
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (_initializationLock)
        {
            if (_initialized)
            {
                return;
            }

            using var connection = CreateConnection();
            using var schemaCommand = connection.CreateCommand();
            var schemaName = NormalizeSchemaName();
            schemaCommand.CommandText = $"CREATE SCHEMA IF NOT EXISTS {QuoteIdentifier(schemaName)}";
            schemaCommand.ExecuteNonQuery();

            using var tableCommand = connection.CreateCommand();
            tableCommand.CommandText = $"CREATE TABLE IF NOT EXISTS {GetQualifiedTableName()} (\n                id uuid PRIMARY KEY,\n                friendlyname text NOT NULL,\n                xml text NOT NULL,\n                createdon timestamptz NOT NULL DEFAULT now()\n            )";
            tableCommand.ExecuteNonQuery();

            _initialized = true;
            _logger?.LogInformation("Data Protection key repository initialized using {Schema}.{Table}", schemaName, _options.TableName);
        }
    }

    private NpgsqlConnection CreateConnection()
    {
        var connection = new NpgsqlConnection(_options.ConnectionString);
        connection.Open();
        return connection;
    }

    private string GetQualifiedTableName()
    {
        var schema = QuoteIdentifier(NormalizeSchemaName());
        var table = QuoteIdentifier(_options.TableName);
        return $"{schema}.{table}";
    }

    private static string QuoteIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return $"\"{identifier.Replace("\"", "\"\"")}\"";
    }

    private string NormalizeSchemaName() => string.IsNullOrWhiteSpace(_options.SchemaName) ? "public" : _options.SchemaName;
}

public static class DataProtectionServiceCollectionExtensions
{
    public static IServiceCollection AddPostgresDataProtectionKeyRepository(
        this IServiceCollection services,
        Action<PostgresDataProtectionOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);

        services.AddOptions<PostgresDataProtectionOptions>()
            .Configure(configureOptions)
            .Validate(options => !string.IsNullOrWhiteSpace(options.ConnectionString), "A PostgreSQL connection string is required for Data Protection persistence.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.TableName), "A table name is required for Data Protection persistence.")
            .ValidateOnStart();

        services.TryAddSingleton<IXmlRepository, PostgresDataProtectionKeyRepository>();
        return services;
    }
}
