using HVO.SkyMonitor.Astronomy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace HVO.SkyMonitor.Catalog.Sqlite.Tests;

[TestClass]
internal sealed class InstalledCelestialCatalogTests
{
    [TestMethod]
    public void InternalTestClassSupportsReflectionConstruction()
    {
        var instance = new InstalledCelestialCatalogTests();

        Assert.IsNotNull(instance);
    }

    [TestMethod]
    public void RegistrationsResolveOnceFromFinalConfigurationAndShareCatalogIdentity()
    {
        using var installation = CatalogSnapshotResolverTests.CreateInstallation();
        var logger = new RecordingLogger<SqliteCelestialCatalog>();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(Configuration(("Catalog:Root", "missing"),
            ("Catalog:RequiredPackageKind", "Production")));
        services.AddSingleton<ILogger<SqliteCelestialCatalog>>(logger);
        services.AddInstalledCelestialCatalog();
        services.AddSingleton<IConfiguration>(Configuration(("Catalog:Root", installation.Root),
            ("Catalog:RequiredPackageKind", "fixture")));
        using var provider = services.BuildServiceProvider();

        var snapshot = provider.GetRequiredService<CatalogSnapshotResult>();
        File.WriteAllText(installation.ManifestPath, "{}");

        var concrete = provider.GetRequiredService<SqliteCelestialCatalog>();
        Assert.AreSame(snapshot.Catalog, concrete);
        Assert.AreSame(concrete, provider.GetRequiredService<ICelestialCatalog>());
        Assert.AreSame(concrete, provider.GetRequiredService<IHipparcosCatalog>());
        Assert.AreSame(concrete, provider.GetRequiredService<ICelestialCatalogMetadataSource>());
        Assert.AreSame(snapshot, provider.GetRequiredService<CatalogSnapshotResult>());

        Assert.HasCount(1, logger.Entries);
        var entry = logger.Entries[0];
        Assert.AreEqual(3000, entry.EventId.Id);
        Assert.AreEqual("InstalledCatalogSnapshotResolved", entry.EventId.Name);
        Assert.AreEqual(LogLevel.Information, entry.Level);
        Assert.AreEqual(CatalogSnapshotPackageKind.Fixture, entry.Properties["Kind"]);
        Assert.AreEqual("hyg-v42-fixture", entry.Properties["CatalogId"]);
        Assert.AreEqual("explicit-manifest-v2", entry.Properties["CatalogIdentitySource"]);
        Assert.AreEqual("4.2-fixture.1", entry.Properties["CatalogVersion"]);
        Assert.AreEqual("2", entry.Properties["SchemaVersion"]);
        Assert.AreEqual("3", entry.Properties["PreprocessingVersion"]);
        Assert.AreEqual(snapshot.DatabaseSha256, entry.Properties["DatabaseSha256"]);
        Assert.AreEqual(9L, entry.Properties["RowCount"]);
        Assert.HasCount(9, entry.Properties);
        Assert.IsFalse(entry.Message.Contains(installation.Root, StringComparison.Ordinal));
    }

    [TestMethod]
    public void ConfigurationErrorsFailBeforeCatalogFallbackCanOccur()
    {
        using var installation = CatalogSnapshotResolverTests.CreateInstallation();

        var missingRoot = Assert.ThrowsExactly<InvalidOperationException>(() =>
            Resolve(Configuration(("Catalog:RequiredPackageKind", "Fixture"))));
        StringAssert.Contains(missingRoot.Message, "Catalog:Root", StringComparison.Ordinal);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            Resolve(Configuration(("Catalog:Root", " "), ("Catalog:RequiredPackageKind", "Fixture"))));

        var missingKind = Assert.ThrowsExactly<InvalidOperationException>(() =>
            Resolve(Configuration(("Catalog:Root", installation.Root))));
        StringAssert.Contains(missingKind.Message, "Catalog:RequiredPackageKind", StringComparison.Ordinal);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            Resolve(Configuration(("Catalog:Root", installation.Root),
                ("Catalog:RequiredPackageKind", "0"))));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            Resolve(Configuration(("Catalog:Root", installation.Root),
                ("Catalog:RequiredPackageKind", " fixture "))));
    }

    [TestMethod]
    public async Task HealthUsesLoadedIdentityAndDistinguishesFixtureFromProduction()
    {
        using var installation = CatalogSnapshotResolverTests.CreateInstallation();
        var snapshot = CatalogSnapshotResolver.Resolve(new CatalogSnapshotResolverOptions(installation.Root)
        {
            ExpectedPackageKind = CatalogSnapshotPackageKind.Fixture
        });
        File.Delete(installation.DatabasePath);

        var fixture = await new CatalogSnapshotHealthCheck(snapshot)
            .CheckHealthAsync(new HealthCheckContext(), CancellationToken.None).ConfigureAwait(false);
        var production = await new CatalogSnapshotHealthCheck(snapshot with
        {
            PackageKind = CatalogSnapshotPackageKind.Production
        })
            .CheckHealthAsync(new HealthCheckContext(), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(HealthStatus.Degraded, fixture.Status);
        Assert.AreEqual("Fixture celestial catalog snapshot is installed; production data is not active.",
            fixture.Description);
        Assert.AreEqual(HealthStatus.Healthy, production.Status);
        Assert.AreEqual("Production celestial catalog snapshot is installed.", production.Description);
        Assert.HasCount(8, fixture.Data);
        Assert.AreEqual("Fixture", fixture.Data["Kind"]);
        Assert.AreEqual("hyg-v42-fixture", fixture.Data["CatalogId"]);
        Assert.AreEqual("explicit-manifest-v2", fixture.Data["CatalogIdentitySource"]);
        Assert.AreEqual(snapshot.DatabaseSha256, fixture.Data["DatabaseSha256"]);
        Assert.AreEqual(snapshot.RowCount, fixture.Data["RowCount"]);
        Assert.IsFalse(fixture.Data.Keys.Any(static key => key.Contains("Path", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void HealthBuilderRegistersStableCatalogName()
    {
        var builder = new RecordingHealthChecksBuilder();

        var returned = builder.AddInstalledCelestialCatalogHealthCheck();

        Assert.AreSame(builder, returned);
        Assert.HasCount(1, builder.Registrations);
        var registration = builder.Registrations[0];
        Assert.AreEqual("catalog", registration.Name);
        CollectionAssert.Contains(registration.Tags.ToArray(), "dependency");
    }

    private static void Resolve(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSingleton<ILogger<SqliteCelestialCatalog>>(new RecordingLogger<SqliteCelestialCatalog>());
        services.AddInstalledCelestialCatalog();
        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<CatalogSnapshotResult>();
    }

    private static TestConfiguration Configuration(params (string Key, string? Value)[] values)
        => new TestConfiguration(values.ToDictionary(static value => value.Key, static value => value.Value,
            StringComparer.OrdinalIgnoreCase));

    private sealed class TestConfiguration(IReadOnlyDictionary<string, string?> values) : IConfiguration
    {
        public string? this[string key]
        {
            get => values.GetValueOrDefault(key);
            set => throw new NotSupportedException();
        }

        public IEnumerable<IConfigurationSection> GetChildren() => [];

        public IChangeToken GetReloadToken() => new CancellationChangeToken(CancellationToken.None);

        public IConfigurationSection GetSection(string key) => throw new NotSupportedException();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = ((IEnumerable<KeyValuePair<string, object?>>)(object)state!)
                .ToDictionary(static property => property.Key, static property => property.Value,
                    StringComparer.Ordinal);
            Entries.Add(new LogEntry(logLevel, eventId, formatter(state, exception), properties));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        EventId EventId,
        string Message,
        IReadOnlyDictionary<string, object?> Properties);

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private sealed class RecordingHealthChecksBuilder : IHealthChecksBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public List<HealthCheckRegistration> Registrations { get; } = [];

        public IHealthChecksBuilder Add(HealthCheckRegistration registration)
        {
            Registrations.Add(registration);
            return this;
        }
    }
}
