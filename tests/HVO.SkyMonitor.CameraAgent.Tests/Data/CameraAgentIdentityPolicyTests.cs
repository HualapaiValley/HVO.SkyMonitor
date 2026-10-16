using HVO.SkyMonitor.CameraAgent.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Data;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentIdentityPolicyTests
{
    private const string OwnerEmail = "owner@cameraagent.test";
    private const string OwnerPassword = "current-password";

    [TestMethod]
    public void Configure_RequiresEightCharactersWithoutCharacterClassesAndLocksOutAfterFiveFailures()
    {
        var options = new IdentityOptions();

        CameraAgentIdentityPolicy.Configure(options);

        Assert.AreEqual(8, options.Password.RequiredLength);
        Assert.AreEqual(1, options.Password.RequiredUniqueChars);
        Assert.IsFalse(options.Password.RequireDigit);
        Assert.IsFalse(options.Password.RequireLowercase);
        Assert.IsFalse(options.Password.RequireUppercase);
        Assert.IsFalse(options.Password.RequireNonAlphanumeric);
        Assert.IsTrue(options.Lockout.AllowedForNewUsers);
        Assert.AreEqual(5, options.Lockout.MaxFailedAccessAttempts);
        Assert.AreEqual(TimeSpan.FromMinutes(5), options.Lockout.DefaultLockoutTimeSpan);
    }

    [TestMethod]
    [DataRow("abcdefgh")]
    [DataRow("aaaaaaaa")]
    [DataRow("Admin1234")]
    public async Task PasswordPolicy_AcceptsEightOrMoreCharactersWithoutCharacterClasses(string password)
    {
        using var fixture = await PolicyFixture.CreateAsync().ConfigureAwait(false);

        var result = await fixture.CreateUserAsync("second-account", "second@cameraagent.test", password).ConfigureAwait(false);

        Assert.IsTrue(result.Succeeded, string.Join(", ", result.Errors.Select(static error => error.Description)));
    }

    [TestMethod]
    [DataRow("abcdefg", "PasswordTooShort")]
    [DataRow("skymonitor", CameraAgentPasswordValidator.DefaultPasswordCode)]
    [DataRow("SkyMonitor", CameraAgentPasswordValidator.DefaultPasswordCode)]
    [DataRow("SKYMONITOR", CameraAgentPasswordValidator.DefaultPasswordCode)]
    [DataRow("Camera-Owner", CameraAgentPasswordValidator.UserNamePasswordCode)]
    public async Task PasswordPolicy_RefusesShortDefaultAndUserNamePasswords(string password, string expectedCode)
    {
        using var fixture = await PolicyFixture.CreateAsync().ConfigureAwait(false);

        var result = await fixture.CreateUserAsync("camera-owner", "second@cameraagent.test", password)
            .ConfigureAwait(false);

        Assert.IsFalse(result.Succeeded);
        CollectionAssert.Contains(result.Errors.Select(static error => error.Code).ToArray(), expectedCode);
    }

    [TestMethod]
    public async Task PasswordPolicy_RefusesTheCurrentPasswordOnEveryReplacementPath()
    {
        using var fixture = await PolicyFixture.CreateAsync().ConfigureAwait(false);
        var users = fixture.Users;
        var owner = await users.FindByEmailAsync(OwnerEmail).ConfigureAwait(false);
        Assert.IsNotNull(owner);

        var changed = await users.ChangePasswordAsync(owner, OwnerPassword, OwnerPassword).ConfigureAwait(false);
        var token = await users.GeneratePasswordResetTokenAsync(owner).ConfigureAwait(false);
        var reset = await users.ResetPasswordAsync(owner, token, OwnerPassword).ConfigureAwait(false);

        Assert.AreEqual(
            CameraAgentPasswordValidator.CurrentPasswordCode,
            changed.Errors.Single().Code);
        Assert.AreEqual(
            CameraAgentPasswordValidator.CurrentPasswordCode,
            reset.Errors.Single().Code);
        Assert.IsTrue(await users.CheckPasswordAsync(owner, OwnerPassword).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow("admin")]
    [DataRow(" ADMIN ")]
    [DataRow(OwnerEmail)]
    [DataRow("OWNER@CAMERAAGENT.TEST")]
    public async Task FindSignInUser_ResolvesTheUserNameOrTheEmail(string value)
    {
        using var fixture = await PolicyFixture.CreateAsync().ConfigureAwait(false);

        var user = await CameraAgentIdentityPolicy.FindSignInUserAsync(fixture.Users, value).ConfigureAwait(false);

        Assert.IsNotNull(user);
        Assert.AreEqual("admin", user.UserName);
    }

    [TestMethod]
    [DataRow("admin@cameraagent.test")]
    [DataRow("owner")]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow((string?)null)]
    public async Task FindSignInUser_ResolvesNothingForAnyOtherValue(string? value)
    {
        using var fixture = await PolicyFixture.CreateAsync().ConfigureAwait(false);

        var user = await CameraAgentIdentityPolicy.FindSignInUserAsync(fixture.Users, value).ConfigureAwait(false);

        // A value containing "@" is only ever an email, so it never matches a user name.
        Assert.IsNull(user);
    }

    [TestMethod]
    public async Task FindSignInUser_ResolvesAnOwnerWhoseUserNameIsItsEmail()
    {
        using var fixture = await PolicyFixture.CreateAsync(ownerUserName: OwnerEmail).ConfigureAwait(false);

        var user = await CameraAgentIdentityPolicy.FindSignInUserAsync(fixture.Users, OwnerEmail).ConfigureAwait(false);

        Assert.IsNotNull(user);
        Assert.AreEqual(OwnerEmail, user.UserName);
    }

    [TestMethod]
    public async Task SignIn_LocksTheAccountForFiveMinutesAfterFiveFailures()
    {
        using var fixture = await PolicyFixture.CreateAsync().ConfigureAwait(false);
        var signIn = fixture.SignInManager;
        var owner = await fixture.Users.FindByNameAsync("admin").ConfigureAwait(false);
        Assert.IsNotNull(owner);

        for (var attempt = 1; attempt < 5; attempt++)
        {
            var failed = await signIn.CheckPasswordSignInAsync(owner, "wrong-password", lockoutOnFailure: true)
                .ConfigureAwait(false);
            Assert.IsFalse(failed.Succeeded);
            Assert.IsFalse(failed.IsLockedOut, $"Attempt {attempt} locked the account early.");
        }

        var before = DateTimeOffset.UtcNow;
        var fifth = await signIn.CheckPasswordSignInAsync(owner, "wrong-password", lockoutOnFailure: true)
            .ConfigureAwait(false);
        var correctWhileLocked = await signIn.CheckPasswordSignInAsync(owner, OwnerPassword, lockoutOnFailure: true)
            .ConfigureAwait(false);

        Assert.IsTrue(fifth.IsLockedOut);
        Assert.IsTrue(correctWhileLocked.IsLockedOut);
        Assert.IsFalse(correctWhileLocked.Succeeded);
        var lockoutEnd = await fixture.Users.GetLockoutEndDateAsync(owner).ConfigureAwait(false);
        Assert.IsNotNull(lockoutEnd);
        Assert.IsTrue(lockoutEnd.Value >= before.AddMinutes(5).AddSeconds(-1), lockoutEnd.Value.ToString("O"));
        Assert.IsTrue(lockoutEnd.Value <= DateTimeOffset.UtcNow.AddMinutes(5).AddSeconds(1), lockoutEnd.Value.ToString("O"));
    }

    [TestMethod]
    public async Task SignIn_SuccessBeforeTheLimitClearsTheFailureCount()
    {
        using var fixture = await PolicyFixture.CreateAsync().ConfigureAwait(false);
        var signIn = fixture.SignInManager;
        var owner = await fixture.Users.FindByNameAsync("admin").ConfigureAwait(false);
        Assert.IsNotNull(owner);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            _ = await signIn.CheckPasswordSignInAsync(owner, "wrong-password", lockoutOnFailure: true)
                .ConfigureAwait(false);
        }
        var succeeded = await signIn.CheckPasswordSignInAsync(owner, OwnerPassword, lockoutOnFailure: true)
            .ConfigureAwait(false);
        var failedAgain = await signIn.CheckPasswordSignInAsync(owner, "wrong-password", lockoutOnFailure: true)
            .ConfigureAwait(false);

        Assert.IsTrue(succeeded.Succeeded);
        Assert.IsFalse(failedAgain.IsLockedOut);
        Assert.AreEqual(1, await fixture.Users.GetAccessFailedCountAsync(owner).ConfigureAwait(false));
    }

    private sealed class PolicyFixture : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _services;
        private readonly IServiceScope _scope;

        private PolicyFixture(SqliteConnection connection, ServiceProvider services, IServiceScope scope)
        {
            _connection = connection;
            _services = services;
            _scope = scope;
        }

        internal UserManager<ApplicationUser> Users => _scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        internal SignInManager<ApplicationUser> SignInManager =>
            _scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>();

        internal static async Task<PolicyFixture> CreateAsync(string ownerUserName = "admin")
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync().ConfigureAwait(false);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDataProtection();
            services.AddAuthentication();
            services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(connection));
            services.AddIdentityCore<ApplicationUser>()
                .AddEntityFrameworkStores<ApplicationDbContext>()
                .AddCameraAgentPasswordPolicy()
                .AddSignInManager()
                .AddDefaultTokenProviders();
            var provider = services.BuildServiceProvider();
            var scope = provider.CreateScope();
            var fixture = new PolicyFixture(connection, provider, scope);
            try
            {
                await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreatedAsync()
                    .ConfigureAwait(false);
                Assert.AreEqual(
                    CameraAgentIdentityPolicy.MinimumPasswordLength,
                    scope.ServiceProvider.GetRequiredService<IOptions<IdentityOptions>>().Value.Password.RequiredLength);
                var created = await fixture.CreateUserAsync(ownerUserName, OwnerEmail, OwnerPassword).ConfigureAwait(false);
                Assert.IsTrue(created.Succeeded, string.Join(", ", created.Errors.Select(static error => error.Description)));
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        internal Task<IdentityResult> CreateUserAsync(string userName, string email, string password)
            => Users.CreateAsync(
                new ApplicationUser
                {
                    UserName = userName,
                    Email = email,
                    EmailConfirmed = true,
                    IsSiteOwner = string.Equals(email, OwnerEmail, StringComparison.Ordinal)
                },
                password);

        public void Dispose()
        {
            _scope.Dispose();
            _services.Dispose();
            _connection.Dispose();
        }
    }
}
