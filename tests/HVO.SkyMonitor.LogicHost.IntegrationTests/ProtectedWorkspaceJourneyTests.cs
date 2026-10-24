using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.IntegrationTests.Infrastructure;
using HVO.SkyMonitor.LogicHost.Components.Shared;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class ProtectedWorkspaceJourneyTests
{
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new() { WriteIndented = true };
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task OneCameraJourneyPreservesExactImagesImmutableImportsAndPublicationAuthority()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Repository root not found.");
        var source = await EvidenceSourceIdentity.CaptureAsync(root.FullName,
            typeof(OperationsScopeShell), typeof(CentralOperationsWorkspaceService), typeof(ProtectedWorkspaceJourneyTests));
        var directory = Path.Combine(Environment.GetEnvironmentVariable("HVO_ISSUE539_EVIDENCE") ?? "TestResults/issue-539",
            source.OutputDirectoryName, source.RunId);
        Directory.CreateDirectory(directory);
        var data = await ExecutionEvidenceIntegrationTests.SeedInspectorAsync(3);
        var foreign = await ExecutionEvidenceIntegrationTests.SeedInspectorAsync(3);
        await using var fixture = await LogicHostUiKestrelFixture.CreateAsync();
        CentralFrame capture;
        CentralArtifact preview;
        CentralArtifact sibling;
        ImportedUnitSnapshot[] importedBefore;
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var installation = await db.LogicalCameraInstallations.Include(i => i.Registration).SingleAsync(i => i.Id == data.Installation);
            var registration = installation.Registration ?? throw new InvalidOperationException("Seeded installation has no registration.");
            capture = new CentralFrame
            {
                RegistrationId = installation.RegistrationId,
                LogicalCameraInstallationId = data.Installation,
                DevicePublicId = registration.DevicePublicId ?? throw new InvalidOperationException("Seeded registration has no public identity."),
                ObservatoryId = data.Observatory,
                AgentId = registration.DeviceId,
                FrameId = Guid.NewGuid(),
                CapturedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
                FirstReceivedAtUtc = DateTimeOffset.UtcNow,
                CaptureSequence = 539
            };
            db.Add(capture);
            await db.SaveChangesAsync();
            preview = await LogicalCameraArchiveIntegrationTests.AddPreviewAsync(scope.ServiceProvider, capture, true);
            sibling = await LogicalCameraArchiveIntegrationTests.AddPreviewAsync(scope.ServiceProvider, capture, false);
            sibling.Variant = "metadata-only-sibling";
            sibling.ObjectState = CentralArtifactObjectState.Expired;
            await db.SaveChangesAsync();
            importedBefore = await ImmutableUnitsAsync(db, data.Binding);
        }
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var diagnostics = new PlaywrightDiagnostics(browser, TestContext);
        await using var context = await diagnostics.NewContextAsync(new() { HasTouch = true, ViewportSize = new() { Width = 1440, Height = 900 } });
        var page = await context.NewPageAsync();
        var cameraPath = $"/app/network/observatories/{data.Observatory:D}/cameras/{data.Camera:D}";
        // Start on the overview so the first tab activation is a navigation, rather than a same-URL click.
        await page.GotoAsync(new Uri(fixture.BaseAddress, cameraPath).AbsoluteUri);
        await page.GetByLabel("Email").FillAsync(TestUsers.Operator.Email);
        await page.GetByLabel("Password").FillAsync(TestUsers.Operator.Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync();
        string? exactImage = null;
        var screens = new List<object>();
        foreach (var (width, height) in new[] { (1440, 900), (390, 844), (844, 390), (320, 700) })
        {
            await page.SetViewportSizeAsync(width, height);
            await TabAsync(page, "Current Sky", ".current-sky", width == 390);
            await Assertions.Expect(page.Locator(".current-sky img")).ToBeVisibleAsync();
            var image = await page.Locator(".current-sky img").GetAttributeAsync("src");
            image.Should().Contain(preview.ArtifactId.ToString("D"));
            exactImage ??= image;
            image.Should().Be(exactImage);
            var currentTree = await CheckLayoutAsync(page, Path.Combine(directory, $"current-{width}x{height}.png"));
            await TabAsync(page, "Camera archive", ".camera-archive", width == 390);
            await Assertions.Expect(page.Locator(".archive-items li")).ToHaveCountAsync(1);
            var exact = page.Locator($"#archive-capture-{capture.Id:D}");
            await exact.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await ReadyAsync(page, ".camera-archive");
            await Assertions.Expect(page.Locator(".camera-archive img")).ToHaveAttributeAsync("src", image!);
            await Assertions.Expect(page.Locator(".archive-exact")).ToContainTextAsync(capture.Id.ToString("D"));
            var archiveTree = await CheckLayoutAsync(page, Path.Combine(directory, $"archive-{width}x{height}.png"));
            await TabAsync(page, "Imported executions", ".imported-inspector", width == 390);
            await page.GetByRole(AriaRole.Link, new() { Name = "Installation " + data.Installation, Exact = true }).ClickAsync();
            await ReadyAsync(page, ".imported-inspector");
            await page.GetByRole(AriaRole.Link, new() { Name = "Binding " + data.Binding, Exact = true }).ClickAsync();
            await ReadyAsync(page, ".imported-inspector");
            await page.Locator($"#imported-unit-{data.ExecutionUnit}").ClickAsync();
            await ReadyAsync(page, ".imported-inspector");
            await Assertions.Expect(page.Locator(".imported-exact")).ToContainTextAsync(data.ExecutionHash);
            await Assertions.Expect(page.Locator(".imported-inspector")).ToContainTextAsync("CameraAgent evidence is read-only");
            // This execution references other, unreceived bytes. The previous tab's image is never a substitute.
            await Assertions.Expect(page.Locator(".imported-inspector img")).ToHaveCountAsync(0);
            await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Open exact capture viewer", Exact = true })).ToHaveCountAsync(0);
            var importedTree = await CheckLayoutAsync(page, Path.Combine(directory, $"imported-{width}x{height}.png"));
            await TabAsync(page, "Central operations", ".central-workspace", width == 390);
            await Assertions.Expect(page.Locator(".central-workspace")).ToContainTextAsync("No central executions on this page.");
            await Assertions.Expect(page.Locator(".central-workspace")).ToContainTextAsync("CameraAgent received executions remain immutable");
            var centralTree = await CheckLayoutAsync(page, Path.Combine(directory, $"central-{width}x{height}.png"));
            screens.Add(new { width, height, currentTree, archiveTree, importedTree, centralTree });
        }
        var publicationUrl = new Uri(fixture.BaseAddress, cameraPath + $"/central?view=artifacts&capture={capture.Id:D}").AbsoluteUri;
        await page.GotoAsync(publicationUrl);
        await ReadyAsync(page, ".central-workspace");
        await Assertions.Expect(page.Locator(".central-artifacts li")).ToHaveCountAsync(2);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Release artifact " + sibling.ArtifactId, Exact = true })).ToHaveCountAsync(0);
        await page.GetByRole(AriaRole.Button, new() { Name = "Release artifact " + preview.ArtifactId, Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Withdraw artifact " + preview.ArtifactId, Exact = true })).ToBeVisibleAsync();
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var decisions = await db.PublicRecordPublicationDecisions.AsNoTracking().Where(d => d.AuthorityObservatoryId == data.Observatory).ToArrayAsync();
            decisions.Should().ContainSingle().Which.CentralArtifactId.Should().Be(preview.Id);
            decisions[0].ActorUserId.Should().Be(data.User);
            decisions[0].State.Should().Be(PublicationDecisionState.Released);
            // Race the rendered Owner control with a role downgrade before the click reaches the server.
            await db.ObservatoryMemberships.Where(m => m.ObservatoryId == data.Observatory && m.UserId == data.User)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, ObservatoryMembershipRole.Viewer));
        }
        await page.GetByRole(AriaRole.Button, new() { Name = "Withdraw artifact " + preview.ArtifactId, Exact = true }).ClickAsync();
        await ReadyAsync(page, ".central-workspace");
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Withdraw artifact " + preview.ArtifactId, Exact = true })).ToHaveCountAsync(0);
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.PublicRecordPublicationDecisions.CountAsync(d => d.AuthorityObservatoryId == data.Observatory)).Should().Be(1);
            (await ImmutableUnitsAsync(db, data.Binding)).Should().Equal(importedBefore);
            await db.ObservatoryMemberships.Where(m => m.ObservatoryId == data.Observatory && m.UserId == data.User)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, ObservatoryMembershipRole.Owner));
        }
        await page.GetByRole(AriaRole.Button, new() { Name = "Refresh central operations", Exact = true }).ClickAsync();
        await ReadyAsync(page, ".central-workspace");
        await page.GetByRole(AriaRole.Button, new() { Name = "Withdraw artifact " + preview.ArtifactId, Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Release artifact " + preview.ArtifactId, Exact = true })).ToBeVisibleAsync();
        await page.EvaluateAsync("window.dispatchEvent(new Event('offline'))");
        await Assertions.Expect(page.Locator(".central-protected")).ToHaveCountAsync(0);
        await page.EvaluateAsync("window.dispatchEvent(new Event('online'))");
        await ReadyAsync(page, ".central-workspace");
        await Assertions.Expect(page.Locator(".central-artifacts li")).ToHaveCountAsync(2);
        await Assertions.Expect(page.Locator(".central-protected")).ToContainTextAsync(capture.Id.ToString("D"));
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Release artifact " + preview.ArtifactId, Exact = true })).ToBeVisibleAsync();
        await TabAsync(page, "Current Sky", ".current-sky", false);
        await Assertions.Expect(page.Locator(".current-sky img")).ToHaveAttributeAsync("src", exactImage!);
        // The actor belongs to both sites. Mixing their route identities must still fail closed in every section.
        foreach (var section in new[] { "current", "archive", "imported-executions", "central" })
        {
            await page.GotoAsync(new Uri(fixture.BaseAddress,
                $"/app/network/observatories/{data.Observatory:D}/cameras/{foreign.Camera:D}/{section}").AbsoluteUri);
            await page.GetByText("Scope not found or access was removed.", new() { Exact = true }).WaitForAsync();
            await Assertions.Expect(page.Locator(".current-sky,.camera-archive,.imported-protected,.central-protected")).ToHaveCountAsync(0);
            (await page.ContentAsync()).Should().NotContain(data.ExecutionHash).And.NotContain(preview.ArtifactId.ToString("D"));
        }
        ImportedUnitSnapshot[] importedAfter;
        object[] publicationDecisions;
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            importedAfter = await ImmutableUnitsAsync(db, data.Binding);
            importedAfter.Should().Equal(importedBefore);
            var decisions = await db.PublicRecordPublicationDecisions.AsNoTracking()
                .Where(d => d.AuthorityObservatoryId == data.Observatory).OrderBy(d => d.OccurredAtUtc).ToArrayAsync();
            decisions.Should().HaveCount(2).And.OnlyContain(d => d.CentralArtifactId == preview.Id && d.ActorUserId == data.User);
            decisions.Select(d => d.State).Should().Equal(PublicationDecisionState.Released, PublicationDecisionState.Withdrawn);
            decisions[1].SupersedesDecisionId.Should().Be(decisions[0].Id);
            publicationDecisions = decisions.Select(d => (object)new { d.Id, d.CentralArtifactId, d.ActorUserId, d.State, d.OccurredAtUtc, d.SupersedesDecisionId }).ToArray();
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "journey.json"), JsonSerializer.Serialize(new
        {
            Source = source,
            Utc = DateTimeOffset.UtcNow,
            Browser = browser.Version,
            data.Observatory,
            data.Camera,
            data.Installation,
            data.Binding,
            data.ExecutionHash,
            Capture = capture.Id,
            Artifact = preview.ArtifactId,
            UnavailableSibling = sibling.ArtifactId,
            ExactImage = exactImage,
            ImportedBefore = importedBefore,
            ImportedAfter = importedAfter,
            PublicationDecisions = publicationDecisions,
            Viewports = screens,
            Publication = "Exact release; expired sibling excluded; stale Owner withdrawal denied; authorized withdrawal; reconnect revalidation",
            Scope = "Four mixed observatory/camera routes denied despite membership in both sites",
            ImmutableEvidence = "Canonical bytes, payload hash and receipt time unchanged by publication and role races"
        }, EvidenceJsonOptions));
        await diagnostics.CompleteAsync();
    }

    private sealed record ImportedUnitSnapshot(long Id, long OriginSequence, string PayloadSha256, DateTimeOffset ReceivedAtUtc, string CanonicalBytesBase64);

    private static async Task<ImportedUnitSnapshot[]> ImmutableUnitsAsync(ApplicationDbContext db, Guid binding)
    {
        var units = await db.Set<ImportedExecutionEvidenceUnit>().AsNoTracking().Where(u => u.BindingId == binding)
            .OrderBy(u => u.OriginSequence).ToArrayAsync();
        return units.Select(u => new ImportedUnitSnapshot(u.Id, u.OriginSequence, u.PayloadSha256, u.ReceivedAtUtc, Convert.ToBase64String(u.CanonicalBytes))).ToArray();
    }

    private static Task ReadyAsync(IPage page, string selector) => page.Locator(
        $".scope-shell[data-scope-interactive='true'][aria-busy='false'] {selector}[data-scope-interactive='true'][aria-busy='false']").WaitForAsync();

    private static async Task TabAsync(IPage page, string name, string selector, bool touch)
    {
        var tab = page.GetByRole(AriaRole.Navigation, new() { Name = "Workspace sections", Exact = true })
            .GetByRole(AriaRole.Link, new() { Name = name, Exact = true });
        await tab.FocusAsync();
        if (touch) await tab.TapAsync(); else await page.Keyboard.PressAsync("Enter");
        await ReadyAsync(page, selector);
        await Assertions.Expect(tab).ToHaveAttributeAsync("aria-current", "page");
        var heading = selector switch
        {
            ".imported-inspector" => "#imported-heading",
            ".central-workspace" => "#central-heading",
            _ => "#scope-heading"
        };
        await Assertions.Expect(page.Locator(heading)).ToBeFocusedAsync();
    }

    private static async Task<string> CheckLayoutAsync(IPage page, string screenshot)
    {
        (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth")).Should().BeTrue();
        (await page.Locator("html").GetAttributeAsync("data-theme")).Should().Be("hvo-dark");
        await Assertions.Expect(page.Locator(".scope-content h1")).ToHaveCountAsync(1);
        var tree = await page.Locator(".scope-content").AriaSnapshotAsync();
        tree.Should().Contain("Workspace sections").And.Contain("heading");
        await page.ScreenshotAsync(new() { Path = screenshot, FullPage = true });
        return tree;
    }
}
