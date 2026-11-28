using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Modules;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;

internal sealed class CenteredOperationsFixture
{
    internal const string CameraId = "browser-centered-operations";
    internal const string SatelliteNode = "operations-satellite";
    internal const string SatelliteKind = "predicted-satellites";
    internal const long ReferenceSequence = 103;
    private static readonly DateTimeOffset SceneTime = new(2026, 11, 19, 8, 0, 0, TimeSpan.Zero);
    private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _start = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _attempts;

    internal TaskCompletionSource ReleaseSatellite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal CameraKey Camera { get; } = new(CameraAgentKestrelFixture.AgentId, CameraId);

    internal void Register(IServiceCollection services)
    {
        services.AddSingleton(this);
        services.AddSingleton<ICameraModuleFactory>(provider => new Factory(provider.GetRequiredService<CameraModuleFactory>(), this));
        services.AddSingleton<CenteredViewProducer>();
        services.AddSingleton<ICenteredViewProducer, BoundedViewProducer>();
        // Use the production satellite output binding with a controlled producer and a non-default variant.
        var original = services.Single(service => service.ImplementationInstance is CaptureProcessingStepRegistration registration &&
            registration.Alias == CaptureSatelliteLayerProcessingStep.Alias);
        services.Remove(original);
        services.AddSingleton(new CaptureProcessingStepRegistration(CaptureSatelliteLayerProcessingStep.Alias,
            typeof(SatelliteStep), typeof(CaptureSatelliteLayerOptions), 60, AutoInclude: false));
    }

    internal async Task StartAsync(string root, CancellationToken token)
    {
        await _initialized.Task.WaitAsync(token).ConfigureAwait(false);
        var source = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "journal", "raw-ingress.db"),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        };
        using var connection = new SqliteConnection(source.ConnectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO raw_capture_sequences (agent_id, last_sequence) VALUES ($agent, 100);";
        command.Parameters.AddWithValue("$agent", CameraAgentKestrelFixture.AgentId);
        Assert.AreEqual(1, await command.ExecuteNonQueryAsync(token).ConfigureAwait(false));
        _start.SetResult();
    }

    internal static async Task PrepareAsync(string root)
    {
        var path = Path.Combine(root, "cameraagent.browser.json");
        var config = JsonNode.Parse(await File.ReadAllTextAsync(path).ConfigureAwait(false))!;
        config["centeredExposure"] = new JsonObject
        {
            ["cameraId"] = CameraId,
            ["executionVersion"] = CenteredExposureProfile.CurrentExecutionVersion
        };
        var scene = config["module"]!["options"]!;
        scene["fixedSceneUtc"] = SceneTime;
        scene["fixedSequenceStartUtc"] = SceneTime;
        scene["psfSigmaPixels"] = 1.0;
        scene["psfRadiusPixels"] = 4;
        scene["magnitudeZeroElectronsPerSecond"] = 800_000;
        scene["maximumResults"] = 100;
        var rig = config["rig"]!;
        rig["readout"] = JsonNode.Parse("""
            {
              "roi": { "x": 0, "y": 0, "width": 160, "height": 120 },
              "binX": 1, "binY": 1, "binningAlgorithm": "IdentityV1", "pixelFormat": "Mono16",
              "sampleDepthBits": 16, "containerDepthBits": 16, "packing": "ByteAligned",
              "storedCodeTransform": "RightAlignedV1", "levelCodeSpace": "NativeSample",
              "blackLevel": 0, "whiteLevel": 65535, "strideBytes": 320, "byteOrder": "LittleEndian", "cfaPattern": "None"
            }
            """);
        rig["pipeline"]!["captureInterval"] = "00:00:02";
        rig["pipeline"]!["dayExposure"] = "00:00:01";
        rig["pipeline"]!["nightExposure"] = "00:00:01";
        var setpoint = config["schedule"]!["setpointProfiles"]![0]!;
        setpoint["captureInterval"] = "00:00:02";
        setpoint["exposure"] = "00:00:01";
        var pipeline = config["pipeline"]!;
        var storage = pipeline["steps"]!.AsArray().Single(step => (string?)step?["type"] == "Storage")!.DeepClone();
        storage["dependsOn"] = new JsonArray("$raw");
        pipeline["steps"] = JsonNode.Parse("""
            [
              { "id": "Calibration", "type": "Calibration", "order": 10, "required": false,
                "dependsOn": ["$raw"], "options": { "strategy": "None", "outputVariant": "operations-linear" } },
              { "id": "ProjectedScene", "type": "ProjectedScene", "order": 15, "required": true,
                "dependsOn": ["$raw"], "publication": { "persistence": "durable-local" } },
              { "id": "operations-satellite", "type": "CaptureSatelliteLayer", "order": 60, "required": false,
                "dependsOn": ["$raw"], "publication": { "persistence": "durable-local" } }
            ]
            """);
        pipeline["steps"]!.AsArray().Add(storage);
        await File.WriteAllTextAsync(path, config.ToJsonString()).ConfigureAwait(false);
        await InstallRegistrationStarsAsync(root).ConfigureAwait(false);
    }

    private static async Task InstallRegistrationStarsAsync(string root)
    {
        // The stock browser catalog is too sparse for a verified relative registration with a holdout.
        (double X, double Y)[] offsets =
        [
            (30, -53), (90, 6), (-16, 21), (-45, -38), (-16, -19), (28, 52), (-60, -22), (-61, 18),
            (-47, 35), (-22, 45), (15, -40), (4, 41), (41, 11), (20, 6), (-70, -49), (-97, -5),
            (-21, -94), (73, 46), (54, 74), (-89, -23), (36, -86), (-96, 31), (13, -98),
            (-38, 79), (-82, 16), (58, -64), (20, 87), (1, 98)
        ];
        var snapshot = Path.Combine(root, "catalog", "current");
        var database = Path.Combine(snapshot, "hyg_v44.sqlite");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ConnectionString))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO celestial_objects (id, display_name, right_ascension_hours, declination_degrees, magnitude, color_index)
                VALUES ($id, $id, $ra, $dec, $magnitude, 0);
                """;
            var id = insert.Parameters.Add("$id", SqliteType.Text);
            var ra = insert.Parameters.Add("$ra", SqliteType.Real);
            var dec = insert.Parameters.Add("$dec", SqliteType.Real);
            var magnitude = insert.Parameters.Add("$magnitude", SqliteType.Real);
            for (var index = 0; index < offsets.Length; index++)
            {
                var (x, y) = offsets[index];
                var altitude = 90 - 0.54 * Math.Sqrt(x * x + y * y) * 90 / 58;
                var azimuth = (Math.Atan2(x, -y) * 180 / Math.PI + 360) % 360;
                var coordinates = CoordinateTransforms.HorizontalToEquatorial(new(altitude, azimuth), SceneTime, 35.347, -113.878);
                var point = EquatorialPrecession.PrecessToJ2000(coordinates, SceneTime);
                id.Value = "operations-star-" + index.ToString(CultureInfo.InvariantCulture);
                ra.Value = point.RightAscensionHours;
                dec.Value = point.DeclinationDegrees;
                magnitude.Value = 4.6 + index * 0.011;
                await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }
        var manifestPath = Path.Combine(snapshot, "manifest.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath).ConfigureAwait(false))!;
        var descriptor = manifest["database"]!;
        var bytes = await File.ReadAllBytesAsync(database).ConfigureAwait(false);
        descriptor["length"] = bytes.Length;
        descriptor["sha256"] = Convert.ToHexString(SHA256.HashData(bytes));
        descriptor["rowCount"] = descriptor["rowCount"]!.GetValue<int>() + offsets.Length;
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString()).ConfigureAwait(false);
    }

    private sealed class Factory(CameraModuleFactory factory, CenteredOperationsFixture fixture) : ICameraModuleFactory
    {
        public ICameraModule Create(string moduleType) => factory.Create(moduleType) is VirtualSkyCameraModule sky
            ? new BoundedCamera(sky, fixture) : throw new InvalidOperationException("The fixture expects VirtualSky.");
    }

    [SuppressMessage("Performance", "CA1812", Justification = "Activated by dependency injection.")]
    private sealed class BoundedViewProducer(CenteredViewProducer producer, SqliteCaptureProcessingStore store) : ICenteredViewProducer
    {
        public ValueTask<CenteredView?> PublishSingleAsync(CenteredViewStream stream, Guid executionId, long referenceOrder,
            LinearExposureLookup result, CenteredViewPreviewOptions options, CancellationToken cancellationToken = default) =>
            producer.PublishSingleAsync(stream, executionId, referenceOrder, result, options, cancellationToken);

        public async ValueTask<CenteredView?> PublishRegisteredStackAsync(CenteredViewStream stream, string windowIdentity,
            Guid registrationEvidenceArtifactId, IExposureProductReader exposureProducts, CenteredViewPreviewOptions options,
            CancellationToken cancellationToken = default)
        {
            var window = await store.ReadCenteredWindowDispositionAsync(windowIdentity, cancellationToken).ConfigureAwait(false);
            // Later partial windows can become eligible even when the camera stops after exposure 105.
            // Hold their publication so the browser can inspect both live revisions of reference 103.
            if (window?.Closure.Center.Input?.CaptureSequence is > ReferenceSequence)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return await producer.PublishRegisteredStackAsync(stream, windowIdentity, registrationEvidenceArtifactId,
                exposureProducts, options, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class BoundedCamera(VirtualSkyCameraModule camera, CenteredOperationsFixture fixture) :
        ICameraModule, ICameraSetpointController, ICameraSimulatedSkyRegime, ICameraBoundExposureCapture
    {
        public string Id => camera.Id;
        public string DisplayName => camera.DisplayName;
        public string ModuleType => camera.ModuleType;
        public CameraModuleCapabilities Capabilities => camera.Capabilities;
        public bool EnforcesBoundStartLimit => camera.EnforcesBoundStartLimit;
        public CaptureSolarRegime? SimulatedSolarRegime => ((ICameraSimulatedSkyRegime)camera).SimulatedSolarRegime;

        public async Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken)
        {
            await camera.InitializeAsync(config, cancellationToken).ConfigureAwait(false);
            fixture._initialized.TrySetResult();
            await fixture._start.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken) =>
            camera.CaptureAsync(request, cancellationToken);

        public async Task<CaptureResult> CaptureBoundAsync(ExposureAttemptKey attempt, CaptureRequest request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref fixture._attempts) > 5)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return await ((ICameraBoundExposureCapture)camera).CaptureBoundAsync(attempt, request, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<DateTimeOffset> ApplySetpointAsync(CaptureSetpoint setpoint, CancellationToken cancellationToken) =>
            ((ICameraSetpointController)camera).ApplySetpointAsync(setpoint, cancellationToken);

        public ValueTask DisposeAsync() => camera.DisposeAsync();
    }

    [SuppressMessage("Performance", "CA1812", Justification = "Activated by the graph step registry.")]
    internal sealed class SatelliteStep(CaptureProcessingStepMetadata metadata, CaptureSatelliteLayerOptions options, CenteredOperationsFixture fixture)
        : ConfigurableCaptureProcessingStep<CaptureSatelliteLayerOptions>(metadata, options), ICaptureProcessingGraphStep, IMultiOutputCaptureProcessingGraphStep
    {
        private const string Version = "operations-test-source-v1";
        private const string StatusVariant = "operations-test-satellite-status";
        private static readonly ProcessingRecipeDefinition StatusRecipe = new("operations-satellite-status", "1.0.0", Version,
            ProcessingOperationKind.Transform);
        public bool Enabled => true;
        public string RecipeName => PresentationProcessingProducts.LayerRecipeName;
        public FrameArtifactRole OutputRole => FrameArtifactRole.Metadata;
        public string OutputVariant => "operations-test-satellite-layer";
        public string OutputSchemaVersion => PresentationLayerPayloadV1.CurrentSchemaVersion;
        public string OutputMediaType => PresentationLayerPayloadJson.MediaType;
        public ProcessingRecipeDefinition SharedOutputRecipe { get; } = new(PresentationProcessingProducts.LayerRecipeName,
            "1.0.0", Version, ProcessingOperationKind.Transform);
        public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } = new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw };
        public IReadOnlyList<CaptureProcessingOutputDescriptor> Outputs =>
        [
            new(OutputRole, OutputVariant, RecipeName, OutputSchemaVersion, SharedOutputRecipe, MediaType: OutputMediaType),
            new(OutputRole, StatusVariant, StatusRecipe.Name, CaptureSatelliteProducts.StatusSchema, StatusRecipe,
                MediaType: CaptureSatelliteProducts.StatusMediaType)
        ];

        public override async ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            var manifest = context.RawCapture?.Manifest ?? throw new InvalidOperationException("No admitted input.");
            var delayed = manifest.CenteredExposure?.Source.Ordinal == 5;
            if (delayed)
                await fixture.ReleaseSatellite.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await context.EnsureRawFrameAsync(cancellationToken).ConfigureAwait(false);
            var descriptor = manifest.Descriptor;
            var raw = CameraAgentRecipeExecutionAdapter.CreateArtifact(context.Config, context.Artifacts!.Raw,
                descriptor.Artifact.Variant, context.AcquisitionTiming, descriptor);
            // Supply a committed prediction status with the actual shutter times, just as the ordinary publisher requires.
            var status = new CaptureSatelliteStatusV1(CaptureSatelliteProducts.StatusSchema, raw.DescriptorIdentitySha256!,
                "Complete", delayed ? "Fixture segment" : "NoExposureSegments", delayed ? 1 : 0, "ConfiguredRig",
                new(descriptor.Timing.ExposureStartedUtc, descriptor.Timing.ExposureEndedUtc),
                new("fixture", OrbitalPopulationCompleteness.Unknown, []), null, null, "Bundled", "Unknown", delayed, "Unknown");
            var bytes = OrbitalContract.SerializeCanonical(status);
            var checksum = PayloadChecksum.ComputeSha256(bytes);
            var recipe = ProcessingIdentity.CreateRecipeIdentity(StatusRecipe, JsonSerializer.SerializeToElement(new { checksum }));
            Guid[] parents = [raw.ArtifactId];
            var output = new ProcessingProduct(OutputRole, StatusVariant,
                ProcessingIdentity.CreateOutputIdentity(OutputRole, StatusVariant, recipe.IdentitySha256, parents),
                CaptureSatelliteProducts.StatusMediaType, null, bytes, checksum, recipe, [new(StatusRecipe.Name, Version)],
                parents, raw.Integration, raw.Compatibility)
            {
                Kind = ProcessingProductKind.Metadata,
                SchemaVersion = CaptureSatelliteProducts.StatusSchema,
                ContentIdentitySha256 = checksum
            };
            var artifact = CameraAgentRecipeExecutionAdapter.CreateArtifact(context, output);
            var payload = PresentationLayerPayloadJson.Create(checksum, descriptor.Layout.Width,
                descriptor.Layout.Height, segments: delayed ? [new(new(31.25, 54.75), new(106.75, 70.25), 2, new(255, 220, 64))] : []);
            LayeredPresentationCaptureProcessing.Add(context, output,
                PresentationProcessingProducts.CreateLayerProduct(payload, OutputVariant, [artifact], Version));
        }
    }
}
