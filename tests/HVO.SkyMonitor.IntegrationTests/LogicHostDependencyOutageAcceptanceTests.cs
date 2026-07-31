using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.IntegrationTests.Infrastructure;
using HVO.SkyMonitor.LogicHost.Models.Diagnostics;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Data.SqlClient;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class LogicHostDependencyOutageAcceptanceTests
{
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new() { WriteIndented = true };
    private static readonly DependencyScenario[] Scenarios =
    [
        new(IntegrationDependency.SqlServer, "database"),
        new(IntegrationDependency.Redis, "redis"),
        new(IntegrationDependency.Minio, "minio"),
        new(IntegrationDependency.Smtp, "smtp")
    ];

    [TestMethod]
    [Timeout(480_000)]
    public async Task Issue107_DependenciesDegradeWithoutFabricatedDataAndRecoverWithinBound()
    {
        await using var fixture = await LogicHostUiKestrelFixture.CreateAsync().ConfigureAwait(false);
        using var client = new HttpClient { BaseAddress = fixture.BaseAddress };
        var token = await HttpHelpers.GetClientCredentialsTokenAsync(
            client,
            "/connect/token",
            TestClients.SystemInternal.ClientId,
            TestClients.SystemInternal.ClientSecret,
            string.Join(' ', TestClients.SystemInternal.Scopes)).ConfigureAwait(false);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        var evidence = new List<DependencyOutageEvidence>();
        var selectedDependency = Environment.GetEnvironmentVariable("HVO_ISSUE_107_DEPENDENCY");
        var scenarios = string.IsNullOrWhiteSpace(selectedDependency)
            ? Scenarios
            : Scenarios.Where(item => item.Dependency.ToString().Equals(
                selectedDependency,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        scenarios.Should().NotBeEmpty();

        foreach (var scenario in scenarios)
        {
            var initial = await WaitForHealthStatusAsync(client, scenario.HealthCheck, "Healthy", TimeSpan.FromSeconds(30))
                .ConfigureAwait(false);
            using (var successfulOperation = await ExerciseDependencyAsync(client, scenario.Dependency).ConfigureAwait(false))
            {
                successfulOperation.IsSuccessStatusCode.Should().BeTrue(scenario.Dependency.ToString());
            }

            var container = AssemblyHooks.Fixture.GetDependencyContainer(scenario.Dependency);
            var outageStarted = Stopwatch.GetTimestamp();
            var restored = false;
            try
            {
                await DisruptAsync(container, scenario.Dependency).ConfigureAwait(false);
                if (scenario.Dependency == IntegrationDependency.SqlServer) SqlConnection.ClearAllPools();
                var unavailable = await ObserveUnavailableHealthEndpointAsync(client, scenario.HealthCheck)
                    .ConfigureAwait(false);
                HttpResponseMessage? failedOperation = null;
                try
                {
                    failedOperation = await ExerciseDependencyAsync(client, scenario.Dependency).ConfigureAwait(false);
                    if (scenario.Dependency == IntegrationDependency.SqlServer)
                    {
                        var body = await failedOperation.Content.ReadAsStringAsync().ConfigureAwait(false);
                        body.Should()
                            .Contain("We hit a snag")
                            .And.NotContain("No observatories are publicly listed");
                    }
                    else
                    {
                        failedOperation.IsSuccessStatusCode.Should().BeFalse(scenario.Dependency.ToString());
                    }
                }
                catch (OperationCanceledException)
                {
                    // A paused dependency can hold its request until the bounded client timeout.
                }
                catch (HttpRequestException)
                {
                }
                finally
                {
                    failedOperation?.Dispose();
                }

                var outageElapsed = Stopwatch.GetElapsedTime(outageStarted);
                if (outageElapsed < TimeSpan.FromSeconds(15))
                {
                    await Task.Delay(TimeSpan.FromSeconds(15) - outageElapsed).ConfigureAwait(false);
                }

                var recoveryStarted = Stopwatch.GetTimestamp();
                await RestoreAsync(container, scenario.Dependency).ConfigureAwait(false);
                restored = true;
                if (scenario.Dependency == IntegrationDependency.SqlServer) SqlConnection.ClearAllPools();
                var recovered = await WaitForRecoveryAsync(
                    client,
                    scenario,
                    TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                var recoveryElapsed = Stopwatch.GetElapsedTime(recoveryStarted);
                recoveryElapsed.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(60));
                evidence.Add(new(
                    scenario.Dependency.ToString(),
                    initial.OverallStatus,
                    unavailable.CheckStatus,
                    recovered.CheckStatus,
                    Math.Max(15_000, Stopwatch.GetElapsedTime(outageStarted).TotalMilliseconds - recoveryElapsed.TotalMilliseconds),
                    recoveryElapsed.TotalMilliseconds,
                    (int)unavailable.HttpStatus,
                    (int)recovered.HttpStatus));
            }
            finally
            {
                if (!restored)
                {
                    var outageElapsed = Stopwatch.GetElapsedTime(outageStarted);
                    if (outageElapsed < TimeSpan.FromSeconds(15))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(15) - outageElapsed).ConfigureAwait(false);
                    }
                    await RestoreAsync(container, scenario.Dependency).ConfigureAwait(false);
                    if (scenario.Dependency == IntegrationDependency.SqlServer) SqlConnection.ClearAllPools();
                }
            }
        }

        await WriteEvidenceAsync(evidence).ConfigureAwait(false);
    }

    private static Task DisruptAsync(DotNet.Testcontainers.Containers.IContainer container, IntegrationDependency dependency)
        => container.PauseAsync(CancellationToken.None);

    private static Task RestoreAsync(DotNet.Testcontainers.Containers.IContainer container, IntegrationDependency dependency)
        => container.UnpauseAsync(CancellationToken.None);

    private static async Task<HealthSnapshot> ObserveUnavailableHealthEndpointAsync(
        HttpClient client,
        string checkName)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try
        {
            using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), timeout.Token)
                .ConfigureAwait(false);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token)
                .ConfigureAwait(false));
            var root = document.RootElement;
            var check = root.GetProperty("checks").EnumerateArray()
                .Single(item => item.GetProperty("name").GetString() == checkName);
            return new(
                response.StatusCode,
                root.GetProperty("status").GetString() ?? "Unknown",
                check.GetProperty("status").GetString() ?? "Unknown");
        }
        catch (OperationCanceledException)
        {
            return new(HttpStatusCode.ServiceUnavailable, "Unavailable", "Unavailable");
        }
    }

    private static async Task<HealthSnapshot> WaitForRecoveryAsync(
        HttpClient client,
        DependencyScenario scenario,
        TimeSpan timeout)
    {
        var started = Stopwatch.GetTimestamp();
        HealthSnapshot? last = null;
        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            if (scenario.Dependency == IntegrationDependency.SqlServer) SqlConnection.ClearAllPools();
            try
            {
                using var operation = await ExerciseDependencyAsync(client, scenario.Dependency).ConfigureAwait(false);
                last = await ReadHealthAsync(client, scenario.HealthCheck).ConfigureAwait(false);
                if (operation.IsSuccessStatusCode && last.CheckStatus == "Healthy") return last;
            }
            catch (OperationCanceledException)
            {
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(500).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"Dependency '{scenario.Dependency}' did not recover. Last health status: {last?.CheckStatus ?? "unavailable"}.");
    }

    private static async Task<HttpResponseMessage> ExerciseDependencyAsync(
        HttpClient client,
        IntegrationDependency dependency)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        return dependency switch
        {
            IntegrationDependency.SqlServer => await client.GetAsync(
                new Uri("/observatories", UriKind.Relative), timeout.Token).ConfigureAwait(false),
            IntegrationDependency.Redis => await client.PostAsJsonAsync(
                new Uri("/api/v1.0/diagnostics/cache", UriKind.Relative),
                new CacheDiagnosticsRequest
                {
                    Key = "diagnostics:i107-outage",
                    Value = "bounded",
                    ExpirationSeconds = 60
                }, timeout.Token).ConfigureAwait(false),
            IntegrationDependency.Minio => await client.PostAsJsonAsync(
                new Uri("/api/v1.0/diagnostics/minio", UriKind.Relative),
                new StorageDiagnosticsRequest
                {
                    ObjectName = "diagnostics/i107-outage.txt",
                    Content = "bounded"
                }, timeout.Token).ConfigureAwait(false),
            IntegrationDependency.Smtp => await client.PostAsJsonAsync(
                new Uri("/api/v1.0/diagnostics/email", UriKind.Relative),
                new EmailDiagnosticsRequest
                {
                    Recipient = TestEmail.AdminRecipient,
                    Subject = "I107 dependency recovery",
                    Body = "Bounded acceptance evidence"
                }, timeout.Token).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(dependency))
        };
    }

    private static async Task<HealthSnapshot> WaitForHealthStatusAsync(
        HttpClient client,
        string checkName,
        string expectedStatus,
        TimeSpan timeout)
    {
        var started = Stopwatch.GetTimestamp();
        HealthSnapshot? last = null;
        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            try
            {
                last = await ReadHealthAsync(client, checkName).ConfigureAwait(false);
                if (last.CheckStatus == expectedStatus) return last;
            }
            catch (OperationCanceledException)
            {
            }
            await Task.Delay(500).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"Health check '{checkName}' did not reach '{expectedStatus}'. Last status: {last?.CheckStatus ?? "unavailable"}.");
    }

    private static async Task<HealthSnapshot> ReadHealthAsync(HttpClient client, string checkName)
    {
        using var requestTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await client.GetAsync(
            new Uri("/health", UriKind.Relative),
            requestTimeout.Token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(requestTimeout.Token).ConfigureAwait(false));
        var root = document.RootElement;
        var check = root.GetProperty("checks").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == checkName);
        return new(
            response.StatusCode,
            root.GetProperty("status").GetString() ?? "Unknown",
            check.GetProperty("status").GetString() ?? "Unknown");
    }

    private static async Task WriteEvidenceAsync(IReadOnlyList<DependencyOutageEvidence> dependencies)
    {
        var root = Environment.GetEnvironmentVariable("HVO_ISSUE_107_EVIDENCE_ROOT");
        if (string.IsNullOrWhiteSpace(root)) return;
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            Path.Combine(root, "dependency-outages.json"),
            JsonSerializer.Serialize(new
            {
                Schema = "hvo-logichost-ui-107-dependency-outages-v1",
                Revision = Environment.GetEnvironmentVariable("HVO_ISSUE_107_REVISION") ?? "development",
                Dependencies = dependencies
            }, EvidenceJsonOptions)).ConfigureAwait(false);
    }

    private sealed record DependencyScenario(IntegrationDependency Dependency, string HealthCheck);
    private sealed record HealthSnapshot(HttpStatusCode HttpStatus, string OverallStatus, string CheckStatus);
    private sealed record DependencyOutageEvidence(
        string Dependency,
        string InitialOverallStatus,
        string OutageStatus,
        string RecoveryStatus,
        double OutageMilliseconds,
        double RecoveryMilliseconds,
        int OutageHealthHttpStatus,
        int RecoveryHealthHttpStatus);
}
