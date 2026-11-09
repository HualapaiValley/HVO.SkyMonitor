using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Authentication;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Automation;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Endpoints;

/// <summary>Real cookie/key authentication, owner authorization, antiforgery middleware and durable SQLite commands.</summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class CameraAgentAutomationAntiforgeryIntegrationTests
{
    [TestMethod]
    [DataRow(false, "missing")]
    [DataRow(false, "invalid")]
    [DataRow(false, "valid")]
    [DataRow(true, "missing")]
    [DataRow(true, "invalid")]
    [DataRow(true, "valid")]
    public async Task SessionTokensAreEnforcedBeforeQueueOrStop(bool stop, string tokenKind)
    {
        var fixture = await RoutedFixture.CreateAsync(stop).ConfigureAwait(false);
        await using var fixtureLease = fixture.ConfigureAwait(false);
        using var client = fixture.Client();
        using var signIn = await client.PostAsync(new Uri("/test/sign-in", UriKind.Relative), null).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, signIn.StatusCode);
        var token = await client.GetStringAsync(new Uri("/test/token", UriKind.Relative)).ConfigureAwait(false);
        if (tokenKind != "missing") client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", tokenKind == "valid" ? token : "invalid-token");
        await fixture.AssertCommandAsync(client, stop, tokenKind == "valid" ? HttpStatusCode.OK : HttpStatusCode.BadRequest).ConfigureAwait(false);
        Assert.AreEqual(tokenKind == "valid", fixture.Validation.IsValid, "The real antiforgery middleware must validate the actual token, with a successful positive control.");
    }

    [TestMethod]
    [DataRow(false, ApiKeyAccessLevel.Read)]
    [DataRow(false, ApiKeyAccessLevel.ReadWrite)]
    [DataRow(true, ApiKeyAccessLevel.Read)]
    [DataRow(true, ApiKeyAccessLevel.ReadWrite)]
    public async Task OwnerReadWriteKeysNeedNoSessionTokenAndReadKeysCannotMutate(bool stop, ApiKeyAccessLevel access)
    {
        var fixture = await RoutedFixture.CreateAsync(stop).ConfigureAwait(false);
        await using var fixtureLease = fixture.ConfigureAwait(false);
        using var scope = fixture.App.Services.CreateScope();
        var key = await scope.ServiceProvider.GetRequiredService<CameraAgentApiKeyStore>()
            .CreateAsync(fixture.OwnerId, "antiforgery regression", access, default).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentApiKeyCreateStatus.Created, key.Status);
        using var client = fixture.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Token);
        await fixture.AssertCommandAsync(client, stop, access == ApiKeyAccessLevel.ReadWrite ? HttpStatusCode.OK : HttpStatusCode.Forbidden).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(false, "anonymous")]
    [DataRow(false, "other-user")]
    [DataRow(false, "stale-owner")]
    [DataRow(true, "anonymous")]
    [DataRow(true, "other-user")]
    [DataRow(true, "stale-owner")]
    public async Task ValidTokensCannotBypassOwnerAuthorization(bool stop, string caller)
    {
        var fixture = await RoutedFixture.CreateAsync(stop).ConfigureAwait(false);
        await using var fixtureLease = fixture.ConfigureAwait(false);
        using var client = fixture.Client();
        if (caller != "anonymous")
        {
            if (caller == "other-user") client.DefaultRequestHeaders.Add("X-Test-User", RoutedFixture.OtherEmail);
            using var signIn = await client.PostAsync(new Uri("/test/sign-in", UriKind.Relative), null).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, signIn.StatusCode);
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await client.GetStringAsync(new Uri("/test/token", UriKind.Relative)).ConfigureAwait(false));
            if (caller == "stale-owner")
            {
                using var scope = fixture.App.Services.CreateScope();
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                Assert.IsTrue((await users.UpdateSecurityStampAsync((await users.FindByIdAsync(fixture.OwnerId).ConfigureAwait(false))!).ConfigureAwait(false)).Succeeded);
            }
        }
        await fixture.AssertCommandAsync(client, stop, caller is "anonymous" or "stale-owner" ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden).ConfigureAwait(false);
    }

    private sealed class ValidationObservation
    {
        internal bool? IsValid { get; set; }
    }

    private sealed class RoutedFixture(WebApplication app, ScriptControlsFixture scripts, string root, string ownerId,
        ValidationObservation validation, TaskCompletionSource cancelled) : IAsyncDisposable
    {
        private const string OwnerEmail = "owner@script-controls.test";
        internal const string OtherEmail = "other@script-controls.test";
        internal WebApplication App => app;
        internal string OwnerId => ownerId;
        internal ValidationObservation Validation => validation;

        internal static async Task<RoutedFixture> CreateAsync(bool running)
        {
            var scripts = new ScriptControlsFixture();
            var entered = ScriptControlsFixture.Signal();
            var cancelled = ScriptControlsFixture.Signal();
            scripts.Stub.Execute = async (_, token) =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { cancelled.TrySetResult(); }
                throw new LocalAutomationDrainedCancellationException(
                    new(LocalAutomationRunOutcome.Interrupted, "script.cancelled; exit=137; stdoutBytes=0; stderrBytes=0"), scripts.Clock.GetUtcNow(), token);
            };
            var root = Path.Combine(Path.GetTempPath(), "hvo-script-http", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root, Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddDbContext<ApplicationDbContext>(options =>
            {
                options.UseSqlite($"Data Source={Path.Combine(root, "identity.db")}");
                options.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
            });
            builder.Services.AddIdentityCore<ApplicationUser>().AddEntityFrameworkStores<ApplicationDbContext>()
                .AddClaimsPrincipalFactory<CanonicalLocalUserClaimsPrincipalFactory>().AddSignInManager();
            builder.Services.AddAuthentication(options => options.DefaultScheme = IdentityConstants.ApplicationScheme).AddIdentityCookies();
            builder.Services.ConfigureApplicationCookie(options => Program.ConfigureApplicationCookie(options));
            builder.Services.AddSingleton(Options.Create(new LocalIdentityOptions { AdminEmail = OwnerEmail }));
            builder.Services.AddCameraAgentAuthorization();
            builder.Services.AddScoped<CameraAgentApiKeyStore>();
            builder.Services.AddSingleton<CameraAgentOperatorTelemetry>();
            builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
            builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "keys")));
            builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
            builder.Services.AddSingleton<ILocalAutomationStore>(scripts.Store);
            builder.Services.AddSingleton(scripts.Commands);
            var app = builder.Build();
            var validation = new ValidationObservation();
            try
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseMiddleware<CameraAgentApiKeyAuthenticationMiddleware>();
                app.UseMiddleware<OwnerBootstrapGateMiddleware>();
                app.UseAuthorization();
                app.UseAntiforgery();
                app.Use(async (context, next) =>
                {
                    validation.IsValid = context.Features.Get<IAntiforgeryValidationFeature>()?.IsValid;
                    await next(context).ConfigureAwait(false);
                });
                app.MapCameraAgentAutomationOperationsEndpoints();
                app.MapGet("/test/token", (HttpContext context, IAntiforgery antiforgery) =>
                    Results.Text(antiforgery.GetAndStoreTokens(context).RequestToken!));
                app.MapPost("/test/sign-in", async (HttpRequest request, SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users) =>
                {
                    var email = request.Headers["X-Test-User"].FirstOrDefault() ?? OwnerEmail;
                    await signIn.SignInAsync((await users.FindByEmailAsync(email).ConfigureAwait(false))!, false).ConfigureAwait(false);
                    return Results.Ok();
                });
                string ownerId;
                using (var scope = app.Services.CreateScope())
                {
                    await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.MigrateAsync().ConfigureAwait(false);
                    var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                    var owner = new ApplicationUser { UserName = OwnerEmail, Email = OwnerEmail, EmailConfirmed = true, IsSiteOwner = true };
                    Assert.IsTrue((await users.CreateAsync(owner).ConfigureAwait(false)).Succeeded);
                    ownerId = owner.Id;
                    Assert.IsTrue((await users.CreateAsync(new ApplicationUser { UserName = OtherEmail, Email = OtherEmail, EmailConfirmed = true }).ConfigureAwait(false)).Succeeded);
                }
                await scripts.Store.InitializeAsync(default).ConfigureAwait(false);
                Assert.AreEqual(LocalAutomationCommandStatus.Applied, (await scripts.Store.SaveAsync(ScriptControlsFixture.Save(), default).ConfigureAwait(false)).Status);
                if (running)
                {
                    await scripts.Commands.RunNowAsync(ScriptControlsFixture.RunNow(), default).ConfigureAwait(false);
                    await scripts.Runner.SweepAsync(default).ConfigureAwait(false);
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                }
                await app.StartAsync().ConfigureAwait(false);
                return new(app, scripts, root, ownerId, validation, cancelled);
            }
            catch
            {
                await app.DisposeAsync().ConfigureAwait(false);
                scripts.Dispose();
                throw;
            }
        }

        internal HttpClient Client() => new(new HttpClientHandler { AllowAutoRedirect = false, CheckCertificateRevocationList = true })
        { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(20) };

        internal async Task AssertCommandAsync(HttpClient client, bool stop, HttpStatusCode expected)
        {
            var before = await scripts.Store.GetStateAsync(default).ConfigureAwait(false);
            var durableBefore = await ReadDurableRowsAsync().ConfigureAwait(false);
            if (stop) Assert.AreEqual(LocalAutomationRunOutcome.Running, before.Runs.Single().Outcome);
            else Assert.IsEmpty(before.Runs);
            using var request = new HttpRequestMessage(HttpMethod.Post,
                "/api/v1/operations/automations/definitions/script/" + (stop ? "script-stop" : "script-run-now"));
            request.Headers.Add("Idempotency-Key", "http-run");
            request.Content = stop ? JsonContent.Create(new { runKey = before.Runs.Single().RunKey, expectedDefinitionVersion = 1 })
                : JsonContent.Create(new { expectedVersion = 1 });
            using var response = await client.SendAsync(request).ConfigureAwait(false);
            Assert.AreEqual(expected, response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var after = await scripts.Store.GetStateAsync(default).ConfigureAwait(false);
            if (expected != HttpStatusCode.OK)
            {
                Assert.AreEqual(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after), "Rejection must not mutate any durable automation state.");
                Assert.AreEqual(durableBefore, await ReadDurableRowsAsync().ConfigureAwait(false), "Rejection must not change any durable table, including command/idempotency rows.");
                Assert.IsFalse(cancelled.Task.IsCompleted, "A rejected stop must not cancel the worker.");
            }
            else
            {
                var run = after.Runs.Single();
                Assert.AreEqual(stop ? LocalAutomationRunOutcome.Interrupted : LocalAutomationRunOutcome.Queued, run.Outcome);
                if (stop) Assert.IsTrue(cancelled.Task.IsCompleted);
                else Assert.AreEqual(ownerId, run.Preparation!.Actor);
            }
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = scripts.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT outcome FROM automation_runs;";
            Assert.AreEqual(after.Runs.SingleOrDefault()?.Outcome.ToString(), await command.ExecuteScalarAsync().ConfigureAwait(false),
                "An independent read-only SQLite connection must observe the same committed outcome.");
        }

        private async Task<string> ReadDurableRowsAsync()
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = scripts.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await connection.OpenAsync().ConfigureAwait(false);
            var tables = new List<List<object?[]>>();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT * FROM automation_definitions ORDER BY rowid;
                SELECT * FROM automation_definition_revisions ORDER BY rowid;
                SELECT * FROM automation_commands ORDER BY rowid;
                SELECT * FROM automation_runs ORDER BY rowid;
                SELECT * FROM automation_progress ORDER BY rowid;
                """;
            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            do
            {
                var rows = new List<object?[]>();
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    rows.Add(Enumerable.Range(0, reader.FieldCount).Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray());
                }
                tables.Add(rows);
            } while (await reader.NextResultAsync().ConfigureAwait(false));
            return JsonSerializer.Serialize(tables);
        }

        public async ValueTask DisposeAsync()
        {
            await scripts.Worker.StopAsync(default).ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
            scripts.Dispose();
            Directory.Delete(root, true);
        }
    }
}
