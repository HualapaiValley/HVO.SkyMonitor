using System.Security.Cryptography;
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
        Assert.HasCount(7, endpoints);
        Assert.IsTrue(endpoints.All(static endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(static item => item.Policy == CameraAgentAuthorizationPolicyNames.OperationsReadV1)));
    }

    [TestMethod]
    [DataRow("GetCameraAgentStillProduct", false)]
    [DataRow("GetCameraAgentStillProductPreview", false)]
    [DataRow("GetCameraAgentStillProductProvenance", false)]
    [DataRow("GetCameraAgentStillProductPresentation", false)]
    [DataRow("GetCameraAgentStillProduct", true)]
    [DataRow("GetCameraAgentStillProductPreview", true)]
    [DataRow("GetCameraAgentStillProductProvenance", true)]
    [DataRow("GetCameraAgentStillProductPresentation", true)]
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
            using var store = Store(root);
            var detail = await PublishAsync(store).ConfigureAwait(false);
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

    [TestMethod]
    public async Task PublishedMedia_IsPrivateChecksumTaggedConditionalRangedAndDownloadable()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-still-api-media");
        try
        {
            using var store = Store(root);
            var detail = await PublishAsync(store).ConfigureAwait(false);
            using var app = CatalogApp(store);
            var etag = $"\"{detail.RenditionSha256.ToUpperInvariant()}\"";

            var preview = await Send(app, "GetCameraAgentStillProductPreview", detail.Summary.ProductId).ConfigureAwait(false);
            Assert.AreEqual(StatusCodes.Status200OK, preview.Response.StatusCode);
            Assert.AreEqual("image/jpeg", preview.Response.ContentType);
            Assert.AreEqual(etag, preview.Response.Headers.ETag.ToString());
            Assert.AreEqual(detail.RenditionSha256, preview.Response.Headers["X-Content-SHA256"].ToString());
            Assert.AreEqual("private, no-cache", preview.Response.Headers.CacheControl.ToString());
            Assert.AreEqual("nosniff", preview.Response.Headers.XContentTypeOptions.ToString());
            Assert.AreEqual("bytes", preview.Response.Headers.AcceptRanges.ToString());
            StringAssert.StartsWith(preview.Response.Headers.ContentDisposition.ToString(), "inline;", StringComparison.Ordinal);
            var bytes = Body(preview);
            Assert.AreEqual(detail.RenditionBytes, bytes.Length);
            Assert.AreEqual(detail.RenditionSha256, Convert.ToHexStringLower(SHA256.HashData(bytes)), ignoreCase: true);

            var notModified = await Send(app, "GetCameraAgentStillProductPreview", detail.Summary.ProductId,
                configure: request => request.Headers.IfNoneMatch = etag).ConfigureAwait(false);
            Assert.AreEqual(StatusCodes.Status304NotModified, notModified.Response.StatusCode);
            Assert.IsEmpty(Body(notModified));

            var head = await Send(app, "GetCameraAgentStillProductPreview", detail.Summary.ProductId,
                configure: request => request.Method = HttpMethods.Head).ConfigureAwait(false);
            Assert.AreEqual(StatusCodes.Status200OK, head.Response.StatusCode);
            Assert.AreEqual(detail.RenditionBytes, head.Response.ContentLength);
            Assert.IsEmpty(Body(head));

            var range = await Send(app, "GetCameraAgentStillProductPreview", detail.Summary.ProductId,
                configure: request => request.Headers.Range = "bytes=0-9").ConfigureAwait(false);
            Assert.AreEqual(StatusCodes.Status206PartialContent, range.Response.StatusCode);
            CollectionAssert.AreEqual(bytes[..10], Body(range));
            Assert.AreEqual($"bytes 0-9/{bytes.Length}", range.Response.Headers.ContentRange.ToString());

            var download = await Send(app, "GetCameraAgentStillProductProvenance", detail.Summary.ProductId,
                configure: request => request.QueryString = new QueryString("?download=1")).ConfigureAwait(false);
            Assert.AreEqual(StatusCodes.Status200OK, download.Response.StatusCode);
            Assert.AreEqual("application/json", download.Response.ContentType);
            Assert.AreEqual($"\"{detail.ProvenanceSha256.ToUpperInvariant()}\"", download.Response.Headers.ETag.ToString());
            StringAssert.StartsWith(download.Response.Headers.ContentDisposition.ToString(),
                $"attachment; filename=\"{detail.Summary.ProductId:D}.provenance.json\"", StringComparison.Ordinal);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Projections_ReturnBoundedDayMonthAndPresentationReads()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-still-api-projections");
        try
        {
            using var store = Store(root);
            var detail = await PublishAsync(store).ConfigureAwait(false);
            using var app = CatalogApp(store);

            var presentation = await Send(app, "GetCameraAgentStillProductPresentation", detail.Summary.ProductId).ConfigureAwait(false);
            Assert.AreEqual(StatusCodes.Status200OK, presentation.Response.StatusCode);
            using (var json = JsonDocument.Parse(Body(presentation)))
            {
                Assert.AreEqual(1, json.RootElement.GetProperty("lineageFrameCount").GetInt32());
                Assert.AreEqual(detail.Summary.ProductId, json.RootElement.GetProperty("detail").GetProperty("summary")
                    .GetProperty("productId").GetGuid());
            }

            var day = await Send(app, "GetCameraAgentStillProductDay", configure: request => request.RouteValues["date"] = "2026-10-01")
                .ConfigureAwait(false);
            Assert.AreEqual(StatusCodes.Status200OK, day.Response.StatusCode);
            using (var json = JsonDocument.Parse(Body(day)))
                Assert.AreEqual(1, json.RootElement.GetProperty("products").GetArrayLength());

            var month = await Send(app, "SummarizeCameraAgentStillProductDates",
                configure: request => request.QueryString = new QueryString("?from=2026-09-28&to=2026-11-08")).ConfigureAwait(false);
            Assert.AreEqual(StatusCodes.Status200OK, month.Response.StatusCode);
            foreach (var query in new[] { "?from=2026-10-02&to=2026-10-01", "?from=2026-10-01&to=2026-12-02" })
            {
                var rejected = await Send(app, "SummarizeCameraAgentStillProductDates",
                    configure: request => request.QueryString = new QueryString(query)).ConfigureAwait(false);
                Assert.AreEqual(StatusCodes.Status400BadRequest, rejected.Response.StatusCode, query);
            }
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

    private static SqliteNightlyProductStore Store(string root) =>
        new(NightlyProductFixture.HostOptions(root, NightlyProductFixture.Options()),
            new NightlyClock(NightlyProductFixture.DayEndUtc.AddMinutes(10)));

    private static async Task<NightlyProductDetail> PublishAsync(SqliteNightlyProductStore store)
    {
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
        return await store.PublishAsync(publication, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<DefaultHttpContext> Send(WebApplication app, string name, Guid? productId = null,
        Action<HttpRequest>? configure = null)
    {
        var context = Context(app);
        if (productId is { } id) context.Request.RouteValues["productId"] = id.ToString("D");
        configure?.Invoke(context.Request);
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints)
            .Single(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name);
        await endpoint.RequestDelegate!(context).ConfigureAwait(false);
        return context;
    }

    private static byte[] Body(HttpContext context) => ((MemoryStream)context.Response.Body).ToArray();

    private static DefaultHttpContext Context(WebApplication app)
    {
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = HttpMethods.Get;
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
        public ValueTask<IReadOnlyList<NightlyProductDateSummary>> SummarizeDatesAsync(DateOnly firstDate, DateOnly lastDate,
            CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<NightlyProductDateSummary>>([]);
        public ValueTask<NightlyProductDay> GetDayAsync(DateOnly observingDate, CancellationToken cancellationToken)
            => ValueTask.FromResult(new NightlyProductDay(observingDate, [], []));
        public ValueTask<NightlyProductPresentation?> GetPresentationAsync(Guid productId, CancellationToken cancellationToken)
            => Read<NightlyProductPresentation>();
        private ValueTask<T?> Read<T>() where T : class => Failure is { } failure ? throw failure : ValueTask.FromResult<T?>(null);
    }
}
