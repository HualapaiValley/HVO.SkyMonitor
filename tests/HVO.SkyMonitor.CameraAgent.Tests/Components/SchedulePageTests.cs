using Bunit;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class SchedulePageTests
{
    private static readonly string[] RawDependency = ["$raw"];

    [TestMethod]
    public void Render_ShowsDurableStateEditorPreviewAndRollbackHistory()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(new ScheduleUiService(State()));

        var cut = context.Render<SchedulePage>();

        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(cut.Markup.Contains("Schedule control", StringComparison.Ordinal));
            Assert.IsTrue(cut.Markup.Contains("Revision 2", StringComparison.Ordinal));
            Assert.IsTrue(cut.Markup.Contains("Canonical JSON", StringComparison.Ordinal));
            Assert.IsTrue(cut.Markup.Contains("Create override", StringComparison.Ordinal));
            Assert.IsTrue(cut.Markup.Contains("Rollback targets", StringComparison.Ordinal));
            Assert.IsTrue(cut.Markup.Contains("Desired and effective graph", StringComparison.Ordinal));
            Assert.IsTrue(cut.Markup.Contains("Preview / required", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public void PipelineToggle_ChangesOnlyV2EnabledStateAndRejectsLegacyProfile()
    {
        var options = JsonSerializer.SerializeToElement(new { outputVariant = "display" });
        var profile = Profile() with
        {
            SchemaVersion = LocalCaptureProfileDefinition.CurrentSchemaVersion,
            DependencyPolicy = CapturePipelineDependencyPolicy.RejectEnabledDependent,
            ProcessingSteps =
            [
                new CaptureProcessingStepConfig(
                    "Preview", "preview", 10, options, RawDependency, Enabled: true)
            ]
        };

        var toggled = CameraAgentPipelineOperatorProjection.Toggle(profile, "preview", enabled: false);

        Assert.IsFalse(toggled.ProcessingSteps.Single().Enabled);
        Assert.AreEqual(options.GetRawText(), toggled.ProcessingSteps.Single().Options!.Value.GetRawText());
        CollectionAssert.AreEqual(RawDependency, toggled.ProcessingSteps.Single().DependsOn!.ToArray());
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            CameraAgentPipelineOperatorProjection.Toggle(Profile(), "preview", enabled: false));
        Assert.AreEqual(
            "Graph toggles require a cameraagent-local-profile-v2 revision.",
            CameraAgentPipelineOperatorProjection.SanitizeValidationFailure(exception));
    }

    [TestMethod]
    public void Render_WhenAuthorizationIsRevoked_NavigatesToAccessDenied()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(new ScheduleUiService(null));

        _ = context.Render<SchedulePage>();

        Assert.IsTrue(context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SerializeProfile_RemovesOpaqueModuleAndProcessingOptions()
    {
        var profile = Profile() with
        {
            Module = new CameraModuleDescriptor(
                "test",
                JsonSerializer.SerializeToElement(new { secret = "module-secret" })),
            ProcessingSteps =
            [
                new CaptureProcessingStepConfig(
                    "test-step",
                    Options: JsonSerializer.SerializeToElement(new { storageRoot = "/private/root" }))
            ]
        };

        var serialized = CameraAgentScheduleUiService.SerializeProfile(profile);

        Assert.IsFalse(serialized.Contains("module-secret", StringComparison.Ordinal));
        Assert.IsFalse(serialized.Contains("/private/root", StringComparison.Ordinal));

        var basis = profile with
        {
            SchemaVersion = LocalCaptureProfileDefinition.CurrentSchemaVersion,
            DependencyPolicy = CapturePipelineDependencyPolicy.RejectEnabledDependent,
            ProcessingSteps =
            [
                new CaptureProcessingStepConfig(
                    "First", Options: JsonSerializer.SerializeToElement(new { marker = "first" }),
                    DependsOn: RawDependency),
                new CaptureProcessingStepConfig(
                    "Second", Options: JsonSerializer.SerializeToElement(new { marker = "second" }),
                    DependsOn: RawDependency)
            ]
        };
        var reordered = CameraAgentScheduleOperatorProjection.Sanitize(basis) with
        {
            ProcessingSteps = CameraAgentScheduleOperatorProjection.Sanitize(basis).ProcessingSteps.Reverse().ToArray()
        };

        var restored = CameraAgentScheduleOperatorProjection.RestoreOpaqueOptions(reordered, basis);

        Assert.AreEqual(
            "second",
            restored.ProcessingSteps[0].Options!.Value.GetProperty("marker").GetString());
        Assert.AreEqual(
            "first",
            restored.ProcessingSteps[1].Options!.Value.GetProperty("marker").GetString());

        var spacedIdBasis = basis with
        {
            ProcessingSteps = [basis.ProcessingSteps[0] with { Id = " first " }]
        };
        var normalizedIdCandidate = CameraAgentScheduleOperatorProjection.Sanitize(spacedIdBasis) with
        {
            ProcessingSteps = [spacedIdBasis.ProcessingSteps[0] with { Id = "first", Options = null }]
        };
        var normalizedIdRestored = CameraAgentScheduleOperatorProjection.RestoreOpaqueOptions(
            normalizedIdCandidate,
            spacedIdBasis);
        Assert.AreEqual(
            "first",
            normalizedIdRestored.ProcessingSteps[0].Options!.Value.GetProperty("marker").GetString());
        Assert.IsFalse(CameraAgentPipelineOperatorProjection.Toggle(spacedIdBasis, "first", enabled: false)
            .ProcessingSteps[0].Enabled);
    }

    [TestMethod]
    public void StageRetry_AfterUnavailable_ReusesIdempotencyKeyAndPayload()
    {
        using var context = new BunitContext();
        var service = new RetryingScheduleUiService(State());
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(service);
        var cut = context.Render<SchedulePage>();
        var save = cut.FindAll("button").Single(button =>
            button.TextContent.Contains("Save immutable draft", StringComparison.Ordinal));

        save.Click();
        save.Click();

        Assert.HasCount(2, service.StageCommands);
        Assert.AreEqual(service.StageCommands[0].Key, service.StageCommands[1].Key);
        Assert.AreEqual(service.StageCommands[0].Payload, service.StageCommands[1].Payload);
        Assert.AreEqual(service.StageCommands[0].ExpectedVersion, service.StageCommands[1].ExpectedVersion);
    }

    private static CaptureScheduleOperatorState State()
    {
        var profile = Profile();
        var revision = new CaptureScheduleRevisionSnapshot(
            "profile-00000002-ABCDEF123456",
            2,
            profile,
            new string('A', 64),
            CaptureScheduleContract.ComputeSha256(profile.Schedule),
            "operator-draft",
            "owner",
            null,
            DateTimeOffset.UnixEpoch);
        var prior = revision with { RevisionId = "profile-00000001-123456ABCDEF", RevisionNumber = 1 };
        var interval = new ExpandedScheduleInterval(
            "legacy-always-open",
            CaptureScheduleIntervalSource.LegacyCompatibility,
            ExpandedScheduleDisposition.Open,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddDays(1),
            new DateOnly(1970, 1, 1),
            "night");
        var preview = new CaptureSchedulePreview(
            revision.ScheduleSha256,
            new string('B', 64),
            "capture-schedule-expand-v1",
            new string('C', 64),
            "none",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddDays(1),
            [interval],
            []);
        var decision = new CaptureScheduleDecision(
            true,
            CaptureScheduleAdmissionReason.LegacyCompatibility,
            CaptureScheduleSafetyState.Available,
            DateTimeOffset.UnixEpoch,
            "night",
            interval,
            null,
            false,
            DateTimeOffset.UnixEpoch.AddDays(1));
        return new CaptureScheduleOperatorState(4, revision, null, [revision, prior], decision, preview, []);
    }

    private static LocalCaptureProfileDefinition Profile()
        => new(
            LocalCaptureProfileDefinition.LegacySchemaVersion,
            new CameraModuleDescriptor("test"),
            new CameraRigConfig(
                new SensorProfile("test", 2, 2, 5, SensorColorMode.Mono, CameraPixelFormat.Mono16),
                new OpticsProfile("Perspective", 50, 10, 0),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
            [],
            new CaptureScheduleDefinition(
                "capture-schedule-v1",
                [new CaptureScheduleSetpointProfile(
                    "night", TimeSpan.FromSeconds(1), 1, TimeSpan.FromSeconds(2))],
                [],
                LegacyAlwaysOpen: true,
                LegacySetpointProfileId: "night"));

    private sealed class ScheduleUiService(CaptureScheduleOperatorState? state) : ICameraAgentScheduleUiService
    {
        public ValueTask<OperatorUiResult<CaptureScheduleOperatorState>> GetAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(state is null
                ? OperatorUiResult<CaptureScheduleOperatorState>.Failure(
                    OperatorUiResultKind.Unauthorized, "Authorization is required.")
                : OperatorUiResult<CaptureScheduleOperatorState>.Success(state));

        public ValueTask<OperatorUiResult<CameraAgentPipelineOperatorState>> GetPipelineAsync(
            CancellationToken cancellationToken)
        {
            if (state is null)
            {
                return ValueTask.FromResult(OperatorUiResult<CameraAgentPipelineOperatorState>.Failure(
                    OperatorUiResultKind.Unauthorized, "Authorization is required."));
            }
            var node = new CaptureProcessingPlanNode(
                "Preview", "Preview", true, true, 10, null, RawDependency,
                "encoded-preview", FrameArtifactRole.Preview, "display");
            var plan = new CaptureProcessingPlanPreview(
                CapturePipelineSchemaVersions.LegacyV1,
                CapturePipelineDependencyPolicy.LegacyInference,
                new string('D', 64),
                new string('E', 64),
                [node],
                [node]);
            return ValueTask.FromResult(OperatorUiResult<CameraAgentPipelineOperatorState>.Success(new(
                new CameraAgentPipelineRevisionPlan(
                    state.ActiveRevision.RevisionId,
                    state.ActiveRevision.RevisionNumber,
                    state.ActiveRevision.ProfileSha256,
                    false,
                    plan),
                null)));
        }

        public ValueTask<OperatorUiResult<CaptureSchedulePreview>> PreviewAsync(
            string profileJson, string basisRevisionId, int dayCount, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> StageAsync(
            string profileJson, string basisRevisionId, long expectedVersion, string idempotencyKey, string? reason,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> ActivateAsync(
            string revisionId, long expectedVersion, string idempotencyKey, string? reason,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> AddOverrideAsync(
            CaptureScheduleOverride scheduleOverride, long expectedVersion, string idempotencyKey,
            string? reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> ClearOverrideAsync(
            string overrideId, long expectedVersion, string idempotencyKey, string? reason,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class RetryingScheduleUiService(CaptureScheduleOperatorState state) : ICameraAgentScheduleUiService
    {
        internal List<(string Payload, string Key, long ExpectedVersion)> StageCommands { get; } = [];

        public ValueTask<OperatorUiResult<CaptureScheduleOperatorState>> GetAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(OperatorUiResult<CaptureScheduleOperatorState>.Success(state));

        public ValueTask<OperatorUiResult<CaptureSchedulePreview>> PreviewAsync(
            string profileJson, string basisRevisionId, int dayCount, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> StageAsync(
            string profileJson, string basisRevisionId, long expectedVersion, string idempotencyKey, string? reason,
            CancellationToken cancellationToken)
        {
            StageCommands.Add((profileJson, idempotencyKey, expectedVersion));
            return ValueTask.FromResult(OperatorUiResult<CaptureScheduleStoreSnapshot>.Failure(
                OperatorUiResultKind.Unavailable, "Response was unavailable."));
        }

        public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> ActivateAsync(
            string revisionId, long expectedVersion, string idempotencyKey, string? reason,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> AddOverrideAsync(
            CaptureScheduleOverride scheduleOverride, long expectedVersion, string idempotencyKey,
            string? reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> ClearOverrideAsync(
            string overrideId, long expectedVersion, string idempotencyKey, string? reason,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
