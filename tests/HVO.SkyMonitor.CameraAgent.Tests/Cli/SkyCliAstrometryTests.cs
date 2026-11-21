namespace HVO.SkyMonitor.CameraAgent.Tests.Cli;

[TestClass]
[TestCategory("Unit")]
public sealed class SkyCliAstrometryTests
{
    [TestMethod]
    [DataRow(null)]
    [DataRow("4.5")]
    public async Task ReferenceAstrometry_UsesTheActiveBasisAndRegistryVersion(string? magnitude)
    {
        const string graphs = "/api/v1/operations/processing-graphs";
        using var cli = new CliHarness();
        cli.Api.Json("GET", graphs + "/", new { activeRevisionId = "previous", stateVersion = 4 });
        cli.Api.Json("POST", graphs + "/revisions/reference-astrometry",
            new { revisionId = "draft", catalogSelection = new { maximumMagnitude = magnitude is null ? 3.25 : 4.5 } });
        cli.Api.Json("POST", graphs + "/revisions/draft/validate", new { revisionId = "draft" });
        cli.Api.Json("POST", graphs + "/revisions/draft/activate", new { activeRevisionId = "draft" });
        string[] args = magnitude is null ? ["astrometry", "configure", "--json"] :
            ["astrometry", "configure", "--reference-magnitude", magnitude, "--json"];

        var result = await cli.RunAsync(args).ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        var create = cli.Api.RequestsTo("POST", graphs + "/revisions/reference-astrometry").Single();
        Assert.AreEqual("previous", create.BodyJson().GetProperty("basisRevisionId").GetString());
        var body = create.BodyJson();
        if (magnitude is null)
            Assert.IsTrue(!body.TryGetProperty("referenceMagnitude", out var value) || value.ValueKind == System.Text.Json.JsonValueKind.Null);
        else
            Assert.AreEqual(4.5, body.GetProperty("referenceMagnitude").GetDouble());
        var activate = cli.Api.RequestsTo("POST", graphs + "/revisions/draft/activate").Single();
        Assert.AreEqual(4, activate.BodyJson().GetProperty("expectedVersion").GetInt32());
        Assert.AreEqual("draft", result.Json().GetProperty("revisionId").GetString());
        Assert.AreEqual("previous", result.Json().GetProperty("previousRevisionId").GetString());
    }
}
