using System.Security.Claims;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Controllers;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Tests.LogicHost.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.Tests.LogicHost.Controllers;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraOperationsControllerTests
{
    [TestMethod]
    public async Task ReadsAreNoStoreAndExactDenialIsNotFoundWhileCollectionsAreEmpty()
    {
        await using var world = await CameraOperationsReadServiceTests.World.CreateAsync(ObservatoryMembershipRole.Viewer).ConfigureAwait(false);
        var a = world.A;
        var member = Controller(world, User("member"));
        var executions = await member.ListExecutionsAsync(a.Observatory, a.Camera).ConfigureAwait(false);
        Assert.AreEqual(2, ((CameraOperationsPage<CentralProcessingGraphExecutionSummary>)((OkObjectResult)executions).Value!).Items.Count);
        Assert.AreEqual("private, no-store", member.Response.Headers.CacheControl.ToString());
        Assert.IsInstanceOfType<OkObjectResult>(await member.GetJobAsync(a.Observatory, a.Camera, a.Job).ConfigureAwait(false));

        var outsider = Controller(world, User("nobody"));
        var empty = (CameraOperationsPage<CentralProcessingGraphExecutionSummary>)((OkObjectResult)await outsider
            .ListExecutionsAsync(a.Observatory, a.Camera).ConfigureAwait(false)).Value!;
        Assert.IsEmpty(empty.Items);
        Assert.IsNull(empty.NextCursor);
        Assert.IsInstanceOfType<NotFoundResult>(await outsider.GetExecutionAsync(a.Observatory, a.Camera, a.Execution).ConfigureAwait(false));
        Assert.IsInstanceOfType<NotFoundResult>(await outsider.GetJobAsync(a.Observatory, a.Camera, a.Job).ConfigureAwait(false));
        Assert.IsInstanceOfType<NotFoundResult>(await outsider.ListJobInputsAsync(a.Observatory, a.Camera, a.Job).ConfigureAwait(false));
        Assert.IsInstanceOfType<NotFoundResult>(await outsider.ListProposalFactsAsync(a.Observatory, a.Camera, a.Proposal).ConfigureAwait(false));
        Assert.AreEqual("private, no-store", outsider.Response.Headers.CacheControl.ToString());
    }

    [TestMethod]
    public async Task InvalidBoundsFiltersAndContinuationsAreBadRequestsAndNonOwnerCredentialsAreForbidden()
    {
        await using var world = await CameraOperationsReadServiceTests.World.CreateAsync(ObservatoryMembershipRole.Owner).ConfigureAwait(false);
        var a = world.A;
        var controller = Controller(world, User("member"));
        foreach (var take in new[] { 0, 51 })
        {
            Assert.IsInstanceOfType<BadRequestObjectResult>(await controller.ListJobsAsync(a.Observatory, a.Camera, take).ConfigureAwait(false));
            Assert.IsInstanceOfType<BadRequestObjectResult>(await controller.GetJobAsync(a.Observatory, a.Camera, a.Job, take).ConfigureAwait(false));
        }
        foreach (var host in new string?[] { null, "central", "Unknown", "1" })
        {
            Assert.IsInstanceOfType<BadRequestObjectResult>(
                await controller.ListAssignmentsAsync(a.Observatory, a.Camera, host).ConfigureAwait(false));
        }
        Assert.IsInstanceOfType<BadRequestObjectResult>(
            await controller.ListEventAssociationsAsync(a.Observatory, a.Camera, cursor: "garbage").ConfigureAwait(false));
        // Bad continuations are only diagnosed for callers who can read the camera.
        Assert.IsInstanceOfType<OkObjectResult>(await Controller(world, User("nobody"))
            .ListEventAssociationsAsync(a.Observatory, a.Camera, cursor: "garbage").ConfigureAwait(false));
        var system = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "member"), new Claim("account_type", "System"), new Claim("scope", "api.admin")],
            CanonicalCredentialClaims.BearerAuthenticationType));
        Assert.IsInstanceOfType<ForbidResult>(await Controller(world, system).ListExecutionsAsync(a.Observatory, a.Camera).ConfigureAwait(false));
        Assert.IsInstanceOfType<ForbidResult>(await Controller(world, new ClaimsPrincipal()).GetJobAsync(a.Observatory, a.Camera, a.Job).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task RevisionSummariesContinueBeyondTheLatestHundredWithoutDefinitions()
    {
        await using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var now = new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 120; index++)
        {
            context.Add(new CentralProcessingGraphRevision
            {
                Name = "catalog",
                Revision = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                DefinitionJson = "{\"large\":true}",
                DefinitionIdentitySha256 = new string('d', 64),
                PortablePlanIdentitySha256 = new string('p', 64),
                CreatedByUserId = "platform",
                // Equal creation times in groups of four exercise the identity tie-breaker.
                CreatedAtUtc = now.AddMinutes(-(index / 4)),
                PublishedAtUtc = index % 3 == 0 ? now : null,
                RetiredAtUtc = index % 5 == 0 ? now : null
            });
        }
        await context.SaveChangesAsync().ConfigureAwait(false);
        var clock = new FixedTimeProvider(now);
        using var telemetry = new ProcessingGraphCatalogTelemetry(clock);
        var catalog = new ProcessingGraphCatalogService(context,
            new CentralProcessingGraphNodeRegistry(new CentralDerivativeRecipeCatalog()), clock, telemetry,
            NullLogger<ProcessingGraphCatalogService>.Instance);
        var seen = new List<CentralProcessingGraphRevisionSummary>();
        string? cursor = null;
        do
        {
            var page = await catalog.ListRevisionSummariesAsync(50, cursor, CancellationToken.None).ConfigureAwait(false);
            Assert.IsLessThanOrEqualTo(50, page.Items.Count);
            seen.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        var expected = await context.CentralProcessingGraphRevisions.OrderByDescending(item => item.CreatedAtUtc)
            .ThenByDescending(item => item.Id).Select(item => item.Id).ToListAsync().ConfigureAwait(false);
        CollectionAssert.AreEqual(expected, seen.Select(item => item.Id).ToList());
        Assert.AreEqual(CentralProcessingGraphLifecycle.Retired, seen.Single(item => item.Revision == "0").Lifecycle);
        Assert.AreEqual(CentralProcessingGraphLifecycle.Published, seen.Single(item => item.Revision == "3").Lifecycle);
        Assert.AreEqual(CentralProcessingGraphLifecycle.Draft, seen.Single(item => item.Revision == "1").Lifecycle);
        Assert.DoesNotContain("large", System.Text.Json.JsonSerializer.Serialize(seen));

        var controller = new ProcessingGraphsController(catalog, clock);
        Assert.IsInstanceOfType<BadRequestObjectResult>((await controller.ListRevisionSummariesAsync(51).ConfigureAwait(false)).Result);
        Assert.IsInstanceOfType<BadRequestObjectResult>((await controller.ListRevisionSummariesAsync(10, "%%%").ConfigureAwait(false)).Result);
        Assert.IsInstanceOfType<OkObjectResult>((await controller.ListRevisionSummariesAsync(10).ConfigureAwait(false)).Result);
    }

    private static CameraOperationsController Controller(CameraOperationsReadServiceTests.World world, ClaimsPrincipal principal)
        => new(world.Service())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } }
        };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static ClaimsPrincipal User(string actor)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor), new Claim("account_type", "User")],
            IdentityConstants.ApplicationScheme));
}
