using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using HVO.Enterprise.Telemetry.OpenTelemetry;
using HVO.SkyMonitor.CameraAgent.Authentication;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Diagnostics;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Common.Observability;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
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
[TestProperty("Area", "Hosting")]
public sealed partial class CameraAgentMetricsAuthorizationTests
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

    [TestMethod]
    public async Task ProductionProfilingScrapeHasBoundedBucketsRuntimeAndNoRequestIdentifiers()
    {
        var fixture = await MetricsFixture.CreateAsync("Production", profiling: true).ConfigureAwait(false);
        await using var fixtureLease = fixture.ConfigureAwait(false);
        var readKey = await fixture.CreateKeyAsync(ApiKeyAccessLevel.Read).ConfigureAwait(false);
        using var client = fixture.Client(readKey);
        const string privatePath = "profiling-test-private-path";
        using var health = await client.GetAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);
        using var parameterized = await client.GetAsync(new Uri($"/test/route/{privatePath}", UriKind.Relative)).ConfigureAwait(false);
        using var missing = await client.GetAsync(new Uri($"/{privatePath}", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, parameterized.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);

        CameraAgentWorkloadTelemetry.Record(CameraAgentWorkloadTelemetry.Start(),
            CameraAgentWorkloadTelemetry.Operation.SqliteCheckpoint, CameraAgentWorkloadTelemetry.Outcome.Incomplete);
        CameraAgentWorkloadTelemetry.RecordSceneBytes(12345);
        CameraAgentWorkloadTelemetry.RecordMaterializationPending(1);
        CameraAgentWorkloadTelemetry.RecordMaterializationPending(-1);
        for (var index = 0; index < 100; index++)
        {
            Probe.Add(1, new KeyValuePair<string, object?>("sample", index));
        }

        using var response = await client.GetAsync(new Uri("/metrics", UriKind.Relative)).ConfigureAwait(false);
        await AssertScrapedAsync("Production", response, SecretOf(readKey)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        foreach (var expected in new[]
        {
            "camera_agent_workload_duration_seconds_bucket", "operation=\"sqlite_checkpoint\"",
            "outcome=\"incomplete\"", "le=\"0.001", "camera_agent_workload_scene_metadata_bytes_bucket",
            "dotnet_process_cpu_time_seconds_total", "dotnet_process_memory_working_set_bytes",
            "dotnet_gc_heap_total_allocated_bytes_total", "dotnet_gc_last_collection_heap_size_bytes",
            "dotnet_gc_pause_time_seconds_total", "# TYPE dotnet_thread_pool_queue_length_total counter",
            "# TYPE dotnet_thread_pool_thread_count_total counter",
            "camera_agent_ingress_accepting", "http_server_request_duration_seconds_bucket"
        })
        {
            StringAssert.Contains(body, expected, StringComparison.Ordinal);
        }
        Assert.IsFalse(body.Contains(privatePath, StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("url_path=", StringComparison.Ordinal));
        var samples = body.Split('\n');
        Assert.IsTrue(samples.Any(line => line.StartsWith("aspnetcore_routing_match_attempts_total{", StringComparison.Ordinal) &&
            HasSampleLabel(line, "aspnetcore_routing_match_status")), "The scrape must exercise the standard routing meter.");
        var routeSample = samples.FirstOrDefault(line => line.Contains("http_route=", StringComparison.Ordinal));
        Assert.IsNull(routeSample, $"The scrape exposed an HTTP route in {routeSample}");
        Assert.IsTrue(samples.Any(line => line.StartsWith("dotnet_gc_last_collection_heap_size_bytes{", StringComparison.Ordinal) &&
            HasSampleLabel(line, "gc_heap_generation")));
        var probeSamples = samples.Where(line => line.StartsWith(MetricName, StringComparison.Ordinal)).ToArray();
        Assert.AreEqual(64, probeSamples.Count(line => line.Contains("sample=", StringComparison.Ordinal)),
            string.Join('\n', probeSamples));
        Assert.IsTrue(probeSamples.Any(line => line.Contains("otel_metric_overflow", StringComparison.Ordinal)));
        AssertDashboardMatchesProfilingScrape(samples);
    }

    [TestMethod]
    public void ProductionProfilingScrapeConfigQuotesTargetsAndLoadsRecordingRules()
    {
        var directory = ObservabilityDirectory();
        var configuration = File.ReadAllText(Path.Combine(directory, "prometheus.yml"));
        StringAssert.Contains(configuration, "- targets: ['cameraagent.example.invalid:443']", StringComparison.Ordinal);
        StringAssert.Contains(configuration, "#     - targets: ['127.0.0.1:8080']", StringComparison.Ordinal);
        StringAssert.Contains(configuration, "- cameraagent.rules.yml", StringComparison.Ordinal);
        Assert.IsTrue(File.Exists(Path.Combine(directory, "cameraagent.rules.yml")));
    }

    private static void AssertDashboardMatchesProfilingScrape(string[] samples)
    {
        var directory = ObservabilityDirectory();
        using var dashboard = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "dashboard.json")));
        var rules = File.ReadAllText(Path.Combine(directory, "cameraagent.rules.yml"));

        foreach (var panel in dashboard.RootElement.GetProperty("panels").EnumerateArray())
        {
            foreach (var target in panel.GetProperty("targets").EnumerateArray())
            {
                var expression = target.GetProperty("expr").GetString()!;
                // Resolve the workload recording rule to the histogram that Prometheus scrapes.
                if (expression.StartsWith("cameraagent:workload_", StringComparison.Ordinal))
                {
                    var rule = Regex.Match(rules,
                        $@"(?m)^\s*- record: {Regex.Escape(expression)}[ \t]*\r?\n[ \t]*expr: (.+)$",
                        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                    Assert.IsTrue(rule.Success, $"Missing recording rule for {expression}");
                    expression = rule.Groups[1].Value;
                }

                foreach (Match match in ProfilingMetricSelector().Matches(expression))
                {
                    var metric = match.Groups[1].Value;
                    var metricSamples = samples.Where(line => line.StartsWith(metric + "{", StringComparison.Ordinal) ||
                        line.StartsWith(metric + " ", StringComparison.Ordinal)).ToArray();
                    Assert.IsNotEmpty(metricSamples, $"Dashboard metric {metric} is absent from the scrape.");
                    foreach (Match label in LegendLabel().Matches(target.GetProperty("legendFormat").GetString()!))
                    {
                        var name = label.Groups[1].Value;
                        // Prometheus adds instance after the scrape.
                        if (name != "instance")
                        {
                            Assert.IsTrue(metricSamples.Any(line => HasSampleLabel(line, name)),
                                $"Dashboard label {name} is absent from {metric}.");
                        }
                    }
                }
            }
        }
    }

    private static bool HasSampleLabel(string sample, string label)
        => sample.Contains("{" + label + "=", StringComparison.Ordinal) ||
           sample.Contains("," + label + "=", StringComparison.Ordinal);

    [GeneratedRegex(@"\b((?:dotnet_|camera_agent_workload_|http_server_)[a-z0-9_]+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ProfilingMetricSelector();

    [GeneratedRegex(@"\{\{([a-zA-Z_][a-zA-Z0-9_]*)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex LegendLabel();

    private static string ObservabilityDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
            {
                return Path.Combine(directory.FullName, "deploy", "observability", "cameraagent");
            }
        }

        throw new DirectoryNotFoundException($"Could not locate the SkyMonitor repository from {AppContext.BaseDirectory}.");
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

        internal static async Task<MetricsFixture> CreateAsync(string environmentName, bool profiling = false)
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
            });
            builder.Services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
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
            if (profiling)
            {
                // Register the same standard meters as AddSkyMonitorObservability without exporting off-host.
                builder.Services.AddOpenTelemetryExport(options =>
                {
                    options.EnableTraceExport = false;
                    options.EnableMetricsExport = false;
                    options.EnableLogExport = false;
                    options.EnableStandardMeters = true;
                });
            }
            builder.Services.AddOpenTelemetry().WithMetrics(metrics =>
            {
                metrics.AddPrometheusExporter();
                metrics.AddMeter(ProbeMeter.Name);
                if (profiling)
                {
                    ProductionProfiling.ConfigureMetrics(metrics);
                    metrics.AddRuntimeInstrumentation();
                    metrics.AddMeter(RawIngressTelemetry.MeterName);
                }
            });
            builder.Services.AddSingleton(new RawIngressState(TimeProvider.System));
            builder.Services.AddSingleton<RawIngressTelemetry>();
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
                app.MapGet("/test/route/{value}", () => Results.Ok()).AllowAnonymous();
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
                if (profiling)
                    _ = app.Services.GetRequiredService<RawIngressTelemetry>();
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
