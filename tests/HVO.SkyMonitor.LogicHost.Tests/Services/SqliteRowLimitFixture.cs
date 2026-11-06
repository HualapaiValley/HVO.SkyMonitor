using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using HVO.SkyMonitor.LogicHost.Data;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

// Query regressions, not schema qualification: SQL Server constraints/locking remain covered by Integration.
// Keep the real model's columns and relationships, with UTC ticks for SQLite's DateTimeOffset ordering.
internal sealed class SqliteRowLimitFixture : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    internal ApplicationDbContext Db { get; }
    internal QueryRecorder Queries { get; } = new();

    [SuppressMessage("Security", "CA2100", Justification = "Identifiers come only from EF model metadata.")]
    internal SqliteRowLimitFixture()
    {
        connection.Open();
        Db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .ReplaceService<IModelCustomizer, QueryModelCustomizer>()
            .ConfigureWarnings(warnings => warnings.Throw(CoreEventId.RowLimitingOperationWithoutOrderByWarning))
            .AddInterceptors(Queries).Options);
        foreach (var table in Db.Model.GetEntityTypes().GroupBy(entity => entity.GetTableName()))
        {
            var columns = table.SelectMany(entity => entity.GetProperties())
                .Select(property => property.GetColumnName()).Distinct();
            using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TABLE \"{table.Key}\" ({string.Join(", ", columns.Select(column => $"\"{column}\""))})";
            command.ExecuteNonQuery();
        }
    }

    [SuppressMessage("Security", "CA2100", Justification = "Only EF model identifiers are interpolated; fixture values are parameters.")]
    internal void Insert<TEntity>(TEntity entity) where TEntity : class
    {
        var entry = Db.Entry(entity);
        var properties = entry.Properties.ToArray();
        using var command = connection.CreateCommand();
        command.CommandText = $"INSERT INTO \"{entry.Metadata.GetTableName()}\" " +
            $"({string.Join(", ", properties.Select(property => $"\"{property.Metadata.GetColumnName()}\""))}) " +
            $"VALUES ({string.Join(", ", properties.Select((_, index) => $"$p{index}"))})";
        for (var index = 0; index < properties.Length; index++)
        {
            var property = properties[index];
            var converter = property.Metadata.GetTypeMapping().Converter;
            command.Parameters.AddWithValue($"$p{index}",
                (converter is null ? property.CurrentValue : converter.ConvertToProvider(property.CurrentValue)) ?? DBNull.Value);
        }
        command.ExecuteNonQuery();
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync().ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);
    }

    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated by EF service replacement.")]
    private sealed class QueryModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties()))
            {
                // The query-only schema has no SQL Server-generated defaults or rowversions.
                property.ValueGenerated = ValueGenerated.Never;
                if ((Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType) == typeof(DateTimeOffset))
                    property.SetValueConverter(new ValueConverter<DateTimeOffset, long>(
                        value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero)));
            }
        }
    }

    internal sealed class QueryRecorder : DbCommandInterceptor
    {
        internal List<string> Commands { get; } = [];
        [SuppressMessage("Security", "CA2100", Justification = "Removes a fixed SQL Server hint from parameterized EF SQL in the SQLite-only fixture.")]
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            // Exercise the read predicate, not SQL Server's mutation lock hint.
            command.CommandText = command.CommandText.Replace(" WITH (UPDLOCK, HOLDLOCK)", "", StringComparison.Ordinal);
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
