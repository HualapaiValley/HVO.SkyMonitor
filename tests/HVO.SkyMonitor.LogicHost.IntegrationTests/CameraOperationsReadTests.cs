using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class CameraOperationsReadTests
{
    [TestMethod]
    public async Task Http_ViewerReadsEveryCameraCollectionWhileNonMembersAndOtherScopesAreDenied()
    {
        var seed = await CameraOperationsSeed.CreateAsync(captures: 6, jobEvery: 2, executions: 3, proposals: 2).ConfigureAwait(false);
        await seed.AddMemberAsync(TestUsers.Viewer.Email, ObservatoryMembershipRole.Viewer).ConfigureAwait(false);
        using var viewer = await ArtifactRetrievalTests.CreateUserClientAsync(TestUsers.Viewer.Username, TestUsers.Viewer.Password).ConfigureAwait(false);
        foreach (var path in seed.CollectionPaths().Concat(seed.ExactPaths()))
        {
            using var response = await viewer.GetAsync(path).ConfigureAwait(false);
            response.StatusCode.Should().Be(HttpStatusCode.OK, path.ToString());
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
            response.Headers.CacheControl.Private.Should().BeTrue();
        }
        using (var executions = await viewer.GetAsync(seed.Path("processing-graph-executions?take=50")).ConfigureAwait(false))
        {
            using var json = JsonDocument.Parse(await executions.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
            json.RootElement.GetProperty("Items").EnumerateArray().Select(item => item.GetProperty("Id").GetGuid())
                .Should().BeEquivalentTo(seed.Executions);
        }
        using (var job = await viewer.GetAsync(seed.Path($"jobs/{seed.InspectedJob:D}?take=1")).ConfigureAwait(false))
        {
            var payload = await job.Content.ReadAsStringAsync().ConfigureAwait(false);
            payload.Should().NotContain("Lease").And.NotContain(seed.LeaseToken.ToString("D"));
            using var json = JsonDocument.Parse(payload);
            json.RootElement.GetProperty("Attempts").GetProperty("Items").GetArrayLength().Should().Be(1);
            json.RootElement.GetProperty("Attempts").GetProperty("NextCursor").GetString().Should().NotBeNullOrEmpty();
            json.RootElement.GetProperty("Inputs").GetProperty("Items")[0].GetProperty("ArtifactId").GetGuid()
                .Should().Be(seed.InspectedJobSourceArtifactId);
        }
        using (var proposals = await viewer.GetAsync(seed.Path("processing-graph-proposals?take=50")).ConfigureAwait(false))
        {
            using var json = JsonDocument.Parse(await proposals.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
            var items = json.RootElement.GetProperty("Items").EnumerateArray().ToArray();
            items.Should().HaveCount(2);
            items.Select(item => item.GetProperty("Proposal").GetProperty("LogicalCameraInstallationId").GetGuid())
                .Should().BeEquivalentTo(new[] { seed.RetiredInstallation, seed.CurrentInstallation });
            var staged = items.Single(item => item.GetProperty("TerminalFact").ValueKind == JsonValueKind.Object &&
                item.GetProperty("TerminalFact").GetProperty("Kind").GetString() == "Accepted");
            staged.GetProperty("DerivedStatus").GetProperty("Basis").GetString().Should().StartWith("derived:");
        }

        // A platform administrator who is not a member, a key scoped to another observatory, and an anonymous caller.
        using var admin = await ArtifactRetrievalTests.CreateUserClientAsync(TestUsers.Admin.Username, TestUsers.Admin.Password).ConfigureAwait(false);
        using var otherScope = await CreateScopedKeyClientAsync(null).ConfigureAwait(false);
        foreach (var client in new[] { admin, otherScope })
        {
            foreach (var path in seed.CollectionPaths())
            {
                using var response = await client.GetAsync(path).ConfigureAwait(false);
                response.StatusCode.Should().Be(HttpStatusCode.OK, path.ToString());
                using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
                json.RootElement.GetProperty("Items").GetArrayLength().Should().Be(0, path.ToString());
                json.RootElement.GetProperty("NextCursor").ValueKind.Should().Be(JsonValueKind.Null);
            }
            foreach (var path in seed.ExactPaths())
            {
                using var response = await client.GetAsync(path).ConfigureAwait(false);
                response.StatusCode.Should().Be(HttpStatusCode.NotFound, path.ToString());
            }
        }
        using var anonymous = AssemblyHooks.Fixture.Factory.CreateClient();
        using (var response = await anonymous.GetAsync(seed.Path("processing-graph-executions")).ConfigureAwait(false))
        {
            response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        }
    }

    [TestMethod]
    public async Task Http_ContinuationsPageExactlyAndRejectReuseOutsideTheirScope()
    {
        var seed = await CameraOperationsSeed.CreateAsync(captures: 8, jobEvery: 1, executions: 4, proposals: 3).ConfigureAwait(false);
        using var owner = await ArtifactRetrievalTests.CreateUserClientAsync(TestUsers.Operator.Username, TestUsers.Operator.Password).ConfigureAwait(false);
        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var query = cursor is null ? "take=1" : $"take=1&cursor={Uri.EscapeDataString(cursor)}";
            using var response = await owner.GetAsync(seed.Path($"processing-graph-executions?{query}")).ConfigureAwait(false);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
            seen.AddRange(json.RootElement.GetProperty("Items").EnumerateArray().Select(item => item.GetProperty("Id").GetGuid()));
            cursor = json.RootElement.GetProperty("NextCursor").GetString();
            if (seen.Count == 1)
            {
                // Reusing the continuation for another collection, filter or camera is rejected, never reinterpreted.
                foreach (var other in new[]
                {
                    seed.Path($"processing-graph-proposals?take=1&cursor={Uri.EscapeDataString(cursor!)}"),
                    seed.Path($"processing-graph-assignments?targetHost=Edge&take=1&cursor={Uri.EscapeDataString(cursor!)}"),
                    seed.Path($"processing-graph-executions?take=1&cursor={Uri.EscapeDataString(cursor!)}", seed.OtherCamera)
                })
                {
                    using var rejected = await owner.GetAsync(other).ConfigureAwait(false);
                    rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest, other.ToString());
                }
            }
        }
        while (cursor is not null);
        seen.Should().Equal(seed.Executions);
        foreach (var path in new[]
        {
            seed.Path("processing-graph-executions?take=0"),
            seed.Path("jobs?take=51"),
            seed.Path("processing-graph-assignments"),
            seed.Path("processing-graph-assignments?targetHost=central"),
            seed.Path("transient-events?cursor=not-a-continuation")
        })
        {
            using var response = await owner.GetAsync(path).ConfigureAwait(false);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, path.ToString());
        }
        var jobs = new List<Guid>();
        cursor = null;
        do
        {
            var query = cursor is null ? "take=2" : $"take=2&cursor={Uri.EscapeDataString(cursor)}";
            using var response = await owner.GetAsync(seed.Path($"jobs?{query}")).ConfigureAwait(false);
            using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
            jobs.AddRange(json.RootElement.GetProperty("Items").EnumerateArray().Select(item => item.GetProperty("JobId").GetGuid()));
            cursor = json.RootElement.GetProperty("NextCursor").GetString();
        }
        while (cursor is not null);
        jobs.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(seed.Jobs);
    }

    internal static async Task<HttpClient> CreateScopedKeyClientAsync(Guid? observatory)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var owner = await db.Users.SingleAsync(item => item.Email == TestUsers.Operator.Email).ConfigureAwait(false);
        if (observatory is null)
        {
            var dedicated = new Observatory
            {
                OwnerUserId = owner.Id, Name = $"Camera-ops-key-{Guid.NewGuid():N}", TimeZoneId = "UTC",
                CreatedAtUtc = DateTimeOffset.UtcNow, IsActive = true
            };
            db.Add(dedicated);
            db.ObservatoryMemberships.Add(new ObservatoryMembership
            {
                ObservatoryId = dedicated.Id, UserId = owner.Id, Role = ObservatoryMembershipRole.Owner, AddedAtUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync().ConfigureAwait(false);
            observatory = dedicated.Id;
        }
        var key = await scope.ServiceProvider.GetRequiredService<IApiKeyLifecycleService>().CreateAsync(
            owner.Id, owner.Email!, $"camera-ops-{Guid.NewGuid():N}", ApiKeyAccessLevel.Read, observatory,
            DateTimeOffset.UtcNow.AddMinutes(10), CancellationToken.None).ConfigureAwait(false);
        var client = AssemblyHooks.Fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", key.PlaintextKey);
        return client;
    }
}

/// <summary>
/// Measures bounded camera history reads against a large retained history on SQL Server. Run on demand with
/// <c>--filter TestCategory=Manual&amp;FullyQualifiedName~CameraOperationsReadPerformanceTests</c>.
/// </summary>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class CameraOperationsReadPerformanceTests
{
    private static readonly JsonSerializerOptions EvidenceJson = new() { WriteIndented = true };

    [TestMethod]
    [Timeout(1_200_000)]
    public async Task SqlServer_CameraHistoriesReadBoundedRowsOnLargeRetainedHistory()
    {
        // Two retained installations of the measured camera plus an unrelated camera of the same observatory with
        // the same volume, so a full-history or observatory-wide scan would show up as rows read.
        var seed = await CameraOperationsSeed.CreateAsync(captures: 3_000, jobEvery: 10, executions: 1_000,
            proposals: 1_000, unrelatedVolume: true, facts: 120).ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var connectionString = services.GetRequiredService<ApplicationDbContext>().Database.GetConnectionString()!;
        var capture = new QueryCapture();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connectionString).AddInterceptors(capture).Options);
        var service = new CameraOperationsReadService(db, services.GetRequiredService<IDataProtectionProvider>());
        var principal = CameraOperationsSeed.Principal(seed.OwnerId);
        var deepExecutions = await service.ListExecutionsAsync(principal, seed.Observatory, seed.Camera, 50, null).ConfigureAwait(false);
        for (var page = 0; page < 10; page++)
            deepExecutions = await service.ListExecutionsAsync(principal, seed.Observatory, seed.Camera, 50, deepExecutions.NextCursor).ConfigureAwait(false);
        var deepJobs = await service.ListJobsAsync(principal, seed.Observatory, seed.Camera, 50, null).ConfigureAwait(false);
        for (var page = 0; page < 5; page++)
            deepJobs = await service.ListJobsAsync(principal, seed.Observatory, seed.Camera, 50, deepJobs.NextCursor).ConfigureAwait(false);
        var requests = new Dictionary<string, (Func<Task<object?>> Run, int MaxCommands, long MaxRowsRead)>
        {
            ["assignments"] = (async () => await service.ListAssignmentsAsync(principal, seed.Observatory, seed.Camera,
                CentralProcessingGraphTargetHost.Central, 50, null).ConfigureAwait(false), 3, 2_000),
            ["executions-first"] = (async () => await service.ListExecutionsAsync(principal, seed.Observatory, seed.Camera, 50, null).ConfigureAwait(false), 4, 2_000),
            ["executions-deep-keyset"] = (async () => await service.ListExecutionsAsync(principal, seed.Observatory, seed.Camera, 50, deepExecutions.NextCursor).ConfigureAwait(false), 4, 2_000),
            ["execution-inspector"] = (async () => await service.GetExecutionAsync(principal, seed.Observatory, seed.Camera, seed.InspectedExecution, 50, null).ConfigureAwait(false), 6, 2_000),
            ["jobs-first"] = (async () => await service.ListJobsAsync(principal, seed.Observatory, seed.Camera, 50, null).ConfigureAwait(false), 6, 4_000),
            ["jobs-deep-keyset"] = (async () => await service.ListJobsAsync(principal, seed.Observatory, seed.Camera, 50, deepJobs.NextCursor).ConfigureAwait(false), 6, 4_000),
            ["job-inspector"] = (async () => await service.GetJobAsync(principal, seed.Observatory, seed.Camera, seed.InspectedJob, 50).ConfigureAwait(false), 6, 2_000),
            ["proposals-first"] = (async () => await service.ListProposalsAsync(principal, seed.Observatory, seed.Camera, 50, null).ConfigureAwait(false), 4, 4_000),
            ["proposal-facts"] = (async () => await service.ListProposalFactsAsync(principal, seed.Observatory, seed.Camera, seed.FactProposal, 50, null).ConfigureAwait(false), 4, 2_000),
            ["event-associations"] = (async () => await service.ListEventAssociationsAsync(principal, seed.Observatory, seed.Camera, 50, null).ConfigureAwait(false), 6, 4_000)
        };
        var evidence = new List<object>();
        foreach (var (name, (run, maxCommands, maxRowsRead)) in requests)
        {
            capture.Enabled = false;
            for (var warm = 0; warm < 3; warm++) _ = await run().ConfigureAwait(false);
            var times = new List<double>();
            var bytes = 0;
            for (var index = 0; index < 20; index++)
            {
                var start = Stopwatch.GetTimestamp();
                var result = await run().ConfigureAwait(false);
                bytes = JsonSerializer.SerializeToUtf8Bytes(result).Length;
                times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            capture.Commands.Clear();
            capture.Enabled = true;
            _ = await run().ConfigureAwait(false);
            capture.Enabled = false;
            var plans = new List<Plan>();
            foreach (var command in capture.Commands) plans.Add(await ExplainAsync(connectionString, command).ConfigureAwait(false));
            capture.Commands.Count.Should().BeLessThanOrEqualTo(maxCommands, name);
            plans.Sum(plan => plan.ActualRowsRead).Should().BeLessThanOrEqualTo(maxRowsRead, name);
            bytes.Should().BeLessThan(256 * 1024, name);
            times.Sort();
            evidence.Add(new
            {
                Name = name,
                MedianMs = times[10],
                P95Ms = times[18],
                MaxMs = times[^1],
                ResponseBytes = bytes,
                SqlCommands = capture.Commands.Count,
                ActualRowsRead = plans.Sum(plan => plan.ActualRowsRead),
                LogicalReads = plans.Sum(plan => plan.LogicalReads),
                Plans = plans
            });
        }
        var path = Environment.GetEnvironmentVariable("HVO_ISSUE1237_EVIDENCE") ?? "TestResults/issue-1237-performance.json";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            Source = Environment.GetEnvironmentVariable("HVO_ISSUE1237_REVISION"),
            Utc = DateTimeOffset.UtcNow,
            Workload = "Measured camera: 2 retained installations, 6,000 captures, 600 source jobs (3 per tenth capture), " +
                "2,000 executions, 2,000 proposals, 120 facts on one proposal; an unrelated camera of the same observatory " +
                "holds the same volume. take=50; deep keyset after 10 execution and 5 job pages.",
            Method = "3 warmup + 20 sequential service/serialization operations; SQL plans and rows read from replay of the " +
                "exact parameterized production commands with SET STATISTICS XML/IO",
            Browser = "N/A: backend prerequisite, no UI changes",
            Results = evidence
        }, EvidenceJson)).ConfigureAwait(false);
    }

    private static async Task<Plan> ExplainAsync(string connectionString, Command command)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        var messages = new StringBuilder();
        connection.InfoMessage += (_, args) => messages.AppendLine(args.Message);
        await using (var settings = connection.CreateCommand())
        {
            settings.CommandText = "SET STATISTICS XML ON; SET STATISTICS IO ON;";
            await settings.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        await using var replay = connection.CreateCommand();
#pragma warning disable CA2100 // Captured EF parameterized production command, never user SQL.
        replay.CommandText = command.Text;
#pragma warning restore CA2100
        replay.Parameters.AddRange(command.Parameters.Select(Copy).ToArray());
        await using var reader = await replay.ExecuteReaderAsync().ConfigureAwait(false);
        var rows = 0;
        var first = true;
        var plans = new List<XDocument>();
        do
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (first) rows++;
                if (!first && reader.FieldCount == 1 && reader.GetValue(0) is string xml && xml.Contains("ShowPlanXML", StringComparison.Ordinal))
                    plans.Add(XDocument.Parse(xml));
            }
            first = false;
        } while (await reader.NextResultAsync().ConfigureAwait(false));
        var indexes = plans.SelectMany(plan => plan.Descendants().Where(item => item.Name.LocalName == "Object")
            .Select(item => ((string?)item.Attribute("Index") ?? "").Trim('[', ']'))).Where(item => item.Length > 0).Distinct().ToArray();
        var reads = Regex.Matches(messages.ToString(), "logical reads ([0-9]+)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            .Sum(match => long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        var accessedRows = plans.SelectMany(plan => plan.Descendants().Where(item => item.Name.LocalName == "RunTimeCountersPerThread"))
            .Sum(item => (long?)item.Attribute("ActualRowsRead") ?? 0);
        return new(rows, accessedRows, reads, indexes);
    }

    private static SqlParameter Copy(SqlParameter item) => new(item.ParameterName, item.SqlDbType, item.Size)
    { Value = item.Value, Precision = item.Precision, Scale = item.Scale };

    private sealed record Plan(int ResultRows, long ActualRowsRead, long LogicalReads, string[] Indexes);

    private sealed record Command(string Text, SqlParameter[] Parameters);

    private sealed class QueryCapture : DbCommandInterceptor
    {
        internal bool Enabled { get; set; }
        internal List<Command> Commands { get; } = [];

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Enabled) Commands.Add(new(command.CommandText, command.Parameters.Cast<SqlParameter>().Select(Copy).ToArray()));
            return ValueTask.FromResult(result);
        }
    }
}

/// <summary>
/// A camera with a retired and a replacement installation, captures alternating between them, non-graph source
/// jobs with attempts and a resolved input, pending replay executions with graph node jobs, proposals with stored
/// facts, and an unrelated camera of the same observatory. Rows are written directly within the store's invariants.
/// </summary>
internal sealed class CameraOperationsSeed
{
    private CameraOperationsSeed() { }

    internal Guid Observatory { get; private set; }
    internal Guid Camera { get; private set; }
    internal Guid OtherCamera { get; private set; }
    internal string OwnerId { get; private set; } = string.Empty;
    internal Guid RetiredInstallation { get; private set; }
    internal Guid CurrentInstallation { get; private set; }
    internal Guid InspectedExecution { get; private set; }
    internal Guid InspectedJob { get; private set; }
    internal Guid InspectedJobSourceArtifactId { get; private set; }
    internal Guid FactProposal { get; private set; }
    internal Guid LeaseToken { get; } = Guid.NewGuid();
    internal List<Guid> Executions { get; } = [];
    internal List<Guid> Jobs { get; } = [];

    internal Uri Path(string relative, Guid? camera = null)
        => new($"/api/v1.0/observatories/{Observatory:D}/cameras/{camera ?? Camera:D}/{relative}", UriKind.Relative);

    internal IEnumerable<Uri> CollectionPaths() =>
    [
        Path("processing-graph-assignments?targetHost=Central"),
        Path("processing-graph-executions"),
        Path("jobs"),
        Path("processing-graph-proposals"),
        Path("transient-events")
    ];

    internal IEnumerable<Uri> ExactPaths() =>
    [
        Path($"processing-graph-executions/{InspectedExecution:D}"),
        Path($"jobs/{InspectedJob:D}"),
        Path($"jobs/{InspectedJob:D}/attempts"),
        Path($"jobs/{InspectedJob:D}/inputs"),
        Path($"jobs/{InspectedJob:D}/outputs"),
        Path($"processing-graph-proposals/{FactProposal:D}/facts")
    ];

    internal static System.Security.Claims.ClaimsPrincipal Principal(string userId)
        => new(new System.Security.Claims.ClaimsIdentity(
            [new(System.Security.Claims.ClaimTypes.NameIdentifier, userId), new("account_type", "User")],
            Microsoft.AspNetCore.Identity.IdentityConstants.ApplicationScheme));

    internal async Task AddMemberAsync(string email, ObservatoryMembershipRole role)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.SingleAsync(item => item.Email == email).ConfigureAwait(false);
        db.Add(new ObservatoryMembership { UserId = user.Id, ObservatoryId = Observatory, Role = role, AddedAtUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    internal static async Task<CameraOperationsSeed> CreateAsync(
        int captures, int jobEvery, int executions, int proposals, bool unrelatedVolume = false, int facts = 2)
    {
        var seed = new CameraOperationsSeed();
        var first = await ArtifactRetrievalTests.SeedArtifactAsync(TestUsers.Operator.Email, [1], putObject: false).ConfigureAwait(false);
        var second = await ArtifactRetrievalTests.SeedArtifactAsync(TestUsers.Operator.Email, [2], putObject: false).ConfigureAwait(false);
        var third = await ArtifactRetrievalTests.SeedArtifactAsync(TestUsers.Operator.Email, [3], putObject: false).ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var registrations = new List<DeviceRegistration>();
        foreach (var artifact in new[] { first, second, third })
            registrations.Add(await db.DeviceRegistrations.SingleAsync(item => item.DevicePublicId == artifact.DevicePublicId).ConfigureAwait(false));
        var observatoryId = registrations[0].ObservatoryId;
        foreach (var registration in registrations.Skip(1)) registration.ObservatoryId = observatoryId;
        var now = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var ownerId = registrations[0].OwnerUserId;
        var camera = NewCamera(observatoryId, ownerId, now);
        var other = NewCamera(observatoryId, ownerId, now);
        var retired = NewInstallation(camera, registrations[0], ownerId, now.AddDays(-30), now.AddDays(-15), null);
        var current = NewInstallation(camera, registrations[1], ownerId, now.AddDays(-15), null, retired.Id);
        var otherInstallation = NewInstallation(other, registrations[2], ownerId, now.AddDays(-30), null, null);
        var revision = await db.CentralProcessingGraphRevisions.AsNoTracking().FirstAsync().ConfigureAwait(false);
        db.AddRange(camera, other, retired, current, otherInstallation);
        foreach (var (assignmentScope, cameraId, host) in new (CentralProcessingGraphAssignmentScope, Guid?, CentralProcessingGraphTargetHost)[]
        {
            (CentralProcessingGraphAssignmentScope.LogicalCamera, camera.Id, CentralProcessingGraphTargetHost.Central),
            (CentralProcessingGraphAssignmentScope.LogicalCamera, camera.Id, CentralProcessingGraphTargetHost.Edge),
            (CentralProcessingGraphAssignmentScope.Observatory, null, CentralProcessingGraphTargetHost.Central),
            (CentralProcessingGraphAssignmentScope.LogicalCamera, other.Id, CentralProcessingGraphTargetHost.Central)
        })
        {
            db.Add(new CentralProcessingGraphAssignment
            {
                RevisionId = revision.Id, TargetHost = host, Scope = assignmentScope, ObservatoryId = observatoryId,
                LogicalCameraId = cameraId, EffectiveFromUtc = now.AddDays(-30), CreatedAtUtc = now.AddDays(-30),
                ActorUserId = ownerId, ReasonCode = "camera-operations"
            });
        }
        await db.SaveChangesAsync().ConfigureAwait(false);
        var assignment = await db.CentralProcessingGraphAssignments.AsNoTracking()
            .FirstAsync(item => item.LogicalCameraId == camera.Id && item.TargetHost == CentralProcessingGraphTargetHost.Edge)
            .ConfigureAwait(false);

        var parsed = ProcessingGraphJson.Parse(Encoding.UTF8.GetBytes(revision.DefinitionJson)).Definition!;
        var plan = ProcessingGraphCompiler.Compile(parsed, new(ProcessingGraphHosts.LogicHost, [])).Plan!;
        var anchors = new List<CentralArtifact>();
        foreach (var (target, targetInstallation, count) in unrelatedVolume
            ? new[] { (camera, (retired, current), captures), (other, (otherInstallation, otherInstallation), captures) }
            : new[] { (camera, (retired, current), captures) })
        {
            for (var index = 0; index < count; index++)
            {
                var installation = index % 2 == 0 ? targetInstallation.Item1 : targetInstallation.Item2;
                var registration = registrations.Single(item => item.Id == installation.RegistrationId);
                // Equal capture times in pairs exercise the identity tie-breaker.
                var frame = NewFrame(installation, registration, now.AddMinutes(-(index / 2)));
                var raw = NewArtifact(frame, registration);
                db.AddRange(frame, raw);
                if (target == camera && index == 0) anchors.Add(raw);
                if (index % jobEvery == 0)
                {
                    for (var jobIndex = 0; jobIndex < (unrelatedVolume ? 3 : 1); jobIndex++)
                    {
                        var job = NewJob(raw, frame.CapturedAtUtc.AddSeconds(jobIndex));
                        db.Add(job);
                        if (target == camera) seed.Jobs.Add(job.Id);
                        if (target == camera && index == 0 && jobIndex == 0)
                        {
                            seed.InspectedJob = job.Id;
                            seed.InspectedJobSourceArtifactId = raw.ArtifactId;
                            job.LeaseOwner = "lease-owner";
                            job.LeaseToken = seed.LeaseToken;
                            job.LeaseAcquiredAtUtc = now;
                            job.LeaseExpiresAtUtc = now.AddMinutes(5);
                            job.AttemptCount = 2;
                            for (var attempt = 1; attempt <= 2; attempt++)
                            {
                                db.Add(new CentralDerivativeJobAttempt
                                {
                                    CentralDerivativeJobId = job.Id, AttemptNumber = attempt, WorkerId = "worker",
                                    LeaseAcquiredAtUtc = now, LeaseExpiresAtUtc = now.AddMinutes(5),
                                    Outcome = CentralDerivativeAttemptOutcome.RetryableFailure, ReasonCode = "retry"
                                });
                            }
                            var requirement = new CentralDerivativeJobInputRequirement
                            {
                                CentralDerivativeJobId = job.Id, Ordinal = 0, BindingName = "primary",
                                SourceKind = CentralDerivativeInputSourceKind.Artifact, IsRequired = true,
                                ExpectedAgentId = frame.AgentId, ExpectedCentralArtifactId = raw.Id,
                                ResolutionState = CentralDerivativeInputResolutionState.Resolved, ResolvedAtUtc = now
                            };
                            db.Add(requirement);
                            db.Add(new CentralDerivativeJobInput
                            {
                                CentralDerivativeJobId = job.Id, CentralDerivativeJobInputRequirementId = requirement.Id,
                                Ordinal = 0, CentralArtifactId = raw.Id, CompatibilitySha256 = new string('0', 64),
                                ByteLength = raw.ByteLength, SelectedAtUtc = now
                            });
                        }
                    }
                }
                if (index % 500 == 499) await SaveAndClearAsync(db).ConfigureAwait(false);
            }
        }
        await SaveAndClearAsync(db).ConfigureAwait(false);
        var anchor = anchors.Single();
        foreach (var (target, installations) in unrelatedVolume
            ? new[] { (camera, new[] { retired, current }), (other, new[] { otherInstallation }) }
            : new[] { (camera, new[] { retired, current }) })
        {
            var anchorArtifact = target == camera
                ? anchor
                : await db.CentralArtifacts.AsNoTracking().FirstAsync(item => item.Frame!.LogicalCameraInstallationId == otherInstallation.Id).ConfigureAwait(false);
            for (var index = 0; index < executions; index++)
            {
                var installation = installations[index % installations.Length];
                var execution = new CentralProcessingGraphExecution
                {
                    ExecutionClass = CentralProcessingGraphExecutionClass.Replay,
                    Status = CentralProcessingGraphExecutionStatus.Pending,
                    RevisionId = revision.Id,
                    RequestIdentitySha256 = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                    DefinitionIdentitySha256 = revision.DefinitionIdentitySha256,
                    FrozenDefinitionJson = revision.DefinitionJson,
                    CentralPlanIdentitySha256 = plan.PlanIdentitySha256,
                    FrozenCentralPlanJson = LogicHostProcessingGraphAdapter.FreezePlan(plan),
                    ObservatoryId = observatoryId,
                    LogicalCameraId = target.Id,
                    LogicalCameraInstallationId = installation.Id,
                    InstallationPublicId = installation.InstallationPublicId,
                    AnchorSourceCentralArtifactId = anchorArtifact.Id,
                    AnchorSourceArtifactId = anchorArtifact.ArtifactId,
                    AnchorSourceChecksumSha256 = anchorArtifact.ChecksumSha256,
                    Trigger = CentralProcessingGraphTrigger.Replay,
                    ActorId = ownerId,
                    IdempotencyKey = Guid.NewGuid().ToString("N"),
                    ReasonCode = "camera-operations",
                    // Equal creation times in pairs exercise the identity tie-breaker.
                    CreatedAtUtc = now.AddSeconds(-(index / 2)),
                    UpdatedAtUtc = now.AddSeconds(-(index / 2))
                };
                db.Add(execution);
                if (target == camera)
                {
                    seed.Executions.Add(execution.Id);
                    if (index == 0)
                    {
                        seed.InspectedExecution = execution.Id;
                        for (var ordinal = 0; ordinal < 3; ordinal++)
                        {
                            var node = NewJob(anchor, execution.CreatedAtUtc);
                            node.GraphExecutionId = execution.Id;
                            node.GraphNodeId = $"node-{ordinal}";
                            node.GraphNodeOrdinal = ordinal;
                            node.SharedNodePlanIdentitySha256 = new string('a', 64);
                            node.FrozenNodePlanJson = "{}";
                            node.GraphFailurePolicy = ProcessingGraphNodeFailurePolicy.Required;
                            db.Add(node);
                            seed.Jobs.Add(node.Id);
                            db.Add(new CentralDerivativeJobOutput
                            {
                                CentralDerivativeJobId = node.Id, Ordinal = 0, Role = FrameArtifactRole.Preview, Variant = "default",
                                ProductKind = ProcessingProductKind.PixelData, ContractJson = "{}",
                                ContractIdentitySha256 = CentralDerivativeJobOutput.ComputeContractIdentitySha256("{}")
                            });
                        }
                    }
                }
                if (index % 500 == 499) await SaveAndClearAsync(db).ConfigureAwait(false);
            }
            await SaveAndClearAsync(db).ConfigureAwait(false);
            for (var index = 0; index < proposals; index++)
            {
                var installation = installations[index % installations.Length];
                var proposal = new CentralProcessingGraphDeliveryProposal
                {
                    AssignmentId = assignment.Id,
                    RevisionId = revision.Id,
                    RegistrationId = installation.RegistrationId,
                    LogicalCameraInstallationId = installation.Id,
                    InstallationPublicId = installation.InstallationPublicId,
                    CapabilitySnapshotSha256 = new string('a', 64),
                    IssuedAtUtc = now.AddSeconds(-(index / 2)),
                    ExpiresAtUtc = now.AddHours(1)
                };
                db.Add(proposal);
                var kind = index % 2 == 0 ? "Accepted" : "Superseded";
                db.Add(new CentralProcessingGraphDeliveryFact
                {
                    ProposalId = proposal.Id, Kind = kind, Source = kind == "Accepted" ? "CameraAgent" : "LogicHost",
                    OccurredAtUtc = proposal.IssuedAtUtc, RecordedAtUtc = proposal.IssuedAtUtc.AddSeconds(1),
                    ReasonCode = kind == "Accepted" ? null : "effective-delivery-tuple-changed"
                });
                if (target == camera && index == 0)
                {
                    seed.FactProposal = proposal.Id;
                    for (var fact = 0; fact < facts; fact++)
                    {
                        db.Add(new CentralProcessingGraphDeliveryFact
                        {
                            ProposalId = proposal.Id, Kind = "Retrieved", Source = "CameraAgent",
                            OccurredAtUtc = proposal.IssuedAtUtc, RecordedAtUtc = proposal.IssuedAtUtc
                        });
                    }
                }
                if (index % 500 == 499) await SaveAndClearAsync(db).ConfigureAwait(false);
            }
            await SaveAndClearAsync(db).ConfigureAwait(false);
        }
        seed.Observatory = observatoryId;
        seed.Camera = camera.Id;
        seed.OtherCamera = other.Id;
        seed.OwnerId = ownerId;
        seed.RetiredInstallation = retired.Id;
        seed.CurrentInstallation = current.Id;
        var ordered = await db.CentralProcessingGraphExecutions.AsNoTracking().Where(item => item.LogicalCameraId == camera.Id)
            .OrderByDescending(item => item.CreatedAtUtc).ThenByDescending(item => item.Id).Select(item => item.Id)
            .ToListAsync().ConfigureAwait(false);
        seed.Executions.Clear();
        seed.Executions.AddRange(ordered);
        return seed;
    }

    private static async Task SaveAndClearAsync(ApplicationDbContext db)
    {
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    private static LogicalCamera NewCamera(Guid observatoryId, string ownerId, DateTimeOffset now)
        => new()
        {
            ObservatoryId = observatoryId, Name = $"Operations camera {Guid.NewGuid():N}", Slug = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = now.AddDays(-60), CreatedByUserId = ownerId
        };

    private static LogicalCameraInstallation NewInstallation(
        LogicalCamera camera, DeviceRegistration registration, string ownerId, DateTimeOffset assigned,
        DateTimeOffset? retired, Guid? replaces)
        => new()
        {
            LogicalCameraId = camera.Id, RegistrationId = registration.Id, InstallationPublicId = Guid.NewGuid(),
            AssignedAtUtc = assigned, RetiredAtUtc = retired, ReplacesInstallationId = replaces,
            AssignedByUserId = ownerId, RetiredByUserId = retired is null ? null : ownerId,
            AssignmentReasonCode = replaces is null ? "initial" : "replacement",
            RetirementReasonCode = retired is null ? null : "replaced"
        };

    private static CentralFrame NewFrame(LogicalCameraInstallation installation, DeviceRegistration registration, DateTimeOffset capturedAt)
        => new()
        {
            RegistrationId = registration.Id, LogicalCameraInstallationId = installation.Id,
            DevicePublicId = registration.DevicePublicId!.Value, ObservatoryId = registration.ObservatoryId,
            AgentId = registration.DeviceId, FrameId = Guid.NewGuid(), CapturedAtUtc = capturedAt,
            FirstReceivedAtUtc = capturedAt, RigId = "rig-1"
        };

    private static CentralArtifact NewArtifact(CentralFrame frame, DeviceRegistration registration)
        => new()
        {
            CentralFrameId = frame.Id, DevicePublicId = registration.DevicePublicId!.Value, ArtifactId = Guid.NewGuid(),
            Role = FrameArtifactRole.Raw, RecipeVersion = "raw-v1", ManifestSchemaVersion = "v2",
            MediaType = "application/octet-stream", ByteLength = 1, ChecksumSha256 = new string('b', 64),
            StorageReference = $"object://skymonitor-artifacts/camera-ops-{Guid.NewGuid():N}",
            ReceivedAtUtc = frame.CapturedAtUtc, IdempotencyKey = Guid.NewGuid().ToString("N"),
            ObjectState = CentralArtifactObjectState.Available, ReconstructionState = CentralReconstructionState.Complete
        };

    private static CentralDerivativeJob NewJob(CentralArtifact source, DateTimeOffset createdAt)
        => new()
        {
            SourceCentralArtifactId = source.Id, TargetRole = FrameArtifactRole.Preview, TargetRecipeVersion = "preview-v1",
            TargetVariant = "default", RecipeName = "preview", RequestedRecipeIdentitySha256 = new string('1', 64),
            ExpectedRecipeIdentitySha256 = new string('1', 64),
            RequestIdentitySha256 = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            Status = CentralDerivativeJobStatus.Pending, MaxAttempts = 3, AvailableAtUtc = createdAt,
            CreatedAtUtc = createdAt, UpdatedAtUtc = createdAt
        };
}
