using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Endpoints;

/// <summary>
/// Saved focus sessions are read only through the operations read policy, and an export is the exact verified stored
/// bytes with their checksum; a tampered, malformed or absent record is refused with a sanitized problem.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentFocusSessionEndpointsTests
{
    private const string ListName = "ListCameraAgentFocusSessions";
    private const string ExportName = "ExportCameraAgentFocusSession";
    private static readonly DateTimeOffset SavedAt = new(2026, 10, 2, 14, 0, 0, TimeSpan.Zero);
    private string _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Path.Combine(Path.GetTempPath(), "hvo-focus-endpoints-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void DeleteRoot()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public void ListAndExport_RequireTheOperationsReadPolicy()
    {
        using var store = new ManualFocusSessionStore(_root);
        using var app = CreateApp(store);

        foreach (var name in new[] { ListName, ExportName })
        {
            var endpoint = Endpoint(app, name);
            CollectionAssert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(static data => data.Policy).ToArray(),
                CameraAgentAuthorizationPolicyNames.OperationsReadV1, name);
            StringAssert.StartsWith(endpoint.RoutePattern.RawText, "/api/v1/operations/focus-sessions", StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task Export_ServesTheExactVerifiedBytesWithTheirChecksumAndNoCaching()
    {
        using var store = new ManualFocusSessionStore(_root);
        var record = Record("session-a");
        var summary = await store.SaveAsync(record, CancellationToken.None).ConfigureAwait(false);
        using var app = CreateApp(store);

        var export = await InvokeAsync(app, ExportName, record.RecordId).ConfigureAwait(false);
        var list = await InvokeAsync(app, ListName).ConfigureAwait(false);

        var stored = await File.ReadAllBytesAsync(Path.Combine(_root, ManualFocusSessionStore.DirectoryName, record.RecordId + ".json"))
            .ConfigureAwait(false);
        Assert.AreEqual(StatusCodes.Status200OK, export.Status);
        CollectionAssert.AreEqual(stored, export.Body);
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(export.Body)), export.Context.Response.Headers["X-Artifact-SHA256"].ToString());
        Assert.AreEqual(summary.Sha256, export.Context.Response.Headers["X-Artifact-SHA256"].ToString());
        Assert.AreEqual("private, no-store", export.Context.Response.Headers.CacheControl.ToString());
        Assert.AreEqual("nosniff", export.Context.Response.Headers.XContentTypeOptions.ToString());
        Assert.AreEqual("application/json", export.Context.Response.ContentType);
        StringAssert.Contains(export.Context.Response.Headers.ContentDisposition.ToString(),
            $"hvo-focus-session-{record.RecordId}.json", StringComparison.Ordinal);
        Assert.AreEqual(StatusCodes.Status200OK, list.Status);
        var listed = Encoding.UTF8.GetString(list.Body);
        StringAssert.Contains(listed, record.RecordId, StringComparison.Ordinal);
        StringAssert.Contains(listed, summary.Sha256, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Export_RefusesTamperedMalformedAndAbsentRecordsWithoutLeakingContent()
    {
        using var store = new ManualFocusSessionStore(_root);
        var record = Record("session-a");
        await store.SaveAsync(record, CancellationToken.None).ConfigureAwait(false);
        var path = Path.Combine(_root, ManualFocusSessionStore.DirectoryName, record.RecordId + ".json");
        var text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        await File.WriteAllTextAsync(path, text.Replace("alice", "mallo", StringComparison.Ordinal)).ConfigureAwait(false);
        using var app = CreateApp(store);

        var tampered = await InvokeAsync(app, ExportName, record.RecordId).ConfigureAwait(false);
        var malformed = await InvokeAsync(app, ExportName, "..").ConfigureAwait(false);
        var absent = await InvokeAsync(app, ExportName, "20261002T140000Z-absent").ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status409Conflict, tampered.Status);
        var problem = Encoding.UTF8.GetString(tampered.Body);
        StringAssert.Contains(problem, "failed checksum verification", StringComparison.Ordinal);
        Assert.IsFalse(problem.Contains("mallo", StringComparison.Ordinal), "No byte of a tampered record is served.");
        Assert.IsFalse(tampered.Context.Response.Headers.ContainsKey("X-Artifact-SHA256"));
        Assert.AreEqual(StatusCodes.Status400BadRequest, malformed.Status);
        Assert.AreEqual(StatusCodes.Status404NotFound, absent.Status);
        Assert.IsFalse(Encoding.UTF8.GetString(absent.Body).Contains(_root, StringComparison.Ordinal), "No storage path leaks.");
    }

    private static WebApplication CreateApp(IManualFocusSessionStore store)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(store);
        var app = builder.Build();
        app.MapCameraAgentFocusSessionEndpoints();
        return app;
    }

    private static RouteEndpoint Endpoint(WebApplication app, string name)
        => ((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate => candidate.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name);

    private static async Task<(int Status, byte[] Body, HttpContext Context)> InvokeAsync(
        WebApplication app,
        string name,
        string? recordId = null)
    {
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = HttpMethods.Get;
        if (recordId is not null)
        {
            context.Request.RouteValues["recordId"] = recordId;
        }
        using var body = new MemoryStream();
        context.Response.Body = body;
        await Endpoint(app, name).RequestDelegate!(context).ConfigureAwait(false);
        return (context.Response.StatusCode, body.ToArray(), context);
    }

    private static ManualFocusSessionRecord Record(string sessionId)
        => ManualFocusSessionRecord.Create(ManualFocusSessionSnapshot.Idle with
        {
            SessionId = sessionId,
            State = ManualFocusSessionState.Stopped,
            OwnerId = "alice",
            StartedUtc = SavedAt.AddMinutes(-5),
            EndedUtc = SavedAt.AddMinutes(-1),
            EndReason = ManualFocusReasonCodes.StoppedByOperator,
            TotalSamples = 4,
            HistoryCapacity = 100,
            ModuleType = "VirtualSky"
        }, "alice", SavedAt);
}
