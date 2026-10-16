using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class ProcessingGraphRevisionReadTests
{
    private const string RevisionsPath = "/api/internal/processing-graphs/revisions";
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task HistoricalAndCurrentExactReadsUseOneIndexedRowAndExistingViewBeyondLatest100()
    {
        var rows = await SeedRevisionsAsync(105).ConfigureAwait(false);
        var probe = new RevisionProbe();
        using var factory = CreateFactory(probe);
        using var client = await CreateKeyClientAsync(factory, null, ApiKeyAccessLevel.Read).ConfigureAwait(false);
        using var listedResponse = await client.GetAsync(ApiPath(RevisionsPath)).ConfigureAwait(false);
        listedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var listed = JsonDocument.Parse(await listedResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
        listed.RootElement.GetArrayLength().Should().Be(100);
        listed.RootElement.EnumerateArray().Should().NotContain(item => item.GetProperty("Id").GetGuid() == rows[0].Id);
        using var tooMany = await client.GetAsync(ApiPath($"{RevisionsPath}?take=101")).ConfigureAwait(false);
        tooMany.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        foreach (var row in new[] { rows[0], rows[^1] })
        {
            probe.Enabled = true;
            probe.Commands.Clear();
            probe.Materialized = 0;
            using var response = await client.GetAsync(ApiPath($"{RevisionsPath}/{row.Id:D}")).ConfigureAwait(false);
            probe.Enabled = false;
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var payload = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            using var json = JsonDocument.Parse(payload);
            var view = json.RootElement;
            view.EnumerateObject().Select(item => item.Name).Should().BeEquivalentTo(
                "Id", "Name", "Revision", "Lifecycle", "DefinitionIdentitySha256",
                "PortablePlanIdentitySha256", "EdgePlanIdentitySha256", "CentralPlanIdentitySha256",
                "Definition", "CreatedAtUtc", "PublishedAtUtc", "RetiredAtUtc");
            view.GetProperty("Id").GetGuid().Should().Be(row.Id);
            view.GetProperty("Name").GetString().Should().Be(row.Name);
            view.GetProperty("Revision").GetString().Should().Be(row.Revision);
            view.GetProperty("Lifecycle").GetString().Should().Be(row.Lifecycle.ToString());
            view.GetProperty("DefinitionIdentitySha256").GetString().Should().Be(row.DefinitionIdentitySha256);
            view.GetProperty("PortablePlanIdentitySha256").GetString().Should().Be(row.PortablePlanIdentitySha256);
            view.GetProperty("CentralPlanIdentitySha256").GetString().Should().Be(row.CentralPlanIdentitySha256);
            view.GetProperty("EdgePlanIdentitySha256").ValueKind.Should().Be(JsonValueKind.Null);
            view.GetProperty("CreatedAtUtc").GetDateTimeOffset().Should().Be(row.CreatedAtUtc);
            view.GetProperty("PublishedAtUtc").GetDateTimeOffset().Should().Be(row.PublishedAtUtc);
            if (row.RetiredAtUtc is { } retired) view.GetProperty("RetiredAtUtc").GetDateTimeOffset().Should().Be(retired);
            else view.GetProperty("RetiredAtUtc").ValueKind.Should().Be(JsonValueKind.Null);
            using var definition = JsonDocument.Parse(row.DefinitionJson);
            JsonElement.DeepEquals(view.GetProperty("Definition"), definition.RootElement).Should().BeTrue();
            probe.Commands.Should().ContainSingle();
            probe.Materialized.Should().Be(1);
            var command = probe.Commands.Single();
            command.Sql.Should().Contain("TOP(2)").And.Contain("WHERE [c].[Id] =");
            command.Sql.Should().NotContain("JOIN").And.NotContain("ORDER BY").And.NotContain("OFFSET");
            command.Id.Should().Be(row.Id);
            payload.Length.Should().BeLessThan(32 * 1024);
            await AssertSeekReadsOneRowAsync(command).ConfigureAwait(false);
            TestContext.WriteLine($"seeded=105 revision={row.Id:D} lifecycle={row.Lifecycle} commands=1 materialized=1 responseBytes={payload.Length}");
            TestContext.WriteLine(command.Sql);
        }
    }

    [TestMethod]
    public async Task CredentialScopeKeepsDefinitionsGlobalPrivateStateDeniedAndMutationAuthorityUnchanged()
    {
        var revision = (await SeedRevisionsAsync(1).ConfigureAwait(false)).Single();
        var first = await ArtifactRetrievalTests.SeedArtifactAsync(TestUsers.Operator.Email, [1], putObject: false).ConfigureAwait(false);
        var second = await ArtifactRetrievalTests.SeedArtifactAsync(TestUsers.Operator.Email, [2], putObject: false).ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var frames = await db.CentralFrames.Where(item => item.DevicePublicId == first.DevicePublicId ||
            item.DevicePublicId == second.DevicePublicId).ToArrayAsync().ConfigureAwait(false);
        var observatoryA = frames.Single(item => item.DevicePublicId == first.DevicePublicId).ObservatoryId;
        var frame = frames.Single(item => item.DevicePublicId == second.DevicePublicId);
        var owner = await db.Users.SingleAsync(item => item.Email == TestUsers.Operator.Email).ConfigureAwait(false);
        var camera = new LogicalCamera
        {
            ObservatoryId = frame.ObservatoryId,
            Slug = $"exact-{Guid.NewGuid():N}",
            Name = "Private camera",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CreatedByUserId = owner.Id
        };
        var installation = new LogicalCameraInstallation
        {
            LogicalCamera = camera,
            LogicalCameraId = camera.Id,
            RegistrationId = frame.RegistrationId,
            InstallationPublicId = Guid.NewGuid(),
            AssignedAtUtc = DateTimeOffset.UtcNow,
            AssignedByUserId = owner.Id,
            AssignmentReasonCode = "exact-read-test"
        };
        var assignment = new CentralProcessingGraphAssignment
        {
            RevisionId = revision.Id,
            TargetHost = CentralProcessingGraphTargetHost.Central,
            Scope = CentralProcessingGraphAssignmentScope.LogicalCamera,
            ObservatoryId = frame.ObservatoryId,
            LogicalCamera = camera,
            LogicalCameraId = camera.Id,
            EffectiveFromUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ActorUserId = owner.Id,
            ReasonCode = "private-assignment"
        };
        var privateFrame = new CentralFrame
        {
            RegistrationId = frame.RegistrationId,
            DevicePublicId = frame.DevicePublicId,
            ObservatoryId = frame.ObservatoryId,
            AgentId = frame.AgentId,
            FrameId = Guid.NewGuid(),
            CapturedAtUtc = DateTimeOffset.UtcNow,
            FirstReceivedAtUtc = DateTimeOffset.UtcNow,
            RigId = "exact-read",
            LogicalCameraInstallation = installation,
            LogicalCameraInstallationId = installation.Id
        };
        var artifact = new CentralArtifact
        {
            Frame = privateFrame,
            CentralFrameId = privateFrame.Id,
            DevicePublicId = frame.DevicePublicId,
            ArtifactId = Guid.NewGuid(),
            Role = FrameArtifactRole.Raw,
            RecipeVersion = "raw-v1",
            ManifestSchemaVersion = "v2",
            MediaType = "application/octet-stream",
            ByteLength = 1,
            ChecksumSha256 = second.Checksum,
            StorageReference = $"object://skymonitor-artifacts/exact-{Guid.NewGuid():N}",
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete
        };
        var parsed = ProcessingGraphJson.Parse(Encoding.UTF8.GetBytes(revision.DefinitionJson)).Definition!;
        var plan = ProcessingGraphCompiler.Compile(parsed, new(ProcessingGraphHosts.LogicHost, [])).Plan!;
        var now = DateTimeOffset.UtcNow;
        // Pending, unexpanded durable execution: no jobs or worker leases are needed for this read-authority test.
        var execution = new CentralProcessingGraphExecution
        {
            ExecutionClass = CentralProcessingGraphExecutionClass.Replay,
            Status = CentralProcessingGraphExecutionStatus.Pending,
            RevisionId = revision.Id,
            RequestIdentitySha256 = revision.DefinitionIdentitySha256,
            DefinitionIdentitySha256 = revision.DefinitionIdentitySha256,
            FrozenDefinitionJson = revision.DefinitionJson,
            CentralPlanIdentitySha256 = plan.PlanIdentitySha256,
            FrozenCentralPlanJson = LogicHostProcessingGraphAdapter.FreezePlan(plan),
            ObservatoryId = frame.ObservatoryId,
            LogicalCamera = camera,
            LogicalCameraId = camera.Id,
            LogicalCameraInstallation = installation,
            LogicalCameraInstallationId = installation.Id,
            InstallationPublicId = installation.InstallationPublicId,
            AnchorSourceArtifact = artifact,
            AnchorSourceCentralArtifactId = artifact.Id,
            AnchorSourceArtifactId = artifact.ArtifactId,
            AnchorSourceChecksumSha256 = artifact.ChecksumSha256,
            Trigger = CentralProcessingGraphTrigger.Replay,
            ActorId = owner.Id,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            ReasonCode = "private-execution",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.AddRange(camera, installation, assignment, privateFrame, artifact, execution);
        await db.SaveChangesAsync().ConfigureAwait(false);

        using var scoped = await CreateKeyClientAsync(AssemblyHooks.Fixture.Factory, observatoryA, ApiKeyAccessLevel.Read).ConfigureAwait(false);
        using var outsider = await ArtifactRetrievalTests.CreateUserClientAsync(TestUsers.Viewer.Username, TestUsers.Viewer.Password).ConfigureAwait(false);
        using var ownerClient = await ArtifactRetrievalTests.CreateUserClientAsync(TestUsers.Operator.Username, TestUsers.Operator.Password).ConfigureAwait(false);
        using var ownerAssignment = await ownerClient.GetAsync(AssignmentPath(frame.ObservatoryId, camera.Id)).ConfigureAwait(false);
        ownerAssignment.StatusCode.Should().Be(HttpStatusCode.OK);
        using var ownerExecution = await ownerClient.GetAsync(ApiPath($"/api/v1.0/processing-graph-executions/{execution.Id:D}")).ConfigureAwait(false);
        ownerExecution.StatusCode.Should().Be(HttpStatusCode.OK);
        foreach (var client in new[] { scoped, outsider })
        {
            using var definitionRead = await client.GetAsync(ApiPath($"{RevisionsPath}/{revision.Id:D}")).ConfigureAwait(false);
            definitionRead.StatusCode.Should().Be(HttpStatusCode.OK);
            var response = await definitionRead.Content.ReadAsStringAsync().ConfigureAwait(false);
            foreach (var privateId in new[] { assignment.Id, execution.Id, frame.ObservatoryId, camera.Id, installation.InstallationPublicId })
                response.Should().NotContain(privateId.ToString("D"));
            using var privateAssignment = await client.GetAsync(AssignmentPath(frame.ObservatoryId, camera.Id)).ConfigureAwait(false);
            privateAssignment.StatusCode.Should().Be(HttpStatusCode.NotFound);
            using var privateExecution = await client.GetAsync(ApiPath($"/api/v1.0/processing-graph-executions/{execution.Id:D}")).ConfigureAwait(false);
            privateExecution.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        using var definitionDocument = JsonDocument.Parse(revision.DefinitionJson);
        using var writeKey = await CreateKeyClientAsync(AssemblyHooks.Fixture.Factory, null, ApiKeyAccessLevel.ReadWrite).ConfigureAwait(false);
        // Preserve the existing multi-scheme policy's 401 write denial for a read-only API key.
        foreach (var (client, expected) in new[] { (scoped, HttpStatusCode.Unauthorized), (writeKey, HttpStatusCode.NotFound) })
        {
            using var create = await client.PostAsJsonAsync(RevisionsPath, definitionDocument.RootElement).ConfigureAwait(false);
            using var publish = await client.PostAsync(ApiPath($"{RevisionsPath}/{revision.Id:D}/publish"), null).ConfigureAwait(false);
            using var retire = await client.PostAsJsonAsync($"{RevisionsPath}/{revision.Id:D}/retire", new { ReasonCode = "should-not-retire" }).ConfigureAwait(false);
            create.StatusCode.Should().Be(expected);
            publish.StatusCode.Should().Be(expected);
            retire.StatusCode.Should().Be(expected);
        }
        (await db.CentralProcessingGraphRevisions.AsNoTracking().SingleAsync(item => item.Id == revision.Id).ConfigureAwait(false))
            .Lifecycle.Should().Be(CentralProcessingGraphLifecycle.Published);
    }

    [TestMethod]
    public async Task MissingMalformedInvalidCredentialsAndRequestCancellationPreserveExistingOutcomes()
    {
        var revision = (await SeedRevisionsAsync(1).ConfigureAwait(false)).Single();
        var probe = new RevisionProbe();
        using var factory = CreateFactory(probe);
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var invalid = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        invalid.DefaultRequestHeaders.Add("X-API-Key", "invalid-exact-read-key");
        foreach (var client in new[] { anonymous, invalid })
        {
            using var list = await client.GetAsync(ApiPath(RevisionsPath)).ConfigureAwait(false);
            using var exact = await client.GetAsync(ApiPath($"{RevisionsPath}/{revision.Id:D}")).ConfigureAwait(false);
            exact.StatusCode.Should().Be(list.StatusCode).And.Be(HttpStatusCode.Unauthorized);
        }
        using var authenticated = await CreateKeyClientAsync(factory, null, ApiKeyAccessLevel.Read).ConfigureAwait(false);
        foreach (var id in new[] { Guid.NewGuid().ToString("D"), Guid.Empty.ToString("D"), "not-a-guid" })
        {
            using var missing = await authenticated.GetAsync(ApiPath($"{RevisionsPath}/{id}")).ConfigureAwait(false);
            missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await missing.Content.ReadAsStringAsync().ConfigureAwait(false)).Should().NotContain(revision.Name);
        }
        probe.Enabled = true;
        probe.WaitForCancellation = true;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = authenticated.GetAsync(ApiPath($"{RevisionsPath}/{revision.Id:D}"), cancellation.Token);
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await cancellation.CancelAsync().ConfigureAwait(false);
        await ((Func<Task>)(async () => { using var response = await pending.ConfigureAwait(false); }))
            .Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
        await probe.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    private static Uri ApiPath(string path) => new(path, UriKind.Relative);

    private static Uri AssignmentPath(Guid observatory, Guid camera)
        => ApiPath($"/api/internal/processing-graphs/assignments/effective?targetHost=Central&observatoryId={observatory:D}&logicalCameraId={camera:D}");

    private static WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> CreateFactory(RevisionProbe probe)
        => AssemblyHooks.Fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddDbContext<ApplicationDbContext>(options => options.AddInterceptors(probe))));

    private static async Task<HttpClient> CreateKeyClientAsync(
        WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory, Guid? observatory, ApiKeyAccessLevel access)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var owner = await db.Users.SingleAsync(item => item.Email == TestUsers.Operator.Email).ConfigureAwait(false);
        if (observatory is null)
        {
            var dedicated = new Observatory
            {
                OwnerUserId = owner.Id,
                Name = $"Exact-key-{Guid.NewGuid():N}",
                TimeZoneId = "UTC",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                IsActive = true
            };
            db.Add(dedicated);
            db.ObservatoryMemberships.Add(new ObservatoryMembership
            {
                Observatory = dedicated,
                ObservatoryId = dedicated.Id,
                UserId = owner.Id,
                Role = ObservatoryMembershipRole.Owner,
                AddedAtUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync().ConfigureAwait(false);
            observatory = dedicated.Id;
        }
        var key = await scope.ServiceProvider.GetRequiredService<IApiKeyLifecycleService>().CreateAsync(
            owner.Id, owner.Email!, $"exact-{Guid.NewGuid():N}", access, observatory,
            DateTimeOffset.UtcNow.AddMinutes(10), CancellationToken.None).ConfigureAwait(false);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", key.PlaintextKey);
        return client;
    }

    private static async Task<CentralProcessingGraphRevision[]> SeedRevisionsAsync(int count)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var owner = await db.Users.SingleAsync(item => item.Email == TestUsers.Operator.Email).ConfigureAwait(false);
        var name = $"exact-read-{Guid.NewGuid():N}";
        var now = new DateTimeOffset(2025, 10, 15, 12, 0, 0, TimeSpan.Zero);
        var rows = Enumerable.Range(0, count).Select(index =>
        {
            var definition = DatabaseSeeder.CreateBasicCentralProcessingGraph() with { Name = name, Revision = index.ToString(CultureInfo.InvariantCulture) };
            var portable = ProcessingGraphCompiler.Compile(definition).Plan!;
            var central = ProcessingGraphCompiler.Compile(definition, new(ProcessingGraphHosts.LogicHost, [])).Plan!;
            return new CentralProcessingGraphRevision
            {
                Name = definition.Name,
                Revision = definition.Revision,
                DefinitionJson = Encoding.UTF8.GetString(ProcessingGraphJson.SerializeCanonical(definition)),
                DefinitionIdentitySha256 = portable.DefinitionIdentitySha256,
                PortablePlanIdentitySha256 = portable.PlanIdentitySha256,
                CentralPlanIdentitySha256 = central.PlanIdentitySha256,
                CreatedAtUtc = now.AddMinutes(index),
                CreatedByUserId = owner.Id,
                PublishedAtUtc = now.AddMinutes(index).AddSeconds(1),
                PublishedByUserId = owner.Id,
                RetiredAtUtc = count > 1 && index == 0 ? now.AddMinutes(1) : null,
                RetiredByUserId = count > 1 && index == 0 ? owner.Id : null,
                RetirementReasonCode = count > 1 && index == 0 ? "historical" : null
            };
        }).ToArray();
        db.AddRange(rows);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return rows;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Replays EF-generated SELECT SQL captured by this test, with its original parameterized GUID; no caller SQL is accepted.")]
    private async Task AssertSeekReadsOneRowAsync(CapturedCommand captured)
    {
        await using var connection = new SqlConnection(AssemblyHooks.Fixture.SqlServerConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SET STATISTICS XML ON; {captured.Sql}; SET STATISTICS XML OFF;";
        command.Parameters.AddWithValue(captured.Parameter, captured.Id);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var rows = 0;
        while (await reader.ReadAsync().ConfigureAwait(false)) rows++;
        rows.Should().Be(1);
        (await reader.NextResultAsync().ConfigureAwait(false)).Should().BeTrue();
        (await reader.ReadAsync().ConfigureAwait(false)).Should().BeTrue();
        var plan = XDocument.Parse(reader.GetString(0));
        XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
        var seek = plan.Descendants(ns + "RelOp").Single(item =>
            ((string?)item.Attribute("PhysicalOp"))?.Contains("Index Seek", StringComparison.Ordinal) == true);
        seek.Descendants(ns + "Object").Single().Attribute("Index")!.Value.Should().Be("[PK_CentralProcessingGraphRevisions]");
        seek.Descendants(ns + "RunTimeCountersPerThread").Sum(item =>
            double.Parse(item.Attribute("ActualRowsRead")!.Value, CultureInfo.InvariantCulture)).Should().Be(1);
        TestContext.WriteLine("actual SQL plan: PK_CentralProcessingGraphRevisions Index Seek; ActualRowsRead=1; returnedRows=1");
    }

    private sealed record CapturedCommand(string Sql, string Parameter, Guid Id);

    private sealed class RevisionProbe : DbCommandInterceptor, IMaterializationInterceptor
    {
        public bool Enabled { get; set; }
        public bool WaitForCancellation { get; set; }
        public int Materialized;
        public ConcurrentQueue<CapturedCommand> Commands { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public object InitializedInstance(MaterializationInterceptionData data, object entity)
        {
            if (Enabled && entity is CentralProcessingGraphRevision) Interlocked.Increment(ref Materialized);
            return entity;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.Contains("FROM [CentralProcessingGraphRevisions]", StringComparison.Ordinal))
            {
                var parameter = command.Parameters.Cast<DbParameter>().Single(item => item.Value is Guid);
                Commands.Enqueue(new(command.CommandText, parameter.ParameterName, (Guid)parameter.Value!));
                if (WaitForCancellation)
                {
                    Started.TrySetResult();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
                }
            }
            return result;
        }
    }
}
