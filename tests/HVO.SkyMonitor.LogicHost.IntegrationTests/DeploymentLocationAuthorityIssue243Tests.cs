using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HVO.SkyMonitor.IntegrationTests;

public sealed partial class DeploymentLocationAuthorityPerformanceTests
{
    private const int Issue243CaptureCount = 10_000;
    private static readonly JsonSerializerOptions Issue243JsonOptions = new() { WriteIndented = true };

    [TestMethod]
    public async Task SingleDeploymentTenThousandCaptures_RecordsTransactionAndSchedulerAmplification()
    {
        var fixture = AssemblyHooks.Fixture;
        var connectionBuilder = new SqlConnectionStringBuilder(fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorIssue243Reconciliation_{Guid.NewGuid():N}",
            ApplicationName = $"HVO.SkyMonitor.Issue243.Reconciliation.{Guid.NewGuid():N}"
        };
        var connectionString = connectionBuilder.ConnectionString;
        var scheduler = new Issue243CountingScheduler();
        var probe = new Issue243SqlProbe();
        var transactionProbe = new Issue243TransactionProbe();
        var factory = CreateIssue243Factory(fixture, connectionString, scheduler, probe, transactionProbe);
        try
        {
            var seeded = await SeedIssue243DeploymentAsync(factory).ConfigureAwait(false);
            probe.Reset();
            var databaseBefore = await ReadDatabaseSizeAsync(factory).ConfigureAwait(false);
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var cpuBefore = process.TotalProcessorTime;
            var rssBefore = process.WorkingSet64;
            var started = Stopwatch.GetTimestamp();
            DeploymentLocationResolutionResult result;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var authority = scope.ServiceProvider.GetRequiredService<IDeploymentLocationAuthorityService>();
                probe.Start(db.ContextId);
                transactionProbe.Start(db.ContextId);
                try
                {
                    result = await authority.ResolveAsync(
                            seeded.DeploymentId,
                            seeded.OwnerUserId,
                            DeploymentLocationResolutionStatus.Acknowledged,
                            "issue-243-baseline",
                            seeded.ConcurrencyToken,
                            null,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                finally
                {
                    transactionProbe.Stop();
                    probe.Stop();
                }
            }
            var elapsed = Stopwatch.GetElapsedTime(started);
            var operationCommandCount = probe.CommandCount;
            var operationTransactionMilliseconds = transactionProbe.TransactionMilliseconds;
            process.Refresh();
            var cpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            var rssAfter = process.WorkingSet64;
            var databaseAfter = await ReadDatabaseSizeAsync(factory).ConfigureAwait(false);

            Assert.AreEqual(DeploymentLocationMutationStatus.Applied, result.Status);
            Assert.AreEqual(Issue243CaptureCount, scheduler.Calls);
            var state = await ReadIssue243StateAsync(factory, seeded).ConfigureAwait(false);
            Assert.AreEqual(Issue243CaptureCount, state.ResolvedCaptures);
            Assert.AreEqual(Issue243CaptureCount, state.BoundArtifacts);
            Assert.AreEqual(1, state.Audits);
            Assert.IsGreaterThan(0, probe.CommandCount);
            Assert.IsGreaterThan(0, transactionProbe.TransactionMilliseconds);
            Assert.AreEqual(1, transactionProbe.Starts);
            Assert.AreEqual(1, transactionProbe.Commits);
            Assert.IsNotNull(probe.ReconciliationCommand);

            var repositoryRoot = FindRepositoryRoot();
            var source = await EvidenceSourceIdentity.CaptureAsync(
                repositoryRoot,
                typeof(DeploymentLocationAuthorityPerformanceTests),
                typeof(DeploymentLocationAuthorityService)).ConfigureAwait(false);
            var output = Path.Combine(
                repositoryRoot, "TestResults", "issue-243", source.OutputDirectoryName, source.RunId);
            Directory.CreateDirectory(output);
            var command = probe.ReconciliationCommand!;
            var evidence = new
            {
                Schema = "hvo-issue-243-deployment-reconciliation-v1",
                Source = source,
                Workload = new
                {
                    Deployments = 1,
                    Captures = Issue243CaptureCount,
                    EligibleArtifacts = Issue243CaptureCount,
                    Concurrency = 1,
                    InitialBacklog = Issue243CaptureCount,
                    ArrivalRate = 0,
                    Warmups = 0,
                    Trials = 1,
                    Purpose = "Structural deep-cardinality probe, not a production scheduler performance benchmark.",
                    SchedulerSubstitution = "Counting no-op ICentralDerivativeJobScheduler; scheduler persistence cost is excluded."
                },
                Measurement = new
                {
                    ElapsedMilliseconds = elapsed.TotalMilliseconds,
                    TransactionMilliseconds = operationTransactionMilliseconds,
                    OperationSqlCommands = operationCommandCount,
                    CountingNoOpSchedulerCalls = scheduler.Calls,
                    CpuMilliseconds = cpuMilliseconds,
                    AllocatedBytes = allocatedBytes,
                    RssBeforeBytes = rssBefore,
                    RssAfterBytes = rssAfter,
                    DataUsedGrowthBytes = databaseAfter.DataUsedBytes - databaseBefore.DataUsedBytes,
                    LogUsedGrowthBytes = databaseAfter.LogUsedBytes - databaseBefore.LogUsedBytes,
                    ReconciliationCommandSha256 = command.NormalizedSha256,
                    Parameters = command.Parameters.Select(static parameter => new
                    {
                        parameter.Name,
                        parameter.SqlDbType,
                        parameter.Size,
                        parameter.Precision,
                        parameter.Scale
                    }).ToArray()
                },
                Correctness = state,
                Result = "The production authority path materializes a deep deployment and invokes the scheduler interface once per artifact before commit. Timing and SQL counts exclude real scheduler persistence and are descriptive only.",
                RecordedAtUtc = DateTimeOffset.UtcNow
            };
            await EvidenceSourceIdentity.WriteJsonAsync(
                Path.Combine(output, "deployment-reconciliation-10000.json"),
                evidence,
                Issue243JsonOptions).ConfigureAwait(false);
        }
        finally
        {
            factory.Dispose();
            await DeleteIssue243DatabaseAsync(connectionString).ConfigureAwait(false);
        }
    }

    private static WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> CreateIssue243Factory(
        IntegrationTestFixture fixture,
        string connectionString,
        Issue243CountingScheduler scheduler,
        Issue243SqlProbe probe,
        Issue243TransactionProbe transactionProbe)
        => fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<ApplicationDbContext>();
            services.AddDbContext<ApplicationDbContext>(options => options
                .UseSqlServer(connectionString, sql => sql.CommandTimeout(180))
                .AddInterceptors(probe, transactionProbe));
            services.RemoveAll<ICentralDerivativeJobScheduler>();
            services.AddSingleton<ICentralDerivativeJobScheduler>(scheduler);
        }));

    private static async Task<Issue243DeploymentFixture> SeedIssue243DeploymentAsync(
        WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var owner = await db.Users.SingleAsync(user => user.Email == TestUsers.Operator.Email).ConfigureAwait(false);
        var now = new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);
        var observatory = new Observatory
        {
            OwnerUserId = owner.Id,
            Name = $"Issue 243 reconciliation {Guid.NewGuid():N}",
            LatitudeDegrees = 35,
            LongitudeDegrees = -113,
            ElevationMeters = 500,
            TimeZoneId = "UTC",
            AllowedDeploymentRadiusMeters = 1000,
            CreatedAtUtc = now,
            IsActive = true
        };
        var observatoryVersion = new ObservatoryLocationVersion
        {
            Observatory = observatory,
            ObservatoryId = observatory.Id,
            Version = 1,
            CanonicalSha256 = new string('A', 64),
            EffectiveFromUtc = now.AddDays(-1),
            LatitudeDegrees = observatory.LatitudeDegrees,
            LongitudeDegrees = observatory.LongitudeDegrees,
            ElevationMeters = observatory.ElevationMeters,
            TimeZoneId = observatory.TimeZoneId,
            AllowedDeploymentRadiusMeters = observatory.AllowedDeploymentRadiusMeters,
            RecordedAtUtc = now,
            RecordedBy = owner.Id
        };
        var registration = new DeviceRegistration
        {
            DeviceId = $"issue-243-device-{Guid.NewGuid():N}",
            ObservatoryId = observatory.Id,
            ObservatoryName = observatory.Name,
            ObservatoryTimeZoneId = observatory.TimeZoneId,
            FriendlyName = "Issue 243 reconciliation camera",
            OwnerUserId = owner.Id,
            OwnerDisplayName = owner.UserName!,
            OwnerConfirmationMethod = "SelfAttested",
            Status = DeviceRegistrationStatus.Active,
            VerificationCodeHash = new string('B', 64),
            DevicePublicId = Guid.NewGuid(),
            IssuedAtUtc = now,
            ActivatedAtUtc = now
        };
        var deployment = new DeviceDeploymentLocationVersion
        {
            Registration = registration,
            RegistrationId = registration.Id,
            DevicePublicId = registration.DevicePublicId,
            ObservatoryId = observatory.Id,
            ObservatoryLocationVersion = observatoryVersion,
            ObservatoryLocationVersionId = observatoryVersion.Id,
            ObservatoryLocationVersionNumber = observatoryVersion.Version,
            ObservatoryLocationCanonicalSha256 = observatoryVersion.CanonicalSha256,
            LocationId = "issue-243-deployment",
            Version = 2,
            CanonicalSha256 = new string('C', 64),
            Source = "operator-survey",
            SourceKind = DeploymentLocationSourceKind.Manual,
            HorizontalAccuracyMeters = 2,
            EffectiveFromUtc = now.AddHours(-1),
            LatitudeDegrees = observatory.LatitudeDegrees,
            LongitudeDegrees = observatory.LongitudeDegrees,
            ElevationMeters = observatory.ElevationMeters,
            TimeZoneId = observatory.TimeZoneId,
            Status = DeploymentLocationResolutionStatus.Pending,
            ReasonCode = "location-changed",
            ProposedAtUtc = now
        };
        db.Observatories.Add(observatory);
        db.ObservatoryMemberships.Add(new ObservatoryMembership
        {
            Observatory = observatory,
            ObservatoryId = observatory.Id,
            UserId = owner.Id,
            Role = ObservatoryMembershipRole.Owner,
            AddedAtUtc = now
        });
        db.ObservatoryLocationVersions.Add(observatoryVersion);
        db.DeviceRegistrations.Add(registration);
        db.DeviceDeploymentLocationVersions.Add(deployment);
        await db.SaveChangesAsync().ConfigureAwait(false);

        for (var offset = 0; offset < Issue243CaptureCount; offset += 250)
        {
            var frames = new List<CentralFrame>(250);
            for (var index = offset; index < Math.Min(offset + 250, Issue243CaptureCount); index++)
            {
                var captured = now.AddMilliseconds(index);
                var frame = new CentralFrame
                {
                    RegistrationId = registration.Id,
                    DevicePublicId = registration.DevicePublicId!.Value,
                    ObservatoryId = observatory.Id,
                    AgentId = registration.DeviceId,
                    FrameId = Guid.NewGuid(),
                    CapturedAtUtc = captured,
                    FirstReceivedAtUtc = captured,
                    LocationEvidenceState = CentralCaptureLocationEvidenceState.ReportedUnresolved
                };
                frame.Location = new CentralCaptureLocation
                {
                    CentralFrame = frame,
                    CentralFrameId = frame.Id,
                    LocationId = deployment.LocationId,
                    Version = deployment.Version,
                    Source = deployment.Source,
                    HorizontalAccuracyMeters = deployment.HorizontalAccuracyMeters,
                    EffectiveFromUtc = deployment.EffectiveFromUtc,
                    EffectiveUntilUtc = deployment.EffectiveUntilUtc
                };
                frame.Artifacts.Add(new CentralArtifact
                {
                    CentralFrameId = frame.Id,
                    DevicePublicId = registration.DevicePublicId!.Value,
                    ArtifactId = Guid.NewGuid(),
                    Role = FrameArtifactRole.Raw,
                    RecipeVersion = "raw-v1",
                    ManifestSchemaVersion = "v2",
                    MediaType = "application/octet-stream",
                    ByteLength = 4,
                    ChecksumSha256 = new string('D', 64),
                    StorageReference = $"object://skymonitor-artifacts/issue-243/{index:D5}",
                    ReceivedAtUtc = captured,
                    IdempotencyKey = Guid.NewGuid().ToString("N"),
                    ObjectState = CentralArtifactObjectState.Available,
                    ReconstructionState = CentralReconstructionState.Complete
                });
                frames.Add(frame);
            }
            db.CentralFrames.AddRange(frames);
            await db.SaveChangesAsync().ConfigureAwait(false);
            db.ChangeTracker.Clear();
        }
        return new(owner.Id, deployment.Id, deployment.ConcurrencyToken, registration.Id);
    }

    private static async Task<Issue243ReconciliationState> ReadIssue243StateAsync(
        WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory,
        Issue243DeploymentFixture fixture)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return new(
            await db.CentralFrames.CountAsync(frame => frame.RegistrationId == fixture.RegistrationId
                && frame.LocationEvidenceState == CentralCaptureLocationEvidenceState.ReportedResolved)
                .ConfigureAwait(false),
            await db.CentralArtifacts.CountAsync(artifact => artifact.Frame!.RegistrationId == fixture.RegistrationId
                && artifact.Frame.Location!.DeviceDeploymentLocationVersionId == fixture.DeploymentId)
                .ConfigureAwait(false),
            await db.DeploymentLocationResolutionAudits.CountAsync(audit =>
                audit.DeviceDeploymentLocationVersionId == fixture.DeploymentId).ConfigureAwait(false));
    }

    private static async Task DeleteIssue243DatabaseAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(connectionString).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureDeletedAsync().ConfigureAwait(false);
    }

    private sealed class Issue243CountingScheduler : ICentralDerivativeJobScheduler
    {
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        public Task EnsureRequiredJobsAsync(
            CentralArtifact artifact,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.CompletedTask;
        }
    }

    private sealed class Issue243SqlProbe : DbCommandInterceptor
    {
        private readonly ConcurrentBag<Issue243CommandSnapshot> _commands = [];
        private int _enabled;
        private DbContextId _targetContextId;

        internal int CommandCount => _commands.Count;

        internal Issue243CommandSnapshot? ReconciliationCommand => _commands.FirstOrDefault(command =>
            command.CommandText.Contains("FROM [CentralCaptureLocations]", StringComparison.Ordinal));

        internal void Reset()
        {
            _commands.Clear();
        }

        internal void Start(DbContextId targetContextId)
        {
            Reset();
            _targetContextId = targetContextId;
            Volatile.Write(ref _enabled, 1);
        }

        internal void Stop() => Volatile.Write(ref _enabled, 0);

        public override DbDataReader ReaderExecuted(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result)
        {
            Record(command, eventData);
            return result;
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            Record(command, eventData);
            return ValueTask.FromResult(result);
        }

        public override object? ScalarExecuted(
            DbCommand command,
            CommandExecutedEventData eventData,
            object? result)
        {
            Record(command, eventData);
            return result;
        }

        public override ValueTask<object?> ScalarExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            object? result,
            CancellationToken cancellationToken = default)
        {
            Record(command, eventData);
            return ValueTask.FromResult(result);
        }

        public override int NonQueryExecuted(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result)
        {
            Record(command, eventData);
            return result;
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            Record(command, eventData);
            return ValueTask.FromResult(result);
        }

        private void Record(DbCommand command, CommandExecutedEventData eventData)
        {
            if (Volatile.Read(ref _enabled) != 0
                && eventData.Context is { } context
                && context.ContextId.Equals(_targetContextId))
            {
                _commands.Add(Issue243CommandSnapshot.Create(command));
            }
        }
    }

    private sealed class Issue243TransactionProbe : DbTransactionInterceptor
    {
        private long _started;
        private double _milliseconds;
        private int _enabled;
        private int _starts;
        private int _commits;
        private DbContextId _targetContextId;

        internal double TransactionMilliseconds => Volatile.Read(ref _milliseconds);
        internal int Starts => Volatile.Read(ref _starts);
        internal int Commits => Volatile.Read(ref _commits);

        internal void Start(DbContextId targetContextId)
        {
            Interlocked.Exchange(ref _started, 0);
            Volatile.Write(ref _milliseconds, 0);
            Volatile.Write(ref _starts, 0);
            Volatile.Write(ref _commits, 0);
            _targetContextId = targetContextId;
            Volatile.Write(ref _enabled, 1);
        }

        internal void Stop() => Volatile.Write(ref _enabled, 0);

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection,
            TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            if (IsTarget(eventData.Context))
            {
                Interlocked.Exchange(ref _started, Stopwatch.GetTimestamp());
                Interlocked.Increment(ref _starts);
            }
            return ValueTask.FromResult(result);
        }

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            var started = Interlocked.Read(ref _started);
            if (IsTarget(eventData.Context) && started != 0)
            {
                Volatile.Write(ref _milliseconds, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                Interlocked.Increment(ref _commits);
            }
            return Task.CompletedTask;
        }

        private bool IsTarget(DbContext? context)
            => Volatile.Read(ref _enabled) != 0
                && context is not null
                && context.ContextId.Equals(_targetContextId);
    }

    private sealed record Issue243CommandSnapshot(
        string CommandText,
        string NormalizedSha256,
        IReadOnlyList<Issue243ParameterMetadata> Parameters)
    {
        internal static Issue243CommandSnapshot Create(DbCommand command)
        {
            var normalized = string.Join(' ', command.CommandText.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return new(
                command.CommandText,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))),
                command.Parameters.Cast<SqlParameter>().Select(static parameter => new Issue243ParameterMetadata(
                    parameter.ParameterName,
                    parameter.SqlDbType.ToString(),
                    parameter.Size,
                    parameter.Precision,
                    parameter.Scale)).ToArray());
        }
    }

    private sealed record Issue243ParameterMetadata(
        string Name,
        string SqlDbType,
        int Size,
        byte Precision,
        byte Scale);

    private sealed record Issue243DeploymentFixture(
        string OwnerUserId,
        Guid DeploymentId,
        Guid ConcurrencyToken,
        Guid RegistrationId);

    private sealed record Issue243ReconciliationState(
        int ResolvedCaptures,
        int BoundArtifacts,
        int Audits);
}
