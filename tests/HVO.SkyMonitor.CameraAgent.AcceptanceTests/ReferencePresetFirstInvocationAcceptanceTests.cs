using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

public sealed partial class RetainedAstrometryBrowserAcceptanceTests
{
    private const string GraphApi = "/api/v1/operations/processing-graphs";

    [TestMethod]
    [DataRow("1", "LegacyScalarSolar", null, "stock")]
    [DataRow("2", "SolarDriven", 5d, "stock")]
    [DataRow("2", "ControlledNight", 3.25d, "stock")]
    [DataRow("2", "ControlledNight", null, "canonical-association")]
    [DataRow("2", "ControlledNight", 3.25d, "legacy-scene")]
    public Task FirstReferencePresetPersistsAndBindsFreshVirtualSkyCaptureAsync(string generation, string mode, double? magnitude, string shape)
        => VerifyFirstReferencePresetAsync(generation, mode, magnitude, shape);

    [TestMethod]
    [DataRow("legacy-collision")]
    [DataRow("legacy-collision-annotation")]
    public Task FirstReferencePresetReusesPredictionWithOccupiedVariantAsync(string shape)
        => VerifyFirstReferencePresetAsync("2", "ControlledNight", 3.25, shape);

    [TestMethod]
    [DataRow("effective")]
    [DataRow("activated-effective")]
    [DataRow("activated-declared")]
    public Task FirstReferencePresetFromSolverFreePredictionAsync(string basisKind)
        => VerifyFirstReferencePresetAsync("2", "ControlledNight", 3.25, "legacy-collision-annotation", basisKind);

    private static async Task VerifyFirstReferencePresetAsync(
        string generation, string mode, double? magnitude, string shape, string? basisKind = null)
    {
        var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var catalogRoot = Environment.GetEnvironmentVariable("HVO_ASTROMETRY_CATALOG_ROOT");
        var evidenceDirectory = Environment.GetEnvironmentVariable("HVO_1901_EVIDENCE_ROOT");
        Assert.IsFalse(string.IsNullOrWhiteSpace(catalogRoot));
        Assert.IsFalse(string.IsNullOrWhiteSpace(evidenceDirectory));
        var evidenceRoot = Path.Combine(evidenceDirectory, basisKind ?? $"{generation}-{mode}-{shape}");
        Directory.CreateDirectory(evidenceRoot);
        var collision = shape is "legacy-collision" or "legacy-collision-annotation";
        var declared = basisKind == "activated-declared";
        var legacy = !declared && (shape == "legacy-scene" || collision);
        await using var host = await CameraAgentKestrelFixture.CreateAsync(prepareRoot: async root =>
        {
            var fixture = mode == "ControlledNight"
                ? Path.Combine(fixtureRoot, "installer-controlled-night.json")
                : Path.Combine(fixtureRoot, $"installer-defaults-{generation}.camera-module.json");
            var config = JsonNode.Parse(await File.ReadAllTextAsync(fixture).ConfigureAwait(false))!;
            if (mode == "SolarDriven")
                config["module"]!["options"]!["illuminationMode"] = mode;
            var steps = config["pipeline"]!["steps"]!.AsArray();
            JsonNode Find(string id) => steps.Single(step => step!["id"]!.GetValue<string>() == id)!;
            InstallerCameraModuleFixture.ConfigureSiloStorage(config);
            if (legacy || declared)
            {
                var scene = new JsonObject
                {
                    ["outputVariant"] = collision ? "astrometry-prediction-v1" : "projected-scene-v1",
                    ["maximumMagnitude"] = 4.5
                };
                if (legacy)
                    scene["independentPrediction"] = true;
                Find("ProjectedScene")["options"] = scene;
            }
            if (collision)
                ConfigureFirstPresetFitFixture(config);
            if (shape is "canonical-association" or "legacy-collision-annotation")
            {
                steps.Add(JsonNode.Parse("""
                    { "type":"MeasuredStellarAssociations", "id":"Measured",
                      "dependsOn":["ProjectedScene"], "required":false,
                      "publication":{"persistence":"durable-local"} }
                    """));
                steps.Add(JsonNode.Parse("""
                    { "type":"Preview", "id":"AnnotationPreview", "dependsOn":["$raw"],
                      "options":{"outputEncoding":"Packed"} }
                    """));
                steps.Add(JsonNode.Parse("""
                    { "type":"Annotation", "id":"Annotation", "required":false,
                      "dependsOn":["AnnotationPreview","ProjectedScene","Measured"],
                      "options":{"requireProjectedSceneDependency":true} }
                    """));
            }
            await File.WriteAllTextAsync(Path.Combine(root, "cameraagent.browser.json"), config.ToJsonString()).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "config.json"), config.ToJsonString()).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "root.txt"), root).ConfigureAwait(false);
        }, scratchRoot: Path.GetTempPath(), preserveRoot: true, productionCatalogRoot: catalogRoot,
            settingsOverrides: new Dictionary<string, string?>
            {
                ["CameraAgent:Observatory:LatitudeDegrees"] = "35.13",
                ["CameraAgent:Observatory:LongitudeDegrees"] = "-113.91",
                ["CameraAgent:Observatory:ElevationMeters"] = "1100",
                ["CameraAgent:Observatory:TimeZoneId"] = "America/Phoenix"
            }).ConfigureAwait(false);
        using var client = await host.CreateOwnerClientAsync().ConfigureAwait(false);
        var registry = await ReadPresetRegistryAsync(client, evidenceRoot, "before").ConfigureAwait(false);
        var basis = registry.GetProperty("activeRevisionId").GetString()!;
        var version = registry.GetProperty("stateVersion").GetInt64();
        Assert.IsFalse(registry.GetProperty("revisions").EnumerateArray().Any(r =>
            r.GetProperty("name").GetString() == "Reference astrometry preset"));
        string? effectiveBasis = null;
        if (shape != "stock")
        {
            using var beforeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var beforeOperations = host.Services.GetRequiredService<IProcessingGraphOperations>();
            while (true)
            {
                var beforeExecutions = await beforeOperations.ReadExecutionsAsync(ProcessingGraphExecutionClass.Live, 10, beforeDeadline.Token)
                    .ConfigureAwait(false);
                var completed = beforeExecutions.FirstOrDefault(row => row.Status == ProcessingGraphExecutionStatus.Completed);
                if (completed is not null)
                {
                    effectiveBasis = completed.GraphRevisionId;
                    await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "basis-execution.json"),
                        JsonSerializer.Serialize(completed, AstrometryBrowserJson), beforeDeadline.Token).ConfigureAwait(false);
                    break;
                }
                await Task.Delay(500, beforeDeadline.Token).ConfigureAwait(false);
            }
        }
        Assert.AreEqual(legacy, effectiveBasis is not null && effectiveBasis != basis);
        if (collision)
        {
            await host.Services.GetRequiredService<CaptureAdmissionCoordinator>()
                .PauseAsync("preset-proof-pause", null, "test", null, CancellationToken.None).ConfigureAwait(false);
        }

        var sourceBasis = basis;
        if (declared)
            effectiveBasis = await CreateDeclaredPredictionBasisAsync(client, host, sourceBasis, evidenceRoot).ConfigureAwait(false);
        if (basisKind is "activated-effective" or "activated-declared")
        {
            await SendPresetRequestAsync(client, evidenceRoot, "activate-basis", $"/revisions/{effectiveBasis}/activate",
                "activate-first-basis", new { expectedVersion = version }).ConfigureAwait(false);
            registry = await ReadPresetRegistryAsync(client, evidenceRoot, "active-basis").ConfigureAwait(false);
            basis = registry.GetProperty("activeRevisionId").GetString()!;
            version = registry.GetProperty("stateVersion").GetInt64();
            Assert.AreEqual(effectiveBasis, basis);
        }
        else if (basisKind == "effective")
        {
            basis = effectiveBasis!;
        }

        if (collision)
        {
            await using var basisConnection = await OpenPresetJournalAsync(host.Root).ConfigureAwait(false);
            var effective = await ReadPresetDefinitionAsync(basisConnection, effectiveBasis!).ConfigureAwait(false);
            var basisNodes = effective.GetProperty("nodes").EnumerateArray().ToArray();
            Assert.IsFalse(basisNodes.Any(node => node.GetProperty("stepAlias").GetString() == "MeasuredAstrometry"));
            var prediction = basisNodes.Single(node => node.GetProperty("stepAlias").GetString() == "AstrometryPrediction");
            Assert.AreEqual(declared ? "operator-reference-prediction" : "astrometry-prediction-v1-1",
                prediction.GetProperty("outputs")[0].GetProperty("variant").GetString());
            await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "basis-definition.json"), effective.GetRawText()).ConfigureAwait(false);
        }
        var request = new { basisRevisionId = basis, revision = "reference-astrometry-first", referenceMagnitude = magnitude, reason = "first invocation probe" };
        await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "first-request.json"),
            JsonSerializer.Serialize(request, AstrometryBrowserJson)).ConfigureAwait(false);
        var create = await SendPresetRequestAsync(client, evidenceRoot, "create", "/revisions/reference-astrometry", "first-create", request).ConfigureAwait(false);
        var revision = create.GetProperty("revisionId").GetString()!;
        var path = "/revisions/" + revision;
        var selection = create.GetProperty("catalogSelection");
        var depth = magnitude ?? 3.25;
        Assert.AreEqual(depth, selection.GetProperty("maximumMagnitude").GetDouble());
        var catalog = CatalogSnapshotResolver.Resolve(new(catalogRoot, "hyg-v44-production"));
        var catalogSelection = await catalog.Catalog.ReadAsync(depth, 2500).ConfigureAwait(false);
        Assert.IsTrue(catalogSelection.IsCompleteForRequestedMagnitude);
        Assert.AreEqual(depth == 5 ? 1637 : 226, catalogSelection.Stars.Count);
        Assert.AreEqual(catalogSelection.IdentitySha256, selection.GetProperty("catalogIdentitySha256").GetString());
        Assert.AreEqual(catalogSelection.SelectionIdentitySha256, selection.GetProperty("selectionIdentitySha256").GetString());
        await SendPresetRequestAsync(client, evidenceRoot, "validate", path + "/validate", "first-validate", new { request.reason }).ConfigureAwait(false);
        await SendPresetRequestAsync(client, evidenceRoot, "activate", path + "/activate", "first-activate", new { expectedVersion = version, request.reason }).ConfigureAwait(false);
        var after = await ReadPresetRegistryAsync(client, evidenceRoot, "after").ConfigureAwait(false);
        Assert.AreEqual(revision, after.GetProperty("activeRevisionId").GetString());
        var repeat = await SendPresetRequestAsync(client, evidenceRoot, "retry", "/revisions/reference-astrometry", "first-create", request).ConfigureAwait(false);
        Assert.AreEqual(create.GetRawText(), repeat.GetRawText());
        if (collision)
            await ReserveThroughNextPresetFitAsync(host).ConfigureAwait(false);
        var restartedAt = DateTimeOffset.UtcNow;
        await host.RestartAsync().ConfigureAwait(false);
        using var reopened = await host.CreateOwnerClientAsync().ConfigureAwait(false);
        var restarted = await ReadPresetRegistryAsync(reopened, evidenceRoot, "restart").ConfigureAwait(false);
        Assert.AreEqual(revision, restarted.GetProperty("activeRevisionId").GetString());
        var retry = await SendPresetRequestAsync(reopened, evidenceRoot, "restart-retry", "/revisions/reference-astrometry", "first-create", request).ConfigureAwait(false);
        Assert.AreEqual(create.GetRawText(), retry.GetRawText());
        if (legacy || declared)
        {
            var differentBasis = basisKind is null ? effectiveBasis : sourceBasis;
            await SendPresetRequestAsync(reopened, evidenceRoot, "changed-basis-retry", "/revisions/reference-astrometry",
                "first-create", new { basisRevisionId = differentBasis, request.revision, request.referenceMagnitude, request.reason },
                HttpStatusCode.Conflict).ConfigureAwait(false);
        }
        var installedDefinition = await VerifyPersistedPresetAsync(
            host.Root, evidenceRoot, effectiveBasis ?? basis, revision, mode, legacy || declared, depth, selection)
            .ConfigureAwait(false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        if (collision)
        {
            await host.Services.GetRequiredService<CaptureAdmissionCoordinator>()
                .ResumeAsync("preset-proof-resume", null, "test", null, deadline.Token).ConfigureAwait(false);
            var reader = host.Services.GetRequiredService<ICameraAgentRetainedAstrometryService>();
            CameraAgentLatestAstrometry? accepted;
            while ((accepted = await reader.GetLatestAcceptedAsync(deadline.Token).ConfigureAwait(false)) is null)
                await Task.Delay(500, deadline.Token).ConfigureAwait(false);
            var captureExecution = (await host.Services.GetRequiredService<IProcessingGraphOperations>()
                .ReadExecutionsAsync(ProcessingGraphExecutionClass.Live, 10, deadline.Token).ConfigureAwait(false))
                .Single(row => row.CaptureId == accepted.CaptureId);
            Assert.AreEqual(revision, captureExecution.GraphRevisionId);
            var journal = new SqliteRawCaptureJournal(Path.Combine(host.Root, "journal", "raw-ingress.db"), 1);
            var committed = (await journal.ReadAllAsync(deadline.Token).ConfigureAwait(false)).Single(row => row.CaptureId == accepted.CaptureId);
            Assert.AreEqual(61L, committed.CaptureSequence);
            await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "accepted-fit.json"),
                JsonSerializer.Serialize(accepted, AstrometryBrowserJson), deadline.Token).ConfigureAwait(false);
        }
        var operations = host.Services.GetRequiredService<IProcessingGraphOperations>();
        while (true)
        {
            var executions = await operations.ReadExecutionsAsync(ProcessingGraphExecutionClass.Live, 20, deadline.Token).ConfigureAwait(false);
            var capture = executions.FirstOrDefault(row => row.AcceptedUtc > restartedAt);
            if (capture is not null)
            {
                await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "fresh-execution.json"), JsonSerializer.Serialize(capture, AstrometryBrowserJson), deadline.Token).ConfigureAwait(false);
                Assert.AreEqual(revision, capture.GraphRevisionId);
                break;
            }
            await Task.Delay(500, deadline.Token).ConfigureAwait(false);
        }

        // A new CLI invocation uses the current registry basis and a new idempotency key.
        var configureAgain = await SendPresetRequestAsync(reopened, evidenceRoot, "configure-again", "/revisions/reference-astrometry",
            "second-create", new { basisRevisionId = revision, revision = "reference-astrometry-again", referenceMagnitude = magnitude, request.reason })
            .ConfigureAwait(false);
        Assert.AreEqual(selection.GetRawText(), configureAgain.GetProperty("catalogSelection").GetRawText());
        await using var connection = await OpenPresetJournalAsync(host.Root).ConfigureAwait(false);
        var secondRevision = configureAgain.GetProperty("revisionId").GetString()!;
        Assert.AreNotEqual(revision, secondRevision);
        var repeatDefinition = await ReadPresetDefinitionAsync(connection, secondRevision).ConfigureAwait(false);
        Assert.IsTrue(JsonElement.DeepEquals(installedDefinition.GetProperty("nodes"), repeatDefinition.GetProperty("nodes")));
        await SendPresetRequestAsync(reopened, evidenceRoot, "validate-again", $"/revisions/{secondRevision}/validate",
            "second-validate", new { request.reason }).ConfigureAwait(false);
        var activeAgain = await SendPresetRequestAsync(reopened, evidenceRoot, "activate-again", $"/revisions/{secondRevision}/activate",
            "second-activate", new { expectedVersion = restarted.GetProperty("stateVersion").GetInt64(), request.reason }).ConfigureAwait(false);
        Assert.AreEqual(secondRevision, activeAgain.GetProperty("activeRevisionId").GetString());
    }

    private static async Task<string> CreateDeclaredPredictionBasisAsync(
        HttpClient client, CameraAgentKestrelFixture host, string sourceBasis, string evidenceRoot)
    {
        var operations = host.Services.GetRequiredService<IProcessingGraphOperations>();
        var original = await operations.ReadRevisionPipelineAsync(sourceBasis, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(original);
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        var pipeline = original with
        {
            Steps =
            [
                .. original.Steps,
                new("AstrometryPrediction", "OperatorPrediction",
                    Options: JsonSerializer.SerializeToElement(new { outputVariant = "operator-reference-prediction", maximumMagnitude = 4.5, maximumResults = 2000 }),
                    DependsOn: ["$raw"], Required: false, Publication: durable),
                new("MeasuredStellarAssociations", "OperatorAssociations",
                    Options: JsonSerializer.SerializeToElement(new { outputVariant = "operator-reference-associations", useAstrometryPrediction = true }),
                    DependsOn: ["OperatorPrediction"], Required: false, Publication: durable)
            ]
        };
        var created = await SendPresetRequestAsync(client, evidenceRoot, "create-basis", "/revisions",
            "create-first-basis", new { name = "Operator solver-free graph", revision = "operator-first-basis", pipeline })
            .ConfigureAwait(false);
        var id = created.GetProperty("revisionId").GetString()!;
        await SendPresetRequestAsync(client, evidenceRoot, "validate-basis", $"/revisions/{id}/validate",
            "validate-first-basis", new { }).ConfigureAwait(false);
        return id;
    }

    private static void ConfigureFirstPresetFitFixture(JsonNode config)
    {
        var rig = config["rig"]!;
        rig["sensor"]!["widthPixels"] = 1024;
        rig["sensor"]!["heightPixels"] = 1024;
        rig["sensor"]!["strideBytes"] = 2048;
        var readout = rig["readout"]!;
        readout["roi"]!["width"] = 1024;
        readout["roi"]!["height"] = 1024;
        readout["strideBytes"] = 2048;
        readout["sampleDepthBits"] = 16;
        readout["blackLevel"] = 0;
        readout["whiteLevel"] = 65535;
        rig["optics"]!["principalPointX"] = 512;
        rig["optics"]!["principalPointY"] = 512;
        rig["optics"]!["imageCircleRadiusPixels"] = 500;
        rig["orientation"]!["boresightAltitudeDegrees"] = 82;
        rig["orientation"]!["boresightAzimuthDegrees"] = 137;
        rig["orientation"]!["rollAdjustmentDegrees"] = 13;
        var renderer = config["module"]!["options"]!.AsObject();
        renderer.Remove("asi174Sensor");
        renderer["magnitudeZeroElectronsPerSecond"] = 60000;
        renderer["backgroundElectronsPerSecond"] = 16;
        renderer["psfSigmaPixels"] = 1;
        renderer["psfRadiusPixels"] = 4;
        renderer["shotNoiseEnabled"] = true;
        renderer["readNoiseStandardDeviation"] = 1;
        var profile = config["schedule"]!["setpointProfiles"]![0]!;
        profile["exposure"] = "00:00:01";
        profile["gain"] = 1;
        profile["captureInterval"] = "00:00:05";
        foreach (var step in config["pipeline"]!["steps"]!.AsArray()
            .Where(step => step!["id"]!.GetValue<string>() is "CloudPresentation" or "EnvironmentPresentation"))
        {
            step!["options"]!["widthPixels"] = 1024;
            step["options"]!["heightPixels"] = 1024;
        }
    }

    private static async Task ReserveThroughNextPresetFitAsync(CameraAgentKestrelFixture host)
    {
        var journal = new SqliteRawCaptureJournal(Path.Combine(host.Root, "journal", "raw-ingress.db"), 1);
        await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var config = await host.Services.GetRequiredService<ICameraAgentConfigurationAccessor>()
            .WaitForConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        // Preserve the production cadence. Reserve synthetic capture identities on the paused disposable host.
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var identity = await journal.ReserveIdentityAsync(config.AgentId!, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)
                .ConfigureAwait(false);
            if (identity.CaptureSequence == 60)
                return;
        }
        Assert.Fail("Could not reserve through capture sequence 60.");
    }

    private static async Task<JsonElement> VerifyPersistedPresetAsync(
        string root, string evidenceRoot, string basis, string revision, string mode, bool hadPrediction, double depth,
        JsonElement selection)
    {
        await using var connection = await OpenPresetJournalAsync(root).ConfigureAwait(false);
        using var profileQuery = connection.CreateCommand();
        profileQuery.CommandText = "SELECT profile_json FROM capture_schedule_revisions ORDER BY revision_number DESC LIMIT 1";
        using var profile = JsonDocument.Parse((byte[])(await profileQuery.ExecuteScalarAsync().ConfigureAwait(false))!);
        var optics = profile.RootElement.GetProperty("rig").GetProperty("optics");
        var renderer = profile.RootElement.GetProperty("module").GetProperty("options");
        Assert.AreEqual(mode, renderer.TryGetProperty("illuminationMode", out var illumination)
            ? illumination.GetString() : "LegacyScalarSolar");
        Assert.AreEqual("EquidistantFisheye", optics.GetProperty("projectionModel").GetString());
        Assert.AreEqual(6.5, renderer.GetProperty("maximumMagnitude").GetDouble());
        Assert.AreEqual(2000, renderer.GetProperty("maximumResults").GetInt32());

        var original = await ReadPresetDefinitionAsync(connection, basis).ConfigureAwait(false);
        var installed = await ReadPresetDefinitionAsync(connection, revision).ConfigureAwait(false);
        var nodes = installed.GetProperty("nodes").EnumerateArray().ToArray();
        var originalNodes = original.GetProperty("nodes").EnumerateArray().ToArray();
        foreach (var node in originalNodes)
        {
            var id = node.GetProperty("id").GetString();
            var current = nodes.Single(n => n.GetProperty("id").GetString() == id);
            if (hadPrediction && node.GetProperty("stepAlias").GetString() == "AstrometryPrediction")
            {
                var expected = JsonNode.Parse(node.GetRawText())!;
                expected["effectiveOptions"]!["maximumMagnitude"] = depth;
                Assert.IsTrue(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected), current), $"Prediction {id} changed beyond magnitude.");
                continue;
            }
            Assert.IsTrue(JsonElement.DeepEquals(node, current), $"Existing node {id} changed.");
        }
        Assert.HasCount(originalNodes.Length + (hadPrediction ? 2 : 3), nodes);
        var prediction = nodes.Single(n => n.GetProperty("stepAlias").GetString() == "AstrometryPrediction");
        var solver = nodes.Single(n => n.GetProperty("stepAlias").GetString() == "MeasuredAstrometry");
        var predictionId = prediction.GetProperty("id").GetString();
        var solverDependencies = solver.GetProperty("dependencies").EnumerateArray()
            .Select(dependency => dependency.GetProperty("producerId").GetString()).ToArray();
        Assert.Contains(predictionId, solverDependencies);
        var measurement = nodes.Single(n => n.GetProperty("id").GetString() == solverDependencies.Single(id => id != predictionId));
        Assert.AreEqual(predictionId, measurement.GetProperty("dependencies").EnumerateArray().Single().GetProperty("producerId").GetString());
        var predictionOptions = prediction.GetProperty("effectiveOptions");
        var solverOptions = solver.GetProperty("effectiveOptions");
        Assert.AreEqual(depth, predictionOptions.GetProperty("maximumMagnitude").GetDouble());
        Assert.AreEqual(2000, predictionOptions.GetProperty("maximumResults").GetInt32());
        Assert.AreEqual(depth, solverOptions.GetProperty("recipe").GetProperty("solver").GetProperty("maximumCatalogMagnitude").GetDouble());
        Assert.AreEqual(60, solverOptions.GetProperty("captureStride").GetInt32());
        Assert.IsTrue(JsonElement.DeepEquals(selection, solverOptions.GetProperty("catalogSelection")));
        await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "profile-sqlite.json"), profile.RootElement.GetRawText()).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "preset-definition-sqlite.json"), installed.GetRawText()).ConfigureAwait(false);
        return installed;
    }

    private static async Task<SqliteConnection> OpenPresetJournalAsync(string root)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "journal", "raw-ingress.db"),
            Mode = SqliteOpenMode.ReadOnly
        }.ConnectionString);
        try
        {
            await connection.OpenAsync().ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<JsonElement> ReadPresetDefinitionAsync(SqliteConnection connection, string revision)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT definition_json FROM processing_graph_revisions WHERE revision_id = $revision";
        command.Parameters.AddWithValue("$revision", revision);
        using var document = JsonDocument.Parse((byte[])(await command.ExecuteScalarAsync().ConfigureAwait(false))!);
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> ReadPresetRegistryAsync(HttpClient client, string evidenceRoot, string phase)
    {
        using var response = await client.GetAsync(new Uri(GraphApi + "/", UriKind.Relative)).ConfigureAwait(false);
        return await SavePresetResponseAsync(response, evidenceRoot, phase).ConfigureAwait(false);
    }

    private static async Task<JsonElement> SendPresetRequestAsync(HttpClient client, string evidenceRoot, string phase, string path, string key, object body, HttpStatusCode expectedStatus = HttpStatusCode.OK)
    {
        var login = await client.GetStringAsync(new Uri("/Account/Login", UriKind.Relative)).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(GraphApi + path, UriKind.Relative));
        request.Content = JsonContent.Create(body);
        request.Headers.Add("Idempotency-Key", key);
        request.Headers.Add("RequestVerificationToken", OwnerBootstrapSession.ExtractAntiforgeryToken(login, "reference preset request"));
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        return await SavePresetResponseAsync(response, evidenceRoot, phase, expectedStatus).ConfigureAwait(false);
    }

    private static async Task<JsonElement> SavePresetResponseAsync(HttpResponseMessage response, string evidenceRoot, string phase, HttpStatusCode expectedStatus = HttpStatusCode.OK)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(evidenceRoot, phase + ".json"), body).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(evidenceRoot, phase + ".status"), ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
        Assert.AreEqual(expectedStatus, response.StatusCode, phase + " " + body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
