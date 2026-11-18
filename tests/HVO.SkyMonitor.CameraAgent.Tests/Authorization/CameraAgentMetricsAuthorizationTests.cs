using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using HVO.SkyMonitor.CameraAgent.Authentication;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Common.Observability;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace HVO.SkyMonitor.CameraAgent.Tests.Authorization;

/// <summary>
/// /metrics uses the operations-read policy on the same middleware path as the host, including the browser
/// redirect gate. /health and /alive stay anonymous. A credential in the URL is never accepted or echoed.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentMetricsAuthorizationTests
{
    private const string MetricName = "hvo_cameraagent_metrics_auth_probe";

    private static readonly Meter ProbeMeter = new("HVO.SkyMonitor.CameraAgent.Tests.MetricsAuthorization");
    private static readonly Counter<long> Probe = ProbeMeter.CreateCounter<long>(MetricName);

    [TestMethod]
    [DataRow("Production")]
    [DataRow("Development")]
    public async Task AnonymousScrapeIsRefused_HealthAndLivenessStayOpen(string environment)
    {
        var fixture = await MetricsFixture.CreateAsync(environment).ConfigureAwait(false);
        await using var fixtureLease = fixture.ConfigureAwait(false);
        var key = await fixture.CreateKeyAsync(ApiKeyAccessLevel.Read).ConfigureAwait(false);
        var secret = SecretOf(key);
        using var anonymous = fixture.Client();

        using var metrics = await anonymous.GetAsync(new Uri("/metrics?access_token=" + Uri.EscapeDataString(key), UriKind.Relative))
            .ConfigureAwait(false);
        using var upper = await anonymous.GetAsync(new Uri("/Metrics", UriKind.Relative)).ConfigureAwait(false);
        using var child = await anonymous.GetAsync(new Uri("/metrics/runtime", UriKind.Relative)).ConfigureAwait(false);
        using var health = await anonymous.GetAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);
        using var alive = await anonymous.GetAsync(new Uri("/alive", UriKind.Relative)).ConfigureAwait(false);
        using var healthWithKey = await fixture.Client(key).GetAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);
        using var aliveWithKey = await fixture.Client(key).GetAsync(new Uri("/alive", UriKind.Relative)).ConfigureAwait(false);
        using var loginPage = await anonymous.GetAsync(new Uri("/operations", UriKind.Relative)).ConfigureAwait(false);

        await AssertDeniedAsync(environment, metrics, HttpStatusCode.Unauthorized, secret).ConfigureAwait(false);
        Assert.AreEqual("Bearer", metrics.Headers.WwwAuthenticate.Single().ToString());
        Assert.IsTrue(metrics.Headers.CacheControl!.NoStore);
        await AssertDeniedAsync(environment, upper, HttpStatusCode.Unauthorized, secret).ConfigureAwait(false);
        Assert.AreNotEqual(HttpStatusCode.OK, child.StatusCode, environment);
        Assert.IsFalse((await child.Content.ReadAsStringAsync().ConfigureAwait(false)).Contains(MetricName, StringComparison.Ordinal), environment);
        AssertOpen(environment, health);
        AssertOpen(environment, alive);
        AssertOpen(environment, healthWithKey);
        AssertOpen(environment, aliveWithKey);
        Assert.AreEqual(HttpStatusCode.Redirect, loginPage.StatusCode, environment);
        var location = loginPage.Headers.Location?.ToString() ?? string.Empty;
        StringAssert.Contains(location, "/Account/Login", StringComparison.Ordinal);
        Assert.IsFalse(location.Contains(secret, StringComparison.Ordinal), environment);
        Assert.IsFalse(fixture.Logs.Any(entry => entry.Contains(secret, StringComparison.Ordinal) || entry.Contains(key, StringComparison.Ordinal)), environment);
    }

    [TestMethod]
    [DataRow("Production")]
    [DataRow("Development")]
    public async Task OperationsReadSessionAndApiKeyCanScrape_OtherCallersCannot(string environment)
    {
        var fixture = await MetricsFixture.CreateAsync(environment).ConfigureAwait(false);
        await using var fixtureLease = fixture.ConfigureAwait(false);
        var readKey = await fixture.CreateKeyAsync(ApiKeyAccessLevel.Read).ConfigureAwait(false);
        var readWriteKey = await fixture.CreateKeyAsync(ApiKeyAccessLevel.ReadWrite).ConfigureAwait(false);
        var readSecret = SecretOf(readKey);
        using var session = await fixture.SignedInClientAsync().ConfigureAwait(false);
        using var outsider = await fixture.SignedInClientAsync(MetricsFixture.OutsiderEmail).ConfigureAwait(false);
        using var read = fixture.Client(readKey);
        using var readWrite = fixture.Client(readWriteKey);
        using var accountWithKey = await read.GetAsync(new Uri("/account/probe", UriKind.Relative)).ConfigureAwait(false);

        using var sessionMetrics = await session.GetAsync(new Uri("/metrics", UriKind.Relative)).ConfigureAwait(false);
        using var readMetrics = await read.GetAsync(new Uri("/metrics", UriKind.Relative)).ConfigureAwait(false);
        using var readWriteMetrics = await readWrite.GetAsync(new Uri("/metrics", UriKind.Relative)).ConfigureAwait(false);
        using var outsiderMetrics = await outsider.GetAsync(new Uri("/metrics", UriKind.Relative)).ConfigureAwait(false);
        using (var pendingSession = await fixture.SignedInClientAsync().ConfigureAwait(false))
        {
            await fixture.UpdateOwnerAsync(owner => owner.PasswordChangeRequired = true).ConfigureAwait(false);
            using var pendingMetrics = await pendingSession.GetAsync(new Uri("/metrics", UriKind.Relative)).ConfigureAwait(false);
            using var pendingKey = await read.GetAsync(new Uri("/metrics", UriKind.Relative)).ConfigureAwait(false);
            await AssertDeniedAsync(environment, pendingMetrics, HttpStatusCode.Forbidden, readSecret).ConfigureAwait(false);
            await AssertDeniedAsync(environment, pendingKey, HttpStatusCode.Forbidden, readSecret).ConfigureAwait(false);
            await fixture.UpdateOwnerAsync(owner => owner.PasswordChangeRequired = false).ConfigureAwait(false);
        }

        using var restored = await read.GetAsync(new Uri("/metrics", UriKind.Relative)).ConfigureAwait(false);

        await AssertScrapedAsync(environment, sessionMetrics, readSecret).ConfigureAwait(false);
        await AssertScrapedAsync(environment, readMetrics, readSecret).ConfigureAwait(false);
        await AssertScrapedAsync(environment, readWriteMetrics, SecretOf(readWriteKey)).ConfigureAwait(false);
        await AssertDeniedAsync(environment, outsiderMetrics, HttpStatusCode.Forbidden, readSecret).ConfigureAwait(false);
        Assert.AreNotEqual(HttpStatusCode.OK, accountWithKey.StatusCode, environment);
        Assert.IsFalse((await accountWithKey.Content.ReadAsStringAsync().ConfigureAwait(false)).Contains("account-page", StringComparison.Ordinal), environment);
        await AssertScrapedAsync(environment, restored, readSecret).ConfigureAwait(false);
        Assert.IsFalse(fixture.Logs.Any(entry => entry.Contains(readSecret, StringComparison.Ordinal) || entry.Contains(readKey, StringComparison.Ordinal)), environment);
    }

    private static void AssertOpen(string environment, HttpResponseMessage response)
    {
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, environment);
        Assert.IsNull(response.Headers.Location, environment);
    }

    private static async Task AssertScrapedAsync(string environment, HttpResponseMessage response, string secret)
    {
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, environment);
        Assert.IsTrue(response.Headers.CacheControl!.NoStore, environment);
        Assert.IsNull(response.Headers.Location, environment);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        StringAssert.Contains(body, MetricName, StringComparison.Ordinal);
        Assert.IsFalse(body.Contains(secret, StringComparison.Ordinal), environment);
        Assert.IsFalse(body.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase), environment);
    }

    private static async Task AssertDeniedAsync(string environment, HttpResponseMessage response, HttpStatusCode status, string secret)
    {
        Assert.AreEqual(status, response.StatusCode, environment);
        Assert.IsNull(response.Headers.Location, environment);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.IsFalse(body.Contains(MetricName, StringComparison.Ordinal), environment);
        Assert.IsFalse(body.Contains(secret, StringComparison.Ordinal), environment);
        var headers = string.Join('\n', response.Headers.Select(header => header.Key + "=" + string.Join(",", header.Value)));
        Assert.IsFalse(headers.Contains(secret, StringComparison.Ordinal), environment);
    }

    private static string SecretOf(string token)
        => token[(CameraAgentApiKeyToken.Prefix.Length + CameraAgentApiKeyToken.IdLength + 1)..];

    private sealed class MetricsFixture : IAsyncDisposable
    {
        internal const string OutsiderEmail = "outsider@metrics.test";
        private const string OwnerEmail = "owner@metrics.test";

        private readonly WebApplication _app;
        private readonly string _root;
        private readonly string _ownerId;

        private MetricsFixture(WebApplication app, string root, string ownerId, ConcurrentQueue<string> logs)
        {
            _app = app;
            _root = root;
            _ownerId = ownerId;
            Logs = logs;
        }

        internal IReadOnlyCollection<string> Logs { get; }

        internal static async Task<MetricsFixture> CreateAsync(string environmentName)
        {
            var root = Path.Combine(Path.GetTempPath(), "hvo-metrics-auth", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var logs = new ConcurrentQueue<string>();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = environmentName,
                ApplicationName = "HVO.SkyMonitor.CameraAgent.Tests",
                ContentRootPath = root,
                Args = []
            });
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Debug);
            builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
            builder.Logging.AddFilter("Microsoft.Hosting", LogLevel.Warning);
            builder.Logging.AddProvider(new CapturingLoggerProvider(logs));
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddDbContext<ApplicationDbContext>(options =>
            {
                options.UseSqlite($"Data Source={Path.Combine(root, "identity.db")}");
                options.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
            });
            builder.Services.AddIdentityCore<ApplicationUser>()
                .AddEntityFrameworkStores<ApplicationDbContext>()
                .AddClaimsPrincipalFactory<CanonicalLocalUserClaimsPrincipalFactory>()
                .AddSignInManager();
            builder.Services.AddAuthentication(options => options.DefaultScheme = IdentityConstants.ApplicationScheme)
                .AddIdentityCookies();
            builder.Services.ConfigureApplicationCookie(options => Program.ConfigureApplicationCookie(options));
            builder.Services.AddSingleton(Options.Create(new LocalIdentityOptions { AdminEmail = OwnerEmail }));
            builder.Services.AddCameraAgentAuthorization();
            builder.Services.AddScoped<CameraAgentApiKeyStore>();
            builder.Services.AddSingleton<CameraAgentOperatorTelemetry>();
            builder.Services.AddAntiforgery();
            builder.Services.AddSkyMonitorHealthChecks();
            builder.Services.AddOpenTelemetry().WithMetrics(metrics =>
            {
                metrics.AddPrometheusExporter();
                metrics.AddMeter(ProbeMeter.Name);
            });
            var app = builder.Build();
            try
            {
                Program.UseBrowserAuthenticationRedirects(app);
                app.UseRouting();
                app.UseAuthentication();
                app.UseMiddleware<CameraAgentApiKeyAuthenticationMiddleware>();
                app.UseMiddleware<OwnerBootstrapGateMiddleware>();
                app.UseAuthorization();
                app.UseAntiforgery();
                CameraAgentMetricsEndpoint.Map(app);
                app.MapSkyMonitorHealthEndpoints();
                app.MapGet("/operations", () => Results.Text("operations-page"))
                    .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1);
                app.MapGet("/account/probe", () => Results.Text("account-page"))
                    .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1);
                app.MapPost("/test/sign-in", async (
                        HttpRequest request,
                        SignInManager<ApplicationUser> signIn,
                        UserManager<ApplicationUser> users) =>
                    {
                        var email = request.Headers["X-Test-User"].ToString();
                        if (string.IsNullOrEmpty(email))
                        {
                            email = OwnerEmail;
                        }

                        var user = await users.FindByEmailAsync(email).ConfigureAwait(false);
                        if (user is null)
                        {
                            return Results.NotFound();
                        }

                        await signIn.SignInAsync(user, isPersistent: false).ConfigureAwait(false);
                        return Results.Ok();
                    })
                    .AllowAnonymous();

                string ownerId;
                var scope = app.Services.CreateAsyncScope();
                await using (scope.ConfigureAwait(false))
                {
                    await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.MigrateAsync()
                        .ConfigureAwait(false);
                    var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                    var owner = new ApplicationUser
                    {
                        UserName = OwnerEmail,
                        Email = OwnerEmail,
                        EmailConfirmed = true,
                        IsSiteOwner = true
                    };
                    Assert.IsTrue((await users.CreateAsync(owner).ConfigureAwait(false)).Succeeded);
                    ownerId = owner.Id;
                    var outsider = new ApplicationUser
                    {
                        UserName = OutsiderEmail,
                        Email = OutsiderEmail,
                        EmailConfirmed = true
                    };
                    Assert.IsTrue((await users.CreateAsync(outsider).ConfigureAwait(false)).Succeeded);
                }

                await app.StartAsync().ConfigureAwait(false);
                Probe.Add(1);
                return new MetricsFixture(app, root, ownerId, logs);
            }
            catch
            {
                await app.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        internal async Task<string> CreateKeyAsync(ApiKeyAccessLevel access)
        {
            var scope = _app.Services.CreateAsyncScope();
            await using var scopeLease = scope.ConfigureAwait(false);
            var result = await scope.ServiceProvider.GetRequiredService<CameraAgentApiKeyStore>()
                .CreateAsync(_ownerId, $"{access} key", access, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentApiKeyCreateStatus.Created, result.Status);
            return result.Token!;
        }

        internal async Task UpdateOwnerAsync(Action<ApplicationUser> change)
        {
            var scope = _app.Services.CreateAsyncScope();
            await using var scopeLease = scope.ConfigureAwait(false);
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var owner = await users.FindByIdAsync(_ownerId).ConfigureAwait(false);
            change(owner!);
            Assert.IsTrue((await users.UpdateAsync(owner!).ConfigureAwait(false)).Succeeded);
        }

        internal HttpClient Client(string? bearer = null)
        {
            var client = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                CheckCertificateRevocationList = true
            })
            {
                BaseAddress = new Uri(_app.Urls.Single())
            };
            if (bearer is not null)
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            }

            return client;
        }

        internal async Task<HttpClient> SignedInClientAsync(string? email = null)
        {
            var client = Client();
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/test/sign-in", UriKind.Relative));
            if (email is not null)
            {
                request.Headers.Add("X-Test-User", email);
            }

            using var signIn = await client.SendAsync(request).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, signIn.StatusCode);
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class CapturingLoggerProvider(ConcurrentQueue<string> entries) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                entries.Enqueue(formatter(state, exception));
                if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
                {
                    entries.Enqueue(string.Join("|", pairs.Select(pair => pair.Value)));
                }
            }
        }
    }
}
