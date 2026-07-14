using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;

/// <summary>Catalog-backed deterministic virtual monochrome, RGB, or RGGB RAW16 camera.</summary>
public sealed class VirtualSkyCameraModule(
    TimeProvider timeProvider,
    ICelestialCatalog catalog,
    IProjectedSceneStore sceneStore,
    IConstellationTopology? constellationTopology = null,
    IPlanetEphemeris? planetEphemeris = null) : ICameraModule
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private CameraModuleConfig? _config;
    private VirtualSkyCameraModuleOptions _options = new();
    private long _captureSequence;

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string DisplayName => "Virtual Sky Camera";
    public string ModuleType => "VirtualSky";
    public CameraModuleCapabilities Capabilities => CameraModuleCapabilities.StillFrames;

    public Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRig(config.Rig);
        _config = config;
        if (config.ModuleOptions is { } options)
        {
            _options = JsonSerializer.Deserialize<VirtualSkyCameraModuleOptions>(options.GetRawText(), SerializerOptions)
                ?? new VirtualSkyCameraModuleOptions();
        }

        _options.Validate();
        if (_options.Asi178Sensor.Enabled != (config.Rig.Sensor.PixelFormat == CameraPixelFormat.BayerRggb16) ||
            _options.Asi174Sensor.Enabled && config.Rig.Sensor.PixelFormat != CameraPixelFormat.Mono16)
        {
            throw new ArgumentException("The configured physical sensor model must match the raw pixel format.", nameof(config));
        }
        _captureSequence = 0;
        return Task.CompletedTask;
    }

    public async Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var config = _config ?? throw new InvalidOperationException("Module has not been initialized.");
        if (request.Mode != CaptureMode.Still)
        {
            throw new NotSupportedException("Virtual sky camera supports still captures only.");
        }

        var setpoint = request.RequestedSetpoint ?? new CaptureSetpoint(
            config.Rig.Pipeline.NightExposure, config.Rig.Pipeline.NightGain, null, null);
        var sensor = config.Rig.Sensor;
        var projection = RigProjectionContextFactory.Create(config.Rig);
        var configuredMetadata = new CatalogMetadata(
            _options.CatalogName,
            _options.CatalogVersion,
            _options.CatalogSourceUrl,
            _options.CatalogChecksumSha256,
            _options.CatalogLicense,
            _options.CatalogSchemaVersion);
        var metadata = (catalog as ICelestialCatalogMetadataSource)?.Metadata ?? configuredMetadata;
        var sceneRequest = new VisibleSceneRequest(
            request.RequestedStartUtc,
            new ObserverLocation(config.Observatory.LatitudeDegrees, config.Observatory.LongitudeDegrees,
                config.Observatory.ElevationMeters),
            projection,
            new CatalogQuery(_options.MaximumMagnitude, _options.MaximumResults),
            metadata,
            horizonPolicy: HorizonPolicy.GeometricHorizon,
            projectionVersion: config.Rig.Optics.CalibrationVersion,
            algorithmVersion: "visible-scene-iau1976-constellation-v2",
            constellationIds: _options.ConstellationIds,
            solarSystemBodies: ParseSolarSystemBodies(_options.SolarSystemBodies),
            includeConstellationEndpointStars: _options.IncludeConstellationEndpointStars);
        var scene = await new VisibleSceneBuilder(catalog, constellationTopology, planetEphemeris)
            .BuildAsync(sceneRequest, cancellationToken).ConfigureAwait(false);
        var layout = new ImageLayout(sensor.WidthPixels, sensor.HeightPixels, sensor.PixelFormat,
            sensor.StrideBytes ?? checked(sensor.WidthPixels * ImageLayout.BytesPerPixel(sensor.PixelFormat)));
        var start = timeProvider.GetTimestamp();
        var captureSequence = Interlocked.Increment(ref _captureSequence) - 1;
        var render = sensor.PixelFormat switch
        {
            CameraPixelFormat.Mono16 => Mono16SceneRenderer.Render(
                scene, layout, CreateMonoOptions(setpoint, captureSequence, projection)),
            CameraPixelFormat.Rgb24 => Rgb24CompatibilityRenderer.Render(scene, layout, CreateRgbOptions(setpoint)),
            CameraPixelFormat.BayerRggb16 => BayerRggb16Renderer.Render(
                scene, layout, CreateBayerOptions(setpoint, captureSequence, projection)),
            _ => throw new UnreachableException()
        };
        var sceneId = CreateSceneId(
            sceneRequest,
            setpoint,
            _options,
            sensor,
            planetEphemeris?.ModelVersion,
            constellationTopology?.Metadata);
        sceneStore.Put(sceneId, scene);
        var provenance = new SceneProvenance(
            sceneId,
            _options.RigProfileVersion,
            metadata.Name,
            metadata.Version,
            metadata.Checksum,
            config.Rig.Optics.ProjectionModel,
            RigProjectionContextFactory.AlgorithmVersion,
            sceneRequest.AlgorithmVersion,
            sensor.SensorRecipeVersion,
            metadata.SourceUrl,
            metadata.License,
            metadata.SchemaVersion,
            (catalog as ICelestialCatalogMetadataSource)?.PreprocessingVersion,
            scene.Objects.Select(static item => new ProjectedObjectProvenance(
                item.Id, item.DisplayName, item.Pixel.X, item.Pixel.Y, item.Magnitude)).ToArray(),
            scene.Segments.Select(static item => new ProjectedSegmentProvenance(
                item.ConstellationId, item.FromObjectId, item.ToObjectId,
                item.FromPixel.X, item.FromPixel.Y, item.ToPixel.X, item.ToPixel.Y, item.PartIndex)).ToArray(),
            sceneRequest.SolarSystemBodies.Count > 0 ? planetEphemeris?.ModelVersion : null,
            sceneRequest.ConstellationIds.Count > 0 ? constellationTopology?.Metadata.Version : null,
            sceneRequest.ConstellationIds.Count > 0 ? constellationTopology?.Metadata.SourceUrl : null,
            sceneRequest.ConstellationIds.Count > 0 ? constellationTopology?.Metadata.SourceSha256 : null,
            sceneRequest.ConstellationIds.Count > 0 ? constellationTopology?.Metadata.License : null,
            sceneRequest.ConstellationIds.Count > 0 ? constellationTopology?.Metadata.PreprocessingVersion : null,
            sceneRequest.ConstellationIds,
            sceneRequest.IncludeConstellationEndpointStars,
            RigProfileHashSha256: RigProjectionContextFactory.CreateProfileHashSha256(config.Rig),
            ProjectionCalibrationVersion: config.Rig.Optics.CalibrationVersion);
        var extra = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sceneId"] = sceneId,
            ["visibleObjectCount"] = scene.Objects.Count.ToString(CultureInfo.InvariantCulture),
            ["constellationStrokeCount"] = scene.Segments.Count.ToString(CultureInfo.InvariantCulture),
            ["renderAlgorithm"] = render.AlgorithmVersion,
            ["renderMean"] = render.Statistics.Mean.ToString("R", CultureInfo.InvariantCulture),
            ["compatibilityLabel"] = render.CompatibilityLabel,
            ["includeConstellationEndpointStars"] = sceneRequest.IncludeConstellationEndpointStars.ToString()
        };
        if (_options.Asi174Sensor.Enabled)
        {
            var response = Asi174MmSensorModel.Resolve(setpoint.Gain, _options.Asi174Sensor.BlackLevelAdu);
            extra["sensorModel"] = Asi174MmSensorModel.Version;
            extra["gainUnits"] = "ZWO 0.1 dB";
            extra["electronsPerAdu"] = response.ElectronsPerAdu.ToString("R", CultureInfo.InvariantCulture);
            extra["readNoiseElectrons"] = response.ReadNoiseElectrons.ToString("R", CultureInfo.InvariantCulture);
            extra["fullWellElectrons"] = response.FullWellElectrons.ToString("R", CultureInfo.InvariantCulture);
            extra["adcBitDepth"] = response.AdcBitDepth.ToString(CultureInfo.InvariantCulture);
            extra["blackLevelAdu"] = response.BlackLevelAdu.ToString("R", CultureInfo.InvariantCulture);
            extra["captureSequence"] = captureSequence.ToString(CultureInfo.InvariantCulture);
        }
        else if (_options.Asi178Sensor.Enabled)
        {
            var response = Asi178McSensorModel.Resolve(setpoint.Gain, _options.Asi178Sensor.BlackLevelContainerAdu);
            extra["sensorModel"] = Asi178McSensorModel.Version;
            extra["gainUnits"] = "ZWO 0.1 dB";
            extra["electronsPerAdu"] = response.ElectronsPerAdu.ToString("R", CultureInfo.InvariantCulture);
            extra["readNoiseElectrons"] = response.ReadNoiseElectrons.ToString("R", CultureInfo.InvariantCulture);
            extra["fullWellElectrons"] = response.FullWellElectrons.ToString("R", CultureInfo.InvariantCulture);
            extra["sensorAdcBitDepth"] = "14";
            extra["containerBitDepth"] = "16";
            extra["cfaPattern"] = "RGGB";
            extra["blackLevelAdu"] = response.BlackLevelAdu.ToString("R", CultureInfo.InvariantCulture);
            extra["captureSequence"] = captureSequence.ToString(CultureInfo.InvariantCulture);
        }
        var frame = new CameraFrame(
            request.RequestedStartUtc.ToUniversalTime(), sensor.WidthPixels, sensor.HeightPixels, sensor.PixelFormat,
            render.Pixels,
            new FrameMetadata(setpoint.Exposure, setpoint.Gain, double.NaN, "VirtualSky", extra, Scene: provenance),
            layout.StrideBytes);
        return new CaptureResult(frame, setpoint, timeProvider.GetElapsedTime(start), request.Mode, false)
        {
            // VirtualSky models exposure energy without waiting wall-clock exposure time.
            AcquisitionTiming = new CaptureAcquisitionTiming(
                request.RequestedStartUtc,
                request.RequestedStartUtc,
                request.RequestedStartUtc)
        };
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Mono16SceneRenderOptions CreateMonoOptions(
        CaptureSetpoint setpoint, long captureSequence, ProjectionContext projection) => new()
        {
            ExposureSeconds = setpoint.Exposure.TotalSeconds,
            Gain = setpoint.Gain,
            MagnitudeZeroElectronsPerSecond = _options.MagnitudeZeroElectronsPerSecond,
            BackgroundElectronsPerSecond = _options.ResolveBackgroundElectronsPerSecond(projection),
            PsfSigmaPixels = _options.PsfSigmaPixels,
            PsfRadiusPixels = _options.PsfRadiusPixels,
            VignettingStrength = _options.VignettingStrength,
            Bias = _options.Asi174Sensor.Enabled ? 0 : _options.Bias,
            ReadNoiseStandardDeviation = _options.Asi174Sensor.Enabled ? 0 : _options.ReadNoiseStandardDeviation,
            ShotNoiseEnabled = _options.Asi174Sensor.Enabled || _options.ShotNoiseEnabled,
            DarkCurrentElectronsPerSecond = _options.DarkCurrentElectronsPerSecond,
            DarkNoiseEnabled = _options.Asi174Sensor.Enabled && _options.DarkCurrentElectronsPerSecond > 0,
            Seed = _options.Asi174Sensor.Enabled
                ? unchecked(_options.Seed + (int)(captureSequence * 104729))
                : _options.Seed,
            SensorResponse = _options.Asi174Sensor.Enabled
                ? Asi174MmSensorModel.Resolve(setpoint.Gain, _options.Asi174Sensor.BlackLevelAdu)
                : null
        };

    private Rgb24CompatibilityRenderOptions CreateRgbOptions(CaptureSetpoint setpoint) => new()
    {
        ExposureSeconds = setpoint.Exposure.TotalSeconds,
        Gain = setpoint.Gain,
        MagnitudeZeroElectronsPerSecond = _options.MagnitudeZeroElectronsPerSecond,
        BackgroundElectronsPerSecond = _options.ResolveBackgroundElectronsPerSecond(),
        PsfSigmaPixels = _options.PsfSigmaPixels,
        PsfRadiusPixels = _options.PsfRadiusPixels,
        VignettingStrength = _options.VignettingStrength,
        Bias = _options.Bias,
        ReadNoiseStandardDeviation = _options.ReadNoiseStandardDeviation,
        ShotNoiseEnabled = _options.ShotNoiseEnabled,
        DarkCurrentElectronsPerSecond = _options.DarkCurrentElectronsPerSecond,
        Seed = _options.Seed
    };

    private BayerRggb16RenderOptions CreateBayerOptions(
        CaptureSetpoint setpoint, long captureSequence, ProjectionContext projection) => new()
        {
            ExposureSeconds = setpoint.Exposure.TotalSeconds,
            Gain = setpoint.Gain,
            MagnitudeZeroElectronsPerSecond = _options.MagnitudeZeroElectronsPerSecond,
            BackgroundElectronsPerSecond = _options.ResolveBackgroundElectronsPerSecond(projection),
            PsfSigmaPixels = _options.PsfSigmaPixels,
            PsfRadiusPixels = _options.PsfRadiusPixels,
            VignettingStrength = _options.VignettingStrength,
            ShotNoiseEnabled = true,
            DarkCurrentElectronsPerSecond = _options.DarkCurrentElectronsPerSecond,
            DarkNoiseEnabled = _options.DarkCurrentElectronsPerSecond > 0,
            Seed = unchecked(_options.Seed + (int)(captureSequence * 104729)),
            SensorResponse = Asi178McSensorModel.Resolve(
                setpoint.Gain, _options.Asi178Sensor.BlackLevelContainerAdu)
        };

    private static void ValidateRig(CameraRigConfig rig)
    {
        ArgumentNullException.ThrowIfNull(rig);
        if (rig.Sensor.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.Rgb24 or CameraPixelFormat.BayerRggb16))
        {
            throw new NotSupportedException("VirtualSky supports Mono16, RGB24 compatibility, or BayerRggb16 output.");
        }

        var model = RigProjectionContextFactory.ParseModel(rig.Optics.ProjectionModel);
        var fisheye = model != ProjectionModel.Perspective;
        if (fisheye && rig.Optics.LensKind is not (LensKind.Unspecified or LensKind.Fisheye) ||
            !fisheye && rig.Optics.LensKind is not (LensKind.Unspecified or LensKind.Rectilinear or LensKind.Telescope))
        {
            throw new NotSupportedException("Projection model and optical lens kind are incompatible.");
        }

        if (!double.IsFinite(rig.Optics.FieldOfViewDegrees) || rig.Optics.FieldOfViewDegrees <= 0 ||
            rig.Optics.FieldOfViewDegrees >= (fisheye ? 360 : 180) ||
            model == ProjectionModel.OrthographicFisheye && rig.Optics.FieldOfViewDegrees > 180 ||
            !double.IsFinite(rig.Optics.RollDegrees) || Math.Abs(rig.Optics.RollDegrees) > 1e-9 ||
            rig.Optics.Crop is not null)
        {
            throw new NotSupportedException("The optical field, crop, or legacy roll is unsupported.");
        }

        if (rig.Sensor.WidthPixels <= 0 || rig.Sensor.HeightPixels <= 0 || !double.IsFinite(rig.Sensor.PixelSizeMicrons) || rig.Sensor.PixelSizeMicrons <= 0 ||
            rig.Sensor.ByteOrder != SampleByteOrder.LittleEndian)
        {
            throw new ArgumentOutOfRangeException(nameof(rig), "Sensor geometry and little-endian layout must be valid.");
        }


        if (rig.Sensor.PixelFormat == CameraPixelFormat.Mono16 &&
            (rig.Sensor.ColorMode != SensorColorMode.Mono || rig.Sensor.ResponseMode != SensorResponseMode.Monochrome) ||
            rig.Sensor.PixelFormat == CameraPixelFormat.Rgb24 &&
            (rig.Sensor.ColorMode != SensorColorMode.Color || rig.Sensor.ResponseMode != SensorResponseMode.RenderedRgb) ||
            rig.Sensor.PixelFormat == CameraPixelFormat.BayerRggb16 &&
            (rig.Sensor.ColorMode != SensorColorMode.Color || rig.Sensor.ResponseMode != SensorResponseMode.BayerRaw))
        {
            throw new ArgumentException("Sensor color and response modes must agree with the selected pixel format.", nameof(rig));
        }

        _ = RigProjectionContextFactory.Create(rig);
    }

    private static string CreateSceneId(
        VisibleSceneRequest request,
        CaptureSetpoint setpoint,
        VirtualSkyCameraModuleOptions options,
        SensorProfile sensor,
        string? ephemerisModelVersion,
        ConstellationTopologyMetadata? constellationTopologyMetadata)
    {
        var value = JsonSerializer.Serialize(new
        {
            request.Utc,
            request.Observer,
            request.Projection,
            request.CatalogQuery,
            request.CatalogMetadata,
            request.Refraction,
            request.HorizonPolicy,
            request.ProjectionVersion,
            request.AlgorithmVersion,
            request.ConstellationIds,
            request.SolarSystemBodies,
            Setpoint = setpoint,
            Options = options,
            Sensor = sensor,
            EphemerisModelVersion = ephemerisModelVersion,
            ConstellationTopology = constellationTopologyMetadata
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static SolarSystemBody[] ParseSolarSystemBodies(IEnumerable<string> names)
        => names.Select(name => Enum.Parse<SolarSystemBody>(name, true)).Distinct().ToArray();
}

/// <summary>Validated deterministic scene and simulated sensor parameters.</summary>
public sealed class VirtualSkyCameraModuleOptions
{
    public int Seed { get; init; } = 2025;
    public double MaximumMagnitude { get; init; } = 6.5;
    public int MaximumResults { get; init; } = 2000;
    public double MagnitudeZeroElectronsPerSecond { get; init; } = 1000;
    public double? BackgroundElectronsPerSecond { get; init; }
    public int BortleClass { get; init; } = 3;
    public double BortleThreeBackgroundElectronsPerSecond { get; init; } = 2;
    public double PsfSigmaPixels { get; init; } = 1;
    public double PsfRadiusPixels { get; init; } = 4;
    public double VignettingStrength { get; init; } = 0.25;
    public double Bias { get; init; } = 64;
    public double ReadNoiseStandardDeviation { get; init; } = 1.5;
    public bool ShotNoiseEnabled { get; init; }
    public double DarkCurrentElectronsPerSecond { get; init; }
    public Asi174MmSensorOptions Asi174Sensor { get; init; } = new();
    public Asi178McSensorOptions Asi178Sensor { get; init; } = new();
    public string CatalogName { get; init; } = "HYG";
    public string CatalogVersion { get; init; } = "4.2-test-fixture";
    public Uri CatalogSourceUrl { get; init; } = new("https://astronexus.com/projects/hyg");
    public string CatalogLicense { get; init; } = "CC BY-SA 4.0";
    public string CatalogChecksumSha256 { get; init; } = "F80689217769A6B13C1B9BFB9711485D3CB1AD8DE009D3D6B0F0B0A4F1FA9840";
    public string CatalogSchemaVersion { get; init; } = "2";
    public string RigProfileVersion { get; init; } = "virtual-asi174-v1";
    public IReadOnlyList<string> ConstellationIds { get; init; } = Array.Empty<string>();
    public bool IncludeConstellationEndpointStars { get; init; }
    public IReadOnlyList<string> SolarSystemBodies { get; init; } = Array.Empty<string>();

    internal void Validate()
    {
        if (!double.IsFinite(MaximumMagnitude) || MaximumResults is < 1 or > 2000 || BortleClass is < 1 or > 9 ||
            BackgroundElectronsPerSecond is { } background && (!double.IsFinite(background) || background < 0) ||
            !double.IsFinite(BortleThreeBackgroundElectronsPerSecond) || BortleThreeBackgroundElectronsPerSecond < 0 ||
            CatalogSourceUrl is null || !CatalogSourceUrl.IsAbsoluteUri || CatalogChecksumSha256.Length != 64 ||
            Asi174Sensor is null || Asi178Sensor is null || Asi174Sensor.Enabled && Asi178Sensor.Enabled ||
            ConstellationIds is null || ConstellationIds.Any(string.IsNullOrWhiteSpace) ||
            SolarSystemBodies is null || SolarSystemBodies.Any(name =>
                string.IsNullOrWhiteSpace(name) || !Enum.TryParse<SolarSystemBody>(name, true, out var body) || !Enum.IsDefined(body)))
        {
            throw new ArgumentOutOfRangeException(nameof(VirtualSkyCameraModuleOptions));
        }

        if (Asi174Sensor.Enabled)
        {
            _ = Asi174MmSensorModel.Resolve(0, Asi174Sensor.BlackLevelAdu);
            _ = Asi174MmSensorModel.Resolve(400, Asi174Sensor.BlackLevelAdu);
        }
        if (Asi178Sensor.Enabled)
        {
            _ = Asi178McSensorModel.Resolve(0, Asi178Sensor.BlackLevelContainerAdu);
            _ = Asi178McSensorModel.Resolve(400, Asi178Sensor.BlackLevelContainerAdu);
        }

        new Mono16SceneRenderOptions
        {
            MagnitudeZeroElectronsPerSecond = MagnitudeZeroElectronsPerSecond,
            BackgroundElectronsPerSecond = ResolveBackgroundElectronsPerSecond(),
            PsfSigmaPixels = PsfSigmaPixels,
            PsfRadiusPixels = PsfRadiusPixels,
            VignettingStrength = VignettingStrength,
            Bias = Bias,
            ReadNoiseStandardDeviation = ReadNoiseStandardDeviation,
            DarkCurrentElectronsPerSecond = DarkCurrentElectronsPerSecond
        }.Validate();
    }

    internal double ResolveBackgroundElectronsPerSecond()
        => BackgroundElectronsPerSecond ?? SkyBrightnessModel.BackgroundElectronsPerSecond(
            BortleClass, BortleThreeBackgroundElectronsPerSecond);

    internal double ResolveBackgroundElectronsPerSecond(ProjectionContext projection)
        => BackgroundElectronsPerSecond ?? (Asi174Sensor.Enabled || Asi178Sensor.Enabled
            ? SkyBrightnessModel.PhotometricBackgroundElectronsPerSecond(
                BortleClass, MagnitudeZeroElectronsPerSecond,
                projection.FocalLengthXPixels, projection.FocalLengthYPixels)
            : ResolveBackgroundElectronsPerSecond());
}

/// <summary>Configures the documented ASI174MM 12-bit electron-domain response.</summary>
public sealed class Asi174MmSensorOptions
{
    public bool Enabled { get; init; }

    /// <summary>Provisional native-ADU pedestal pending measurement of the installed camera offset.</summary>
    public double BlackLevelAdu { get; init; } = 64;
}

/// <summary>Configures the sourced ASI178MC 14-bit sensor response in its RAW16 container.</summary>
public sealed class Asi178McSensorOptions
{
    public bool Enabled { get; init; }

    /// <summary>Provisional RAW16 pedestal pending controlled bias characterization.</summary>
    public double BlackLevelContainerAdu { get; init; } = 64;
}
