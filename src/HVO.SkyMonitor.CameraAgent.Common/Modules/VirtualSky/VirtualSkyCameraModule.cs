using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;
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
    IPlanetEphemeris? planetEphemeris = null,
    IProjectedSceneStagingStore? stagingStore = null) :
    ICameraModule,
    ICameraSetpointController,
    ICameraModuleConfigurationPreflight
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private CameraModuleConfig? _config;
    private VirtualSkyCameraModuleOptions _options = new();
    private VirtualCloudField? _cloudField;
    private VirtualTransientScenario? _transientScenario;
    private ResolvedSensorReadout? _resolvedReadout;
    private readonly object _virtualCalibrationCacheLock = new();
    private readonly object _fixedSequenceLock = new();
    private PreparedVirtualCalibration? _preparedVirtualCalibration;
    private long _captureSequence;
    private long _fixedSequenceElapsedTicks;
    private bool _stageProjectedScene;

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string DisplayName => "Virtual Sky Camera";
    public string ModuleType => "VirtualSky";
    public CameraModuleCapabilities Capabilities => CameraModuleCapabilities.StillFrames;

    public Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_virtualCalibrationCacheLock)
        {
            _preparedVirtualCalibration = null;
        }
        if (config.ModuleOptions is { } options)
        {
            _options = JsonSerializer.Deserialize<VirtualSkyCameraModuleOptions>(options.GetRawText(), SerializerOptions)
                ?? new VirtualSkyCameraModuleOptions();
        }

        _options.Validate();
        ValidateRig(config.Rig, _options);
        _resolvedReadout = config.Rig.Readout is null
            ? null
            : SensorReadoutResolver.Resolve(config.Rig.Sensor, config.Rig.Readout);
        ValidateVirtualCalibration(_options.VirtualCalibration, _resolvedReadout);
        _config = config;
        _stageProjectedScene = config.ResolveProcessingSteps().Any(static step =>
            step.Enabled != false &&
            (string.Equals(step.Type, "ProjectedScene", StringComparison.OrdinalIgnoreCase) ||
             step.Type.Contains("ProjectedSceneCaptureProcessingStep", StringComparison.Ordinal)));
        var outputWidth = _resolvedReadout?.Layout.Width ?? config.Rig.Sensor.WidthPixels;
        var outputHeight = _resolvedReadout?.Layout.Height ?? config.Rig.Sensor.HeightPixels;
        if (_options.SyntheticCalibration is { } syntheticCalibration)
        {
            syntheticCalibration.Validate(outputWidth, outputHeight);
        }
        if (_options.CloudScenario is { } configuredCloud)
        {
            _options.CloudScenario = configuredCloud with
            {
                ScenarioId = configuredCloud.ComputeCanonicalScenarioId()
            };
        }
        _cloudField = _options.CloudScenario is null ? null : new VirtualCloudField(_options.CloudScenario);
        if (_options.TransientScenario is { } configuredTransient)
        {
            configuredTransient.ValidateSensorBounds(config.Rig.Sensor.WidthPixels, config.Rig.Sensor.HeightPixels);
            _options.TransientScenario = configuredTransient with
            {
                ScenarioId = configuredTransient.ComputeCanonicalScenarioId()
            };
        }
        _transientScenario = _options.TransientScenario is null
            ? null
            : new VirtualTransientScenario(_options.TransientScenario);
        ValidatePixelFormat(config, _options);
        _captureSequence = 0;
        _fixedSequenceElapsedTicks = 0;
        return Task.CompletedTask;
    }

    void ICameraModuleConfigurationPreflight.ValidateConfiguration(CameraModuleConfig config)
        => _ = ValidateConfiguration(config);

    private static void ValidatePixelFormat(CameraModuleConfig config, VirtualSkyCameraModuleOptions options)
    {
        var pixelFormat = config.Rig.Sensor.PixelFormat;
        if (options.Asi174Sensor.Enabled && pixelFormat != CameraPixelFormat.Mono16 ||
            options.Asi178Sensor.Enabled && pixelFormat != CameraPixelFormat.BayerRggb16 ||
            options.Asi676Enabled && pixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16) ||
            pixelFormat == CameraPixelFormat.BayerRggb16 &&
            !options.Asi178Sensor.Enabled && !options.Asi676Enabled && options.SyntheticCalibration is null &&
            config.Rig.Sensor.SimulationResponse is null ||
            options.SyntheticCalibration is not null &&
            (pixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16) ||
                options.Asi174Sensor.Enabled || options.Asi178Sensor.Enabled || options.Asi676Enabled))
        {
            throw new ArgumentException("The configured physical sensor model must match the raw pixel format.", nameof(config));
        }
    }

    public ValueTask<DateTimeOffset> ApplySetpointAsync(
        CaptureSetpoint setpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(setpoint);
        cancellationToken.ThrowIfCancellationRequested();
        if (setpoint.Exposure < TimeSpan.Zero || !double.IsFinite(setpoint.Gain) || setpoint.Gain < 0 ||
            setpoint.TargetFps is { } fps && (!double.IsFinite(fps) || fps <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(setpoint));
        }
        return ValueTask.FromResult(timeProvider.GetUtcNow().ToUniversalTime());
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Best-effort stage cleanup must preserve the original capture failure or cancellation.")]
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
        var outputProjection = RigProjectionContextFactory.Create(config.Rig);
        var useNativeReadout = RequiresNativeReadout(config.Rig, _resolvedReadout);
        var renderProjection = useNativeReadout
            ? RigProjectionContextFactory.CreateNativeRoi(config.Rig)
            : outputProjection;
        var configuredMetadata = new CatalogMetadata(
            _options.CatalogName,
            _options.CatalogVersion,
            _options.CatalogSourceUrl,
            _options.CatalogChecksumSha256,
            _options.CatalogLicense,
            _options.CatalogSchemaVersion);
        var metadata = (catalog as ICelestialCatalogMetadataSource)?.Metadata ?? configuredMetadata;
        long? fixedSequence = null;
        var timelineUtc = request.RequestedStartUtc;
        if (_options.FixedSequenceStartUtc is { } fixedSequenceStartUtc)
        {
            lock (_fixedSequenceLock)
            {
                fixedSequence = _captureSequence++;
                timelineUtc = fixedSequenceStartUtc.AddTicks(_fixedSequenceElapsedTicks);
                _fixedSequenceElapsedTicks = checked(_fixedSequenceElapsedTicks + request.TargetInterval.Ticks);
            }
        }
        var sceneUtc = _options.FixedSceneUtc ?? timelineUtc;
        var observatory = config.ResolveObservatory(sceneUtc);
        var sceneRequest = new VisibleSceneRequest(
            sceneUtc,
            new ObserverLocation(observatory.LatitudeDegrees, observatory.LongitudeDegrees,
                observatory.ElevationMeters),
            renderProjection,
            new CatalogQuery(_options.MaximumMagnitude, _options.MaximumResults),
            metadata,
            horizonPolicy: HorizonPolicy.GeometricHorizon,
            projectionVersion: config.Rig.Optics.CalibrationVersion,
            algorithmVersion: "visible-scene-iau1976-constellation-v2",
            constellationIds: _options.ConstellationIds,
            solarSystemBodies: ParseSolarSystemBodies(_options.SolarSystemBodies),
            includeConstellationEndpointStars: _options.IncludeConstellationEndpointStars);
        var renderScene = await new VisibleSceneBuilder(catalog, constellationTopology, planetEphemeris)
            .BuildAsync(sceneRequest, cancellationToken).ConfigureAwait(false);
        var scene = useNativeReadout
            ? VisibleSceneReadoutTransform.ToOutput(
                renderScene, outputProjection, _resolvedReadout!.Geometry.BinX, _resolvedReadout.Geometry.BinY)
            : renderScene;
        var layout = _resolvedReadout is null
            ? new ImageLayout(sensor.WidthPixels, sensor.HeightPixels, sensor.PixelFormat,
                sensor.StrideBytes ?? checked(sensor.WidthPixels * ImageLayout.BytesPerPixel(sensor.PixelFormat)))
            : new ImageLayout(
                _resolvedReadout.Layout.Width,
                _resolvedReadout.Layout.Height,
                _resolvedReadout.Layout.PixelFormat,
                _resolvedReadout.Layout.StrideBytes);
        var start = timeProvider.GetTimestamp();
        var sceneId = CreateSceneId(
            sceneRequest,
            setpoint,
            _options,
            sensor,
            planetEphemeris?.ModelVersion,
            constellationTopology?.Metadata);
        if (config.Rig.Readout is not null)
        {
            sceneId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(
                sceneId,
                "\n",
                CaptureContractJson.ComputeCanonicalJsonSha256(config.Rig.Readout)))));
        }
        var stageKey = _stageProjectedScene && stagingStore is not null
            ? Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
            : null;
        var captureSequence = fixedSequence ?? (_cloudField is null && _transientScenario is null
            ? Interlocked.Increment(ref _captureSequence) - 1
            : CreateDeterministicCaptureSequence(sceneId));
        var cloud = _cloudField is null
            ? null
            : new VirtualCloudRenderContext(_cloudField, timelineUtc, setpoint.Exposure);
        var transient = _transientScenario is null
            ? null
            : new VirtualTransientRenderContext(
                _transientScenario, timelineUtc, setpoint.Exposure);
        var render = useNativeReadout
            ? RenderNativeReadout(
                renderScene, layout, setpoint, captureSequence, renderProjection, cloud, transient, cancellationToken)
            : sensor.PixelFormat switch
            {
                CameraPixelFormat.Mono16 => Mono16SceneRenderer.Render(
                    scene, layout, CreateMonoOptions(setpoint, captureSequence, outputProjection, cloud, transient),
                    cancellationToken),
                CameraPixelFormat.Rgb24 => Rgb24CompatibilityRenderer.Render(
                    scene, layout, CreateRgbOptions(setpoint, cloud, transient), cancellationToken),
                CameraPixelFormat.BayerRggb16 => BayerRggb16Renderer.Render(
                    scene, layout, CreateBayerOptions(setpoint, captureSequence, outputProjection, cloud, transient),
                    cancellationToken),
                _ => throw new UnreachableException()
            };
        if (_options.SyntheticCalibration is { } syntheticCalibration)
        {
            var affected = SyntheticCalibrationReferenceGenerator.ApplyToLightWithStatistics(
                new Linear16Frame(
                    layout.Width,
                    layout.Height,
                    layout.StrideBytes,
                    layout.PixelFormat,
                    render.Pixels),
                setpoint.Exposure,
                syntheticCalibration,
                cancellationToken);
            render = render with
            {
                Pixels = affected.PixelData,
                AlgorithmVersion = $"{render.AlgorithmVersion}+{SyntheticCalibrationReferenceGenerator.AlgorithmVersion}",
                Statistics = affected.Statistics
            };
        }
        PreparedVirtualCalibration? preparedVirtualCalibration = null;
        if (_options.VirtualCalibration is { } virtualCalibration)
        {
            preparedVirtualCalibration = GetOrCreatePreparedVirtualCalibration(
                _resolvedReadout!.Layout,
                virtualCalibration,
                setpoint.Gain,
                cancellationToken);
            var affected = VirtualCalibrationSourceGenerator.ApplyToLightWithStatistics(
                new CalibrationSourceFrame(_resolvedReadout!.Layout, render.Pixels),
                setpoint.Exposure,
                preparedVirtualCalibration,
                cancellationToken);
            render = render with
            {
                Pixels = affected.PixelData,
                AlgorithmVersion = $"{render.AlgorithmVersion}+{affected.AlgorithmVersion}",
                Statistics = affected.Statistics
            };
        }
        sceneStore.Put(sceneId, scene);
        var cloudProvenance = CreateCloudProvenance(_options.CloudScenario, timelineUtc, setpoint.Exposure);
        var transientProvenance = CreateTransientProvenance(
            _options.TransientScenario, timelineUtc, setpoint.Exposure);
        var provenance = new SceneProvenance(
            sceneId,
            config.Rig.ProfileVersion,
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
            ProjectionCalibrationVersion: config.Rig.Optics.CalibrationVersion,
            CloudScenario: cloudProvenance,
            TransientScenario: transientProvenance,
            SceneUtc: sceneRequest.Utc,
            ProjectedSceneStageSchemaVersion: stageKey is null
                ? null
                : StagedProjectedSceneDocument.CurrentSchemaVersion,
            ProjectedSceneStageKey: stageKey);
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
        if (cloudProvenance is not null)
        {
            extra["cloudScenarioId"] = cloudProvenance.ScenarioId;
            extra["cloudParametersSha256"] = cloudProvenance.ParametersSha256;
            extra["cloudAlgorithm"] = cloudProvenance.AlgorithmVersion;
        }
        if (transientProvenance is not null)
        {
            extra["transientScenarioId"] = transientProvenance.ScenarioId;
            extra["transientParametersSha256"] = transientProvenance.ParametersSha256;
            extra["transientAlgorithm"] = transientProvenance.AlgorithmVersion;
        }
        if (_options.SyntheticCalibration is { } calibration)
        {
            extra["syntheticCalibrationSchema"] = calibration.SchemaVersion;
            extra["syntheticCalibrationModelSha256"] =
                SyntheticCalibrationReferenceGenerator.ComputeModelIdentitySha256(calibration);
            extra["blackLevelAdu"] = calibration.BiasPedestalAdu.ToString(CultureInfo.InvariantCulture);
            extra["whiteLevelAdu"] = ushort.MaxValue.ToString(CultureInfo.InvariantCulture);
        }
        if (_options.VirtualCalibration is { } virtualCalibrationMetadata &&
            preparedVirtualCalibration is { } appliedVirtualCalibration)
        {
            extra["virtualCalibrationSchema"] = virtualCalibrationMetadata.SourceModel.SchemaVersion;
            extra["virtualCalibrationModelSha256"] = appliedVirtualCalibration.ModelIdentitySha256;
            extra["virtualCalibrationAlgorithm"] = VirtualCalibrationSourceGenerator.LightCorruptionAlgorithmVersion;
        }
        if (sensor.SimulationResponse is { } configuredResponse)
        {
            var response = ConfiguredSensorResponseResolver.Resolve(configuredResponse, setpoint.Gain);
            extra["sensorModel"] = configuredResponse.ModelVersion;
            extra["gainUnits"] = configuredResponse.GainUnits;
            extra["electronsPerAdu"] = response.ElectronsPerAdu.ToString("R", CultureInfo.InvariantCulture);
            extra["readNoiseElectrons"] = response.ReadNoiseElectrons.ToString("R", CultureInfo.InvariantCulture);
            extra["fullWellElectrons"] = response.FullWellElectrons.ToString("R", CultureInfo.InvariantCulture);
            extra["sensorAdcBitDepth"] = response.AdcBitDepth.ToString(CultureInfo.InvariantCulture);
            extra["blackLevelAdu"] = response.BlackLevelAdu.ToString("R", CultureInfo.InvariantCulture);
            extra["whiteLevelAdu"] = ((1 << response.AdcBitDepth) - 1).ToString(CultureInfo.InvariantCulture);
            extra["captureSequence"] = captureSequence.ToString(CultureInfo.InvariantCulture);
            if (configuredResponse.CalibrationStatus is { } calibrationStatus)
            {
                extra["responseCalibrationStatus"] = calibrationStatus;
            }
            if (sensor.PixelFormat == CameraPixelFormat.BayerRggb16)
            {
                extra["containerBitDepth"] = "16";
                extra["cfaPattern"] = "RGGB";
                extra["channelResponseModel"] = configuredResponse.ColorResponse!.Model;
            }
        }
        else if (_options.Asi174Sensor.Enabled)
        {
            var response = Asi174MmSensorModel.Resolve(setpoint.Gain, _options.Asi174Sensor.BlackLevelAdu);
            extra["sensorModel"] = Asi174MmSensorModel.Version;
            extra["gainUnits"] = "ZWO 0.1 dB";
            extra["electronsPerAdu"] = response.ElectronsPerAdu.ToString("R", CultureInfo.InvariantCulture);
            extra["readNoiseElectrons"] = response.ReadNoiseElectrons.ToString("R", CultureInfo.InvariantCulture);
            extra["fullWellElectrons"] = response.FullWellElectrons.ToString("R", CultureInfo.InvariantCulture);
            extra["adcBitDepth"] = response.AdcBitDepth.ToString(CultureInfo.InvariantCulture);
            extra["blackLevelAdu"] = response.BlackLevelAdu.ToString("R", CultureInfo.InvariantCulture);
            extra["whiteLevelAdu"] = "4095";
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
            extra["blackLevelAdu"] = MapAsi178NativeCodeToRaw16(response.BlackLevelAdu).ToString(CultureInfo.InvariantCulture);
            extra["whiteLevelAdu"] = ushort.MaxValue.ToString(CultureInfo.InvariantCulture);
            extra["captureSequence"] = captureSequence.ToString(CultureInfo.InvariantCulture);
        }
        else if (_options.Asi676Enabled)
        {
            var response = Asi676SensorModel.Resolve(setpoint.Gain, _options.Asi676Sensor!.BlackLevelAdu);
            extra["sensorModel"] = Asi676SensorModel.Version;
            extra["gainUnits"] = "ZWO 0.1 dB";
            extra["electronsPerAdu"] = response.ElectronsPerAdu.ToString("R", CultureInfo.InvariantCulture);
            extra["readNoiseElectrons"] = response.ReadNoiseElectrons.ToString("R", CultureInfo.InvariantCulture);
            extra["fullWellElectrons"] = response.FullWellElectrons.ToString("R", CultureInfo.InvariantCulture);
            extra["sensorAdcBitDepth"] = "12";
            extra["containerBitDepth"] = "16";
            extra["blackLevelAdu"] = response.BlackLevelAdu.ToString("R", CultureInfo.InvariantCulture);
            extra["whiteLevelAdu"] = "4095";
            extra["responseCalibrationStatus"] = "provisional-published-envelope";
            extra["captureSequence"] = captureSequence.ToString(CultureInfo.InvariantCulture);
            if (sensor.PixelFormat == CameraPixelFormat.BayerRggb16)
            {
                extra["cfaPattern"] = "RGGB";
                extra["channelResponseModel"] = "neutral-uncharacterized";
            }
        }
        if (_resolvedReadout is { } configuredReadout)
        {
            if (configuredReadout.Layout.BlackLevel is { } blackLevel)
            {
                extra["blackLevelAdu"] = blackLevel.ToString("R", CultureInfo.InvariantCulture);
            }
            if (configuredReadout.Layout.WhiteLevel is { } whiteLevel)
            {
                extra["whiteLevelAdu"] = whiteLevel.ToString("R", CultureInfo.InvariantCulture);
            }
        }
        var frameLayout = _resolvedReadout?.Layout ?? CreateFrameLayout(sensor, layout, extra);
        var layoutValidation = frameLayout.Validate();
        if (!layoutValidation.IsValid)
        {
            throw new InvalidOperationException($"VirtualSky produced an invalid frame layout ({layoutValidation.ReasonCode}).");
        }
        var frame = new CameraFrame(
            request.RequestedStartUtc.ToUniversalTime(), layout.Width, layout.Height, layout.PixelFormat,
            render.Pixels,
            new FrameMetadata(
                setpoint.Exposure,
                setpoint.Gain,
                _options.VirtualCalibration?.TemperatureC ?? _options.SyntheticCalibration?.TemperatureC ?? double.NaN,
                "VirtualSky",
                extra,
                Offset: _options.VirtualCalibration?.Offset,
                Scene: provenance),
            layout.StrideBytes)
        {
            Layout = frameLayout
        };
        var result = new CaptureResult(frame, setpoint, timeProvider.GetElapsedTime(start), request.Mode, false)
        {
            // VirtualSky models exposure energy without waiting wall-clock exposure time.
            AcquisitionTiming = new CaptureAcquisitionTiming(
                request.RequestedStartUtc,
                request.RequestedStartUtc,
                request.RequestedStartUtc)
        };
        if (stageKey is null || stagingStore is null) return result;
        try
        {
            await stagingStore.StageAsync(stageKey, sceneId, scene, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch
        {
            try
            {
                await stagingStore.DeleteAsync(stageKey, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Capture failure or cancellation remains authoritative; host startup reconciles any orphan.
            }
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_virtualCalibrationCacheLock)
        {
            _preparedVirtualCalibration = null;
        }
        return ValueTask.CompletedTask;
    }

    private static (VirtualSkyCameraModuleOptions Options, ResolvedSensorReadout? Readout) ValidateConfiguration(
        CameraModuleConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var options = config.ModuleOptions is { } serialized
            ? JsonSerializer.Deserialize<VirtualSkyCameraModuleOptions>(serialized.GetRawText(), SerializerOptions)
                ?? new VirtualSkyCameraModuleOptions()
            : new VirtualSkyCameraModuleOptions();
        options.Validate();
        ValidateRig(config.Rig, options);
        var readout = config.Rig.Readout is null
            ? null
            : SensorReadoutResolver.Resolve(config.Rig.Sensor, config.Rig.Readout);
        ValidateVirtualCalibration(options.VirtualCalibration, readout);
        var outputWidth = readout?.Layout.Width ?? config.Rig.Sensor.WidthPixels;
        var outputHeight = readout?.Layout.Height ?? config.Rig.Sensor.HeightPixels;
        options.SyntheticCalibration?.Validate(outputWidth, outputHeight);
        options.TransientScenario?.ValidateSensorBounds(
            config.Rig.Sensor.WidthPixels,
            config.Rig.Sensor.HeightPixels);
        var pixelFormat = config.Rig.Sensor.PixelFormat;
        if (options.Asi174Sensor.Enabled && pixelFormat != CameraPixelFormat.Mono16 ||
            options.Asi178Sensor.Enabled && pixelFormat != CameraPixelFormat.BayerRggb16 ||
            options.Asi676Enabled && pixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16) ||
            pixelFormat == CameraPixelFormat.BayerRggb16 &&
            !options.Asi178Sensor.Enabled && !options.Asi676Enabled && options.SyntheticCalibration is null &&
            config.Rig.Sensor.SimulationResponse is null ||
            options.SyntheticCalibration is not null &&
            (pixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16) ||
                options.Asi174Sensor.Enabled || options.Asi178Sensor.Enabled || options.Asi676Enabled))
        {
            throw new ArgumentException("The configured physical sensor model must match the raw pixel format.", nameof(config));
        }
        return (options, readout);
    }

    private static void ValidateVirtualCalibration(
        VirtualCalibrationLightOptions? calibration,
        ResolvedSensorReadout? readout)
    {
        if (calibration is null)
        {
            return;
        }
        calibration.Validate();
        if (readout is null)
        {
            throw new ArgumentException(
                "Virtual calibration light corruption requires an explicit native readout.",
                nameof(readout));
        }
        _ = VirtualCalibrationSourceGenerator.Generate(
            VirtualCalibrationSourceKind.Bias,
            0,
            readout.Layout,
            calibration.BiasExposure,
            0,
            calibration.Offset,
            calibration.TemperatureC,
            calibration.SourceModel);
    }

    private PreparedVirtualCalibration GetOrCreatePreparedVirtualCalibration(
        FrameLayoutDescriptor layout,
        VirtualCalibrationLightOptions calibration,
        double gain,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var modelIdentitySha256 =
            VirtualCalibrationSourceGenerator.ComputeModelIdentitySha256(calibration.SourceModel);
        lock (_virtualCalibrationCacheLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_preparedVirtualCalibration is { } prepared &&
                prepared.Layout == layout &&
                prepared.BiasExposure == calibration.BiasExposure &&
                prepared.DarkExposure == calibration.DarkExposure &&
                prepared.FlatExposure == calibration.FlatExposure &&
                prepared.DefectExposure == calibration.DefectExposure &&
                prepared.Gain == gain &&
                prepared.Offset == calibration.Offset &&
                prepared.TemperatureC == calibration.TemperatureC &&
                string.Equals(prepared.ModelIdentitySha256, modelIdentitySha256, StringComparison.Ordinal))
            {
                return prepared;
            }

            prepared = VirtualCalibrationSourceGenerator.Prepare(
                layout,
                calibration.BiasExposure,
                calibration.DarkExposure,
                calibration.FlatExposure,
                calibration.DefectExposure,
                gain,
                calibration.Offset,
                calibration.TemperatureC,
                calibration.SourceModel,
                cancellationToken);
            _preparedVirtualCalibration = prepared;
            return prepared;
        }
    }

    private Mono16SceneRenderOptions CreateMonoOptions(
        CaptureSetpoint setpoint,
        long captureSequence,
        ProjectionContext projection,
        VirtualCloudRenderContext? cloud,
        VirtualTransientRenderContext? transient)
    {
        var configured = _config!.Rig.Sensor.SimulationResponse;
        var response = configured is not null
            ? ConfiguredSensorResponseResolver.Resolve(configured, setpoint.Gain)
            : _options.Asi174Sensor.Enabled
                ? Asi174MmSensorModel.Resolve(setpoint.Gain, _options.Asi174Sensor.BlackLevelAdu)
                : _options.Asi676Enabled
                    ? Asi676SensorModel.Resolve(setpoint.Gain, _options.Asi676Sensor!.BlackLevelAdu)
                    : null;
        var electronDomain = response is not null;
        return new Mono16SceneRenderOptions
        {
            ExposureSeconds = setpoint.Exposure.TotalSeconds,
            Gain = setpoint.Gain,
            MagnitudeZeroElectronsPerSecond = _options.MagnitudeZeroElectronsPerSecond,
            BackgroundElectronsPerSecond = _options.BackgroundElectronsPerSecond ?? (electronDomain
                ? SkyBrightnessModel.PhotometricBackgroundElectronsPerSecond(
                    _options.BortleClass,
                    _options.MagnitudeZeroElectronsPerSecond,
                    projection.FocalLengthXPixels,
                    projection.FocalLengthYPixels)
                : _options.ResolveBackgroundElectronsPerSecond()),
            PsfSigmaPixels = _options.PsfSigmaPixels,
            PsfRadiusPixels = _options.PsfRadiusPixels,
            VignettingStrength = _options.SyntheticCalibration is null ? _options.VignettingStrength : 0,
            Bias = _options.SyntheticCalibration is not null || electronDomain ? 0 : _options.Bias,
            ReadNoiseStandardDeviation = electronDomain ? 0 : _options.ReadNoiseStandardDeviation,
            ShotNoiseEnabled = configured?.ShotNoiseEnabled ??
                (_options.Asi174Sensor.Enabled || _options.Asi676Enabled || _options.ShotNoiseEnabled),
            DarkCurrentElectronsPerSecond = _options.SyntheticCalibration is null ? _options.DarkCurrentElectronsPerSecond : 0,
            DarkNoiseEnabled = (configured?.DarkNoiseEnabled ??
                (_options.Asi174Sensor.Enabled || _options.Asi676Enabled)) &&
                _options.DarkCurrentElectronsPerSecond > 0,
            Seed = electronDomain
                ? unchecked(_options.Seed + (int)(captureSequence * 104729))
                : _options.Seed,
            Cloud = cloud,
            Transient = transient,
            SensorResponse = response
        };
    }

    private Rgb24CompatibilityRenderOptions CreateRgbOptions(
        CaptureSetpoint setpoint,
        VirtualCloudRenderContext? cloud,
        VirtualTransientRenderContext? transient) => new()
        {
            ExposureSeconds = setpoint.Exposure.TotalSeconds,
            Gain = setpoint.Gain,
            MagnitudeZeroElectronsPerSecond = _options.MagnitudeZeroElectronsPerSecond,
            BackgroundElectronsPerSecond = _options.ResolveBackgroundElectronsPerSecond(),
            PsfSigmaPixels = _options.PsfSigmaPixels,
            PsfRadiusPixels = _options.PsfRadiusPixels,
            VignettingStrength = _options.SyntheticCalibration is null ? _options.VignettingStrength : 0,
            Bias = _options.Bias,
            ReadNoiseStandardDeviation = _options.ReadNoiseStandardDeviation,
            ShotNoiseEnabled = _options.ShotNoiseEnabled,
            DarkCurrentElectronsPerSecond = _options.SyntheticCalibration is null ? _options.DarkCurrentElectronsPerSecond : 0,
            Seed = _options.Seed,
            Cloud = cloud,
            Transient = transient
        };

    private BayerRggb16RenderOptions CreateBayerOptions(
        CaptureSetpoint setpoint,
        long captureSequence,
        ProjectionContext projection,
        VirtualCloudRenderContext? cloud,
        VirtualTransientRenderContext? transient) => new()
        {
            ExposureSeconds = setpoint.Exposure.TotalSeconds,
            Gain = setpoint.Gain,
            MagnitudeZeroElectronsPerSecond = _options.MagnitudeZeroElectronsPerSecond,
            BackgroundElectronsPerSecond = _config!.Rig.Sensor.SimulationResponse is not null
                ? _options.BackgroundElectronsPerSecond ?? SkyBrightnessModel.PhotometricBackgroundElectronsPerSecond(
                    _options.BortleClass,
                    _options.MagnitudeZeroElectronsPerSecond,
                    projection.FocalLengthXPixels,
                    projection.FocalLengthYPixels)
                : _options.ResolveBackgroundElectronsPerSecond(projection),
            PsfSigmaPixels = _options.PsfSigmaPixels,
            PsfRadiusPixels = _options.PsfRadiusPixels,
            VignettingStrength = _options.SyntheticCalibration is null ? _options.VignettingStrength : 0,
            ShotNoiseEnabled = _config.Rig.Sensor.SimulationResponse?.ShotNoiseEnabled ?? true,
            DarkCurrentElectronsPerSecond = _options.SyntheticCalibration is null ? _options.DarkCurrentElectronsPerSecond : 0,
            DarkNoiseEnabled = (_config.Rig.Sensor.SimulationResponse?.DarkNoiseEnabled ?? true) &&
                _options.SyntheticCalibration is null && _options.DarkCurrentElectronsPerSecond > 0,
            Seed = unchecked(_options.Seed + (int)(captureSequence * 104729)),
            Cloud = cloud,
            Transient = transient,
            ChannelResponse = _config.Rig.Sensor.SimulationResponse?.ColorResponse is { } configuredColor
                ? new RgbChannelSettings(configuredColor.Red, configuredColor.Green, configuredColor.Blue)
                : _options.SyntheticCalibration is not null || _options.Asi676Enabled
                    ? new RgbChannelSettings(1, 1, 1)
                    : new RgbChannelSettings(0.94, 1, 0.8),
            SensorResponse = _config.Rig.Sensor.SimulationResponse is { } configuredResponse
                ? ConfiguredSensorResponseResolver.Resolve(configuredResponse, setpoint.Gain)
                : _options.Asi178Sensor.Enabled
                    ? Asi178McSensorModel.Resolve(setpoint.Gain, _options.Asi178Sensor.BlackLevelContainerAdu)
                    : _options.Asi676Enabled
                        ? Asi676SensorModel.Resolve(setpoint.Gain, _options.Asi676Sensor!.BlackLevelAdu)
                        : new MonoSensorResponse
                        {
                            AdcBitDepth = 16,
                            FullWellElectrons = ushort.MaxValue,
                            ElectronsPerAdu = 1,
                            ReadNoiseElectrons = 0,
                            BlackLevelAdu = 0,
                            CompatibilityLabel = "Synthetic calibration ideal RGGB16 input"
                        },
            StoredCodeTransform = _resolvedReadout?.Layout.StoredCodeTransform ??
                (_options.Asi178Sensor.Enabled
                    ? FrameStoredCodeTransform.FullRangeScaledV1
                    : FrameStoredCodeTransform.RightAlignedV1),
            ContainerDepthBits = _resolvedReadout?.Layout.ContainerDepthBits ?? 16
        };

    private FrameLayoutDescriptor CreateFrameLayout(
        SensorProfile sensor,
        ImageLayout imageLayout,
        IReadOnlyDictionary<string, string> metadata)
    {
        var is16Bit = sensor.PixelFormat is CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16;
        var (sampleDepth, containerDepth, packing, transform, levelSpace, blackLevel, whiteLevel) = _options switch
        {
            { Asi174Sensor.Enabled: true } =>
                (12, 16, FrameSamplePacking.ByteAligned, FrameStoredCodeTransform.RightAlignedV1,
                    FrameLevelCodeSpace.NativeSample, ResolveMetadataLevel(metadata, "blackLevelAdu"), ResolveMetadataLevel(metadata, "whiteLevelAdu")),
            { Asi178Sensor.Enabled: true } =>
                (14, 16, FrameSamplePacking.ByteAligned, FrameStoredCodeTransform.FullRangeScaledV1,
                    FrameLevelCodeSpace.StoredContainer, ResolveMetadataLevel(metadata, "blackLevelAdu"), ResolveMetadataLevel(metadata, "whiteLevelAdu")),
            { Asi676Enabled: true } =>
                (12, 16, FrameSamplePacking.ByteAligned, FrameStoredCodeTransform.RightAlignedV1,
                    FrameLevelCodeSpace.NativeSample, ResolveMetadataLevel(metadata, "blackLevelAdu"), ResolveMetadataLevel(metadata, "whiteLevelAdu")),
            _ => (is16Bit ? 16 : 8, is16Bit ? 16 : 8, FrameSamplePacking.ByteAligned,
                FrameStoredCodeTransform.IdentityV1, FrameLevelCodeSpace.StoredContainer,
                ResolveMetadataLevel(metadata, "blackLevelAdu"), ResolveMetadataLevel(metadata, "whiteLevelAdu"))
        };
        return new FrameLayoutDescriptor(
            imageLayout.Width,
            imageLayout.Height,
            imageLayout.StrideBytes,
            imageLayout.PixelFormat,
            is16Bit ? FrameByteOrder.LittleEndian : FrameByteOrder.NotApplicable,
            sampleDepth,
            containerDepth,
            packing,
            sensor.PixelFormat == CameraPixelFormat.BayerRggb16
                ? ColorFilterArrayPattern.Rggb
                : ColorFilterArrayPattern.None,
            blackLevel,
            whiteLevel,
            imageLayout.RequiredByteLength)
        {
            Readout = new FrameReadoutDescriptor(
                imageLayout.Width,
                imageLayout.Height,
                0,
                0,
                imageLayout.Width,
                imageLayout.Height,
                1,
                1,
                FrameBinningAlgorithm.IdentityV1,
                sensor.PixelFormat == CameraPixelFormat.BayerRggb16 ? 0 : null,
                sensor.PixelFormat == CameraPixelFormat.BayerRggb16 ? 0 : null),
            StoredCodeTransform = transform,
            LevelCodeSpace = levelSpace
        };
    }

    private SceneRenderResult RenderNativeReadout(
        VisibleScene renderScene,
        ImageLayout outputLayout,
        CaptureSetpoint setpoint,
        long captureSequence,
        ProjectionContext renderProjection,
        VirtualCloudRenderContext? cloud,
        VirtualTransientRenderContext? transient,
        CancellationToken cancellationToken)
    {
        var resolved = _resolvedReadout ?? throw new InvalidOperationException("A resolved readout is required.");
        var nativeLayout = new ImageLayout(
            resolved.Geometry.RoiWidth,
            resolved.Geometry.RoiHeight,
            CameraPixelFormat.Mono16,
            checked(resolved.Geometry.RoiWidth * 2));
        var options = CreateMonoOptions(setpoint, captureSequence, renderProjection, cloud, transient);
        var native = Mono16SceneRenderer.Render(renderScene, nativeLayout, options, cancellationToken);
        return MonoDigitalReadoutRenderer.Apply(
            native,
            nativeLayout,
            outputLayout,
            resolved.Profile,
            options.SensorResponse?.AdcBitDepth ?? 16,
            RigProjectionContextFactory.Create(_config!.Rig),
            cancellationToken);
    }

    private static bool RequiresNativeReadout(CameraRigConfig rig, ResolvedSensorReadout? resolved)
        => resolved is not null &&
            (resolved.Geometry.RoiX != 0 || resolved.Geometry.RoiY != 0 ||
             resolved.Geometry.RoiWidth != rig.Sensor.WidthPixels ||
             resolved.Geometry.RoiHeight != rig.Sensor.HeightPixels ||
              resolved.Geometry.BinX != 1 || resolved.Geometry.BinY != 1 ||
              resolved.Layout.PixelFormat != rig.Sensor.PixelFormat ||
              rig.Sensor.PixelFormat == CameraPixelFormat.Mono16 &&
              resolved.Layout.SampleDepthBits != NominalSampleDepth(rig.Sensor.PixelFormat) ||
              resolved.Layout.ByteOrder == FrameByteOrder.BigEndian);

    private static int NominalSampleDepth(CameraPixelFormat format)
        => format is CameraPixelFormat.Mono8 or CameraPixelFormat.Rgb24 ? 8 : 16;

    private static double? ResolveMetadataLevel(IReadOnlyDictionary<string, string> metadata, string key)
        => metadata.TryGetValue(key, out var value) && double.TryParse(
            value, NumberStyles.Float, CultureInfo.InvariantCulture, out var level)
            ? level
            : null;

    private static int MapAsi178NativeCodeToRaw16(double nativeCode)
        => (int)Math.Round(nativeCode * ushort.MaxValue / 16_383, MidpointRounding.AwayFromZero);

    private static void ValidateRig(CameraRigConfig rig, VirtualSkyCameraModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(rig);
        if (rig.Sensor.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.Rgb24 or CameraPixelFormat.BayerRggb16))
        {
            throw new NotSupportedException("VirtualSky supports Mono16, RGB24 compatibility, or BayerRggb16 output.");
        }

        if (rig.Sensor.SimulationResponse is { } simulationResponse)
        {
            ConfiguredSensorResponseResolver.Validate(simulationResponse);
            var configuredMono = rig.Sensor.PixelFormat == CameraPixelFormat.Mono16 &&
                rig.Sensor.ColorMode == SensorColorMode.Mono &&
                rig.Sensor.ResponseMode == SensorResponseMode.Monochrome;
            var configuredBayer = rig.Sensor.PixelFormat == CameraPixelFormat.BayerRggb16 &&
                rig.Sensor.ColorMode == SensorColorMode.Color &&
                rig.Sensor.ResponseMode == SensorResponseMode.BayerRaw &&
                simulationResponse.ColorResponse is not null;
            if (rig.Readout is null || !configuredMono && !configuredBayer)
            {
                throw new NotSupportedException(
                    "A configured sensor response requires a supported native sensor and an explicit readout descriptor.");
            }
            var pipeline = rig.Pipeline;
            var gainsAreSupported = pipeline.Envelope is { } envelope
                ? IsSupportedGain(envelope.MinGain, simulationResponse) &&
                  IsSupportedGain(envelope.MaxGain, simulationResponse)
                : IsSupportedGain(pipeline.DayGain, simulationResponse) &&
                  IsSupportedGain(pipeline.NightGain, simulationResponse);
            if (!gainsAreSupported)
            {
                throw new NotSupportedException(
                    "The pipeline gain range must be contained by the configured sensor response.");
            }
        }

        var resolved = rig.Readout is null ? null : SensorReadoutResolver.Resolve(rig.Sensor, rig.Readout);
        var requiresNativeReadout = RequiresNativeReadout(rig, resolved);
        if (requiresNativeReadout &&
            (rig.Sensor.PixelFormat != CameraPixelFormat.Mono16 ||
             resolved!.Layout.PixelFormat is not (CameraPixelFormat.Mono8 or CameraPixelFormat.Mono16) ||
             resolved.Profile.BinningAlgorithm == FrameBinningAlgorithm.ChargeSumV1 ||
             resolved.Profile.Packing != FrameSamplePacking.ByteAligned ||
             options.SyntheticCalibration is not null || options.CloudScenario is not null ||
             options.TransientScenario is { SensorTracks.Count: > 0 }))
        {
            throw new NotSupportedException(
                "VirtualSky native readout currently supports byte-aligned monochrome identity, digital-sum, and digital-average modes without clouds, sensor-plane transient tracks, or synthetic calibration.");
        }
        var nativeSampleDepth = rig.Sensor.SimulationResponse?.AdcBitDepth ?? options switch
        {
            { Asi174Sensor.Enabled: true } => 12,
            { Asi676Enabled: true } => 12,
            { Asi178Sensor.Enabled: true } => 14,
            _ => 16
        };
        if (resolved is not null && resolved.Layout.SampleDepthBits > nativeSampleDepth)
        {
            throw new NotSupportedException("The output sample depth exceeds the configured native sensor response.");
        }
        if (resolved is not null && rig.Sensor.PixelFormat == CameraPixelFormat.BayerRggb16 &&
            resolved.Layout.SampleDepthBits != nativeSampleDepth)
        {
            throw new NotSupportedException("VirtualSky Bayer output currently requires the native sensor sample depth.");
        }
        if (rig.Sensor.SimulationResponse is { } configuredResponse)
        {
            if (rig.Sensor.PixelFormat == CameraPixelFormat.BayerRggb16 &&
                (requiresNativeReadout || resolved!.Layout.PixelFormat != CameraPixelFormat.BayerRggb16 ||
                 resolved.Layout.SampleDepthBits != configuredResponse.AdcBitDepth ||
                 resolved.Profile.BinningAlgorithm != FrameBinningAlgorithm.IdentityV1 ||
                 resolved.Profile.Packing != FrameSamplePacking.ByteAligned ||
                 resolved.Profile.StoredCodeTransform is not
                     (FrameStoredCodeTransform.RightAlignedV1 or FrameStoredCodeTransform.FullRangeScaledV1)))
            {
                throw new NotSupportedException(
                    "Configured Bayer response currently requires a full-frame, unbinned, little-endian Bayer readout at native sample depth.");
            }
            ValidateConfiguredReadoutLevels(configuredResponse, resolved!);
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

    private static bool IsSupportedGain(double gain, ConfiguredSensorResponseProfile response)
        => double.IsFinite(gain) && gain >= response.MinimumGainControl && gain <= response.MaximumGainControl;

    private static void ValidateConfiguredReadoutLevels(
        ConfiguredSensorResponseProfile response,
        ResolvedSensorReadout readout)
    {
        var profile = readout.Profile;
        var nativeBlack = checked((int)Math.Round(response.BlackLevelAdu, MidpointRounding.AwayFromZero));
        var shift = response.AdcBitDepth - profile.SampleDepthBits;
        var meaningfulBlack = shift == 0
            ? nativeBlack
            : (nativeBlack + (1 << (shift - 1))) >> shift;
        var meaningfulMaximum = (1 << profile.SampleDepthBits) - 1;
        if (profile.BinningAlgorithm == FrameBinningAlgorithm.DigitalSumV1)
        {
            meaningfulBlack = Math.Min(
                checked(meaningfulBlack * profile.BinX * profile.BinY),
                meaningfulMaximum);
        }
        var expectedBlack = profile.LevelCodeSpace == FrameLevelCodeSpace.NativeSample
            ? meaningfulBlack
            : MapStoredCode(meaningfulBlack, profile);
        var expectedWhite = profile.LevelCodeSpace == FrameLevelCodeSpace.NativeSample
            ? meaningfulMaximum
            : MapStoredCode(meaningfulMaximum, profile);
        if (profile.BlackLevel != expectedBlack || profile.WhiteLevel != expectedWhite)
        {
            throw new NotSupportedException(
                "Configured readout levels must match the sensor response, binning, and stored-code transform.");
        }
    }

    private static int MapStoredCode(int meaningfulCode, SensorReadoutProfile readout)
        => readout.StoredCodeTransform switch
        {
            FrameStoredCodeTransform.IdentityV1 or FrameStoredCodeTransform.RightAlignedV1 => meaningfulCode,
            FrameStoredCodeTransform.LeftShiftedV1 => meaningfulCode <<
                (readout.ContainerDepthBits - readout.SampleDepthBits),
            FrameStoredCodeTransform.FullRangeScaledV1 => checked((int)Math.Round(
                meaningfulCode * ((1 << readout.ContainerDepthBits) - 1d) /
                ((1 << readout.SampleDepthBits) - 1d),
                MidpointRounding.AwayFromZero)),
            _ => throw new ArgumentOutOfRangeException(nameof(readout))
        };

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

    private static long CreateDeterministicCaptureSequence(string sceneId)
        => Convert.ToInt64(sceneId[..15], 16);

    private static CloudScenarioProvenance? CreateCloudProvenance(
        VirtualCloudScenarioDefinition? definition,
        DateTimeOffset integrationStartUtc,
        TimeSpan exposure)
    {
        if (definition is null)
        {
            return null;
        }

        var parameters = CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(definition));
        return new CloudScenarioProvenance(
            definition.SchemaVersion,
            definition.ScenarioId,
            definition.ScenarioVersion,
            VirtualCloudScenarioDefinition.CurrentAlgorithmVersion,
            CaptureContractJson.ComputeCanonicalJsonSha256(parameters),
            definition.Seed,
            definition.EpochUtc,
            integrationStartUtc,
            integrationStartUtc + exposure,
            definition.TemporalSampleCount,
            parameters);
    }

    private static TransientScenarioProvenance? CreateTransientProvenance(
        VirtualTransientScenarioDefinition? definition,
        DateTimeOffset integrationStartUtc,
        TimeSpan exposure)
    {
        if (definition is null)
        {
            return null;
        }

        var parameters = CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(definition));
        return new TransientScenarioProvenance(
            definition.SchemaVersion,
            definition.ScenarioId,
            definition.ScenarioVersion,
            definition.Recurrence is null
                ? VirtualTransientScenarioDefinition.CurrentAlgorithmVersion
                : $"{VirtualTransientScenarioDefinition.CurrentAlgorithmVersion}+{VirtualTransientRecurrenceDefinition.CurrentAlgorithmVersion}",
            CaptureContractJson.ComputeCanonicalJsonSha256(parameters),
            definition.Seed,
            definition.EpochUtc,
            integrationStartUtc,
            integrationStartUtc + exposure,
            definition.TemporalSampleCount,
            definition.SkyTracks.Count,
            definition.SensorTracks.Count,
            parameters);
    }

    private static SolarSystemBody[] ParseSolarSystemBodies(IEnumerable<string> names)
        => names.Select(name => Enum.Parse<SolarSystemBody>(name, true)).Distinct().ToArray();
}

/// <summary>Validated deterministic scene and simulated sensor parameters.</summary>
public sealed class VirtualSkyCameraModuleOptions
{
    public int Seed { get; init; } = 2025;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? FixedSceneUtc { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? FixedSequenceStartUtc { get; init; }
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Asi676SensorOptions? Asi676Sensor { get; init; }
    internal bool Asi676Enabled => Asi676Sensor?.Enabled == true;
    public VirtualCloudScenarioDefinition? CloudScenario { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VirtualTransientScenarioDefinition? TransientScenario { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SyntheticCalibrationModelV1? SyntheticCalibration { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VirtualCalibrationLightOptions? VirtualCalibration { get; init; }
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
            FixedSceneUtc is { Offset: var sceneOffset } && sceneOffset != TimeSpan.Zero ||
            FixedSequenceStartUtc is { Offset: var sequenceOffset } && sequenceOffset != TimeSpan.Zero ||
            Asi174Sensor is null || Asi178Sensor is null ||
            (Asi174Sensor.Enabled ? 1 : 0) + (Asi178Sensor.Enabled ? 1 : 0) +
            (Asi676Enabled ? 1 : 0) > 1 ||
            ConstellationIds is null || ConstellationIds.Any(string.IsNullOrWhiteSpace) ||
            SyntheticCalibration is not null && VirtualCalibration is not null ||
            SolarSystemBodies is null || SolarSystemBodies.Any(name =>
                string.IsNullOrWhiteSpace(name) || !Enum.TryParse<SolarSystemBody>(name, true, out var body) || !Enum.IsDefined(body)))
        {
            throw new ArgumentOutOfRangeException(nameof(VirtualSkyCameraModuleOptions));
        }

        CloudScenario?.Validate();
        TransientScenario?.Validate();

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
        if (Asi676Enabled)
        {
            _ = Asi676SensorModel.Resolve(0, Asi676Sensor!.BlackLevelAdu);
            _ = Asi676SensorModel.Resolve(180, Asi676Sensor.BlackLevelAdu);
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
        => BackgroundElectronsPerSecond ?? (Asi174Sensor.Enabled || Asi178Sensor.Enabled || Asi676Enabled
            ? SkyBrightnessModel.PhotometricBackgroundElectronsPerSecond(
                BortleClass, MagnitudeZeroElectronsPerSecond,
                projection.FocalLengthXPixels, projection.FocalLengthYPixels)
            : ResolveBackgroundElectronsPerSecond());
}

public sealed record VirtualCalibrationLightOptions
{
    public VirtualCalibrationSourceModelV1 SourceModel { get; init; } = new();
    public TimeSpan BiasExposure { get; init; } = TimeSpan.FromMilliseconds(1);
    public TimeSpan DarkExposure { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan FlatExposure { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan DefectExposure { get; init; } = TimeSpan.FromMilliseconds(1);
    public double Offset { get; init; } = 1;
    public double TemperatureC { get; init; } = -10;

    internal void Validate()
    {
        SourceModel?.Validate();
        if (SourceModel is null || BiasExposure <= TimeSpan.Zero || DarkExposure <= TimeSpan.Zero ||
            FlatExposure <= TimeSpan.Zero || DefectExposure <= TimeSpan.Zero ||
            !double.IsFinite(Offset) || !double.IsFinite(TemperatureC))
        {
            throw new ArgumentOutOfRangeException(nameof(VirtualCalibrationLightOptions));
        }
    }

    internal VirtualCalibrationLightParameters CreateParameters(TimeSpan lightExposure, double gain)
        => new(
            BiasExposure,
            DarkExposure,
            FlatExposure,
            DefectExposure,
            lightExposure,
            gain,
            Offset,
            TemperatureC);
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

/// <summary>Configures the provisional shared ASI676MM/MC 12-bit response in a RAW16 container.</summary>
public sealed class Asi676SensorOptions
{
    public bool Enabled { get; init; }

    /// <summary>Provisional native 12-bit pedestal pending controlled bias characterization.</summary>
    public double BlackLevelAdu { get; init; } = 64;
}
