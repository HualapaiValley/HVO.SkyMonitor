using HVO.SkyMonitor.CameraAgent.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Data;

[TestClass]
[TestCategory("Unit")]
public sealed class OwnerPasswordReplacementServiceTests
{
    [TestMethod]
    public async Task ReplaceAsync_ChangesPasswordClearsRequirementAndRotatesSecurityStamp()
    {
        using var fixture = await ReplacementFixture.CreateAsync().ConfigureAwait(false);
        var before = fixture.Owner.SecurityStamp;

        var result = await fixture.Service.ReplaceAsync(
            fixture.Owner,
            ReplacementFixture.TemporaryPassword,
            ReplacementFixture.NewPassword).ConfigureAwait(false);

        Assert.IsTrue(result.Succeeded);
        Assert.IsFalse(fixture.Owner.PasswordChangeRequired);
        Assert.AreNotEqual(before, fixture.Owner.SecurityStamp);
        Assert.IsFalse(await fixture.UserManager.CheckPasswordAsync(
            fixture.Owner, ReplacementFixture.TemporaryPassword).ConfigureAwait(false));
        Assert.IsTrue(await fixture.UserManager.CheckPasswordAsync(
            fixture.Owner, ReplacementFixture.NewPassword).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow("WrongCurrent!418", "ValidReplacement!418")]
    [DataRow("TemporaryOwner!418", "weak")]
    public async Task ReplaceAsync_InvalidPasswordLeavesBootstrapRequirement(
        string currentPassword,
        string newPassword)
    {
        using var fixture = await ReplacementFixture.CreateAsync().ConfigureAwait(false);

        var result = await fixture.Service.ReplaceAsync(
            fixture.Owner,
            currentPassword,
            newPassword).ConfigureAwait(false);

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(fixture.Owner.PasswordChangeRequired);
        Assert.IsTrue(await fixture.UserManager.CheckPasswordAsync(
            fixture.Owner, ReplacementFixture.TemporaryPassword).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ReplaceAsync_SamePasswordLeavesBootstrapRequirementAndCredentialUnchanged()
    {
        using var fixture = await ReplacementFixture.CreateAsync().ConfigureAwait(false);
        var securityStamp = fixture.Owner.SecurityStamp;

        var result = await fixture.Service.ReplaceAsync(
            fixture.Owner,
            ReplacementFixture.TemporaryPassword,
            ReplacementFixture.TemporaryPassword).ConfigureAwait(false);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("PasswordMustChange", result.Errors.Single().Code);
        Assert.AreEqual(
            "The new password must be different from the current password.",
            result.Errors.Single().Description);
        Assert.IsTrue(fixture.Owner.PasswordChangeRequired);
        Assert.AreEqual(securityStamp, fixture.Owner.SecurityStamp);
        Assert.IsTrue(await fixture.UserManager.CheckPasswordAsync(
            fixture.Owner, ReplacementFixture.TemporaryPassword).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ReplaceAsync_FlagPersistenceFailureRollsBackPasswordAndReloadsOwner()
    {
        using var fixture = await ReplacementFixture.CreateAsync(failPasswordChangeCompletion: true)
            .ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Service.ReplaceAsync(
            fixture.Owner,
            ReplacementFixture.TemporaryPassword,
            ReplacementFixture.NewPassword)).ConfigureAwait(false);

        Assert.IsTrue(fixture.Owner.PasswordChangeRequired);
        Assert.IsTrue(await fixture.UserManager.CheckPasswordAsync(
            fixture.Owner, ReplacementFixture.TemporaryPassword).ConfigureAwait(false));
        Assert.IsFalse(await fixture.UserManager.CheckPasswordAsync(
            fixture.Owner, ReplacementFixture.NewPassword).ConfigureAwait(false));
    }

    private sealed class ReplacementFixture : IDisposable
    {
        internal const string TemporaryPassword = "TemporaryOwner!418";
        internal const string NewPassword = "ValidReplacement!418";

        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _services;
        private readonly IServiceScope _scope;

        private ReplacementFixture(
            SqliteConnection connection,
            ServiceProvider services,
            IServiceScope scope,
            ApplicationUser owner)
        {
            _connection = connection;
            _services = services;
            _scope = scope;
            Owner = owner;
            UserManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Service = new OwnerPasswordReplacementService(
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
                UserManager);
        }

        internal ApplicationUser Owner { get; }
        internal UserManager<ApplicationUser> UserManager { get; }
        internal OwnerPasswordReplacementService Service { get; }

        internal static async Task<ReplacementFixture> CreateAsync(bool failPasswordChangeCompletion = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync().ConfigureAwait(false);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<ApplicationDbContext>(options =>
            {
                options.UseSqlite(connection);
                if (failPasswordChangeCompletion)
                {
                    options.AddInterceptors(new PasswordChangeCompletionFailureInterceptor());
                }
            });
            services.AddIdentityCore<ApplicationUser>()
                .AddEntityFrameworkStores<ApplicationDbContext>();
            var provider = services.BuildServiceProvider();
            var scope = provider.CreateScope();
            try
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await dbContext.Database.EnsureCreatedAsync().ConfigureAwait(false);
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var owner = new ApplicationUser
                {
                    UserName = "owner@cameraagent.test",
                    Email = "owner@cameraagent.test",
                    EmailConfirmed = true,
                    IsSiteOwner = true,
                    PasswordChangeRequired = true
                };
                var created = await users.CreateAsync(owner, TemporaryPassword).ConfigureAwait(false);
                Assert.IsTrue(created.Succeeded, string.Join(", ", created.Errors.Select(static error => error.Description)));
                return new ReplacementFixture(connection, provider, scope, owner);
            }
            catch
            {
                scope.Dispose();
                await provider.DisposeAsync().ConfigureAwait(false);
                connection.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _scope.Dispose();
            _services.Dispose();
            _connection.Dispose();
        }
    }

    private sealed class PasswordChangeCompletionFailureInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var failsCompletion = eventData.Context?.ChangeTracker.Entries<ApplicationUser>()
                .Any(static entry =>
                    entry.Property(user => user.PasswordChangeRequired).IsModified &&
                    !entry.Entity.PasswordChangeRequired) == true;
            return failsCompletion
                ? ValueTask.FromException<InterceptionResult<int>>(
                    new InvalidOperationException("Injected owner bootstrap completion failure."))
                : ValueTask.FromResult(result);
        }
    }
}
