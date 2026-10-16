using System.Net;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class OwnerBootstrapClientTests
{
    private const string Token = "synthetic-verification-secret";
    private const string TimeoutMessage = "CameraAgent installation verification request timed out.";
    private static readonly Uri BaseAddress = new("http://verification.invalid");
    private static readonly InstallationVerificationExpectation Expectation = new(
        "test-agent", "owner@example.test", "owner-ready",
        new string('a', 64), new string('b', 64), new string('c', 64),
        "test-location", 1, new string('d', 64), HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.InProcess,
        new CatalogInstallationIdentity(
            HygV42.CatalogId, HygV42.PackageVersion, "2", "3",
            HygV42.DatabaseSha256, HygV42.DatabaseLength, HygV42.RowCount,
            "/test/catalog", new string('e', 64), "local-offline"));

    [TestMethod]
    public void ProtectedReadTimeout_ProductionPolicyIs120Seconds()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(120), OwnerBootstrapClient.ProtectedReadTimeout);
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(0)]
    [DataRow(120001)]
    public void ProtectedReadTimeout_TestOverrideCannotRemoveOrExtendDeadline(int milliseconds)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new OwnerBootstrapClient(BaseAddress, protectedReadTimeout: TimeSpan.FromMilliseconds(milliseconds)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProtectedRead_SlowResponseSucceedsWithinBudget(bool verify)
    {
        // Scale the former 15s and measured 35s response down to milliseconds, keeping the 120:15 ratio.
        var oldBudget = TimeSpan.FromMilliseconds(100);
        using var oldDeadline = new CancellationTokenSource();
        using var handler = new ScriptedHandler(async (request, _, cancellationToken) =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/internal/owner-bootstrap/installation-verification", request.RequestUri?.AbsolutePath);
            Assert.AreEqual(Token, request.Headers.GetValues("X-HVO-Installation-Token").Single());
            oldDeadline.CancelAfter(oldBudget);
            await Task.Delay(TimeSpan.FromMilliseconds(235), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ValidBody()) };
        });
        var client = new OwnerBootstrapClient(BaseAddress, handler, oldBudget * 8);

        var state = await ReadAsync(client, verify, CancellationToken.None);

        Assert.AreEqual("owner-ready", state);
        Assert.IsTrue(oldDeadline.IsCancellationRequested, "The response must outlast the scaled old budget.");
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProtectedRead_HeadersAndBodyShareOneDeadline(bool verify)
    {
        using var handler = new ScriptedHandler(async (_, _, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(60), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new DelayedContent(token => Task.Delay(TimeSpan.FromMilliseconds(60), token), ValidBody())
            };
        });
        var client = new OwnerBootstrapClient(BaseAddress, handler, TimeSpan.FromMilliseconds(100));

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => ReadAsync(client, verify, CancellationToken.None));

        Assert.AreEqual(TimeoutMessage, exception.Message);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    [DataRow(false, 302)]
    [DataRow(true, 302)]
    [DataRow(false, 401)]
    [DataRow(true, 401)]
    [DataRow(false, 403)]
    [DataRow(true, 403)]
    public async Task ProtectedRead_RejectsRedirectAndAuthenticationFailure(bool verify, int statusCode)
    {
        using var handler = new ScriptedHandler((_, _, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)statusCode)
        {
            Content = new StringContent(ValidBody())
        }));
        var client = new OwnerBootstrapClient(BaseAddress, handler);

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => ReadAsync(client, verify, CancellationToken.None));

        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task ProtectedRead_DeadlineCoversHeadersAndBodyWithoutLeakingSecrets(bool verify, bool delayBody)
    {
        using var handler = new ScriptedHandler(async (_, _, cancellationToken) =>
        {
            if (!delayBody) await WaitForCancellationAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new DelayedContent(WaitForCancellationAsync, ValidBody())
            };
        });
        var client = new OwnerBootstrapClient(BaseAddress, handler, TimeSpan.FromMilliseconds(100));

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => ReadAsync(client, verify, CancellationToken.None));

        Assert.AreEqual(TimeoutMessage, exception.Message);
        Assert.IsNull(exception.InnerException);
        Assert.DoesNotContain(Token, exception.ToString(), StringComparison.Ordinal);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task ProtectedRead_CallerCancellationRemainsCancellation(bool verify, bool delayBody)
    {
        using var caller = new CancellationTokenSource();
        using var handler = new ScriptedHandler(async (_, _, cancellationToken) =>
        {
            if (!delayBody)
            {
                await caller.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new DelayedContent(async token =>
                {
                    await caller.CancelAsync();
                    token.ThrowIfCancellationRequested();
                }, ValidBody())
            };
        });
        var client = new OwnerBootstrapClient(BaseAddress, handler);

        await Assert.ThrowsAsync<OperationCanceledException>(() => ReadAsync(client, verify, caller.Token));

        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProtectedRead_PreCanceledCallerDoesNotSend(bool verify)
    {
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();
        using var handler = new ScriptedHandler((_, _, _) => throw new AssertFailedException("Request must not be sent."));
        var client = new OwnerBootstrapClient(BaseAddress, handler);

        await Assert.ThrowsAsync<OperationCanceledException>(() => ReadAsync(client, verify, caller.Token));

        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProtectedRead_UnrelatedCancellationIsNotReportedAsDeadline(bool verify)
    {
        using var handler = new ScriptedHandler((_, _, _) => throw new OperationCanceledException("transport cancellation"));
        var client = new OwnerBootstrapClient(BaseAddress, handler);

        await Assert.ThrowsAsync<OperationCanceledException>(() => ReadAsync(client, verify, CancellationToken.None));
    }

    [TestMethod]
    public async Task VerifyInstallation_DefaultBudgetStillRejectsIdentityMismatch()
    {
        using var handler = new ScriptedHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ValidBody())
        }));
        var client = new OwnerBootstrapClient(BaseAddress, handler);

        Assert.AreEqual("owner-ready", await client.VerifyInstallationAsync(Token, Expectation, CancellationToken.None));
        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => client.VerifyInstallationAsync(
            Token, Expectation with { AgentId = "different-agent" }, CancellationToken.None));

        Assert.AreEqual("CameraAgent reported installation identities that differ from the installer manifest.", exception.Message);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ReadState_PostsTheOwnerIdentifierAsInputEmailAndRequiresTheSignInRedirect(bool signInAccepted)
    {
        // CameraAgent keeps the sign-in field's original Input.Email name, now accepting a user name or an email, so
        // an installer, a split-host deployment, and a CameraAgent image from either side of issue #1278 still agree.
        using var listener = new HttpListener();
        var port = FreeLoopbackPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");
        listener.Prefixes.Add(baseAddress.ToString());
        listener.Start();
        string? postedForm = null;
        var server = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (HttpListenerException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                var response = context.Response;
                var path = context.Request.Url!.AbsolutePath;
                string body;
                if (context.Request.HttpMethod == "GET" && path == "/Account/Login")
                {
                    body = "<form><input name=\"__RequestVerificationToken\" type=\"hidden\" value=\"anti&amp;forgery\" /></form>";
                }
                else if (context.Request.HttpMethod == "POST" && path == "/Account/Login")
                {
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    postedForm = await reader.ReadToEndAsync();
                    if (signInAccepted)
                    {
                        response.StatusCode = 302;
                        response.RedirectLocation = "/Account/ReplaceTemporaryPassword";
                        response.AppendHeader("Set-Cookie", "owner=session; path=/");
                        body = string.Empty;
                    }
                    else
                    {
                        // A refused or locked-out sign-in re-renders the form instead of redirecting.
                        body = "<p>Error: This account is locked after 5 failed sign-in attempts.</p>";
                    }
                }
                else if (path == "/api/internal/owner-bootstrap/status" && context.Request.Headers["Cookie"] == "owner=session")
                {
                    body = "{\"state\":\"owner-temporary-password\",\"passwordChangeRequired\":true}";
                }
                else
                {
                    response.StatusCode = 404;
                    body = string.Empty;
                }

                var bytes = Encoding.UTF8.GetBytes(body);
                response.ContentLength64 = bytes.Length;
                await response.OutputStream.WriteAsync(bytes);
                response.Close();
            }
        });
        try
        {
            var client = new OwnerBootstrapClient(baseAddress);
            if (signInAccepted)
            {
                var state = await client.ReadStateAsync("owner@cameraagent.local", "skymonitor", CancellationToken.None);
                Assert.AreEqual("owner-temporary-password", state);
            }
            else
            {
                var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() =>
                    client.ReadStateAsync("owner@cameraagent.local", "skymonitor", CancellationToken.None));
                Assert.AreEqual("The CameraAgent login response was invalid.", exception.Message);
            }

            Assert.IsNotNull(postedForm);
            var fields = postedForm.Split('&')
                .Select(static pair => pair.Split('=', 2))
                .ToDictionary(static pair => WebUtility.UrlDecode(pair[0]), static pair => WebUtility.UrlDecode(pair[1]));
            Assert.AreEqual("anti&forgery", fields["__RequestVerificationToken"]);
            Assert.AreEqual("owner@cameraagent.local", fields["Input.Email"]);
            Assert.AreEqual("skymonitor", fields["Input.Password"]);
            Assert.AreEqual("login", fields["_handler"]);
        }
        finally
        {
            listener.Stop();
            await server;
        }
    }

    private static int FreeLoopbackPort()
    {
        using var socket = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static Task<string> ReadAsync(OwnerBootstrapClient client, bool verify, CancellationToken cancellationToken)
        => verify
            ? client.VerifyInstallationAsync(Token, Expectation, cancellationToken)
            : client.ReadInstallationStateAsync(Token, cancellationToken);

    private static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw new OperationCanceledException(Token, cancellationToken);
        }
    }

    private static string ValidBody() => JsonSerializer.Serialize(new
    {
        agentId = Expectation.AgentId,
        ownerEmail = Expectation.OwnerEmail,
        ownerBootstrapState = Expectation.OwnerBootstrapState,
        configurationSha256 = Expectation.ConfigurationSha256,
        rigProfileSha256 = Expectation.RigProfileSha256,
        scheduleSha256 = Expectation.ScheduleSha256,
        deploymentLocationId = Expectation.DeploymentLocationId,
        deploymentLocationVersion = Expectation.DeploymentLocationVersion,
        deploymentLocationSha256 = Expectation.DeploymentLocationSha256,
        rawIngressRoot = "/app/data/raw",
        replayProfile = Expectation.ReplayProfile.ToString(),
        catalogId = Expectation.Catalog.CatalogId,
        packageVersion = Expectation.Catalog.PackageVersion,
        schemaVersion = Expectation.Catalog.SchemaVersion,
        preprocessingVersion = Expectation.Catalog.PreprocessingVersion,
        databaseSha256 = Expectation.Catalog.DatabaseSha256,
        databaseLength = Expectation.Catalog.DatabaseLength,
        rowCount = Expectation.Catalog.RowCount
    });

    private sealed class DelayedContent(Func<CancellationToken, Task> wait, string body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new AssertFailedException("The response body must receive the request cancellation token.");

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await wait(cancellationToken);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(body), cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
