using HVO.SkyMonitor.CameraAgent.Authentication;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Authentication;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentApiKeyStoreTests
{
    [TestMethod]
    public void Token_HasTheFixedFormatAndAFreshIdentifierEachTime()
    {
        var (firstId, first) = CameraAgentApiKeyToken.Create();
        var (secondId, second) = CameraAgentApiKeyToken.Create();

        Assert.HasCount(CameraAgentApiKeyToken.Length, first);
        StringAssert.StartsWith(first, $"{CameraAgentApiKeyToken.Prefix}{firstId}_", StringComparison.Ordinal);
        Assert.IsTrue(CameraAgentApiKeyToken.TryReadId(first, out var read));
        Assert.AreEqual(firstId, read);
        Assert.AreNotEqual(firstId, secondId);
        Assert.AreNotEqual(first, second);
        Assert.AreEqual(64, CameraAgentApiKeyToken.Hash(first).Length);
        Assert.IsTrue(CameraAgentApiKeyToken.Matches(first, CameraAgentApiKeyToken.Hash(first)));
        Assert.IsFalse(CameraAgentApiKeyToken.Matches(second, CameraAgentApiKeyToken.Hash(first)));
    }

    [TestMethod]
    [DataRow("empty")]
    [DataRow("prefix-only")]
    [DataRow("with-scheme")]
    [DataRow("uppercase-id")]
    [DataRow("dash-separator")]
    [DataRow("short")]
    [DataRow("long")]
    [DataRow("padding")]
    [DataRow("plus")]
    [DataRow("other-prefix")]
    public void Token_OutsideTheIssuedFormat_IsRefusedBeforeAnyLookup(string variant)
    {
        var valid = CameraAgentApiKeyToken.Prefix + "abcdefabcdefabcd_" + new string('A', 43);
        Assert.IsTrue(CameraAgentApiKeyToken.TryReadId(valid, out _));
        var separator = CameraAgentApiKeyToken.Prefix.Length + CameraAgentApiKeyToken.IdLength;
        var token = variant switch
        {
            "empty" => string.Empty,
            "prefix-only" => CameraAgentApiKeyToken.Prefix,
            "with-scheme" => "Bearer " + valid,
            "uppercase-id" => CameraAgentApiKeyToken.Prefix + "ABCDEFABCDEFABCD_" + new string('A', 43),
            "dash-separator" => string.Concat(valid.AsSpan(0, separator), "-", valid.AsSpan(separator + 1)),
            "short" => valid[..^1],
            "long" => valid + "A",
            "padding" => valid[..^1] + "=",
            "plus" => valid[..^2] + "+A",
            _ => "hvo_sko_" + valid[CameraAgentApiKeyToken.Prefix.Length..]
        };

        Assert.IsFalse(CameraAgentApiKeyToken.TryReadId(token, out _));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("not-hex")]
    [DataRow("0123")]
    public void Matches_RefusesAStoredHashThatIsNotASha256(string stored)
        => Assert.IsFalse(CameraAgentApiKeyToken.Matches(CameraAgentApiKeyToken.Create().Token, stored));

    [TestMethod]
    public async Task Create_StoresOnlyTheHashAndReturnsTheKeyOnceAsync()
    {
        var fixture = await StoreFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureLease = fixture.ConfigureAwait(false);

        var created = await fixture.Store.CreateAsync(StoreFixture.OwnerId, "  hvo-sky laptop  ", ApiKeyAccessLevel.ReadWrite,
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CameraAgentApiKeyCreateStatus.Created, created.Status);
        var token = created.Token!;
        Assert.IsTrue(CameraAgentApiKeyToken.TryReadId(token, out var id));
        Assert.AreEqual(id, created.Key!.Id);
        Assert.AreEqual("hvo-sky laptop", created.Key.Label);
        var row = await fixture.Context.UserTokens.AsNoTracking().SingleAsync().ConfigureAwait(false);
        Assert.AreEqual(CameraAgentApiKeyStore.LoginProvider, row.LoginProvider);
        Assert.AreEqual(id, row.Name);
        Assert.AreEqual(StoreFixture.OwnerId, row.UserId);
        // Neither the key nor its secret part is at rest; only its SHA-256 is.
        Assert.DoesNotContain(token, row.Value!, StringComparison.Ordinal);
        Assert.DoesNotContain(token[(CameraAgentApiKeyToken.Prefix.Length + CameraAgentApiKeyToken.IdLength + 1)..],
            row.Value!, StringComparison.Ordinal);
        Assert.Contains(CameraAgentApiKeyToken.Hash(token), row.Value!, StringComparison.Ordinal);

        var listed = await fixture.Store.ListAsync(StoreFixture.OwnerId, CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(1, listed);
        Assert.AreEqual(new CameraAgentApiKeyInfo(id, "hvo-sky laptop", ApiKeyAccessLevel.ReadWrite, created.Key.CreatedUtc),
            listed[0]);
        var match = await fixture.Store.FindAsync(token, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(new CameraAgentApiKeyMatch(StoreFixture.OwnerId, id, ApiKeyAccessLevel.ReadWrite), match);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("tab\tname")]
    public async Task Create_WithAnUnusableName_RecordsNothingAsync(string? label)
    {
        var fixture = await StoreFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureLease = fixture.ConfigureAwait(false);

        var result = await fixture.Store.CreateAsync(StoreFixture.OwnerId, label, ApiKeyAccessLevel.Read,
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CameraAgentApiKeyCreateStatus.InvalidLabel, result.Status);
        Assert.IsNull(result.Token);
        Assert.AreEqual(0, await fixture.Context.UserTokens.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task Create_RefusesALongNameAnUndefinedAccessAndMoreThanTheLimitAsync()
    {
        var fixture = await StoreFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureLease = fixture.ConfigureAwait(false);

        var tooLong = await fixture.Store.CreateAsync(StoreFixture.OwnerId,
            new string('k', CameraAgentApiKeyStore.MaximumLabelLength + 1), ApiKeyAccessLevel.Read,
            CancellationToken.None).ConfigureAwait(false);
        var undefined = await fixture.Store.CreateAsync(StoreFixture.OwnerId, "scripts", (ApiKeyAccessLevel)99,
            CancellationToken.None).ConfigureAwait(false);
        for (var index = 0; index < CameraAgentApiKeyStore.MaximumKeysPerOwner; index++)
        {
            Assert.AreEqual(CameraAgentApiKeyCreateStatus.Created, (await fixture.Store.CreateAsync(StoreFixture.OwnerId,
                $"key {index}", ApiKeyAccessLevel.Read, CancellationToken.None).ConfigureAwait(false)).Status);
        }
        var overLimit = await fixture.Store.CreateAsync(StoreFixture.OwnerId, "one more", ApiKeyAccessLevel.Read,
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CameraAgentApiKeyCreateStatus.InvalidLabel, tooLong.Status);
        Assert.AreEqual(CameraAgentApiKeyCreateStatus.InvalidAccess, undefined.Status);
        Assert.AreEqual(CameraAgentApiKeyCreateStatus.LimitReached, overLimit.Status);
        Assert.AreEqual(CameraAgentApiKeyStore.MaximumKeysPerOwner,
            await fixture.Context.UserTokens.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task Find_RefusesAWrongSecretATamperedIdentifierAndARevokedKeyAsync()
    {
        var fixture = await StoreFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureLease = fixture.ConfigureAwait(false);
        var created = await fixture.Store.CreateAsync(StoreFixture.OwnerId, "automation", ApiKeyAccessLevel.Read,
            CancellationToken.None).ConfigureAwait(false);
        var token = created.Token!;
        var other = CameraAgentApiKeyToken.Create().Token;
        var wrongSecret = string.Concat(token.AsSpan(0, CameraAgentApiKeyToken.Prefix.Length + CameraAgentApiKeyToken.IdLength + 1),
            other.AsSpan(CameraAgentApiKeyToken.Prefix.Length + CameraAgentApiKeyToken.IdLength + 1));
        var wrongId = string.Concat(other.AsSpan(0, CameraAgentApiKeyToken.Prefix.Length + CameraAgentApiKeyToken.IdLength),
            token.AsSpan(CameraAgentApiKeyToken.Prefix.Length + CameraAgentApiKeyToken.IdLength));

        Assert.IsNull(await fixture.Store.FindAsync(wrongSecret, CancellationToken.None).ConfigureAwait(false));
        Assert.IsNull(await fixture.Store.FindAsync(wrongId, CancellationToken.None).ConfigureAwait(false));
        Assert.IsNotNull(await fixture.Store.FindAsync(token, CancellationToken.None).ConfigureAwait(false));

        Assert.IsTrue(await fixture.Store.RevokeAsync(StoreFixture.OwnerId, created.Key!.Id, CancellationToken.None)
            .ConfigureAwait(false));

        Assert.IsNull(await fixture.Store.FindAsync(token, CancellationToken.None).ConfigureAwait(false));
        Assert.IsFalse(await fixture.Store.RevokeAsync(StoreFixture.OwnerId, created.Key.Id, CancellationToken.None)
            .ConfigureAwait(false));
        Assert.IsEmpty(await fixture.Store.ListAsync(StoreFixture.OwnerId, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task Revoke_OnlyEverRemovesTheCallersOwnKeyAsync()
    {
        var fixture = await StoreFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureLease = fixture.ConfigureAwait(false);
        var created = await fixture.Store.CreateAsync(StoreFixture.OwnerId, "owner key", ApiKeyAccessLevel.Read,
            CancellationToken.None).ConfigureAwait(false);

        Assert.IsFalse(await fixture.Store.RevokeAsync(StoreFixture.OtherUserId, created.Key!.Id, CancellationToken.None)
            .ConfigureAwait(false));

        Assert.IsNotNull(await fixture.Store.FindAsync(created.Token!, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task RemoveAll_DeletesEveryKeyOfOneAccountAndNoOtherTokenAsync()
    {
        var fixture = await StoreFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureLease = fixture.ConfigureAwait(false);
        var first = await fixture.Store.CreateAsync(StoreFixture.OwnerId, "first", ApiKeyAccessLevel.Read,
            CancellationToken.None).ConfigureAwait(false);
        _ = await fixture.Store.CreateAsync(StoreFixture.OwnerId, "second", ApiKeyAccessLevel.ReadWrite,
            CancellationToken.None).ConfigureAwait(false);
        var others = await fixture.Store.CreateAsync(StoreFixture.OtherUserId, "other account", ApiKeyAccessLevel.Read,
            CancellationToken.None).ConfigureAwait(false);
        fixture.Context.UserTokens.Add(new IdentityUserToken<string>
        {
            UserId = StoreFixture.OwnerId,
            LoginProvider = "HVO.SkyMonitor.OwnerRecovery",
            Name = "LastCompletedOperation",
            Value = Guid.NewGuid().ToString("D")
        });
        await fixture.Context.SaveChangesAsync().ConfigureAwait(false);

        var removed = await CameraAgentApiKeyStore.RemoveAllAsync(fixture.Context, StoreFixture.OwnerId, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(2, removed);
        Assert.IsNull(await fixture.Store.FindAsync(first.Token!, CancellationToken.None).ConfigureAwait(false));
        Assert.IsNotNull(await fixture.Store.FindAsync(others.Token!, CancellationToken.None).ConfigureAwait(false));
        Assert.IsTrue(await fixture.Context.UserTokens.AnyAsync(token =>
            token.LoginProvider == "HVO.SkyMonitor.OwnerRecovery").ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow("not json")]
    [DataRow("""{"schema":2,"sha256":"00","access":"Read","label":"x","createdUtc":"2026-10-16T00:00:00Z"}""")]
    [DataRow("""{"schema":1,"sha256":"00","access":"Admin","label":"x","createdUtc":"2026-10-16T00:00:00Z"}""")]
    public async Task UnreadableRecord_IsListedForRevocationAndNeverAuthenticatesAsync(string value)
    {
        var fixture = await StoreFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureLease = fixture.ConfigureAwait(false);
        var (id, token) = CameraAgentApiKeyToken.Create();
        fixture.Context.UserTokens.Add(new IdentityUserToken<string>
        {
            UserId = StoreFixture.OwnerId,
            LoginProvider = CameraAgentApiKeyStore.LoginProvider,
            Name = id,
            Value = value
        });
        await fixture.Context.SaveChangesAsync().ConfigureAwait(false);

        var listed = await fixture.Store.ListAsync(StoreFixture.OwnerId, CancellationToken.None).ConfigureAwait(false);

        Assert.IsFalse(listed.Single().Readable);
        Assert.IsNull(await fixture.Store.FindAsync(token, CancellationToken.None).ConfigureAwait(false));
        Assert.IsTrue(await fixture.Store.RevokeAsync(StoreFixture.OwnerId, id, CancellationToken.None).ConfigureAwait(false));
    }

    /// <summary>A real identity database in memory with two accounts.</summary>
    private sealed class StoreFixture : IAsyncDisposable
    {
        internal const string OwnerId = "owner-account";
        internal const string OtherUserId = "other-account";

        private readonly SqliteConnection _connection;

        private StoreFixture(SqliteConnection connection, ApplicationDbContext context)
        {
            _connection = connection;
            Context = context;
            Store = new CameraAgentApiKeyStore(context, TimeProvider.System, NullLogger<CameraAgentApiKeyStore>.Instance);
        }

        internal ApplicationDbContext Context { get; }

        internal CameraAgentApiKeyStore Store { get; }

        internal static async Task<StoreFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync().ConfigureAwait(false);
            var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .Options);
            await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
            context.Users.AddRange(
                new ApplicationUser { Id = OwnerId, UserName = "owner", IsSiteOwner = true },
                new ApplicationUser { Id = OtherUserId, UserName = "other" });
            await context.SaveChangesAsync().ConfigureAwait(false);
            return new StoreFixture(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync().ConfigureAwait(false);
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
