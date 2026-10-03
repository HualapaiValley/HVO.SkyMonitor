using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Endpoints;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class CameraAgentStillProductEndpointsTests
{
    [TestMethod]
    public void EveryStillProductRead_RequiresTheExistingOperationsReadPolicy()
    {
        using var app = App(new());
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints).ToArray();
        Assert.HasCount(4, endpoints);
        Assert.IsTrue(endpoints.All(static endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(static item => item.Policy == CameraAgentAuthorizationPolicyNames.OperationsReadV1)));
    }

    [TestMethod]
    [DataRow("GetCameraAgentStillProduct", false)]
    [DataRow("GetCameraAgentStillProductPreview", false)]
    [DataRow("GetCameraAgentStillProductProvenance", false)]
    [DataRow("GetCameraAgentStillProduct", true)]
    [DataRow("GetCameraAgentStillProductPreview", true)]
    [DataRow("GetCameraAgentStillProductProvenance", true)]
    public async Task UnknownProduct_IsNotFoundAndStorageFailureDoesNotExposePrivateDetails(string name, bool denied)
    {
        var catalog = new ProbeCatalog();
        using var app = App(catalog);
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints)
            .Single(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name);
        var missing = Context(app);
        await endpoint.RequestDelegate!(missing).ConfigureAwait(false);
        Assert.AreEqual(StatusCodes.Status404NotFound, missing.Response.StatusCode);
        catalog.Failure = denied ? new UnauthorizedAccessException("PRIVATE root or credentials") : new IOException("PRIVATE root or credentials");
        var failed = Context(app);
        await endpoint.RequestDelegate!(failed).ConfigureAwait(false);
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, failed.Response.StatusCode);
        var body = Encoding.UTF8.GetString(((MemoryStream)failed.Response.Body).ToArray());
        Assert.IsFalse(body.Contains("PRIVATE", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("GetCameraAgentStillProductPreview", "*.jpg", false)]
    [DataRow("GetCameraAgentStillProductPreview", "*.jpg", true)]
    [DataRow("GetCameraAgentStillProductProvenance", "*.provenance.json", false)]
    [DataRow("GetCameraAgentStillProductProvenance", "*.provenance.json", true)]
    public async Task ActualCorruptPublication_ReturnsSanitizedUnavailable(string name, string pattern, bool missing)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-still-api-corruption");
        try
        {
            using var store = new SqliteNightlyProductStore(NightlyProductFixture.HostOptions(root, NightlyProductFixture.Options()),
                new NightlyClock(NightlyProductFixture.DayEndUtc.AddMinutes(10)));
            var occurrence = NightlyProductFixture.Occurrence(NightlyProductKind.StarTrail);
            var frame = NightlyProductFixture.Frame(1, NightlyProductFixture.DayStartUtc.AddHours(16));
            var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(new ProcessingExecutionRequest(
                BuiltInProcessingRecipes.StarTrail, JsonSerializer.SerializeToElement(new StarTrailRecipeOptions()),
                ProcessingInputSelector.RecipeResult(frame.Artifact.Role, frame.Artifact.Variant, frame.Artifact.RecipeIdentitySha256),
                [frame.Artifact], NightlyProductGenerator.StarTrailSegmentVariant,
                AuxiliaryInputs: [NightlyProductPreset.BindOccurrence(occurrence)])).ConfigureAwait(false);
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status);
            var product = outcome.Products.Single();
            var publication = new NightlyProductPublication(NightlyProductKind.StarTrail, NightlyProductScope.Segment,
                NightlyProductFixture.ObservingDate, occurrence.SourceWindow!.StartUtc, occurrence.SourceWindow.EndUtc, 0,
                BuiltInProcessingRecipes.StarTrail, product, [new(0, NightlyProductSourceKind.PreviewFrame,
                    frame.Candidate.ArtifactId, frame.Candidate.OutputIdentitySha256, frame.Candidate.CaptureId,
                    frame.Candidate.ExposureStartedUtc)], 90)
            { Occurrence = occurrence };
            var detail = await store.PublishAsync(publication, CancellationToken.None).ConfigureAwait(false);
            var file = Directory.GetFiles(Path.Combine(root, SqliteNightlyProductStore.ProductDirectoryName), pattern,
                SearchOption.AllDirectories).Single();
            if (missing) File.Delete(file);
            else
            {
                var bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
                bytes[^1] ^= 1;
                await File.WriteAllBytesAsync(file, bytes).ConfigureAwait(false);
            }
            using var app = CatalogApp(store);
            var context = Context(app);
            context.Request.RouteValues["productId"] = detail.Summary.ProductId.ToString("D");
            await Unavailable(app, name, context).ConfigureAwait(false);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow("GetCameraAgentStillProduct")]
    [DataRow("ListCameraAgentStillProducts")]
    public async Task ActualUnsupportedStoreSchema_ReturnsSanitizedUnavailable(string name)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-still-api-schema");
        try
        {
            var directory = Path.Combine(root, SqliteNightlyProductStore.DirectoryName);
            Directory.CreateDirectory(directory);
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(directory, SqliteNightlyProductStore.FileName)};Pooling=False"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version=1;";
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            using var store = new SqliteNightlyProductStore(NightlyProductFixture.HostOptions(root, NightlyProductFixture.Options()),
                new NightlyClock(NightlyProductFixture.DayEndUtc.AddMinutes(10)));
            using var app = CatalogApp(store);
            var context = Context(app);
            context.Request.RouteValues["date"] = "2026-10-01";
            await Unavailable(app, name, context).ConfigureAwait(false);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private static async Task Unavailable(WebApplication app, string name, HttpContext context)
    {
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints)
            .Single(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name);
        await endpoint.RequestDelegate!(context).ConfigureAwait(false);
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        var body = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        StringAssert.Contains(body, "The still product catalog is temporarily unavailable.", StringComparison.Ordinal);
        Assert.IsFalse(body.Contains("nightly-products", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("schema", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(body.Contains("checksum", StringComparison.OrdinalIgnoreCase));
    }

    private static DefaultHttpContext Context(WebApplication app)
    {
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.RouteValues["productId"] = Guid.NewGuid().ToString("D");
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static WebApplication App(ProbeCatalog catalog) => CatalogApp(catalog);

    private static WebApplication CatalogApp(INightlyProductCatalog catalog)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Services.AddSingleton<INightlyProductCatalog>(catalog);
        var app = builder.Build();
        app.MapCameraAgentStillProductEndpoints();
        return app;
    }

    private sealed class ProbeCatalog : INightlyProductCatalog
    {
        internal Exception? Failure { get; set; }
        public ValueTask<IReadOnlyList<NightlyProductSummary>> ListAsync(DateOnly observingDate, CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<NightlyProductSummary>>([]);
        public ValueTask<IReadOnlyList<NightlyProductWindowStatus>> ListWindowsAsync(DateOnly observingDate, CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<NightlyProductWindowStatus>>([]);
        public ValueTask<NightlyProductDetail?> GetAsync(Guid productId, CancellationToken cancellationToken)
            => Read<NightlyProductDetail>();
        public ValueTask<NightlyProductRendition?> OpenRenditionAsync(Guid productId, CancellationToken cancellationToken)
            => Read<NightlyProductRendition>();
        public ValueTask<NightlyProductProvenance?> OpenProvenanceAsync(Guid productId, CancellationToken cancellationToken)
            => Read<NightlyProductProvenance>();
        private ValueTask<T?> Read<T>() where T : class => Failure is { } failure ? throw failure : ValueTask.FromResult<T?>(null);
    }
}
