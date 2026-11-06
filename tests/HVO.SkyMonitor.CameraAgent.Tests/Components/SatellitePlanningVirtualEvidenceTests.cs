using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Bunit;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.OrbitalPlanning;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

/// <summary>
/// Explicit offline evidence export through the production planner, authenticated UI adapter and Razor components.
/// Presentation preservation checks are consumer checks, not independent numerical or optical-visibility oracles.
/// </summary>
[TestClass]
[TestCategory("Manual")]
public sealed class SatellitePlanningVirtualEvidenceTests
{
    private const string OutputVariable = "HVO_SATELLITE_PLANNING_EVIDENCE_DIR";
    private static readonly DateTimeOffset FrozenNow = new(2026, 10, 20, 3, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions SettingsJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly string[] ComponentSourcePaths =
    [
        "src/HVO.SkyMonitor.CameraAgent/Components/Operations/SatellitePassPlanner.razor",
        "src/HVO.SkyMonitor.CameraAgent/Components/Operations/SatellitePassPlanner.razor.cs",
        "src/HVO.SkyMonitor.CameraAgent/Components/Operations/SatellitePassPlanner.razor.css",
        "src/HVO.SkyMonitor.CameraAgent/Components/Operations/SatellitePassArc.razor",
        "src/HVO.SkyMonitor.CameraAgent/Components/Operations/SatellitePassArc.razor.cs",
        "src/HVO.SkyMonitor.CameraAgent/Components/Operations/SatellitePassArc.razor.css",
        "src/HVO.SkyMonitor.CameraAgent/Services/CameraAgentSatellitePlanningUiService.cs",
        "src/HVO.SkyMonitor.CameraAgent/Authorization/CameraAgentAuthorization.cs",
        "src/HVO.SkyMonitor.CameraAgent.Common/OrbitalPlanning/CameraAgentSatellitePlanner.cs",
        "tests/HVO.SkyMonitor.CameraAgent.Tests/OrbitalPlanning/PlanningTestInputs.cs",
        "tests/HVO.SkyMonitor.CameraAgent.Tests/Components/SatellitePlanningVirtualEvidenceTests.cs"
    ];
    private static readonly string[] Limitations =
    [
        "Manual frozen component HTML, with inert controls, is not an interactive backend or runtime DI integration test.",
        "Snapshot reader and time source are test-only immutable input adapters; nothing was fetched or installed as a runtime default.",
        "The shared SkyMap projection uses an empty test celestial catalog; this evidence concerns satellite planning, not star-catalog completeness.",
        "Existing reduced virtual-camera configured rigs are nominal and unmeasured; height is assumed ellipsoidal.",
        "All illumination and physical orbit uncertainty remain Unknown. No solar/shadow, brightness, visibility or measured-trail qualification.",
        "No camera recapture, reference-fixture regeneration, benchmark, capture persistence, live snapshot refresh or independent numerical oracle.",
        "Session IDs include a production nonce and elapsed work timings vary; canonical input/product files are retained with individual byte checksums."
    ];

    public Microsoft.VisualStudio.TestTools.UnitTesting.TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ExportFrozenIssAndTwoStarlinkSearchSelectionAsync()
    {
        var output = Environment.GetEnvironmentVariable(OutputVariable);
        if (string.IsNullOrWhiteSpace(output)) Assert.Inconclusive($"Set {OutputVariable} to a new export directory.");
        output = Path.GetFullPath(output);
        Assert.IsFalse(Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any(),
            "Use a new empty directory; previously retained evidence must not be overwritten.");
        Directory.CreateDirectory(output);
        var root = RepositoryRoot();
        var styles = ReadStyles(root);
        var cases = new List<object>();
        cases.Add(await ExportCaseAsync(root, output, styles, "iss", "seattle", "six-facts", "25544",
            new(2026, 10, 20, 12, 55, 0, TimeSpan.Zero), ["25544"]).ConfigureAwait(false));
        cases.Add(await ExportCaseAsync(root, output, styles, "starlinks", "hvo", "two-starlinks", "starlink",
            new(2026, 10, 20, 3, 55, 0, TimeSpan.Zero), ["56838", "65283"]).ConfigureAwait(false));
        WriteJson(output, "evidence.json", new
        {
            schema = "hvo-satellite-planning-real-ui-evidence-v1",
            exportedUtc = DateTimeOffset.UtcNow,
            sourceRevision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION"),
            componentSourceFiles = ComponentSourcePaths.Select(path => FileFact(root, Path.Combine(root, path))).ToArray(),
            assemblies = new[] { typeof(SatellitePlanningVirtualEvidenceTests).Assembly, typeof(SatellitePassPlanner).Assembly,
                typeof(CameraAgentSatellitePlanner).Assembly, typeof(OrbitalPassArcJson).Assembly }
                .Select(assembly => FileFact(root, assembly.Location)).ToArray(),
            frozenCurrentTimeUtc = FrozenNow,
            frozenTimeMeaning = "Only GetUtcNow is pinned for retrieval age and session TTL; real retained EOP/leaps and real monotonic work limits remain in use.",
            chain = new[] { "bUnit form submit and pass-button click", nameof(CameraAgentSatellitePlanningUiService),
                CameraAgentAuthorizationPolicyNames.OperationsReadV1, nameof(CameraAgentSatellitePlanner),
                nameof(CameraAgentOrbitalPlanningEngineFactory), nameof(SatellitePassPlanner), nameof(SatellitePassArc) },
            authentication = "Real operations-read policy and credential/site-owner handlers; canonical test cookie identity and mocked UserManager lookup/stamp storage. No HTTP cookie issuance or live host login is claimed.",
            limitations = Limitations,
            styles = styles.Inputs,
            cases
        });
        var checksums = Directory.GetFiles(output).Order(StringComparer.Ordinal).Select(path => new
        {
            file = Path.GetFileName(path),
            bytes = new FileInfo(path).Length,
            sha256 = PlanningTestInputs.Hash(File.ReadAllBytes(path))
        }).ToArray();
        WriteJson(output, "checksums.json", checksums);
        TestContext.AddResultFile(Path.Combine(output, "evidence.json"));
        TestContext.AddResultFile(Path.Combine(output, "checksums.json"));
        TestContext.WriteLine($"Exported actual frozen ISS and two-Starlink UI evidence: {output}");
    }

    private async Task<object> ExportCaseAsync(string root, string output, StylePack styles, string name,
        string site, string snapshotName, string filter, DateTimeOffset start, string[] expectedObjects)
    {
        var token = TestContext.CancellationToken;
        var input = new PlanningTestInputs(realEngine: true);
        input.Time.Now = FrozenNow;
        input.Snapshots.Current = PlanningTestInputs.Snapshot(snapshotName);
        var settingsPath = Path.Combine(root, "tests", "fixtures", "orbital-prediction-v1", "virtual-camera", $"settings-{site}.json");
        var settingsBytes = await File.ReadAllBytesAsync(settingsPath, token).ConfigureAwait(false);
        var configured = JsonSerializer.Deserialize<CameraModuleConfig>(settingsBytes, SettingsJson)!;
        var location = configured.Observatory;
        input.Configuration.Value = configured with
        {
            DeploymentLocation = DeploymentLocationSnapshot.Create($"retained-{site}-virtual-site", 1, "startup-seed", null,
                DateTimeOffset.UnixEpoch, null, location.LatitudeDegrees, location.LongitudeDegrees,
                location.ElevationMeters, location.TimeZoneId)
        };
        var scene = await input.Sky.ProjectAsync(start, token).ConfigureAwait(false);
        var planner = new RecordingPlanner(input.Planner);
        var authentication = new Authentication();
        using var context = new BunitContext();
        ConfigureAuthorization(context, authentication, planner);
        var anonymous = await context.Services.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(authentication.User, CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false);
        Assert.IsFalse(anonymous.Succeeded);
        var cut = context.Render<SatellitePassPlanner>(parameters => parameters
            .Add(component => component.Observer, scene.Observer).Add(component => component.Rig, scene.Geometry)
            .Add(component => component.AtUtc, start));
        await cut.Find("#satellite-hours").InputAsync(new() { Value = "1" }).ConfigureAwait(false);
        await cut.Find("#satellite-elevation").InputAsync(new() { Value = "0" }).ConfigureAwait(false);
        await cut.Find("#satellite-filter").InputAsync(new() { Value = filter }).ConfigureAwait(false);

        // Exercise denial through the same real adapter/policy before admitting the test owner's identity.
        await cut.Find("form").SubmitAsync().ConfigureAwait(false);
        Assert.Contains("Authorization is required", cut.Markup, StringComparison.Ordinal);
        Assert.IsEmpty(planner.Searches);
        Freeze(output, $"{name}-unauthorized", cut.Markup, styles, "Anonymous read denied by the operations-read policy");
        authentication.User = OwnerPrincipal();
        await cut.Find("form").SubmitAsync().ConfigureAwait(false);
        Assert.HasCount(1, planner.Searches);
        var search = planner.Searches.Single().Response.Value;
        Assert.IsNotNull(search, cut.Markup);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, planner.Searches.Single().Response.Outcome);
        Assert.AreEqual(OrbitalExecutionStatus.Complete, search.Prediction.Status, cut.Markup);
        Assert.AreEqual(expectedObjects.Length, search.MatchingObjects);
        Assert.AreEqual(expectedObjects.Length, search.SelectedObjects);
        CollectionAssert.AreEquivalent(expectedObjects, search.Passes.Select(row => row.Pass.Lineage.ObjectIdentity.CatalogNumber).Distinct().ToArray());
        Assert.AreEqual(RigProjectionContextFactory.Create(input.Configuration.Value.Rig), search.Context.RigProjection);
        Assert.AreEqual(scene.Observer.CanonicalSha256, search.Context.Observer.CanonicalSha256);
        Assert.AreEqual(scene.Geometry.RigProfileHashSha256, search.Context.Rig.RigProfileHashSha256);
        Assert.IsFalse(search.Prediction.Model.NominalZeroEarthOrientation);
        Assert.AreEqual(input.Times.Current!.EarthOrientationSha256, search.Prediction.Model.EarthOrientationSha256);
        Assert.AreEqual(input.Times.Current.LeapSecondsSha256, search.Prediction.Model.LeapSecondsSha256);
        Assert.Contains(location.TimeZoneId!, cut.Markup, StringComparison.Ordinal);
        Assert.Contains(start.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture), cut.Markup, StringComparison.Ordinal);
        Assert.Contains(site == "hvo" ? "-07:00 (America/Phoenix)" : "-07:00 (America/Los_Angeles)", cut.Markup, StringComparison.Ordinal);
        WriteJson(output, $"{name}-search.json", search);
        WriteJson(output, $"{name}-configured-context.json", new { configuration = input.Configuration.Value, scene.Observer, scene.Geometry, search.Context.RigProjection });
        foreach (var row in search.Passes)
            await File.WriteAllBytesAsync(Path.Combine(output, $"{name}-pass-{row.Pass.ProductSha256}.json"),
                OrbitalPredictionJson.SerializePass(row.Pass), token).ConfigureAwait(false);

        var selections = new List<object>();
        foreach (var identity in expectedObjects)
        {
            var row = search.Passes.First(candidate => candidate.Pass.Lineage.ObjectIdentity.CatalogNumber == identity);
            var button = cut.FindAll(".satellite-select").Single(candidate => candidate.TextContent.Contains($"NORAD {identity}", StringComparison.Ordinal));
            await button.ClickAsync(new()).ConfigureAwait(false);
            Assert.AreEqual(search.SearchIdentitySha256, planner.Arcs[^1].SearchIdentity);
            Assert.AreEqual(row.Pass.ProductSha256, planner.Arcs[^1].ParentIdentity);
            var detail = planner.Arcs[^1].Response.Value;
            Assert.IsNotNull(detail, cut.Markup);
            Assert.AreEqual(OrbitalExecutionStatus.Complete, detail.Prediction.Status, cut.Markup);
            var arc = detail.Prediction.Products.Single();
            Assert.AreEqual(row.Pass.ProductSha256, arc.Pass.ProductSha256);
            Assert.AreEqual(row.Pass.SearchInterval, arc.Pass.SearchInterval);
            Assert.AreEqual(input.Snapshots.Current.Manifest.ManifestSha256, arc.Pass.Lineage.SnapshotManifestSha256);
            Assert.IsTrue(arc.Samples.Count > 2);
            Assert.IsTrue(arc.Samples.All(sample => sample.Pixel is null && sample.Illumination == OrbitalIllumination.Unknown));
            Assert.IsNull(arc.OrbitUncertainty.Value);
            await cut.Find("#satellite-show-rig").ChangeAsync(new() { Value = true }).ConfigureAwait(false);
            AssertProjectionPreserved(cut, arc, search.Context);
            var fullSkyRenderedPoints = RenderedPointCount(cut, ".satellite-arc > svg .predicted-path");
            var rigRenderedPoints = RenderedPointCount(cut, ".rig-preview .predicted-path");
            var stem = $"{name}-{identity}";
            Freeze(output, stem + "-selected", cut.Markup, styles, $"{row.DisplayName}: real full-pass angular arc and configured-rig preview");
            var canonical = OrbitalPassArcJson.Serialize(arc);
            await File.WriteAllBytesAsync(Path.Combine(output, stem + "-arc.json"), canonical, token).ConfigureAwait(false);
            WriteJson(output, stem + "-detail.json", detail);
            await File.WriteAllTextAsync(Path.Combine(output, stem + "-full-sky.svg"), cut.Find(".satellite-arc > svg").OuterHtml, token).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(output, stem + "-rig.svg"), cut.Find(".rig-preview svg").OuterHtml, token).ConfigureAwait(false);

            var readsBeforeToggles = (planner.Searches.Count, planner.Arcs.Count);
            var labels = cut.FindAll(".arc-time-label").Count;
            Assert.IsTrue(labels > 0);
            await cut.Find("#satellite-show-path").ChangeAsync(new() { Value = false }).ConfigureAwait(false);
            Assert.IsEmpty(cut.FindAll(".predicted-path"));
            Assert.HasCount(labels, cut.FindAll(".arc-time-label"));
            Assert.HasCount(1, cut.FindAll(".rig-preview"));
            Freeze(output, stem + "-path-hidden", cut.Markup, styles, "Predicted path hidden; independent time labels and rig panel retained");
            await cut.Find("#satellite-show-path").ChangeAsync(new() { Value = true }).ConfigureAwait(false);
            await cut.Find("#satellite-show-labels").ChangeAsync(new() { Value = false }).ConfigureAwait(false);
            Assert.IsEmpty(cut.FindAll(".arc-time-label"));
            Assert.IsNotEmpty(cut.FindAll(".predicted-path"));
            Assert.IsNotEmpty(cut.FindAll(".arc-anchor"));
            Freeze(output, stem + "-labels-hidden", cut.Markup, styles, "Time labels hidden; predicted path and exact anchor facts retained");
            await cut.Find("#satellite-show-labels").ChangeAsync(new() { Value = true }).ConfigureAwait(false);
            await cut.Find("#satellite-show-rig").ChangeAsync(new() { Value = false }).ConfigureAwait(false);
            Assert.IsEmpty(cut.FindAll(".rig-preview"));
            Assert.IsNotEmpty(cut.FindAll(".satellite-arc > svg .predicted-path"));
            Assert.AreEqual(readsBeforeToggles, (planner.Searches.Count, planner.Arcs.Count));
            Freeze(output, stem + "-rig-hidden", cut.Markup, styles, "Configured-rig preview hidden; full angular path retained");

            await cut.FindAll(".satellite-select").Single(candidate => candidate.TextContent.Contains($"NORAD {identity}", StringComparison.Ordinal))
                .ClickAsync(new()).ConfigureAwait(false);
            var repeatedArc = planner.Arcs[^1].Response.Value!.Prediction.Products.Single();
            Assert.AreEqual(arc.ProductSha256, repeatedArc.ProductSha256);
            CollectionAssert.AreEqual(canonical, OrbitalPassArcJson.Serialize(repeatedArc));
            selections.Add(new
            {
                objectIdentity = identity,
                row.DisplayName,
                parentPassSha256 = row.Pass.ProductSha256,
                arcSha256 = arc.ProductSha256,
                canonicalFileSha256 = PlanningTestInputs.Hash(canonical),
                sampleCount = arc.Samples.Count,
                segmentCount = arc.Segments.Count,
                arc.Anchors,
                minimumAltitudeDegrees = arc.Samples.Min(sample => sample.Horizontal.AltitudeDegrees),
                maximumAltitudeDegrees = arc.Samples.Max(sample => sample.Horizontal.AltitudeDegrees),
                belowGeometricHorizonSamples = arc.Samples.Count(sample => sample.Horizontal.AltitudeDegrees < 0),
                fullSkyRenderedPoints,
                rigRenderedPoints,
                sharedRigProjectedSamples = arc.Samples.Count(sample => ProjectorFactory.Create(search.Context.RigProjection).Project(sample.Horizontal) is not null),
                repeatedSelectionPreservedExactCanonicalBytes = true,
                togglesDidNotReadPlanner = true
            });
        }

        object? supplemental = null;
        if (name == "iss")
        {
            // The retained pass supplies the instant; no expected propagation numbers are generated here.
            var parent = search.Passes.Single().Pass;
            var clippedStart = parent.PeakUtc;
            cut.Render(parameters => parameters.Add(component => component.AtUtc, clippedStart));
            await cut.Find("form").SubmitAsync().ConfigureAwait(false);
            var clipped = planner.Searches[^1].Response.Value;
            Assert.IsNotNull(clipped, cut.Markup);
            var clippedPass = clipped.Passes.Single().Pass;
            Assert.AreEqual(OrbitalPassBoundary.SearchWindow, clippedPass.StartBoundary);
            Assert.AreEqual(clippedStart, clippedPass.PassInterval.StartUtc);
            Assert.Contains("Window start (clipped)", cut.Markup, StringComparison.Ordinal);
            await cut.Find(".satellite-select").ClickAsync(new()).ConfigureAwait(false);
            var clippedArc = planner.Arcs[^1].Response.Value!.Prediction.Products.Single();
            Assert.AreEqual(clippedPass.ProductSha256, clippedArc.Pass.ProductSha256);
            WriteJson(output, "iss-clipped-search.json", clipped);
            await File.WriteAllBytesAsync(Path.Combine(output, "iss-clipped-arc.json"), OrbitalPassArcJson.Serialize(clippedArc), token).ConfigureAwait(false);
            Freeze(output, "iss-clipped", cut.Markup, styles, "Real ISS pass clipped at the new search-window start");

            var belowStart = parent.PassInterval.EndUtc.AddSeconds(1);
            cut.Render(parameters => parameters.Add(component => component.AtUtc, belowStart));
            await cut.Find("form").SubmitAsync().ConfigureAwait(false);
            var below = planner.Searches[^1].Response.Value;
            Assert.IsNotNull(below, cut.Markup);
            Assert.AreEqual(OrbitalExecutionStatus.Complete, below.Prediction.Status);
            Assert.IsEmpty(below.Passes);
            Assert.Contains("No passes were returned", cut.Markup, StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll(".satellite-detail"));
            WriteJson(output, "iss-no-pass-search.json", below);
            Freeze(output, "iss-no-pass", cut.Markup, styles, "Real complete one-hour ISS search with no above-threshold pass returned");
            supplemental = new
            {
                clippedSearchIdentity = clipped.SearchIdentitySha256,
                clippedPassSha256 = clippedPass.ProductSha256,
                noPassSearchIdentity = below.SearchIdentitySha256,
                noPassWindow = below.Prediction.RequestedInterval,
                qualification = "No pass returned applies only to the selected ISS record, threshold and bounded window; no empty-sky or optical-visibility claim."
            };
        }
        var sourceFiles = new[]
        {
            settingsPath,
            Path.Combine(root, "tests/fixtures/orbital-prediction-v1/time/predicted-2026.finals2000A"),
            Path.Combine(root, "tests/fixtures/orbital-prediction-v1/time/leap-seconds.list"),
            Path.Combine(root, $"tests/fixtures/orbital-prediction-v1/virtual-camera/snapshots/{snapshotName}/manifest.json"),
            Path.Combine(root, $"tests/fixtures/orbital-prediction-v1/virtual-camera/snapshots/{snapshotName}/records.json")
        }.Select(path => FileFact(root, path)).ToArray();
        WriteJson(output, name + "-calls.json", new { planner.Searches, planner.Arcs });
        return new
        {
            name,
            filter,
            startUtc = start,
            endUtc = start.AddHours(1),
            minimumElevationDegrees = 0,
            sourceFiles,
            search.SearchIdentitySha256,
            search.Context,
            search.Prediction.Model,
            input.Snapshots.Current.Manifest,
            selections,
            supplemental,
            authorizedSearchCalls = planner.Searches.Count,
            authorizedArcCalls = planner.Arcs.Count,
            anonymousSearchDidNotReachPlanner = true
        };
    }

    private static void ConfigureAuthorization(BunitContext context, Authentication authentication, RecordingPlanner planner)
    {
        var manager = new Mock<UserManager<ApplicationUser>>(Mock.Of<IUserStore<ApplicationUser>>(), Options.Create(new IdentityOptions()),
            new PasswordHasher<ApplicationUser>(), Array.Empty<IUserValidator<ApplicationUser>>(), Array.Empty<IPasswordValidator<ApplicationUser>>(),
            new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), Mock.Of<IServiceProvider>(), NullLogger<UserManager<ApplicationUser>>.Instance);
        manager.SetupGet(value => value.SupportsUserSecurityStamp).Returns(true);
        manager.Setup(value => value.FindByIdAsync("evidence-owner")).ReturnsAsync(new ApplicationUser
        {
            Id = "evidence-owner",
            IsSiteOwner = true,
            PasswordChangeRequired = false,
            NormalizedEmail = "EVIDENCE@CAMERAAGENT.TEST",
            SecurityStamp = "evidence-stamp"
        });
        manager.Setup(value => value.GetSecurityStampAsync(It.IsAny<ApplicationUser>())).ReturnsAsync("evidence-stamp");
        context.Services.AddSingleton(manager.Object);
        context.Services.AddSingleton<ILookupNormalizer, UpperInvariantLookupNormalizer>();
        context.Services.AddSingleton(Options.Create(new LocalIdentityOptions { AdminEmail = "evidence@cameraagent.test" }));
        context.Services.AddSingleton<AuthenticationStateProvider>(authentication);
        // AddAuthorization uses TryAdd; bUnit's placeholder would otherwise win over the real host policy service.
        context.Services.RemoveAll<IAuthorizationService>();
        context.Services.AddCameraAgentAuthorization();
        context.Services.AddSingleton<ICameraAgentSatellitePlanner>(planner);
        context.Services.AddCameraAgentSatellitePlanningUi();
    }

    private static ClaimsPrincipal OwnerPrincipal() => new(new ClaimsIdentity(
    [
        new(ClaimTypes.NameIdentifier, "evidence-owner"),
        new(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType),
        new(new IdentityOptions().ClaimsIdentity.SecurityStampClaimType, "evidence-stamp")
    ], IdentityConstants.ApplicationScheme));

    private sealed class Authentication : AuthenticationStateProvider
    {
        internal ClaimsPrincipal User { get; set; } = new(new ClaimsIdentity());
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(User));
    }

    private sealed record SearchCall(CameraAgentSatellitePassSearchRequest Request, CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassSearchView> Response);
    private sealed record ArcCall(string SearchIdentity, string ParentIdentity, CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassArcView> Response);

    /// <summary>Records only; all responses come unchanged from the real facade.</summary>
    private sealed class RecordingPlanner(ICameraAgentSatellitePlanner inner) : ICameraAgentSatellitePlanner
    {
        internal List<SearchCall> Searches { get; } = [];
        internal List<ArcCall> Arcs { get; } = [];
        public async ValueTask<CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassSearchView>> SearchAsync(
            CameraAgentSatellitePassSearchRequest request, CancellationToken cancellationToken)
        {
            var response = await inner.SearchAsync(request, cancellationToken).ConfigureAwait(false);
            Searches.Add(new(request, response));
            return response;
        }
        public async ValueTask<CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassArcView>> GetArcAsync(
            string searchIdentitySha256, string parentPassSha256, CancellationToken cancellationToken)
        {
            var response = await inner.GetArcAsync(searchIdentitySha256, parentPassSha256, cancellationToken).ConfigureAwait(false);
            Arcs.Add(new(searchIdentitySha256, parentPassSha256, response));
            return response;
        }
    }

    private static void AssertProjectionPreserved(IRenderedComponent<SatellitePassPlanner> cut, OrbitalPassArcV1 arc,
        CameraAgentSatellitePlanningContext context)
    {
        var fullSky = ProjectorFactory.Create(new(ProjectionModel.EquidistantFisheye, 256, 256, 480 / Math.PI,
            480 / Math.PI, 512, 512, ProjectionAperture.Circular, 240));
        CollectionAssert.AreEqual(ProjectedPaths(arc, fullSky), cut.FindAll(".satellite-arc > svg .predicted-path")
            .Select(path => path.GetAttribute("points")!).ToArray());
        CollectionAssert.AreEqual(ProjectedPaths(arc, ProjectorFactory.Create(context.RigProjection)),
            cut.FindAll(".rig-preview .predicted-path").Select(path => path.GetAttribute("points")!).ToArray());
    }

    private static string[] ProjectedPaths(OrbitalPassArcV1 arc, IImageProjector projector)
    {
        var paths = new List<string>();
        foreach (var segment in arc.Segments)
        {
            var run = new List<string>();
            foreach (var sample in arc.Samples.Skip(segment.StartSampleIndex).Take(segment.SampleCount))
            {
                if (projector.Project(sample.Horizontal) is { } point)
                    run.Add($"{point.X.ToString("0.###", CultureInfo.InvariantCulture)},{point.Y.ToString("0.###", CultureInfo.InvariantCulture)}");
                else Flush();
            }
            Flush();
            void Flush() { if (run.Count >= 2) paths.Add(string.Join(' ', run)); run.Clear(); }
        }
        return paths.ToArray();
    }

    private static int RenderedPointCount(IRenderedComponent<SatellitePassPlanner> cut, string selector)
        => cut.FindAll(selector).Sum(path => path.GetAttribute("points")!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);

    private sealed record StylePack(string Css, string LayoutScope, object[] Inputs);

    private static StylePack ReadStyles(string root)
    {
        var host = Path.Combine(root, "src/HVO.SkyMonitor.CameraAgent");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var compiled = Path.Combine(host, "obj", configuration, "net10.0", "scopedcss", "bundle", "HVO.SkyMonitor.CameraAgent.styles.css");
        Assert.IsTrue(File.Exists(compiled), $"Build the host in {configuration} before exporting compiled scoped styles: {compiled}");
        var css = File.ReadAllText(compiled);
        Assert.Contains(".satellite-planner[b-", css, StringComparison.Ordinal);
        Assert.Contains(".predicted-path[b-", css, StringComparison.Ordinal);
        var layoutScope = Regex.Match(css, @"\.operations-layout\[(b-[a-z0-9]+)\]", RegexOptions.CultureInvariant).Groups[1].Value;
        Assert.IsFalse(string.IsNullOrEmpty(layoutScope));
        var paths = new[] { Path.Combine(host, "wwwroot/vendor/bootstrap/bootstrap.min.css"),
            Path.Combine(host, "wwwroot/css/themes/hvo-dark.css"), compiled };
        return new(string.Join('\n', paths.Select(File.ReadAllText)), layoutScope, paths.Select(path => FileFact(root, path)).ToArray());
    }

    private static void Freeze(string output, string stem, string markup, StylePack styles, string title)
    {
        foreach (var className in new[] { "satellite-planner", "satellite-arc" })
        {
            if (!markup.Contains($"class=\"{className}", StringComparison.Ordinal) && className == "satellite-arc") continue;
            var scope = Regex.Match(styles.Css, @"\." + className + @"\[(b-[a-z0-9]+)\]", RegexOptions.CultureInvariant).Groups[1].Value;
            Assert.Contains(scope, markup, StringComparison.Ordinal, "Rendered scope attributes must match the compiled stylesheet.");
        }
        File.WriteAllText(Path.Combine(output, stem + ".markup.html"), markup, Encoding.UTF8);
        var html = $$"""
            <!DOCTYPE html>
            <html lang="en" data-theme="hvo-dark"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <meta name="color-scheme" content="dark"><title>{{WebUtility.HtmlEncode(title)}}</title><style>{{styles.Css}}</style>
            <style>body{margin:0}.evidence-container{grid-column:1/-1;width:min(100%,1400px);margin:auto;padding:1.5rem}.evidence-caption{padding:1rem;border:1px solid #344258;border-radius:.5rem;margin-bottom:1rem}.evidence-caption h1{font-size:1.2rem}.evidence-caption p{margin:.5rem 0}</style></head>
            <body data-theme="hvo-dark"><main class="operations-layout" {{styles.LayoutScope}}><div class="evidence-container">
            <header class="evidence-caption"><h1>{{WebUtility.HtmlEncode(title)}}</h1>
            <p>Frozen component evidence • real retained orbital/time inputs • configured virtual rig, unmeasured.</p>
            <p>Controls below are inert. Search, selection, authorization and toggles were executed in bUnit before this HTML was frozen. This page has no live backend.</p></header>
            <div inert>{{markup}}</div></div></main></body></html>
            """;
        File.WriteAllText(Path.Combine(output, stem + ".html"), html, Encoding.UTF8);
    }

    private static object FileFact(string root, string path) => new
    {
        path = Path.GetRelativePath(root, path),
        bytes = new FileInfo(path).Length,
        sha256 = PlanningTestInputs.Hash(File.ReadAllBytes(path))
    };

    private static void WriteJson<T>(string output, string name, T value)
        => File.WriteAllBytes(Path.Combine(output, name), OrbitalContract.SerializeCanonical(value));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Run from a repository checkout containing retained fixture inputs.");
    }
}
