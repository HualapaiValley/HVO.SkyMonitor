using System.Collections.Concurrent;
using System.Data.Common;
using System.Data.SqlTypes;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Identity;
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
public sealed class ProcessingGraphProposalReadTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task Http_CurrentRolesReadExactStoredFieldsForReplacedInactiveInstallation(int role)
    {
        var seed = await SeedAsync().ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var viewer = await db.Users.SingleAsync(user => user.Email == TestUsers.Viewer.Email).ConfigureAwait(false);
        db.Add(new ObservatoryMembership
        {
            UserId = viewer.Id,
            ObservatoryId = seed.ObservatoryId,
            Role = (ObservatoryMembershipRole)role,
            AddedAtUtc = seed.Now
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
        using var client = await ArtifactRetrievalTests.CreateUserClientAsync(TestUsers.Viewer.Username, TestUsers.Viewer.Password).ConfigureAwait(false);
        using var response = await client.GetAsync(Path(seed)).ConfigureAwait(false);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.CacheControl.Private.Should().BeTrue();
        var payload = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        using var json = JsonDocument.Parse(payload);
        json.RootElement.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("Proposal", "Facts", "FactsAreTruncated");
        var view = json.RootElement.GetProperty("Proposal");
        view.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            "Id", "AssignmentId", "RevisionId", "RegistrationId", "LogicalCameraInstallationId", "InstallationPublicId",
            "ExpectedActiveLocalRevisionId", "CapabilitySnapshotSha256", "IssuedAtUtc", "ExpiresAtUtc");
        view.GetProperty("Id").GetGuid().Should().Be(seed.Proposal.Id);
        view.GetProperty("AssignmentId").GetGuid().Should().Be(seed.Proposal.AssignmentId);
        view.GetProperty("RevisionId").GetGuid().Should().Be(seed.Proposal.RevisionId);
        view.GetProperty("RegistrationId").GetGuid().Should().Be(seed.Proposal.RegistrationId);
        view.GetProperty("LogicalCameraInstallationId").GetGuid().Should().Be(seed.Installation.Id);
        view.GetProperty("InstallationPublicId").GetGuid().Should().Be(seed.Installation.InstallationPublicId).And.NotBe(seed.Installation.Id);
        view.GetProperty("ExpectedActiveLocalRevisionId").GetString().Should().Be(seed.Proposal.ExpectedActiveLocalRevisionId);
        view.GetProperty("CapabilitySnapshotSha256").GetString().Should().Be(seed.Proposal.CapabilitySnapshotSha256);
        view.GetProperty("IssuedAtUtc").GetDateTimeOffset().Should().Be(seed.Proposal.IssuedAtUtc);
        view.GetProperty("ExpiresAtUtc").GetDateTimeOffset().Should().Be(seed.Proposal.ExpiresAtUtc);
        json.RootElement.GetProperty("FactsAreTruncated").GetBoolean().Should().BeFalse();
        var facts = json.RootElement.GetProperty("Facts").EnumerateArray().ToArray();
        facts.Should().HaveCount(seed.Facts.Length);
        foreach (var fact in facts)
        {
            fact.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
                "Id", "ProposalId", "Kind", "Source", "OccurredAtUtc", "RecordedAtUtc", "ReasonCode", "LocalRevisionId",
                "DefinitionIdentitySha256", "SharedPlanIdentitySha256", "LocalPlanIdentitySha256");
            var expected = seed.Facts.Single(item => item.Id == fact.GetProperty("Id").GetGuid());
            fact.GetProperty("ProposalId").GetGuid().Should().Be(expected.ProposalId);
            fact.GetProperty("Kind").GetString().Should().Be(expected.Kind);
            fact.GetProperty("Source").GetString().Should().Be(expected.Source);
            fact.GetProperty("OccurredAtUtc").GetDateTimeOffset().Should().Be(expected.OccurredAtUtc);
            fact.GetProperty("RecordedAtUtc").GetDateTimeOffset().Should().Be(expected.RecordedAtUtc);
            foreach (var (name, value) in new[]
            {
                ("ReasonCode", expected.ReasonCode), ("LocalRevisionId", expected.LocalRevisionId),
                ("DefinitionIdentitySha256", expected.DefinitionIdentitySha256),
                ("SharedPlanIdentitySha256", expected.SharedPlanIdentitySha256), ("LocalPlanIdentitySha256", expected.LocalPlanIdentitySha256)
            }) fact.GetProperty(name).GetString().Should().Be(value);
        }
        facts.Select(fact => fact.GetProperty("Id").GetGuid()).Should().Equal(Ordered(seed.Facts).Select(fact => fact.Id));
        facts.Select(fact => fact.GetProperty("Kind").GetString()).Should().BeEquivalentTo("Accepted", "Activated", "RolledBack", "Retrieved");
        foreach (var terminal in seed.Proposals.Skip(1))
        {
            using var terminalResponse = await client.GetAsync(Path(seed, proposal: terminal.Id)).ConfigureAwait(false);
            terminalResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var terminalResult = JsonSerializer.Deserialize<ProcessingGraphProposalReadResult>(await terminalResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false))!;
            var expectedTerminal = seed.AllFacts.Single(fact => fact.ProposalId == terminal.Id);
            terminalResult.Facts.Should().ContainSingle().Which.Kind.Should().Be(expectedTerminal.Kind);
            terminalResult.Facts[0].Id.Should().Be(expectedTerminal.Id);
            terminalResult.Facts[0].ReasonCode.Should().Be(expectedTerminal.ReasonCode);
        }
        System.Text.Encoding.UTF8.GetString(payload).Should().NotContain("Staged").And.NotContain("DeviceKey").And.NotContain("LeaseToken");
        // The read is observational: exact rows remain unchanged, including historical installation state.
        db.ChangeTracker.Clear();
        var persisted = await db.CentralProcessingGraphDeliveryFacts.AsNoTracking().Where(item => seed.ProposalIds.Contains(item.ProposalId)).ToArrayAsync().ConfigureAwait(false);
        persisted.Should().BeEquivalentTo(seed.AllFacts, options => options.Excluding(item => item.Proposal));
        (await db.LogicalCameraInstallations.AsNoTracking().SingleAsync(item => item.Id == seed.Installation.Id).ConfigureAwait(false)).RetiredAtUtc.Should().Be(seed.Installation.RetiredAtUtc);
    }

    [TestMethod]
    public async Task Http_MultiObservatoryScopeSubstitutionAndRemovedMembershipConcealAllPrivateData()
    {
        var a = await SeedAsync().ConfigureAwait(false);
        var b = await SeedAsync(viewerOwner: true).ConfigureAwait(false);
        using var owner = await ArtifactRetrievalTests.CreateUserClientAsync(TestUsers.Operator.Username, TestUsers.Operator.Password).ConfigureAwait(false);
        using var outsider = await ArtifactRetrievalTests.CreateUserClientAsync(TestUsers.Viewer.Username, TestUsers.Viewer.Password).ConfigureAwait(false);
        await AssertDeniedAsync(outsider, Path(a), a).ConfigureAwait(false);
        await AssertDeniedAsync(owner, Path(b), b).ConfigureAwait(false);
        foreach (var path in new[]
        {
            Path(a, camera: b.Camera.Id), Path(a, installation: b.Installation.Id), Path(a, proposal: b.Proposal.Id),
            Path(a, installation: a.Installation.InstallationPublicId), Path(a, observatory: b.ObservatoryId),
            Path(a, installation: a.Replacement.Id)
        }) await AssertDeniedAsync(owner, path, b).ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Add(new ObservatoryMembership { UserId = a.OwnerId, ObservatoryId = b.ObservatoryId, Role = ObservatoryMembershipRole.Viewer, AddedAtUtc = a.Now });
        await db.SaveChangesAsync().ConfigureAwait(false);
        using var allowedB = await owner.GetAsync(Path(b)).ConfigureAwait(false);
        allowedB.StatusCode.Should().Be(HttpStatusCode.OK);
        using var scoped = await KeyClientAsync(AssemblyHooks.Fixture.Factory, a).ConfigureAwait(false);
        using var allowedA = await scoped.GetAsync(Path(a)).ConfigureAwait(false);
        allowedA.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertDeniedAsync(scoped, Path(b), b).ConfigureAwait(false);
        // Global definition authority must not imply private proposal authority.
        using var definition = await outsider.GetAsync(new Uri($"/api/internal/processing-graphs/revisions/{a.Proposal.RevisionId:D}", UriKind.Relative)).ConfigureAwait(false);
        definition.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertDeniedAsync(outsider, Path(a), a).ConfigureAwait(false);
        await db.ObservatoryMemberships.Where(item => item.UserId == a.OwnerId && item.ObservatoryId == a.ObservatoryId).ExecuteDeleteAsync().ConfigureAwait(false);
        await AssertDeniedAsync(owner, Path(a), a).ConfigureAwait(false);
        await AssertDeniedAsync(scoped, Path(a), a).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task Http_MissingMalformedCredentialsBoundsAndCancellationPreserveOutcomes()
    {
        var seed = await SeedAsync().ConfigureAwait(false);
        var probe = new ReadProbe();
        using var factory = CreateFactory(probe);
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var invalid = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        invalid.DefaultRequestHeaders.Add("X-API-Key", "invalid-proposal-key");
        foreach (var client in new[] { anonymous, invalid })
        {
            using var response = await client.GetAsync(Path(seed)).ConfigureAwait(false);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        using var owner = await KeyClientAsync(factory, seed).ConfigureAwait(false);
        foreach (var id in new[] { "not-a-guid", Guid.Empty.ToString("D"), Guid.NewGuid().ToString("D") })
        {
            foreach (var segment in new[] { seed.ObservatoryId, seed.Camera.Id, seed.Installation.Id, seed.Proposal.Id })
                await AssertDeniedAsync(owner, new Uri(Path(seed).OriginalString.Replace(segment.ToString("D"), id, StringComparison.Ordinal)), seed).ConfigureAwait(false);
        }
        foreach (var take in new[] { "0", "-1", "51", "2147483647", "malformed" })
        {
            using var response = await owner.GetAsync(new Uri(Path(seed).OriginalString + "?take=" + take, UriKind.Relative)).ConfigureAwait(false);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        using var noList = await owner.GetAsync(new Uri(Path(seed).OriginalString[..Path(seed).OriginalString.LastIndexOf('/')], UriKind.Relative)).ConfigureAwait(false);
        noList.StatusCode.Should().Be(HttpStatusCode.NotFound);
        probe.WaitForCancellation = true;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = owner.GetAsync(Path(seed), cancellation.Token);
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await cancellation.CancelAsync().ConfigureAwait(false);
        await ((Func<Task>)(async () => { using var response = await pending.ConfigureAwait(false); })).Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
        await probe.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task Service_RevocationDuringReadReturnsNullAndDoesNotTrackOrWrite()
    {
        var seed = await SeedAsync().ConfigureAwait(false);
        var probe = new ReadProbe
        {
            BeforeFacts = async () =>
            {
                await using var removal = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
                await removal.ServiceProvider.GetRequiredService<ApplicationDbContext>().ObservatoryMemberships
                    .Where(item => item.UserId == seed.OwnerId && item.ObservatoryId == seed.ObservatoryId).ExecuteDeleteAsync().ConfigureAwait(false);
            }
        };
        using var factory = CreateFactory(probe);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IProcessingGraphProposalReadService>();
        probe.NonQueries.Clear();
        var result = await service.GetAsync(Principal(seed.OwnerId), seed.ObservatoryId, seed.Camera.Id, seed.Installation.Id, seed.Proposal.Id).ConfigureAwait(false);
        result.Should().BeNull();
        db.ChangeTracker.Entries().Should().BeEmpty();
        probe.Commands.Should().HaveCount(3);
        probe.NonQueries.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Http_LargeHistoryReadsIndexedBoundedRowsWithEqualTimesAndConcurrentInsert()
    {
        var seed = await SeedAsync(pending: true).ConfigureAwait(false);
        await SeedHistoryAsync(seed, 10000).ConfigureAwait(false);
        var probe = new ReadProbe();
        using var factory = CreateFactory(probe);
        using var client = await KeyClientAsync(factory, seed).ConfigureAwait(false);
        // One unmeasured warm-up; one observed HTTP operation, no percentile or speedup claim.
        using (var warmup = await client.GetAsync(Path(seed)).ConfigureAwait(false)) warmup.StatusCode.Should().Be(HttpStatusCode.OK);
        probe.Commands.Clear();
        probe.NonQueries.Clear();
        var clock = Stopwatch.StartNew();
        using var response = await client.GetAsync(Path(seed)).ConfigureAwait(false);
        var payload = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        clock.Stop();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = JsonSerializer.Deserialize<ProcessingGraphProposalReadResult>(payload)!;
        result.Facts.Should().HaveCount(50);
        result.FactsAreTruncated.Should().BeTrue();
        probe.Commands.Should().HaveCount(3);
        probe.NonQueries.Should().BeEmpty();
        var factsCommand = probe.Commands.Single(item => item.Sql.Contains("proposal-read:facts", StringComparison.Ordinal));
        factsCommand.Sql.Should().Contain("TOP(").And.Contain("ORDER BY").And.NotContain("OFFSET").And.NotContain("COUNT(");
        await AssertIndexedBoundAsync(factsCommand, 51).ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var expected = await db.CentralProcessingGraphDeliveryFacts.AsNoTracking().Where(fact => fact.ProposalId == seed.Proposal.Id)
            .OrderByDescending(fact => fact.RecordedAtUtc).ThenByDescending(fact => fact.Id).Take(50).ToArrayAsync().ConfigureAwait(false);
        result.Facts.Select(fact => fact.Id).Should().Equal(expected.Select(fact => fact.Id));
        result.Facts.Select(fact => fact.RecordedAtUtc).Distinct().Should().ContainSingle();
        TestContext.WriteLine($"source workload=1270-v1 retainedFacts=10000 unrelatedFacts=10000 unrelatedProposals=200 concurrency=1 warmup=1 observedOperations=1 productSQL=3 factRowsReturned=51 outputFacts=50 responseBytes={payload.Length} observedHttpMs={clock.Elapsed.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)}");
        TestContext.WriteLine(factsCommand.Sql);
        // Insert while the next request is between the proposal and fact commands. The new read
        // may see the appended immutable fact; it remains deterministic and bounded, without a snapshot promise.
        var inserted = new CentralProcessingGraphDeliveryFact
        {
            ProposalId = seed.Proposal.Id, Kind = "Retrieved", Source = "LogicHost",
            OccurredAtUtc = seed.Now.AddMinutes(45), RecordedAtUtc = seed.Now.AddMinutes(45), ReasonCode = "proposal-retrieved"
        };
        probe.BeforeFacts = async () =>
        {
            await using var insertion = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
            var writer = insertion.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            writer.Add(inserted);
            await writer.SaveChangesAsync().ConfigureAwait(false);
        };
        using var concurrent = await client.GetAsync(Path(seed)).ConfigureAwait(false);
        concurrent.StatusCode.Should().Be(HttpStatusCode.OK);
        var next = JsonSerializer.Deserialize<ProcessingGraphProposalReadResult>(await concurrent.Content.ReadAsByteArrayAsync().ConfigureAwait(false))!;
        next.Facts.Should().HaveCount(50);
        next.FactsAreTruncated.Should().BeTrue();
        next.Facts[0].Id.Should().Be(inserted.Id);
        using var small = await client.GetAsync(new Uri(Path(seed).OriginalString + "?take=1", UriKind.Relative)).ConfigureAwait(false);
        var one = JsonSerializer.Deserialize<ProcessingGraphProposalReadResult>(await small.Content.ReadAsByteArrayAsync().ConfigureAwait(false))!;
        one.Facts.Should().ContainSingle().Which.Id.Should().Be(inserted.Id);
        one.FactsAreTruncated.Should().BeTrue();
    }

    [TestMethod]
    public async Task Http_EmptyAndExactLimitHistoryReportTruthfulTruncation()
    {
        var seed = await SeedAsync().ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var empty = new CentralProcessingGraphDeliveryProposal
        {
            AssignmentId = seed.Proposal.AssignmentId,
            RevisionId = seed.Proposal.RevisionId,
            RegistrationId = seed.Proposal.RegistrationId,
            LogicalCameraInstallationId = seed.Installation.Id,
            InstallationPublicId = seed.Installation.InstallationPublicId,
            CapabilitySnapshotSha256 = new string('a', 64),
            IssuedAtUtc = seed.Now,
            ExpiresAtUtc = seed.Now.AddHours(1)
        };
        db.Add(empty);
        await db.SaveChangesAsync().ConfigureAwait(false);
        using var client = await KeyClientAsync(AssemblyHooks.Fixture.Factory, seed).ConfigureAwait(false);
        using var emptyResponse = await client.GetAsync(Path(seed, proposal: empty.Id)).ConfigureAwait(false);
        emptyResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var emptyResult = JsonSerializer.Deserialize<ProcessingGraphProposalReadResult>(await emptyResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false))!;
        emptyResult.Facts.Should().BeEmpty();
        emptyResult.FactsAreTruncated.Should().BeFalse();
        for (var index = 0; index < 50; index++) db.Add(Fact(empty.Id, "Retrieved", seed.Now, index));
        await db.SaveChangesAsync().ConfigureAwait(false);
        using var exactResponse = await client.GetAsync(Path(seed, proposal: empty.Id)).ConfigureAwait(false);
        exactResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var exact = JsonSerializer.Deserialize<ProcessingGraphProposalReadResult>(await exactResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false))!;
        exact.Facts.Should().HaveCount(50);
        exact.FactsAreTruncated.Should().BeFalse();
    }

    private static IEnumerable<CentralProcessingGraphDeliveryFact> Ordered(IEnumerable<CentralProcessingGraphDeliveryFact> facts)
        => facts.OrderByDescending(fact => fact.RecordedAtUtc).ThenByDescending(fact => new SqlGuid(fact.Id));

    private static async Task AssertDeniedAsync(HttpClient client, Uri path, Seed seed)
    {
        using var response = await client.GetAsync(path).ConfigureAwait(false);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        foreach (var value in new[] { seed.Proposal.Id.ToString("D"), seed.Camera.Name, "Facts", "Truncated", "Cursor", seed.Proposal.CapabilitySnapshotSha256 })
            body.Should().NotContain(value);
    }

    private static Uri Path(Seed seed, Guid? observatory = null, Guid? camera = null, Guid? installation = null, Guid? proposal = null)
        => new($"/api/v1.0/observatories/{observatory ?? seed.ObservatoryId:D}/cameras/{camera ?? seed.Camera.Id:D}/installations/{installation ?? seed.Installation.Id:D}/processing-graph-proposals/{proposal ?? seed.Proposal.Id:D}", UriKind.Relative);

    private static ClaimsPrincipal Principal(string owner)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, owner), new Claim("account_type", "User")], IdentityConstants.ApplicationScheme));

    private static WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> CreateFactory(ReadProbe probe)
        => AssemblyHooks.Fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddDbContext<ApplicationDbContext>(options => options.AddInterceptors(probe))));

    private static async Task<HttpClient> KeyClientAsync(WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory, Seed seed)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var key = await scope.ServiceProvider.GetRequiredService<IApiKeyLifecycleService>().CreateAsync(
            seed.OwnerId, TestUsers.Operator.Email, $"proposal-{Guid.NewGuid():N}", ApiKeyAccessLevel.Read, seed.ObservatoryId,
            DateTimeOffset.UtcNow.AddMinutes(10), CancellationToken.None).ConfigureAwait(false);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", key.PlaintextKey);
        return client;
    }

    private static async Task<Seed> SeedAsync(bool viewerOwner = false, bool pending = false)
    {
        var artifact = await ArtifactRetrievalTests.SeedArtifactAsync(viewerOwner ? TestUsers.Viewer.Email : TestUsers.Operator.Email, [1], putObject: false).ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var registration = await db.DeviceRegistrations.SingleAsync(item => item.DevicePublicId == artifact.DevicePublicId).ConfigureAwait(false);
        var now = new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
        var camera = new LogicalCamera
        {
            ObservatoryId = registration.ObservatoryId,
            Name = $"Proposal camera {Guid.NewGuid():N}",
            Slug = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = now.AddDays(-2),
            CreatedByUserId = registration.OwnerUserId,
            DeactivatedAtUtc = now.AddHours(2)
        };
        var installation = new LogicalCameraInstallation
        {
            LogicalCamera = camera,
            LogicalCameraId = camera.Id,
            RegistrationId = registration.Id,
            InstallationPublicId = Guid.NewGuid(),
            AssignedAtUtc = now.AddDays(-1),
            RetiredAtUtc = now.AddHours(1),
            AssignedByUserId = registration.OwnerUserId,
            RetiredByUserId = registration.OwnerUserId,
            AssignmentReasonCode = "initial",
            RetirementReasonCode = "replaced"
        };
        // A real replacement has its own registration and public identity, while the retained row stays exact.
        var other = await ArtifactRetrievalTests.SeedArtifactAsync(viewerOwner ? TestUsers.Viewer.Email : TestUsers.Operator.Email, [2], putObject: false).ConfigureAwait(false);
        var otherRegistration = await db.DeviceRegistrations.SingleAsync(item => item.DevicePublicId == other.DevicePublicId).ConfigureAwait(false);
        otherRegistration.ObservatoryId = registration.ObservatoryId;
        var replacement = new LogicalCameraInstallation
        {
            LogicalCamera = camera,
            LogicalCameraId = camera.Id,
            RegistrationId = otherRegistration.Id,
            InstallationPublicId = Guid.NewGuid(),
            AssignedAtUtc = now.AddHours(1),
            ReplacesInstallationId = installation.Id,
            AssignedByUserId = registration.OwnerUserId,
            AssignmentReasonCode = "replacement"
        };
        var revision = await db.CentralProcessingGraphRevisions.AsNoTracking().FirstAsync().ConfigureAwait(false);
        var assignment = new CentralProcessingGraphAssignment
        {
            RevisionId = revision.Id,
            TargetHost = CentralProcessingGraphTargetHost.Edge,
            Scope = CentralProcessingGraphAssignmentScope.LogicalCamera,
            ObservatoryId = camera.ObservatoryId,
            LogicalCamera = camera,
            LogicalCameraId = camera.Id,
            EffectiveFromUtc = now.AddDays(-1),
            EffectiveUntilUtc = now.AddHours(1),
            CreatedAtUtc = now.AddDays(-1),
            ActorUserId = registration.OwnerUserId,
            ReasonCode = "retained"
        };
        // Independent proposals keep the terminal settlement invariant faithful: Accepted,
        // Rejected, and Expired cannot coexist as terminal settlements on a single proposal.
        var proposals = Enumerable.Range(0, 4).Select(index => new CentralProcessingGraphDeliveryProposal
        {
            Assignment = assignment,
            AssignmentId = assignment.Id,
            RevisionId = revision.Id,
            RegistrationId = registration.Id,
            LogicalCameraInstallation = installation,
            LogicalCameraInstallationId = installation.Id,
            InstallationPublicId = installation.InstallationPublicId,
            ExpectedActiveLocalRevisionId = new string('e', 64),
            CapabilitySnapshotSha256 = new string('a', 64),
            IssuedAtUtc = now.AddTicks(index),
            ExpiresAtUtc = now.AddHours(1)
        }).ToArray();
        CentralProcessingGraphDeliveryFact[] facts = pending ? [] : [Fact(proposals[0].Id, "Retrieved", now, 0), Fact(proposals[0].Id, "Accepted", now, 1),
            Fact(proposals[0].Id, "Activated", now, 2), Fact(proposals[0].Id, "RolledBack", now, 3)];
        var otherFacts = new[] { Fact(proposals[1].Id, "Superseded", now, 4), Fact(proposals[2].Id, "Rejected", now, 5), Fact(proposals[3].Id, "Expired", now, 6) };
        db.AddRange(camera, installation, replacement, assignment);
        db.AddRange(proposals);
        db.AddRange(facts.Concat(otherFacts));
        await db.SaveChangesAsync().ConfigureAwait(false);
        return new(camera.ObservatoryId, registration.OwnerUserId, now, camera, installation, replacement, proposals[0], proposals, facts, facts.Concat(otherFacts).ToArray());
    }

    private static CentralProcessingGraphDeliveryFact Fact(Guid proposalId, string kind, DateTimeOffset now, int index)
        => new()
        {
            ProposalId = proposalId,
            Kind = kind,
            Source = kind is "Superseded" or "Expired" ? "LogicHost" : "CameraAgent",
            OccurredAtUtc = kind == "Expired" ? now.AddHours(1) : now.AddSeconds(index),
            RecordedAtUtc = kind == "Expired" ? now.AddHours(1).AddMinutes(2) : now.AddMinutes(2).AddSeconds(index),
            ReasonCode = $"stored-{kind.ToUpperInvariant()}",
            LocalRevisionId = kind is "Accepted" or "Activated" or "RolledBack" ? new string('b', 64) : null,
            DefinitionIdentitySha256 = kind is "Accepted" or "Activated" or "RolledBack" ? new string('c', 64) : null,
            SharedPlanIdentitySha256 = kind is "Accepted" or "Activated" or "RolledBack" ? new string('d', 64) : null,
            LocalPlanIdentitySha256 = kind is "Accepted" or "Activated" or "RolledBack" ? new string('f', 64) : null
        };

    private static async Task SeedHistoryAsync(Seed seed, int count)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var unrelated = Enumerable.Range(0, 200).Select(index => new CentralProcessingGraphDeliveryProposal
        {
            AssignmentId = seed.Proposal.AssignmentId,
            RevisionId = seed.Proposal.RevisionId,
            RegistrationId = seed.Proposal.RegistrationId,
            LogicalCameraInstallationId = seed.Installation.Id,
            InstallationPublicId = seed.Installation.InstallationPublicId,
            CapabilitySnapshotSha256 = new string('a', 64),
            IssuedAtUtc = seed.Now.AddSeconds(index),
            ExpiresAtUtc = seed.Now.AddDays(1)
        }).ToArray();
        db.AddRange(unrelated);
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.ChangeTracker.Clear();
        for (var offset = 0; offset < count; offset += 500)
        {
            for (var index = offset; index < Math.Min(count, offset + 500); index++)
            {
                // Repeated retrievals are valid append-only delivery facts, with tied recorded UTC
                // values across a large retained proposal and independent unrelated histories.
                db.Add(new CentralProcessingGraphDeliveryFact
                {
                    ProposalId = seed.Proposal.Id,
                    Kind = "Retrieved",
                    Source = "LogicHost",
                    OccurredAtUtc = seed.Now.AddMilliseconds(index),
                    RecordedAtUtc = seed.Now.AddMinutes(30),
                    ReasonCode = "proposal-retrieved"
                });
                db.Add(new CentralProcessingGraphDeliveryFact
                {
                    ProposalId = unrelated[index % unrelated.Length].Id,
                    Kind = "Retrieved",
                    Source = "LogicHost",
                    OccurredAtUtc = seed.Now.AddMilliseconds(index),
                    RecordedAtUtc = seed.Now.AddMinutes(30),
                    ReasonCode = "proposal-retrieved"
                });
            }
            await db.SaveChangesAsync().ConfigureAwait(false);
            db.ChangeTracker.Clear();
        }
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Replays captured EF-generated SELECT text and its original typed parameters; no external SQL is accepted.")]
    private async Task AssertIndexedBoundAsync(CapturedCommand captured, int expectedRows)
    {
        await using var connection = new SqlConnection(AssemblyHooks.Fixture.SqlServerConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SET STATISTICS XML ON; {captured.Sql}; SET STATISTICS XML OFF;";
        foreach (var parameter in captured.Parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var rows = 0;
        while (await reader.ReadAsync().ConfigureAwait(false)) rows++;
        rows.Should().Be(expectedRows);
        (await reader.NextResultAsync().ConfigureAwait(false)).Should().BeTrue();
        (await reader.ReadAsync().ConfigureAwait(false)).Should().BeTrue();
        var planText = reader.GetString(0);
        var plan = XDocument.Parse(planText);
        XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
        var seek = plan.Descendants(ns + "RelOp").Single(item =>
            (string?)item.Attribute("PhysicalOp") == "Index Seek" && item.Descendants(ns + "Object").Any(obj =>
                ((string?)obj.Attribute("Index"))?.Contains("ProposalId_RecordedAtUtc_Id", StringComparison.Ordinal) == true));
        var rowsRead = seek.Element(ns + "RunTimeInformation")!.Elements(ns + "RunTimeCountersPerThread").Sum(item =>
            double.Parse(item.Attribute("ActualRowsRead")!.Value, CultureInfo.InvariantCulture));
        rowsRead.Should().Be(expectedRows);
        TestContext.WriteLine($"actual fact index seek rows examined={rowsRead} rows returned/materialized={rows} existingIndex=IX_CentralProcessingGraphDeliveryFacts_ProposalId_RecordedAtUtc_Id");
        TestContext.WriteLine(planText);
    }

    private sealed record Seed(Guid ObservatoryId, string OwnerId, DateTimeOffset Now, LogicalCamera Camera,
        LogicalCameraInstallation Installation, LogicalCameraInstallation Replacement, CentralProcessingGraphDeliveryProposal Proposal,
        CentralProcessingGraphDeliveryProposal[] Proposals, CentralProcessingGraphDeliveryFact[] Facts, CentralProcessingGraphDeliveryFact[] AllFacts)
    {
        public Guid[] ProposalIds => Proposals.Select(item => item.Id).ToArray();
    }

    private sealed record CapturedParameter(string Name, object Value);
    private sealed record CapturedCommand(string Sql, CapturedParameter[] Parameters);

    private sealed class ReadProbe : DbCommandInterceptor
    {
        public ConcurrentQueue<CapturedCommand> Commands { get; } = new();
        public ConcurrentQueue<string> NonQueries { get; } = new();
        public bool WaitForCancellation { get; set; }
        public Func<Task>? BeforeFacts { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("proposal-read:", StringComparison.Ordinal))
            {
                Commands.Enqueue(new(command.CommandText, command.Parameters.Cast<DbParameter>()
                    .Select(item => new CapturedParameter(item.ParameterName, item.Value!)).ToArray()));
                if (WaitForCancellation)
                {
                    Started.TrySetResult();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
                }
                if (command.CommandText.Contains("proposal-read:facts", StringComparison.Ordinal) && BeforeFacts is { } callback)
                {
                    BeforeFacts = null;
                    await callback().ConfigureAwait(false);
                }
            }
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            NonQueries.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
