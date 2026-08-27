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
    public void ModelHasSingleInitialMigration()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var dbContext = new ApplicationDbContext(options);

        var migrations = dbContext.Database.GetMigrations().ToArray();

        Assert.HasCount(1, migrations);
        Assert.EndsWith("_InitialIdentity", migrations[0], StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task InitializeAsync_SeedsTemporaryOwnerOnceAndDoesNotRevertReplacement()
    {
        using var fixture = await IdentityFixture.CreateAsync().ConfigureAwait(false);

        await fixture.InitializeSeederAsync().ConfigureAwait(false);

        var owner = await fixture.FindByEmailAsync(ConfiguredEmail).ConfigureAwait(false);
        Assert.IsNotNull(owner);
        Assert.IsTrue(owner.PasswordChangeRequired);
        Assert.AreEqual(OwnerBootstrapStates.TemporaryPassword, await fixture.GetBootstrapStateAsync().ConfigureAwait(false));
        Assert.IsTrue(await fixture.CheckPasswordAsync(ConfiguredPassword).ConfigureAwait(false));

        const string replacementPassword = "ReplacementOwner!456";
        await fixture.ReplacePasswordAsync(ConfiguredPassword, replacementPassword).ConfigureAwait(false);
        await fixture.InitializeSeederAsync().ConfigureAwait(false);

        Assert.IsFalse(await fixture.CheckPasswordAsync(ConfiguredPassword).ConfigureAwait(false));
        Assert.IsTrue(await fixture.CheckPasswordAsync(replacementPassword).ConfigureAwait(false));
        Assert.AreEqual(OwnerBootstrapStates.Ready, await fixture.GetBootstrapStateAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task InitializeAsync_AllowsMissingPasswordOnlyForDurableOwner()
    {
        using var fixture = await IdentityFixture.CreateAsync(
            adminPassword: string.Empty,
            allowMissingPassword: true).ConfigureAwait(false);
        await fixture.CreateUserAsync(ConfiguredEmail, ConfiguredPassword, isSiteOwner: true).ConfigureAwait(false);

        await fixture.InitializeSeederAsync().ConfigureAwait(false);

        Assert.IsTrue(await fixture.CheckPasswordAsync(ConfiguredPassword).ConfigureAwait(false));
        Assert.AreEqual(OwnerBootstrapStates.Ready, await fixture.GetBootstrapStateAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task InitializeAsync_PendingOwnerSurvivesRestartWithoutRuntimePasswordAuthority()
    {
        using var fixture = await IdentityFixture.CreateAsync(
            adminPassword: string.Empty,
            allowMissingPassword: true).ConfigureAwait(false);
        await fixture.CreateUserAsync(
            ConfiguredEmail,
            ConfiguredPassword,
            isSiteOwner: true,
            passwordChangeRequired: true).ConfigureAwait(false);

        await fixture.InitializeSeederAsync().ConfigureAwait(false);

        Assert.AreEqual(
            OwnerBootstrapStates.PasswordChangeRequired,
            await fixture.GetBootstrapStateAsync().ConfigureAwait(false));
        Assert.IsTrue(await fixture.CheckPasswordAsync(ConfiguredPassword).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task InitializeAsync_RejectsConflictingTemporaryPasswordForPendingOwner()
    {
        using var fixture = await IdentityFixture.CreateAsync().ConfigureAwait(false);
        const string durablePassword = "DurableTemporary!418";
        await fixture.CreateUserAsync(
            ConfiguredEmail,
            durablePassword,
            isSiteOwner: true,
            passwordChangeRequired: true).ConfigureAwait(false);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            fixture.InitializeSeederAsync).ConfigureAwait(false);

        Assert.AreEqual("The configured temporary owner password does not match the seeded owner.", exception.Message);
        Assert.IsTrue(await fixture.CheckPasswordAsync(durablePassword).ConfigureAwait(false));
        Assert.IsFalse(exception.Message.Contains(ConfiguredPassword, StringComparison.Ordinal));
        Assert.IsFalse(exception.Message.Contains(durablePassword, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task InitializeAsync_RejectsMissingPasswordWithoutDurableOwner()
    {
        using var fixture = await IdentityFixture.CreateAsync(
            adminPassword: string.Empty,
            allowMissingPassword: true).ConfigureAwait(false);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            fixture.InitializeSeederAsync).ConfigureAwait(false);

        Assert.AreEqual("The initial site owner password is required until local identity has been seeded.", exception.Message);
    }

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

        public static async Task<IdentityFixture> CreateAsync(
            string adminPassword = ConfiguredPassword,
            bool allowMissingPassword = false)
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
                AdminPassword = adminPassword,
                AllowMissingAdminPassword = allowMissingPassword
            }));
            services.AddSingleton<CameraAgentIdentitySeeder>();
            services.AddScoped<OwnerBootstrapStateReader>();

            var serviceProvider = services.BuildServiceProvider();
            using var scope = serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await dbContext.Database.MigrateAsync().ConfigureAwait(false);

            return new IdentityFixture(connection, serviceProvider);
        }

        public async Task CreateUserAsync(
            string email,
            string password,
            bool isSiteOwner,
            bool passwordChangeRequired = false)
        {
            using var scope = _serviceProvider.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var result = await userManager.CreateAsync(
                new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    IsSiteOwner = isSiteOwner,
                    PasswordChangeRequired = passwordChangeRequired
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

        public async Task<bool> CheckPasswordAsync(string password)
        {
            using var scope = _serviceProvider.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var owner = await users.FindByEmailAsync(ConfiguredEmail).ConfigureAwait(false);
            return owner is not null && await users.CheckPasswordAsync(owner, password).ConfigureAwait(false);
        }

        public async Task ReplacePasswordAsync(string currentPassword, string replacementPassword)
        {
            using var scope = _serviceProvider.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var owner = await users.FindByEmailAsync(ConfiguredEmail).ConfigureAwait(false);
            Assert.IsNotNull(owner);
            var changed = await users.ChangePasswordAsync(owner, currentPassword, replacementPassword).ConfigureAwait(false);
            Assert.IsTrue(changed.Succeeded, string.Join(", ", changed.Errors.Select(static error => error.Description)));
            owner.PasswordChangeRequired = false;
            var updated = await users.UpdateAsync(owner).ConfigureAwait(false);
            Assert.IsTrue(updated.Succeeded, string.Join(", ", updated.Errors.Select(static error => error.Description)));
        }

        public async Task<string> GetBootstrapStateAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<OwnerBootstrapStateReader>()
                .GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _serviceProvider.Dispose();
            _connection.Dispose();
        }
    }
}
