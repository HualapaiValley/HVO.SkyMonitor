using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "Process observations and evidence hashing are intentionally synchronous outside request execution.")]
public sealed class DeviceBootstrapPerformanceTests
{
    private const int Warmups = 5;
    private const int Measurements = 30;

    [TestMethod]
    public async Task FreshRegistrationEnvelopeAndBootstrap_RecordsPerformanceEvidence()
    {
        Assert.AreEqual("Release", typeof(DeviceBootstrapPerformanceTests).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
            .Cast<System.Reflection.AssemblyConfigurationAttribute>().Single().Configuration);
        var evidenceRun = Issue170PerformanceEvidence.Create();
        var fixture = AssemblyHooks.Fixture;
        var sql = new SqlCommandCounter();
        var transactions = new SqlTransactionCounter();
        using var factory = CreateFactory(fixture, sql, transactions);
        var owner = await SeedObservatoryAsync(factory).ConfigureAwait(false);
        using var ownerClient = factory.CreateClient();
        var ownerToken = await HttpHelpers.GetPasswordTokenAsync(
            ownerClient,
            "/connect/token",
            TestUsers.Operator.Username,
            TestUsers.Operator.Password,
            TestClients.WebUI.ClientId,
            string.Join(' ', TestClients.WebUI.Scopes)).ConfigureAwait(false);
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ownerToken.AccessToken);
        using var bootstrapClient = factory.CreateClient();

        var operations = new List<BootstrapOperation>(Warmups + Measurements);
        for (var index = 0; index < Warmups; index++)
        {
            operations.Add(await ExecuteBootstrapAsync(ownerClient, bootstrapClient, owner, index).ConfigureAwait(false));
        }

        StabilizeGc();
        sql.Reset();
        transactions.Reset();
        var latencies = new double[Measurements];
        long requestBytes = 0;
        long responseBytes = 0;
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var allocationsBefore = GC.GetTotalAllocatedBytes(precise: true);
        var rssBefore = process.WorkingSet64;
        await using var rssSampler = new RssSampler(process, rssBefore);
        var measuredStarted = Stopwatch.GetTimestamp();
        for (var index = 0; index < Measurements; index++)
        {
            var operationStarted = Stopwatch.GetTimestamp();
            var result = await ExecuteBootstrapAsync(
                ownerClient, bootstrapClient, owner, Warmups + index).ConfigureAwait(false);
            operations.Add(result);
            latencies[index] = Stopwatch.GetElapsedTime(operationStarted).TotalMilliseconds;
            requestBytes += result.RequestBytes;
            responseBytes += result.ResponseBytes;
        }
        var elapsed = Stopwatch.GetElapsedTime(measuredStarted);
        var measuredSqlCommands = sql.Count;
        var measuredTransactions = transactions.Snapshot();
        process.Refresh();
        var cpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocationsBefore;
        var rssAfter = process.WorkingSet64;
        var rssPeak = Math.Max(
            Math.Max(rssBefore, rssAfter),
            await rssSampler.StopAsync().ConfigureAwait(false));
        Array.Sort(latencies);

        await using var assertionScope = factory.Services.CreateAsyncScope();
        var db = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var devicePrefix = owner.DevicePrefix;
        var registrations = await db.DeviceRegistrations.AsNoTracking()
            .Where(item => item.DeviceId.StartsWith(devicePrefix))
            .ToArrayAsync().ConfigureAwait(false);
        Assert.AreEqual(Warmups + Measurements, registrations.Length);
        Assert.IsTrue(registrations.All(item => item.Status == DeviceRegistrationStatus.Active));
        Assert.IsTrue(operations.All(item => item.RegistrationId == item.ResponseRegistrationId));
        var registrationIds = registrations.Select(item => item.Id).ToArray();
        var authoritySchemaPresent = await ScalarAsync(db,
            "SELECT COUNT(*) FROM sys.tables WHERE name = 'DeviceDeploymentLocationVersions'").ConfigureAwait(false) == 1;
        var deploymentRows = authoritySchemaPresent
            ? await ScalarAsync(db, $"""
                SELECT COUNT(*) FROM DeviceDeploymentLocationVersions d
                INNER JOIN DeviceRegistrations r ON r.Id = d.RegistrationId
                WHERE r.DeviceId LIKE '{devicePrefix}%'
                """).ConfigureAwait(false)
            : 0;
        var auditRows = authoritySchemaPresent
            ? await ScalarAsync(db, $"""
                SELECT COUNT(*) FROM DeploymentLocationResolutionAudits a
                INNER JOIN DeviceRegistrations r ON r.Id = a.RegistrationId
                WHERE r.DeviceId LIKE '{devicePrefix}%'
                """).ConfigureAwait(false)
            : 0;
        var frameRows = await db.CentralFrames.AsNoTracking()
            .CountAsync(item => registrationIds.Contains(item.RegistrationId)).ConfigureAwait(false);
        var artifactRows = await db.CentralArtifacts.AsNoTracking()
            .CountAsync(item => registrationIds.Contains(item.Frame!.RegistrationId)).ConfigureAwait(false);
        Assert.AreEqual(authoritySchemaPresent ? registrations.Length : 0, deploymentRows);
        Assert.AreEqual(authoritySchemaPresent ? registrations.Length : 0, auditRows);
        Assert.AreEqual(0, frameRows);
        Assert.AreEqual(0, artifactRows);
        Assert.IsTrue(!authoritySchemaPresent || operations.All(item => item.AcknowledgmentExact));

        var evidence = new
        {
            Schema = "hvo-device-bootstrap-performance-v1",
            Issue = 170,
            Revision = evidenceRun.Revision,
            Environment = new
            {
                Observed = evidenceRun.Environment,
                Topology = "ASP.NET Core TestServer and SQL Server 2022 Testcontainer"
            },
            BuildCommand = Issue170PerformanceEvidence.BuildCommand,
            Command = evidenceRun.CreateTestCommand(
                "DeviceBootstrapPerformanceTests.FreshRegistrationEnvelopeAndBootstrap_RecordsPerformanceEvidence"),
            Workload = new
            {
                LogicalOperation = "fresh owner-authenticated registration verification, envelope issuance, and anonymous bootstrap redemption",
                Warmups,
                Measurements,
                Concurrency = 1,
                Boundary = "first verify HTTP request start through successful bootstrap HTTP response body read",
                ArrivalModel = "closed-loop",
                Deployment = "inherited Observatory fallback on candidate; extra bootstrap JSON fields are ignored by the baseline controller"
            },
            Method = new
            {
                Trials = "Five separately launched Release test processes with unique run identities.",
                Percentiles = "nearest-rank over 30 retained measured-operation latency samples after five warmups",
                Resources = "Process.TotalProcessorTime, monotonic GC allocation delta, and sampled process working set",
                Sql = "EF command and transaction interceptors around the measured HTTP operations",
                Boundary = "owner verify request start through bootstrap response-body read"
            },
            Measurements = new
            {
                ElapsedMilliseconds = elapsed.TotalMilliseconds,
                OperationsPerSecond = Measurements / elapsed.TotalSeconds,
                LatencyMilliseconds = Distribution(latencies),
                LatencySamplesMilliseconds = latencies,
                Http = new
                {
                    Requests = Measurements * 3,
                    RequestBodyBytes = requestBytes,
                    ResponseBodyBytes = responseBytes,
                    TransportHeaders = "N/A: TestServer does not expose TCP/TLS wire bytes."
                },
                Sql = new
                {
                    Commands = measuredSqlCommands,
                    Transactions = measuredTransactions,
                    Rows = "N/A: provider diagnostics do not expose complete returned and affected row counts.",
                    WireBytes = "Unavailable from Microsoft.Data.SqlClient diagnostics."
                },
                Resources = new
                {
                    CpuMilliseconds = cpuMilliseconds,
                    AllocatedBytes = allocatedBytes,
                    WorkingSetBeforeBytes = rssBefore,
                    WorkingSetAfterBytes = rssAfter,
                    WorkingSetObservedPeakBytes = rssPeak,
                    WorkingSetSamplingIntervalMilliseconds = 10
                }
            },
            IO = new
            {
                MinioRequests = 0,
                MinioBytes = 0,
                FileSystemBytes = "N/A: SQL Server container filesystem I/O is not instrumented.",
                QueueBytes = "N/A: synchronous metadata path has no durable queue.",
                LohAndFullFrameCopies = "N/A: metadata-only path has no frame payload."
            },
            Correctness = new
            {
                ActiveRegistrations = registrations.Length,
                ExpectedActiveRegistrations = Warmups + Measurements,
                UniqueDeviceIds = registrations.Select(item => item.DeviceId).Distinct(StringComparer.Ordinal).Count(),
                AuthoritySchemaPresent = authoritySchemaPresent,
                AcknowledgedDeploymentRows = deploymentRows,
                ResolutionAuditRows = auditRows,
                CentralFrameRows = frameRows,
                CentralArtifactRows = artifactRows,
                ExactAcknowledgments = operations.Count(item => item.AcknowledgmentExact)
            },
            Result = new
            {
                Comparison = "The reviewed summary pairs baseline and candidate only when immutable harness, workload, method, environment, scenario, metric, and sample fingerprints match.",
                ResidualRisk = "Process counters exclude SQL Server; TestServer excludes kernel TCP/TLS; SQL row and wire-byte observations are unavailable."
            },
            RecordedAtUtc = DateTimeOffset.UtcNow
        };
        await evidenceRun.WriteTrialAsync("device-bootstrap-performance.json", evidence).ConfigureAwait(false);
    }

    private static async Task<BootstrapOwner> SeedObservatoryAsync(
        WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var owner = await db.Users.SingleAsync(user => user.Email == TestUsers.Operator.Email).ConfigureAwait(false);
        var prefix = $"bootstrap-perf-{Guid.NewGuid():N}-";
        var observatory = new Observatory
        {
            OwnerUserId = owner.Id,
            Name = $"Bootstrap performance {Guid.NewGuid():N}",
            LatitudeDegrees = 35.347,
            LongitudeDegrees = -113.878,
            ElevationMeters = 520,
            TimeZoneId = "America/Phoenix",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            IsActive = true
        };
        db.Observatories.Add(observatory);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return new BootstrapOwner(observatory.Id, prefix, observatory.LatitudeDegrees,
            observatory.LongitudeDegrees, observatory.ElevationMeters, observatory.TimeZoneId);
    }

    private static async Task<BootstrapOperation> ExecuteBootstrapAsync(
        HttpClient ownerClient,
        HttpClient bootstrapClient,
        BootstrapOwner owner,
        int index)
    {
        var deviceId = $"{owner.DevicePrefix}{index:D4}";
        var verify = await SendJsonAsync(ownerClient, "/api/internal/devices/verify", new
        {
            deviceId,
            verificationCode = "SELFATTEST",
            observatoryId = owner.ObservatoryId,
            friendlyName = $"Bootstrap performance {index}",
            pendingLifetimeMinutes = 15
        }).ConfigureAwait(false);
        using var verifyJson = JsonDocument.Parse(verify.Body);
        var registrationId = GetProperty(verifyJson.RootElement, "registrationId").GetGuid();
        var envelope = await SendJsonAsync(ownerClient, "/api/internal/devices/envelope", new
        {
            registrationId,
            deviceId,
            observatoryId = owner.ObservatoryId,
            envelopeLifetimeMinutes = 10
        }).ConfigureAwait(false);
        using var envelopeJson = JsonDocument.Parse(envelope.Body);
        var protectedEnvelope = GetProperty(envelopeJson.RootElement, "envelope").GetString()
            ?? throw new InvalidOperationException("Envelope response was empty.");
        var deployment = DeploymentLocationSnapshot.Create(
            $"{deviceId}-location",
            1,
            "observatory-fallback",
            null,
            DateTimeOffset.UnixEpoch,
            null,
            owner.LatitudeDegrees,
            owner.LongitudeDegrees,
            owner.ElevationMeters,
            owner.TimeZoneId);
        var bootstrap = await SendJsonAsync(bootstrapClient, "/api/device/bootstrap", new
        {
            deviceId,
            envelope = protectedEnvelope,
            deploymentLocation = deployment,
            deploymentLocationSourceKind = "Inherited"
        }).ConfigureAwait(false);
        using var bootstrapJson = JsonDocument.Parse(bootstrap.Body);
        var responseRegistrationId = GetProperty(bootstrapJson.RootElement, "registrationId").GetGuid();
        var deviceKey = GetProperty(bootstrapJson.RootElement, "deviceKey").GetString()
            ?? throw new InvalidOperationException("Bootstrap response omitted the device key.");
        var encrypted = GetProperty(bootstrapJson.RootElement, "payload");
        var ciphertext = Convert.FromBase64String(GetProperty(encrypted, "ciphertext").GetString()!);
        var plaintext = new byte[ciphertext.Length];
        using (var aes = new AesGcm(Convert.FromBase64String(deviceKey), 16))
        {
            aes.Decrypt(
                Convert.FromBase64String(GetProperty(encrypted, "nonce").GetString()!),
                ciphertext,
                Convert.FromBase64String(GetProperty(encrypted, "tag").GetString()!),
                plaintext);
        }
        using var secrets = JsonDocument.Parse(plaintext);
        var acknowledgmentExact = TryGetProperty(
                secrets.RootElement, "deploymentLocationAcknowledgment", out var acknowledgment)
            && TryGetProperty(acknowledgment, "deployment", out var acknowledgedDeployment)
            && string.Equals(
                GetProperty(acknowledgedDeployment, "canonicalSha256").GetString(),
                deployment.CanonicalSha256,
                StringComparison.OrdinalIgnoreCase)
            && GetProperty(GetProperty(acknowledgment, "observatory"), "observatoryId").GetGuid()
                == owner.ObservatoryId
            && string.Equals(GetProperty(acknowledgment, "sourceKind").GetString(), "Inherited", StringComparison.Ordinal)
            && string.Equals(GetProperty(acknowledgment, "status").GetString(), "Acknowledged", StringComparison.Ordinal);
        return new BootstrapOperation(
            verify.RequestBytes + envelope.RequestBytes + bootstrap.RequestBytes,
            verify.Body.LongLength + envelope.Body.LongLength + bootstrap.Body.LongLength,
            registrationId,
            responseRegistrationId,
            deviceId,
            acknowledgmentExact);
    }

    private static JsonElement GetProperty(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }
        throw new KeyNotFoundException($"Response property {name} was not present.");
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Performance evidence uses fixed SQL with a generated GUID-only device prefix.")]
    private static async Task<int> ScalarAsync(ApplicationDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync().ConfigureAwait(false);
        }
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<HttpResult> SendJsonAsync(HttpClient client, string path, object request)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(request, HttpHelpers.DefaultJsonOptions);
        using var message = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(body)
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client.SendAsync(message).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Bootstrap performance request {path} failed with {(int)response.StatusCode}.");
        }
        return new HttpResult(body.LongLength, responseBody);
    }

    private static WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> CreateFactory(
        IntegrationTestFixture fixture,
        SqlCommandCounter counter,
        SqlTransactionCounter transactions)
        => fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<ApplicationDbContext>();
            services.AddDbContext<ApplicationDbContext>(options =>
            {
                options.UseSqlServer(fixture.SqlServerConnectionString);
                options.AddInterceptors(counter, transactions);
            });
        }));

    private static object Distribution(double[] ordered)
        => new
        {
            Minimum = ordered[0],
            Median = Percentile(ordered, 0.50),
            P95 = Percentile(ordered, 0.95),
            P99 = Percentile(ordered, 0.99),
            Maximum = ordered[^1]
        };

    private static double Percentile(double[] ordered, double percentile)
        => ordered[(int)Math.Ceiling(percentile * ordered.Length) - 1];

    private static void StabilizeGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static string RunGit(string root, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("git could not be started.");
        process.WaitForExit();
        return process.StandardOutput.ReadToEnd().Trim();
    }

    private sealed class SqlCommandCounter : DbCommandInterceptor
    {
        private long count;
        public long Count => Interlocked.Read(ref count);
        public void Reset() => Interlocked.Exchange(ref count, 0);
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref count);
            return result;
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref count);
            return ValueTask.FromResult(result);
        }
        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Interlocked.Increment(ref count);
            return result;
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref count);
            return ValueTask.FromResult(result);
        }
        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Interlocked.Increment(ref count);
            return result;
        }
        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref count);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class SqlTransactionCounter : DbTransactionInterceptor
    {
        private long started;
        private long committed;
        private long rolledBack;
        private long failed;

        public void Reset()
        {
            Interlocked.Exchange(ref started, 0);
            Interlocked.Exchange(ref committed, 0);
            Interlocked.Exchange(ref rolledBack, 0);
            Interlocked.Exchange(ref failed, 0);
        }

        public TransactionCounts Snapshot()
            => new(
                Interlocked.Read(ref started),
                Interlocked.Read(ref committed),
                Interlocked.Read(ref rolledBack),
                Interlocked.Read(ref failed));

        public override InterceptionResult<DbTransaction> TransactionStarting(
            DbConnection connection,
            TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result)
        {
            Interlocked.Increment(ref started);
            return result;
        }

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection,
            TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref started);
            return ValueTask.FromResult(result);
        }

        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
            => Interlocked.Increment(ref committed);

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref committed);
            return Task.CompletedTask;
        }

        public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
            => Interlocked.Increment(ref rolledBack);

        public override Task TransactionRolledBackAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref rolledBack);
            return Task.CompletedTask;
        }

        public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData)
            => Interlocked.Increment(ref failed);

        public override Task TransactionFailedAsync(
            DbTransaction transaction,
            TransactionErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref failed);
            return Task.CompletedTask;
        }
    }

    private sealed class RssSampler : IAsyncDisposable
    {
        private readonly Process process;
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task sampling;
        private long peak;

        public RssSampler(Process process, long initial)
        {
            this.process = process;
            peak = initial;
            sampling = SampleAsync();
        }

        public async Task<long> StopAsync()
        {
            cancellation.Cancel();
            await sampling.ConfigureAwait(false);
            return Interlocked.Read(ref peak);
        }

        public async ValueTask DisposeAsync()
        {
            if (!cancellation.IsCancellationRequested)
            {
                _ = await StopAsync().ConfigureAwait(false);
            }
            cancellation.Dispose();
        }

        private async Task SampleAsync()
        {
            try
            {
                while (true)
                {
                    process.Refresh();
                    var observed = process.WorkingSet64;
                    long current;
                    do
                    {
                        current = Interlocked.Read(ref peak);
                    }
                    while (observed > current && Interlocked.CompareExchange(ref peak, observed, current) != current);
                    await Task.Delay(TimeSpan.FromMilliseconds(10), cancellation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }
    }

    private sealed record BootstrapOwner(
        Guid ObservatoryId,
        string DevicePrefix,
        double LatitudeDegrees,
        double LongitudeDegrees,
        double ElevationMeters,
        string TimeZoneId);
    private sealed record BootstrapOperation(
        long RequestBytes,
        long ResponseBytes,
        Guid RegistrationId,
        Guid ResponseRegistrationId,
        string DeviceId,
        bool AcknowledgmentExact);
    private sealed record HttpResult(long RequestBytes, byte[] Body);
    private sealed record TransactionCounts(long Started, long Committed, long RolledBack, long Failed);
}
