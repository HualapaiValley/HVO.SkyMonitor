using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Authentication;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Authentication;

/// <summary>
/// API keys through the real request pipeline: the key middleware, the owner bootstrap gate, the operations policies
/// and a real identity database created by the canonical migration, over a loopback listener.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentApiKeyAuthenticationTests
{
    private const string ReadPath = "/api/v1/operations/test/read";
    private const string MutatePath = "/api/v1/operations/test/mutate";
    private const string SessionOnlyPath = "/api/v1/operations/test/session-only";
    private const string InternalPath = "/api/internal/test/read";

    [TestMethod]
    public async Task ReadKey_ReadsAsTheOwnerButCannotChangeAnythingAsync()
    {
        var fixture = await PipelineFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureLease = fixture.ConfigureAwait(false);
        var key = await fixture.CreateKeyAsync(ApiKeyAccessLevel.Read).ConfigureAwait(false);
        using var client = fixture.Client(bearer: key);

        using var read = await client.GetAsync(new Uri(ReadPath, UriKind.Relative)).ConfigureAwait(false);
        using var change = await client.PostAsync(new Uri(MutatePath, UriKind.Relative), null).ConfigureAwait(false);

        Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
        var probe = await read.Content.ReadFromJsonAsync<Probe>().ConfigureAwait(false);
        Assert.AreEqual(new Probe(fixture.OwnerId, ApiKey: true), probe);
        Assert.AreEqual(HttpStatusCode.Forbidden, change.StatusCode);
    }

    [TestMethod]
    public async Task ReadWriteKey_ChangesWithoutAnAntiforgeryToken_WhileTheSessionStillNeedsOneAsync()
    {
        var fixture = await PipelineFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureLease = fixture.ConfigureAwait(false);
        var key = await fixture.CreateKeyAsync(ApiKeyAccessLevel.ReadWrite).ConfigureAwait(false);
        using var keyClient = fixture.Client(bearer: key);
        using var sessionClient = await fixture.SignedInClientAsync().ConfigureAwait(false);

        using var keyChange = await keyClient.PostAsync(new Uri(MutatePath, UriKind.Relative), null).ConfigureAwait(false);
        using var sessionChange = await sessionClient.PostAsync(new Uri(MutatePath, UriKind.Relative), null)
            .ConfigureAwait(false);
        using var sessionRead = await sessionClient.GetAsync(new Uri(ReadPath, UriKind.Relative)).ConfigureAwait(false);

        Assert.AreEqual(HttpStatusCode.OK, keyChange.StatusCode);
        Assert.AreEqual(new Probe(fixture.OwnerId, ApiKey: true),
            await keyChange.Content.ReadFromJsonAsync<Probe>().ConfigureAwait(false));
        // A browser session without an antiforgery token is still refused: only a key skips it.
        Assert.AreEqual(HttpStatusCode.BadRequest, sessionChange.StatusCode);
        Assert.AreEqual(new Probe(fixture.OwnerId, ApiKey: false),
            await sessionRead.Content.ReadFromJsonAsync<Probe>().ConfigureAwait(false));
        var id = KeyId(key);
        Assert.IsTrue(fixture.Logs.Any(entry => entry.Level == LogLevel.Information &&
            entry.Message.Contains($"API key {id} with ReadWrite access authenticated POST {MutatePath}",
                StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow("query-access-token")]
    [DataRow("query-api-key")]
    [DataRow("query-key")]
    [DataRow("x-api-key-header")]
    [DataRow("apikey-scheme")]
    [DataRow("raw-authorization")]
    public async Task Key_IsNeverAcceptedFromTheQueryStringOrAnyOtherPlaceAsync(string placement)
    {
        var fixture = await PipelineFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureLease = fixture.ConfigureAwait(false);
        var key = await fixture.CreateKeyAsync(ApiKeyAccessLevel.ReadWrite).ConfigureAwait(false);
        using var client = fixture.Client();
        var escaped = Uri.EscapeDataString(key);
        using var request = new HttpRequestMessage(HttpMethod.Get, placement switch
        {
            "query-access-token" => $"{ReadPath}?access_token={escaped}",
            "query-api-key" => $"{ReadPath}?api_key={escaped}",
            "query-key" => $"{ReadPath}?key={escaped}",
            _ => ReadPath
        });
        switch (placement)
        {
            case "x-api-key-header":
                request.Headers.Add(ApiKeyAuthenticationOptions.HeaderName, key);
                break;
            case "apikey-scheme":
                request.Headers.Authorization = new AuthenticationHeaderValue("ApiKey", key);
                break;
            case "raw-authorization":
                Assert.IsTrue(request.Headers.TryAddWithoutValidation("Authorization", key));
                break;
        }

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    [DataRow("malformed")]
    [DataRow("unknown")]
    [DataRow("wrong-secret")]
    [DataRow("revoked")]
    public async Task InvalidKey_IsRefusedAlikeAndNeverWrittenToTheLogAsync(string kind)
    {
        var fixture = await PipelineFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureLease = fixture.ConfigureAwait(false);
        var issued = await fixture.CreateKeyAsync(ApiKeyAccessLevel.ReadWrite).ConfigureAwait(false);
        var other = CameraAgentApiKeyToken.Create().Token;
        var secretStart = CameraAgentApiKeyToken.Prefix.Length + CameraAgentApiKeyToken.IdLength + 1;
        var presented = kind switch
        {
            "malformed" => issued[..^3],
            "unknown" => other,
            "wrong-secret" => string.Concat(issued.AsSpan(0, secretStart), other.AsSpan(secretStart)),
            _ => issued
        };
        if (kind == "revoked")
        {
            Assert.IsTrue(await fixture.RevokeAsync(KeyId(issued)).ConfigureAwait(false));
        }
        using var client = fixture.Client(bearer: presented);
        // The owner's session on the same request does not rescue a bad key.
        using var signedIn = await fixture.SignedInClientAsync(bearer: presented).ConfigureAwait(false);

        using var response = await client.GetAsync(new Uri(ReadPath, UriKind.Relative)).ConfigureAwait(false);
        using var withSession = await signedIn.GetAsync(new Uri(ReadPath, UriKind.Relative)).ConfigureAwait(false);

        foreach (var refused in new[] { response, withSession })
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.AreEqual("Bearer error=\"invalid_token\"", refused.Headers.WwwAuthenticate.Single().ToString());
            Assert.IsTrue(refused.Headers.CacheControl!.NoStore);
            StringAssert.Contains(await refused.Content.ReadAsStringAsync().ConfigureAwait(false),
                "The API key is not valid.", StringComparison.Ordinal);
        }
        var rejections = fixture.Logs.Where(entry => entry.Message.StartsWith("Rejected an API key", StringComparison.Ordinal))
            .ToArray();
        Assert.HasCount(2, rejections);
        Assert.IsTrue(rejections.All(entry => entry.Level == LogLevel.Warning));
        if (kind != "malformed")
        {
            Assert.IsTrue(rejections.All(entry => entry.Message.Contains($"key {KeyId(presented)}", StringComparison.Ordinal)));
        }
        foreach (var secret in new[] { issued, presented, other }.Select(token => token[secretStart..]))
        {
            Assert.IsFalse(fixture.Logs.Any(entry => entry.Message.Contains(secret, StringComparison.Ordinal) ||
                entry.State.Contains(secret, StringComparison.Ordinal)));
        }
    }

    [TestMethod]
    public async Task Key_DoesNotAuthenticateOutsideTheOperationsApiAsync()
    {
        var fixture = await PipelineFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureLease = fixture.ConfigureAwait(false);
        var key = await fixture.CreateKeyAsync(ApiKeyAccessLevel.ReadWrite).ConfigureAwait(false);
        using var keyClient = fixture.Client(bearer: key);
        using var sessionClient = await fixture.SignedInClientAsync().ConfigureAwait(false);

        using var keyInternal = await keyClient.GetAsync(new Uri(InternalPath, UriKind.Relative)).ConfigureAwait(false);
        using var sessionInternal = await sessionClient.GetAsync(new Uri(InternalPath, UriKind.Relative)).ConfigureAwait(false);

        Assert.AreEqual(HttpStatusCode.Unauthorized, keyInternal.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, sessionInternal.StatusCode);
    }

    [TestMethod]
    public async Task Key_CannotReachASessionOnlyPolicyEvenUnderTheOperationsApiAsync()
    {
        var fixture = await PipelineFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureLease = fixture.ConfigureAwait(false);
        var key = await fixture.CreateKeyAsync(ApiKeyAccessLevel.ReadWrite).ConfigureAwait(false);
        using var keyClient = fixture.Client(bearer: key);
        using var sessionClient = await fixture.SignedInClientAsync().ConfigureAwait(false);

        using var byKey = await keyClient.GetAsync(new Uri(SessionOnlyPath, UriKind.Relative)).ConfigureAwait(false);
        using var bySession = await sessionClient.GetAsync(new Uri(SessionOnlyPath, UriKind.Relative)).ConfigureAwait(false);

        Assert.AreEqual(HttpStatusCode.Forbidden, byKey.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, bySession.StatusCode);
    }

    [TestMethod]
    public async Task KeyWithASession_TheKeyAloneDecidesSoItNeverBorrowsTheSessionsAccessAsync()
    {
        var fixture = await PipelineFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureLease = fixture.ConfigureAwait(false);
        var key = await fixture.CreateKeyAsync(ApiKeyAccessLevel.Read).ConfigureAwait(false);
        using var client = await fixture.SignedInClientAsync(bearer: key).ConfigureAwait(false);

        using var change = await client.PostAsync(new Uri(MutatePath, UriKind.Relative), null).ConfigureAwait(false);
        using var read = await client.GetAsync(new Uri(ReadPath, UriKind.Relative)).ConfigureAwait(false);

        Assert.AreEqual(HttpStatusCode.Forbidden, change.StatusCode);
        Assert.AreEqual(new Probe(fixture.OwnerId, ApiKey: true),
            await read.Content.ReadFromJsonAsync<Probe>().ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow("embedded-space", false)]
    [DataRow("embedded-space", true)]
    [DataRow("embedded-tab", false)]
    [DataRow("embedded-tab", true)]
    [DataRow("trailing-junk", false)]
    [DataRow("trailing-junk", true)]
    [DataRow("comma-key-first", false)]
    [DataRow("comma-key-first", true)]
    [DataRow("comma-key-last", false)]
    [DataRow("comma-key-last", true)]
    [DataRow("duplicated", false)]
    [DataRow("duplicated", true)]
    [DataRow("other-scheme", false)]
    [DataRow("other-scheme", true)]
    [DataRow("raw", false)]
    [DataRow("raw", true)]
    [DataRow("double-quoted", false)]
    [DataRow("double-quoted", true)]
    [DataRow("single-quoted", false)]
    [DataRow("single-quoted", true)]
    [DataRow("token-equals", false)]
    [DataRow("token-equals", true)]
    [DataRow("bearer-colon", false)]
    [DataRow("bearer-colon", true)]
    [DataRow("bearer-semicolon", false)]
    [DataRow("bearer-semicolon", true)]
    public async Task MalformedKeyWithASession_NeverReachesReadOrMutationAsync(string kind, bool mutation)
    {
        var fixture = await PipelineFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureLease = fixture.ConfigureAwait(false);
        var key = await fixture.CreateKeyAsync(ApiKeyAccessLevel.Read).ConfigureAwait(false);
        var sessionCookies = new CookieContainer();
        using var client = await fixture.SignedInClientAsync(cookies: sessionCookies).ConfigureAwait(false);
        var antiforgery = await client.GetStringAsync(new Uri("/test/antiforgery", UriKind.Relative)).ConfigureAwait(false);
        client.DefaultRequestHeaders.Add("X-Test-Antiforgery", antiforgery);
        using var sessionChange = await client.PostAsync(new Uri(MutatePath, UriKind.Relative), null).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, sessionChange.StatusCode);
        var callsBefore = fixture.EndpointCalls.Count;
        string[] headers = kind switch
        {
            "embedded-space" => [$"Bearer {key[..25]} {key[25..]}"],
            "embedded-tab" => [$"Bearer {key[..25]}\t{key[25..]}"],
            "trailing-junk" => [$"Bearer {key} extra"],
            "comma-key-first" => [$"Bearer {key}, Bearer unrelated-test-token"],
            "comma-key-last" => [$"Bearer unrelated-test-token, Bearer {key}"],
            "duplicated" => ["Bearer unrelated-test-token", $"Bearer {key}"],
            "other-scheme" => [$"ApiKey {key}"],
            "double-quoted" => [$"Bearer \"{key}\""],
            "single-quoted" => [$"Bearer '{key}'"],
            "token-equals" => [$"Bearer token={key}"],
            "bearer-colon" => [$"Bearer:{key}"],
            "bearer-semicolon" => [$"Bearer;{key}"],
            _ => [key]
        };
        var method = mutation ? HttpMethod.Post : HttpMethod.Get;
        using var request = new HttpRequestMessage(method, method == HttpMethod.Get ? ReadPath : MutatePath);
        Assert.IsTrue(request.Headers.TryAddWithoutValidation("Authorization", headers));
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, $"{kind}: {method}");
        Assert.AreEqual("Bearer error=\"invalid_token\"", response.Headers.WwwAuthenticate.Single().ToString());
        Assert.IsTrue(response.Headers.CacheControl!.NoStore);
        Assert.AreEqual("{\"status\":401,\"title\":\"The API key is not valid.\"}",
            await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        Assert.AreEqual(callsBefore, fixture.EndpointCalls.Count);
        if (kind == "duplicated")
        {
            // HttpClient can coalesce the wire values; also exercise the middleware with two distinct values.
            await fixture.AssertDistinctAuthorizationValuesRefusedAsync(method, headers, sessionCookies, antiforgery)
                .ConfigureAwait(false);
        }
        var secret = key[(CameraAgentApiKeyToken.Prefix.Length + CameraAgentApiKeyToken.IdLength + 1)..];
        foreach (var credential in headers.Append(secret))
        {
            Assert.IsFalse(fixture.Logs.Any(entry => entry.Message.Contains(credential, StringComparison.Ordinal) ||
                entry.State.Contains(credential, StringComparison.Ordinal)));
        }
    }

    [TestMethod]
    [DataRow("Basic", "cHJveHk6cHJveHk=")]
    [DataRow("Bearer", "unrelated-test-token")]
    [DataRow("Bearer", "abc.hvo_sky_x.def")]
    public async Task OtherAuthorizationSchemes_AreLeftToTheSessionAsBeforeAsync(string scheme, string credential)
    {
        var fixture = await PipelineFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureLease = fixture.ConfigureAwait(false);
        using var client = await fixture.SignedInClientAsync().ConfigureAwait(false);
        // A reverse proxy in front of CameraAgent may add its own credentials.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(scheme, credential);

        using var read = await client.GetAsync(new Uri(ReadPath, UriKind.Relative)).ConfigureAwait(false);

        Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
        Assert.AreEqual(new Probe(fixture.OwnerId, ApiKey: false),
            await read.Content.ReadFromJsonAsync<Probe>().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task Key_StopsWhileTheOwnerMustReplaceThePasswordAndAfterTheAccountStopsBeingTheOwnerAsync()
    {
        var fixture = await PipelineFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureLease = fixture.ConfigureAwait(false);
        var key = await fixture.CreateKeyAsync(ApiKeyAccessLevel.ReadWrite).ConfigureAwait(false);
        using var client = fixture.Client(bearer: key);

        await fixture.UpdateOwnerAsync(owner => owner.PasswordChangeRequired = true).ConfigureAwait(false);
        using var pending = await client.GetAsync(new Uri(ReadPath, UriKind.Relative)).ConfigureAwait(false);
        await fixture.UpdateOwnerAsync(owner =>
        {
            owner.PasswordChangeRequired = false;
            owner.IsSiteOwner = false;
        }).ConfigureAwait(false);
        using var demoted = await client.GetAsync(new Uri(ReadPath, UriKind.Relative)).ConfigureAwait(false);

        Assert.AreEqual(HttpStatusCode.Forbidden, pending.StatusCode);
        Assert.AreEqual(OwnerBootstrapStates.PasswordChangeRequired,
            pending.Headers.GetValues(OwnerBootstrapGateMiddleware.AuthorizationReasonHeader).Single());
        Assert.AreEqual(HttpStatusCode.Forbidden, demoted.StatusCode);
    }

    [TestMethod]
    public async Task MoreThanOneAuthorizationValue_IsRefusedAsMalformedAsync()
    {
        var called = false;
        var middleware = new CameraAgentApiKeyAuthenticationMiddleware(
            _ =>
            {
                called = true;
                return Task.CompletedTask;
            },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CameraAgentApiKeyAuthenticationMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Path = ReadPath;
        context.Request.Headers.Authorization = new Microsoft.Extensions.Primitives.StringValues(
            ["Bearer " + CameraAgentApiKeyToken.Create().Token, "Basic cHJveHk6cHJveHk="]);
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, null!, null!, null!, new CameraAgentOperatorTelemetry())
            .ConfigureAwait(false);

        Assert.IsFalse(called);
        Assert.AreEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    private static string KeyId(string token)
    {
        Assert.IsTrue(CameraAgentApiKeyToken.TryReadId(token, out var id));
        return id;
    }

    private sealed record Probe(string? OwnerId, bool ApiKey);

    private sealed record LogEntry(LogLevel Level, string Message, string State);

    /// <summary>A minimal CameraAgent pipeline with the key middleware, over a temporary identity database.</summary>
    private sealed class PipelineFixture : IAsyncDisposable
    {
        private const string OwnerEmail = "owner@apikeys.test";

        private readonly WebApplication _app;
        private readonly string _root;

        private PipelineFixture(WebApplication app, string root, string ownerId, ConcurrentQueue<LogEntry> logs)
        {
            _app = app;
            _root = root;
            OwnerId = ownerId;
            LogQueue = logs;
        }

        internal string OwnerId { get; }

        internal IReadOnlyCollection<LogEntry> Logs => LogQueue;

        internal ConcurrentQueue<string> EndpointCalls { get; private init; } = new();

        private ConcurrentQueue<LogEntry> LogQueue { get; }

        internal static async Task<PipelineFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "hvo-api-key-pipeline", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var logs = new ConcurrentQueue<LogEntry>();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Debug);
            builder.Logging.AddProvider(new CapturingLoggerProvider(logs));
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddDbContext<ApplicationDbContext>(options =>
            {
                options.UseSqlite($"Data Source={Path.Combine(root, "identity.db")}");
                options.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
            });
            builder.Services.AddIdentityCore<ApplicationUser>()
                .AddEntityFrameworkStores<ApplicationDbContext>()
                .AddClaimsPrincipalFactory<CanonicalLocalUserClaimsPrincipalFactory>()
                .AddSignInManager();
            builder.Services.AddAuthentication(options => options.DefaultScheme = IdentityConstants.ApplicationScheme)
                .AddIdentityCookies();
            builder.Services.ConfigureApplicationCookie(options => Program.ConfigureApplicationCookie(options));
            builder.Services.AddSingleton(Options.Create(new LocalIdentityOptions { AdminEmail = OwnerEmail }));
            builder.Services.AddCameraAgentAuthorization();
            builder.Services.AddScoped<CameraAgentApiKeyStore>();
            builder.Services.AddSingleton<CameraAgentOperatorTelemetry>();
            builder.Services.AddAntiforgery(options => options.HeaderName = "X-Test-Antiforgery");
            var app = builder.Build();
            try
            {
                app.UseAuthentication();
                app.UseMiddleware<CameraAgentApiKeyAuthenticationMiddleware>();
                app.UseMiddleware<OwnerBootstrapGateMiddleware>();
                app.UseAuthorization();
                app.UseAntiforgery();
                var calls = new ConcurrentQueue<string>();
                MapEndpoints(app, calls);

                string ownerId;
                var scope = app.Services.CreateAsyncScope();
                await using (scope.ConfigureAwait(false))
                {
                    // The canonical migration, so the keys use the schema a deployed host has.
                    await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.MigrateAsync()
                        .ConfigureAwait(false);
                    var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                    var owner = new ApplicationUser
                    {
                        UserName = OwnerEmail,
                        Email = OwnerEmail,
                        EmailConfirmed = true,
                        IsSiteOwner = true
                    };
                    var created = await users.CreateAsync(owner).ConfigureAwait(false);
                    Assert.IsTrue(created.Succeeded);
                    ownerId = owner.Id;
                }

                await app.StartAsync().ConfigureAwait(false);
                return new PipelineFixture(app, root, ownerId, logs) { EndpointCalls = calls };
            }
            catch
            {
                await app.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        internal async Task<string> CreateKeyAsync(ApiKeyAccessLevel access)
        {
            var scope = _app.Services.CreateAsyncScope();
            await using var scopeLease = scope.ConfigureAwait(false);
            var result = await scope.ServiceProvider.GetRequiredService<CameraAgentApiKeyStore>()
                .CreateAsync(OwnerId, $"{access} key", access, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentApiKeyCreateStatus.Created, result.Status);
            return result.Token!;
        }

        internal async Task<bool> RevokeAsync(string id)
        {
            var scope = _app.Services.CreateAsyncScope();
            await using var scopeLease = scope.ConfigureAwait(false);
            return await scope.ServiceProvider.GetRequiredService<CameraAgentApiKeyStore>()
                .RevokeAsync(OwnerId, id, CancellationToken.None).ConfigureAwait(false);
        }

        internal async Task UpdateOwnerAsync(Action<ApplicationUser> change)
        {
            var scope = _app.Services.CreateAsyncScope();
            await using var scopeLease = scope.ConfigureAwait(false);
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var owner = await users.FindByIdAsync(OwnerId).ConfigureAwait(false);
            change(owner!);
            Assert.IsTrue((await users.UpdateAsync(owner!).ConfigureAwait(false)).Succeeded);
        }

        internal HttpClient Client(string? bearer = null)
            => Configure(new HttpClient { BaseAddress = new Uri(_app.Urls.Single()) }, bearer);

        /// <summary>A client holding the owner's session cookie, and optionally a key as well.</summary>
        internal async Task<HttpClient> SignedInClientAsync(string? bearer = null, CookieContainer? cookies = null)
        {
            var client = new HttpClient(new HttpClientHandler
            {
                CookieContainer = cookies ?? new CookieContainer(),
                CheckCertificateRevocationList = true
            })
            {
                BaseAddress = new Uri(_app.Urls.Single())
            };
            using var signIn = await client.PostAsync(new Uri("/test/sign-in", UriKind.Relative), null).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, signIn.StatusCode);
            return Configure(client, bearer);
        }

        internal async Task AssertDistinctAuthorizationValuesRefusedAsync(
            HttpMethod method,
            string[] authorization,
            CookieContainer cookies,
            string antiforgeryToken)
        {
            var scope = _app.Services.CreateAsyncScope();
            await using var scopeLease = scope.ConfigureAwait(false);
            var address = new Uri(_app.Urls.Single());
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            context.Request.Scheme = address.Scheme;
            context.Request.Host = HostString.FromUriComponent(address);
            context.Request.Path = method == HttpMethod.Get ? ReadPath : MutatePath;
            context.Request.Method = method.Method;
            context.Request.Headers.Cookie = cookies.GetCookieHeader(address);
            context.Request.Headers.Authorization = authorization;
            context.Request.Headers["X-Test-Antiforgery"] = antiforgeryToken;
            Assert.HasCount(2, context.Request.Headers.Authorization);
            var session = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false);
            Assert.IsTrue(session.Succeeded);
            context.User = session.Principal!;
            Assert.AreEqual(OwnerId, CameraAgentCredentialAccess.GetOwnerId(context.User));
            Assert.IsFalse(CameraAgentCredentialAccess.IsApiKey(context.User));
            await scope.ServiceProvider.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context).ConfigureAwait(false);
            context.Features.Set<IAntiforgeryValidationFeature>(new ValidatedAntiforgeryFeature());
            Assert.IsTrue(CameraAgentCredentialAccess.IsForgeryProtected(context));
            using var body = new MemoryStream();
            context.Response.Body = body;
            var endpointReached = false;
            var middleware = new CameraAgentApiKeyAuthenticationMiddleware(_ =>
            {
                endpointReached = true;
                return Task.CompletedTask;
            }, scope.ServiceProvider.GetRequiredService<ILogger<CameraAgentApiKeyAuthenticationMiddleware>>());

            await middleware.InvokeAsync(context,
                scope.ServiceProvider.GetRequiredService<CameraAgentApiKeyStore>(),
                scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
                scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>(),
                scope.ServiceProvider.GetRequiredService<CameraAgentOperatorTelemetry>()).ConfigureAwait(false);

            Assert.AreEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
            Assert.AreEqual("Bearer error=\"invalid_token\"", context.Response.Headers.WWWAuthenticate.ToString());
            Assert.IsFalse(endpointReached);
        }

        private static HttpClient Configure(HttpClient client, string? bearer)
        {
            if (bearer is not null)
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            }
            return client;
        }

        private static void MapEndpoints(WebApplication app, ConcurrentQueue<string> calls)
        {
            app.MapPost("/test/sign-in", static async (
                    SignInManager<ApplicationUser> signIn,
                    UserManager<ApplicationUser> users) =>
                {
                    await signIn.SignInAsync((await users.FindByEmailAsync(OwnerEmail).ConfigureAwait(false))!, false)
                        .ConfigureAwait(false);
                    return Results.Ok();
                })
                .AllowAnonymous();
            app.MapGet("/test/antiforgery", static (HttpContext context, IAntiforgery antiforgery) =>
                    Results.Text(antiforgery.GetAndStoreTokens(context).RequestToken!))
                .RequireAuthorization();
            var operations = app.MapGroup("/api/v1/operations/test");
            operations.MapGet("/read", (HttpContext context) =>
                {
                    calls.Enqueue(ReadPath);
                    return Results.Ok(ProbeOf(context.User));
                })
                .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1);
            operations.MapPost("/mutate", (HttpContext context) =>
                {
                    calls.Enqueue(MutatePath);
                    return CameraAgentCredentialAccess.IsForgeryProtected(context)
                        ? Results.Ok(ProbeOf(context.User))
                        : Results.BadRequest();
                })
                .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
                .WithMetadata(new RequireAntiforgeryTokenAttribute());
            operations.MapGet("/session-only", static () => Results.Ok())
                .RequireAuthorization(CameraAgentAuthorizationPolicyNames.ApiKeyManagementV1);
            app.MapGet(InternalPath, static (HttpContext context) => Results.Ok(ProbeOf(context.User)))
                .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1);
        }

        private static Probe ProbeOf(ClaimsPrincipal user)
            => new(CameraAgentCredentialAccess.GetOwnerId(user), CameraAgentCredentialAccess.IsApiKey(user));

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class ValidatedAntiforgeryFeature : IAntiforgeryValidationFeature
    {
        public bool IsValid => true;
        public Exception? Error => null;
    }

    private sealed class CapturingLoggerProvider(ConcurrentQueue<LogEntry> entries) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? string.Join("|", pairs.Select(pair => $"{pair.Key}={pair.Value}"))
                    : string.Empty;
                entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), values));
            }
        }
    }
}
