using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Configured async disposal would hide the strongly typed browser and host fixtures used throughout each acceptance scope.")]
public sealed class CameraAgentBrowserAcceptanceTests
{
    private const float DefaultTimeoutMilliseconds = 45_000;
    private const string OwnerRecoveryAttestationPurpose = "HVO.SkyMonitor.CameraAgent.OwnerRecovery.Attestation.v1";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task FirstOwnerLoginRequiresPasswordReplacementAndRevokesStaleSessionAsync()
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await playwright.EnsureLaunchableOrInconclusiveAsync().ConfigureAwait(false);

        await using var host = await CameraAgentKestrelFixture.CreateAsync(
            requireOwnerPasswordReplacement: true).ConfigureAwait(false);
        using (var installerVerification = await host.CreateOwnerClientAsync().ConfigureAwait(false))
        using (var initialStatus = await installerVerification.GetAsync(
            new Uri("/api/internal/owner-bootstrap/status", UriKind.Relative)).ConfigureAwait(false))
        {
            Assert.AreEqual(System.Net.HttpStatusCode.OK, initialStatus.StatusCode);
            StringAssert.Contains(
                await initialStatus.Content.ReadAsStringAsync().ConfigureAwait(false),
                OwnerBootstrapStates.TemporaryPassword,
                StringComparison.Ordinal);
        }
        await host.RestartWithoutPasswordAuthorityAsync().ConfigureAwait(false);
        await using var browser = await playwright.LaunchOrInconclusiveAsync().ConfigureAwait(false);
        await using var diagnostics = new PlaywrightDiagnostics(browser, TestContext);
        await using var replacingContext = await diagnostics.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString()
        }).ConfigureAwait(false);
        await using var staleContext = await diagnostics.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString()
        }).ConfigureAwait(false);
        var replacingPage = await replacingContext.NewPageAsync().ConfigureAwait(false);
        var stalePage = await staleContext.NewPageAsync().ConfigureAwait(false);

        await LoginAsync(replacingPage, CameraAgentKestrelFixture.OwnerEmail, CameraAgentKestrelFixture.OwnerPassword)
            .ConfigureAwait(false);
        await LoginAsync(stalePage, CameraAgentKestrelFixture.OwnerEmail, CameraAgentKestrelFixture.OwnerPassword)
            .ConfigureAwait(false);
        await replacingPage.WaitForURLAsync(
            url => url.Contains("/Account/ReplaceTemporaryPassword", StringComparison.OrdinalIgnoreCase))
            .ConfigureAwait(false);
        await stalePage.WaitForURLAsync(
            url => url.Contains("/Account/ReplaceTemporaryPassword", StringComparison.OrdinalIgnoreCase))
            .ConfigureAwait(false);

        var pendingStatus = await replacingContext.APIRequest.GetAsync("/api/internal/owner-bootstrap/status")
            .ConfigureAwait(false);
        try
        {
            Assert.AreEqual(200, pendingStatus.Status);
            StringAssert.Contains(
                await pendingStatus.TextAsync().ConfigureAwait(false),
                OwnerBootstrapStates.PasswordChangeRequired,
                StringComparison.Ordinal);
        }
        finally
        {
            await pendingStatus.DisposeAsync().ConfigureAwait(false);
        }

        var denied = await replacingContext.APIRequest.GetAsync("/api/v1/operations/summary").ConfigureAwait(false);
        try
        {
            Assert.AreEqual(403, denied.Status);
            Assert.AreEqual(OwnerBootstrapStates.PasswordChangeRequired,
                denied.Headers["x-hvo-authorization-reason"]);
        }
        finally
        {
            await denied.DisposeAsync().ConfigureAwait(false);
        }

        const string replacementPassword = "BrowserReplacement!418";
        await replacingPage.GetByLabel("Current password").FillAsync(CameraAgentKestrelFixture.OwnerPassword)
            .ConfigureAwait(false);
        await replacingPage.GetByLabel("New password", new() { Exact = true }).FillAsync(replacementPassword)
            .ConfigureAwait(false);
        await replacingPage.GetByLabel("Confirm new password").FillAsync(replacementPassword).ConfigureAwait(false);
        await SubmitPasswordReplacementAsync(replacingPage, replacingContext).ConfigureAwait(false);

        var ownerReady = await replacingContext.APIRequest.GetAsync("/api/internal/owner-bootstrap/status")
            .ConfigureAwait(false);
        try
        {
            Assert.AreEqual(200, ownerReady.Status);
            StringAssert.Contains(await ownerReady.TextAsync().ConfigureAwait(false), OwnerBootstrapStates.Ready, StringComparison.Ordinal);
        }
        finally
        {
            await ownerReady.DisposeAsync().ConfigureAwait(false);
        }

        var stale = await staleContext.APIRequest.GetAsync("/api/v1/operations/summary").ConfigureAwait(false);
        try
        {
            Assert.AreEqual(401, stale.Status);
        }
        finally
        {
            await stale.DisposeAsync().ConfigureAwait(false);
        }

        await host.RestartAsync().ConfigureAwait(false);
        await using var oldCredentialContext = await diagnostics.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString()
        }).ConfigureAwait(false);
        var oldCredentialPage = await oldCredentialContext.NewPageAsync().ConfigureAwait(false);
        await oldCredentialPage.GotoAsync("/Account/Login").ConfigureAwait(false);
        await oldCredentialPage.GetByLabel("Email").FillAsync(CameraAgentKestrelFixture.OwnerEmail)
            .ConfigureAwait(false);
        await oldCredentialPage.GetByLabel("Password").FillAsync(CameraAgentKestrelFixture.OwnerPassword)
            .ConfigureAwait(false);
        await oldCredentialPage.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync()
            .ConfigureAwait(false);
        await VisibleAsync(oldCredentialPage.GetByText("Error: Invalid login attempt.", new() { Exact = true }))
            .ConfigureAwait(false);
        await AssertComputedContrastAsync(
            oldCredentialPage,
            "/Account/Login",
            new ViewportSize { Width = 1280, Height = 720 }).ConfigureAwait(false);

        await using var replacementCredentialContext = await diagnostics.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString()
        }).ConfigureAwait(false);
        var replacementCredentialPage = await replacementCredentialContext.NewPageAsync().ConfigureAwait(false);
        await LoginAsync(
            replacementCredentialPage,
            CameraAgentKestrelFixture.OwnerEmail,
            replacementPassword).ConfigureAwait(false);
        await replacementCredentialPage.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/").ConfigureAwait(false);
        await VisibleAsync(replacementCredentialPage.GetByRole(AriaRole.Heading, new() { Name = "Current sky", Level = 1 }))
            .ConfigureAwait(false);
        await diagnostics.CompleteAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task LocalOwnerRecoveryRevokesSessionsAndPreservesCaptureContinuityAsync()
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await playwright.EnsureLaunchableOrInconclusiveAsync().ConfigureAwait(false);

        await using var host = await CameraAgentKestrelFixture.CreateAsync().ConfigureAwait(false);
        await host.RestartWithoutPasswordAuthorityAsync().ConfigureAwait(false);
        await using var browser = await playwright.LaunchOrInconclusiveAsync().ConfigureAwait(false);
        await using var diagnostics = new PlaywrightDiagnostics(browser, TestContext);
        await using var staleContext = await diagnostics.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString()
        }).ConfigureAwait(false);
        var stalePage = await staleContext.NewPageAsync().ConfigureAwait(false);
        await LoginAsync(stalePage, CameraAgentKestrelFixture.OwnerEmail, CameraAgentKestrelFixture.OwnerPassword)
            .ConfigureAwait(false);
        await stalePage.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/").ConfigureAwait(false);
        await stalePage.GotoAsync("/operations").ConfigureAwait(false);
        await VisibleAsync(stalePage.GetByRole(AriaRole.Heading, new() { Name = "Operations overview", Level = 1 }))
            .ConfigureAwait(false);
        var staleCaptureAction = stalePage.Locator("#capture-action");
        var staleConfirmation = stalePage.Locator("dialog.confirmation");
        await OpenDialogAsync(staleCaptureAction, staleConfirmation).ConfigureAwait(false);

        await using var staleReadContext = await diagnostics.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString()
        }).ConfigureAwait(false);
        var staleReadPage = await staleReadContext.NewPageAsync().ConfigureAwait(false);
        await LoginAsync(staleReadPage, CameraAgentKestrelFixture.OwnerEmail, CameraAgentKestrelFixture.OwnerPassword)
            .ConfigureAwait(false);
        await staleReadPage.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/").ConfigureAwait(false);
        await staleReadPage.GotoAsync("/operations").ConfigureAwait(false);
        await VisibleAsync(staleReadPage.GetByRole(AriaRole.Heading, new() { Name = "Operations overview", Level = 1 }))
            .ConfigureAwait(false);

        using var lifecycleClient = new HttpClient
        {
            BaseAddress = host.BaseAddress,
            Timeout = TimeSpan.FromSeconds(15)
        };
        lifecycleClient.DefaultRequestHeaders.Add(
            "X-HVO-Installation-Token",
            CameraAgentKestrelFixture.LifecycleControlToken);
        var before = await ReadLifecycleContinuityAsync(lifecycleClient).ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        var nonce = RandomNumberGenerator.GetBytes(32);
        using (var tcpRequest = new HttpRequestMessage(
                   HttpMethod.Post, "/api/internal/owner-bootstrap/recovery/attestation"))
        {
            tcpRequest.Headers.Add("X-HVO-Recovery-Operation", operationId.ToString("D"));
            tcpRequest.Headers.Add("X-Forwarded-For", "127.0.0.1");
            tcpRequest.Headers.Add("X-Forwarded-Proto", "http");
            tcpRequest.Content = new ByteArrayContent(nonce);
            tcpRequest.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            using var response = await lifecycleClient.SendAsync(tcpRequest).ConfigureAwait(false);
            Assert.AreEqual(404, (int)response.StatusCode);
        }

        using var recoveryClient = CreateUnixSocketHttpClient(host.RecoverySocketPath);
        recoveryClient.DefaultRequestHeaders.Add(
            "X-HVO-Installation-Token",
            CameraAgentKestrelFixture.LifecycleControlToken);
        using (var request = new HttpRequestMessage(
                   HttpMethod.Post, "/api/internal/owner-bootstrap/recovery/attestation"))
        {
            request.Headers.Add("X-HVO-Recovery-Operation", operationId.ToString("D"));
            request.Content = new ByteArrayContent(nonce);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            using var response = await recoveryClient.SendAsync(request).ConfigureAwait(false);
            Assert.AreEqual(200, (int)response.StatusCode);
            Assert.AreEqual("no-store", response.Headers.CacheControl?.ToString());
            var proof = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            var expectedProof = CreateOwnerRecoveryAttestationProof(
                CameraAgentKestrelFixture.LifecycleControlToken, operationId, nonce);
            Assert.IsTrue(CryptographicOperations.FixedTimeEquals(proof, expectedProof));
        }
        using var challengeRequest = CreateOwnerRecoveryRequest(
            "/api/internal/owner-bootstrap/recovery/challenge", operationId, []);
        using var challengeResponse = await recoveryClient.SendAsync(challengeRequest).ConfigureAwait(false);
        var challengeBody = await challengeResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        Assert.AreEqual(200, (int)challengeResponse.StatusCode);
        Assert.AreEqual("no-store", challengeResponse.Headers.CacheControl?.ToString());
        var challenge = Encoding.UTF8.GetString(challengeBody);
        Assert.IsFalse(string.IsNullOrWhiteSpace(challenge));

        const string temporaryPassword = "RecoveredBrowserOwner!515";
        using var completeRequest = CreateOwnerRecoveryRequest(
            "/api/internal/owner-bootstrap/recovery/complete",
            operationId,
            CreateOwnerRecoveryCompletionPayload(challenge!, temporaryPassword));
        using var completeResponse = await recoveryClient.SendAsync(completeRequest).ConfigureAwait(false);
        var completeBody = await completeResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(200, (int)completeResponse.StatusCode, completeBody);
        Assert.AreEqual("no-store", completeResponse.Headers.CacheControl?.ToString());
        Assert.IsFalse(Encoding.UTF8.GetString(challengeBody)
            .Contains(CameraAgentKestrelFixture.OwnerEmail, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(completeBody.Contains(temporaryPassword, StringComparison.Ordinal));

        if (!new Uri(stalePage.Url).AbsolutePath.Equals("/Account/AccessDenied", StringComparison.OrdinalIgnoreCase))
        {
            await stalePage.GetByRole(AriaRole.Button, new() { Name = "Confirm pause capture" }).ClickAsync()
                .ConfigureAwait(false);
        }
        await stalePage.WaitForURLAsync(url =>
            new Uri(url).AbsolutePath.Equals("/Account/AccessDenied", StringComparison.OrdinalIgnoreCase))
            .ConfigureAwait(false);
        await staleReadPage.WaitForURLAsync(url =>
            new Uri(url).AbsolutePath.Equals("/Account/AccessDenied", StringComparison.OrdinalIgnoreCase))
            .ConfigureAwait(false);

        var stale = await staleContext.APIRequest.GetAsync("/api/v1/operations/summary").ConfigureAwait(false);
        try
        {
            Assert.AreEqual(401, stale.Status);
        }
        finally
        {
            await stale.DisposeAsync().ConfigureAwait(false);
        }

        LifecycleContinuity after = before;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline && after.CaptureSequence <= before.CaptureSequence)
        {
            await Task.Delay(250).ConfigureAwait(false);
            after = await ReadLifecycleContinuityAsync(lifecycleClient).ConfigureAwait(false);
        }
        Assert.AreEqual(before.CaptureState, after.CaptureState);
        Assert.IsGreaterThan(before.CaptureSequence, after.CaptureSequence);

        await using var oldPasswordContext = await diagnostics.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString()
        }).ConfigureAwait(false);
        var oldPasswordPage = await oldPasswordContext.NewPageAsync().ConfigureAwait(false);
        await oldPasswordPage.GotoAsync("/Account/Login").ConfigureAwait(false);
        await oldPasswordPage.GetByLabel("Email").FillAsync(CameraAgentKestrelFixture.OwnerEmail).ConfigureAwait(false);
        await oldPasswordPage.GetByLabel("Password").FillAsync(CameraAgentKestrelFixture.OwnerPassword).ConfigureAwait(false);
        await oldPasswordPage.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync()
            .ConfigureAwait(false);
        await VisibleAsync(oldPasswordPage.GetByText("Error: Invalid login attempt.", new() { Exact = true }))
            .ConfigureAwait(false);

        await using var recoveredContext = await diagnostics.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString()
        }).ConfigureAwait(false);
        var recoveredPage = await recoveredContext.NewPageAsync().ConfigureAwait(false);
        await LoginAsync(recoveredPage, CameraAgentKestrelFixture.OwnerEmail, temporaryPassword).ConfigureAwait(false);
        await recoveredPage.WaitForURLAsync(
            url => url.Contains("/Account/ReplaceTemporaryPassword", StringComparison.OrdinalIgnoreCase))
            .ConfigureAwait(false);
        const string finalPassword = "FinalRecoveredOwner!515";
        await recoveredPage.GetByLabel("Current password").FillAsync(temporaryPassword).ConfigureAwait(false);
        await recoveredPage.GetByLabel("New password", new() { Exact = true }).FillAsync(finalPassword).ConfigureAwait(false);
        await recoveredPage.GetByLabel("Confirm new password").FillAsync(finalPassword).ConfigureAwait(false);
        await SubmitPasswordReplacementAsync(recoveredPage, recoveredContext).ConfigureAwait(false);

        await host.RestartAsync().ConfigureAwait(false);
        await using var finalContext = await diagnostics.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString()
        }).ConfigureAwait(false);
        var finalPage = await finalContext.NewPageAsync().ConfigureAwait(false);
        await LoginAsync(finalPage, CameraAgentKestrelFixture.OwnerEmail, finalPassword).ConfigureAwait(false);
        await finalPage.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/").ConfigureAwait(false);
        await diagnostics.CompleteAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ObservatoryShellAndAccountPagesRemainLocalResponsiveAndKeyboardReachableAsync()
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await playwright.EnsureLaunchableOrInconclusiveAsync().ConfigureAwait(false);

        await using var host = await CameraAgentKestrelFixture.CreateAsync().ConfigureAwait(false);
        await using var browser = await playwright.LaunchOrInconclusiveAsync().ConfigureAwait(false);
        await using var diagnostics = new PlaywrightDiagnostics(browser, TestContext);
        await using var context = await diagnostics.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString(),
            ViewportSize = new ViewportSize { Width = 390, Height = 844 },
            ColorScheme = ColorScheme.Dark,
            ReducedMotion = ReducedMotion.Reduce
        }).ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        var externalRequests = new List<string>();
        page.Request += (_, request) =>
        {
            var requestUri = new Uri(request.Url);
            if (!string.Equals(requestUri.Host, host.BaseAddress.Host, StringComparison.OrdinalIgnoreCase) ||
                requestUri.Port != host.BaseAddress.Port)
            {
                externalRequests.Add(request.Url);
            }
        };

        await page.GotoAsync("/Account/Login").ConfigureAwait(false);
        await VisibleAsync(page.GetByText("HVO SkyMonitor", new() { Exact = true })).ConfigureAwait(false);
        foreach (var retiredRoute in new[]
        {
            "/Account/ForgotPassword",
            "/Account/ForgotPasswordConfirmation",
            "/Account/ResetPassword?code=retired-browser-token",
            "/Account/ResetPasswordConfirmation",
            "/Account/ResendEmailConfirmation",
            "/Account/ConfirmEmail",
            "/Account/ConfirmEmailChange",
            "/Account/InvalidPasswordReset",
            "/Account/Manage/SetPassword",
            "/account/recovery?code=retired-browser-token#fragment"
        })
        {
            await page.GotoAsync(retiredRoute).ConfigureAwait(false);
            var canonicalRecovery = new Uri(page.Url);
            Assert.AreEqual("/Account/Recovery", canonicalRecovery.AbsolutePath, retiredRoute);
            Assert.AreEqual(string.Empty, canonicalRecovery.Query, retiredRoute);
            Assert.AreEqual(string.Empty, canonicalRecovery.Fragment, retiredRoute);
        }
        await page.GotoAsync("/Account/Login").ConfigureAwait(false);
        Assert.IsFalse((await page.ContentAsync().ConfigureAwait(false)).Contains("cdn.jsdelivr", StringComparison.OrdinalIgnoreCase));
        await page.GetByRole(AriaRole.Link, new() { Name = "Recover owner access" }).ClickAsync().ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Recover owner access", Level = 1 }))
            .ConfigureAwait(false);
        Assert.IsEmpty(await page.Locator("input").AllAsync().ConfigureAwait(false));
        Assert.IsFalse((await page.Locator("main").InnerTextAsync().ConfigureAwait(false))
            .Contains(CameraAgentKestrelFixture.OwnerEmail, StringComparison.OrdinalIgnoreCase));

        await LoginAsync(page, CameraAgentKestrelFixture.OwnerEmail, CameraAgentKestrelFixture.OwnerPassword)
            .ConfigureAwait(false);
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/").ConfigureAwait(false);
        var currentSkyHeading = page.GetByRole(AriaRole.Heading, new() { Name = "Current sky", Level = 1 });
        await VisibleAsync(currentSkyHeading).ConfigureAwait(false);
        await VisibleAsync(page.Locator(".current-sky-hero")).ConfigureAwait(false);
        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
        var keyboardPage = await context.NewPageAsync().ConfigureAwait(false);
        try
        {
            await keyboardPage.GotoAsync("/").ConfigureAwait(false);
            await VisibleAsync(keyboardPage.GetByRole(AriaRole.Heading, new() { Name = "Current sky", Level = 1 }))
                .ConfigureAwait(false);
            await WaitForInteractiveShellAsync(keyboardPage).ConfigureAwait(false);
            var skipLink = keyboardPage.Locator(".skip-link");
            await keyboardPage.EvaluateAsync(
                "() => { document.body.tabIndex = -1; document.body.focus(); }").ConfigureAwait(false);
            await keyboardPage.Keyboard.PressAsync("Tab").ConfigureAwait(false);
            var firstTabTarget = await keyboardPage.EvaluateAsync<string>(
                "() => `${document.activeElement?.tagName ?? ''}#${document.activeElement?.id ?? ''}.${document.activeElement?.className ?? ''}`")
                .ConfigureAwait(false);
            Assert.AreEqual("A#.skip-link", firstTabTarget, $"Unexpected first tab target: {firstTabTarget}");
            await Task.Delay(300).ConfigureAwait(false);
            var skipLinkTop = await skipLink.EvaluateAsync<double>(
                "element => element.getBoundingClientRect().top").ConfigureAwait(false);
            Assert.IsGreaterThanOrEqualTo(0, skipLinkTop);
            await keyboardPage.Keyboard.PressAsync("Enter").ConfigureAwait(false);
            await keyboardPage.WaitForFunctionAsync("() => document.activeElement?.id === 'mainContent'").ConfigureAwait(false);
        }
        finally
        {
            await keyboardPage.CloseAsync().ConfigureAwait(false);
        }

        var menuToggle = page.Locator("button[aria-controls='shell-menu-panel']");
        await VisibleAsync(menuToggle).ConfigureAwait(false);
        await OpenMenuWithKeyboardAsync(menuToggle).ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Link, new() { Name = "Operations", Exact = true }).First)
            .ConfigureAwait(false);
        await AssertNavigationModalAsync(page, "shell-menu-panel").ConfigureAwait(false);
        await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
        await page.WaitForFunctionAsync("() => document.querySelector('button[aria-controls=\"shell-menu-panel\"]')?.getAttribute('aria-expanded') === 'false'")
            .ConfigureAwait(false);
        await WaitForFocusAsync(page, menuToggle).ConfigureAwait(false);
        await OpenMenuWithKeyboardAsync(menuToggle).ConfigureAwait(false);
        var accountToggle = page.Locator("summary.nav-avatar-button");
        await accountToggle.ClickAsync().ConfigureAwait(false);
        await page.WaitForFunctionAsync("() => document.querySelector('details.nav-account-info')?.open === true")
            .ConfigureAwait(false);
        var accountSettings = page.GetByRole(AriaRole.Link, new() { Name = "Account settings", Exact = true });
        Assert.AreEqual("Account/Manage", await accountSettings.GetAttributeAsync("href").ConfigureAwait(false));
        await page.GotoAsync("/Account/Manage").ConfigureAwait(false);
        // Each Manage route names itself. "Account settings" is the navigation region's name and
        // the link that reaches it, not the page name; asserting it here passed on all three routes.
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Profile", Level = 1, Exact = true }))
            .ConfigureAwait(false);
        var ownerEmail = page.GetByRole(AriaRole.Link, new() { Name = "Owner email", Exact = true });
        Assert.AreEqual("Account/Manage/Email", await ownerEmail.GetAttributeAsync("href").ConfigureAwait(false));
        await page.GotoAsync("/Account/Manage/Email").ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Owner email", Level = 1, Exact = true }))
            .ConfigureAwait(false);
        Assert.AreEqual("", await page.Locator("#owner-email").GetAttributeAsync("readonly").ConfigureAwait(false));
        Assert.AreEqual(
            0,
            await page.Locator("[role='alert'], [role='status'], [aria-live]").CountAsync().ConfigureAwait(false));
        Assert.IsFalse(await page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1").ConfigureAwait(false));

        await page.SetViewportSizeAsync(1024, 768).ConfigureAwait(false);
        await page.GotoAsync("/").ConfigureAwait(false);
        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
        var desktopCurrentSky = page.GetByRole(AriaRole.Link, new() { Name = "Current Sky", Exact = true });
        await VisibleAsync(desktopCurrentSky).ConfigureAwait(false);
        Assert.IsFalse(await page.Locator("button[aria-controls='shell-menu-panel']").IsVisibleAsync().ConfigureAwait(false));
        await desktopCurrentSky.FocusAsync().ConfigureAwait(false);
        await WaitForFocusAsync(page, desktopCurrentSky).ConfigureAwait(false);
        await page.GotoAsync("/schedule").ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Capture schedule", Level = 1 }))
            .ConfigureAwait(false);
        await VisibleAsync(page.GetByLabel("Current schedule state")).ConfigureAwait(false);
        Assert.AreEqual(
            "page",
            await page.GetByRole(AriaRole.Link, new() { Name = "Operations", Exact = true })
                .GetAttributeAsync("aria-current").ConfigureAwait(false));
        await AssertOperationsWorkspaceAsync(page).ConfigureAwait(false);
        await page.Locator(".shell-primary a[href='/transients']").ClickAsync().ConfigureAwait(false);
        await page.WaitForFunctionAsync("() => document.querySelector('.shell-primary a[aria-current=\"page\"]')?.textContent.trim() === 'Events'")
            .ConfigureAwait(false);
        await page.GoBackAsync().ConfigureAwait(false);
        await page.WaitForFunctionAsync("() => document.querySelector('.shell-primary a[aria-current=\"page\"]')?.textContent.trim() === 'Operations'")
            .ConfigureAwait(false);
        Assert.IsFalse(await page.Locator("#blazor-error-ui").IsVisibleAsync().ConfigureAwait(false));

        foreach (var asset in new[]
        {
            "/vendor/bootstrap/bootstrap.min.css",
            "/vendor/bootstrap-icons/bootstrap-icons.min.css",
            "/vendor/bootstrap-icons/fonts/bootstrap-icons.woff2"
        })
        {
            var response = await context.APIRequest.GetAsync(asset).ConfigureAwait(false);
            try
            {
                Assert.AreEqual(200, response.Status, asset);
                Assert.IsGreaterThan(1000, (await response.BodyAsync().ConfigureAwait(false)).Length, asset);
            }
            finally
            {
                await response.DisposeAsync().ConfigureAwait(false);
            }
        }

        Assert.IsEmpty(externalRequests, string.Join(Environment.NewLine, externalRequests));
        await page.GotoAsync("/").ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Current sky", Level = 1 }))
            .ConfigureAwait(false);
        await diagnostics.CompleteAsync().ConfigureAwait(false);
    }

    [TestMethod]
    [TestCategory("Manual")]
    [DoNotParallelize]
    public async Task SharedShellReviewRegressionsAsync()
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await playwright.EnsureLaunchableOrInconclusiveAsync().ConfigureAwait(false);
        await using var host = await CameraAgentKestrelFixture.CreateAsync(enableCentralIntegration: true).ConfigureAwait(false);
        await using var browser = await playwright.LaunchOrInconclusiveAsync().ConfigureAwait(false);
        await using var diagnostics = new PlaywrightDiagnostics(browser, TestContext);
        await using var context = await diagnostics.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString(),
            ViewportSize = new ViewportSize { Width = 390, Height = 844 }
        }).ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        await LoginAsync(page, CameraAgentKestrelFixture.OwnerEmail, CameraAgentKestrelFixture.OwnerPassword).ConfigureAwait(false);
        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
        await page.Locator("button[aria-controls='shell-menu-panel']").ClickAsync().ConfigureAwait(false);
        await page.Locator("#shell-menu-panel a[href='/operations']").ClickAsync().ConfigureAwait(false);
        await page.WaitForFunctionAsync("() => document.activeElement?.matches('h1') && document.activeElement.textContent === 'Operations overview'")
            .ConfigureAwait(false);
        await page.WaitForFunctionAsync("() => document.querySelector('button[aria-controls=\"shell-menu-panel\"]')?.getAttribute('aria-expanded') === 'false'")
            .ConfigureAwait(false);
        Assert.AreEqual("H1", await page.EvaluateAsync<string>("() => document.activeElement.tagName").ConfigureAwait(false));

        await page.Locator("button[aria-controls='operations-sections']").ClickAsync().ConfigureAwait(false);
        await page.Locator("#operations-sections a[href='/operations/system']").ClickAsync().ConfigureAwait(false);
        await page.WaitForFunctionAsync("() => document.activeElement?.matches('h1') && document.activeElement.textContent === 'System snapshot'")
            .ConfigureAwait(false);
        await page.WaitForFunctionAsync("() => document.querySelector('button[aria-controls=\"operations-sections\"]')?.getAttribute('aria-expanded') === 'false'")
            .ConfigureAwait(false);
        Assert.AreEqual("H1", await page.EvaluateAsync<string>("() => document.activeElement.tagName").ConfigureAwait(false));

        Assert.IsTrue(await page.EvaluateAsync<bool>("""
            async () => {
              const module = await import('./Components/Layout/ResponsiveNavigation.razor.js');
              const panel = document.createElement('dialog');
              const toggle = document.createElement('button');
              const receiver = { invokeMethodAsync() { throw new Error('Detached initialization must not notify .NET'); } };
              module.initialize(null, null, 780, receiver);
              module.initialize(panel, toggle, 780, receiver);
              module.open(panel);
              if (panel.open) return false;
              document.body.append(toggle, panel);
              module.initialize(panel, toggle, 780, { invokeMethodAsync: () => Promise.resolve() });
              module.open(panel);
              const closed = new Promise(resolve => panel.addEventListener('close', resolve, { once: true }));
              module.close(panel, false);
              const heading = document.querySelector('h1');
              heading.focus();
              await closed;
              const retainedHeadingFocus = document.activeElement === heading;
              module.dispose(panel);
              panel.remove();
              toggle.remove();
              return retainedHeadingFocus;
            }
            """).ConfigureAwait(false));

        // Exercise actual theme CSS for both selected-state forms, not a hand-coded ratio.
        await page.EvaluateAsync("""
            () => {
              const controls = document.createElement('section');
              controls.id = 'shell-contrast-regression';
              controls.innerHTML = '<button class="btn btn-primary">Primary action</button>' +
                '<button class="hvo-segmented__option" aria-pressed="true">Selected by aria</button>' +
                '<button class="hvo-segmented__option hvo-segmented__option--selected">Selected by class</button>';
              document.querySelector('main').append(controls);
            }
            """).ConfigureAwait(false);
        await AssertComputedContrastAsync(page, "/operations/system", new ViewportSize { Width = 390, Height = 844 },
            "#shell-contrast-regression button").ConfigureAwait(false);

        await page.SetViewportSizeAsync(1440, 900).ConfigureAwait(false);
        await AssertOperationsAndCaptureControlAsync(page).ConfigureAwait(false);
        await AssertQuarantineAsync(page).ConfigureAwait(false);
        foreach (var width in new[] { 1440, 390, 320 })
        {
            await page.SetViewportSizeAsync(width, width == 1440 ? 900 : 844).ConfigureAwait(false);
            foreach (var route in new[] { "/gallery", "/archive/calendar", "/archive/products" })
            {
                await page.GotoAsync(route).ConfigureAwait(false);
                await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
                await VisibleAsync(page.Locator(".archive-subnav")).ConfigureAwait(false);
                var bounds = await page.Locator(".archive-subnav").BoundingBoxAsync().ConfigureAwait(false);
                Assert.IsNotNull(bounds);
                var expectedX = width == 1440 ? 37.44f : 10.4f;
                var expectedY = route == "/gallery" ? width == 1440 ? 102.44f : 80f : width == 1440 ? 65f : 64f;
                Assert.AreEqual(expectedX, bounds.X, .1f, route);
                Assert.AreEqual(expectedY, bounds.Y, .1f, route);
                Assert.IsFalse(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > innerWidth").ConfigureAwait(false));
            }
        }
        Assert.IsFalse(await page.Locator("#blazor-error-ui").IsVisibleAsync().ConfigureAwait(false));
        await diagnostics.CompleteAsync().ConfigureAwait(false);
    }

    [TestMethod]
    [TestCategory("Manual")]
    [DoNotParallelize]
    public async Task OwnerOperationsGalleryAndResponsiveAcceptanceAsync()
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await playwright.EnsureLaunchableOrInconclusiveAsync().ConfigureAwait(false);

        await using var host = await CameraAgentKestrelFixture.CreateAsync(
            enableCentralIntegration: true).ConfigureAwait(false);
        await using var browser = await playwright.LaunchOrInconclusiveAsync().ConfigureAwait(false);
        await using var diagnostics = new PlaywrightDiagnostics(browser, TestContext);

        await AssertAnonymousAndNonOwnerAuthorizationAsync(diagnostics, host.BaseAddress).ConfigureAwait(false);

        await using var context = await diagnostics.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString(),
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            ColorScheme = ColorScheme.Dark,
            ReducedMotion = ReducedMotion.Reduce
        }).ConfigureAwait(false);
        context.SetDefaultTimeout(DefaultTimeoutMilliseconds);
        context.SetDefaultNavigationTimeout(DefaultTimeoutMilliseconds);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        var browserErrors = new List<string>();
        var previewFailures = new List<string>();
        page.PageError += (_, error) => browserErrors.Add(error);
        page.Response += (_, response) =>
        {
            var path = new Uri(response.Url).AbsolutePath;
            if (path.Contains("/api/v1/operations/artifacts/", StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith("/preview", StringComparison.OrdinalIgnoreCase) &&
                !response.Ok && response.Status != 304)
            {
                previewFailures.Add($"{response.Status}:{path}");
            }
        };

        await LoginAsync(
            page,
            CameraAgentKestrelFixture.OwnerEmail,
            CameraAgentKestrelFixture.OwnerPassword).ConfigureAwait(false);
        await AssertCurrentSkyAsync(page).ConfigureAwait(false);
        await AssertOperationsAndCaptureControlAsync(page).ConfigureAwait(false);
        var detailUrl = await AssertGalleryAsync(page, context, host).ConfigureAwait(false);
        await AssertQuarantineAsync(page).ConfigureAwait(false);
        await AssertSystemNavigationAsync(page).ConfigureAwait(false);
        await AssertSchedulePreviewAsync(page).ConfigureAwait(false);
        await AssertCalibrationAsync(page).ConfigureAwait(false);
        await AssertResponsiveAndAccessibleAsync(page, detailUrl).ConfigureAwait(false);

        Assert.IsEmpty(browserErrors, string.Join(Environment.NewLine, browserErrors));
        Assert.IsEmpty(previewFailures, string.Join(Environment.NewLine, previewFailures));
        await diagnostics.CompleteAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task OwnerCurrentSkyResponsiveAcceptanceAsync()
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await playwright.EnsureLaunchableOrInconclusiveAsync().ConfigureAwait(false);

        await using var host = await CameraAgentKestrelFixture.CreateAsync().ConfigureAwait(false);
        await using var browser = await playwright.LaunchOrInconclusiveAsync().ConfigureAwait(false);
        await using var diagnostics = new PlaywrightDiagnostics(browser, TestContext);
        await AssertAnonymousAndNonOwnerAuthorizationAsync(diagnostics, host.BaseAddress).ConfigureAwait(false);
        await using var context = await diagnostics.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString(),
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            ColorScheme = ColorScheme.Dark,
            ReducedMotion = ReducedMotion.Reduce
        }).ConfigureAwait(false);
        context.SetDefaultTimeout(DefaultTimeoutMilliseconds);
        context.SetDefaultNavigationTimeout(DefaultTimeoutMilliseconds);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        var browserErrors = new List<string>();
        var previewFailures = new List<string>();
        page.PageError += (_, error) => browserErrors.Add(error);
        page.Response += (_, response) =>
        {
            var path = new Uri(response.Url).AbsolutePath;
            if (path.Contains("/api/v1/operations/artifacts/", StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith("/preview", StringComparison.OrdinalIgnoreCase) &&
                !response.Ok && response.Status != 304)
            {
                previewFailures.Add($"{response.Status}:{path}");
            }
        };

        await LoginAsync(page, CameraAgentKestrelFixture.OwnerEmail, CameraAgentKestrelFixture.OwnerPassword)
            .ConfigureAwait(false);
        await AssertCurrentSkyAsync(page).ConfigureAwait(false);
        foreach (var viewport in new[]
        {
            new ViewportSize { Width = 1440, Height = 900 },
            new ViewportSize { Width = 820, Height = 1180 },
            new ViewportSize { Width = 390, Height = 844 },
            new ViewportSize { Width = 844, Height = 390 },
            new ViewportSize { Width = 320, Height = 700 }
        })
        {
            await page.SetViewportSizeAsync(viewport.Width, viewport.Height).ConfigureAwait(false);
            await page.GotoAsync("/").ConfigureAwait(false);
            await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Current sky", Level = 1 }))
                .ConfigureAwait(false);
            await AssertPageStructureAsync(page, "/").ConfigureAwait(false);
            await AssertComputedContrastAsync(page, "/", viewport).ConfigureAwait(false);
            await AssertCurrentSkyResponsiveAsync(page, viewport).ConfigureAwait(false);
        }

        Assert.IsEmpty(browserErrors, string.Join(Environment.NewLine, browserErrors));
        Assert.IsEmpty(previewFailures, string.Join(Environment.NewLine, previewFailures));
        await diagnostics.CompleteAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task OwnerArchivePresentationAcceptanceAsync()
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await playwright.EnsureLaunchableOrInconclusiveAsync().ConfigureAwait(false);

        await using var host = await CameraAgentKestrelFixture.CreateAsync().ConfigureAwait(false);
        await using var browser = await playwright.LaunchOrInconclusiveAsync().ConfigureAwait(false);
        await using var diagnostics = new PlaywrightDiagnostics(browser, TestContext);
        await AssertAnonymousAndNonOwnerAuthorizationAsync(diagnostics, host.BaseAddress).ConfigureAwait(false);
        await using var context = await diagnostics.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString(),
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            ColorScheme = ColorScheme.Dark,
            ReducedMotion = ReducedMotion.Reduce
        }).ConfigureAwait(false);
        context.SetDefaultTimeout(DefaultTimeoutMilliseconds);
        context.SetDefaultNavigationTimeout(DefaultTimeoutMilliseconds);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        var browserErrors = new List<string>();
        var previewFailures = new List<string>();
        page.PageError += (_, error) => browserErrors.Add(error);
        page.Response += (_, response) =>
        {
            var path = new Uri(response.Url).AbsolutePath;
            if (path.Contains("/api/v1/operations/artifacts/", StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith("/preview", StringComparison.OrdinalIgnoreCase) &&
                !response.Ok && response.Status != 304)
            {
                previewFailures.Add($"{response.Status}:{path}");
            }
        };

        await LoginAsync(page, CameraAgentKestrelFixture.OwnerEmail, CameraAgentKestrelFixture.OwnerPassword)
            .ConfigureAwait(false);
        await WaitForGalleryCapturesAsync(page, minimumCards: 24).ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Captures", Level = 1 })).ConfigureAwait(false);
        var advanced = page.Locator(".advanced-filters");
        Assert.IsFalse(await advanced.EvaluateAsync<bool>("details => details.open").ConfigureAwait(false));
        var firstCard = page.Locator(".capture-card")
            .Filter(new LocatorFilterOptions { HasText = "Processed" })
            .First;

        await CollapsibleSection.EnsureOpenAsync(advanced, page.GetByLabel("Evidence origin")).ConfigureAwait(false);
        await page.GetByLabel("Evidence origin").SelectOptionAsync("Simulated").ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Apply" }).ClickAsync().ConfigureAwait(false);
        await page.WaitForURLAsync(url => new Uri(url).Query.Contains("origin=Simulated", StringComparison.Ordinal)).ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Button, new() { Name = "Older captures" })).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Older captures" }).ClickAsync().ConfigureAwait(false);
        await page.WaitForURLAsync(url => new Uri(url).Query.Contains("origin=Simulated", StringComparison.Ordinal) &&
            new Uri(url).Query.Contains("cursor=", StringComparison.Ordinal)).ConfigureAwait(false);
        await page.WaitForFunctionAsync(
            "() => decodeURIComponent(document.querySelector('.capture-image')?.getAttribute('href') || '').includes('cursor=')")
            .ConfigureAwait(false);
        var detailUrl = await page.Locator(".capture-image").First.GetAttributeAsync("href").ConfigureAwait(false);
        Assert.IsNotNull(detailUrl);
        StringAssert.Contains(Uri.UnescapeDataString(detailUrl), "origin=Simulated", StringComparison.Ordinal);
        StringAssert.Contains(Uri.UnescapeDataString(detailUrl), "cursor=", StringComparison.Ordinal);

        foreach (var viewport in new[]
        {
            new ViewportSize { Width = 1440, Height = 900 },
            new ViewportSize { Width = 390, Height = 844 },
            new ViewportSize { Width = 844, Height = 390 }
        })
        {
            await page.SetViewportSizeAsync(viewport.Width, viewport.Height).ConfigureAwait(false);
            await page.GotoAsync("/gallery?pageSize=24").ConfigureAwait(false);
            await VisibleAsync(page.Locator(".capture-card").First).ConfigureAwait(false);
            Assert.IsFalse(await page.EvaluateAsync<bool>(
                "() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1").ConfigureAwait(false),
                $"archive overflowed at {viewport.Width}x{viewport.Height}");
            await AssertComputedContrastAsync(page, "/gallery?pageSize=24", viewport).ConfigureAwait(false);
        }

        Assert.IsEmpty(browserErrors, string.Join(Environment.NewLine, browserErrors));
        Assert.IsEmpty(previewFailures, string.Join(Environment.NewLine, previewFailures));
        await diagnostics.CompleteAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task OwnerArchiveCalendarProductsAndCandidatesAcceptanceAsync()
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await playwright.EnsureLaunchableOrInconclusiveAsync().ConfigureAwait(false);

        await using var host = await CameraAgentKestrelFixture.CreateAsync().ConfigureAwait(false);
        await using var browser = await playwright.LaunchOrInconclusiveAsync().ConfigureAwait(false);
        await using var diagnostics = new PlaywrightDiagnostics(browser, TestContext);
        await using var context = await diagnostics.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString(),
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            ColorScheme = ColorScheme.Dark,
            ReducedMotion = ReducedMotion.Reduce
        }).ConfigureAwait(false);
        context.SetDefaultTimeout(DefaultTimeoutMilliseconds);
        context.SetDefaultNavigationTimeout(DefaultTimeoutMilliseconds);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        var browserErrors = new List<string>();
        page.PageError += (_, error) => browserErrors.Add(error);

        await LoginAsync(page, CameraAgentKestrelFixture.OwnerEmail, CameraAgentKestrelFixture.OwnerPassword)
            .ConfigureAwait(false);
        await WaitForGalleryCapturesAsync(page, minimumCards: 1).ConfigureAwait(false);

        // Calendar: the current night carries the seeded captures and links into the filtered archive.
        await page.GotoAsync("/archive/calendar").ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Observing calendar", Level = 1 })).ConfigureAwait(false);
        await VisibleAsync(page.Locator(".night").First).ConfigureAwait(false);
        var populated = page.Locator(".night:not(.night--empty)").First;
        await VisibleAsync(populated).ConfigureAwait(false);
        var capturesLink = populated.GetByRole(AriaRole.Link, new() { Name = "Open captures" });
        var dayUrl = await capturesLink.GetAttributeAsync("href").ConfigureAwait(false);
        Assert.IsNotNull(dayUrl);
        StringAssert.StartsWith(dayUrl, "/gallery?from=", StringComparison.Ordinal);
        await AssertPageStructureAsync(page, "/archive/calendar").ConfigureAwait(false);
        await capturesLink.ClickAsync().ConfigureAwait(false);
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/gallery" && new Uri(url).Query.Contains("from=", StringComparison.Ordinal)).ConfigureAwait(false);
        await VisibleAsync(page.Locator(".capture-card").First).ConfigureAwait(false);

        // Products: retained outputs list, a detail page, and its capture link.
        await page.GotoAsync("/archive/products").ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Products", Level = 1 })).ConfigureAwait(false);
        await VisibleAsync(page.Locator(".product-table tbody tr").First).ConfigureAwait(false);
        await AssertPageStructureAsync(page, "/archive/products").ConfigureAwait(false);
        await page.GetByLabel("Role").SelectOptionAsync("Metadata").ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Apply", Exact = true }).ClickAsync().ConfigureAwait(false);
        await page.WaitForURLAsync(url => new Uri(url).Query.Contains("role=Metadata", StringComparison.Ordinal)).ConfigureAwait(false);
        await page.GotoAsync("/archive/products").ConfigureAwait(false);
        var firstProduct = page.Locator(".product-table tbody tr").First.Locator("a").First;
        await VisibleAsync(firstProduct).ConfigureAwait(false);
        await firstProduct.ClickAsync().ConfigureAwait(false);
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath.StartsWith("/archive/products/", StringComparison.Ordinal)).ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Link, new() { Name = "Open capture" })).ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Sources", Level = 2 })).ConfigureAwait(false);
        await AssertPageStructureAsync(page, "/archive/products/detail").ConfigureAwait(false);
        var missingProduct = await context.NewPageAsync().ConfigureAwait(false);
        await missingProduct.GotoAsync($"/archive/products/{Guid.NewGuid():D}").ConfigureAwait(false);
        await VisibleAsync(missingProduct.GetByRole(AriaRole.Alert)).ConfigureAwait(false);
        await missingProduct.CloseAsync().ConfigureAwait(false);

        // Candidates: the list accepts the calendar range and never claims event authority.
        await page.GotoAsync("/transients?from=2000-01-01T00:00:00&to=2000-01-02T00:00:00").ConfigureAwait(false);
        await VisibleAsync(page.Locator(".range-state")).ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Transient candidates", Level = 1 })).ConfigureAwait(false);
        var candidateText = await page.Locator("main").InnerTextAsync().ConfigureAwait(false);
        foreach (var forbidden in new[] { "ground track", "impact location", "validated event" })
        {
            Assert.IsFalse(candidateText.Contains(forbidden, StringComparison.OrdinalIgnoreCase), forbidden);
        }
        await AssertPageStructureAsync(page, "/transients").ConfigureAwait(false);

        foreach (var route in new[] { "/archive/calendar", "/archive/products", "/transients" })
        {
            foreach (var viewport in new[]
            {
                new ViewportSize { Width = 1440, Height = 900 },
                new ViewportSize { Width = 390, Height = 844 },
                new ViewportSize { Width = 844, Height = 390 }
            })
            {
                await page.SetViewportSizeAsync(viewport.Width, viewport.Height).ConfigureAwait(false);
                await page.GotoAsync(route).ConfigureAwait(false);
                await VisibleAsync(page.Locator("main h1")).ConfigureAwait(false);
                Assert.IsFalse(await page.EvaluateAsync<bool>(
                    "() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1").ConfigureAwait(false),
                    $"{route} overflowed at {viewport.Width}x{viewport.Height}");
                await AssertComputedContrastAsync(page, route, viewport).ConfigureAwait(false);
            }
        }

        Assert.IsEmpty(browserErrors, string.Join(Environment.NewLine, browserErrors));
        await diagnostics.CompleteAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task OwnerCaptureDetailPresentationAcceptanceAsync()
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await playwright.EnsureLaunchableOrInconclusiveAsync().ConfigureAwait(false);

        await using var host = await CameraAgentKestrelFixture.CreateAsync().ConfigureAwait(false);
        await using var browser = await playwright.LaunchOrInconclusiveAsync().ConfigureAwait(false);
        await using var diagnostics = new PlaywrightDiagnostics(browser, TestContext);
        await using var context = await diagnostics.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString(),
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            ColorScheme = ColorScheme.Dark,
            ReducedMotion = ReducedMotion.Reduce
        }).ConfigureAwait(false);
        context.SetDefaultTimeout(DefaultTimeoutMilliseconds);
        context.SetDefaultNavigationTimeout(DefaultTimeoutMilliseconds);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        var browserErrors = new List<string>();
        var previewFailures = new List<string>();
        page.PageError += (_, error) => browserErrors.Add(error);
        page.Response += (_, response) =>
        {
            var path = new Uri(response.Url).AbsolutePath;
            if (path.Contains("/api/v1/operations/artifacts/", StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith("/preview", StringComparison.OrdinalIgnoreCase) &&
                !response.Ok && response.Status != 304)
            {
                previewFailures.Add($"{response.Status}:{path}");
            }
        };

        await LoginAsync(page, CameraAgentKestrelFixture.OwnerEmail, CameraAgentKestrelFixture.OwnerPassword)
            .ConfigureAwait(false);
        await WaitForGalleryCapturesAsync(page, minimumCards: 3).ConfigureAwait(false);
        var detailLink = page.Locator(".capture-card a[aria-label^='Open capture']").Nth(1);
        var detailUrl = await detailLink.GetAttributeAsync("href").ConfigureAwait(false);
        Assert.IsNotNull(detailUrl);
        var capture = await ReadGalleryCaptureAsync(page, detailUrl).ConfigureAwait(false);
        // Pin the resolved capture: a live list rerender must not retarget this nth-card locator.
        await page.GotoAsync(detailUrl).ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = $"Capture #{capture.CaptureSequence}", Level = 1, Exact = true }))
            .ConfigureAwait(false);
        var image = page.Locator(".sky-image-stage img");
        await VisibleAsync(image).ConfigureAwait(false);
        Assert.IsFalse(await page.Locator("#technical-evidence").EvaluateAsync<bool>("details => details.open")
            .ConfigureAwait(false));
        await VisibleAsync(page.Locator(".current-sky-summary")).ConfigureAwait(false);
        var archivedPathAndQuery = new Uri(page.Url).PathAndQuery;
        foreach (var linkText in new[] { "Technical evidence and downloads", "View immutable raw source" })
        {
            var evidenceLink = page.GetByRole(AriaRole.Link, new() { Name = linkText, Exact = true });
            Assert.AreEqual(archivedPathAndQuery + "#technical-evidence",
                await evidenceLink.GetAttributeAsync("href").ConfigureAwait(false));
            await evidenceLink.ClickAsync().ConfigureAwait(false);
            await page.WaitForFunctionAsync("() => document.querySelector('#technical-evidence')?.open === true")
                .ConfigureAwait(false);
            Assert.AreEqual(archivedPathAndQuery, new Uri(page.Url).PathAndQuery);
            Assert.AreEqual($"Capture #{capture.CaptureSequence}", await page.Locator("h1").InnerTextAsync().ConfigureAwait(false));
            await page.Locator("#technical-evidence > summary").ClickAsync().ConfigureAwait(false);
        }
        Assert.IsGreaterThanOrEqualTo(2, await page.Locator(".stage-switcher button:not(:disabled)").CountAsync().ConfigureAwait(false));
        Assert.IsGreaterThan(0, await page.Locator(".stage-switcher button:disabled").CountAsync().ConfigureAwait(false));

        var rawStage = page.Locator(".stage-switcher button[title='Show Raw image']");
        var rawArtifact = capture.Artifacts.Single(artifact => artifact.Role == HVO.SkyMonitor.AgentCore.FrameArtifactRole.Raw);
        var rawPreviewPath = $"/api/v1/operations/artifacts/{rawArtifact.ArtifactId:D}/preview";
        await rawStage.ClickAsync().ConfigureAwait(false);
        await page.WaitForFunctionAsync("""
            path => {
                const image = document.querySelector('.sky-image-stage img');
                return document.querySelector('.stage-switcher button[title="Show Raw image"]')?.getAttribute('aria-pressed') === 'true' &&
                    image?.complete && image.naturalWidth > 0 && new URL(image.src).pathname === path;
            }
            """, rawPreviewPath).ConfigureAwait(false);
        var selectedSource = await image.GetAttributeAsync("src").ConfigureAwait(false);
        Assert.IsNotNull(selectedSource);
        Assert.AreEqual(rawPreviewPath, new Uri(new Uri(page.Url), selectedSource).AbsolutePath);
        Assert.IsNotNull(capture.Detail?.Layout);
        Assert.AreEqual(capture.Detail.Layout.Width, await image.EvaluateAsync<int>("image => image.naturalWidth").ConfigureAwait(false));
        Assert.AreEqual(capture.Detail.Layout.Height, await image.EvaluateAsync<int>("image => image.naturalHeight").ConfigureAwait(false));
        Assert.AreEqual("true", await rawStage.GetAttributeAsync("aria-pressed").ConfigureAwait(false));

        var trigger = page.Locator("#current-sky-view-large");
        var figure = page.Locator("figure.sky-figure");
        await trigger.ClickAsync().ConfigureAwait(false);
        await page.WaitForFunctionAsync("() => document.fullscreenElement === document.querySelector('figure.sky-figure')")
            .ConfigureAwait(false);
        Assert.AreEqual(selectedSource, await figure.Locator("img").GetAttributeAsync("src").ConfigureAwait(false));
        foreach (var viewport in new[]
        {
            new ViewportSize { Width = 390, Height = 844 },
            new ViewportSize { Width = 844, Height = 390 }
        })
        {
            await page.EvaluateAsync("() => document.exitFullscreen()").ConfigureAwait(false);
            await page.WaitForFunctionAsync("() => document.fullscreenElement === null").ConfigureAwait(false);
            await page.SetViewportSizeAsync(viewport.Width, viewport.Height).ConfigureAwait(false);
            await trigger.ClickAsync().ConfigureAwait(false);
            await page.WaitForFunctionAsync("() => document.fullscreenElement === document.querySelector('figure.sky-figure')")
                .ConfigureAwait(false);
            await VisibleAsync(figure).ConfigureAwait(false);
            Assert.IsTrue(await figure.EvaluateAsync<bool>("element => document.fullscreenElement === element")
                .ConfigureAwait(false));
            Assert.AreEqual(selectedSource, await figure.Locator("img").GetAttributeAsync("src").ConfigureAwait(false));
            Assert.AreEqual("contain", await image.EvaluateAsync<string>("image => getComputedStyle(image).objectFit")
                .ConfigureAwait(false));
        }
        // Exit the native browser surface, not the retired large-image dialog.
        await page.EvaluateAsync("() => document.exitFullscreen()").ConfigureAwait(false);
        await page.WaitForFunctionAsync("() => document.fullscreenElement === null").ConfigureAwait(false);
        await WaitForFocusAsync(page, trigger).ConfigureAwait(false);
        await page.Keyboard.PressAsync("Enter").ConfigureAwait(false);
        await page.WaitForFunctionAsync("() => document.fullscreenElement === document.querySelector('figure.sky-figure')")
            .ConfigureAwait(false);
        Assert.AreEqual(selectedSource, await figure.Locator("img").GetAttributeAsync("src").ConfigureAwait(false));
        await page.GetByRole(AriaRole.Link, new() { Name = "Technical evidence and downloads", Exact = true })
            .ClickAsync().ConfigureAwait(false);
        await page.WaitForFunctionAsync("() => document.fullscreenElement === null").ConfigureAwait(false);
        await page.WaitForFunctionAsync("() => document.querySelector('#technical-evidence')?.open === true && document.activeElement === document.querySelector('#technical-evidence > summary')")
            .ConfigureAwait(false);
        Assert.AreEqual(archivedPathAndQuery, new Uri(page.Url).PathAndQuery);
        await page.Locator("#technical-evidence > summary").ClickAsync().ConfigureAwait(false);

        foreach (var viewport in new[]
        {
            new ViewportSize { Width = 1440, Height = 900 },
            new ViewportSize { Width = 820, Height = 1180 },
            new ViewportSize { Width = 390, Height = 844 },
            new ViewportSize { Width = 844, Height = 390 },
            new ViewportSize { Width = 320, Height = 700 }
        })
        {
            await page.SetViewportSizeAsync(viewport.Width, viewport.Height).ConfigureAwait(false);
            await WaitForResponsiveShellLayoutAsync(page, viewport.Width).ConfigureAwait(false);
            var overflow = await page.EvaluateAsync<string>(
                """
                () => {
                  const root = document.documentElement;
                  if (root.scrollWidth <= root.clientWidth + 1) return '';
                  const describe = selector => {
                    const element = document.querySelector(selector);
                    if (!element) return `${selector}=missing`;
                    const rect = element.getBoundingClientRect();
                    const style = getComputedStyle(element);
                    return `${selector}=[${rect.left.toFixed(1)},${rect.right.toFixed(1)}]` +
                      ` width=${style.width} min=${style.minWidth} padding=${style.paddingInline}` +
                      ` grid=${style.gridTemplateColumns}`;
                  };
                  const offenders = [...document.querySelectorAll('*')]
                    .map(element => ({ element, rect: element.getBoundingClientRect() }))
                    .filter(item => item.rect.right > root.clientWidth + 1 || item.rect.left < -1)
                    .slice(0, 8)
                    .map(item => `${item.element.tagName.toLowerCase()}.${item.element.className || ''} ` +
                      `[${item.rect.left.toFixed(1)},${item.rect.right.toFixed(1)}]`);
                  const structure = [
                    'body', '.app-frame', '.shell-header', '.shell-header__bar',
                    '.shell-brand', '.shell-menu', 'button[aria-controls="shell-menu-panel"]'
                  ].map(describe);
                  return `viewport inner=${innerWidth} outer=${outerWidth} visual=${visualViewport?.width} ` +
                    `mobile=${matchMedia('(max-width: 780px)').matches}; ` +
                    `document ${root.clientWidth}/${root.scrollWidth}; ${structure.join('; ')}; ` +
                    `offenders: ${offenders.join('; ')}`;
                }
                """).ConfigureAwait(false);
            Assert.AreEqual(string.Empty, overflow, $"capture detail overflowed at {viewport.Width}x{viewport.Height}");
            Assert.AreEqual("contain", await image
                .EvaluateAsync<string>("image => getComputedStyle(image).objectFit").ConfigureAwait(false));
            Assert.IsTrue(await image.EvaluateAsync<bool>("""
                image => {
                    const bounds = image.getBoundingClientRect();
                    const container = image.closest('.capture-image').getBoundingClientRect();
                    return bounds.top >= container.top - 1 && bounds.bottom <= container.bottom + 1 &&
                        bounds.left >= container.left - 1 && bounds.right <= container.right + 1;
                }
                """).ConfigureAwait(false), $"The complete raw image must fit at {viewport.Width}x{viewport.Height}.");
            Assert.AreEqual(selectedSource, await image.GetAttributeAsync("src").ConfigureAwait(false));
            await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = $"Capture #{capture.CaptureSequence}", Level = 1, Exact = true }))
                .ConfigureAwait(false);
        }

        Assert.AreEqual(2, await page.Locator(".capture-navigation__control[href]").CountAsync().ConfigureAwait(false));
        await CollapsibleSection.EnsureOpenAsync(
            page.Locator("#technical-evidence"),
            page.GetByRole(AriaRole.Heading, new() { Name = "Artifacts", Level = 2 })).ConfigureAwait(false);
        await VisibleAsync(page.Locator("#technical-evidence").GetByText(capture.CaptureId.ToString("D"), new() { Exact = true }))
            .ConfigureAwait(false);
        Assert.IsGreaterThan(0, await page.Locator("#technical-evidence a[download]").CountAsync().ConfigureAwait(false));
        await page.GotoAsync($"/gallery/{Guid.NewGuid():D}").ConfigureAwait(false);
        await VisibleAsync(page.GetByText("Capture unavailable", new() { Exact = true })).ConfigureAwait(false);

        await using (var anonymous = await diagnostics.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString()
        }).ConfigureAwait(false))
        {
            var anonymousPage = await anonymous.NewPageAsync().ConfigureAwait(false);
            await anonymousPage.GotoAsync(detailUrl).ConfigureAwait(false);
            await anonymousPage.WaitForURLAsync(url => url.Contains("/Account/Login", StringComparison.OrdinalIgnoreCase) &&
                url.Contains("returnUrl=", StringComparison.OrdinalIgnoreCase)).ConfigureAwait(false);
        }
        await using (var nonOwner = await diagnostics.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString()
        }).ConfigureAwait(false))
        {
            var nonOwnerPage = await nonOwner.NewPageAsync().ConfigureAwait(false);
            await LoginAsync(nonOwnerPage, CameraAgentKestrelFixture.NonOwnerEmail, CameraAgentKestrelFixture.NonOwnerPassword)
                .ConfigureAwait(false);
            await nonOwnerPage.GotoAsync(detailUrl).ConfigureAwait(false);
            await nonOwnerPage.WaitForURLAsync(url => url.Contains("/Account/AccessDenied", StringComparison.OrdinalIgnoreCase))
                .ConfigureAwait(false);
        }

        Assert.IsEmpty(browserErrors, string.Join(Environment.NewLine, browserErrors));
        Assert.IsEmpty(previewFailures, string.Join(Environment.NewLine, previewFailures));
        await diagnostics.CompleteAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CalibrationAcquisitionActivationAndRollbackAsync()
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await playwright.EnsureLaunchableOrInconclusiveAsync().ConfigureAwait(false);

        await using var host = await CameraAgentKestrelFixture.CreateAsync(
            useCalibrationLibrary: true).ConfigureAwait(false);
        var acquisition = host.Services.GetRequiredService<VirtualCalibrationAcquisitionCoordinator>();
        var operations = host.Services.GetRequiredService<CalibrationLibraryOperationsCoordinator>();
        var effectiveFrom = DateTimeOffset.UtcNow.AddDays(-1).ToUniversalTime();
        var first = await acquisition.AcquireAsync(
            new VirtualCalibrationAcquisitionRequestV1(
                VirtualCalibrationAcquisitionRequestV1.CurrentSchemaVersion,
                "browser-calibration-first",
                82,
                1,
                -10,
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromSeconds(5),
                effectiveFrom,
                effectiveFrom.AddYears(1),
                new VirtualCalibrationSourceModelV1 { Seed = 675 },
                "browser-owner",
                "browser rollback target"),
            CancellationToken.None).ConfigureAwait(false);
        _ = await operations.ActivateAsync(
            first.BundleId!,
            "browser-calibration-first-activate",
            0,
            "browser-owner",
            "browser rollback target",
            CancellationToken.None).ConfigureAwait(false);

        await using var browser = await playwright.LaunchOrInconclusiveAsync().ConfigureAwait(false);
        await using var diagnostics = new PlaywrightDiagnostics(browser, TestContext);
        await using var context = await diagnostics.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString(),
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            ColorScheme = ColorScheme.Dark,
            ReducedMotion = ReducedMotion.Reduce
        }).ConfigureAwait(false);
        context.SetDefaultTimeout(DefaultTimeoutMilliseconds);
        context.SetDefaultNavigationTimeout(DefaultTimeoutMilliseconds);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        var browserErrors = new List<string>();
        page.PageError += (_, error) => browserErrors.Add(error);
        await LoginAsync(
            page,
            CameraAgentKestrelFixture.OwnerEmail,
            CameraAgentKestrelFixture.OwnerPassword).ConfigureAwait(false);
        await page.GotoAsync("/calibration").ConfigureAwait(false);
        await VisibleAsync(page.GetByText(first.BundleId!, new() { Exact = true }).First).ConfigureAwait(false);
        var activeBundle = page.Locator(".calibration-card--active h2");
        Assert.AreEqual(first.BundleId, (await activeBundle.InnerTextAsync().ConfigureAwait(false)).Trim());
        var reviewAcquisition = page.GetByRole(AriaRole.Button, new() { Name = "Review acquisition" });
        var confirmation = page.Locator("dialog.confirmation-panel");
        await OpenDialogAsync(reviewAcquisition, confirmation).ConfigureAwait(false);
        Assert.IsEmpty(browserErrors, string.Join(Environment.NewLine, browserErrors));
        await WaitForContainedFocusAsync(page, confirmation).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm", Exact = true }).ClickAsync().ConfigureAwait(false);
        var resultBanner = page.Locator(".calibration-banner");
        try
        {
            await VisibleAsync(resultBanner, 180_000).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Assert.Fail(string.Join(
                Environment.NewLine,
                [.. browserErrors, await page.Locator(".calibration-console").InnerTextAsync().ConfigureAwait(false)]));
        }
        Assert.AreEqual(
            "Calibration acquisition published durably.",
            (await resultBanner.InnerTextAsync().ConfigureAwait(false)).Trim());

        var activate = page.GetByRole(AriaRole.Button, new() { Name = "Review activate" });
        await VisibleAsync(activate).ConfigureAwait(false);
        await activate.ClickAsync().ConfigureAwait(false);
        await VisibleAsync(confirmation).ConfigureAwait(false);
        await WaitForContainedFocusAsync(page, confirmation).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm", Exact = true }).ClickAsync().ConfigureAwait(false);
        await page.WaitForFunctionAsync(
            "prior => document.querySelector('.calibration-card--active h2')?.textContent?.trim() !== prior",
            first.BundleId).ConfigureAwait(false);
        var secondBundleId = (await activeBundle.InnerTextAsync().ConfigureAwait(false)).Trim();
        Assert.AreNotEqual(first.BundleId, secondBundleId);
        await VisibleAsync(page.GetByText("Calibration command completed durably.", new() { Exact = true }))
            .ConfigureAwait(false);

        var rollback = page.GetByRole(AriaRole.Button, new() { Name = "Review rollback" });
        await VisibleAsync(rollback).ConfigureAwait(false);
        await rollback.ClickAsync().ConfigureAwait(false);
        await VisibleAsync(confirmation).ConfigureAwait(false);
        await WaitForContainedFocusAsync(page, confirmation).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm", Exact = true }).ClickAsync().ConfigureAwait(false);
        await page.WaitForFunctionAsync(
            "expected => document.querySelector('.calibration-card--active h2')?.textContent?.trim() === expected",
            first.BundleId).ConfigureAwait(false);
        await VisibleAsync(page.GetByText("Calibration command completed durably.", new() { Exact = true }))
            .ConfigureAwait(false);
        Assert.AreEqual(first.BundleId, (await activeBundle.InnerTextAsync().ConfigureAwait(false)).Trim());
        Assert.IsEmpty(browserErrors, string.Join(Environment.NewLine, browserErrors));
        await diagnostics.CompleteAsync().ConfigureAwait(false);
    }

    private static async Task AssertAnonymousAndNonOwnerAuthorizationAsync(PlaywrightDiagnostics diagnostics, Uri baseAddress)
    {
        await using (var anonymous = await diagnostics.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = baseAddress.ToString()
        }).ConfigureAwait(false))
        {
            var page = await anonymous.NewPageAsync().ConfigureAwait(false);
            await page.GotoAsync("/").ConfigureAwait(false);
            await page.WaitForURLAsync(url => url.Contains("/Account/Login", StringComparison.OrdinalIgnoreCase) &&
                url.Contains("returnUrl=", StringComparison.OrdinalIgnoreCase)).ConfigureAwait(false);
            Assert.AreEqual("Log in", await page.GetByRole(AriaRole.Heading, new() { Level = 1 }).InnerTextAsync().ConfigureAwait(false));

            var response = await anonymous.APIRequest.GetAsync("/api/v1/operations/gallery").ConfigureAwait(false);
            try
            {
                Assert.AreEqual(401, response.Status);
            }
            finally
            {
                await response.DisposeAsync().ConfigureAwait(false);
            }
        }

        await using var nonOwner = await diagnostics.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = baseAddress.ToString()
        }).ConfigureAwait(false);
        var nonOwnerPage = await nonOwner.NewPageAsync().ConfigureAwait(false);
        await LoginAsync(
            nonOwnerPage,
            CameraAgentKestrelFixture.NonOwnerEmail,
            CameraAgentKestrelFixture.NonOwnerPassword).ConfigureAwait(false);
        await nonOwnerPage.GotoAsync("/").ConfigureAwait(false);
        await nonOwnerPage.WaitForURLAsync(url => url.Contains("/Account/AccessDenied", StringComparison.OrdinalIgnoreCase))
            .ConfigureAwait(false);
        await nonOwnerPage.GotoAsync("/gallery").ConfigureAwait(false);
        await nonOwnerPage.WaitForURLAsync(url => url.Contains("/Account/AccessDenied", StringComparison.OrdinalIgnoreCase))
            .ConfigureAwait(false);
        Assert.AreEqual(
            "Access denied",
            await nonOwnerPage.GetByRole(AriaRole.Heading, new() { Level = 1 }).InnerTextAsync().ConfigureAwait(false));
        await nonOwnerPage.GotoAsync("/schedule").ConfigureAwait(false);
        await nonOwnerPage.WaitForURLAsync(url => url.Contains("/Account/AccessDenied", StringComparison.OrdinalIgnoreCase))
            .ConfigureAwait(false);
        await nonOwnerPage.GotoAsync("/calibration").ConfigureAwait(false);
        await nonOwnerPage.WaitForURLAsync(url => url.Contains("/Account/AccessDenied", StringComparison.OrdinalIgnoreCase))
            .ConfigureAwait(false);
    }

    private static async Task AssertCalibrationAsync(IPage page)
    {
        await page.GotoAsync("/calibration").ConfigureAwait(false);
        var heading = page.GetByRole(AriaRole.Heading, new() { Name = "Calibration library", Level = 1 });
        await VisibleAsync(heading).ConfigureAwait(false);
        await VisibleAsync(page.GetByText("No active bundle", new() { Exact = true })).ConfigureAwait(false);
        Assert.AreEqual("82", await page.GetByLabel("Gain", new() { Exact = true }).InputValueAsync().ConfigureAwait(false));
        Assert.AreEqual("1", await page.GetByLabel("Effective offset").InputValueAsync().ConfigureAwait(false));
        Assert.AreEqual("-10", await page.GetByLabel("Camera temperature C").InputValueAsync().ConfigureAwait(false));
        Assert.AreEqual("10", await page.GetByLabel("Dark exposure seconds").InputValueAsync().ConfigureAwait(false));
        Assert.AreEqual("5", await page.GetByLabel("Exact light exposure seconds").InputValueAsync().ConfigureAwait(false));

        await page.GetByRole(AriaRole.Button, new() { Name = "Review acquisition" }).ClickAsync().ConfigureAwait(false);
        var confirmation = page.Locator("dialog.confirmation-panel");
        await VisibleAsync(confirmation).ConfigureAwait(false);
        await page.WaitForFunctionAsync(
            "element => element.contains(document.activeElement)", await confirmation.ElementHandleAsync().ConfigureAwait(false))
            .ConfigureAwait(false);
        await AssertDialogCancelIsSynchronouslyGuardedAsync(confirmation).ConfigureAwait(false);
        await page.WaitForFunctionAsync("() => document.activeElement?.id === 'start-calibration-acquisition'").ConfigureAwait(false);
    }

    private static async Task AssertSchedulePreviewAsync(IPage page)
    {
        await page.GotoAsync("/schedule").ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Capture schedule" })).ConfigureAwait(false);
        var activeHash = page.Locator(".schedule-card:has-text('Active immutable profile') code");
        var originalActiveHash = (await activeHash.InnerTextAsync().ConfigureAwait(false)).Trim();
        var preview = page.GetByRole(AriaRole.Button, new() { Name = "Validate and preview" });
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Desired and effective graph" }))
            .ConfigureAwait(false);
        var desiredGraph = page.Locator(".pipeline-columns article").First;
        var telemetryToggle = desiredGraph.Locator("li:has(strong:text-is('Telemetry'))")
            .GetByRole(AriaRole.Button, new() { Name = "Disable" });
        var graphUpdated = page.GetByText(
            "Desired graph updated in the editor. Save the immutable draft to persist it.",
            new() { Exact = true });
        await ClickAndWaitForVisibleAsync(telemetryToggle, graphUpdated).ConfigureAwait(false);
        await desiredGraph.Locator("li:has(strong:text-is('Telemetry'))")
            .GetByRole(AriaRole.Button, new() { Name = "Enable" }).ClickAsync().ConfigureAwait(false);
        await desiredGraph.Locator("li:has(strong:text-is('Calibration'))")
            .GetByRole(AriaRole.Button, new() { Name = "Disable" }).ClickAsync().ConfigureAwait(false);
        var graphAlert = page.Locator(".schedule-banner[role='alert']");
        await VisibleAsync(graphAlert).ConfigureAwait(false);
        StringAssert.Contains(
            await graphAlert.InnerTextAsync().ConfigureAwait(false),
            "depends on the disabled node",
            StringComparison.OrdinalIgnoreCase);
        await preview.ClickAsync().ConfigureAwait(false);
        await VisibleAsync(page.GetByText(
            "Schedule and desired graph previews are valid. No durable state changed.",
            new() { Exact = true }))
            .ConfigureAwait(false);
        var previewTimes = page.Locator(".preview-card time");
        Assert.IsGreaterThanOrEqualTo(2, await previewTimes.CountAsync().ConfigureAwait(false));
        Assert.IsTrue(await previewTimes.EvaluateAllAsync<bool>(
            "elements => elements.every(element => Boolean(element.getAttribute('datetime')))").ConfigureAwait(false));

        var editor = page.GetByLabel("Local profile JSON");
        await CollapsibleSection.EnsureOpenAsync(page.Locator("details.editor-advanced"), editor)
            .ConfigureAwait(false);
        var candidate = await editor.EvaluateAsync<string>("""
            element => {
                const profile = JSON.parse(element.value);
                profile.schedule.setpointProfiles[0].gain += 0.125;
                return JSON.stringify(profile, null, 2);
            }
            """).ConfigureAwait(false);
        await editor.FillAsync(candidate).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Save immutable draft" }).ClickAsync().ConfigureAwait(false);
        await VisibleAsync(page.GetByText("Draft saved.", new() { Exact = true })).ConfigureAwait(false);
        var reviewApply = page.GetByLabel("Current schedule state")
            .GetByRole(AriaRole.Button, new() { Name = "Review apply" });
        await reviewApply.ClickAsync().ConfigureAwait(false);
        var confirmation = page.Locator("dialog.confirmation-panel");
        await VisibleAsync(confirmation).ConfigureAwait(false);
        Assert.AreEqual("Apply revision?", await confirmation.GetAttributeAsync("aria-labelledby").ConfigureAwait(false) is { } labelId
            ? await page.Locator($"#{labelId}").InnerTextAsync().ConfigureAwait(false)
            : null);
        await page.WaitForFunctionAsync(
            "element => element.contains(document.activeElement)", await confirmation.ElementHandleAsync().ConfigureAwait(false))
            .ConfigureAwait(false);
        await AssertDialogCancelIsSynchronouslyGuardedAsync(confirmation).ConfigureAwait(false);
        await page.WaitForFunctionAsync(
            "element => element === document.activeElement", await reviewApply.ElementHandleAsync().ConfigureAwait(false))
            .ConfigureAwait(false);
        await reviewApply.ClickAsync().ConfigureAwait(false);
        await VisibleAsync(confirmation).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm apply" }).ClickAsync().ConfigureAwait(false);
        await confirmation.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden }).ConfigureAwait(false);
        await page.WaitForFunctionAsync("() => document.activeElement?.id === 'schedule-heading'").ConfigureAwait(false);
        await VisibleAsync(page.GetByText("No draft", new() { Exact = true })).ConfigureAwait(false);
        Assert.AreNotEqual(originalActiveHash, (await activeHash.InnerTextAsync().ConfigureAwait(false)).Trim());

        var overrideStart = DateTimeOffset.UtcNow.AddMinutes(10).ToString("O", CultureInfo.InvariantCulture);
        var overrideEnd = DateTimeOffset.UtcNow.AddMinutes(20).ToString("O", CultureInfo.InvariantCulture);
        await page.GetByLabel("Start UTC").FillAsync(overrideStart).ConfigureAwait(false);
        await page.GetByLabel("End UTC").FillAsync(overrideEnd).ConfigureAwait(false);
        var scheduleBanner = page.Locator(".schedule-banner");
        var priorMessage = await scheduleBanner.InnerTextAsync().ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create durable override" }).ClickAsync().ConfigureAwait(false);
        await page.WaitForFunctionAsync(
            """
            previous => {
                const banner = document.querySelector('.schedule-banner');
                return banner && banner.textContent.trim() !== previous.trim();
            }
            """,
            priorMessage).ConfigureAwait(false);
        Assert.AreEqual("Override created.", (await scheduleBanner.InnerTextAsync().ConfigureAwait(false)).Trim());
        await page.GetByRole(AriaRole.Button, new() { Name = "Clear", Exact = true }).ClickAsync().ConfigureAwait(false);
        await VisibleAsync(page.GetByText("Override cleared.", new() { Exact = true })).ConfigureAwait(false);

        await page.GetByRole(AriaRole.Button, new() { Name = "Review rollback" }).First.ClickAsync().ConfigureAwait(false);
        await VisibleAsync(confirmation).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm rollback" }).ClickAsync().ConfigureAwait(false);
        await confirmation.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden }).ConfigureAwait(false);
        await page.WaitForFunctionAsync(
            """
            expected => [...document.querySelectorAll('.schedule-card')]
                .find(card => card.textContent.includes('Active immutable profile'))
                ?.querySelector('code')?.textContent.trim() === expected
            """,
            originalActiveHash).ConfigureAwait(false);
        await VisibleAsync(page.GetByText("Revision applied at the capture boundary.", new() { Exact = true }))
            .ConfigureAwait(false);

        await editor.FillAsync("{}").ConfigureAwait(false);
        await preview.ClickAsync().ConfigureAwait(false);
        var alert = page.Locator(".schedule-banner[role='alert']");
        await VisibleAsync(alert).ConfigureAwait(false);
        Assert.AreEqual(0, await page.Locator(".preview-card time").CountAsync().ConfigureAwait(false));
        Assert.IsTrue((await alert.InnerTextAsync().ConfigureAwait(false)).Contains(
            "invalid", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<LifecycleContinuity> ReadLifecycleContinuityAsync(HttpClient client)
    {
        using var response = await client.GetAsync(
            new Uri("/api/internal/deployment/lifecycle/state", UriKind.Relative)).ConfigureAwait(false);
        var body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        Assert.AreEqual(200, (int)response.StatusCode, System.Text.Encoding.UTF8.GetString(body));
        using var json = JsonDocument.Parse(body);
        return new LifecycleContinuity(
            json.RootElement.GetProperty("captureControl").GetProperty("value").GetProperty("state").GetString()
                ?? string.Empty,
            json.RootElement.GetProperty("captureSequence").GetInt64());
    }

    private static byte[] CreateOwnerRecoveryAttestationProof(
        string token,
        Guid operationId,
        ReadOnlySpan<byte> nonce)
    {
        var prefix = Encoding.UTF8.GetBytes($"{OwnerRecoveryAttestationPurpose}\n{operationId:D}\n");
        var payload = new byte[prefix.Length + nonce.Length];
        prefix.CopyTo(payload, 0);
        nonce.CopyTo(payload.AsSpan(prefix.Length));
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), payload);
    }

    private static HttpRequestMessage CreateOwnerRecoveryRequest(string path, Guid operationId, byte[] payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-HVO-Recovery-Operation", operationId.ToString("D"));
        request.Content = new ByteArrayContent(payload);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        return request;
    }

    private static byte[] CreateOwnerRecoveryCompletionPayload(string challenge, string password)
    {
        var challengeBytes = Encoding.UTF8.GetBytes(challenge);
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var payload = new byte[9 + challengeBytes.Length + passwordBytes.Length];
        payload[0] = 1;
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(1, 4), challengeBytes.Length);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(5, 4), passwordBytes.Length);
        challengeBytes.CopyTo(payload, 9);
        passwordBytes.CopyTo(payload, 9 + challengeBytes.Length);
        return payload;
    }

    [TestMethod]
    [TestCategory("Manual")]
    public async Task OwnerObserverCoordinateEditIsConfirmedAndKeyboardCancellableAsync()
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await playwright.EnsureLaunchableOrInconclusiveAsync().ConfigureAwait(false);

        await using var host = await CameraAgentKestrelFixture.CreateAsync().ConfigureAwait(false);
        await using var browser = await playwright.LaunchOrInconclusiveAsync().ConfigureAwait(false);
        await using var diagnostics = new PlaywrightDiagnostics(browser, TestContext);
        await using var context = await diagnostics.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString(),
            ViewportSize = new ViewportSize { Width = 1280, Height = 720 }
        }).ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        await LoginAsync(page, CameraAgentKestrelFixture.OwnerEmail, CameraAgentKestrelFixture.OwnerPassword)
            .ConfigureAwait(false);

        await page.GotoAsync("/operations/sky-map").ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Sky map & catalog", Level = 1 }))
            .ConfigureAwait(false);
        await VisibleAsync(page.Locator("#sky-map-latitude")).ConfigureAwait(false);

        // The page states which captures keep which version before anything durable is asked for.
        var edit = page.Locator("#sky-map-edit-coordinates");
        await VisibleAsync(edit).ConfigureAwait(false);
        var seeded = await page.Locator("#sky-map-latitude").InputValueAsync().ConfigureAwait(false);
        Assert.IsFalse(string.IsNullOrWhiteSpace(seeded), "The entry fields must be seeded from durable state.");
        await page.Locator("#sky-map-latitude").FillAsync("31.500000").ConfigureAwait(false);

        var dialog = page.Locator("dialog.confirmation-panel");
        await OpenDialogAsync(edit, dialog).ConfigureAwait(false);
        Assert.IsTrue(await page.EvaluateAsync<bool>(
            "() => document.querySelector('dialog.confirmation-panel')?.contains(document.activeElement) === true")
            .ConfigureAwait(false), "Opening the confirmation must move focus inside it.");
        StringAssert.Contains(
            await dialog.InnerTextAsync().ConfigureAwait(false),
            "keep version",
            StringComparison.Ordinal);

        // Escape is guarded, so it cancels through the component and returns focus to the trigger.
        await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden })
            .ConfigureAwait(false);
        Assert.AreEqual(
            "sky-map-edit-coordinates",
            await page.EvaluateAsync<string>("() => document.activeElement?.id || ''").ConfigureAwait(false));

        // Cancelling leaves the durable deployment version untouched.
        await page.ReloadAsync().ConfigureAwait(false);
        await VisibleAsync(page.Locator("#sky-map-latitude")).ConfigureAwait(false);
        StringAssert.Contains(
            await page.Locator("article[aria-labelledby='sky-observer-edit']").InnerTextAsync().ConfigureAwait(false),
            "No manual coordinate change has been recorded",
            StringComparison.Ordinal);
        await diagnostics.CompleteAsync().ConfigureAwait(false);
    }

    [TestMethod]
    [TestCategory("Manual")]
    public async Task OwnerLocalAutomationSectionRefusesAnUnregisteredTaskTargetAsync()
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await playwright.EnsureLaunchableOrInconclusiveAsync().ConfigureAwait(false);

        await using var host = await CameraAgentKestrelFixture.CreateAsync().ConfigureAwait(false);
        await using var browser = await playwright.LaunchOrInconclusiveAsync().ConfigureAwait(false);
        await using var diagnostics = new PlaywrightDiagnostics(browser, TestContext);
        await using var context = await diagnostics.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString(),
            ViewportSize = new ViewportSize { Width = 1280, Height = 720 }
        }).ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        await LoginAsync(page, CameraAgentKestrelFixture.OwnerEmail, CameraAgentKestrelFixture.OwnerPassword)
            .ConfigureAwait(false);

        await page.GotoAsync("/operations/automations").ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Automations", Level = 1 }))
            .ConfigureAwait(false);
        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
        await VisibleAsync(page.Locator("#automation-definitions")).ConfigureAwait(false);
        await VisibleAsync(page.Locator("#automation-calendar")).ConfigureAwait(false);
        await VisibleAsync(page.Locator("#automation-runs")).ConfigureAwait(false);

        // Only registered targets and triggers can be chosen; the editor never accepts free text for one.
        Assert.AreEqual(
            "SELECT",
            await page.Locator("#automation-target").EvaluateAsync<string>("node => node.tagName")
                .ConfigureAwait(false));
        Assert.AreEqual(
            "SELECT",
            await page.Locator("#automation-trigger").EvaluateAsync<string>("node => node.tagName")
                .ConfigureAwait(false));

        // The two trigger vocabularies on this page are disambiguated for the operator.
        StringAssert.Contains(
            await page.Locator("article[aria-labelledby='automation-triggers']").InnerTextAsync()
                .ConfigureAwait(false),
            "their own separate trigger vocabulary",
            StringComparison.Ordinal);

        // This fixture disables environmental acquisition, so the closed registry publishes no target.
        // The section must say so and refuse, rather than raising a confirmation for a command the store
        // would reject. Recording a definition end to end is proved where a target exists, by the bUnit
        // page tests, the endpoint tests, and the owner-authorization integration test.
        var definitions = page.Locator("article[aria-labelledby='automation-definitions']");
        StringAssert.Contains(
            await definitions.InnerTextAsync().ConfigureAwait(false),
            "Environmental acquisition is disabled in this CameraAgent's startup configuration",
            StringComparison.Ordinal);
        Assert.AreEqual(
            1,
            await page.Locator("#automation-target option").CountAsync().ConfigureAwait(false),
            "An unavailable task must offer no target to choose.");

        await page.Locator("#automation-id").FillAsync("browser-acceptance").ConfigureAwait(false);
        await page.Locator("#automation-name").FillAsync("Browser acceptance").ConfigureAwait(false);
        await page.Locator("#automation-save").ClickAsync().ConfigureAwait(false);
        await page.Locator("p.automation-error").WaitForAsync().ConfigureAwait(false);
        Assert.AreEqual(
            0,
            await page.Locator("dialog.confirmation-panel").CountAsync().ConfigureAwait(false),
            "No confirmation may be raised for a command that cannot be recorded.");
        StringAssert.Contains(
            await page.Locator("p.automation-error").InnerTextAsync().ConfigureAwait(false),
            "Select a registered task target",
            StringComparison.Ordinal);

        // Nothing durable was recorded.
        await page.ReloadAsync().ConfigureAwait(false);
        await VisibleAsync(page.Locator("#automation-definitions")).ConfigureAwait(false);
        StringAssert.Contains(
            await definitions.InnerTextAsync().ConfigureAwait(false),
            "No local automation is defined",
            StringComparison.Ordinal);
        StringAssert.Contains(
            await page.Locator("article[aria-labelledby='automation-runs']").InnerTextAsync()
                .ConfigureAwait(false),
            "No automation run has been recorded",
            StringComparison.Ordinal);
        await diagnostics.CompleteAsync().ConfigureAwait(false);
    }

    [TestMethod]
    [TestCategory("Manual")]
    public async Task OwnerLocalAutomationDefinitionIsConfirmedRecordedAndKeyboardCancellableAsync()
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await playwright.EnsureLaunchableOrInconclusiveAsync().ConfigureAwait(false);

        // This test opts into one on-demand-capable environmental source so the closed registry publishes
        // a target and a definition can actually be recorded through the operator's own surface.
        await using var host = await CameraAgentKestrelFixture.CreateAsync(useEnvironmentalAcquisition: true)
            .ConfigureAwait(false);
        await using var browser = await playwright.LaunchOrInconclusiveAsync().ConfigureAwait(false);
        await using var diagnostics = new PlaywrightDiagnostics(browser, TestContext);
        await using var context = await diagnostics.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString(),
            ViewportSize = new ViewportSize { Width = 1280, Height = 720 }
        }).ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        await LoginAsync(page, CameraAgentKestrelFixture.OwnerEmail, CameraAgentKestrelFixture.OwnerPassword)
            .ConfigureAwait(false);

        await page.GotoAsync("/operations/automations").ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Automations", Level = 1 }))
            .ConfigureAwait(false);
        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
        await VisibleAsync(page.Locator("#automation-definitions")).ConfigureAwait(false);
        StringAssert.Contains(
            await page.Locator("#automation-target").InnerTextAsync().ConfigureAwait(false),
            CameraAgentKestrelFixture.EnvironmentalSourceId,
            StringComparison.Ordinal);

        await page.Locator("#automation-id").FillAsync("browser-acceptance").ConfigureAwait(false);
        await page.Locator("#automation-name").FillAsync("Browser acceptance").ConfigureAwait(false);
        await page.Locator("#automation-reason").FillAsync("browser acceptance evidence").ConfigureAwait(false);
        await page.Locator("#automation-target")
            .SelectOptionAsync(CameraAgentKestrelFixture.EnvironmentalSourceId).ConfigureAwait(false);

        var save = page.Locator("#automation-save");
        var dialog = page.Locator("dialog.confirmation-panel");
        await OpenDialogAsync(save, dialog).ConfigureAwait(false);
        Assert.IsTrue(await page.EvaluateAsync<bool>(
            "() => document.querySelector('dialog.confirmation-panel')?.contains(document.activeElement) === true")
            .ConfigureAwait(false), "Opening the confirmation must move focus inside it.");
        StringAssert.Contains(
            await dialog.InnerTextAsync().ConfigureAwait(false),
            "immutable revision",
            StringComparison.Ordinal);

        // Escape is guarded, so it cancels through the component and returns focus to the trigger.
        await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden })
            .ConfigureAwait(false);
        Assert.AreEqual(
            "automation-save",
            await page.EvaluateAsync<string>("() => document.activeElement?.id || ''").ConfigureAwait(false));

        // Confirming records a durable revision the operator can then see survive a reload.
        await OpenDialogAsync(save, dialog).ConfigureAwait(false);
        await dialog.Locator(".btn-primary").ClickAsync().ConfigureAwait(false);
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden })
            .ConfigureAwait(false);
        await VisibleAsync(page.Locator("#automation-remove-browser-acceptance")).ConfigureAwait(false);

        await page.ReloadAsync().ConfigureAwait(false);
        await VisibleAsync(page.Locator("#automation-definitions")).ConfigureAwait(false);
        var recorded = await page.Locator("article[aria-labelledby='automation-definitions']").InnerTextAsync()
            .ConfigureAwait(false);
        StringAssert.Contains(recorded, "browser-acceptance", StringComparison.Ordinal);
        StringAssert.Contains(recorded, "Recorded revisions", StringComparison.Ordinal);

        // The retained revision history is collapsed by default; expanding it shows the recorded reason.
        var history = page.Locator("details.revision-history");
        await VisibleAsync(history).ConfigureAwait(false);
        await CollapsibleSection.EnsureOpenAsync(history, page.Locator("details.revision-history table"))
            .ConfigureAwait(false);
        StringAssert.Contains(
            await history.InnerTextAsync().ConfigureAwait(false),
            "browser acceptance evidence",
            StringComparison.Ordinal);
        StringAssert.Contains(
            await page.Locator("article[aria-labelledby='automation-calendar']").InnerTextAsync()
                .ConfigureAwait(false),
            "Browser acceptance",
            StringComparison.Ordinal);
        await diagnostics.CompleteAsync().ConfigureAwait(false);
    }

    private static async Task LoginAsync(IPage page, string email, string password)
    {
        await page.GotoAsync("/Account/Login").ConfigureAwait(false);
        await page.GetByLabel("Email").FillAsync(email).ConfigureAwait(false);
        await page.GetByLabel("Password").FillAsync(password).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync().ConfigureAwait(false);
        await page.WaitForURLAsync(url => !url.Contains("/Account/Login", StringComparison.OrdinalIgnoreCase))
            .ConfigureAwait(false);
    }

    private sealed record LifecycleContinuity(string CaptureState, long CaptureSequence);

    private static async Task SubmitPasswordReplacementAsync(IPage page, IBrowserContext context)
    {
        var form = page.Locator("form[action='/Account/ReplaceTemporaryPassword']");
        var isValid = await form.EvaluateAsync<bool>("form => form.checkValidity()").ConfigureAwait(false);
        if (!isValid)
        {
            Assert.Fail(await ReadPasswordReplacementDiagnosticsAsync(page, context, "browser-validation")
                .ConfigureAwait(false));
        }

        var responseTask = page.WaitForResponseAsync(response =>
            response.Request.Method == "POST" &&
            new Uri(response.Url).AbsolutePath == "/Account/ReplaceTemporaryPassword");
        try
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "Replace password" }).ClickAsync()
                .ConfigureAwait(false);
            var response = await responseTask.ConfigureAwait(false);
            response.Headers.TryGetValue("location", out var location);
            var locationPath = Uri.TryCreate(location, UriKind.Absolute, out var absoluteLocation)
                ? absoluteLocation.AbsolutePath
                : location;
            if (response.Status != 302 || locationPath != "/" || new Uri(page.Url).AbsolutePath != "/")
            {
                Assert.Fail(await ReadPasswordReplacementDiagnosticsAsync(
                    page,
                    context,
                    $"post-status-{response.Status}-location-{locationPath ?? "missing"}").ConfigureAwait(false));
            }
        }
        catch (TimeoutException)
        {
            Assert.Fail(await ReadPasswordReplacementDiagnosticsAsync(page, context, "post-not-observed")
                .ConfigureAwait(false));
        }
    }

    private static async Task<string> ReadPasswordReplacementDiagnosticsAsync(
        IPage page,
        IBrowserContext context,
        string phase)
    {
        var fields = page.Locator("input[type=password]");
        var fieldLengths = new List<int>();
        var fieldNames = new List<string>();
        var browserMessages = new List<string>();
        for (var index = 0; index < await fields.CountAsync().ConfigureAwait(false); index++)
        {
            var field = fields.Nth(index);
            fieldLengths.Add((await field.InputValueAsync().ConfigureAwait(false)).Length);
            fieldNames.Add(await field.GetAttributeAsync("name").ConfigureAwait(false) ?? "missing");
            browserMessages.Add(await field.EvaluateAsync<string>("input => input.validationMessage")
                .ConfigureAwait(false));
        }
        var renderedValidation = await page.Locator("[role=alert]").AllInnerTextsAsync().ConfigureAwait(false);
        var form = page.Locator("form[action='/Account/ReplaceTemporaryPassword']");
        var formState = await form.CountAsync().ConfigureAwait(false) == 1
            ? await form.EvaluateAsync<string>(
                "form => `method=${form.method},submitDisabled=${form.querySelector('button[type=submit]')?.disabled ?? 'missing'}`")
                .ConfigureAwait(false)
            : "form-missing";
        var status = await context.APIRequest.GetAsync("/api/internal/owner-bootstrap/status").ConfigureAwait(false);
        try
        {
            return string.Join(
                "; ",
                $"phase={phase}",
                $"path={new Uri(page.Url).AbsolutePath}",
                $"fieldNames={string.Join(',', fieldNames)}",
                $"fieldLengths={string.Join(',', fieldLengths)}",
                $"browserValidation={string.Join('|', browserMessages.Where(static value => value.Length > 0))}",
                $"renderedValidation={string.Join('|', renderedValidation)}",
                $"form={formState}",
                $"bootstrapStatusCode={status.Status}");
        }
        finally
        {
            await status.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task AssertOperationsAndCaptureControlAsync(IPage page)
    {
        await page.GotoAsync("/operations").ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Operations overview", Level = 1 }))
            .ConfigureAwait(false);
        await VisibleAsync(page.Locator("header.shell-header")).ConfigureAwait(false);
        await VisibleAsync(page.Locator("main#mainContent")).ConfigureAwait(false);
        await VisibleAsync(page.Locator(".heading-status .state-chip")).ConfigureAwait(false);
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            await page.Locator(".heading-status .state-chip").InnerTextAsync().ConfigureAwait(false)));

        var action = page.Locator("#capture-action");
        await VisibleAsync(action).ConfigureAwait(false);
        var dialog = page.Locator("dialog.confirmation");
        await OpenDialogAsync(action, dialog).ConfigureAwait(false);
        Assert.IsTrue(await dialog.EvaluateAsync<bool>("element => element.matches(':modal') && element.contains(document.activeElement)")
            .ConfigureAwait(false));
        await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden }).ConfigureAwait(false);
        Assert.AreEqual("capture-action", await page.EvaluateAsync<string>("() => document.activeElement?.id || ''").ConfigureAwait(false));

        await OpenDialogAsync(action, dialog).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm pause capture" }).ClickAsync().ConfigureAwait(false);
        await VisibleAsync(page.Locator(".receipt[role='status']")).ConfigureAwait(false);
        StringAssert.Contains(
            await page.Locator(".receipt[role='status']").InnerTextAsync().ConfigureAwait(false),
            "Current state: Paused",
            StringComparison.Ordinal);
        Assert.AreEqual("Review resume", await action.InnerTextAsync().ConfigureAwait(false));

        await OpenDialogAsync(action, dialog).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm resume capture" }).ClickAsync().ConfigureAwait(false);
        await VisibleAsync(page.Locator(".receipt[role='status']")).ConfigureAwait(false);
        StringAssert.Contains(
            await page.Locator(".receipt[role='status']").InnerTextAsync().ConfigureAwait(false),
            "Current state: Running",
            StringComparison.Ordinal);
    }

    private static async Task AssertCurrentSkyAsync(IPage page)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await page.GotoAsync("/").ConfigureAwait(false);
            await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Current sky", Level = 1 }))
                .ConfigureAwait(false);
            if (await page.Locator(".capture-image img").CountAsync().ConfigureAwait(false) > 0)
            {
                break;
            }
            await Task.Delay(500).ConfigureAwait(false);
        }

        var image = page.Locator(".capture-image img");
        await VisibleAsync(image).ConfigureAwait(false);
        Assert.IsTrue(await image.EvaluateAsync<bool>(
            "element => element.complete && element.naturalWidth > 0 && element.naturalHeight > 0").ConfigureAwait(false));
        // The selector re-renders while the first captures settle, so assert the settled shape in one evaluation.
        await page.WaitForFunctionAsync(
            """
            () => document.querySelectorAll('.stage-selector button').length === 4 &&
                document.querySelectorAll('.stage-selector button[aria-pressed=true]').length === 1 &&
                document.querySelectorAll('.stage-selector button:disabled').length > 0
            """)
            .ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Link, new() { Name = "Open capture details" })).ConfigureAwait(false);

        var trigger = page.Locator("#current-sky-view-large");
        var dialog = page.Locator("dialog.large-viewer");
        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
        await trigger.ClickAsync().ConfigureAwait(false);
        await VisibleAsync(dialog).ConfigureAwait(false);
        Assert.IsTrue(await dialog.Locator("img").EvaluateAsync<bool>(
            "element => element.complete && element.naturalWidth > 0").ConfigureAwait(false));
        var nativeSize = dialog.GetByRole(AriaRole.Button, new() { Name = "100%" });
        await nativeSize.ClickAsync().ConfigureAwait(false);
        await page.WaitForFunctionAsync(
            "element => element.getAttribute('aria-pressed') === 'true'",
            await nativeSize.ElementHandleAsync().ConfigureAwait(false)).ConfigureAwait(false);
        await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden }).ConfigureAwait(false);
        Assert.AreEqual(
            "current-sky-view-large",
            await page.EvaluateAsync<string>("() => document.activeElement?.id || ''").ConfigureAwait(false));
    }

    private static async Task<string> AssertGalleryAsync(
        IPage page,
        IBrowserContext ownerContext,
        CameraAgentKestrelFixture host)
    {
        await WaitForGalleryCapturesAsync(page, minimumCards: 24).ConfigureAwait(false);
        await AssertPageStructureAsync(page, "/gallery").ConfigureAwait(false);
        var evidenceBadge = page.Locator(".capture-card .evidence").First;
        await VisibleAsync(evidenceBadge).ConfigureAwait(false);
        Assert.AreEqual("Simulated evidence", await evidenceBadge.InnerTextAsync().ConfigureAwait(false));
        await VisibleAsync(page.GetByText("Older captures", new() { Exact = true })).ConfigureAwait(false);

        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
        var advanced = page.Locator(".advanced-filters");
        await CollapsibleSection.EnsureOpenAsync(advanced, page.GetByLabel("Evidence origin")).ConfigureAwait(false);
        await page.GetByLabel("Evidence origin").SelectOptionAsync("Simulated").ConfigureAwait(false);
        await page.GetByLabel("Page size").SelectOptionAsync("24").ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Apply" }).ClickAsync().ConfigureAwait(false);
        await page.WaitForURLAsync(url => new Uri(url).Query.Contains("origin=Simulated", StringComparison.Ordinal))
            .ConfigureAwait(false);
        await VisibleAsync(page.Locator(".capture-card").First).ConfigureAwait(false);
        await AssertVisibleImagesDecodeAsync(page).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Older captures" }).ClickAsync().ConfigureAwait(false);
        await page.WaitForURLAsync(url => new Uri(url).Query.Contains("origin=Simulated", StringComparison.Ordinal) &&
            new Uri(url).Query.Contains("cursor=", StringComparison.Ordinal)).ConfigureAwait(false);

        var detailLink = page.Locator(".capture-card a[aria-label^='Open capture']").First;
        var detailUrl = await detailLink.GetAttributeAsync("href").ConfigureAwait(false);
        Assert.IsNotNull(detailUrl);
        StringAssert.Contains(detailUrl, "returnUrl=", StringComparison.Ordinal);
        var capture = await ReadGalleryCaptureAsync(page, detailUrl).ConfigureAwait(false);
        await detailLink.ClickAsync().ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = $"Capture #{capture.CaptureSequence}", Level = 1, Exact = true }))
            .ConfigureAwait(false);
        await CollapsibleSection.EnsureOpenAsync(
            page.Locator("#technical-evidence"),
            page.GetByRole(AriaRole.Heading, new() { Name = "Artifacts", Level = 2 })).ConfigureAwait(false);

        var image = page.Locator(".sky-image-stage img");
        await VisibleAsync(image).ConfigureAwait(false);
        Assert.IsTrue(await image.EvaluateAsync<bool>("image => image.complete && image.naturalWidth > 0").ConfigureAwait(false));
        Assert.AreEqual("contain", await image.EvaluateAsync<string>("image => getComputedStyle(image).objectFit").ConfigureAwait(false));
        Assert.IsTrue(await image.EvaluateAsync<bool>("image => image.getBoundingClientRect().width <= innerWidth && image.getBoundingClientRect().height <= innerHeight")
            .ConfigureAwait(false));
        var comparisonImages = page.Locator(".comparison-grid img");
        await VisibleAsync(comparisonImages.First).ConfigureAwait(false);
        Assert.AreEqual(2, await comparisonImages.CountAsync().ConfigureAwait(false));
        await page.WaitForFunctionAsync(
            "() => [...document.querySelectorAll('.comparison-grid img')].length === 2 && " +
            "[...document.querySelectorAll('.comparison-grid img')].every(image => image.complete && image.naturalWidth > 0)")
            .ConfigureAwait(false);
        Assert.IsTrue(await comparisonImages.EvaluateAllAsync<bool>(
            "images => images.every(image => image.complete && image.naturalWidth > 0)").ConfigureAwait(false));
        Assert.AreNotEqual(
            await page.GetByLabel("Left artifact").InputValueAsync().ConfigureAwait(false),
            await page.GetByLabel("Right artifact").InputValueAsync().ConfigureAwait(false));

        var previewUrl = await image.GetAttributeAsync("src").ConfigureAwait(false);
        var contentUrl = await page.Locator("#technical-evidence a[download]").First.GetAttributeAsync("href").ConfigureAwait(false);
        Assert.IsNotNull(previewUrl);
        Assert.IsNotNull(contentUrl);
        await AssertCookieProtectedContentAsync(ownerContext, previewUrl, "image/jpeg").ConfigureAwait(false);
        await AssertCookieProtectedContentAsync(ownerContext, contentUrl, null).ConfigureAwait(false);

        var bodyText = await page.Locator("body").InnerTextAsync().ConfigureAwait(false);
        foreach (var prohibited in new[]
        {
            host.Root,
            CameraAgentKestrelFixture.OwnerPassword,
            CameraAgentKestrelFixture.NonOwnerPassword,
            "leaseToken",
            "clientSecret",
            "DataProtection-Keys"
        })
        {
            Assert.IsFalse(bodyText.Contains(prohibited, StringComparison.OrdinalIgnoreCase), prohibited);
        }

        await page.GetByRole(AriaRole.Link, new() { Name = "Back to gallery results", Exact = true }).ClickAsync().ConfigureAwait(false);
        await page.WaitForURLAsync(url => new Uri(url).Query.Contains("origin=Simulated", StringComparison.Ordinal) &&
            new Uri(url).Query.Contains("cursor=", StringComparison.Ordinal)).ConfigureAwait(false);
        return detailUrl;
    }

    private static async Task<CameraAgentGalleryCapture> ReadGalleryCaptureAsync(IPage page, string detailUrl)
    {
        var captureId = Guid.Parse(new Uri(new Uri(page.Url), detailUrl).Segments[^1]);
        var response = await page.Context.APIRequest.GetAsync($"/api/v1/operations/gallery/{captureId:D}").ConfigureAwait(false);
        try
        {
            Assert.AreEqual(200, response.Status);
            var capture = JsonSerializer.Deserialize<CameraAgentGalleryCapture>(
                await response.TextAsync().ConfigureAwait(false), JsonSerializerOptions.Web);
            Assert.IsNotNull(capture);
            Assert.AreEqual(captureId, capture.CaptureId);
            return capture;
        }
        finally
        {
            await response.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task AssertCookieProtectedContentAsync(
        IBrowserContext context,
        string relativeUrl,
        string? expectedContentType)
    {
        var response = await context.APIRequest.GetAsync(relativeUrl).ConfigureAwait(false);
        try
        {
            Assert.AreEqual(200, response.Status, relativeUrl);
            Assert.IsGreaterThan(0, (await response.BodyAsync().ConfigureAwait(false)).Length, relativeUrl);
            if (expectedContentType is not null)
            {
                Assert.IsTrue(response.Headers.TryGetValue("content-type", out var contentType));
                StringAssert.StartsWith(contentType, expectedContentType, StringComparison.OrdinalIgnoreCase);
            }
            Assert.IsTrue(response.Headers.TryGetValue("vary", out var vary));
            StringAssert.Contains(vary, "Cookie", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await response.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task AssertSystemNavigationAsync(IPage page)
    {
        await page.GotoAsync("/system").ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "System snapshot", Level = 1 }))
            .ConfigureAwait(false);
        await VisibleAsync(page.GetByText("Read-only system facts:", new() { Exact = true })).ConfigureAwait(false);
        await AssertPageStructureAsync(page, "/system").ConfigureAwait(false);
    }

    private static async Task AssertQuarantineAsync(IPage page)
    {
        await page.GotoAsync("/operations/quarantine?kind=Artifact").ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Quarantine", Level = 1 })).ConfigureAwait(false);
        var olderRecords = page.GetByRole(AriaRole.Button, new() { Name = "Older records" });
        await VisibleAsync(olderRecords).ConfigureAwait(false);
        if (await olderRecords.IsEnabledAsync().ConfigureAwait(false))
        {
            await olderRecords.ClickAsync().ConfigureAwait(false);
            await page.WaitForFunctionAsync("() => new URL(location.href).searchParams.has('cursor')").ConfigureAwait(false);
        }

        var action = page.GetByRole(AriaRole.Button, new() { Name = "Review abandon" }).First;
        await VisibleAsync(action).ConfigureAwait(false);
        var triggerId = await action.GetAttributeAsync("id").ConfigureAwait(false);
        var dialog = page.Locator("dialog.confirmation");
        await OpenDialogAsync(action, dialog).ConfigureAwait(false);
        Assert.IsTrue(await dialog.EvaluateAsync<bool>("element => element.matches(':modal') && element.contains(document.activeElement)")
            .ConfigureAwait(false));
        await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden }).ConfigureAwait(false);
        Assert.AreEqual(triggerId, await page.EvaluateAsync<string>("() => document.activeElement?.id || ''").ConfigureAwait(false));
        await OpenDialogAsync(action, dialog).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm abandon" }).ClickAsync().ConfigureAwait(false);
        await VisibleAsync(page.Locator(".receipt[role='status']")).ConfigureAwait(false);
    }

    private static async Task AssertResponsiveAndAccessibleAsync(IPage page, string detailUrl)
    {
        var capture = await ReadGalleryCaptureAsync(page, detailUrl).ConfigureAwait(false);
        var viewports = new[]
        {
            new ViewportSize { Width = 1440, Height = 900 },
            new ViewportSize { Width = 820, Height = 1180 },
            new ViewportSize { Width = 390, Height = 844 },
            new ViewportSize { Width = 844, Height = 390 },
            new ViewportSize { Width = 320, Height = 700 }
        };
        // Each route is paired with the level-1 heading that names it. Asserting only that some h1
        // exists is what let the three Manage routes share the constant "Account settings": presence
        // was satisfied while none of the three had a name of its own. The operations workspace walk
        // in StandaloneW6DockerAcceptanceTests already pairs routes with names; this is that form.
        var routes = new (string Route, string Heading)[]
        {
            ("/", "Current sky"),
            ("/operations", "Operations overview"),
            ("/operations/quarantine?kind=Artifact", "Quarantine browser"),
            ("/gallery?pageSize=24", "Archive"),
            (detailUrl, $"Capture #{capture.CaptureSequence}"),
            ("/schedule", "Capture schedule"),
            ("/calibration", "Calibration library"),
            ("/system", "System snapshot"),
            ("/operations/camera", "Camera & rig"),
            ("/operations/pipeline", "Pipeline summary"),
            ("/operations/automations", "Automations"),
            ("/operations/data", "Data & storage"),
            ("/operations/sky-map", "Sky map & catalog"),
            ("/operations/pipeline/executions", "Processing executions"),
            ("/operations/pipeline/graphs", "Named graphs"),
            ("/operations/pipeline/graphs/new", "Draft graph"),
            ("/Account/Login", "Log in"),
            ("/Account/Recovery", "Recover owner access"),
            ("/Account/Manage", "Profile"),
            ("/Account/Manage/Email", "Owner email"),
            ("/Account/Manage/ChangePassword", "Change password"),
            ("/not-found", "Page not found")
        };
        // A heading that names more than one route names none of them, so the table itself is checked
        // before it is used. Without this, restoring a shared constant heading would leave the walk green.
        Assert.AreEqual(
            routes.Length,
            routes.Select(entry => entry.Heading).Distinct(StringComparer.Ordinal).Count(),
            "Two routes in the responsive walk expect the same level-1 heading.");
        foreach (var viewport in viewports)
        {
            await page.SetViewportSizeAsync(viewport.Width, viewport.Height).ConfigureAwait(false);
            foreach (var (route, heading) in routes)
            {
                await page.GotoAsync(route).ConfigureAwait(false);
                if (string.Equals(route, detailUrl, StringComparison.Ordinal))
                {
                    await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = heading, Level = 1, Exact = true }))
                        .ConfigureAwait(false);
                }
                await VisibleAsync(page.Locator("main#mainContent h1").First).ConfigureAwait(false);
                Assert.AreEqual(
                    heading,
                    (await page.Locator("main#mainContent h1").First.InnerTextAsync().ConfigureAwait(false)).Trim(),
                    $"Unexpected level-1 heading on {route} at {viewport.Width}x{viewport.Height}.");
                var overflowing = await page.EvaluateAsync<string>("""
                    () => {
                      const limit = document.documentElement.clientWidth + 1;
                      if (document.documentElement.scrollWidth <= limit) { return ''; }
                      return [...document.querySelectorAll('body *')]
                        .filter(element => element.getBoundingClientRect().right > limit)
                        .slice(0, 6)
                        .map(element => {
                          const describe = node => `${node.tagName.toLowerCase()}${node.id ? '#' + node.id : ''}${node.className && typeof node.className === 'string' && node.className.trim() ? '.' + node.className.trim().split(/\s+/).join('.') : ''}`;
                          const parents = [element.parentElement, element.parentElement?.parentElement].filter(Boolean).map(describe).join(' < ');
                          return `${describe(element)} "${(element.textContent || '').trim().slice(0, 40)}" in ${parents} right=${Math.round(element.getBoundingClientRect().right)}`;
                        })
                        .join(' | ') || `scrollWidth=${document.documentElement.scrollWidth}`;
                    }
                    """).ConfigureAwait(false);
                Assert.AreEqual(string.Empty, overflowing, $"Horizontal overflow at {viewport.Width}x{viewport.Height} on {route}: {overflowing}");
                await AssertPageStructureAsync(page, route)
                    .ConfigureAwait(false);
                await AssertComputedContrastAsync(page, route, viewport).ConfigureAwait(false);
                if (string.Equals(route, "/", StringComparison.Ordinal))
                {
                    await AssertCurrentSkyResponsiveAsync(page, viewport).ConfigureAwait(false);
                }
                if (viewport.Width <= 390 && WorkspaceFormRoutes.Contains(route))
                {
                    var undersized = await page.EvaluateAsync<string>("""
                        () => [...document.querySelectorAll('.operations-content input:not([type=checkbox]), .operations-content select, .operations-content button, .operations-content a.btn')]
                            .filter(element => element.getClientRects().length > 0 && element.getBoundingClientRect().height < 44)
                            .slice(0, 5)
                            .map(element => `${element.tagName.toLowerCase()} "${(element.getAttribute('aria-label') || element.textContent || '').trim().slice(0, 30)}" h=${Math.round(element.getBoundingClientRect().height)}`)
                            .join(' | ')
                        """).ConfigureAwait(false);
                    Assert.AreEqual(string.Empty, undersized, $"Workspace controls under 44 CSS pixels on {route}: {undersized}");
                }
            }
        }
    }

    private static readonly HashSet<string> WorkspaceFormRoutes = new(StringComparer.Ordinal)
    {
        "/schedule", "/operations/camera", "/operations/pipeline", "/operations/automations", "/operations/data", "/operations/sky-map",
        "/operations/pipeline/executions", "/operations/pipeline/graphs", "/operations/pipeline/graphs/new"
    };

    [TestMethod]
    [TestCategory("Manual")]
    public async Task OwnerGraphEditorIsKeyboardOperableEndToEndAsync()
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await playwright.EnsureLaunchableOrInconclusiveAsync().ConfigureAwait(false);

        await using var host = await CameraAgentKestrelFixture.CreateAsync().ConfigureAwait(false);
        await using var browser = await playwright.LaunchOrInconclusiveAsync().ConfigureAwait(false);
        await using var diagnostics = new PlaywrightDiagnostics(browser, TestContext);
        await using var context = await diagnostics.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString(),
            ViewportSize = new ViewportSize { Width = 1280, Height = 720 }
        }).ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        await LoginAsync(page, CameraAgentKestrelFixture.OwnerEmail, CameraAgentKestrelFixture.OwnerPassword).ConfigureAwait(false);

        // Draft: name and revision, one node of a registered type, preview, and the diagram's keyboard path.
        await page.GotoAsync("/operations/pipeline/graphs/new").ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Draft graph", Level = 1 })).ConfigureAwait(false);
        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
        await TabToAsync(page, "input", "Graph name").ConfigureAwait(false);
        await page.Keyboard.TypeAsync("keyboard-graph").ConfigureAwait(false);
        await TabToAsync(page, "input", "Revision label").ConfigureAwait(false);
        await page.Keyboard.TypeAsync("1").ConfigureAwait(false);
        // Pick the preview step by typeahead on the focused select, then add it.
        await TabToAsync(page, "select", "New node type").ConfigureAwait(false);
        await page.Keyboard.TypeAsync("Preview").ConfigureAwait(false);
        await TabToAsync(page, "button", "Add node").ConfigureAwait(false);
        await page.Keyboard.PressAsync("Enter").ConfigureAwait(false);
        await VisibleAsync(page.Locator(".node-row").First).ConfigureAwait(false);
        await TabToAsync(page, "button", "Preview plan").ConfigureAwait(false);
        await page.Keyboard.PressAsync("Enter").ConfigureAwait(false);
        var diagram = page.Locator("svg[role='group']");
        await page.Locator("svg[role='group'], .editor-banner").First.WaitForAsync().ConfigureAwait(false);
        var banners = string.Join(" | ", await page.Locator(".editor-banner").AllInnerTextsAsync().ConfigureAwait(false));
        var rows = await page.Locator(".node-row").CountAsync().ConfigureAwait(false);
        var nodeTypes = await page.EvaluateAsync<string>("() => [...document.querySelectorAll('.node-row select[aria-label=\"Node type\"]')].map(select => select.value).join(',')").ConfigureAwait(false);
        Assert.IsGreaterThan(0, await diagram.CountAsync().ConfigureAwait(false), $"Preview did not render a diagram: {banners}; rows={rows}; types={nodeTypes}");
        await VisibleAsync(diagram).ConfigureAwait(false);
        Assert.IsGreaterThan(0, await page.Locator("svg desc").CountAsync().ConfigureAwait(false));
        Assert.IsFalse(await page.GetByRole(AriaRole.Button, new() { Name = "Save immutable draft" }).IsDisabledAsync().ConfigureAwait(false));

        // A diagram node is reachable by Tab and Enter moves focus to its editable row.
        await TabToAsync(page, "g", null).ConfigureAwait(false);
        await page.Keyboard.PressAsync("Enter").ConfigureAwait(false);
        await page.WaitForFunctionAsync("() => (document.activeElement?.id || '').startsWith('graph-node-')").ConfigureAwait(false);

        // Save the draft; it appears on the named graphs list with a validate action reachable by keyboard.
        await page.GetByRole(AriaRole.Button, new() { Name = "Save immutable draft" }).FocusAsync().ConfigureAwait(false);
        await page.Keyboard.PressAsync("Enter").ConfigureAwait(false);
        await VisibleAsync(page.GetByText("saved as an immutable revision")).ConfigureAwait(false);
        await page.GotoAsync("/operations/pipeline/graphs").ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Named graphs", Level = 1 })).ConfigureAwait(false);
        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
        var validate = page.Locator("button[id^='graph-VALIDATE-']").First;
        await VisibleAsync(validate).ConfigureAwait(false);
        await validate.FocusAsync().ConfigureAwait(false);
        await page.Keyboard.PressAsync("Enter").ConfigureAwait(false);
        var dialog = page.Locator("dialog.confirmation-panel");
        await VisibleAsync(dialog).ConfigureAwait(false);
        await page.WaitForFunctionAsync(
            "element => element.contains(document.activeElement)", await dialog.ElementHandleAsync().ConfigureAwait(false))
            .ConfigureAwait(false);
        await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Hidden }).ConfigureAwait(false);
        await page.WaitForFunctionAsync("() => (document.activeElement?.id || '').startsWith('graph-VALIDATE-')").ConfigureAwait(false);
        await diagnostics.CompleteAsync().ConfigureAwait(false);
    }

    private static async Task TabToAsync(IPage page, string tagName, string? accessibleName)
    {
        for (var step = 0; step < 80; step++)
        {
            await page.Keyboard.PressAsync("Tab").ConfigureAwait(false);
            var matched = await page.EvaluateAsync<bool>("""
                ([tag, name]) => {
                  const element = document.activeElement;
                  if (!element || element.tagName.toLowerCase() !== tag) { return false; }
                  if (name === null) { return true; }
                  const label = element.getAttribute('aria-label') || element.labels?.[0]?.textContent || element.textContent || '';
                  return label.trim().startsWith(name);
                }
                """, new object?[] { tagName, accessibleName }).ConfigureAwait(false);
            if (matched)
            {
                return;
            }
        }
        Assert.Fail($"No {tagName} named '{accessibleName}' was reachable by Tab.");
    }

    private static async Task AssertOperationsWorkspaceAsync(IPage page)
    {
        var originalViewport = page.ViewportSize;
        // Desktop: the grouped section sidebar is visible, marks the current section, and the drawer toggle is hidden.
        var sidebarNavigation = page.GetByRole(AriaRole.Navigation, new() { Name = "Operations navigation" });
        await VisibleAsync(sidebarNavigation).ConfigureAwait(false);
        Assert.AreEqual(
            "page",
            await sidebarNavigation.GetByRole(AriaRole.Link, new() { Name = "Capture schedule", Exact = true })
                .GetAttributeAsync("aria-current").ConfigureAwait(false));
        Assert.IsFalse(await page.Locator("button[aria-controls='operations-sections']").IsVisibleAsync().ConfigureAwait(false));
        foreach (var group in new[] { "Setup", "Capture", "Processing", "Automation", "Data", "System" })
        {
            await VisibleAsync(sidebarNavigation.Locator($"#operations-group-{group}")).ConfigureAwait(false);
        }

        // Narrow: the sidebar collapses behind a toggle that opens the drawer, and Escape returns focus to the toggle.
        await page.SetViewportSizeAsync(390, 844).ConfigureAwait(false);
        var toggle = page.Locator("button[aria-controls='operations-sections']");
        await VisibleAsync(toggle).ConfigureAwait(false);
        await sidebarNavigation.WaitForAsync(new() { State = WaitForSelectorState.Hidden }).ConfigureAwait(false);
        await toggle.ClickAsync().ConfigureAwait(false);
        await VisibleAsync(sidebarNavigation).ConfigureAwait(false);
        Assert.AreEqual("true", await toggle.GetAttributeAsync("aria-expanded").ConfigureAwait(false));
        await AssertNavigationModalAsync(page, "operations-sections").ConfigureAwait(false);
        await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
        await page.WaitForFunctionAsync(
            "() => document.querySelector('button[aria-controls=\"operations-sections\"]')?.getAttribute('aria-expanded') === 'false' && document.activeElement?.getAttribute('aria-controls') === 'operations-sections'")
            .ConfigureAwait(false);
        await sidebarNavigation.WaitForAsync(new() { State = WaitForSelectorState.Hidden }).ConfigureAwait(false);
        await page.SetViewportSizeAsync(originalViewport?.Width ?? 1440, originalViewport?.Height ?? 900).ConfigureAwait(false);
        await VisibleAsync(sidebarNavigation).ConfigureAwait(false);
    }

    private static async Task AssertNavigationModalAsync(IPage page, string id)
    {
        var dialog = page.Locator($"#{id}");
        Assert.IsTrue(await dialog.EvaluateAsync<bool>("element => element.matches(':modal')").ConfigureAwait(false));
        for (var index = 0; index < 32; index++)
        {
            await page.Keyboard.PressAsync(index < 16 ? "Tab" : "Shift+Tab").ConfigureAwait(false);
            Assert.IsTrue(await dialog.EvaluateAsync<bool>("element => element.contains(document.activeElement)").ConfigureAwait(false), id);
        }
        await page.Locator(".shell-brand").EvaluateAsync("element => element.focus()").ConfigureAwait(false);
        Assert.IsTrue(await dialog.EvaluateAsync<bool>("element => element.contains(document.activeElement)").ConfigureAwait(false), "Background must be inert.");
    }

    private static async Task AssertCurrentSkyResponsiveAsync(IPage page, ViewportSize viewport)
    {
        var image = page.Locator(".capture-image img");
        if (await image.CountAsync().ConfigureAwait(false) > 0)
        {
            await page.WaitForFunctionAsync("""
                () => {
                  const image = document.querySelector('.capture-image img');
                  return image && getComputedStyle(image).getPropertyValue('object-fit') === 'contain' &&
                    image.getBoundingClientRect().right <= document.documentElement.clientWidth + 1;
                }
                """).ConfigureAwait(false);
        }
        if (viewport.Width <= 760 && viewport.Height > viewport.Width)
        {
            Assert.IsTrue(await page.EvaluateAsync<bool>("""
                () => document.querySelector('.current-sky-visual').getBoundingClientRect().top <=
                    document.querySelector('.current-sky-summary').getBoundingClientRect().top
                """).ConfigureAwait(false), "The image must remain visually first in narrow portrait layouts.");
        }
        if (viewport.Width <= 390)
        {
            Assert.IsTrue(await page.EvaluateAsync<bool>("""
                () => [...document.querySelectorAll('.stage-selector button, #current-sky-view-large')]
                    .filter(element => element.getClientRects().length > 0)
                    .every(element => element.getBoundingClientRect().height >= 44)
                """).ConfigureAwait(false), "Current-sky touch controls must remain at least 44 CSS pixels high.");
        }
    }

    private static async Task AssertPageStructureAsync(IPage page, string route)
    {
        Assert.AreEqual(
            1,
            await page.Locator("main").CountAsync().ConfigureAwait(false),
            $"Unexpected main landmark count on {page.Url}.");
        Assert.AreEqual(1, await page.Locator("main h1").CountAsync().ConfigureAwait(false));
        Assert.IsGreaterThan(0, await page.Locator("header").CountAsync().ConfigureAwait(false));
        Assert.IsTrue(await page.EvaluateAsync<bool>("""
            () => {
              const name = element => {
                const labelledBy = (element.getAttribute('aria-labelledby') || '')
                  .split(/\s+/)
                  .filter(Boolean)
                  .map(id => document.getElementById(id)?.textContent || '')
                  .join(' ');
                const descendantAlt = [...element.querySelectorAll('img[alt]')]
                  .map(image => image.getAttribute('alt') || '')
                  .join(' ');
                return (element.getAttribute('aria-label') || labelledBy ||
                  (element.tagName === 'IMG' ? element.getAttribute('alt') : '') ||
                  element.textContent || descendantAlt || '').trim();
              };
              return [...document.querySelectorAll('a[href], button, img')]
                .filter(element => element.getClientRects().length > 0)
                .every(element => Boolean(name(element)));
            }
            """).ConfigureAwait(false));
        Assert.IsTrue(await page.EvaluateAsync<bool>("""
            () => [...document.querySelectorAll('main input, main select, main textarea')]
                .filter(element => element.getClientRects().length > 0)
                .every(element => element.labels?.length > 0 || Boolean(element.getAttribute('aria-label')))
            """).ConfigureAwait(false));
        if (route.StartsWith("/gallery?", StringComparison.Ordinal))
        {
            Assert.IsGreaterThan(0, await page.Locator("form[aria-label]").CountAsync().ConfigureAwait(false));
        }
    }

    private static async Task WaitForGalleryCapturesAsync(IPage page, int minimumCards)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await page.GotoAsync("/gallery").ConfigureAwait(false);
            await page.Locator(".gallery-state, .prototype-gallery-grid").First.WaitForAsync().ConfigureAwait(false);
            if (await page.Locator(".capture-card").CountAsync().ConfigureAwait(false) >= minimumCards &&
                await page.GetByRole(AriaRole.Button, new() { Name = "Older captures" }).CountAsync().ConfigureAwait(false) > 0)
            {
                return;
            }
            await Task.Delay(500).ConfigureAwait(false);
        }
        Assert.Fail($"The real VirtualSky pipeline did not produce {minimumCards} durable gallery captures within 45 seconds.");
    }

    private static async Task AssertVisibleImagesDecodeAsync(IPage page)
    {
        await page.WaitForFunctionAsync("""
            () => {
              const images = [...document.querySelectorAll('.capture-card img')]
                .filter(image => image.getClientRects().length > 0);
              return images.length > 0 && images.every(image => image.complete && image.naturalWidth > 0);
            }
            """).ConfigureAwait(false);
    }

    private static async Task AssertComputedContrastAsync(IPage page, string route, ViewportSize viewport, string? selector = null)
    {
        var result = await page.EvaluateAsync<string>("""
            selector => {
              const parse = value => {
                const channels = (value.match(/[\d.]+/g) || []).map(Number);
                return { rgb: channels.slice(0, 3), alpha: channels.length > 3 ? channels[3] : 1 };
              };
              const over = (top, bottom) => {
                const alpha = top.alpha + bottom.alpha * (1 - top.alpha);
                return {
                  rgb: top.rgb.map((channel, index) =>
                    alpha === 0 ? 0 : (channel * top.alpha + bottom.rgb[index] * bottom.alpha * (1 - top.alpha)) / alpha),
                  alpha
                };
              };
              const backgrounds = element => {
                let colors = [{ rgb: [0, 0, 0], alpha: 0 }];
                for (let current = element; current && colors.some(color => color.alpha < 1); current = current.parentElement) {
                  const style = getComputedStyle(current);
                  const solid = parse(style.backgroundColor);
                  const gradientColors = (style.backgroundImage.match(/rgba?\([^)]+\)/g) || []).map(parse);
                  const layers = gradientColors.length > 0
                    ? gradientColors.map(gradient => over(gradient, solid))
                    : [solid];
                  colors = colors.flatMap(color => layers.map(layer => over(color, layer)));
                }
                return colors.map(color => over(color, { rgb: [255, 255, 255], alpha: 1 }));
              };
              const luminance = color => {
                const linear = color.rgb.map(value => { const c = value / 255; return c <= .04045 ? c / 12.92 : ((c + .055) / 1.055) ** 2.4; });
                return .2126 * linear[0] + .7152 * linear[1] + .0722 * linear[2];
              };
              const measure = element => {
                const style = getComputedStyle(element);
                const candidates = backgrounds(element).map(backdrop => {
                  const foreground = over(parse(style.color), backdrop);
                  const foregroundLuminance = luminance(foreground);
                  const backgroundLuminance = luminance(backdrop);
                  return {
                    ratio: (Math.max(foregroundLuminance, backgroundLuminance) + .05) /
                      (Math.min(foregroundLuminance, backgroundLuminance) + .05),
                    backdrop
                  };
                }).sort((left, right) => left.ratio - right.ratio);
                const worst = candidates[0];
                return {
                  ratio: worst.ratio,
                  foreground: style.color,
                  background: style.backgroundColor,
                  backdrop: worst.backdrop.rgb.map(Math.round).join(',')
                };
              };
              const pairs = [...document.querySelectorAll(selector ||
                'main h1, main h2, main h3, main h4, main a, main p, main dt, main dd, main label, main button:not(:disabled), main input, main select, main textarea, main code, main time, main strong, main span, main .alert, main .state-chip, main .decision-chip')]
                .filter(element => element.getClientRects().length > 0 &&
                  (['INPUT', 'SELECT', 'TEXTAREA'].includes(element.tagName) || (element.textContent || '').trim().length > 0));
              const measured = pairs.map(element => Object.assign(measure(element), {
                  element: `${element.tagName.toLowerCase()}.${[...element.classList].join('.')}`,
                  text: (element.textContent || '').trim().slice(0, 80)
                })).sort((left, right) => left.ratio - right.ratio);
              return JSON.stringify(measured[0] || { ratio: 21, element: 'none', text: '' });
            }
            """, selector).ConfigureAwait(false);
        using var measurement = JsonDocument.Parse(result);
        var minimum = measurement.RootElement.GetProperty("ratio").GetDouble();
        var element = measurement.RootElement.GetProperty("element").GetString();
        var text = measurement.RootElement.GetProperty("text").GetString();
        var foreground = measurement.RootElement.GetProperty("foreground").GetString();
        var background = measurement.RootElement.GetProperty("background").GetString();
        var backdrop = measurement.RootElement.GetProperty("backdrop").GetString();
        Assert.IsGreaterThanOrEqualTo(4.5, minimum,
            $"Visible primary text, status, and control pairs require deterministic 4.5:1 contrast. " +
            $"Route {route} at {viewport.Width}x{viewport.Height}; {element}: '{text}'; " +
            $"foreground {foreground}, background {background}, composited backdrop rgb({backdrop}).");
    }

    private static async Task OpenDialogAsync(ILocator trigger, ILocator dialog)
    {
        await WaitForInteractiveShellAsync(trigger.Page).ConfigureAwait(false);
        await trigger.ClickAsync().ConfigureAwait(false);
        await VisibleAsync(dialog).ConfigureAwait(false);
    }

    private static async Task WaitForResponsiveShellLayoutAsync(IPage page, int viewportWidth)
    {
        await page.WaitForFunctionAsync(
            """
            expectedMobile => {
              const toggle = document.querySelector('button[aria-controls="shell-menu-panel"]');
              const menu = document.querySelector('.shell-menu');
              if (!toggle || !menu || matchMedia('(max-width: 780px)').matches !== expectedMobile) return false;
              const toggleVisible = getComputedStyle(toggle).display !== 'none';
              return toggleVisible === expectedMobile &&
                (!expectedMobile || toggle.getBoundingClientRect().width <= menu.getBoundingClientRect().width + 1);
            }
            """,
            viewportWidth <= 780).ConfigureAwait(false);
    }

    private static async Task AssertDialogCancelIsSynchronouslyGuardedAsync(ILocator dialog)
    {
        await dialog.EvaluateAsync(
            """
            element => {
              globalThis.hvoLastDialogCancelWasGuarded = false;
              element.addEventListener(
                'cancel',
                event => {
                  globalThis.hvoLastDialogCancelWasGuarded =
                    event.defaultPrevented && element.dataset.hvoCancelGuarded === 'true';
                },
                { capture: true, once: true });
            }
            """).ConfigureAwait(false);
        await dialog.Page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden }).ConfigureAwait(false);
        var guarded = await dialog.Page.EvaluateAsync<bool>(
            "() => globalThis.hvoLastDialogCancelWasGuarded === true").ConfigureAwait(false);
        Assert.IsTrue(guarded, "The native dialog cancel default must be prevented before server dispatch.");
    }

    private static async Task ClickAndWaitForVisibleAsync(ILocator trigger, ILocator result)
    {
        await WaitForInteractiveShellAsync(trigger.Page).ConfigureAwait(false);
        await trigger.ClickAsync().ConfigureAwait(false);
        await VisibleAsync(result).ConfigureAwait(false);
    }

    private static async Task OpenMenuWithKeyboardAsync(ILocator toggle)
    {
        await WaitForInteractiveShellAsync(toggle.Page).ConfigureAwait(false);
        await toggle.PressAsync("Enter").ConfigureAwait(false);
        await toggle.Page.WaitForFunctionAsync(
            "element => element.getAttribute('aria-expanded') === 'true'",
            await toggle.ElementHandleAsync().ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static Task WaitForInteractiveShellAsync(IPage page)
        => page.Locator(".app-frame[data-interactive='true']").WaitForAsync();

    private static async Task WaitForFocusAsync(IPage page, ILocator locator)
        => await page.WaitForFunctionAsync(
            "element => element === document.activeElement",
            await locator.ElementHandleAsync().ConfigureAwait(false)).ConfigureAwait(false);

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The returned HttpClient owns and disposes its socket handler.")]
    private static HttpClient CreateUnixSocketHttpClient(string socketPath)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken)
                        .ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        try
        {
            return new HttpClient(handler, disposeHandler: true)
            {
                BaseAddress = new Uri("http://localhost", UriKind.Absolute)
            };
        }
        catch
        {
            handler.Dispose();
            throw;
        }
    }

    private static async Task WaitForContainedFocusAsync(IPage page, ILocator locator)
        => await page.WaitForFunctionAsync(
            "element => element.contains(document.activeElement)",
            await locator.ElementHandleAsync().ConfigureAwait(false)).ConfigureAwait(false);

    private static Task VisibleAsync(ILocator locator, float timeout = DefaultTimeoutMilliseconds)
        => locator.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = timeout
        });
}
