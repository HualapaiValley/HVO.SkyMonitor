using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Data;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentIdentitySeederTests
{
    private const string ConfiguredEmail = "configured-owner@cameraagent.test";
    private const string ConfiguredPassword = "ConfiguredOwner!123";

    [TestMethod]
    public async Task InitializeAsync_PromotesExistingConfiguredOwner()
    {
        using var fixture = await IdentityFixture.CreateAsync().ConfigureAwait(false);
        await fixture.CreateUserAsync(ConfiguredEmail, ConfiguredPassword, isSiteOwner: false).ConfigureAwait(false);

        await fixture.InitializeSeederAsync().ConfigureAwait(false);

        var configuredOwner = await fixture.FindByEmailAsync(ConfiguredEmail).ConfigureAwait(false);
        Assert.IsNotNull(configuredOwner);
        Assert.IsTrue(configuredOwner.IsSiteOwner);
        Assert.AreEqual(1, await fixture.CountOwnersAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task InitializeAsync_DemotesStaleOwner()
    {
        using var fixture = await IdentityFixture.CreateAsync().ConfigureAwait(false);
        await fixture.CreateUserAsync(ConfiguredEmail, ConfiguredPassword, isSiteOwner: true).ConfigureAwait(false);
        await fixture.CreateUserAsync("stale-owner@cameraagent.test", "StaleOwner!123", isSiteOwner: true)
            .ConfigureAwait(false);

        await fixture.InitializeSeederAsync().ConfigureAwait(false);

        var configuredOwner = await fixture.FindByEmailAsync(ConfiguredEmail).ConfigureAwait(false);
        var staleOwner = await fixture.FindByEmailAsync("stale-owner@cameraagent.test").ConfigureAwait(false);
        Assert.IsNotNull(configuredOwner);
        Assert.IsNotNull(staleOwner);
        Assert.IsTrue(configuredOwner.IsSiteOwner);
        Assert.IsFalse(staleOwner.IsSiteOwner);
        Assert.AreEqual(1, await fixture.CountOwnersAsync().ConfigureAwait(false));
    }

    private sealed class IdentityFixture : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _serviceProvider;

        private IdentityFixture(SqliteConnection connection, ServiceProvider serviceProvider)
        {
            _connection = connection;
            _serviceProvider = serviceProvider;
        }

        public static async Task<IdentityFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync().ConfigureAwait(false);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDataProtection();
            services.AddDbContext<ApplicationDbContext>(options =>
            {
                options.UseSqlite(connection);
                options.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
            });
            services.AddIdentityCore<ApplicationUser>(options =>
                {
                    options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
                })
                .AddEntityFrameworkStores<ApplicationDbContext>()
                .AddDefaultTokenProviders();
            services.AddSingleton<IOptions<LocalIdentityOptions>>(Options.Create(new LocalIdentityOptions
            {
                AdminEmail = ConfiguredEmail,
                AdminPassword = ConfiguredPassword
            }));
            services.AddSingleton<CameraAgentIdentitySeeder>();

            var serviceProvider = services.BuildServiceProvider();
            using var scope = serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await dbContext.Database.MigrateAsync().ConfigureAwait(false);

            return new IdentityFixture(connection, serviceProvider);
        }

        public async Task CreateUserAsync(string email, string password, bool isSiteOwner)
        {
            using var scope = _serviceProvider.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var result = await userManager.CreateAsync(
                new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    IsSiteOwner = isSiteOwner
                },
                password).ConfigureAwait(false);

            Assert.IsTrue(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Description)));
        }

        public Task InitializeSeederAsync()
            => _serviceProvider.GetRequiredService<CameraAgentIdentitySeeder>()
                .InitializeAsync(CancellationToken.None);

        public async Task<ApplicationUser?> FindByEmailAsync(string email)
        {
            using var scope = _serviceProvider.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users
                .AsNoTracking()
                .SingleOrDefaultAsync(user => user.Email == email)
                .ConfigureAwait(false);
        }

        public async Task<int> CountOwnersAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users
                .CountAsync(user => user.IsSiteOwner)
                .ConfigureAwait(false);
        }

        public void Dispose()
        {
            _serviceProvider.Dispose();
            _connection.Dispose();
        }
    }
}
