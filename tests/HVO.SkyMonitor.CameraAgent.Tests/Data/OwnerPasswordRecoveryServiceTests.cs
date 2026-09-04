using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Data;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Data;

[TestClass]
[TestCategory("Unit")]
public sealed class OwnerPasswordRecoveryServiceTests
{
    [TestMethod]
    public async Task Recovery_ReplacesPasswordRequiresFinalChangeAndIsIdempotent()
    {
        using var fixture = await RecoveryFixture.CreateAsync().ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        var originalStamp = fixture.Owner.SecurityStamp;
        var challenge = await fixture.Service.CreateChallengeAsync(operationId, CancellationToken.None)
            .ConfigureAwait(false);

        var completed = await fixture.Service.CompleteAsync(
            operationId,
            challenge.Challenge!,
            RecoveryFixture.RecoveryPassword,
            CancellationToken.None).ConfigureAwait(false);
        var repeated = await fixture.Service.CompleteAsync(
            operationId,
            challenge.Challenge!,
            RecoveryFixture.RecoveryPassword,
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OwnerPasswordRecoveryOutcome.Completed, completed);
        Assert.AreEqual(OwnerPasswordRecoveryOutcome.AlreadyCompleted, repeated);
        Assert.IsTrue(fixture.Owner.PasswordChangeRequired);
        Assert.AreNotEqual(originalStamp, fixture.Owner.SecurityStamp);
        Assert.IsFalse(await fixture.UserManager.CheckPasswordAsync(
            fixture.Owner, RecoveryFixture.OriginalPassword).ConfigureAwait(false));
        Assert.IsTrue(await fixture.UserManager.CheckPasswordAsync(
            fixture.Owner, RecoveryFixture.RecoveryPassword).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task Recovery_ResumeAfterFinalPasswordReplacementReportsReadyWithoutReusingCredential()
    {
        using var fixture = await RecoveryFixture.CreateAsync().ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        var challenge = await fixture.Service.CreateChallengeAsync(operationId, CancellationToken.None)
            .ConfigureAwait(false);
        var completed = await fixture.Service.CompleteAsync(
            operationId,
            challenge.Challenge!,
            RecoveryFixture.RecoveryPassword,
            CancellationToken.None).ConfigureAwait(false);
        var replacement = await fixture.ReplacePasswordAsync("FinalOwnerPassword!515").ConfigureAwait(false);

        var resumed = await fixture.Service.CompleteAsync(
            operationId,
            "invalid-challenge",
            "NoLongerApplicable!515",
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OwnerPasswordRecoveryOutcome.Completed, completed);
        Assert.IsTrue(replacement.Succeeded);
        Assert.AreEqual(OwnerPasswordRecoveryOutcome.AlreadyCompletedPasswordReplaced, resumed);
        Assert.IsFalse(fixture.Owner.PasswordChangeRequired);
        Assert.IsTrue(await fixture.UserManager.CheckPasswordAsync(
            fixture.Owner, "FinalOwnerPassword!515").ConfigureAwait(false));
        Assert.IsFalse(await fixture.UserManager.CheckPasswordAsync(
            fixture.Owner, "NoLongerApplicable!515").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task Recovery_RejectsChallengeAfterASeparateRecoveryRotatesTheStamp()
    {
        using var fixture = await RecoveryFixture.CreateAsync().ConfigureAwait(false);
        var staleOperationId = Guid.NewGuid();
        var winningOperationId = Guid.NewGuid();
        var stale = await fixture.Service.CreateChallengeAsync(staleOperationId, CancellationToken.None)
            .ConfigureAwait(false);
        var winning = await fixture.Service.CreateChallengeAsync(winningOperationId, CancellationToken.None)
            .ConfigureAwait(false);
        var completed = await fixture.Service.CompleteAsync(
            winningOperationId,
            winning.Challenge!,
            RecoveryFixture.RecoveryPassword,
            CancellationToken.None).ConfigureAwait(false);

        var replay = await fixture.Service.CompleteAsync(
            staleOperationId,
            stale.Challenge!,
            "DifferentRecovery!515",
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OwnerPasswordRecoveryOutcome.Completed, completed);
        Assert.AreEqual(OwnerPasswordRecoveryOutcome.Conflict, replay);
        Assert.IsTrue(await fixture.UserManager.CheckPasswordAsync(
            fixture.Owner, RecoveryFixture.RecoveryPassword).ConfigureAwait(false));
        Assert.IsFalse(await fixture.UserManager.CheckPasswordAsync(
            fixture.Owner, "DifferentRecovery!515").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task Recovery_InvalidPasswordRollsBackCredentialAndBootstrapState()
    {
        using var fixture = await RecoveryFixture.CreateAsync().ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        var challenge = await fixture.Service.CreateChallengeAsync(operationId, CancellationToken.None)
            .ConfigureAwait(false);

        var outcome = await fixture.Service.CompleteAsync(
            operationId,
            challenge.Challenge!,
            "alllowercasepassword",
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OwnerPasswordRecoveryOutcome.InvalidRequest, outcome);
        Assert.IsFalse(fixture.Owner.PasswordChangeRequired);
        Assert.IsTrue(await fixture.UserManager.CheckPasswordAsync(
            fixture.Owner, RecoveryFixture.OriginalPassword).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task Recovery_RequiresRemovedBootstrapAuthorityAndExactlyOneConfiguredOwner()
    {
        using var retainedAuthority = await RecoveryFixture.CreateAsync(passwordAuthorityRemoved: false)
            .ConfigureAwait(false);
        var retainedResult = await retainedAuthority.Service.CreateChallengeAsync(
            Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);

        using var duplicateOwner = await RecoveryFixture.CreateAsync().ConfigureAwait(false);
        await duplicateOwner.AddOwnerAsync("other-owner@cameraagent.test").ConfigureAwait(false);
        var duplicateResult = await duplicateOwner.Service.CreateChallengeAsync(
            Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OwnerPasswordRecoveryOutcome.InvalidRequest, retainedResult.Outcome);
        Assert.IsNull(retainedResult.Challenge);
        Assert.AreEqual(OwnerPasswordRecoveryOutcome.Conflict, duplicateResult.Outcome);
        Assert.IsNull(duplicateResult.Challenge);
    }

    [TestMethod]
    public async Task Recovery_RejectsExpiredChallengeAndNullPassword()
    {
        using var fixture = await RecoveryFixture.CreateAsync().ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            OperationId = operationId,
            OwnerId = fixture.Owner.Id,
            SecurityStamp = fixture.Owner.SecurityStamp
        });
        var expiredChallenge = fixture.DataProtectionProvider
            .CreateProtector("HVO.SkyMonitor.CameraAgent.OwnerPasswordRecovery.v1")
            .ToTimeLimitedDataProtector()
            .Protect(payload, DateTimeOffset.UtcNow.AddSeconds(-1));

        var expired = await fixture.Service.CompleteAsync(
            operationId,
            expiredChallenge,
            RecoveryFixture.RecoveryPassword,
            CancellationToken.None).ConfigureAwait(false);
        var missingPassword = await fixture.Service.CompleteAsync(
            operationId,
            "invalid-challenge",
            null,
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OwnerPasswordRecoveryOutcome.InvalidRequest, expired);
        Assert.AreEqual(OwnerPasswordRecoveryOutcome.InvalidRequest, missingPassword);
    }

    private sealed class RecoveryFixture : IDisposable
    {
        internal const string OriginalPassword = "OriginalOwner!515";
        internal const string RecoveryPassword = "RecoveredOwner!515";
        private const string OwnerEmail = "owner@cameraagent.test";

        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _services;
        private readonly IServiceScope _scope;

        private RecoveryFixture(
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
            DataProtectionProvider = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
            Service = new OwnerPasswordRecoveryService(
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
                UserManager,
                DataProtectionProvider,
                scope.ServiceProvider.GetRequiredService<IOptions<LocalIdentityOptions>>(),
                NullLogger<OwnerPasswordRecoveryService>.Instance);
        }

        internal ApplicationUser Owner { get; }
        internal UserManager<ApplicationUser> UserManager { get; }
        internal IDataProtectionProvider DataProtectionProvider { get; }
        internal OwnerPasswordRecoveryService Service { get; }

        internal Task<IdentityResult> ReplacePasswordAsync(string newPassword)
            => new OwnerPasswordReplacementService(
                    _scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
                    UserManager)
                .ReplaceAsync(Owner, RecoveryPassword, newPassword);

        internal static async Task<RecoveryFixture> CreateAsync(bool passwordAuthorityRemoved = true)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync().ConfigureAwait(false);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(connection));
            services.AddIdentityCore<ApplicationUser>()
                .AddEntityFrameworkStores<ApplicationDbContext>()
                .AddDefaultTokenProviders();
            services.AddSingleton(Options.Create(new LocalIdentityOptions
            {
                AdminEmail = OwnerEmail,
                AdminPassword = passwordAuthorityRemoved ? string.Empty : OriginalPassword,
                AllowMissingAdminPassword = passwordAuthorityRemoved
            }));
            var provider = services.BuildServiceProvider();
            var scope = provider.CreateScope();
            try
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await dbContext.Database.EnsureCreatedAsync().ConfigureAwait(false);
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var owner = new ApplicationUser
                {
                    UserName = OwnerEmail,
                    Email = OwnerEmail,
                    EmailConfirmed = true,
                    IsSiteOwner = true
                };
                var created = await users.CreateAsync(owner, OriginalPassword).ConfigureAwait(false);
                Assert.IsTrue(created.Succeeded, string.Join(", ", created.Errors.Select(static error => error.Description)));
                return new RecoveryFixture(connection, provider, scope, owner);
            }
            catch
            {
                scope.Dispose();
                await provider.DisposeAsync().ConfigureAwait(false);
                connection.Dispose();
                throw;
            }
        }

        internal async Task AddOwnerAsync(string email)
        {
            var owner = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                IsSiteOwner = true
            };
            var result = await UserManager.CreateAsync(owner, OriginalPassword).ConfigureAwait(false);
            Assert.IsTrue(result.Succeeded);
        }

        public void Dispose()
        {
            _scope.Dispose();
            _services.Dispose();
            _connection.Dispose();
        }
    }
}
