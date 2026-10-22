using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class ProcessingGraphRevisionReadTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task ExactReadPreservesExistingViewAndDoesNotTrack(int lifecycleValue)
    {
        var lifecycle = (CentralProcessingGraphLifecycle)lifecycleValue;
        await using var db = CreateContext();
        var definition = DatabaseSeeder.CreateBasicCentralProcessingGraph();
        var plan = ProcessingGraphCompiler.Compile(definition).Plan!;
        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        var row = new CentralProcessingGraphRevision
        {
            Name = definition.Name,
            Revision = definition.Revision,
            DefinitionJson = Encoding.UTF8.GetString(ProcessingGraphJson.SerializeCanonical(definition)),
            DefinitionIdentitySha256 = plan.DefinitionIdentitySha256,
            PortablePlanIdentitySha256 = plan.PlanIdentitySha256,
            CentralPlanIdentitySha256 = plan.PlanIdentitySha256,
            CreatedAtUtc = now,
            PublishedAtUtc = lifecycle == CentralProcessingGraphLifecycle.Draft ? null : now.AddMinutes(1),
            RetiredAtUtc = lifecycle == CentralProcessingGraphLifecycle.Retired ? now.AddMinutes(2) : null
        };
        db.Add(row);
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.ChangeTracker.Clear();
        using var telemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);
        var service = CreateService(db, telemetry);

        var actual = await service.GetRevisionAsync(row.Id, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(actual);
        var listed = (await service.ListRevisionsAsync(100, CancellationToken.None).ConfigureAwait(false)).Single();
        Assert.AreEqual(JsonSerializer.Serialize(listed), JsonSerializer.Serialize(actual));
        Assert.AreEqual(row.Id, actual.Id);
        Assert.AreEqual(lifecycle, actual.Lifecycle);
        Assert.AreEqual(plan.DefinitionIdentitySha256, actual.DefinitionIdentitySha256);
        Assert.AreEqual(plan.PlanIdentitySha256, actual.PortablePlanIdentitySha256);
        Assert.IsNull(actual.EdgePlanIdentitySha256);
        Assert.AreEqual(row.CentralPlanIdentitySha256, actual.CentralPlanIdentitySha256);
        Assert.AreEqual(row.CreatedAtUtc, actual.CreatedAtUtc);
        Assert.AreEqual(row.PublishedAtUtc, actual.PublishedAtUtc);
        Assert.AreEqual(row.RetiredAtUtc, actual.RetiredAtUtc);
        Assert.AreEqual(definition.Name, actual.Definition.GetProperty("name").GetString());
        Assert.IsEmpty(db.ChangeTracker.Entries());
    }

    [TestMethod]
    public async Task MissingAndEmptyIdsReturnNullAndCancellationPropagates()
    {
        await using var db = CreateContext();
        using var telemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);
        var service = CreateService(db, telemetry);
        Assert.IsNull(await service.GetRevisionAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false));
        Assert.IsNull(await service.GetRevisionAsync(Guid.Empty, CancellationToken.None).ConfigureAwait(false));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.GetRevisionAsync(Guid.NewGuid(), cancellation.Token)).ConfigureAwait(false);
    }

    private static ApplicationDbContext CreateContext()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ProcessingGraphCatalogService CreateService(
        ApplicationDbContext db, ProcessingGraphCatalogTelemetry telemetry)
        => new(db, new CentralProcessingGraphNodeRegistry(new CentralDerivativeRecipeCatalog()),
            TimeProvider.System, telemetry, NullLogger<ProcessingGraphCatalogService>.Instance);
}
