using System.ComponentModel.DataAnnotations;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Endpoints;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentTimeLapseSampleEndpointsTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize() => _root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-time-lapse-sample");

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, true);

    [TestMethod]
    public async Task DisabledSample_IsNotFoundBehindTheOperationsReadPolicy()
    {
        using var app = App(new TimeLapseSampleOptions());
        var endpoint = Endpoint(app);
        Assert.IsTrue(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(static item => item.Policy == CameraAgentAuthorizationPolicyNames.OperationsReadV1));

        var context = await SendAsync(app).ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.AreEqual("private, no-cache", context.Response.Headers.CacheControl.ToString());
    }

    [TestMethod]
    public async Task EnabledSample_ServesItsDeclaredTypeWithRangesValidatorsAndHead()
    {
        var path = Path.Combine(_root, "sample.webm");
        var bytes = Enumerable.Range(0, 100).Select(static value => (byte)value).ToArray();
        await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
        using var app = App(new TimeLapseSampleOptions { Enabled = true, FilePath = path, MediaType = "video/webm" });

        var full = await SendAsync(app).ConfigureAwait(false);
        Assert.AreEqual(StatusCodes.Status200OK, full.Response.StatusCode);
        Assert.AreEqual("video/webm", full.Response.ContentType);
        Assert.AreEqual("bytes", full.Response.Headers.AcceptRanges.ToString());
        Assert.AreEqual("nosniff", full.Response.Headers.XContentTypeOptions.ToString());
        CollectionAssert.AreEqual(bytes, Body(full));
        var etag = full.Response.Headers.ETag.ToString();
        Assert.IsFalse(string.IsNullOrEmpty(etag));

        var range = await SendAsync(app, request => request.Headers.Range = "bytes=10-19").ConfigureAwait(false);
        Assert.AreEqual(StatusCodes.Status206PartialContent, range.Response.StatusCode);
        CollectionAssert.AreEqual(bytes[10..20], Body(range));

        var conditional = await SendAsync(app, request => request.Headers.IfNoneMatch = etag).ConfigureAwait(false);
        Assert.AreEqual(StatusCodes.Status304NotModified, conditional.Response.StatusCode);
        Assert.IsEmpty(Body(conditional));

        var head = await SendAsync(app, request => request.Method = HttpMethods.Head).ConfigureAwait(false);
        Assert.AreEqual(StatusCodes.Status200OK, head.Response.StatusCode);
        Assert.AreEqual(100, head.Response.ContentLength);
        Assert.IsEmpty(Body(head));
    }

    [TestMethod]
    public async Task EnabledSampleWithoutItsFile_IsNotFound()
    {
        using var app = App(new TimeLapseSampleOptions { Enabled = true, FilePath = Path.Combine(_root, "absent.mp4") });

        var context = await SendAsync(app).ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [TestMethod]
    public void Validation_RequiresAFullyQualifiedPathAndASupportedMediaType()
    {
        Assert.IsEmpty(Validate(new TimeLapseSampleOptions()));
        Assert.IsEmpty(Validate(new TimeLapseSampleOptions { Enabled = true, FilePath = Path.Combine(_root, "a.mp4") }));
        Assert.HasCount(1, Validate(new TimeLapseSampleOptions { Enabled = true }));
        Assert.HasCount(1, Validate(new TimeLapseSampleOptions { Enabled = true, FilePath = "relative/a.mp4" }));
        Assert.HasCount(1, Validate(new TimeLapseSampleOptions { MediaType = "video/quicktime" }));
        Assert.HasCount(1, Validate(new TimeLapseSampleOptions { Width = 0 }));
        Assert.IsTrue(Validate(new CameraAgentHostOptions { RawIngressRoot = "raw-ingress", TimeLapseSample = new() { Enabled = true } })
            .Any(static result => result.MemberNames.Contains(nameof(TimeLapseSampleOptions.FilePath))));
    }

    private static List<ValidationResult> Validate(object options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }

    private static WebApplication App(TimeLapseSampleOptions sample)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new CameraAgentHostOptions { TimeLapseSample = sample }));
        var app = builder.Build();
        app.MapCameraAgentTimeLapseSampleEndpoints();
        return app;
    }

    private static Endpoint Endpoint(WebApplication app) => ((IEndpointRouteBuilder)app).DataSources
        .SelectMany(static source => source.Endpoints).Single();

    private static async Task<DefaultHttpContext> SendAsync(WebApplication app, Action<HttpRequest>? configure = null)
    {
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = HttpMethods.Get;
        context.Response.Body = new MemoryStream();
        configure?.Invoke(context.Request);
        await Endpoint(app).RequestDelegate!(context).ConfigureAwait(false);
        return context;
    }

    private static byte[] Body(HttpContext context) => ((MemoryStream)context.Response.Body).ToArray();
}
