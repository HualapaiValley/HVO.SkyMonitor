using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Cli;
using HVO.SkyMonitor.CameraAgent.Cli.Client;

namespace HVO.SkyMonitor.CameraAgent.Tests.Cli;

/// <summary>What one run of <c>hvo-sky</c> returned and wrote.</summary>
internal sealed record CliResult(int ExitCode, string Out, string Error)
{
    public string All => Out + Error;

    public JsonElement Json()
    {
        using var document = JsonDocument.Parse(Out);
        return document.RootElement.Clone();
    }

    public override string ToString() => $"exit {ExitCode}\n--- out ---\n{Out}\n--- error ---\n{Error}";
}

/// <summary>One request the fake API received.</summary>
internal sealed record RecordedRequest(
    string Method,
    string PathAndQuery,
    string? Authorization,
    string? Body,
    string? IdempotencyKey,
    string? UserAgent)
{
    public JsonElement BodyJson()
    {
        using var document = JsonDocument.Parse(Body ?? "{}");
        return document.RootElement.Clone();
    }
}

/// <summary>
/// A fake CameraAgent operations API: routes by method and path, records every request, and can be made unreachable,
/// to refuse everything with one status, or to hang until the request is cancelled.
/// </summary>
internal sealed class FakeCameraAgent
{
    private readonly Dictionary<string, Func<RecordedRequest, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly List<RecordedRequest> _requests = [];

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    /// <summary>Every request fails as a connection that could not be made.</summary>
    public bool Unreachable { get; set; }

    /// <summary>Every request is answered with this status and a problem document.</summary>
    public HttpStatusCode? RefuseAll { get; set; }

    /// <summary>Every request waits until it is cancelled.</summary>
    public bool Hang { get; set; }

    /// <summary>Every answer's headers arrive, and its body never does: reading it waits until the read is cancelled.</summary>
    public bool StallBodies { get; set; }

    /// <summary>Raised as each request arrives, before it is answered.</summary>
    public Action<RecordedRequest>? OnRequest { get; set; }

    public FakeCameraAgent Json(string method, string path, object body, HttpStatusCode status = HttpStatusCode.OK)
        => Handle(method, path, _ => Response(status, body));

    public FakeCameraAgent Problem(string method, string path, HttpStatusCode status, string title, string? code = null, object? extra = null)
        => Handle(method, path, _ => ProblemResponse(status, title, code, extra));

    public FakeCameraAgent Handle(string method, string path, Func<RecordedRequest, HttpResponseMessage> respond)
    {
        _routes[$"{method} {path}"] = respond;
        return this;
    }

    public IReadOnlyList<RecordedRequest> RequestsTo(string method, string path)
        => Requests.Where(request => request.Method == method && request.PathAndQuery.Split('?')[0] == path).ToArray();

    public HttpMessageHandler CreateHandler() => new Forwarder(this);

    public static HttpResponseMessage Response(HttpStatusCode status, object body)
        => new(status) { Content = new StringContent(JsonSerializer.Serialize(body, SkyApiClient.Json), Encoding.UTF8, "application/json") };

    public static HttpResponseMessage ProblemResponse(HttpStatusCode status, string title, string? code = null, object? extra = null)
    {
        var problem = new Dictionary<string, object?> { ["status"] = (int)status, ["title"] = title };
        if (code is not null)
        {
            problem["code"] = code;
        }

        if (extra is not null)
        {
            foreach (var property in JsonSerializer.SerializeToElement(extra, SkyApiClient.Json).EnumerateObject())
            {
                problem[property.Name] = property.Value;
            }
        }

        return new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(problem), Encoding.UTF8, "application/problem+json")
        };
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var recorded = new RecordedRequest(
            request.Method.Method,
            request.RequestUri!.PathAndQuery,
            request.Headers.Authorization?.ToString(),
            body,
            request.Headers.TryGetValues(SkyApiClient.IdempotencyKeyHeader, out var keys) ? keys.Single() : null,
            request.Headers.UserAgent.ToString());
        lock (_gate)
        {
            _requests.Add(recorded);
        }

        OnRequest?.Invoke(recorded);
        if (Hang)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }

        if (Unreachable)
        {
            throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused (127.0.0.1:5130)");
        }

        if (RefuseAll is { } status)
        {
            return ProblemResponse(status, status == HttpStatusCode.Unauthorized ? "Unauthorized" : "Refused");
        }

        var response = _routes.TryGetValue($"{recorded.Method} {request.RequestUri.AbsolutePath}", out var respond)
            ? respond(recorded)
            : ProblemResponse(HttpStatusCode.NotFound, "No such route in the fake API.");
        if (StallBodies)
        {
            response.Content.Dispose();
            response.Content = new StalledContent();
        }

        return response;
    }

    // A fresh handler per client that never disposes the fake, which every command's client would otherwise do.
    private sealed class Forwarder(FakeCameraAgent fake) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => fake.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// Runs <c>hvo-sky</c> in the test process against a <see cref="FakeCameraAgent"/>, with a home directory of its own
/// for the credentials file, an environment of its own, and scripted answers to its prompts.
/// </summary>
internal sealed class CliHarness : IDisposable
{
    public const string Url = "http://127.0.0.1:5130/";

    public const string KeyId = "0123456789abcdef";

    public CliHarness(bool signedIn = true)
    {
        Home = Path.Combine(Path.GetTempPath(), $"hvo-sky-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Home);
        Fake.Install(Api, this);
        if (signedIn)
        {
            SaveCredentials(Url, Key);
        }
    }

    /// <summary>A well-formed key, built here rather than written out, so no key-shaped literal is committed.</summary>
    public static string Key { get; } = MakeKey(KeyId, 'k');

    public static string MakeKey(string id, char fill) => SkyApiKey.Prefix + id + "_" + new string(fill, 43);

    public FakeCameraAgent Api { get; } = new();

    public Dictionary<string, string?> Environment { get; } = new(StringComparer.Ordinal);

    public string Home { get; }

    public Queue<string?> Input { get; } = new();

    public List<(string Prompt, bool Secret)> Prompts { get; } = [];

    public string StandardInput { get; set; } = string.Empty;

    public bool Interactive { get; set; }

    /// <summary>How long the person at the terminal takes to answer each prompt.</summary>
    public TimeSpan AnswerDelay { get; set; }

    /// <summary>The user hvo-sky takes itself to run as: another than this process's makes its own files someone else's.</summary>
    public uint UserId { get; set; } = SkyFileSystem.CurrentUserId;

    /// <summary>The clock hvo-sky reads the time from (its deadlines run on its timers).</summary>
    public TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>Whether standard input and output are a terminal, for <c>hvo-sky ui</c>.</summary>
    public bool HasTerminal { get; set; }

    /// <summary>For <c>hvo-sky ui</c>: the Terminal.Gui application, which tests make on the in-memory driver.</summary>
    public Func<Terminal.Gui.App.IApplication>? CreateApplication { get; set; }

    /// <summary>For <c>hvo-sky ui</c>: what runs the interface, which tests replace to drive it.</summary>
    public Action<Terminal.Gui.App.IApplication, Terminal.Gui.App.IRunnable>? RunApplication { get; set; }

    /// <summary>The addresses bound to this machine's interfaces, as hvo-sky sees them; none unless a test names some.</summary>
    public IReadOnlyList<IPAddress> LocalAddresses { get; set; } = [];

    /// <summary>The proxy hvo-sky takes its connections to go through; none unless a test names one.</summary>
    public IWebProxy? Proxy { get; set; }

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(5);

    /// <summary>The fake's current process start time, which a restart replaces.</summary>
    public DateTimeOffset StartedUtc { get; set; } = new(2026, 10, 18, 18, 0, 0, TimeSpan.Zero);

    public bool Supervised { get; set; } = true;

    /// <summary>How many info reads after a restart request still see the old process; -1 never sees a new one.</summary>
    public int RestartReadsBeforeNewProcess { get; set; } = 1;

    public string CredentialsPath => Path.Combine(Home, ".config", "hvo-sky", "credentials.json");

    public void SaveCredentials(string? url, string? key)
        => SkyCredentialStore.Save(CredentialsPath, new SkyStoredCredentials { Url = url is null ? null : new Uri(url), ApiKey = key });

    public string WriteFile(string name, string content)
    {
        var path = Path.Combine(Home, name);
        File.WriteAllText(path, content);
        return path;
    }

    public Task<CliResult> RunAsync(params string[] args) => RunAsync(CancellationToken.None, args);

    public async Task<CliResult> RunAsync(CancellationToken cancellationToken, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var host = CreateHost(output, error);
        var code = await SkyCli.RunAsync(args, host, cancellationToken).ConfigureAwait(false);
        return new CliResult(code, output.ToString(), error.ToString());
    }

    /// <summary>The machine hvo-sky runs on: this harness's home, environment, answers, clock and fake API.</summary>
    public SkyCliHost CreateHost(TextWriter output, TextWriter error)
    {
        var host = new SkyCliHost
        {
            Out = output,
            Error = error,
            GetEnvironmentVariable = name => Environment.TryGetValue(name, out var value) ? value : null,
            UserProfile = Home,
            ReadLine = (prompt, secret) =>
            {
                Prompts.Add((prompt, secret));
                Thread.Sleep(AnswerDelay);
                return Input.Count > 0 ? Input.Dequeue() : null;
            },
            IsInteractive = Interactive,
            ReadAllInput = () => StandardInput,
            PollInterval = PollInterval,
            UserId = UserId,
            Time = Time,
            HasTerminal = HasTerminal,
            LocalAddresses = () => LocalAddresses,
            Proxy = () => Proxy,
            ClientVersion = "0.9.0-test+" + new string('c', 40),
            CreateHandler = Api.CreateHandler,
            CreateApplication = CreateApplication ?? SkyCliHost.CreateTerminalApplication,
            RunApplication = RunApplication ?? SkyCliHost.RunTerminalApplication
        };
        return host;
    }

    public void Dispose()
    {
        if (Directory.Exists(Home))
        {
            Directory.Delete(Home, recursive: true);
        }
    }
}

/// <summary>The fake API's documents, shaped as CameraAgent serves them.</summary>
internal static class Fake
{
    public const string SettingsRoute = "/api/v1/operations/settings";
    public const string SummaryRoute = "/api/v1/operations/summary";
    public const string InfoRoute = "/api/v1/operations/info";
    public const string RestartRoute = "/api/v1/operations/restart";
    public const string RigRoute = "/api/v1/operations/rig-profiles";
    public const string ScheduleRoute = "/api/v1/operations/schedule";

    private static readonly string[] GroupNames = ["site", "location", "skyMap", "camera", "schedule"];

    public static void Install(FakeCameraAgent api, CliHarness harness)
    {
        var restartRequested = false;
        var readsSinceRestart = 0;
        api.Json("GET", SettingsRoute + "/catalogue", Catalogue())
            .Json("GET", SettingsRoute, Values())
            .Json("GET", SummaryRoute, Summary())
            .Handle("GET", InfoRoute, _ =>
            {
                if (restartRequested && harness.RestartReadsBeforeNewProcess >= 0 && readsSinceRestart++ >= harness.RestartReadsBeforeNewProcess)
                {
                    restartRequested = false;
                    harness.StartedUtc = harness.StartedUtc.AddMinutes(5);
                }

                return FakeCameraAgent.Response(HttpStatusCode.OK, Info(harness.StartedUtc, harness.Supervised, restartRequested));
            })
            .Handle("POST", RestartRoute, _ =>
            {
                restartRequested = true;
                readsSinceRestart = 0;
                return FakeCameraAgent.Response(HttpStatusCode.Accepted, new { status = "Scheduled", delaySeconds = 1.0 });
            })
            .Json("GET", RigRoute, Rigs(active: "rig-1", pending: null))
            .Json("GET", RigRoute + "/preview/rig-2", new
            {
                scheduleRevisionId = "schedule-3",
                scheduleProfileSha256 = new string('a', 64),
                rigRevisionId = "rig-2",
                valid = true,
                failure = (string?)null,
                runtimeVerified = false
            })
            .Json("POST", RigRoute + "/stage", new
            {
                receiptId = "receipt-1",
                revisionId = "rig-2",
                version = 8,
                disposition = "Staged",
                acknowledgedUnvalidated = true,
                scheduleRevisionId = "schedule-3"
            })
            .Json("GET", ScheduleRoute, Schedule())
            .Json("POST", ScheduleRoute + "/stage", new
            {
                activeRevision = ScheduleRevision("schedule-3", 3),
                pendingRevision = ScheduleRevision("schedule-4", 4)
            })
            .Json("POST", ScheduleRoute + "/activate", new { activeRevision = ScheduleRevision("schedule-4", 4), pendingRevision = (object?)null })
            .Json("POST", ScheduleRoute + "/rollback", new { activeRevision = ScheduleRevision("schedule-2", 2), pendingRevision = (object?)null });
        foreach (var group in GroupNames)
        {
            api.Handle("PUT", $"{SettingsRoute}/{group}", request => FakeCameraAgent.Response(HttpStatusCode.OK, new
            {
                status = group == "location" ? "PendingRestart" : "Applied",
                group = GroupState(group, version: $"{group}-v2"),
                recorded = new[] { $"{group} saved" }
            }));
        }
    }

    public static object Catalogue() => new
    {
        schemaVersion = 1,
        groups = new object[]
        {
            Group("site", "Site profile", "settingsFile",
                Setting("site.observatoryName", "site", "Observatory name", "String", nullable: true, maximumLength: 64)),
            Group("location", "Deployment location", "deploymentLocation",
                Setting("location.latitudeDegrees", "location", "Latitude", "Number", minimum: -90, maximum: 90, unit: "degrees",
                    safety: "CaptureGeometry", application: "Restart"),
                Setting("location.longitudeDegrees", "location", "Longitude", "Number", minimum: -180, maximum: 180, unit: "degrees",
                    safety: "CaptureGeometry", application: "Restart")),
            Group("skyMap", "Sky map", "settingsFile",
                Setting("skyMap.maximumObjects", "skyMap", "Scene object limit", "Integer", minimum: 50, maximum: 2000,
                    safety: "CaptureOutput", @default: 400)),
            Group("camera", "Camera", "rigRevision",
                Setting("camera.skyLighting", "camera", "Sky lighting", "Enum", safety: "CaptureOutput", application: "Restart",
                    allowedValues: [Choice("SolarDriven"), Choice("ControlledNight")])),
            Group("schedule", "Capture schedule", "scheduleRevision",
                Setting("schedule.activeRevision", "schedule", "Active schedule revision", "Enum", safety: "CaptureOutput",
                    application: "NextCaptureBoundary"))
        }
    };

    public static object Values(bool siteWritable = true, int maximumObjects = 400, string skyMapVersion = "skyMap-v1") => new
    {
        schemaVersion = 1,
        groups = GroupNames
            .Select(name => GroupState(name, name == "skyMap" ? skyMapVersion : $"{name}-v1", name != "site" || siteWritable, maximumObjects))
            .ToArray()
    };

    public static object GroupState(string group, string version, bool canWrite = true, int maximumObjects = 400) => new
    {
        group,
        version,
        canWrite,
        readOnlyReason = canWrite ? null : $"The {group} group is set by an environment variable.",
        settings = group switch
        {
            "site" => new object[] { State("site.observatoryName", "Hualapai Valley", canWrite) },
            "location" => [State("location.latitudeDegrees", 35.5, canWrite), State("location.longitudeDegrees", -113.9, canWrite)],
            "skyMap" => [State("skyMap.maximumObjects", maximumObjects, canWrite)],
            "camera" => [State("camera.skyLighting", "SolarDriven", canWrite)],
            _ => [State("schedule.activeRevision", "schedule-3", canWrite, allowedValues: [Choice("schedule-3"), Choice("schedule-2")])]
        }
    };

    /// <summary>
    /// The operations summary as CameraAgent serves it (<c>CameraAgentOperationsSummary</c>), in its own words: a configuration
    /// <c>validated</c>, raw ingress <c>Accepting</c>, the lanes, processing and delivery <c>Healthy</c>, the camera and the
    /// heartbeat <c>Available</c>, and central integration on, so every section is read.
    /// </summary>
    public static object Summary(
        string capture = "Running",
        long rawPending = 0,
        string freshness = "fresh",
        string validation = "validated",
        string delivery = "Healthy")
    {
        return new
        {
            capturedUtc = "2026-10-18T19:30:00Z",
            captureControl = Section(new { state = capture, version = 3, isInitialized = true }, freshness),
            rawIngress = Section(Queue("Accepting", rawPending, 1024 * rawPending)),
            captureLanes = Section(new
            {
                availability = "Healthy",
                lanes = new[]
                {
                    new
                    {
                        name = "rawArchive",
                        required = true,
                        pendingCount = 0,
                        pendingBytes = 0,
                        leasedCount = 0,
                        retryCount = 0,
                        quarantineCount = 0,
                        pressureLevel = 0,
                        oldestPendingUtc = (string?)null,
                        pendingCaptures = Array.Empty<object>()
                    }
                },
                pendingCount = 0,
                pendingBytes = 0,
                leasedCount = 0,
                retryCount = 0,
                quarantineCount = 0,
                oldestPendingUtc = (string?)null
            }),
            captureProcessing = Section(Queue("Healthy", 0, 0)),
            artifactOutbox = Section(Queue("Healthy", 2, 4096)),
            storage = Section(new[]
            {
                new { alias = "raw", totalBytes = 500L * 1024 * 1024 * 1024, availableBytes = 120L * 1024 * 1024 * 1024, isUnderPressure = false, effectiveRetentionDays = 30, probeSucceeded = true }
            }),
            captureRuntime = Section(new
            {
                availability = "Available",
                lastSucceededUtc = "2026-10-18T19:29:58Z",
                lastFailedUtc = (string?)null,
                lastRecoveredUtc = (string?)null,
                timings = Array.Empty<object>()
            }),
            heartbeat = Section(new
            {
                availability = "Available",
                lastAcknowledgedUtc = "2026-10-18T19:29:50Z",
                pendingCount = 0,
                pendingBytes = 0,
                leasedCount = 0,
                retryCount = 0,
                quarantineCount = 0,
                overflowCount = 0,
                blockedCount = 0,
                oldestPendingUtc = (string?)null
            }),
            environmentalDelivery = Section(new
            {
                availability = delivery,
                lastAcknowledgedUtc = "2026-10-18T19:29:30Z",
                storedCount = 120,
                storedBytes = 61440,
                pendingCount = 0,
                pendingBytes = 0,
                leasedCount = 0,
                retryCount = 0,
                quarantineCount = 0,
                terminalCount = 0,
                overflowCount = 0,
                oldestPendingUtc = (string?)null
            }),
            executionEvidenceExport = Section(new
            {
                availability = "Healthy",
                reasonCode = "export.drained",
                pendingCount = 0,
                pendingBytes = 0,
                retryCount = 0,
                quarantineCount = 0,
                abandonedCount = 0,
                acknowledgedCount = 42,
                totalAttempts = 42,
                conflictCount = 0,
                rejectCount = 0,
                drainedCount = 42,
                resyncRequestCount = 0,
                sourcePrunedCount = 0,
                projectionRejectedCount = 0,
                storageBytes = 8192,
                highestSequence = 42,
                acknowledgedThroughSequence = 42,
                inFlightRequests = 0,
                storagePressure = false,
                negotiatedSchemaVersion = "1",
                lastAcknowledgementUtc = "2026-10-18T19:29:40Z",
                oldestPendingUtc = (string?)null
            }),
            transientWorker = Section(new { availability = "Healthy", pendingFrames = 0, pendingCandidates = 0, maximumCandidates = 64 }),
            captureTelemetry = Section(new
            {
                sampleCount = 10,
                latestCaptureStartedUtc = "2026-10-18T19:29:58Z",
                latestMode = "Night",
                latestExposureMilliseconds = 15000.0,
                latestGain = 200.0,
                averageIntervalMilliseconds = 15000.0,
                averageExposureMilliseconds = 15000.0,
                averageProcessingMilliseconds = 850.0,
                averageLoopMilliseconds = 15900.0,
                capturesPerMinute = 4.0,
                dutyCycle = 0.94,
                framesStored = 10,
                immediateUploadCount = 0
            }),
            configuration = new
            {
                source = "validated-configuration",
                observedUtc = (string?)null,
                freshness = "static",
                value = new
                {
                    isCurrent = true,
                    validationStatus = validation,
                    agentId = "allsky01",
                    moduleType = "VirtualSky",
                    centralIntegration = "Enabled",
                    transientDetection = "Edge"
                }
            },
            archivedReplay = Section(new
            {
                availability = "Healthy",
                pendingCount = 0,
                pendingBytes = 0,
                leasedCount = 0,
                retryCount = 0,
                quarantineCount = 0,
                terminalCount = 0,
                oldestPendingUtc = (string?)null,
                abandonedCount = 0
            })
        };
    }

    public static object Info(DateTimeOffset started, bool supervised = true, bool requested = false, string access = "ReadWrite") => new
    {
        schemaVersion = 1,
        application = new { name = "CameraAgent", version = "0.9.0-test", revision = new string('b', 40), informationalVersion = "0.9.0-test+" + new string('b', 40) },
        host = new { runtime = ".NET 10.0.13", runtimeIdentifier = "linux-arm64", processStartedUtc = started, uptimeSeconds = 600.0 },
        catalog = new { catalogId = "hyg-v42-production", catalogVersion = "4.2", rowCount = 118218 },
        restart = new { supervised, requested },
        caller = new { credential = "ApiKey", apiKeyId = CliHarness.KeyId, access }
    };

    public static object Rigs(string? active, string? pending, string? pendingFailure = null, string? activeFailure = null) => new
    {
        selection = new { activeRevisionId = active, pendingRevisionId = pending, version = 7, pendingScheduleRevisionId = (string?)null },
        pendingRuntimeFailure = pendingFailure,
        activeRuntimeFailure = activeFailure,
        revisions = new[]
        {
            new { revisionId = "rig-1", profileId = "allsky", revisionNumber = 1, moduleType = "VirtualSky", sensorName = "ASI676MC" },
            new { revisionId = "rig-2", profileId = "allsky", revisionNumber = 2, moduleType = "VirtualSky", sensorName = "ASI676MC" }
        }
    };

    public static object Schedule(string? pending = null) => new
    {
        stateVersion = 11,
        activeRevision = ScheduleRevision("schedule-3", 3),
        pendingRevision = pending is null ? null : ScheduleRevision(pending, 4),
        history = new[] { ScheduleRevision("schedule-2", 2), ScheduleRevision("schedule-1", 1) },
        decision = new { admitted = true, reason = 1, nextTransitionUtc = "2026-10-19T01:00:00Z" },
        preview = new
        {
            intervals = new[]
            {
                new { id = "night-1", startUtc = "2999-10-19T01:00:00Z", endUtc = "2999-10-19T12:00:00Z", setpointProfileId = "night" }
            }
        },
        overrides = Array.Empty<object>()
    };

    public static object ScheduleRevision(string id, int number) => new
    {
        revisionId = id,
        revisionNumber = number,
        profile = new { schemaVersion = "2", name = id },
        source = "Operator",
        createdUtc = "2026-10-18T12:00:00Z"
    };

    private static object Section(object value, string freshness = "fresh")
        => new { source = "test", observedUtc = "2026-10-18T19:30:00Z", freshness, value };

    private static object Queue(string availability, long pending, long bytes) => new
    {
        availability,
        pendingCount = pending,
        pendingBytes = bytes,
        leasedCount = 0,
        retryCount = 0,
        quarantineCount = 0,
        terminalCount = 0,
        oldestPendingUtc = (string?)null
    };

    private static object Group(string name, string title, string store, params object[] settings)
        => new { name, title, description = title + ".", store, settings };

    private static object Setting(
        string key,
        string group,
        string title,
        string type,
        bool nullable = false,
        double? minimum = null,
        double? maximum = null,
        int? maximumLength = null,
        object[]? allowedValues = null,
        string? unit = null,
        object? @default = null,
        string safety = "Descriptive",
        string application = "Immediate") => new
        {
            key,
            group,
            title,
            description = title + " of the site.",
            type,
            nullable,
            minimum,
            maximum,
            maximumLength,
            allowedValues,
            unit,
            @default,
            readRole = "OperationsRead",
            writeRole = "OperationsMutate",
            safety,
            application,
            appliesAfterRestart = application == "Restart",
            secret = false,
            configurationKey = (string?)null
        };

    private static object Choice(string value) => new { value, label = value, description = value + "." };

    private static object State(string key, object value, bool canWrite, object[]? allowedValues = null) => new
    {
        key,
        value,
        isSet = true,
        source = "SettingsFile",
        sourceDetail = (string?)null,
        canWrite,
        readOnlyReason = canWrite ? null : "Set by an environment variable.",
        pendingValue = (object?)null,
        problem = (string?)null,
        allowedValues
    };
}

/// <summary>A JSON answer whose body never arrives: a read of it waits until it is cancelled.</summary>
internal sealed class StalledContent : HttpContent
{
    public StalledContent() => Headers.ContentType = new MediaTypeHeaderValue("application/json");

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => Task.Delay(Timeout.InfiniteTimeSpan);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

    protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new StalledStream());

    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => CreateContentReadStreamAsync();

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>A clock whose timers fire only when the test says, for the termination grace period.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];

    public IReadOnlyList<ManualTimer> Timers => _timers;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(callback, state, dueTime);
        _timers.Add(timer);
        return timer;
    }

    internal sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        public TimeSpan DueTime { get; private set; } = dueTime;

        public bool Disposed { get; private set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            DueTime = dueTime;
            return true;
        }

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

internal static class CliAssert
{
    /// <summary>Asserts that no output and no request URL holds the key; the key travels only as a bearer header.</summary>
    public static void KeyNeverLeaks(CliResult result, FakeCameraAgent api, string key)
    {
        Assert.DoesNotContain(key, result.All, StringComparison.Ordinal, result.ToString());
        Assert.DoesNotContain(key[(SkyApiKey.Prefix.Length + SkyApiKey.IdLength + 1)..], result.All, StringComparison.Ordinal);
        foreach (var request in api.Requests)
        {
            Assert.DoesNotContain(key, request.PathAndQuery, StringComparison.Ordinal);
            Assert.IsTrue(request.Authorization is null || request.Authorization == new AuthenticationHeaderValue("Bearer", key).ToString(),
                request.Authorization);
            Assert.IsTrue(request.Body is null || !request.Body.Contains(key, StringComparison.Ordinal));
        }
    }
}
