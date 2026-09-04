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

    [TestMethod]
    public async Task FirstOwnerLoginRequiresPasswordReplacementAndRevokesStaleSessionAsync()
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        if (!File.Exists(playwright.Chromium.ExecutablePath))
        {
            Assert.Inconclusive(
                "Pinned Playwright Chromium is absent. Run `scripts/test:cameraagent-ui --install-browser` from the repository root.");
        }

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
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        }).ConfigureAwait(false);
        await using var replacingContext = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString()
        }).ConfigureAwait(false);
        await using var staleContext = await browser.NewContextAsync(new BrowserNewContextOptions
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
        await using var oldCredentialContext = await browser.NewContextAsync(new BrowserNewContextOptions
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

        await using var replacementCredentialContext = await browser.NewContextAsync(new BrowserNewContextOptions
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
    }

    [TestMethod]
    public async Task LocalOwnerRecoveryRevokesSessionsAndPreservesCaptureContinuityAsync()
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        if (!File.Exists(playwright.Chromium.ExecutablePath))
        {
            Assert.Inconclusive(
                "Pinned Playwright Chromium is absent. Run `scripts/test:cameraagent-ui --install-browser` from the repository root.");
        }

        await using var host = await CameraAgentKestrelFixture.CreateAsync().ConfigureAwait(false);
        await host.RestartWithoutPasswordAuthorityAsync().ConfigureAwait(false);
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        }).ConfigureAwait(false);
        await using var staleContext = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString()
        }).ConfigureAwait(false);
        var stalePage = await staleContext.NewPageAsync().ConfigureAwait(false);
        await LoginAsync(stalePage, CameraAgentKestrelFixture.OwnerEmail, CameraAgentKestrelFixture.OwnerPassword)
            .ConfigureAwait(false);
        await stalePage.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/").ConfigureAwait(false);
        await stalePage.GotoAsync("/operations").ConfigureAwait(false);
        await VisibleAsync(stalePage.GetByRole(AriaRole.Heading, new() { Name = "Capture operations", Level = 1 }))
            .ConfigureAwait(false);
        var staleCaptureAction = stalePage.Locator("#capture-action");
        var staleConfirmation = stalePage.Locator("dialog.confirmation");
        await OpenDialogAsync(staleCaptureAction, staleConfirmation).ConfigureAwait(false);

        await using var staleReadContext = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString()
        }).ConfigureAwait(false);
        var staleReadPage = await staleReadContext.NewPageAsync().ConfigureAwait(false);
        await LoginAsync(staleReadPage, CameraAgentKestrelFixture.OwnerEmail, CameraAgentKestrelFixture.OwnerPassword)
            .ConfigureAwait(false);
        await staleReadPage.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/").ConfigureAwait(false);
        await staleReadPage.GotoAsync("/operations").ConfigureAwait(false);
        await VisibleAsync(staleReadPage.GetByRole(AriaRole.Heading, new() { Name = "Capture operations", Level = 1 }))
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

        await using var oldPasswordContext = await browser.NewContextAsync(new BrowserNewContextOptions
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

        await using var recoveredContext = await browser.NewContextAsync(new BrowserNewContextOptions
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
        await using var finalContext = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString()
        }).ConfigureAwait(false);
        var finalPage = await finalContext.NewPageAsync().ConfigureAwait(false);
        await LoginAsync(finalPage, CameraAgentKestrelFixture.OwnerEmail, finalPassword).ConfigureAwait(false);
        await finalPage.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/").ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ObservatoryShellAndAccountPagesRemainLocalResponsiveAndKeyboardReachableAsync()
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        if (!File.Exists(playwright.Chromium.ExecutablePath))
        {
            Assert.Inconclusive(
                "Pinned Playwright Chromium is absent. Run `scripts/test:cameraagent-ui --install-browser` from the repository root.");
        }

        await using var host = await CameraAgentKestrelFixture.CreateAsync().ConfigureAwait(false);
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        }).ConfigureAwait(false);
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
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

        var menuToggle = page.Locator("button.shell-menu__toggle");
        await VisibleAsync(menuToggle).ConfigureAwait(false);
        await OpenMenuWithKeyboardAsync(menuToggle).ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Link, new() { Name = "Operations", Exact = true }).First)
            .ConfigureAwait(false);
        await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
        await page.WaitForFunctionAsync("() => document.querySelector('button.shell-menu__toggle')?.getAttribute('aria-expanded') === 'false'")
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
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Account settings", Level = 1 }))
            .ConfigureAwait(false);
        var ownerEmail = page.GetByRole(AriaRole.Link, new() { Name = "Owner email", Exact = true });
        Assert.AreEqual("Account/Manage/Email", await ownerEmail.GetAttributeAsync("href").ConfigureAwait(false));
        await page.GotoAsync("/Account/Manage/Email").ConfigureAwait(false);
        Assert.AreEqual("", await page.Locator("#owner-email").GetAttributeAsync("readonly").ConfigureAwait(false));
        Assert.AreEqual(
            0,
            await page.Locator("[role='alert'], [role='status'], [aria-live]").CountAsync().ConfigureAwait(false));
        Assert.IsFalse(await page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1").ConfigureAwait(false));

        await page.SetViewportSizeAsync(1024, 768).ConfigureAwait(false);
        await page.GotoAsync("/").ConfigureAwait(false);
        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
        var desktopCurrentSky = page.GetByRole(AriaRole.Link, new() { Name = "Current sky", Exact = true });
        await VisibleAsync(desktopCurrentSky).ConfigureAwait(false);
        Assert.IsFalse(await page.Locator("button.shell-menu__toggle").IsVisibleAsync().ConfigureAwait(false));
        await desktopCurrentSky.FocusAsync().ConfigureAwait(false);
        await WaitForFocusAsync(page, desktopCurrentSky).ConfigureAwait(false);
        await page.GotoAsync("/schedule").ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Schedule control", Level = 1 }))
            .ConfigureAwait(false);
        await VisibleAsync(page.GetByLabel("Current schedule state")).ConfigureAwait(false);
        Assert.AreEqual(
            "page",
            await page.GetByRole(AriaRole.Link, new() { Name = "Operations", Exact = true })
                .GetAttributeAsync("aria-current").ConfigureAwait(false));

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
    }

    [TestMethod]
    [TestCategory("Manual")]
    [DoNotParallelize]
    public async Task OwnerOperationsGalleryAndResponsiveAcceptanceAsync()
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        if (!File.Exists(playwright.Chromium.ExecutablePath))
        {
            Assert.Inconclusive(
                "Pinned Playwright Chromium is absent. Run `scripts/test:cameraagent-ui --install-browser` from the repository root.");
        }

        await using var host = await CameraAgentKestrelFixture.CreateAsync(
            enableCentralIntegration: true).ConfigureAwait(false);
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        }).ConfigureAwait(false);

        await AssertAnonymousAndNonOwnerAuthorizationAsync(browser, host.BaseAddress).ConfigureAwait(false);

        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
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
    }

    [TestMethod]
    public async Task OwnerCurrentSkyResponsiveAcceptanceAsync()
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        if (!File.Exists(playwright.Chromium.ExecutablePath))
        {
            Assert.Inconclusive(
                "Pinned Playwright Chromium is absent. Run `scripts/test:cameraagent-ui --install-browser` from the repository root.");
        }

        await using var host = await CameraAgentKestrelFixture.CreateAsync().ConfigureAwait(false);
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        }).ConfigureAwait(false);
        await AssertAnonymousAndNonOwnerAuthorizationAsync(browser, host.BaseAddress).ConfigureAwait(false);
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
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
    }

    [TestMethod]
    public async Task OwnerArchivePresentationAcceptanceAsync()
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        if (!File.Exists(playwright.Chromium.ExecutablePath))
        {
            Assert.Inconclusive(
                "Pinned Playwright Chromium is absent. Run `scripts/test:cameraagent-ui --install-browser` from the repository root.");
        }

        await using var host = await CameraAgentKestrelFixture.CreateAsync().ConfigureAwait(false);
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        }).ConfigureAwait(false);
        await AssertAnonymousAndNonOwnerAuthorizationAsync(browser, host.BaseAddress).ConfigureAwait(false);
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
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
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Archive", Level = 1 })).ConfigureAwait(false);
        var advanced = page.Locator(".advanced-filters");
        Assert.IsFalse(await advanced.EvaluateAsync<bool>("details => details.open").ConfigureAwait(false));
        var firstCard = page.Locator(".capture-card")
            .Filter(new LocatorFilterOptions { HasText = "Processed" })
            .First;
        var imageLink = firstCard.Locator(".capture-card__image-link");
        var image = firstCard.Locator("img");
        var viewerButton = firstCard.GetByRole(AriaRole.Button, new() { Name = "View large image" });
        await VisibleAsync(image).ConfigureAwait(false);
        Assert.AreEqual("lazy", await image.GetAttributeAsync("loading").ConfigureAwait(false));
        Assert.IsTrue(await image.EvaluateAsync<bool>(
            "element => element.complete && element.naturalWidth > 0 && element.naturalHeight > 0").ConfigureAwait(false));
        StringAssert.Contains(await firstCard.Locator(".capture-card__summary").InnerTextAsync().ConfigureAwait(false), "Processed", StringComparison.Ordinal);
        Assert.IsTrue(await firstCard.EvaluateAsync<bool>(
            "card => (card.querySelector('.capture-card__image-link').compareDocumentPosition(card.querySelector('button')) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0")
            .ConfigureAwait(false));
        Assert.IsNotNull(await imageLink.GetAttributeAsync("href").ConfigureAwait(false));

        var viewerSource = await image.GetAttributeAsync("src").ConfigureAwait(false);
        var viewerTriggerId = await viewerButton.GetAttributeAsync("id").ConfigureAwait(false);
        await viewerButton.ClickAsync().ConfigureAwait(false);
        var dialog = page.Locator("dialog.large-viewer");
        await VisibleAsync(dialog).ConfigureAwait(false);
        Assert.AreEqual(viewerSource, await dialog.Locator("img").GetAttributeAsync("src").ConfigureAwait(false));
        await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden }).ConfigureAwait(false);
        Assert.AreEqual(viewerTriggerId, await page.EvaluateAsync<string>("() => document.activeElement?.id || ''").ConfigureAwait(false));

        await advanced.Locator("summary").ClickAsync().ConfigureAwait(false);
        Assert.IsTrue(await advanced.EvaluateAsync<bool>("details => details.open").ConfigureAwait(false));
        await page.GetByLabel("Evidence origin").SelectOptionAsync("Simulated").ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Apply filters" }).ClickAsync().ConfigureAwait(false);
        await page.WaitForURLAsync(url => new Uri(url).Query.Contains("origin=Simulated", StringComparison.Ordinal)).ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Button, new() { Name = "Older captures" })).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Older captures" }).ClickAsync().ConfigureAwait(false);
        await page.WaitForURLAsync(url => new Uri(url).Query.Contains("origin=Simulated", StringComparison.Ordinal) &&
            new Uri(url).Query.Contains("cursor=", StringComparison.Ordinal)).ConfigureAwait(false);
        await page.WaitForFunctionAsync(
            "() => decodeURIComponent(document.querySelector('.capture-card__image-link')?.getAttribute('href') || '').includes('cursor=')")
            .ConfigureAwait(false);
        var detailUrl = await page.Locator(".capture-card__image-link").First.GetAttributeAsync("href").ConfigureAwait(false);
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
    }

    [TestMethod]
    public async Task OwnerArchiveCalendarProductsAndCandidatesAcceptanceAsync()
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        if (!File.Exists(playwright.Chromium.ExecutablePath))
        {
            Assert.Inconclusive(
                "Pinned Playwright Chromium is absent. Run `scripts/test:cameraagent-ui --install-browser` from the repository root.");
        }

        await using var host = await CameraAgentKestrelFixture.CreateAsync().ConfigureAwait(false);
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        }).ConfigureAwait(false);
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
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
    }

    [TestMethod]
    public async Task OwnerCaptureDetailPresentationAcceptanceAsync()
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        if (!File.Exists(playwright.Chromium.ExecutablePath))
        {
            Assert.Inconclusive(
                "Pinned Playwright Chromium is absent. Run `scripts/test:cameraagent-ui --install-browser` from the repository root.");
        }

        await using var host = await CameraAgentKestrelFixture.CreateAsync().ConfigureAwait(false);
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        }).ConfigureAwait(false);
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
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
        await detailLink.ClickAsync().ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Capture detail", Level = 1 }))
            .ConfigureAwait(false);
        await VisibleAsync(page.Locator(".detail-capture-image img")).ConfigureAwait(false);
        Assert.IsFalse(await page.Locator(".technical-evidence").EvaluateAsync<bool>("details => details.open")
            .ConfigureAwait(false));
        await VisibleAsync(page.Locator(".capture-summary")).ConfigureAwait(false);
        Assert.IsGreaterThanOrEqualTo(2, await page.Locator(".stage-selector__button:not(:disabled)").CountAsync().ConfigureAwait(false));
        Assert.IsGreaterThan(0, await page.Locator(".stage-selector__button:disabled").CountAsync().ConfigureAwait(false));

        var rawStage = page.GetByRole(AriaRole.Button, new() { Name = "Raw", Exact = true });
        await rawStage.ClickAsync().ConfigureAwait(false);
        await page.WaitForFunctionAsync("""
            () => [...document.querySelectorAll('.stage-selector__button')]
                .some(button => button.textContent.trim() === 'Raw' && button.getAttribute('aria-pressed') === 'true')
            """).ConfigureAwait(false);
        var selectedSource = await page.Locator(".detail-capture-image img").GetAttributeAsync("src").ConfigureAwait(false);
        Assert.IsNotNull(selectedSource);
        StringAssert.Contains(selectedSource, "/preview", StringComparison.Ordinal);
        Assert.AreEqual("true", await rawStage.GetAttributeAsync("aria-pressed").ConfigureAwait(false));

        var trigger = page.Locator("#capture-detail-view-large");
        await trigger.ClickAsync().ConfigureAwait(false);
        var dialog = page.Locator(".large-viewer");
        await VisibleAsync(dialog).ConfigureAwait(false);
        await page.WaitForFunctionAsync("() => document.activeElement?.getAttribute('aria-label') === 'Close large image'")
            .ConfigureAwait(false);
        Assert.AreEqual(selectedSource, await dialog.Locator("img").GetAttributeAsync("src").ConfigureAwait(false));
        await page.Keyboard.PressAsync("Shift+Tab").ConfigureAwait(false);
        Assert.IsTrue(await dialog.EvaluateAsync<bool>("element => element.contains(document.activeElement)")
            .ConfigureAwait(false));
        await page.GetByRole(AriaRole.Button, new() { Name = "100%", Exact = true }).ClickAsync().ConfigureAwait(false);
        await page.WaitForFunctionAsync(
            "() => [...document.querySelectorAll('.large-viewer__modes button')].some(button => button.textContent.trim() === '100%' && button.getAttribute('aria-pressed') === 'true')")
            .ConfigureAwait(false);
        Assert.AreEqual("true", await page.GetByRole(AriaRole.Button, new() { Name = "100%", Exact = true })
            .GetAttributeAsync("aria-pressed").ConfigureAwait(false));
        foreach (var viewport in new[]
        {
            new ViewportSize { Width = 390, Height = 844 },
            new ViewportSize { Width = 844, Height = 390 }
        })
        {
            await page.SetViewportSizeAsync(viewport.Width, viewport.Height).ConfigureAwait(false);
            await VisibleAsync(dialog).ConfigureAwait(false);
            Assert.AreEqual(selectedSource, await dialog.Locator("img").GetAttributeAsync("src").ConfigureAwait(false));
            await page.Keyboard.PressAsync("Tab").ConfigureAwait(false);
            Assert.IsTrue(await dialog.EvaluateAsync<bool>("element => element.contains(document.activeElement)")
                .ConfigureAwait(false));
        }
        await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden }).ConfigureAwait(false);
        Assert.AreEqual(
            "capture-detail-view-large",
            await page.EvaluateAsync<string>("() => document.activeElement?.id || ''").ConfigureAwait(false));
        await page.Keyboard.PressAsync("Enter").ConfigureAwait(false);
        await VisibleAsync(dialog).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Close large image", Exact = true }).ClickAsync()
            .ConfigureAwait(false);
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden }).ConfigureAwait(false);
        Assert.AreEqual(
            "capture-detail-view-large",
            await page.EvaluateAsync<string>("() => document.activeElement?.id || ''").ConfigureAwait(false));

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
                    '.shell-brand', '.shell-menu', '.shell-menu__toggle'
                  ].map(describe);
                  return `viewport inner=${innerWidth} outer=${outerWidth} visual=${visualViewport?.width} ` +
                    `mobile=${matchMedia('(max-width: 767.98px)').matches}; ` +
                    `document ${root.clientWidth}/${root.scrollWidth}; ${structure.join('; ')}; ` +
                    `offenders: ${offenders.join('; ')}`;
                }
                """).ConfigureAwait(false);
            Assert.AreEqual(string.Empty, overflow, $"capture detail overflowed at {viewport.Width}x{viewport.Height}");
            Assert.AreEqual("contain", await page.Locator(".detail-capture-image img")
                .EvaluateAsync<string>("image => getComputedStyle(image).objectFit").ConfigureAwait(false));
        }

        Assert.AreEqual(2, await page.Locator(".capture-navigation__control[href]").CountAsync().ConfigureAwait(false));
        await page.Locator(".technical-evidence > summary").ClickAsync().ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Artifacts", Level = 2 })).ConfigureAwait(false);
        Assert.IsGreaterThan(0, await page.Locator("a[download]").CountAsync().ConfigureAwait(false));
        await page.GotoAsync($"/gallery/{Guid.NewGuid():D}").ConfigureAwait(false);
        await VisibleAsync(page.GetByText("Capture unavailable", new() { Exact = true })).ConfigureAwait(false);

        await using (var anonymous = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString()
        }).ConfigureAwait(false))
        {
            var anonymousPage = await anonymous.NewPageAsync().ConfigureAwait(false);
            await anonymousPage.GotoAsync(detailUrl).ConfigureAwait(false);
            await anonymousPage.WaitForURLAsync(url => url.Contains("/Account/Login", StringComparison.OrdinalIgnoreCase) &&
                url.Contains("returnUrl=", StringComparison.OrdinalIgnoreCase)).ConfigureAwait(false);
        }
        await using (var nonOwner = await browser.NewContextAsync(new BrowserNewContextOptions
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
    }

    [TestMethod]
    public async Task CalibrationAcquisitionActivationAndRollbackAsync()
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        if (!File.Exists(playwright.Chromium.ExecutablePath))
        {
            Assert.Inconclusive(
                "Pinned Playwright Chromium is absent. Run `scripts/test:cameraagent-ui --install-browser` from the repository root.");
        }

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

        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        }).ConfigureAwait(false);
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
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
    }

    private static async Task AssertAnonymousAndNonOwnerAuthorizationAsync(IBrowser browser, Uri baseAddress)
    {
        await using (var anonymous = await browser.NewContextAsync(new BrowserNewContextOptions
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

        await using var nonOwner = await browser.NewContextAsync(new BrowserNewContextOptions
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
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Schedule control" })).ConfigureAwait(false);
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
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Capture operations", Level = 1 }))
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
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.querySelector('dialog')?.contains(document.activeElement) === true")
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
        await page.WaitForFunctionAsync(
            """
            () => document.querySelectorAll('.stage-selector button').length === 4 &&
                document.querySelectorAll('.stage-selector button[aria-pressed=true]').length === 1
            """)
            .ConfigureAwait(false);
        Assert.AreEqual(4, await page.Locator(".stage-selector button").CountAsync().ConfigureAwait(false));
        Assert.AreEqual(1, await page.Locator(".stage-selector button[aria-pressed='true']").CountAsync().ConfigureAwait(false));
        Assert.IsGreaterThan(0, await page.Locator(".stage-selector button:disabled").CountAsync().ConfigureAwait(false));
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
        if (!await advanced.EvaluateAsync<bool>("details => details.open").ConfigureAwait(false))
        {
            await advanced.Locator("summary").ClickAsync().ConfigureAwait(false);
        }
        await page.GetByLabel("Evidence origin").SelectOptionAsync("Simulated").ConfigureAwait(false);
        await page.GetByLabel("Page size").SelectOptionAsync("24").ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Apply filters" }).ClickAsync().ConfigureAwait(false);
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
        await detailLink.ClickAsync().ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Capture detail", Level = 1 })).ConfigureAwait(false);
        await page.Locator(".technical-evidence > summary").ClickAsync().ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Artifacts", Level = 2 })).ConfigureAwait(false);

        var image = page.Locator(".detail-capture-image img");
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
        var contentUrl = await page.Locator("a[download]").First.GetAttributeAsync("href").ConfigureAwait(false);
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
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.querySelector('dialog')?.contains(document.activeElement) === true")
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
        var viewports = new[]
        {
            new ViewportSize { Width = 1440, Height = 900 },
            new ViewportSize { Width = 820, Height = 1180 },
            new ViewportSize { Width = 390, Height = 844 },
            new ViewportSize { Width = 844, Height = 390 },
            new ViewportSize { Width = 320, Height = 700 }
        };
        var routes = new[]
        {
            "/", "/operations", "/operations/quarantine?kind=Artifact", "/gallery?pageSize=24", detailUrl,
            "/schedule", "/calibration", "/system", "/Account/Login", "/Account/Recovery",
            "/Account/Manage", "/Account/Manage/Email", "/Account/Manage/ChangePassword"
        };
        foreach (var viewport in viewports)
        {
            await page.SetViewportSizeAsync(viewport.Width, viewport.Height).ConfigureAwait(false);
            foreach (var route in routes)
            {
                await page.GotoAsync(route).ConfigureAwait(false);
                await VisibleAsync(page.Locator("main#mainContent h1").First).ConfigureAwait(false);
                Assert.IsTrue(await page.EvaluateAsync<bool>(
                    "() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1")
                    .ConfigureAwait(false), $"Horizontal overflow at {viewport.Width}x{viewport.Height} on {route}.");
                await AssertPageStructureAsync(page, route)
                    .ConfigureAwait(false);
                await AssertComputedContrastAsync(page, route, viewport).ConfigureAwait(false);
                if (string.Equals(route, "/", StringComparison.Ordinal))
                {
                    await AssertCurrentSkyResponsiveAsync(page, viewport).ConfigureAwait(false);
                }
            }
        }
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
            await page.Locator(".gallery-state, .capture-grid").First.WaitForAsync().ConfigureAwait(false);
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

    private static async Task AssertComputedContrastAsync(IPage page, string route, ViewportSize viewport)
    {
        var result = await page.EvaluateAsync<string>("""
            () => {
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
              const pairs = [...document.querySelectorAll(
                'main h1, main h2, main h3, main h4, main a, main p, main dt, main dd, main label, main button:not(:disabled), main input, main select, main textarea, main code, main time, main strong, main span, main .alert, main .state-chip, main .decision-chip')]
                .filter(element => element.getClientRects().length > 0 &&
                  (['INPUT', 'SELECT', 'TEXTAREA'].includes(element.tagName) || (element.textContent || '').trim().length > 0));
              const measured = pairs.map(element => Object.assign(measure(element), {
                  element: `${element.tagName.toLowerCase()}.${[...element.classList].join('.')}`,
                  text: (element.textContent || '').trim().slice(0, 80)
                })).sort((left, right) => left.ratio - right.ratio);
              return JSON.stringify(measured[0] || { ratio: 21, element: 'none', text: '' });
            }
            """).ConfigureAwait(false);
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
              const toggle = document.querySelector('.shell-menu__toggle');
              const menu = document.querySelector('.shell-menu');
              if (!toggle || !menu || matchMedia('(max-width: 767.98px)').matches !== expectedMobile) return false;
              const toggleVisible = getComputedStyle(toggle).display !== 'none';
              return toggleVisible === expectedMobile &&
                (!expectedMobile || toggle.getBoundingClientRect().width <= menu.getBoundingClientRect().width + 1);
            }
            """,
            viewportWidth <= 767).ConfigureAwait(false);
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
