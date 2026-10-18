using System.Net;
using System.Runtime.Versioning;

namespace HVO.SkyMonitor.CameraAgent.Tests.Cli;

/// <summary><c>restart</c>, <c>rig show|stage|apply</c> and <c>schedule show|stage|apply</c> against the fake API.</summary>
[TestClass]
[TestCategory("Unit")]
[UnsupportedOSPlatform("windows")]
public sealed class SkyCliRuntimeTests
{
    [TestMethod]
    public async Task Restart_RequestsItAndWaitsForANewProcessAsync()
    {
        using var harness = new CliHarness();
        var before = harness.StartedUtc;

        var result = await harness.RunAsync("restart", "--force").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        Assert.HasCount(1, harness.Api.RequestsTo("POST", Fake.RestartRoute));
        Assert.AreNotEqual(before, harness.StartedUtc);
        StringAssert.Contains(result.Out, "CameraAgent is back: started 2026-10-18T18:05:00Z", StringComparison.Ordinal);
        StringAssert.Contains(result.Error, "Restart requested; waiting up to 30 s", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(3, harness.Api.RequestsTo("GET", Fake.InfoRoute).Count, "It read the old process, then the new one.");
    }

    [TestMethod]
    public async Task Restart_Json_SaysWhenTheNewProcessStartedAsync()
    {
        using var harness = new CliHarness();

        var result = await harness.RunAsync("restart", "--force", "--json").ConfigureAwait(false);

        var json = result.Json();
        Assert.IsTrue(json.GetProperty("restarted").GetBoolean());
        Assert.AreEqual("Scheduled", json.GetProperty("status").GetString());
        Assert.AreEqual(new DateTimeOffset(2026, 10, 18, 18, 5, 0, TimeSpan.Zero), json.GetProperty("startedUtc").GetDateTimeOffset());
        Assert.AreEqual(string.Empty, result.Error);
    }

    [TestMethod]
    public async Task Restart_NoWait_ReturnsOnceCameraAgentAcceptedItAsync()
    {
        using var harness = new CliHarness();

        var result = await harness.RunAsync("restart", "--force", "--no-wait").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Out, "CameraAgent is restarting.", StringComparison.Ordinal);
        Assert.HasCount(1, harness.Api.RequestsTo("GET", Fake.InfoRoute));
    }

    [TestMethod]
    public async Task Restart_WithoutATerminalOrForce_AsksNoOneAndSendsNothingAsync()
    {
        using var harness = new CliHarness();

        var text = await harness.RunAsync("restart").ConfigureAwait(false);
        var json = await harness.RunAsync("restart", "--json").ConfigureAwait(false);

        Assert.AreEqual(2, text.ExitCode, text.ToString());
        StringAssert.Contains(text.Error, "confirm it with --force", StringComparison.Ordinal);
        Assert.AreEqual(2, json.ExitCode, json.ToString());
        Assert.AreEqual("ConfirmationRequired", json.Json().GetProperty("error").GetProperty("kind").GetString());
        Assert.IsEmpty(harness.Api.Requests);
    }

    [TestMethod]
    public async Task Restart_AtATerminal_AsksAndANoChangesNothingAsync()
    {
        using var harness = new CliHarness { Interactive = true };
        harness.Input.Enqueue("n");

        var declined = await harness.RunAsync("restart").ConfigureAwait(false);
        harness.Input.Enqueue("y");
        var confirmed = await harness.RunAsync("restart").ConfigureAwait(false);

        Assert.AreEqual(3, declined.ExitCode, declined.ToString());
        StringAssert.Contains(harness.Prompts[0].Prompt, "Restart CameraAgent at http://127.0.0.1:5130/? Capture stops until it is back. [y/N]", StringComparison.Ordinal);
        Assert.AreEqual(0, confirmed.ExitCode, confirmed.ToString());
        Assert.HasCount(1, harness.Api.RequestsTo("POST", Fake.RestartRoute));
    }

    [TestMethod]
    public async Task Restart_AnUnsupervisedCameraAgent_IsRefusedBeforeAnythingIsSentAsync()
    {
        using var harness = new CliHarness { Supervised = false };

        var result = await harness.RunAsync("restart", "--force").ConfigureAwait(false);

        Assert.AreEqual(3, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "is not supervised here", StringComparison.Ordinal);
        Assert.IsEmpty(harness.Api.RequestsTo("POST", Fake.RestartRoute));
    }

    [TestMethod]
    public async Task Restart_CameraAgentsOwnRefusal_ExitsThreeAsync()
    {
        using var harness = new CliHarness();
        harness.Api.Problem("POST", Fake.RestartRoute, HttpStatusCode.Conflict,
            "CameraAgent is not supervised here, so it cannot restart itself: nothing would start it again.", "restart.unsupervised");

        var result = await harness.RunAsync("restart", "--force", "--json").ConfigureAwait(false);

        Assert.AreEqual(3, result.ExitCode, result.ToString());
        Assert.AreEqual("restart.unsupervised", result.Json().GetProperty("error").GetProperty("code").GetString());
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.Forbidden)]
    public async Task Restart_AVerificationReadRefusedAfterCameraAgentAcceptedIt_ExitsOneAndSaysItWasAcceptedAsync(HttpStatusCode status)
    {
        using var harness = new CliHarness();
        harness.Api.OnRequest = request =>
        {
            if (request.PathAndQuery == Fake.InfoRoute && harness.Api.RequestsTo("POST", Fake.RestartRoute).Count > 0)
            {
                harness.Api.RefuseAll = status;
            }
        };

        var result = await harness.RunAsync("restart", "--force").ConfigureAwait(false);

        Assert.AreEqual(1, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "CameraAgent accepted the restart, but it could not be verified", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Restart_AVerificationReadRefusedAfterItWasAccepted_IsUnverifiedInJsonAsync()
    {
        using var harness = new CliHarness();
        harness.Api.OnRequest = request =>
        {
            if (request.PathAndQuery == Fake.InfoRoute && harness.Api.RequestsTo("POST", Fake.RestartRoute).Count > 0)
            {
                harness.Api.RefuseAll = HttpStatusCode.Unauthorized;
            }
        };

        var result = await harness.RunAsync("restart", "--force", "--json").ConfigureAwait(false);

        Assert.AreEqual(1, result.ExitCode, result.ToString());
        var error = result.Json().GetProperty("error");
        Assert.AreEqual(1, error.GetProperty("exitCode").GetInt32());
        Assert.AreEqual("Unverified", error.GetProperty("kind").GetString());
        Assert.AreEqual("SignedOut", error.GetProperty("cause").GetString());
    }

    [TestMethod]
    public async Task Restart_InterruptedAfterCameraAgentAcceptedIt_Exits130AndSaysItWasAcceptedAsync()
    {
        using var harness = new CliHarness();
        using var stop = new CancellationTokenSource();
        harness.Api.OnRequest = request =>
        {
            if (request.PathAndQuery == Fake.InfoRoute && harness.Api.RequestsTo("POST", Fake.RestartRoute).Count > 0)
            {
                harness.Api.Hang = true;
                stop.Cancel();
            }
        };

        var result = await harness.RunAsync(stop.Token, "restart", "--force").ConfigureAwait(false);

        Assert.AreEqual(130, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "after CameraAgent accepted the restart", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Restart_ARequestCameraAgentMayHaveReceived_SaysItIsNotKnownAsync()
    {
        using var harness = new CliHarness();
        harness.Api.OnRequest = request =>
        {
            if (request.PathAndQuery == Fake.RestartRoute)
            {
                harness.Api.Unreachable = true;
            }
        };

        var result = await harness.RunAsync("restart", "--force").ConfigureAwait(false);

        Assert.AreEqual(1, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "Whether CameraAgent received the restart is not known", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Restart_WhileTheOldProcessStopsAndTheNewOneStarts_KeepsWaitingThroughFailedReadsAsync()
    {
        using var harness = new CliHarness();
        var reads = 0;
        harness.Api.OnRequest = request =>
        {
            if (request.PathAndQuery == Fake.InfoRoute && harness.Api.RequestsTo("POST", Fake.RestartRoute).Count > 0)
            {
                harness.Api.Unreachable = ++reads <= 3;
            }
        };

        var result = await harness.RunAsync("restart", "--force").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        Assert.IsGreaterThan(3, reads);
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("3601")]
    public async Task Restart_TimeoutOutOfRange_IsAUsageErrorAsync(string seconds)
    {
        using var harness = new CliHarness();

        var result = await harness.RunAsync("restart", "--force", "--timeout", seconds).ConfigureAwait(false);

        Assert.AreEqual(2, result.ExitCode, result.ToString());
        Assert.IsEmpty(harness.Api.Requests);
    }

    [TestMethod]
    public async Task RigShow_ListsTheActiveAndStagedRevisionsAsync()
    {
        using var harness = new CliHarness();
        harness.Api.Json("GET", Fake.RigRoute, Fake.Rigs(active: "rig-1", pending: "rig-2", pendingFailure: null));

        var result = await harness.RunAsync("rig", "show").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Out, "Active: rig-1 (profile allsky, revision 1, VirtualSky, ASI676MC)", StringComparison.Ordinal);
        StringAssert.Contains(result.Out, "Staged: rig-2 (profile allsky, revision 2, VirtualSky, ASI676MC); it applies when CameraAgent next starts ('hvo-sky rig apply')", StringComparison.Ordinal);
        StringAssert.Contains(result.Out, "REVISION", StringComparison.Ordinal);
        StringAssert.Contains(result.Out, "staged", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RigStage_PreviewsThenStagesWithTheVersionAndScheduleThePreviewNamedAsync()
    {
        using var harness = new CliHarness();

        var result = await harness.RunAsync("rig", "stage", "rig-2", "--acknowledge-unvalidated").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        Assert.HasCount(1, harness.Api.RequestsTo("GET", Fake.RigRoute + "/preview/rig-2"));
        var stage = harness.Api.RequestsTo("POST", Fake.RigRoute + "/stage").Single();
        var body = stage.BodyJson();
        Assert.AreEqual("rig-2", body.GetProperty("revisionId").GetString());
        Assert.AreEqual(7, body.GetProperty("expectedVersion").GetInt64());
        Assert.IsTrue(body.GetProperty("acknowledgeUnvalidated").GetBoolean());
        Assert.AreEqual("schedule-3", body.GetProperty("expectedScheduleRevisionId").GetString());
        Assert.AreEqual(new string('a', 64), body.GetProperty("expectedScheduleProfileSha256").GetString());
        Assert.IsFalse(string.IsNullOrEmpty(stage.IdempotencyKey));
        StringAssert.Contains(result.Out, "Staged rig revision rig-2.", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RigStage_WithoutTheAcknowledgement_AsksAtATerminalAndNeedsTheOptionWithoutOneAsync()
    {
        using var harness = new CliHarness();

        var scripted = await harness.RunAsync("rig", "stage", "rig-2").ConfigureAwait(false);
        harness.Interactive = true;
        harness.Input.Enqueue("y");
        var confirmed = await harness.RunAsync("rig", "stage", "rig-2").ConfigureAwait(false);

        Assert.AreEqual(2, scripted.ExitCode, scripted.ToString());
        StringAssert.Contains(scripted.Error, "--acknowledge-unvalidated", StringComparison.Ordinal);
        Assert.AreEqual(0, confirmed.ExitCode, confirmed.ToString());
        StringAssert.Contains(harness.Prompts.Single().Prompt, "its camera may fail to initialize", StringComparison.Ordinal);
        Assert.HasCount(1, harness.Api.RequestsTo("POST", Fake.RigRoute + "/stage"));
    }

    [TestMethod]
    [DataRow("pending-named-rig", "another rig revision is already staged")]
    [DataRow("pending-schedule-draft", "a schedule revision is staged; apply it first")]
    [DataRow("rig.readout", "CameraAgent's preview refused it (rig.readout).")]
    public async Task RigStage_APreviewThatFails_IsRefusedAndNothingIsStagedAsync(string failure, string expected)
    {
        using var harness = new CliHarness();
        harness.Api.Json("GET", Fake.RigRoute + "/preview/rig-2", new
        {
            scheduleRevisionId = "schedule-3",
            scheduleProfileSha256 = new string('a', 64),
            rigRevisionId = "rig-2",
            valid = false,
            failure,
            runtimeVerified = false
        });

        var result = await harness.RunAsync("rig", "stage", "rig-2", "--acknowledge-unvalidated").ConfigureAwait(false);

        Assert.AreEqual(3, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, expected, StringComparison.Ordinal);
        Assert.IsEmpty(harness.Api.RequestsTo("POST", Fake.RigRoute + "/stage"));
    }

    [TestMethod]
    public async Task RigApply_RestartsAndConfirmsTheStagedRevisionBecameActiveAsync()
    {
        using var harness = new CliHarness();
        using var stop = new CancellationTokenSource();
        var args = SkyCliExitCodeTests.Prepare("rig apply", harness, stop);

        var result = await harness.RunAsync(stop.Token, args).ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Out, "Rig revision rig-2 is active.", StringComparison.Ordinal);
        Assert.HasCount(1, harness.Api.RequestsTo("POST", Fake.RestartRoute));
    }

    [TestMethod]
    public async Task RigApply_WhenTheStagedRevisionDidNotBecomeActive_ExitsOneWithTheFailureAsync()
    {
        using var harness = new CliHarness();
        harness.Api.Handle("GET", Fake.RigRoute, _ => FakeCameraAgent.Response(HttpStatusCode.OK,
            harness.Api.RequestsTo("POST", Fake.RestartRoute).Count > 0
                ? Fake.Rigs(active: "rig-1", pending: "rig-2", pendingFailure: "The camera did not initialize.")
                : Fake.Rigs(active: "rig-1", pending: "rig-2")));

        var result = await harness.RunAsync("rig", "apply", "--force").ConfigureAwait(false);

        Assert.AreEqual(1, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "rig revision rig-2 did not become active: The camera did not initialize.", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RigApply_WaitsUntilTheStagedRevisionBecomesActiveAsync()
    {
        using var harness = new CliHarness();
        var readsAfterRestart = 0;
        harness.Api.Handle("GET", Fake.RigRoute, _ => FakeCameraAgent.Response(HttpStatusCode.OK,
            harness.Api.RequestsTo("POST", Fake.RestartRoute).Count == 0
                ? Fake.Rigs(active: "rig-1", pending: "rig-2")
                : ++readsAfterRestart <= 2
                    ? Fake.Rigs(active: "rig-1", pending: "rig-2")
                    : Fake.Rigs(active: "rig-2", pending: null)));

        var result = await harness.RunAsync("rig", "apply", "--force").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Out, "Rig revision rig-2 is active.", StringComparison.Ordinal);
        Assert.AreEqual(3, readsAfterRestart, "It read the rig until the staged revision was active.");
    }

    [TestMethod]
    [DataRow("pending")]
    [DataRow("active")]
    public async Task RigApply_AnInitializationFailureThatAppearsLater_EndsTheWaitWithExitOneAsync(string which)
    {
        using var harness = new CliHarness();
        var readsAfterRestart = 0;
        harness.Api.Handle("GET", Fake.RigRoute, _ => FakeCameraAgent.Response(HttpStatusCode.OK,
            harness.Api.RequestsTo("POST", Fake.RestartRoute).Count == 0 || ++readsAfterRestart == 1
                ? Fake.Rigs(active: "rig-1", pending: "rig-2")
                : which == "pending"
                    ? Fake.Rigs(active: "rig-1", pending: "rig-2", pendingFailure: "The camera did not initialize.")
                    : Fake.Rigs(active: "rig-1", pending: "rig-2", activeFailure: "The camera did not initialize.")));

        var result = await harness.RunAsync("rig", "apply", "--force").ConfigureAwait(false);

        Assert.AreEqual(1, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "rig revision rig-2 did not become active: The camera did not initialize.", StringComparison.Ordinal);
        Assert.AreEqual(2, readsAfterRestart);
    }

    [TestMethod]
    public async Task RigApply_ARevisionThatNeverBecomesActive_TimesOutAtTheDeadlineAsync()
    {
        using var harness = new CliHarness();
        harness.Api.Json("GET", Fake.RigRoute, Fake.Rigs(active: "rig-1", pending: "rig-2"));

        var result = await harness.RunAsync("rig", "apply", "--force", "--timeout", "1").ConfigureAwait(false);

        Assert.AreEqual(1, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "CameraAgent accepted the restart that applies rig revision rig-2, but it could not be verified", StringComparison.Ordinal);
        StringAssert.Contains(result.Error, "Timed out after 1 s (--timeout) waiting for rig revision rig-2 to become active", StringComparison.Ordinal);
        Assert.IsGreaterThan(1, harness.Api.RequestsTo("GET", Fake.RigRoute).Count - 1, "It read the rig more than once after the restart.");
    }

    [TestMethod]
    public async Task RigApply_ARevisionNoLongerStaged_EndsTheWaitWithExitOneAsync()
    {
        using var harness = new CliHarness();
        harness.Api.Handle("GET", Fake.RigRoute, _ => FakeCameraAgent.Response(HttpStatusCode.OK,
            harness.Api.RequestsTo("POST", Fake.RestartRoute).Count == 0
                ? Fake.Rigs(active: "rig-1", pending: "rig-2")
                : Fake.Rigs(active: "rig-1", pending: null)));

        var result = await harness.RunAsync("rig", "apply", "--force").ConfigureAwait(false);

        Assert.AreEqual(1, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "rig revision rig-2 is no longer staged and did not become active", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.Forbidden)]
    public async Task RigApply_ARigReadRefusedAfterTheRestartWasAccepted_ExitsOneAsync(HttpStatusCode status)
    {
        using var harness = new CliHarness();
        harness.Api.Handle("GET", Fake.RigRoute, _ => harness.Api.RequestsTo("POST", Fake.RestartRoute).Count == 0
            ? FakeCameraAgent.Response(HttpStatusCode.OK, Fake.Rigs(active: "rig-1", pending: "rig-2"))
            : FakeCameraAgent.ProblemResponse(status, "Refused"));

        var result = await harness.RunAsync("rig", "apply", "--force").ConfigureAwait(false);

        Assert.AreEqual(1, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "CameraAgent accepted the restart that applies rig revision rig-2, but it could not be verified", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RigApply_NothingStaged_SaysSoAndRestartsNothingAsync()
    {
        using var harness = new CliHarness();

        var result = await harness.RunAsync("rig", "apply", "--force").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Out, "No rig revision is staged; nothing to apply.", StringComparison.Ordinal);
        Assert.IsEmpty(harness.Api.RequestsTo("POST", Fake.RestartRoute));
    }

    [TestMethod]
    public async Task ScheduleShow_ListsRevisionsAdmissionAndTheNextWindowsAsync()
    {
        using var harness = new CliHarness();
        harness.Api.Json("GET", Fake.ScheduleRoute, Fake.Schedule(pending: "schedule-4"));

        var result = await harness.RunAsync("schedule", "show").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Out, "Active:      schedule-3 (#3, from Operator, 2026-10-18T12:00:00Z)", StringComparison.Ordinal);
        StringAssert.Contains(result.Out, "Staged:      schedule-4 (#4", StringComparison.Ordinal);
        StringAssert.Contains(result.Out, "admitted; next change 2026-10-19T01:00:00Z", StringComparison.Ordinal);
        StringAssert.Contains(result.Out, "2999-10-19T01:00:00Z", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ScheduleStage_SendsTheProfileOnTheActiveRevisionWithTheVersionReadAsync()
    {
        using var harness = new CliHarness();
        var file = harness.WriteFile("profile.json", """{"schemaVersion": "2", "name": "winter"}""");

        var result = await harness.RunAsync("schedule", "stage", file, "--reason", "longer nights").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        var body = harness.Api.RequestsTo("POST", Fake.ScheduleRoute + "/stage").Single().BodyJson();
        Assert.AreEqual("winter", body.GetProperty("profile").GetProperty("name").GetString());
        Assert.AreEqual("schedule-3", body.GetProperty("basisRevisionId").GetString());
        Assert.AreEqual(11, body.GetProperty("expectedVersion").GetInt64());
        Assert.AreEqual("longer nights", body.GetProperty("reason").GetString());
        StringAssert.Contains(result.Out, "Staged schedule revision schedule-4, based on schedule-3.", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ScheduleStage_FromStandardInputWithABasisAsync()
    {
        using var harness = new CliHarness { StandardInput = """{"schemaVersion": "2", "name": "summer"}""" };

        var result = await harness.RunAsync("schedule", "stage", "-", "--basis", "schedule-2").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        Assert.AreEqual("schedule-2", harness.Api.RequestsTo("POST", Fake.ScheduleRoute + "/stage").Single().BodyJson()
            .GetProperty("basisRevisionId").GetString());
    }

    [TestMethod]
    [DataRow("not json", "not valid JSON")]
    [DataRow("[]", "must be a JSON object")]
    public async Task ScheduleStage_AProfileThatIsNotAnObject_IsAUsageErrorAsync(string content, string expected)
    {
        using var harness = new CliHarness();

        var result = await harness.RunAsync("schedule", "stage", harness.WriteFile("p.json", content)).ConfigureAwait(false);

        Assert.AreEqual(2, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, expected, StringComparison.Ordinal);
        Assert.IsEmpty(harness.Api.Requests);
    }

    [TestMethod]
    public async Task ScheduleApply_ActivatesTheStagedRevisionAsync()
    {
        using var harness = new CliHarness();
        harness.Api.Json("GET", Fake.ScheduleRoute, Fake.Schedule(pending: "schedule-4"));

        var result = await harness.RunAsync("schedule", "apply", "--reason", "winter").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        var body = harness.Api.RequestsTo("POST", Fake.ScheduleRoute + "/activate").Single().BodyJson();
        Assert.AreEqual("schedule-4", body.GetProperty("revisionId").GetString());
        Assert.AreEqual(11, body.GetProperty("expectedVersion").GetInt64());
        StringAssert.Contains(result.Out, "Schedule revision schedule-4 is active from the next capture boundary.", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ScheduleApply_AnOlderRevision_GoesBackByRollbackAsync()
    {
        using var harness = new CliHarness();

        var result = await harness.RunAsync("schedule", "apply", "schedule-2").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        Assert.HasCount(1, harness.Api.RequestsTo("POST", Fake.ScheduleRoute + "/rollback"));
        Assert.IsEmpty(harness.Api.RequestsTo("POST", Fake.ScheduleRoute + "/activate"));
    }

    [TestMethod]
    public async Task ScheduleApply_ARevisionCameraAgentDoesNotList_IsRefusedRatherThanGuessedAsync()
    {
        using var harness = new CliHarness();

        var result = await harness.RunAsync("schedule", "apply", "schedule-0").ConfigureAwait(false);

        Assert.AreEqual(3, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "whether schedule revision schedule-0 is older or newer than the active revision cannot be established", StringComparison.Ordinal);
        StringAssert.Contains(result.Error, "Nothing was sent.", StringComparison.Ordinal);
        Assert.IsFalse(harness.Api.Requests.Any(static request => request.Method == "POST"));
    }

    [TestMethod]
    [DataRow("target")]
    [DataRow("active")]
    public async Task ScheduleApply_ARevisionNumberCameraAgentDidNotGive_IsRefusedRatherThanGuessedAsync(string missing)
    {
        using var harness = new CliHarness();
        harness.Api.Json("GET", Fake.ScheduleRoute, new
        {
            stateVersion = 11,
            activeRevision = missing == "active" ? (object)new { revisionId = "schedule-3" } : Fake.ScheduleRevision("schedule-3", 3),
            pendingRevision = (object?)null,
            history = new[] { missing == "target" ? (object)new { revisionId = "schedule-2" } : Fake.ScheduleRevision("schedule-2", 2) }
        });

        var result = await harness.RunAsync("schedule", "apply", "schedule-2", "--json").ConfigureAwait(false);

        Assert.AreEqual(3, result.ExitCode, result.ToString());
        Assert.AreEqual("Refused", result.Json().GetProperty("error").GetProperty("kind").GetString());
        Assert.IsFalse(harness.Api.Requests.Any(static request => request.Method == "POST"));
    }

    [TestMethod]
    public async Task ScheduleApply_ANewerListedRevision_IsActivatedAsync()
    {
        using var harness = new CliHarness();
        harness.Api.Json("GET", Fake.ScheduleRoute, Fake.Schedule(pending: "schedule-4"));

        var result = await harness.RunAsync("schedule", "apply", "schedule-4").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        Assert.HasCount(1, harness.Api.RequestsTo("POST", Fake.ScheduleRoute + "/activate"));
        Assert.IsEmpty(harness.Api.RequestsTo("POST", Fake.ScheduleRoute + "/rollback"));
    }

    [TestMethod]
    public async Task ScheduleApply_AnActivationCameraAgentMayHaveReceived_ExitsOneAndSaysItIsNotKnownAsync()
    {
        using var harness = new CliHarness();
        harness.Api.Handle("POST", Fake.ScheduleRoute + "/rollback", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StalledContent() });

        var result = await harness.RunAsync("schedule", "apply", "schedule-2", "--timeout", "1").ConfigureAwait(false);

        Assert.AreEqual(1, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "Whether CameraAgent received the rollback to schedule revision schedule-2 is not known", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(new string[0], "No schedule revision is staged; nothing to apply.")]
    [DataRow(new[] { "schedule-3" }, "Schedule revision schedule-3 is already active; nothing to apply.")]
    public async Task ScheduleApply_NothingToApply_SaysSoAndSendsNothingAsync(string[] revision, string expected)
    {
        using var harness = new CliHarness();

        var result = await harness.RunAsync(["schedule", "apply", .. revision]).ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Out, expected, StringComparison.Ordinal);
        Assert.IsFalse(harness.Api.Requests.Any(static request => request.Method == "POST"));
    }

    [TestMethod]
    public async Task ScheduleApply_ARefusedActivation_ExitsThreeAsync()
    {
        using var harness = new CliHarness();
        harness.Api.Problem("POST", Fake.ScheduleRoute + "/rollback", HttpStatusCode.Conflict, "The schedule command conflicts with durable state.");

        var result = await harness.RunAsync("schedule", "apply", "schedule-2").ConfigureAwait(false);

        Assert.AreEqual(3, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "conflicts with durable state", StringComparison.Ordinal);
    }
}
