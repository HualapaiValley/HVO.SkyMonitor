using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;

internal sealed class StandaloneCameraAgentKestrelFixture : IAsyncDisposable
{
    internal const string AgentId = "cameraagent-standalone-acceptance";
    internal const string OwnerEmail = "standalone-owner@cameraagent.test";
    internal const string OwnerPassword = "StandaloneOwner!194";

    private readonly string _root;
    private readonly CatalogFixtureInstallation? _catalog;
    private readonly IReadOnlyDictionary<string, string?> _overrides;
    private readonly string? _environmentalSettingsPath;
    private readonly string _configurationPath;
    private readonly TimeProvider? _timeProvider;
    private readonly ControllableLaneFaultInjector? _laneFaultInjector;
    private readonly ConcurrentQueue<OutboundHttpAttempt> _outboundAttempts = new();
    private HostInstance? _host;

    private StandaloneCameraAgentKestrelFixture(
        string root,
        CatalogFixtureInstallation? catalog,
        IReadOnlyDictionary<string, string?> overrides,
        string configurationPath,
        string? environmentalSettingsPath = null,
        TimeProvider? timeProvider = null,
        ControllableLaneFaultInjector? laneFaultInjector = null)
    {
        _root = root;
        _catalog = catalog;
        _overrides = overrides;
        _configurationPath = configurationPath;
        _environmentalSettingsPath = environmentalSettingsPath;
        _timeProvider = timeProvider;
        _laneFaultInjector = laneFaultInjector;
    }

    internal Uri BaseAddress => Host.LifetimeClient.BaseAddress
        ?? throw new InvalidOperationException("The standalone fixture has no base address.");

    internal IServiceProvider Services => Host.Factory.Services;

    internal string Root => _root;

    internal DateTimeOffset UtcNow => (_timeProvider ?? TimeProvider.System).GetUtcNow();

    internal IReadOnlyCollection<OutboundHttpAttempt> OutboundAttempts => _outboundAttempts.ToArray();

    internal int LaneInterruptionCount => _laneFaultInjector?.InjectedCount ?? 0;

    private HostInstance Host => _host
        ?? throw new InvalidOperationException("The standalone CameraAgent host is not running.");

    internal static async Task<StandaloneCameraAgentKestrelFixture> CreateAsync(
        bool useSidingSpringLocation = false,
        bool useSyntheticCalibration = false,
        bool useEnvironmentalAcquisition = false,
        bool useProjectedScene = false)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-cameraagent-standalone-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var catalog = CatalogFixtureInstallation.Create(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "hyg-v42-bright-stars.sqlite"),
            Path.Combine(root, "catalog"));

        try
        {
            var configPath = Path.Combine(root, "cameraagent.standalone.json");
            await WriteConfigurationAsync(configPath, root, useSyntheticCalibration, useProjectedScene).ConfigureAwait(false);
            var environmentalSettingsPath = useEnvironmentalAcquisition
                ? Path.Combine(root, "environmental.settings.json")
                : null;
            if (environmentalSettingsPath is not null)
            {
                await WriteEnvironmentalSettingsAsync(environmentalSettingsPath).ConfigureAwait(false);
            }
            var overrides = new Dictionary<string, string?>
            {
                ["CameraAgent:CentralIntegration:Mode"] = "Disabled",
                ["CameraAgent:CaptureDistribution:UploadEnabled"] = "true",
                ["CameraAgent:EnvironmentalDelivery:Enabled"] = "true",
                ["CameraAgent:ConfigFilePath"] = configPath,
                ["CameraAgent:RawIngressRoot"] = root,
                ["CameraAgent:RawIngressReserveBytes"] = "0",
                ["CameraAgent:TransientDetection:Mode"] = "Off",
                ["LocalIdentity:AdminEmail"] = OwnerEmail,
                ["LocalIdentity:AdminPassword"] = OwnerPassword,
                ["LocalIdentity:AdminPasswordFile"] = string.Empty,
                ["LocalIdentity:AllowMissingAdminPassword"] = "false",
                ["LocalIdentity:DatabasePath"] = Path.Combine(root, "identity", "cameraagent_identity.db"),
                ["LocalIdentity:CookieName"] = "CameraAgent.Standalone194.Auth",
                ["DeviceProvisioning:StateDirectory"] = Path.Combine(root, "provisioning"),
                ["Catalog:Root"] = catalog.Root,
                ["Catalog:RequiredCatalogId"] = "hyg-v42-fixture",
                ["Catalog:RequiredPackageKind"] = "Fixture",
                ["CentralIdentity:ServiceUrl"] = "http://central-forbidden.invalid/",
                ["SkyMonitor:BaseUrl"] = "http://central-forbidden.invalid",
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = string.Empty,
                ["Logging:LogLevel:Default"] = "Warning",
                ["Serilog:MinimumLevel:Default"] = "Warning"
            };
            if (useSidingSpringLocation)
            {
                overrides["CameraAgent:Observatory:LatitudeDegrees"] = "-31.2733";
                overrides["CameraAgent:Observatory:LongitudeDegrees"] = "149.0700";
                overrides["CameraAgent:Observatory:ElevationMeters"] = "1165";
                overrides["CameraAgent:Observatory:TimeZoneId"] = "Australia/Sydney";
                overrides["CameraAgent:DeploymentLocation:LocationId"] = "siding-spring-synthetic";
                overrides["CameraAgent:DeploymentLocation:Source"] =
                    "GitHub issue #196 operator-pinned acceptance coordinates; not a physical survey";
                overrides["CameraAgent:DeploymentLocation:EffectiveFromUtc"] = "2025-01-01T00:00:00Z";
            }
            var fixture = new StandaloneCameraAgentKestrelFixture(
                root, catalog, overrides, configPath, environmentalSettingsPath);
            await fixture.StartHostAsync().ConfigureAwait(false);
            await fixture.CompleteOwnerBootstrapForExistingAcceptanceTestsAsync().ConfigureAwait(false);
            return fixture;
        }
        catch
        {
            catalog.Dispose();
            await DeleteWithRetriesAsync(root).ConfigureAwait(false);
            throw;
        }
    }

    internal static async Task<StandaloneCameraAgentKestrelFixture> CreateProductionSmokeAsync(
        string catalogRoot,
        DateTimeOffset fixedStartUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogRoot);
        if (fixedStartUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The production smoke start must be UTC.", nameof(fixedStartUtc));
        }

        var root = Path.Combine(Path.GetTempPath(), $"hvo-cameraagent-production-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var configPath = Path.Combine(root, "cameraagent.standalone-production-smoke.json");
            await WriteProductionSmokeConfigurationAsync(configPath, root).ConfigureAwait(false);
            var overrides = new Dictionary<string, string?>
            {
                ["CameraAgent:CentralIntegration:Mode"] = "Disabled",
                ["CameraAgent:CaptureDistribution:UploadEnabled"] = "false",
                ["CameraAgent:CaptureDistribution:ShutdownDrainSeconds"] = "1",
                ["CameraAgent:EnvironmentalDelivery:Enabled"] = "false",
                ["CameraAgent:ConfigFilePath"] = configPath,
                ["CameraAgent:RawIngressRoot"] = root,
                ["CameraAgent:RawIngressReserveBytes"] = "0",
                ["CameraAgent:TransientDetection:Mode"] = "Off",
                ["CameraAgent:Observatory:LatitudeDegrees"] = "35.5599378",
                ["CameraAgent:Observatory:LongitudeDegrees"] = "-113.9119818",
                ["CameraAgent:Observatory:ElevationMeters"] = "520",
                ["CameraAgent:Observatory:TimeZoneId"] = "America/Phoenix",
                ["CameraAgent:DeploymentLocation:LocationId"] = "hualapai-cameraagent-standalone-full",
                ["CameraAgent:DeploymentLocation:Source"] = "issue-171-operator-pinned-smoke-configuration",
                ["CameraAgent:DeploymentLocation:EffectiveFromUtc"] = "2025-01-01T00:00:00Z",
                ["LocalIdentity:AdminEmail"] = OwnerEmail,
                ["LocalIdentity:AdminPassword"] = OwnerPassword,
                ["LocalIdentity:AdminPasswordFile"] = string.Empty,
                ["LocalIdentity:AllowMissingAdminPassword"] = "false",
                ["LocalIdentity:DatabasePath"] = Path.Combine(root, "identity", "cameraagent_identity.db"),
                ["LocalIdentity:CookieName"] = "CameraAgent.Standalone171.Auth",
                ["DeviceProvisioning:StateDirectory"] = Path.Combine(root, "provisioning"),
                ["Catalog:Root"] = Path.GetFullPath(catalogRoot),
                ["Catalog:RequiredCatalogId"] = "hyg-v42-production",
                ["Catalog:RequiredPackageKind"] = "Production",
                ["CentralIdentity:ServiceUrl"] = "http://central-forbidden.invalid/",
                ["SkyMonitor:BaseUrl"] = "http://central-forbidden.invalid",
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = string.Empty,
                ["Logging:LogLevel:Default"] = "Warning",
                ["Serilog:MinimumLevel:Default"] = "Warning"
            };
            var timeProvider = new AnchoredTimeProvider(fixedStartUtc);
            var laneFaultInjector = new ControllableLaneFaultInjector();
            var fixture = new StandaloneCameraAgentKestrelFixture(
                root,
                null,
                overrides,
                configPath,
                null,
                timeProvider,
                laneFaultInjector);
            await fixture.StartHostAsync().ConfigureAwait(false);
            await fixture.CompleteOwnerBootstrapForExistingAcceptanceTestsAsync().ConfigureAwait(false);
            return fixture;
        }
        catch
        {
            await DeleteWithRetriesAsync(root).ConfigureAwait(false);
            throw;
        }
    }

    internal async Task RestartHostAsync()
    {
        await StopHostAsync().ConfigureAwait(false);
        await StartHostAsync().ConfigureAwait(false);
    }

    internal async Task ChangeCurrentProjectedSceneConfigurationAsync()
    {
        var configuration = JsonNode.Parse(await File.ReadAllTextAsync(_configurationPath).ConfigureAwait(false))!.AsObject();
        configuration["module"]!["options"]!["maximumMagnitude"] = 5.75;
        configuration["pipeline"]!["steps"]!.AsArray()
            .Select(static node => node!.AsObject())
            .Single(static step => step["type"]!.GetValue<string>() == "ProjectedScene")
            ["options"]!["maximumMagnitude"] = 5.75;
        await File.WriteAllTextAsync(
            _configurationPath,
            configuration.ToJsonString(new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(_configurationPath).ConfigureAwait(false))!;
        if (persisted["module"]!["options"]!["maximumMagnitude"]!.GetValue<double>() != 5.75)
            throw new InvalidDataException("Changed standalone projected-scene configuration was not persisted.");
        var persistedStep = persisted["pipeline"]!["steps"]!.AsArray()
            .Select(static node => node!.AsObject())
            .Single(static step => step["type"]!.GetValue<string>() == "ProjectedScene");
        if (persistedStep["options"]!["maximumMagnitude"]!.GetValue<double>() != 5.75)
            throw new InvalidDataException("Changed standalone projected-scene step was not persisted.");
    }

    internal int ProjectedSceneMemoryCacheCount =>
        ((ProjectedSceneStore)Services.GetRequiredService<IProjectedSceneStore>()).Count;

    internal async Task<double> ReadLoadedMaximumMagnitudeAsync()
    {
        var config = await Services.GetRequiredService<ICameraAgentConfigurationLoader>()
            .LoadAsync(CancellationToken.None).ConfigureAwait(false);
        return config.ResolveProcessingSteps().Single(static step => step.Type == "ProjectedScene")
            .Options!.Value.GetProperty("maximumMagnitude").GetDouble();
    }

    private async Task CompleteOwnerBootstrapForExistingAcceptanceTestsAsync()
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var owner = await users.FindByEmailAsync(OwnerEmail).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The standalone acceptance owner was not seeded.");
        owner.PasswordChangeRequired = false;
        var result = await users.UpdateAsync(owner).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not prepare the standalone acceptance owner: {string.Join(", ", result.Errors.Select(static error => error.Code))}");
        }
    }

    internal void ArmLaneInterruption() => (_laneFaultInjector
        ?? throw new InvalidOperationException("The standalone fixture has no controllable lane fault injector."))
        .Arm();

    internal void DisarmLaneInterruption() => (_laneFaultInjector
        ?? throw new InvalidOperationException("The standalone fixture has no controllable lane fault injector."))
        .Disarm();

    internal Task StartStoppedHostAsync() => StartHostAsync();

    private static async Task WriteEnvironmentalSettingsAsync(string path)
    {
        var sources = new (string Kind, double? Numeric, bool? Boolean, string? Rig)[]
        {
            ("AirTemperature", 12.5, null, null),
            ("RelativeHumidity", 45, null, null),
            ("AtmosphericPressure", 101325, null, null),
            ("WindSpeed", 4, null, null),
            ("WindDirection", 180, null, null),
            ("WindGust", 6, null, null),
            ("PrecipitationRate", 0, null, null),
            ("RainState", null, false, null),
            ("SkyBrightness", 21, null, null),
            ("SkyQuality", 21, null, null),
            ("CloudCover", 0.2, null, null),
            ("CameraSensorTemperature", -10, null, "standalone-rig")
        };
        var configuredSources = new JsonArray();
        for (var index = 0; index < sources.Length; index++)
        {
            var source = sources[index];
            configuredSources.Add(new JsonObject
            {
                ["Id"] = $"standalone-{source.Kind}",
                ["Type"] = "VirtualEnvironment",
                ["Kind"] = source.Kind,
                ["Required"] = true,
                ["Triggers"] = new JsonArray("Periodic", "OnDemand"),
                ["ScheduleEpochUtc"] = "2026-01-15T08:00:00Z",
                ["PeriodSeconds"] = 30,
                ["EveryNthCapture"] = 3,
                ["ValidForSeconds"] = 120,
                ["StaleAfterSeconds"] = 45,
                ["RigId"] = source.Rig,
                ["Options"] = new JsonObject
                {
                    ["Seed"] = 209 + index,
                    ["EpochUtc"] = "2026-01-15T08:00:00Z",
                    ["NumericValue"] = source.Numeric,
                    ["BooleanValue"] = source.Boolean,
                    ["NoiseAmplitude"] = 0.25,
                    ["Uncertainty"] = source.Boolean is null ? 0.1 : null,
                    ["Quality"] = "Good",
                    ["Mode"] = "Normal",
                    ["DelayMilliseconds"] = 0,
                    ["AlgorithmVersion"] = "virtual-environment-source-v1"
                }
            });
        }
        var settings = new JsonObject
        {
            ["CameraAgent"] = new JsonObject
            {
                ["EnvironmentalAcquisition"] = new JsonObject
                {
                    ["Enabled"] = true,
                    ["MaximumConcurrency"] = 4,
                    ["QueueCapacity"] = 256,
                    ["Sources"] = configuredSources
                }
            }
        };
        await File.WriteAllTextAsync(
            path, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
    }

    internal async Task StopHostAsync()
    {
        if (_host is null)
        {
            return;
        }

        var host = _host;
        _host = null;
        await host.DisposeAsync().ConfigureAwait(false);
    }

    internal Task StopAsync() => StopHostAsync();

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "HttpClient takes ownership of its handler and the caller owns the returned client.")]
    internal async Task<HttpClient> CreateOwnerClientAsync()
    {
        var cookies = new CookieContainer();
        var client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            CookieContainer = cookies,
            CheckCertificateRevocationList = true
        })
        {
            BaseAddress = BaseAddress
        };
        var succeeded = false;
        try
        {
            using var login = await client.GetAsync(new Uri("/Account/Login", UriKind.Relative)).ConfigureAwait(false);
            login.EnsureSuccessStatusCode();
            var html = await login.Content.ReadAsStringAsync().ConfigureAwait(false);
            var match = Regex.Match(
                html,
                "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
                RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                throw new InvalidOperationException("The local login antiforgery token was not rendered.");
            }

            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value),
                ["Input.Email"] = OwnerEmail,
                ["Input.Password"] = OwnerPassword,
                ["Input.RememberMe"] = "false",
                ["_handler"] = "login"
            });
            using var response = await client.PostAsync(new Uri("/Account/Login", UriKind.Relative), form).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var configuredCookieName = _overrides["LocalIdentity:CookieName"]
                ?? throw new InvalidOperationException("The standalone fixture has no configured owner cookie name.");
            if (cookies.GetCookies(BaseAddress)[configuredCookieName] is null)
            {
                var storedCookieNames = string.Join(",", cookies.GetCookies(BaseAddress).Cast<Cookie>().Select(static cookie => cookie.Name));
                throw new InvalidOperationException(
                    $"The local login did not issue configured cookie {configuredCookieName}; cookies={storedCookieNames}.");
            }
            using var ownerCheck = await client.GetAsync(
                new Uri("/api/v1/operations/summary", UriKind.Relative)).ConfigureAwait(false);
            if (ownerCheck.StatusCode != HttpStatusCode.OK)
            {
                var body = await ownerCheck.Content.ReadAsStringAsync().ConfigureAwait(false);
                var ownerState = await ReadOwnerStateAsync().ConfigureAwait(false);
                var storedCookieNames = string.Join(",", cookies.GetCookies(BaseAddress).Cast<Cookie>().Select(static cookie => cookie.Name));
                var loginDestination = response.RequestMessage?.RequestUri?.AbsolutePath ?? "unknown";
                throw new InvalidOperationException(
                    $"The local owner session check returned {(int)ownerCheck.StatusCode} after login destination {loginDestination}, cookies={storedCookieNames}, and {ownerState}: {body}");
            }
            succeeded = true;
            return client;
        }
        finally
        {
            if (!succeeded)
            {
                client.Dispose();
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Best-effort diagnostics must not replace the original HTTP authorization failure.")]
    private async Task<string> ReadOwnerStateAsync()
    {
        try
        {
            using var scope = Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var owner = await users.FindByEmailAsync(OwnerEmail).ConfigureAwait(false);
            if (owner is null)
            {
                return "no persisted configured owner";
            }
            return $"persisted owner={owner.IsSiteOwner}, normalized-email-match={string.Equals(owner.NormalizedEmail, users.NormalizeEmail(OwnerEmail), StringComparison.Ordinal)}";
        }
        catch (Exception exception)
        {
            return $"owner-state-diagnostic-unavailable={exception.GetType().Name}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopHostAsync().ConfigureAwait(false);
        _catalog?.Dispose();
        await DeleteWithRetriesAsync(_root).ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The created factory is transferred to HostInstance ownership or disposed by the failure path.")]
    private async Task StartHostAsync()
    {
        if (_host is not null)
        {
            throw new InvalidOperationException("The standalone CameraAgent host is already running.");
        }

        var factory = new StandaloneWebApplicationFactory(
            _root,
            _overrides,
            _environmentalSettingsPath,
            _outboundAttempts,
            _timeProvider,
            _laneFaultInjector);
        factory.UseKestrel(0);
        try
        {
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });
            var server = factory.Services.GetRequiredService<IServer>();
            var address = server.Features.Get<IServerAddressesFeature>()?.Addresses.SingleOrDefault()
                ?? throw new InvalidOperationException("Kestrel did not publish its loopback address.");
            client.BaseAddress = new Uri(address, UriKind.Absolute);
            await factory.Services.GetRequiredService<CameraAgentIdentityInitialization>()
                .WaitAsync(CancellationToken.None).ConfigureAwait(false);
            _host = new HostInstance(factory, client);
        }
        catch
        {
            await factory.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task WriteConfigurationAsync(
        string configPath,
        string root,
        bool useSyntheticCalibration,
        bool useProjectedScene)
    {
        var template = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "cameraagent.integration.json")).ConfigureAwait(false);
        var json = template
            .Replace("__STORAGE_ROOT__", JsonSerializer.Serialize(root), StringComparison.Ordinal)
            .Replace(
                "__TRANSIENT_EPOCH_UTC__",
                JsonSerializer.Serialize(DateTimeOffset.UtcNow.AddMinutes(-1)),
                StringComparison.Ordinal);
        var configuration = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidDataException("The standalone CameraAgent fixture configuration is invalid.");
        configuration["agentId"] = AgentId;
        var localStorage = configuration["processingSteps"]!.AsArray()
            .Select(static node => node!.AsObject())
            .Single(static step => step["id"]!.GetValue<string>() == "LocalStorage");
        var options = localStorage["options"]!.AsObject();
        options["queueForUpload"] = true;
        foreach (var policy in options["policies"]!.AsArray())
        {
            policy!.AsObject()["queueForUpload"] = true;
        }
        if (useProjectedScene)
        {
            options["queueForUpload"] = false;
            foreach (var policy in options["policies"]!.AsArray()) policy!.AsObject()["queueForUpload"] = false;
            var projectedScene = JsonNode.Parse("""
                {
                  "id": "ProjectedScene",
                  "type": "ProjectedScene",
                  "order": 1,
                  "dependsOn": ["$raw"],
                  "publication": { "persistence": "durable-local" },
                  "options": { "outputVariant": "projected-scene-v1", "maximumMagnitude": 6.5, "maximumResults": 9 }
                }
                """)!;
            configuration["processingSteps"]!.AsArray().Insert(0, projectedScene);
            localStorage["dependsOn"]!.AsArray().Add("ProjectedScene");
            foreach (var stepNode in configuration["processingSteps"]!.AsArray())
            {
                var step = stepNode!.AsObject();
                var type = step["type"]!.GetValue<string>();
                if (type.Contains("NoOpFileStorageProcessingStep", StringComparison.Ordinal)) step["type"] = "Storage";
                if (type.Contains("TelemetryCaptureProcessingStep", StringComparison.Ordinal)) step["type"] = "Telemetry";
                step["dependsOn"] ??= new JsonArray("$raw");
                var stepOptions = step["options"]?.AsObject();
                if (stepOptions?.Remove("enabled", out var enabled) == true)
                {
                    step["enabled"] = enabled;
                }
            }
            configuration["pipeline"] = new JsonObject
            {
                ["schemaVersion"] = "cameraagent-capture-pipeline-v2",
                ["dependencyPolicy"] = "reject-enabled-dependent-v1",
                ["steps"] = configuration["processingSteps"]!.DeepClone()
            };
            configuration.Remove("processingSteps");
        }
        if (useSyntheticCalibration)
        {
            var model = JsonNode.Parse("""
                {
                  "schemaVersion": "synthetic-calibration-model-v1",
                  "seed": 195,
                  "biasPedestalAdu": 100,
                  "biasPatternAmplitudeAdu": 8,
                  "darkCurrentAduPerSecond": 5.0,
                  "darkPatternFraction": 0.25,
                  "pixelResponseVariationFraction": 0.1,
                  "vignettingStrength": 0.2,
                  "flatSignalAdu": 20000,
                  "biasExposure": "00:00:00.0010000",
                  "darkExposure": "00:00:10",
                  "flatExposure": "00:00:02",
                  "gain": 1.0,
                  "temperatureC": -10.0,
                  "defects": [{ "x": 4, "y": 4, "fixedValueAdu": 65535 }]
                }
                """)!;
            var moduleOptions = configuration["module"]!["options"]!.AsObject();
            moduleOptions["asi174Sensor"] = new JsonObject { ["enabled"] = false };
            moduleOptions["syntheticCalibration"] = model.DeepClone();
            var calibration = configuration["processingSteps"]!.AsArray()
                .Select(static node => node!.AsObject())
                .Single(static step => step["id"]!.GetValue<string>() == "Calibration");
            calibration["type"] = "Calibration";
            calibration["options"]!["strategy"] = "SyntheticReferences";
            calibration["options"]!["outputVariant"] = "synthetic-corrected";
            calibration["options"]!["syntheticCalibration"] = model.DeepClone();
        }

        await File.WriteAllTextAsync(
            configPath,
            configuration.ToJsonString(new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
    }

    private static async Task WriteProductionSmokeConfigurationAsync(string configPath, string root)
    {
        var configuration = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "cameraagent.standalone-production-smoke.json")).ConfigureAwait(false))?.AsObject()
            ?? throw new InvalidDataException("The production smoke CameraAgent configuration is invalid.");
        configuration["agentId"] = AgentId;
        var localStorage = configuration["processingSteps"]!.AsArray()
            .Select(static node => node!.AsObject())
            .Single(static step => step["id"]!.GetValue<string>() == "LocalStorage");
        localStorage["options"]!["storageRoot"] = root;
        await File.WriteAllTextAsync(
            configPath,
            configuration.ToJsonString(new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
    }

    private static async Task DeleteWithRetriesAsync(string path)
    {
        SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                return;
            }
            catch (Exception exception) when (
                attempt < 9 && exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1))).ConfigureAwait(false);
            }
        }
        throw new IOException($"Acceptance fixture cleanup failed; retained path: {path}");
    }

    internal sealed record OutboundHttpAttempt(string Method, Uri? RequestUri, DateTimeOffset AttemptedUtc);

    private sealed class HostInstance(
        WebApplicationFactory<Program> factory,
        HttpClient lifetimeClient) : IAsyncDisposable
    {
        internal WebApplicationFactory<Program> Factory { get; } = factory;

        internal HttpClient LifetimeClient { get; } = lifetimeClient;

        public async ValueTask DisposeAsync()
        {
            LifetimeClient.Dispose();
            await Factory.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class StandaloneWebApplicationFactory(
        string root,
        IReadOnlyDictionary<string, string?> overrides,
        string? environmentalSettingsPath,
        ConcurrentQueue<OutboundHttpAttempt> outboundAttempts,
        TimeProvider? timeProvider,
        ControllableLaneFaultInjector? laneFaultInjector) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(timeProvider is null ? "Development" : "StandaloneProductionSmoke");
            // Program captures local Identity settings before WebApplicationFactory app overrides are applied.
            foreach (var setting in overrides.Where(static setting =>
                         setting.Key.StartsWith("LocalIdentity:", StringComparison.Ordinal)))
            {
                builder.UseSetting(setting.Key, setting.Value);
            }
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                if (environmentalSettingsPath is not null)
                {
                    configuration.AddJsonFile(environmentalSettingsPath, optional: false, reloadOnChange: false);
                }
                configuration.AddInMemoryCollection(overrides);
            });
            builder.ConfigureServices(services =>
            {
                if (timeProvider is not null)
                {
                    services.AddSingleton(timeProvider);
                }
                if (laneFaultInjector is not null)
                {
                    services.RemoveAll<ICaptureLaneFaultInjector>();
                    services.AddSingleton<ICaptureLaneFaultInjector>(laneFaultInjector);
                }
                services.AddDataProtection()
                    .SetApplicationName("HVO.SkyMonitor.CameraAgent.StandaloneAcceptanceTests")
                    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "dataprotection")));
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(messageBuilder =>
                        messageBuilder.AdditionalHandlers.Insert(
                            0,
                            new RejectingOutboundHttpHandler(outboundAttempts))));
            });
        }
    }

    private sealed class ControllableLaneFaultInjector : ICaptureLaneFaultInjector
    {
        private volatile bool _armed;
        private int _injectedCount;

        internal int InjectedCount => Volatile.Read(ref _injectedCount);

        internal void Arm() => _armed = true;

        internal void Disarm() => _armed = false;

        public void Inject(CaptureLaneFaultPoint point)
        {
            if (_armed && point == CaptureLaneFaultPoint.BeforeHandler)
            {
                Interlocked.Increment(ref _injectedCount);
                throw new InvalidOperationException("Issue #171 injected a restart interruption before lane handling.");
            }
        }
    }

    private sealed class AnchoredTimeProvider(DateTimeOffset originUtc) : TimeProvider
    {
        private readonly long _originTimestamp = TimeProvider.System.GetTimestamp();

        public override DateTimeOffset GetUtcNow() =>
            originUtc + TimeProvider.System.GetElapsedTime(_originTimestamp, TimeProvider.System.GetTimestamp());

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;

        public override long GetTimestamp() => TimeProvider.System.GetTimestamp();

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) => TimeProvider.System.CreateTimer(callback, state, dueTime, period);
    }

    private sealed class RejectingOutboundHttpHandler(
        ConcurrentQueue<OutboundHttpAttempt> outboundAttempts) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            outboundAttempts.Enqueue(new OutboundHttpAttempt(
                request.Method.Method,
                request.RequestUri,
                DateTimeOffset.UtcNow));
            throw new HttpRequestException("Standalone acceptance rejected an outbound HTTP request.");
        }
    }
}
