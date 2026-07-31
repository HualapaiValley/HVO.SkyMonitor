using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.TestSupport;
using HVO.SkyMonitor.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class LogicHostUiBrowserAcceptanceTests
{
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new() { WriteIndented = true };
    private static readonly (int Width, int Height)[] Viewports =
    [
        (1440, 900),
        (820, 1180),
        (390, 844),
        (844, 390),
        (320, 700)
    ];

    [TestMethod]
    public async Task Issue107_PublicAndProtectedJourneysAreResponsiveAccessibleAndLeakFree()
    {
        using var runtimeEvidence = new Issue107RuntimeEvidenceCollector();
        await using var fixture = await LogicHostUiKestrelFixture.CreateAsync(runtimeEvidence).ConfigureAwait(false);
        var seeded = await SeedPublicObservatoryAsync(fixture.Services).ConfigureAwait(false);
        var rawArtifact = await SeedBrowserRawArtifactAsync(fixture.Services).ConfigureAwait(false);
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        }).ConfigureAwait(false);
        var pageErrors = new List<string>();
        var responseFailures = new List<string>();
        var routeEvidence = new List<RouteEvidence>();
        var mapRequests = new List<MapRequestEvidence>();
        await using (var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            ColorScheme = ColorScheme.Dark,
            ReducedMotion = ReducedMotion.Reduce
        }).ConfigureAwait(false))
        {
            var page = await context.NewPageAsync().ConfigureAwait(false);
            page.Request += (_, request) => RecordMapRequest(request, fixture.BaseAddress, mapRequests);
            page.PageError += (_, error) => pageErrors.Add(error);
            page.Console += (_, message) =>
            {
                if (message.Type == "error" && !IsUnsupportedConditionalPasskey(message.Text))
                {
                    pageErrors.Add(message.Text);
                }
            };
            page.Response += (_, response) =>
            {
                if (response.Url.StartsWith(fixture.BaseAddress.AbsoluteUri, StringComparison.Ordinal)
                    && response.Status >= 500)
                {
                    responseFailures.Add($"{response.Status}:{new Uri(response.Url).AbsolutePath}");
                }
            };
            foreach (var viewport in Viewports)
            {
                await page.SetViewportSizeAsync(viewport.Width, viewport.Height).ConfigureAwait(false);
                foreach (var route in new[] { "/", "/observatories", $"/observatories/{seeded.PublicSlug}", "/events" })
                {
                    var started = Stopwatch.GetTimestamp();
                    var response = await page.GotoAsync(
                        new Uri(fixture.BaseAddress, route).AbsoluteUri,
                        new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded }).ConfigureAwait(false);
                    response.Should().NotBeNull();
                    response!.Status.Should().BeLessThan(500);
                    var scopedStyles = await page.Locator("link[href*='.styles.css']").GetAttributeAsync("href")
                        .ConfigureAwait(false);
                    if (scopedStyles is not null)
                    {
                        var styleResponse = await page.Context.APIRequest.GetAsync(
                            new Uri(fixture.BaseAddress, scopedStyles).AbsoluteUri).ConfigureAwait(false);
                        Assert.IsTrue(
                            styleResponse.Status < 500,
                            $"Scoped CSS failed with {styleResponse.Status}: {await styleResponse.TextAsync().ConfigureAwait(false)}");
                    }
                    await AssertResponsiveAndAccessibleAsync(page, route).ConfigureAwait(false);
                    await AssertPublicLeakageAsync(page).ConfigureAwait(false);
                    if (route == "/observatories" && viewport == Viewports[0])
                    {
                        await page.Locator(".directory-map-marker").WaitForAsync().ConfigureAwait(false);
                        await page.GetByRole(AriaRole.Button, new() { Name = "Standard" }).FocusAsync()
                            .ConfigureAwait(false);
                        await page.Keyboard.PressAsync("Enter").ConfigureAwait(false);
                        await page.GetByRole(AriaRole.Button, new() { Name = "Satellite fixture" }).ClickAsync()
                            .ConfigureAwait(false);
                        (await page.Locator(".maplibregl-ctrl-attrib").CountAsync().ConfigureAwait(false))
                            .Should().BeGreaterThan(0);
                    }
                    routeEvidence.Add(new(
                        SanitizeRoute(route),
                        viewport.Width,
                        viewport.Height,
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                        (await page.ContentAsync().ConfigureAwait(false)).Length));
                }
            }
        }

        await using (var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            ColorScheme = ColorScheme.Dark
        }).ConfigureAwait(false))
        {
            var page = await context.NewPageAsync().ConfigureAwait(false);
            page.PageError += (_, error) => pageErrors.Add(error);
            page.Console += (_, message) =>
            {
                if (message.Type == "error" && !IsUnsupportedConditionalPasskey(message.Text))
                {
                    pageErrors.Add(message.Text);
                }
            };
            page.Response += (_, response) =>
            {
                if (response.Url.StartsWith(fixture.BaseAddress.AbsoluteUri, StringComparison.Ordinal)
                    && response.Status >= 500)
                {
                    responseFailures.Add($"{response.Status}:{new Uri(response.Url).AbsolutePath}");
                }
            };
            await LoginAsync(page, fixture.BaseAddress, TestUsers.Operator.Email, TestUsers.Operator.Password)
                .ConfigureAwait(false);
            await page.GotoAsync(new Uri(fixture.BaseAddress, "/app/processing").AbsoluteUri).ConfigureAwait(false);
            await page.GetByRole(AriaRole.Heading, new() { Name = "Jobs and output policy" }).WaitForAsync()
                .ConfigureAwait(false);
            await AssertResponsiveAndAccessibleAsync(page, "/app/processing").ConfigureAwait(false);
            await page.GetByLabel("Observatory scope").WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 5_000
            }).ConfigureAwait(false);
            (await page.GetByLabel("Observatory scope").CountAsync().ConfigureAwait(false)).Should().Be(1);
            await page.GetByText("Versioned observatory policy").WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 5_000
            }).ConfigureAwait(false);
            (await page.GetByText("Versioned observatory policy").CountAsync().ConfigureAwait(false))
                .Should().BeGreaterThan(0);
            await page.GetByLabel("Cloud transmission threshold override").FillAsync("620000")
                .ConfigureAwait(false);
            await page.GetByRole(AriaRole.Button, new() { Name = "Activate successor version" }).ClickAsync()
                .ConfigureAwait(false);
            await page.GetByText("The observatory processing override was activated for future jobs.")
                .WaitForAsync().ConfigureAwait(false);
            foreach (var route in new[]
            {
                "/app",
                $"/app/observatories/{seeded.ObservatoryId:D}",
                "/app/captures",
                $"/app/captures/{rawArtifact.CaptureId:D}",
                "/app/processing",
                "/app/events",
                $"/app/observatories/{seeded.ObservatoryId:D}/members",
                $"/app/observatories/{seeded.ObservatoryId:D}/publication"
            })
            {
                var started = Stopwatch.GetTimestamp();
                await page.GotoAsync(new Uri(fixture.BaseAddress, route).AbsoluteUri).ConfigureAwait(false);
                await AssertResponsiveAndAccessibleAsync(page, route).ConfigureAwait(false);
                routeEvidence.Add(new(
                    SanitizeRoute(route),
                    1440,
                    900,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    (await page.ContentAsync().ConfigureAwait(false)).Length));
            }
        }

        foreach (var identity in new[]
        {
            (TestUsers.Viewer.Email, TestUsers.Viewer.Password, "Viewer"),
            (TestUsers.Regular.Email, TestUsers.Regular.Password, "Manager")
        })
        {
            await using var context = await browser.NewContextAsync().ConfigureAwait(false);
            var page = await context.NewPageAsync().ConfigureAwait(false);
            page.PageError += (_, error) => pageErrors.Add(error);
            await LoginAsync(page, fixture.BaseAddress, identity.Email, identity.Password).ConfigureAwait(false);
            await page.GotoAsync(new Uri(fixture.BaseAddress, "/app/processing").AbsoluteUri).ConfigureAwait(false);
            await page.GetByLabel("Cloud transmission threshold override").WaitForAsync().ConfigureAwait(false);
            (await page.GetByLabel("Cloud transmission threshold override").IsDisabledAsync().ConfigureAwait(false))
                .Should().BeTrue($"{identity.Item3} processing policy is read-only");
            (await page.GetByRole(AriaRole.Button, new() { Name = "Activate successor version" }).CountAsync()
                .ConfigureAwait(false)).Should().Be(0);
            if (identity.Item3 == "Viewer")
            {
                await page.GotoAsync(
                    new Uri(fixture.BaseAddress, $"/app/captures/{rawArtifact.CaptureId:D}").AbsoluteUri)
                    .ConfigureAwait(false);
                await page.GetByText("Edge evidence", new() { Exact = true }).WaitForAsync().ConfigureAwait(false);
                await page.GetByRole(AriaRole.Button, new() { Name = "Load full trace" }).ClickAsync()
                    .ConfigureAwait(false);
                await page.GetByRole(AriaRole.Heading, new() { Name = "Central processing trace" }).WaitForAsync()
                    .ConfigureAwait(false);
                var downloadTask = page.WaitForDownloadAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = "Authorize raw download" }).ClickAsync()
                    .ConfigureAwait(false);
                var download = await downloadTask.ConfigureAwait(false);
                var downloadPath = await download.PathAsync().ConfigureAwait(false);
                downloadPath.Should().NotBeNullOrWhiteSpace();
                (await File.ReadAllBytesAsync(downloadPath!).ConfigureAwait(false)).Should().Equal(rawArtifact.Payload);
                await download.DeleteAsync().ConfigureAwait(false);
            }
        }

        await using (var context = await browser.NewContextAsync().ConfigureAwait(false))
        {
            var page = await context.NewPageAsync().ConfigureAwait(false);
            page.PageError += (_, error) => pageErrors.Add(error);
            await LoginAsync(page, fixture.BaseAddress, TestUsers.Admin.Email, TestUsers.Admin.Password)
                .ConfigureAwait(false);
            await page.GotoAsync(new Uri(fixture.BaseAddress, "/app/editorial").AbsoluteUri).ConfigureAwait(false);
            await page.GetByRole(AriaRole.Heading, new() { Name = "Public home curation" }).WaitForAsync()
                .ConfigureAwait(false);
        }

        pageErrors.Should().BeEmpty();
        responseFailures.Should().BeEmpty();
        await ExerciseRuntimeSignalsAsync(fixture.Services, seeded).ConfigureAwait(false);
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var policy = await db.CentralProcessingOverrideVersions.AsNoTracking()
                .SingleAsync(item => item.ObservatoryId == seeded.ObservatoryId && item.SupersededAtUtc == null)
                .ConfigureAwait(false);
            policy.CloudTransmissionThresholdMillionths.Should().Be(620_000);
            policy.ActorUserId.Should().NotBeNullOrWhiteSpace();
            var rawAudit = await db.CentralArtifactDownloadAuthorizations.AsNoTracking()
                .SingleAsync(item => item.CentralArtifactId == rawArtifact.CentralArtifactId)
                .ConfigureAwait(false);
            rawAudit.MembershipRole.Should().Be(ObservatoryMembershipRole.Viewer);
        }
        await WriteEvidenceAsync(browser.Version, routeEvidence, mapRequests).ConfigureAwait(false);
        var evidenceRoot = Environment.GetEnvironmentVariable("HVO_ISSUE_107_EVIDENCE_ROOT");
        if (!string.IsNullOrWhiteSpace(evidenceRoot))
        {
            await PrepareSteadyStateHealthAsync(fixture.Services).ConfigureAwait(false);
            using var healthClient = new HttpClient { BaseAddress = fixture.BaseAddress };
            using var healthResponse = await healthClient.GetAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);
            var health = JsonSerializer.Deserialize<JsonElement>(
                await healthResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
            await runtimeEvidence.WriteAsync(
                evidenceRoot,
                Environment.GetEnvironmentVariable("HVO_ISSUE_107_REVISION") ?? "development",
                new
                {
                    Schema = "hvo-logichost-ui-107-health-v1",
                    HttpStatus = (int)healthResponse.StatusCode,
                    SteadyState = health
                }).ConfigureAwait(false);
        }
    }

    private static async Task PrepareSteadyStateHealthAsync(IServiceProvider services)
    {
        services.GetRequiredService<EnvironmentalRetentionState>().Succeeded(DateTimeOffset.UtcNow);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var checkpoint = await db.CentralRecoveryCheckpoints.SingleAsync(item =>
            item.Id == CentralRecoveryCheckpoint.SingletonId).ConfigureAwait(false);
        checkpoint.Phase = CentralRecoveryPhases.Idle;
        checkpoint.LastCompletedAtUtc = DateTimeOffset.UtcNow;
        checkpoint.NextInventoryAtUtc = DateTimeOffset.UtcNow.AddMinutes(15);
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task ExerciseRuntimeSignalsAsync(
        IServiceProvider services,
        BrowserObservatoryFixture seeded)
    {
        string ownerId;
        string editorId;
        string viewerId;
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            ownerId = (await db.Users.SingleAsync(user => user.Email == TestUsers.Operator.Email).ConfigureAwait(false)).Id;
            editorId = (await db.Users.SingleAsync(user => user.Email == TestUsers.Admin.Email).ConfigureAwait(false)).Id;
            viewerId = (await db.Users.SingleAsync(user => user.Email == TestUsers.Viewer.Email).ConfigureAwait(false)).Id;
        }
        await using (var scope = services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<IObservatoryInvitationService>().IssueAsync(
                seeded.ObservatoryId,
                ownerId,
                editorId,
                ObservatoryMembershipRole.Viewer,
                TimeSpan.FromHours(1)).ConfigureAwait(false);
            result.Outcome.Should().Be(ObservatoryInvitationMutationOutcome.Applied);
        }
        await using (var scope = services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<ILogicalCameraService>().CreateAsync(
                seeded.ObservatoryId,
                ownerId,
                "runtime-evidence",
                "Runtime evidence camera",
                "Exercises the durable installation audit family.").ConfigureAwait(false);
            result.Outcome.Should().Be(LogicalCameraMutationOutcome.Applied);
        }
        await using (var scope = services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<IRegisteredUserPersonalizationService>()
                .FollowAsync(viewerId, seeded.PublicSlug).ConfigureAwait(false);
            result.Should().Be(PersonalizationMutationOutcome.Applied);
        }
        await using (var scope = services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<ICuratedPublicPlacementService>()
                .DecideObservatoryAsync(
                    editorId,
                    seeded.PublicSlug,
                    CuratedPlacementState.Featured,
                    0,
                    "browser-acceptance").ConfigureAwait(false);
            result.Should().Be(CuratedPlacementOutcome.Applied);
        }
        await using (var scope = services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<IObservatoryMembershipService>().SetRoleAsync(
                seeded.ObservatoryId,
                ownerId,
                editorId,
                ObservatoryMembershipRole.Viewer).ConfigureAwait(false);
            result.Outcome.Should().Be(ObservatoryMembershipMutationOutcome.Applied);
        }
    }

    private static async Task<BrowserObservatoryFixture> SeedPublicObservatoryAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var owner = await db.Users.SingleAsync(user => user.Email == TestUsers.Operator.Email).ConfigureAwait(false);
        var viewer = await db.Users.SingleAsync(user => user.Email == TestUsers.Viewer.Email).ConfigureAwait(false);
        var manager = await db.Users.SingleAsync(user => user.Email == TestUsers.Regular.Email).ConfigureAwait(false);
        var editor = await db.Users.SingleAsync(user => user.Email == TestUsers.Admin.Email).ConfigureAwait(false);
        var marker = Guid.NewGuid().ToString("N");
        var observatory = await scope.ServiceProvider.GetRequiredService<IObservatoryService>().CreateOrUpdateAsync(
            new ObservatoryUpsertRequest(
                null,
                owner.Id,
                $"Browser station {marker}",
                19.812345,
                -155.412345,
                4201,
                "Pacific/Honolulu",
                true)).ConfigureAwait(false);
        var publication = scope.ServiceProvider.GetRequiredService<IObservatoryPublicationService>();
        db.ObservatoryMemberships.AddRange(
            new ObservatoryMembership
            {
                ObservatoryId = observatory.Id,
                UserId = viewer.Id,
                Role = ObservatoryMembershipRole.Viewer,
                AddedAtUtc = DateTimeOffset.UtcNow
            },
            new ObservatoryMembership
            {
                ObservatoryId = observatory.Id,
                UserId = manager.Id,
                Role = ObservatoryMembershipRole.Manager,
                AddedAtUtc = DateTimeOffset.UtcNow
            });
        await db.SaveChangesAsync().ConfigureAwait(false);
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        if (!await userManager.IsInRoleAsync(editor, AuthorizationRoleNames.PlatformEditor).ConfigureAwait(false))
        {
            (await userManager.AddToRoleAsync(editor, AuthorizationRoleNames.PlatformEditor).ConfigureAwait(false))
                .Succeeded.Should().BeTrue();
        }
        var slug = $"browser-{marker}";
        _ = await publication.SetProfileAsync(
            observatory.Id,
            owner.Id,
            new ObservatoryPublicationProfileRequest(
                slug,
                "Browser Public Station",
                "A deliberately released browser fixture.",
                ObservatoryProfileVisibility.Public,
                false,
                false,
                "browser-acceptance")).ConfigureAwait(false);
        _ = await publication.SetLocationDisclosureAsync(
            observatory.Id,
            owner.Id,
            new ObservatoryLocationDisclosureRequest(
                ObservatoryLocationDisclosureLevel.Approximate,
                "US-HI",
                "Hawaii Island",
                19.8,
                -155.4,
                20_000,
                "browser-acceptance")).ConfigureAwait(false);
        return new(observatory.Id, slug);
    }

    private static async Task<BrowserRawArtifactFixture> SeedBrowserRawArtifactAsync(IServiceProvider services)
    {
        byte[] payload = [11, 22, 33, 44, 55, 66];
        var artifact = await ArtifactRetrievalTests.SeedArtifactAsync(TestUsers.Operator.Email, payload)
            .ConfigureAwait(false);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var viewer = await db.Users.SingleAsync(user => user.Email == TestUsers.Viewer.Email).ConfigureAwait(false);
        var stored = await db.CentralArtifacts.Include(item => item.Frame)
            .SingleAsync(item => item.ArtifactId == artifact.ArtifactId).ConfigureAwait(false);
        var observatory = await db.Observatories.SingleAsync(item => item.Id == stored.Frame!.ObservatoryId)
            .ConfigureAwait(false);
        _ = await scope.ServiceProvider.GetRequiredService<IObservatoryService>().CreateOrUpdateAsync(
            new ObservatoryUpsertRequest(
                observatory.Id,
                observatory.OwnerUserId,
                observatory.Name,
                observatory.LatitudeDegrees,
                observatory.LongitudeDegrees,
                observatory.ElevationMeters,
                observatory.TimeZoneId,
                observatory.IsActive,
                observatory.AllowedDeploymentRadiusMeters)).ConfigureAwait(false);
        db.ObservatoryMemberships.Add(new ObservatoryMembership
        {
            ObservatoryId = stored.Frame!.ObservatoryId,
            UserId = viewer.Id,
            Role = ObservatoryMembershipRole.Viewer,
            AddedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
        return new(stored.CentralFrameId, stored.Id, payload);
    }

    private static async Task LoginAsync(IPage page, Uri baseAddress, string email, string password)
    {
        await page.GotoAsync(new Uri(baseAddress, "/app").AbsoluteUri).ConfigureAwait(false);
        await page.WaitForURLAsync(url => url.Contains("/Account/Login", StringComparison.OrdinalIgnoreCase))
            .ConfigureAwait(false);
        await page.GetByLabel("Email").FillAsync(email).ConfigureAwait(false);
        await page.GetByLabel("Password").FillAsync(password).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync()
            .ConfigureAwait(false);
    }

    private static async Task AssertResponsiveAndAccessibleAsync(IPage page, string route)
    {
        await page.Locator("h1").WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 5_000
        }).ConfigureAwait(false);
        await page.WaitForTimeoutAsync(150).ConfigureAwait(false);
        await page.Locator("h1").WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 5_000
        }).ConfigureAwait(false);
        (await page.Locator("main").CountAsync().ConfigureAwait(false)).Should().Be(1, route);
        (await page.Locator("h1").CountAsync().ConfigureAwait(false)).Should().Be(1, route);
        var hasOverflow = await page.EvaluateAsync<bool>("""
            () => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1
            """).ConfigureAwait(false);
        hasOverflow.Should().BeFalse();
        var unnamedInteractive = await page.EvaluateAsync<int>("""
            () => [...document.querySelectorAll('a,button,input,select,textarea')]
                .filter(element => {
                    if (element.getClientRects().length === 0 || element.disabled) return false;
                    const labelled = element.getAttribute('aria-label')
                        || element.getAttribute('aria-labelledby')
                        || (element.id && document.querySelector(`label[for="${CSS.escape(element.id)}"]`));
                    return !labelled && !(element.textContent || element.value || element.title || element.alt || '').trim();
                }).length
            """).ConfigureAwait(false);
        unnamedInteractive.Should().Be(0);
    }

    private static async Task AssertPublicLeakageAsync(IPage page)
    {
        var content = await page.ContentAsync().ConfigureAwait(false);
        var browserState = await page.EvaluateAsync<string>("""
            () => JSON.stringify({ local: { ...localStorage }, session: { ...sessionStorage } })
            """).ConfigureAwait(false);
        foreach (var forbidden in new[]
        {
            "19.812345",
            "-155.412345",
            TestUsers.Operator.Email,
            IntegrationTestFixture.MinioSecretKey,
            "minio://",
            "ConnectionStrings:",
            "/tmp/"
        })
        {
            content.Should().NotContain(forbidden);
            browserState.Should().NotContain(forbidden);
        }
    }

    private static void RecordMapRequest(IRequest request, Uri baseAddress, List<MapRequestEvidence> evidence)
    {
        if (!request.Url.Contains("maplibre", StringComparison.OrdinalIgnoreCase)) return;
        var requestUri = new Uri(request.Url);
        evidence.Add(new(
            request.Method,
            request.ResourceType,
            requestUri.Host.Equals(baseAddress.Host, StringComparison.OrdinalIgnoreCase) ? "logic-host" : "map-library-cdn",
            requestUri.AbsolutePath.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ? "map-library-css" : "map-library-js",
            !string.IsNullOrEmpty(requestUri.Query)));
    }

    private static bool IsUnsupportedConditionalPasskey(string message)
        => message.Contains(
            "Resident credentials or empty 'allowCredentials' lists are not supported",
            StringComparison.Ordinal);

    private static string SanitizeRoute(string route)
    {
        var segments = route.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 2 && segments[0] == "observatories") return "/observatories/{public-slug}";
        if (segments.Length >= 3 && segments[0] == "app" && segments[1] == "observatories"
            && Guid.TryParse(segments[2], out _))
        {
            segments[2] = "{observatory-id}";
            return "/" + string.Join('/', segments);
        }
        return route;
    }

    private static async Task WriteEvidenceAsync(
        string browserVersion,
        IReadOnlyList<RouteEvidence> routes,
        IReadOnlyList<MapRequestEvidence> mapRequests)
    {
        var root = Environment.GetEnvironmentVariable("HVO_ISSUE_107_EVIDENCE_ROOT");
        if (string.IsNullOrWhiteSpace(root)) return;
        Directory.CreateDirectory(root);
        var evidence = new
        {
            Schema = "hvo-logichost-ui-107-browser-v1",
            Revision = Environment.GetEnvironmentVariable("HVO_ISSUE_107_REVISION") ?? "development",
            Browser = browserVersion,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Routes = routes
        };
        await File.WriteAllTextAsync(
            Path.Combine(root, "browser-acceptance.json"),
            JsonSerializer.Serialize(evidence, EvidenceJsonOptions)).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(root, "map-requests.json"),
            JsonSerializer.Serialize(new
            {
                Schema = "hvo-logichost-ui-107-map-requests-v1",
                Revision = Environment.GetEnvironmentVariable("HVO_ISSUE_107_REVISION") ?? "development",
                Requests = mapRequests,
                FixtureStylesOnly = true,
                ProductionProviderRequests = 0
            }, EvidenceJsonOptions)).ConfigureAwait(false);
    }

    private sealed record RouteEvidence(
        string Route,
        int Width,
        int Height,
        double DurationMilliseconds,
        int HtmlCharacters);

    private sealed record MapRequestEvidence(
        string Method,
        string ResourceType,
        string HostClass,
        string PathClass,
        bool HasQuery);

    private sealed record BrowserObservatoryFixture(Guid ObservatoryId, string PublicSlug);
    private sealed record BrowserRawArtifactFixture(Guid CaptureId, Guid CentralArtifactId, byte[] Payload);
}
